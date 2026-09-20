using Cirq.Components.Electromechanical;
using Cirq.Components.Ics;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Audio;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// Sound in and sound out: a microphone that plays a file, and a speaker that writes one.
/// <para>
/// A file rather than the sound card, and deliberately. The transient solver takes the steps the
/// circuit needs rather than the steps a clock wants, so it does not run at any fixed speed —
/// there is nothing to hand a device that needs forty-four thousand samples every second whatever
/// happens. Through files the same run is reproducible, which is the other half of it: a test can
/// assert what came out.
/// </para>
/// </summary>
public class AudioFileTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "cirq-audio-" + Guid.NewGuid().ToString("N"));

    public AudioFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string At(string name) => Path.Combine(_directory, name);

    private static double[] Tone(int samples, int rate, double hertz, double amplitude = 0.5) =>
        [.. Enumerable.Range(0, samples)
            .Select(i => amplitude * Math.Sin(2.0 * Math.PI * hertz * i / rate))];

    /// <summary>What goes in comes back, to within the quantisation of sixteen bits.</summary>
    [Fact]
    public void AWavFileSurvivesBeingWrittenAndReadBack()
    {
        var path = At("round-trip.wav");
        var written = Tone(4410, 44_100, 1e3);

        WaveFile.Write(path, written, 44_100);
        var (read, rate) = WaveFile.Read(path);

        Assert.Equal(44_100, rate);
        Assert.Equal(written.Length, read.Length);

        // One part in 32768 is the step of a 16-bit sample; anything larger is a real error.
        for (var i = 0; i < written.Length; i++) Assert.Equal(written[i], read[i], 1.0 / 32768.0);
    }

    /// <summary>And a sample beyond the range is clipped, not wrapped round into noise.</summary>
    [Fact]
    public void SamplesBeyondFullScaleAreClippedRatherThanWrapped()
    {
        var path = At("loud.wav");

        WaveFile.Write(path, [0.0, 2.0, -2.0, 0.5], 8000);
        var (read, _) = WaveFile.Read(path);

        Assert.Equal(1.0, read[1], 1e-3);
        Assert.Equal(-1.0, read[2], 1e-3);
        Assert.Equal(0.5, read[3], 1e-3);
    }

    /// <summary>
    /// The reader takes what people actually have: eight-bit files are unsigned with silence at
    /// 128, and anything with two channels is mixed down, because a circuit has one node.
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void TheReaderTakesEveryCommonSampleSize(int bits)
    {
        var path = At($"pcm{bits}.wav");
        WriteRaw(path, [0.0, 0.5, -0.5, 0.25], 22_050, bits, channels: 1);

        var (read, rate) = WaveFile.Read(path);

        Assert.Equal(22_050, rate);

        // Eight-bit has 256 steps across the range, so it is coarse and is meant to be.
        var tolerance = bits == 8 ? 0.01 : 1e-3;

        Assert.Equal(0.0, read[0], tolerance);
        Assert.Equal(0.5, read[1], tolerance);
        Assert.Equal(-0.5, read[2], tolerance);
        Assert.Equal(0.25, read[3], tolerance);
    }

    [Fact]
    public void AndMixesAStereoFileDownToOneSignal()
    {
        var path = At("stereo.wav");

        // Left and right pulling opposite ways, so a reader that took one channel would be obvious.
        WriteRaw(path, [1.0, -1.0, 0.5, 0.5], 44_100, bits: 16, channels: 2);

        var (read, _) = WaveFile.Read(path);

        Assert.Equal(2, read.Length);
        Assert.Equal(0.0, read[0], 1e-3);
        Assert.Equal(0.5, read[1], 1e-3);
    }

    /// <summary>
    /// The speaker's recording is on a fixed grid, whatever the solver did: a fixed number of
    /// samples per second of simulated time, and at the frequency that was fed in.
    /// </summary>
    [Fact]
    public void TheSpeakerRecordsOnAFixedGridWhateverTheSolverDid()
    {
        var path = At("speaker.wav");

        var circuit = new Circuit();
        var generator = circuit.Add(new FunctionGenerator(Waveform.Sine, 1e3, 2.0));
        var speaker = circuit.Add(new Speaker(8.0));
        var gnd = circuit.Add(new Ground());

        speaker.RecordingPath = path;
        speaker.RecordingSampleRate = 44_100;
        speaker.RecordingFullScaleVolts = 1.0;

        circuit.Connect(generator.Output, speaker.A);
        circuit.Connect(speaker.B, gnd.Pin);
        circuit.Connect(generator.Return, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(100e-3);

        speaker.Flush();

        var (samples, rate) = WaveFile.Read(path);

        Assert.Equal(44_100, rate);

        // A tenth of a second of simulated time is a tenth of a second of audio, give or take the
        // last partial sample.
        Assert.InRange(samples.Length, 4390, 4430);

        // One volt peak against one volt of full scale, so the file should be at about full range.
        Assert.Equal(1.0, samples.Max(), 0.1);
        Assert.Equal(-1.0, samples.Min(), 0.1);

        // And the right pitch: a 1 kHz sine at 44.1 kHz crosses zero upwards every 44.1 samples.
        Assert.InRange(RisingZeroCrossings(samples), 98, 102);
    }

    /// <summary>
    /// The microphone plays the file rather than its tone, and what reaches the circuit is the
    /// recording: same frequency, and the amplitude the file had.
    /// </summary>
    [Fact]
    public void TheMicrophonePlaysAClipInsteadOfItsTone()
    {
        var path = At("clip.wav");

        // Deliberately not the microphone's own 1 kHz default, so the two cannot be confused.
        WaveFile.Write(path, Tone(44_100, 44_100, 250.0, amplitude: 1.0), 44_100);

        var (sim, microphone, _) = CapsuleRig();
        microphone.SourcePath = path;

        Assert.True(microphone.IsPlayingClip);
        Assert.Null(microphone.SourceError);
        Assert.Equal(1.0, microphone.ClipSeconds, 1e-3);

        List<double> seen = [];
        while (sim.Time < 40e-3)
        {
            sim.Step();
            seen.Add(sim.NodeVoltage(microphone.A));
        }

        // Ten cycles of 250 Hz in forty milliseconds, not forty of the default tone.
        var centred = Centre(seen);
        Assert.InRange(RisingZeroCrossings(centred), 9, 11);
    }

    /// <summary>And an unreadable file is reported rather than thrown mid-solve.</summary>
    [Fact]
    public void ABadClipIsReportedRatherThanThrown()
    {
        var path = At("not-audio.wav");
        File.WriteAllText(path, "this is not a wave file at all");

        var (sim, microphone, _) = CapsuleRig();
        microphone.SourcePath = path;

        Assert.False(microphone.IsPlayingClip);
        Assert.NotNull(microphone.SourceError);
        Assert.Contains(microphone.Violations, v => v.Contains("cannot read"));

        // And the circuit still runs, on the tone it had before.
        sim.Run(5e-3);
        Assert.True(microphone.IsBiased);
    }

    /// <summary>
    /// The whole point of the pair: a recording through the amplifier and out the other side, with
    /// the gain of the stage between them and nothing else.
    /// </summary>
    [Fact]
    public void AClipGoesInOneEndAndComesOutTheOther()
    {
        var inPath = At("programme.wav");
        var outPath = At("through-the-amp.wav");

        WaveFile.Write(inPath, Tone(22_050, 44_100, 400.0, amplitude: 1.0), 44_100);

        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(9.0));
        var microphone = circuit.Add(new Microphone { SourcePath = inPath });
        var bias = circuit.Add(new Resistor(2.2e3));
        var coupling = circuit.Add(new Capacitor(1e-6));
        var volume = circuit.Add(new Potentiometer(10e3, 0.08));
        var amplifier = circuit.Add(new Lm386 { VoltageGain = 20.0 });
        var output = circuit.Add(new Capacitor(220e-6));
        var speaker = circuit.Add(new Speaker(8.0));
        var gnd = circuit.Add(new Ground());

        speaker.RecordingPath = outPath;
        speaker.RecordingFullScaleVolts = 1.0;

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(bias.A, supply.Positive);
        circuit.Connect(bias.B, microphone.A);
        circuit.Connect(microphone.B, gnd.Pin);
        circuit.Connect(microphone.A, coupling.A);
        circuit.Connect(coupling.B, volume.B);
        circuit.Connect(volume.A, gnd.Pin);
        circuit.Connect(amplifier.InPlus, volume.Wiper);
        circuit.Connect(amplifier.InMinus, gnd.Pin);
        circuit.Connect(amplifier.PositiveSupply, supply.Positive);
        circuit.Connect(amplifier.NegativeSupply, gnd.Pin);
        circuit.Connect(amplifier.Output, output.A);
        circuit.Connect(output.B, speaker.A);
        circuit.Connect(speaker.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        // Past the coupling capacitors, then record a clean stretch.
        sim.Run(300e-3);
        speaker.Flush();

        var settled = speaker.RecordedSeconds;
        sim.Run(50e-3);
        speaker.Flush();

        var (samples, _) = WaveFile.Read(outPath);
        var tail = samples.Skip((int)(settled * 44_100)).ToArray();

        Assert.NotEmpty(tail);

        // The 400 Hz that went in is the 400 Hz that came out: twenty cycles in fifty milliseconds.
        Assert.InRange(RisingZeroCrossings(Centre(tail)), 18, 22);
        Assert.True(tail.Max() > 0.05, $"the recording only reached {tail.Max():0.000} of full scale");
    }

    /// <summary>
    /// A moment of clipping is a real event and is left alone; a level set too low clips a steady
    /// fraction of everything, and that is the one the speaker complains about.
    /// </summary>
    [Fact]
    public void ItComplainsAboutASteadyClipAndNotAboutAMomentaryOne()
    {
        var circuit = new Circuit();
        var generator = circuit.Add(new FunctionGenerator(Waveform.Sine, 1e3, 2.0));
        var speaker = circuit.Add(new Speaker(8.0));
        var gnd = circuit.Add(new Ground());

        speaker.RecordingPath = At("level.wav");
        speaker.RecordingFullScaleVolts = 0.1;      // ten times too low for a 1 V peak

        circuit.Connect(generator.Output, speaker.A);
        circuit.Connect(speaker.B, gnd.Pin);
        circuit.Connect(generator.Return, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(50e-3);

        Assert.True(speaker.ClippedFraction > 0.5, $"only {speaker.ClippedFraction:P0} clipped");
        Assert.Contains(speaker.Violations, v => v.Contains("clipping"));

        // And with a level that fits, it says nothing.
        speaker.RecordingFullScaleVolts = 2.0;
        speaker.ResetState();

        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(50e-3);

        Assert.Equal(0.0, speaker.ClippedFraction);
        Assert.DoesNotContain(speaker.Violations, v => v.Contains("clipping"));
    }

    /// <summary>
    /// And the recording hears the gain: the same clip through ten times the gain is ten times the
    /// amplitude in the file, which is the thing a scope trace is worst at conveying.
    /// </summary>
    [Fact]
    public void TheRecordingCarriesTheGainOfWhateverItWentThrough()
    {
        var quiet = PeakThroughAmplifier(gain: 20.0);
        var loud = PeakThroughAmplifier(gain: 200.0);

        Assert.Equal(10.0, loud / quiet, 1.0);
    }

    private double PeakThroughAmplifier(double gain)
    {
        var path = At($"gain-{gain}.wav");

        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(9.0));
        var generator = circuit.Add(new FunctionGenerator(Waveform.Sine, 1e3, 0.02));
        var amplifier = circuit.Add(new Lm386 { VoltageGain = gain });
        var output = circuit.Add(new Capacitor(220e-6));
        var speaker = circuit.Add(new Speaker(8.0));
        var gnd = circuit.Add(new Ground());

        speaker.RecordingPath = path;
        speaker.RecordingFullScaleVolts = 5.0;

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(generator.Return, gnd.Pin);
        circuit.Connect(amplifier.InPlus, generator.Output);
        circuit.Connect(amplifier.InMinus, gnd.Pin);
        circuit.Connect(amplifier.PositiveSupply, supply.Positive);
        circuit.Connect(amplifier.NegativeSupply, gnd.Pin);
        circuit.Connect(amplifier.Output, output.A);
        circuit.Connect(output.B, speaker.A);
        circuit.Connect(speaker.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(400e-3);

        speaker.Flush();

        var (samples, _) = WaveFile.Read(path);
        var tail = samples.Skip(samples.Length / 2).ToArray();

        Assert.False(amplifier.IsClipping);
        return tail.Max() - tail.Min();
    }

    private static (CircuitSimulator Sim, Microphone Capsule, Resistor Load) CapsuleRig()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var microphone = circuit.Add(new Microphone());
        var bias = circuit.Add(new Resistor(2.2e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(bias.A, supply.Positive);
        circuit.Connect(bias.B, microphone.A);
        circuit.Connect(microphone.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        return (sim, microphone, bias);
    }

    /// <summary>The signal with its average taken out, so zero crossings mean what they say.</summary>
    private static double[] Centre(IReadOnlyList<double> values)
    {
        var mean = values.Average();
        return [.. values.Select(v => v - mean)];
    }

    private static int RisingZeroCrossings(IReadOnlyList<double> samples)
    {
        var crossings = 0;

        for (var i = 1; i < samples.Count; i++)
            if (samples[i - 1] <= 0 && samples[i] > 0) crossings++;

        return crossings;
    }

    /// <summary>
    /// A WAV written by hand at a given depth and channel count, standing in for the files other
    /// programs produce. The library only writes one format, so the reader needs its own fixtures.
    /// </summary>
    private static void WriteRaw(string path, double[] samples, int rate, int bits, int channels)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);

        var bytesPerSample = bits / 8;
        var dataBytes = samples.Length * bytesPerSample;

        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(rate);
        writer.Write(rate * bytesPerSample * channels);
        writer.Write((short)(bytesPerSample * channels));
        writer.Write((short)bits);
        writer.Write("data"u8);
        writer.Write(dataBytes);

        foreach (var sample in samples)
        {
            switch (bits)
            {
                case 8: writer.Write((byte)Math.Round((sample * 128.0) + 128.0)); break;
                case 16: writer.Write((short)Math.Round(sample * 32767.0)); break;
                case 24:
                    var packed = (int)Math.Round(sample * 8388607.0);
                    writer.Write((byte)(packed & 0xFF));
                    writer.Write((byte)((packed >> 8) & 0xFF));
                    writer.Write((byte)((packed >> 16) & 0xFF));
                    break;
                case 32: writer.Write((int)Math.Round(sample * 2147483647.0)); break;
            }
        }
    }
}
