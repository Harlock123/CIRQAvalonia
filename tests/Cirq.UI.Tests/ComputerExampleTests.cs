using System.Text.RegularExpressions;
using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Processors;
using Cirq.Components.Tests;
using Cirq.UI.Rendering;
using Cirq.UI.Services;
using Cirq.UI.Tests.Support;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The 6502 and 6510 examples, run for real: the hello world on the LCD, the greeting and the echo
/// on the terminal, the SID playing its tune and the CIA reading the keypad — and the programs in
/// their ROMs being exactly the source the guide prints.
/// </summary>
public class ComputerExampleTests
{
    private static MainWindowViewModel Run(string example, double seconds)
    {
        var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        vm.Scope.ClearProbes();
        Examples.All.Single(e => e.Name == example).Build(vm);
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);
        vm.Simulation.Simulator!.Run(seconds);
        return vm;
    }

    [Fact]
    public void TheBenEaterComputerSaysHelloOnTheLcd()
    {
        using var vm = Run("Ben Eater 6502 Computer", 3.5e-3);

        var lcd = vm.Circuit.Components.OfType<CharacterLcd>().Single();
        Assert.True(lcd.DisplayOn);
        Assert.Equal("Hello, world!", lcd.Line(0).TrimEnd());

        var cpu = vm.Circuit.Components.OfType<Cpu65C02>().Single();
        Assert.Equal(0, cpu.UndefinedReads);
        Assert.Equal(0x8022, cpu.Core.InstructionAddress);   // idling in its last loop
    }

    [Fact]
    public void TheSerialComputerGreetsAndEchoes()
    {
        using var vm = Run("6502 Serial Terminal", 13.5e-3);

        var terminal = vm.Circuit.Components.OfType<SerialTerminal>().Single();
        Assert.Equal("Hello, 6502!..hi there", terminal.ReceivedText);
        Assert.Equal(0, terminal.FramingErrors);
        Assert.Equal(("Hello, 6502!", "hi there"), SymbolRenderer.TerminalLines(terminal));
    }

    /// <summary>
    /// The SID player: the CIA's timer A runs at its 5,000-cycle latch, the interrupt handler steps
    /// the tune — the first bar's A2 on the bass, the A minor arpeggio, the lead on E5 — and AUDIO OUT
    /// swings about its bias into the amplifier, with P0 marking the beat.
    /// </summary>
    [Fact]
    public void TheSidPlayerPlaysItsTune()
    {
        using var vm = Run("6510 SID Player", 12e-3);
        var sim = vm.Simulation.Simulator!;
        var sid = vm.Circuit.Components.OfType<Sid6581>().Single();
        var cia = vm.Circuit.Components.OfType<Cia6526>().Single();
        var cpu = vm.Circuit.Components.OfType<Cpu6510>().Single();

        Assert.Equal(4999, cia.TimerALatch);
        Assert.Equal(0x01, cia.ControlA & 0x09);           // running, continuous
        Assert.Equal(0, cpu.UndefinedReads);
        Assert.Equal(0x01, cpu.PortDirection);

        // A2 on the bass, the arpeggio somewhere in A minor, E5 on the lead with its vibrato.
        Assert.Equal(110, sid.VoiceFrequency(0), 0.5);
        Assert.Contains(Math.Round(sid.VoiceFrequency(1)), new double[] { 440, 523, 659 });
        Assert.Equal(659, sid.VoiceFrequency(2), 659 * 0.02);
        Assert.Equal("vol 15 gates 111", sid.ValueLabel);

        double low = double.MaxValue, high = double.MinValue;
        var end = sim.Time + 3e-3;
        while (sim.Time < end)
        {
            sim.Step();
            var v = sim.NodeVoltage(sid.AudioOut);
            low = Math.Min(low, v);
            high = Math.Max(high, v);
        }

        Assert.InRange(high - low, 0.5, 3.0);
        Assert.InRange((high + low) / 2, 3.5, 5.0);    // half VDD, less what the C64's 1 kΩ load takes
        Assert.InRange(vm.Circuit.Components.OfType<Sram62256>().Single().ReadByte(0x02), 0, 4);   // the tick within the step
    }

    /// <summary>
    /// The guide's picture of what the SID plays: AUDIO OUT over ten milliseconds of the first bar,
    /// and its spectrum over forty — the bass's 110 Hz and its harmonics, the A minor arpeggio's
    /// A, C and E, and the lead's E5 — all from the circuit's own run.
    /// </summary>
    [Fact]
    public void TheSidsOutputIsDrawnAsAWaveformAndASpectrum()
    {
        using var vm = Run("6510 SID Player", 5e-3);
        var sim = vm.Simulation.Simulator!;
        var sid = vm.Circuit.Components.OfType<Sid6581>().Single();

        List<Cirq.Core.Primitives.DataPoint> samples = [];
        var next = sim.Time;
        while (sim.Time < 46e-3)
        {
            sim.Step();
            if (sim.Time < next) continue;
            samples.Add(new(sim.Time, sim.NodeVoltage(sid.AudioOut)));
            next += 10e-6;
        }

        var spectrum = Cirq.Engine.Simulation.Spectrum.Of(samples, samples[0].Time, samples[^1].Time,
            new Cirq.Engine.Simulation.SpectrumRequest(Cirq.Engine.Simulation.SpectrumWindow.Hann, 4096));

        var mean = samples.Average(p => p.Value);
        var window = samples.Where(p => p.Time is > 14e-3 and < 24e-3).ToList();
        var upTo = spectrum.Frequencies.Count(f => f <= 3000);

        DocPlot.Stack("54-sid-output.png", (wave, spec) =>
        {
            DocPlot.Style(wave, "Time (ms)", "AUDIO OUT, AC (V)");
            DocPlot.Line(wave, window.Select(p => p.Time * 1e3), window.Select(p => p.Value - mean), 0);
            DocPlot.Title(wave, "The 6581's AUDIO OUT in the first bar: bass, arpeggio and lead together");

            DocPlot.Style(spec, "Frequency (Hz)", "Amplitude (dB)");
            DocPlot.Line(spec, spectrum.Frequencies.Take(upTo), Enumerable.Range(0, upTo).Select(spectrum.Decibels), 1);
            foreach (var (note, name) in new[] { (110.0, "A2"), (440.0, "A4"), (523.25, "C5"), (659.26, "E5") })
                DocPlot.Vertical(spec, note, name);
            spec.Axes.SetLimitsY(-90, 0);
        });

        // Each note the bar holds stands up out of what the pulse's harmonics spread above them.
        double Level(double f) => Enumerable.Range(0, spectrum.Frequencies.Count)
            .Where(i => Math.Abs(spectrum.Frequencies[i] - f) < 30).Max(spectrum.Decibels);
        var high = Enumerable.Range(0, spectrum.Frequencies.Count)
            .Where(i => spectrum.Frequencies[i] is > 2000 and < 3000).Average(spectrum.Decibels);
        foreach (var note in new[] { 110.0, 440, 523, 659 })
            Assert.True(Level(note) > high + 12, $"no peak at {note} Hz: {Level(note):0} dB against {high:0}");
    }

    /// <summary>The CIA keypad: each key pressed and let go in turn ends up on the display.</summary>
    [Fact]
    public void TheCiaReadsTheKeypadOntoTheLcd()
    {
        using var vm = Run("CIA Keypad", 6e-3);
        var sim = vm.Simulation.Simulator!;
        var lcd = vm.Circuit.Components.OfType<CharacterLcd>().Single();
        var keypad = vm.Circuit.Components.OfType<Cirq.Components.Passive.MatrixKeypad>().Single();
        Assert.Equal("Keys:", lcd.Line(0).TrimEnd());

        foreach (var key in new[] { "6", "5", "0", "2", "#" })
        {
            keypad.SetPressed(key, true);
            sim.Run(2.5e-3);
            keypad.SetPressed(key, false);
            sim.Run(2.5e-3);
        }

        Assert.Equal("Keys: 6502#", lcd.Line(0).TrimEnd());
        Assert.Equal(0, vm.Circuit.Components.OfType<Cpu6510>().Single().UndefinedReads);
    }

    /// <summary>The bus decoder reads the 6502's cycles, with the instructions disassembled on SYNC.</summary>
    [Fact]
    public void TheBusDecoderReadsTheProcessorsCycles()
    {
        using var vm = Run("Ben Eater 6502 Computer", 30e-6);

        var decode = new BusDecodeViewModel(vm.Circuit);
        Assert.Equal(BusProtocol.Cpu6502, decode.Protocol);
        decode.Run();

        Assert.Contains(decode.Rows, r => r.Text == "$8000 LDX #$FF");
        Assert.Contains(decode.Rows, r => r.Text == "$8003 LDA #$E0");
        Assert.Contains(decode.Rows, r => r.Text == "W $6003 E0");
        Assert.Contains("instructions", decode.Summary);
        Assert.StartsWith("U2 (W65C02S): ", decode.Summary);
    }

    /// <summary>The 6510 shares the 6502's core, so its bus decodes the same way, labelled with its own part number.</summary>
    [Fact]
    public void TheBusDecoderReadsA6510()
    {
        using var vm = Run("CIA Keypad", 20e-6);

        var decode = new BusDecodeViewModel(vm.Circuit);
        Assert.Equal(BusProtocol.Cpu6502, decode.Protocol);
        decode.Run();

        Assert.Contains(decode.Rows, r => r.Text == "$8000 LDX #$FF");
        Assert.StartsWith("U2 (6510): ", decode.Summary);
    }

    /// <summary>
    /// With one processor the decode reads its record whatever channel A says, so the window hides
    /// that picker rather than show an unrelated probe (the LCD's E line) under the word CPU.
    /// </summary>
    [Fact]
    public void OneProcessorNeedsNoProcessorPicker()
    {
        using var vm = Run("Ben Eater 6502 Computer", 1e-6);

        var decode = new BusDecodeViewModel(vm.Circuit);
        Assert.False(decode.UsesA);

        decode.Protocol = BusProtocol.Uart;
        Assert.True(decode.UsesA);
    }

    /// <summary>
    /// Each example's ROM holds the program the guide prints, assembled — so the source a reader is
    /// shown is the program that runs, and the reset vector points at it.
    /// </summary>
    [Theory]
    [InlineData("Ben Eater 6502 Computer", "PORTB   = $6000")]
    [InlineData("6502 Serial Terminal", "DATA    = $5000")]
    [InlineData("6510 SID Player", "SID     = $5000", true)]
    [InlineData("CIA Keypad", "PRA     = $4000", true)]
    public void TheRomHoldsTheSourceTheGuidePrints(string example, string marker, bool nmos = false)
    {
        var guide = File.ReadAllText(Path.Combine(DocPlot.ImageDirectory, "..", "USER_GUIDE.md"));
        var source = Regex.Matches(guide, "```asm\n(.*?)```", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .Single(code => code.Contains(marker, StringComparison.Ordinal));

        var expected = Assembler6502.Assemble(source, nmos ? Cpu6502Variant.Nmos6502 : Cpu6502Variant.Wdc65C02).Rom32K();

        var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        Examples.All.Single(e => e.Name == example).Build(vm);
        var rom = vm.Circuit.Components.OfType<Eeprom28C256>().Single();

        Assert.Equal(expected, rom.Snapshot());
        vm.Dispose();
    }
}

/// <summary>The guide's pictures of the 6502 examples, drawn after the programs have run.</summary>
[Collection(WindowCollection.Name)]
public class ComputerFigureTests(WindowSession session)
{
    private static void Shoot(string example, string file, double seconds, Action<MainWindowViewModel> check)
    {
        var was = ThemeManager.Selected;
        ThemeManager.Apply(AppTheme.Dark);
        CanvasTheme.Invalidate();

        try
        {
            using var vm = new MainWindowViewModel();
            vm.Circuit.Clear();
            vm.Scope.ClearProbes();
            Examples.All.Single(e => e.Name == example).Build(vm);
            Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);
            vm.Simulation.Simulator!.Run(seconds);
            check(vm);

            Directory.CreateDirectory(DocPlot.ImageDirectory);
            var path = Path.Combine(DocPlot.ImageDirectory, file);
            CircuitExporter.Export(vm.Circuit, null, path, new ExportOptions(ExportFormat.Png, ExportContent.Schematic, RasterScale: 1.5));
            Assert.True(new FileInfo(path).Length > 2000, $"{file} came out almost empty");
        }
        finally
        {
            ThemeManager.Apply(was);
            CanvasTheme.Invalidate();
        }
    }

    [Fact]
    public void BenEaterComputer() => session.Run(() =>
        Shoot("Ben Eater 6502 Computer", "46-ben-eater-6502.png", 3.5e-3,
            vm => Assert.Equal("Hello, world!", vm.Circuit.Components.OfType<CharacterLcd>().Single().Line(0).TrimEnd())));

    [Fact]
    public void SidPlayer() => session.Run(() =>
        Shoot("6510 SID Player", "53-sid-player.png", 8e-3,
            vm => Assert.Equal(3, vm.Circuit.Components.OfType<Sid6581>().Single().Core.Voices.Count(v => v.Gated))));

    [Fact]
    public void CiaKeypad() => session.Run(() =>
        Shoot("CIA Keypad", "55-cia-keypad.png", 6e-3, vm =>
        {
            var keypad = vm.Circuit.Components.OfType<Cirq.Components.Passive.MatrixKeypad>().Single();
            foreach (var key in new[] { "6", "5", "0" })
            {
                keypad.SetPressed(key, true);
                vm.Simulation.Simulator!.Run(2.5e-3);
                keypad.SetPressed(key, false);
                vm.Simulation.Simulator!.Run(2.5e-3);
            }

            // The last one still held, so the picture shows a key down.
            keypad.SetPressed("2", true);
            vm.Simulation.Simulator!.Run(2.5e-3);
            Assert.Equal("Keys: 6502", vm.Circuit.Components.OfType<CharacterLcd>().Single().Line(0).TrimEnd());
        }));

    [Fact]
    public void SerialComputer() => session.Run(() =>
        Shoot("6502 Serial Terminal", "47-serial-6502.png", 13.5e-3,
            vm => Assert.EndsWith("hi there", vm.Circuit.Components.OfType<SerialTerminal>().Single().ReceivedText)));
}
