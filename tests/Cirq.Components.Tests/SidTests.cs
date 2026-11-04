using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Processors;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The SID's digital half on its own, cycle by cycle: the oscillators and every waveform, sync, ring
/// modulation and the test bit, the envelopes against the datasheet's times — the exponential decay,
/// the sustain, the hold at zero and the delay bug — the readback registers, and the mix.
/// </summary>
public class SidCoreTests
{
    private static SidCore Core(SidModel model = SidModel.Mos6581) => new(model);

    private static void Run(SidCore sid, int cycles)
    {
        for (var i = 0; i < cycles; i++) sid.Clock();
    }

    [Fact]
    public void TheOscillatorAddsItsFrequencyEveryCycle()
    {
        var sid = Core();
        sid.Write(0x00, 0x00);
        sid.Write(0x01, 0x10);           // $1000: the 24-bit accumulator wraps every 4096 cycles
        Run(sid, 100);
        Assert.Equal(100u * 0x1000, sid.Voices[0].Accumulator);
        Run(sid, 4096);
        Assert.Equal(100u * 0x1000, sid.Voices[0].Accumulator);
    }

    [Fact]
    public void TheWaveformsAreReadFromTheAccumulator()
    {
        var sid = Core();
        var v = sid.Voices[0];
        var source = sid.SourceOf(0);

        v.Control = SidCore.Sawtooth;
        v.Accumulator = 0x123456;
        Assert.Equal(0x123, v.Waveform(source));

        v.Control = SidCore.Triangle;
        v.Accumulator = 0x400000;
        Assert.Equal(0x800, v.Waveform(source));     // half way up
        v.Accumulator = 0xC00000;
        Assert.Equal(0x7FF, v.Waveform(source));     // half way back down

        v.Control = SidCore.Pulse;
        v.PulseWidth = 0x800;
        v.Accumulator = 0x7FF000;
        Assert.Equal(0, v.Waveform(source));
        v.Accumulator = 0x800000;
        Assert.Equal(0xFFF, v.Waveform(source));

        // Two at once is their AND.
        v.Control = SidCore.Sawtooth | SidCore.Pulse;
        Assert.Equal(0x800, v.Waveform(source));

        v.Control = 0;
        Assert.Equal(-1, v.Waveform(source));
        Assert.Equal(0.0, v.Output(source));
    }

    /// <summary>A pulse width of $400 is high a quarter of the time... the other way round: low for a quarter, high for three.</summary>
    [Fact]
    public void ThePulseWidthSetsTheDutyCycle()
    {
        var sid = Core();
        sid.Write(0x01, 0x10);
        sid.Write(0x02, 0x00);
        sid.Write(0x03, 0x04);
        sid.Write(0x04, SidCore.Pulse);
        var high = 0;
        for (var i = 0; i < 4096; i++)
        {
            sid.Clock();
            if (sid.Voices[0].Waveform(sid.SourceOf(0)) == 0xFFF) high++;
        }

        Assert.Equal(3072, high);
    }

    /// <summary>The noise register shifts once each time bit 19 rises, feeding back taps 22 and 17, and gives eight bits of output.</summary>
    [Fact]
    public void NoiseIsTheShiftRegisterClockedByBit19()
    {
        var sid = Core();
        sid.Write(0x01, 0x10);           // bit 19 rises after 128 cycles, then every 256
        sid.Write(0x04, SidCore.Noise);
        var start = sid.Voices[0].ShiftRegister;
        Run(sid, 127);
        Assert.Equal(start, sid.Voices[0].ShiftRegister);

        uint expected = start;
        HashSet<int> seen = [];
        for (var shift = 0; shift < 300; shift++)
        {
            Run(sid, shift == 0 ? 1 : 256);
            var bit = ((expected >> 22) ^ (expected >> 17)) & 1;
            expected = ((expected << 1) | bit) & 0x7FFFFF;
            Assert.Equal(expected, sid.Voices[0].ShiftRegister);
            var output = sid.Voices[0].Waveform(sid.SourceOf(0));
            Assert.Equal(0, output & 0x00F);
            seen.Add(output >> 4);
        }

        Assert.True(seen.Count > 150, $"only {seen.Count} different noise values");

        // The test bit fills it with ones and holds the oscillator.
        sid.Write(0x04, SidCore.Noise | SidCore.Test);
        Run(sid, 1000);
        Assert.Equal(0x7FFFFFu, sid.Voices[0].ShiftRegister);
        Assert.Equal(0u, sid.Voices[0].Accumulator);
    }

    [Fact]
    public void TheTestBitHoldsTheOscillatorAndThePulseHigh()
    {
        var sid = Core();
        sid.Write(0x01, 0x40);
        Run(sid, 50);
        sid.Write(0x04, SidCore.Pulse | SidCore.Test);
        Run(sid, 50);
        Assert.Equal(0u, sid.Voices[0].Accumulator);
        Assert.Equal(0xFFF, sid.Voices[0].Waveform(sid.SourceOf(0)));
    }

    /// <summary>Hard sync: voice 2's accumulator goes back to zero whenever voice 1's bit 23 rises.</summary>
    [Fact]
    public void SyncResetsTheVoiceWhenThePreviousOnesTopBitRises()
    {
        var sid = Core();
        sid.Write(0x01, 0x10);                           // voice 1: period 4096 cycles
        sid.Write(0x08, 0x31);                           // voice 2: a faster, unrelated rate
        sid.Write(0x0B, SidCore.Sawtooth | SidCore.Sync);

        for (var i = 0; i < (3 * 4096) + 7; i++)
        {
            sid.Clock();
            if (sid.Voices[0].MsbRose) Assert.Equal(0u, sid.Voices[1].Accumulator);
        }

        // Voice 1 is synced to voice 3, which is not running: nothing happens to it.
        Assert.NotEqual(0u, sid.Voices[0].Accumulator);
    }

    /// <summary>Ring modulation folds voice 1's triangle the other way while voice 3's top bit is set.</summary>
    [Fact]
    public void RingModulationExclusiveOrsTheTriangleWithThePreviousVoice()
    {
        var sid = Core();
        var v1 = sid.Voices[0];
        v1.Control = SidCore.Triangle | SidCore.Ring;
        v1.Accumulator = 0x400000;
        sid.Voices[2].Accumulator = 0x000000;
        Assert.Equal(0x800, v1.Waveform(sid.SourceOf(0)));
        sid.Voices[2].Accumulator = 0x800000;
        Assert.Equal(0x7FF, v1.Waveform(sid.SourceOf(0)));

        // Without the triangle selected, ring modulation does nothing.
        v1.Control = SidCore.Sawtooth | SidCore.Ring;
        Assert.Equal(0x400, v1.Waveform(sid.SourceOf(0)));
    }

    /// <summary>
    /// The fastest attack: one step every 9 cycles, so 255 steps is 2,295 cycles — the datasheet's
    /// 2 ms at 1 MHz, near enough. Then the decay to the sustain level, stepping slower as it falls.
    /// </summary>
    [Fact]
    public void TheEnvelopeAttacksLinearlyAndDecaysToTheSustainLevel()
    {
        var sid = Core();
        sid.Write(0x05, 0x00);           // attack 2 ms, decay 6 ms
        sid.Write(0x06, 0x80);           // sustain 8: $88
        sid.Write(0x04, SidCore.Gate);
        var v = sid.Voices[0];

        Run(sid, 9 * 254);
        Assert.Equal(254, v.Envelope);
        Assert.Equal(EnvelopePhase.Attack, v.Phase);
        Run(sid, 9);
        Assert.Equal(255, v.Envelope);
        Assert.Equal(EnvelopePhase.DecaySustain, v.Phase);

        Run(sid, 20000);
        Assert.Equal(0x88, v.Envelope);
        Assert.Equal(0x88, sid.Read(0x1C) == 0 ? 0x88 : v.Envelope);
    }

    /// <summary>
    /// A decay all the way down takes the sum of the exponential steps — 162 at one period, 39 at two,
    /// 28 at four, 12 at eight, 8 at sixteen and 6 at thirty: 756 periods, three attacks' worth.
    /// </summary>
    [Fact]
    public void TheDecayIsExponentialAndThreeTimesTheAttack()
    {
        var sid = Core();
        var v = sid.Voices[0];
        v.Envelope = 0xFF;
        v.Phase = EnvelopePhase.DecaySustain;
        v.HoldZero = false;
        sid.Write(0x05, 0x00);
        sid.Write(0x06, 0x00);           // sustain 0

        var cycles = 0;
        while (v.Envelope > 0 && cycles < 100000)
        {
            sid.Clock();
            cycles++;
        }

        Assert.Equal(756 * 9, cycles, 9.0);
        Assert.True(v.HoldZero);
        Run(sid, 50000);
        Assert.Equal(0, v.Envelope);

        // The datasheet's decay for rate 0 is 6 ms: three times the 2 ms attack.
        Assert.Equal(3 * SidCore.AttackTimes[0], cycles / 1e6, 1e-3);
    }

    [Fact]
    public void ReleaseFallsFromWhereverTheGateLeftIt()
    {
        var sid = Core();
        sid.Write(0x05, 0x00);
        sid.Write(0x06, 0xF0);
        sid.Write(0x04, SidCore.Gate);
        Run(sid, 4000);
        Assert.Equal(0xFF, sid.Voices[0].Envelope);       // sustain 15 holds at the top

        sid.Write(0x04, 0);
        Assert.Equal(EnvelopePhase.Release, sid.Voices[0].Phase);
        Run(sid, 9 * 100);
        Assert.InRange(sid.Voices[0].Envelope, 150, 160);
        Run(sid, 10000);
        Assert.Equal(0, sid.Voices[0].Envelope);
    }

    /// <summary>
    /// The delay bug: the rate counter only resets when it equals the period, so dropping from a slow
    /// attack to a fast one after the counter has passed the new period stalls the envelope until it
    /// has counted all the way round 32,768.
    /// </summary>
    [Fact]
    public void AShorterRateWaitsForTheCounterToWrap()
    {
        var sid = Core();
        sid.Write(0x05, 0xF0);           // attack 8 s: 31,251 cycles a step
        sid.Write(0x04, SidCore.Gate);
        Run(sid, 20000);
        Assert.Equal(0, sid.Voices[0].Envelope);
        Assert.Equal(20000, sid.Voices[0].RateCounter);

        sid.Write(0x05, 0x00);           // now 2 ms — but the counter is past 9
        Run(sid, 32768 - 20000);
        Assert.Equal(0, sid.Voices[0].Envelope);
        Run(sid, 9);
        Assert.Equal(1, sid.Voices[0].Envelope);
        Run(sid, 9 * 10);
        Assert.Equal(11, sid.Voices[0].Envelope);
    }

    [Fact]
    public void TheReadbackRegistersAndTheFloatingBus()
    {
        var sid = Core();
        sid.Write(0x0F, 0x20);
        sid.Write(0x12, SidCore.Sawtooth | SidCore.Gate);
        sid.Write(0x13, 0x00);
        sid.PotX = 0x40;
        sid.PotY = 0x80;
        Run(sid, 3000);

        Assert.Equal(sid.Voices[2].Accumulator >> 16, sid.Read(0x1B));
        Assert.Equal(sid.Voices[2].Envelope, sid.Read(0x1C));
        Assert.True(sid.Read(0x1C) > 0);
        Assert.Equal(0x40, sid.Read(0x19));
        Assert.Equal(0x80, sid.Read(0x1A));

        // A write-only register reads as the last byte written.
        sid.Write(0x00, 0x5A);
        Assert.Equal(0x5A, sid.Read(0x0E));
        Assert.Equal(0x5A, sid.Read(0x18));
        Assert.Equal(0x20, sid.Register(0x0F));
    }

    /// <summary>The routing bits send voices through the filter; voice 3 off takes it out of the direct mix only.</summary>
    [Fact]
    public void TheMixSplitsAtTheFilterAndVoiceThreeCanBeTurnedOff()
    {
        var sid = Core();
        foreach (var v in sid.Voices)
        {
            v.Control = SidCore.Pulse | SidCore.Test;     // a steady full-scale level
            v.Envelope = 0xFF;
        }

        sid.Clock();
        Assert.Equal(3 * 2047 / 2048.0, sid.Direct, 6);
        Assert.Equal(0.0, sid.FilterInput);

        sid.Write(0x17, 0x01);
        sid.Write(0x18, 0x80 | 0x0F);
        sid.Clock();
        Assert.Equal(2047 / 2048.0, sid.FilterInput, 6);
        Assert.Equal(2047 / 2048.0, sid.Direct, 6);       // voice 2 alone: 1 filtered, 3 off

        sid.Write(0x17, 0x05);
        sid.Clock();
        Assert.Equal(2 * 2047 / 2048.0, sid.FilterInput, 6);   // routed through the filter, 3OFF doesn't apply

        sid.ExternalInput = 0.25;
        sid.Write(0x17, 0x0D);
        sid.Clock();
        Assert.Equal((2 * 2047 / 2048.0) + 0.25, sid.FilterInput, 6);
        Assert.Equal(15, sid.Volume);
    }

    [Fact]
    public void TheCutoffCurves()
    {
        Assert.Equal(30, SidCore.CutoffFrequency(SidModel.Mos8580, 0), 6);
        Assert.Equal(12000, SidCore.CutoffFrequency(SidModel.Mos8580, 2047), 6);
        Assert.Equal(6012, SidCore.CutoffFrequency(SidModel.Mos8580, 1023), 0);

        // The 6581's is low and flat at the bottom and climbs steeply.
        Assert.Equal(220, SidCore.CutoffFrequency(SidModel.Mos6581, 0), 6);
        Assert.Equal(12000, SidCore.CutoffFrequency(SidModel.Mos6581, 2047), 6);
        Assert.InRange(SidCore.CutoffFrequency(SidModel.Mos6581, 1024), 1800, 2300);
        Assert.True(SidCore.CutoffFrequency(SidModel.Mos6581, 256) < 600);

        Assert.Equal(0.707, SidCore.QualityFactor(SidModel.Mos6581, 0), 3);
        Assert.True(SidCore.QualityFactor(SidModel.Mos8580, 15) > SidCore.QualityFactor(SidModel.Mos6581, 15));
        Assert.Equal(SidCore.AttackTimes[15], SidCore.RatePeriods[15] * 256 / 1e6, 1e-3 * 8);
    }
}

/// <summary>
/// The SID as a part: its pinout, its bus, AUDIO OUT driving the circuit, the filter on the real
/// capacitors at its CAP pins — moving when they change — the volume and the 6581's offset, and the
/// paddles timed from the R and C on the POT pins.
/// </summary>
public class SidChipTests
{
    /// <summary>A SID on its own, clocked at 1 MHz, deselected, with its filter capacitors fitted.</summary>
    private sealed class Bench
    {
        public Circuit Circuit { get; } = new();

        public SidChip Sid { get; }

        public Capacitor C1 { get; }

        public Capacitor C2 { get; }

        public Ground Ground { get; }

        public DcVoltageSource Vcc { get; }

        /// <summary>/RES, high until a test takes it low.</summary>
        public LogicToggle ResetLine { get; }

        public Bench(SidChip sid, double? capacitance = null)
        {
            Sid = Circuit.Add(sid);
            Ground = Circuit.Add(new Ground());
            Vcc = Circuit.Add(new DcVoltageSource(5.0));
            var vdd = Circuit.Add(new DcVoltageSource(sid.Model == SidModel.Mos8580 ? 9.0 : 12.0));
            var clock = Circuit.Add(new ClockSource(1e6));
            C1 = Circuit.Add(new Capacitor(capacitance ?? sid.NominalCapacitance));
            C2 = Circuit.Add(new Capacitor(capacitance ?? sid.NominalCapacitance));

            Circuit.Connect(Vcc.Negative, Ground.Pin);
            Circuit.Connect(vdd.Negative, Ground.Pin);
            Circuit.Connect(sid.Vcc, Vcc.Positive);
            Circuit.Connect(sid.Vdd, vdd.Positive);
            Circuit.Connect(sid.Gnd, Ground.Pin);
            Circuit.Connect(clock.Out, sid.Clock);
            foreach (var pin in new[] { sid.ChipSelect, sid.ReadWrite }) Circuit.Connect(pin, Vcc.Positive);
            ResetLine = Circuit.Add(new LogicToggle { State = true });
            Circuit.Connect(ResetLine.Out, sid.Reset);
            foreach (var pin in sid.Address) Circuit.Connect(pin, Ground.Pin);
            Circuit.Connect(C1.A, sid.Cap1A);
            Circuit.Connect(C1.B, sid.Cap1B);
            Circuit.Connect(C2.A, sid.Cap2A);
            Circuit.Connect(C2.B, sid.Cap2B);
        }

        private CircuitSimulator? _simulator;

        public CircuitSimulator Simulator
        {
            get
            {
                if (_simulator is not null) return _simulator;
                _simulator = new CircuitSimulator(Circuit, new() { TimeStep = 1e-6, MaxTimeStep = 1e-5, UseInitialConditions = true });
                _simulator.Reset();
                _simulator.SolveOperatingPoint();
                return _simulator;
            }
        }

        /// <summary>Registers written straight into the chip, as a program would, once the run has started.</summary>
        public void Write(params (int Register, int Value)[] writes)
        {
            _ = Simulator;
            foreach (var (register, value) in writes) Sid.Core.Write(register, (byte)value);
        }

        /// <summary>The lowest, highest and mean voltage at a pin over a stretch of the run.</summary>
        public (double Low, double High, double Mean) Measure(Terminal pin, double settle, double span)
        {
            var sim = Simulator;
            sim.Run(settle);
            var end = sim.Time + span;
            double low = double.MaxValue, high = double.MinValue, sum = 0;
            var n = 0;
            while (sim.Time < end)
            {
                sim.Step();
                var v = sim.NodeVoltage(pin);
                low = Math.Min(low, v);
                high = Math.Max(high, v);
                sum += v;
                n++;
            }

            return (low, high, sum / n);
        }
    }

    [Fact]
    public void ThePinoutIsTheDatasheets()
    {
        var sid = new Sid6581();
        string Name(int pin) => sid.Terminals.Single(t => t.Id == $"p{pin}").Name;

        Assert.Equal(28, sid.Terminals.Count);
        Assert.Equal(["CAP1A", "CAP1B", "CAP2A", "CAP2B", "RES", "PHI2", "RW", "CS"], Enumerable.Range(1, 8).Select(Name));
        Assert.Equal(["A0", "A1", "A2", "A3", "A4", "GND"], Enumerable.Range(9, 6).Select(Name));
        Assert.Equal("D0", Name(15));
        Assert.Equal("D7", Name(22));
        Assert.Equal(["POTY", "POTX", "VCC", "EXTIN", "AUDIO", "VDD"], Enumerable.Range(23, 6).Select(Name));
        Assert.Equal("AUDIO OUT", sid.PinLabel(sid.AudioOut));
        Assert.Equal("/CS", sid.PinLabel(sid.ChipSelect));

        Assert.Equal("8580", new Sid8580().PartNumber);
        Assert.Equal(22e-9, new Sid8580().NominalCapacitance);
        Assert.Equal(470e-12, sid.NominalCapacitance);
    }

    /// <summary>
    /// A sawtooth at 1 kHz, no filter, full volume: AUDIO OUT sits at half VDD and swings a volt peak
    /// to peak — one full-scale voice — at the voice's frequency.
    /// </summary>
    [Fact]
    public void AudioOutIsAVoltageAtHalfVddCarryingTheVoices()
    {
        var b = new Bench(new Sid8580());
        // 1 kHz is 1000 × 2^24 / 10^6 = 16777.
        b.Write((0x00, 16777 & 0xFF), (0x01, 16777 >> 8), (0x05, 0x00), (0x06, 0xF0), (0x18, 0x0F), (0x04, SidCore.Sawtooth | SidCore.Gate));

        var (low, high, mean) = b.Measure(b.Sid.AudioOut, settle: 5e-3, span: 4e-3);
        Assert.Equal(4.5, mean, 0.1);
        Assert.Equal(1.0, high - low, 0.05);
        Assert.Equal(1000, b.Sid.VoiceFrequency(0), 1.0);
        Assert.Equal(1e6, b.Sid.ClockFrequency, 1.0);

        // Volume 0: silence (and on the 8580 almost no offset either).
        b.Write((0x18, 0x00));
        var (qLow, qHigh, qMean) = b.Measure(b.Sid.AudioOut, settle: 1e-3, span: 2e-3);
        Assert.True(qHigh - qLow < 1e-3);
        Assert.Equal(4.5, qMean, 0.01);
    }

    /// <summary>The 6581's mixer offset moves with the volume: the volume register alone makes a step, which is how its samples are played.</summary>
    [Fact]
    public void The6581sVolumeMovesItsOutput()
    {
        var b = new Bench(new Sid6581());
        b.Write((0x18, 0x00));
        var quiet = b.Measure(b.Sid.AudioOut, 1e-3, 1e-3).Mean;
        b.Write((0x18, 0x0F));
        var loud = b.Measure(b.Sid.AudioOut, 1e-3, 1e-3).Mean;

        Assert.Equal(6.0, quiet, 0.05);
        Assert.Equal(b.Sid.OutputScale * b.Sid.MixerOffset, loud - quiet, 0.01);
    }

    /// <summary>The filter's response measured through EXT IN: its gain at a frequency, routed through a mode, relative to going round it.</summary>
    private static double Gain(SidChip chip, double frequency, int cutoff, int modeBits, double? capacitance = null, int resonance = 0)
    {
        var b = new Bench(chip, capacitance);
        var centre = chip.Model == SidModel.Mos8580 ? 4.5 : 6.0;
        var source = b.Circuit.Add(new FunctionGenerator(Waveform.Sine, frequency, 0.4) { DcOffset = centre });
        b.Circuit.Connect(source.Output, chip.ExternalIn);
        b.Circuit.Connect(source.Return, b.Ground.Pin);

        b.Write((0x15, cutoff & 7), (0x16, cutoff >> 3), (0x17, 0x08 | (resonance << 4)), (0x18, modeBits | 0x0F));
        var periods = Math.Max(3, frequency * 4e-3);
        var (low, high, _) = b.Measure(chip.AudioOut, settle: Math.Max(6e-3, 6 / frequency), span: periods / frequency);
        return (high - low) / 0.4;
    }

    /// <summary>
    /// The 8580's low-pass at its cutoff gives Q — 0.707 with no resonance — and passes a tenth of
    /// that frequency and stops ten times it, as a second-order filter should.
    /// </summary>
    [Fact]
    public void TheLowPassTurnsOverAtTheCutoff()
    {
        // 1 kHz on the 8580's line: (1000 − 30) / 11970 × 2047 = 166.
        Assert.Equal(1000, SidCore.CutoffFrequency(SidModel.Mos8580, 166), 1.0);

        Assert.Equal(0.707, Gain(new Sid8580(), 1000, 166, 0x10), 0.05);
        Assert.Equal(1.0, Gain(new Sid8580(), 100, 166, 0x10), 0.05);
        Assert.InRange(Gain(new Sid8580(), 8000, 166, 0x10), 0.0, 0.03);
    }

    /// <summary>The capacitors are real: double them and the same register gives half the cutoff — at the old cutoff, the gain of a second-order filter at twice its corner.</summary>
    [Fact]
    public void BiggerCapacitorsLowerTheCutoff()
    {
        var gain = Gain(new Sid8580(), 1000, 166, 0x10, capacitance: 44e-9);
        Assert.Equal(1 / Math.Sqrt(9 + (4 / 0.5)), gain, 0.03);
    }

    [Fact]
    public void TheHighPassAndBandPass()
    {
        Assert.InRange(Gain(new Sid8580(), 100, 166, 0x40), 0.0, 0.03);
        Assert.Equal(1.0, Gain(new Sid8580(), 8000, 166, 0x40), 0.06);
        Assert.Equal(0.707, Gain(new Sid8580(), 1000, 166, 0x20), 0.05);
        Assert.InRange(Gain(new Sid8580(), 100, 166, 0x20), 0.0, 0.12);
    }

    /// <summary>Resonance raises the peak at the cutoff to the filter's Q.</summary>
    [Fact]
    public void ResonancePeaksAtTheCutoff()
    {
        var q = SidCore.QualityFactor(SidModel.Mos8580, 15);
        Assert.Equal(q, Gain(new Sid8580(), 1000, 166, 0x10, resonance: 15), q * 0.08);
    }

    /// <summary>The 6581 with the C64's 470 pF: its curve's cutoff, at the register's setting.</summary>
    [Fact]
    public void The6581FiltersOnItsOwnCurve()
    {
        var cutoff = 1024;
        var frequency = SidCore.CutoffFrequency(SidModel.Mos6581, cutoff);
        Assert.Equal(0.707, Gain(new Sid6581(), frequency, cutoff, 0x10), 0.06);
    }

    /// <summary>
    /// The paddles, timed from the R and C on the pin: 235 kΩ and 1 nF charge to 0.42 of VCC in
    /// 128 µs, so the count is 128; with nothing on the pin it never charges, and reads 255.
    /// </summary>
    [Fact]
    public void ThePaddlesCountTheChargeTimeOfTheirRAndC()
    {
        var b = new Bench(new Sid6581());
        var pot = b.Circuit.Add(new Resistor(235e3));
        var cap = b.Circuit.Add(new Capacitor(1e-9));
        b.Circuit.Connect(pot.A, b.Vcc.Positive);
        b.Circuit.Connect(pot.B, b.Sid.PotX);
        b.Circuit.Connect(cap.A, b.Sid.PotX);
        b.Circuit.Connect(cap.B, b.Ground.Pin);

        b.Simulator.Run(1.6e-3);
        var expected = 235e3 * 1e-9 * Math.Log(1 / (1 - SidChip.PotThreshold)) * 1e6;
        Assert.Equal(expected, b.Sid.Core.PotX, 3.0);
        Assert.Equal(255, b.Sid.Core.PotY);
    }

    /// <summary>On the bus: a program writes the voice registers, and reads ENV3 and OSC3 back.</summary>
    [Fact]
    public void AProgramPlaysItThroughTheBus()
    {
        var program = Assembler6502.Assemble("""
                    .org $8000
            reset:  ldx #$FF
                    txs
                    lda #$00
                    sta $4013       ; voice 3: attack 2 ms
                    lda #$F0
                    sta $4014       ; sustain 15
                    lda #$40
                    sta $400F       ; frequency $4000
                    lda #$21
                    sta $4012       ; sawtooth, gate
                    lda #$0F
                    sta $4018
                    ldy #3
                    ldx #0
            wait:   dex
                    bne wait
                    dey
                    bne wait
                    lda $401C       ; ENV3
                    sta $0200
                    lda $401B       ; OSC3
                    sta $0201
            done:   jmp done
                    .org $FFFC
                    .word reset
                    .word reset
            """).Rom32K();
        var computer = new Cpu6502CircuitTests.Computer(rom: program);
        var circuit = computer.Circuit;
        var sid = circuit.Add(new Sid6581());
        var vdd = circuit.Add(new DcVoltageSource(12));
        circuit.Connect(vdd.Negative, computer.Ground.Pin);
        circuit.Connect(sid.Vdd, vdd.Positive);
        circuit.Connect(sid.Vcc, computer.Rail.Positive);
        circuit.Connect(sid.Gnd, computer.Ground.Pin);
        for (var i = 0; i < 8; i++) circuit.Connect(sid.Data[i], computer.Cpu.Data[i]);
        for (var i = 0; i < 5; i++) circuit.Connect(sid.Address[i], computer.Cpu.Address[i]);
        circuit.Connect(sid.ChipSelect, computer.IoSelect);
        circuit.Connect(sid.ReadWrite, computer.Cpu.ReadWrite);
        circuit.Connect(sid.Clock, computer.Clock.Output);
        circuit.Connect(sid.Reset, computer.Cpu.Reset);
        var c1 = circuit.Add(new Capacitor(470e-12));
        var c2 = circuit.Add(new Capacitor(470e-12));
        circuit.Connect(c1.A, sid.Cap1A);
        circuit.Connect(c1.B, sid.Cap1B);
        circuit.Connect(c2.A, sid.Cap2A);
        circuit.Connect(c2.B, sid.Cap2B);

        computer.Simulator.Run(4.5e-3);

        Assert.Equal(0x40, sid.Core.Voices[2].Frequency >> 8);
        Assert.Equal(0xFF, computer.Ram.ReadByte(0x200));     // the attack is long over
        Assert.NotEqual(0, computer.Ram.ReadByte(0x201));
        Assert.Equal(0, computer.Cpu.UndefinedReads);
        Assert.Equal("vol 15 gates 001", sid.ValueLabel);
    }

    /// <summary>/RES clears the registers and silences the voices.</summary>
    [Fact]
    public void ResetSilencesIt()
    {
        var b = new Bench(new Sid6581());
        b.Write((0x01, 0x20), (0x06, 0xF0), (0x18, 0x0F), (0x04, SidCore.Pulse | SidCore.Gate));
        b.Simulator.Run(3e-3);
        Assert.Equal(0xFF, b.Sid.Core.Voices[0].Envelope);

        b.ResetLine.State = false;
        b.Simulator.Run(5e-6);
        Assert.Equal(0, b.Sid.Core.Voices[0].Envelope);
        Assert.Equal(0, b.Sid.Core.Volume);
        Assert.Equal(0, b.Sid.Core.Voices[0].Frequency);
    }

    /// <summary>A run hands its SID back whole: registers, envelopes and the measured clock.</summary>
    [Fact]
    public void TheRunStateCarriesTheVoices()
    {
        var b = new Bench(new Sid8580());
        b.Write((0x01, 0x20), (0x06, 0xF0), (0x18, 0x0F), (0x04, SidCore.Pulse | SidCore.Gate));
        b.Simulator.Run(1e-3);

        var other = new Sid8580();
        other.AdoptRunState(b.Sid.CaptureRunState());
        Assert.Equal(b.Sid.Core.Voices[0].Envelope, other.Core.Voices[0].Envelope);
        Assert.Equal(15, other.Core.Volume);
        Assert.Equal(1e6, other.ClockFrequency, 1.0);
        Assert.NotSame(b.Sid.Core, other.Core);
    }
}
