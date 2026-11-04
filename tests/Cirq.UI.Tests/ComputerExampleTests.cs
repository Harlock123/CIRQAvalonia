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
/// The two 6502 examples, run for real: the hello world on the LCD, the greeting and the echo on
/// the terminal — and the programs in their ROMs being exactly the source the guide prints.
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
    }

    /// <summary>
    /// Each example's ROM holds the program the guide prints, assembled — so the source a reader is
    /// shown is the program that runs, and the reset vector points at it.
    /// </summary>
    [Theory]
    [InlineData("Ben Eater 6502 Computer", "PORTB   = $6000")]
    [InlineData("6502 Serial Terminal", "DATA    = $5000")]
    public void TheRomHoldsTheSourceTheGuidePrints(string example, string marker)
    {
        var guide = File.ReadAllText(Path.Combine(DocPlot.ImageDirectory, "..", "USER_GUIDE.md"));
        var source = Regex.Matches(guide, "```asm\n(.*?)```", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .Single(code => code.Contains(marker, StringComparison.Ordinal));

        var expected = Assembler6502.Assemble(source).Rom32K();

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
    public void SerialComputer() => session.Run(() =>
        Shoot("6502 Serial Terminal", "47-serial-6502.png", 13.5e-3,
            vm => Assert.EndsWith("hi there", vm.Circuit.Components.OfType<SerialTerminal>().Single().ReceivedText)));
}
