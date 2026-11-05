using System.Text.RegularExpressions;
using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Processors;
using Cirq.Components.Tests;
using Cirq.UI.Rendering;
using Cirq.UI.Services;
using Cirq.UI.Tests.Support;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The 68000 examples run for real: the monitor's banner and its dump of the ROM on the terminal,
/// the MFP's timer interrupts counting and printing, the button setting the count back — and the
/// programs in the ROM pairs being exactly the source the guide prints.
/// </summary>
public class Computer68000ExampleTests
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

    /// <summary>The monitor's banner, the terminal's command echoed, and the eight bytes at $000400 dumped.</summary>
    [Fact]
    public void TheMonitorDumpsMemoryForTheTerminal()
    {
        using var vm = Run("68000 Single-Board Computer", 21e-3);

        var terminal = vm.Circuit.Components.OfType<SerialTerminal>().Single();
        Assert.StartsWith("CIRQ 68000 monitor", terminal.ReceivedText);
        Assert.Contains("> D 000400", terminal.ReceivedText);
        Assert.Contains("000400: 13 FC 00 10 00 C0 00 05", terminal.ReceivedText);
        Assert.Equal(0, terminal.FramingErrors);

        var cpu = vm.Circuit.Components.OfType<Cpu68000>().Single();
        Assert.Equal(0, cpu.UndefinedReads);
        Assert.True(cpu.WaitStates > 1000);       // QB: a wait state on every memory cycle
        Assert.Equal("D 000400\r", System.Text.Encoding.ASCII.GetString([.. vm.Circuit.Components.OfType<Duart68681>().Single().ReceivedA]));
    }

    /// <summary>Timer A's interrupts count; the main loop prints each eighth; the button on GPIP7 sets the count back to zero.</summary>
    [Fact]
    public void TheMfpCountsTimerInterrupts()
    {
        using var vm = Run("68000 Timer Interrupts", 13e-3);
        var sim = vm.Simulation.Simulator!;

        var terminal = vm.Circuit.Components.OfType<SerialTerminal>().Single();
        Assert.StartsWith("MFP ticks", terminal.ReceivedText);
        Assert.Contains("0008", terminal.ReceivedText);

        var cpu = vm.Circuit.Components.OfType<Cpu68000>().Single();
        var mfp = vm.Circuit.Components.OfType<Mfp68901>().Single();
        Assert.Equal(0x4D, cpu.Core.LastException);
        Assert.True(mfp.TimerState(0).Timeouts >= 10);
        Assert.Equal(0, cpu.UndefinedReads);

        int Ticks()
        {
            var even = vm.Circuit.Components.OfType<Sram62256>().First();
            var odd = vm.Circuit.Components.OfType<Sram62256>().Last();
            return (even.ReadByte(0) << 8) | odd.ReadByte(0);
        }

        Assert.InRange(Ticks(), 10, 30);
        var button = vm.Circuit.Components.OfType<PushButton>().Last();     // the first is the reset button
        button.IsPressed = true;
        sim.Run(0.3e-3);
        Assert.InRange(Ticks(), 0, 1);
        button.IsPressed = false;
    }

    /// <summary>The 68000 bus decode: instructions disassembled where they start, a byte written to the DUART on the odd half, the wait states.</summary>
    [Fact]
    public void TheBusDecoderReadsThe68000()
    {
        using var vm = Run("68000 Single-Board Computer", 120e-6);

        var decode = new BusDecodeViewModel(vm.Circuit) { Protocol = BusProtocol.Cpu68000 };
        decode.Run();

        Assert.False(decode.UsesA);
        Assert.Contains(decode.Rows, r => r.Text == "$000400 MOVE.B #$10,$C00005");
        Assert.Contains(decode.Rows, r => r.Text == "W.B $C00005 10 (SD)");
        Assert.Contains(decode.Rows, r => r.Text == "R.W $000004 0000 (SP)");
        Assert.StartsWith("U2 (MC68000): ", decode.Summary);
        Assert.Contains("wait states", decode.Summary);
    }

    /// <summary>Each example's ROM pair holds the program the guide prints, assembled and split even and odd.</summary>
    [Theory]
    [InlineData("68000 Single-Board Computer", "DUART   equ $C00001")]
    [InlineData("68000 Timer Interrupts", "MFP     equ $C00001")]
    public void TheRomPairHoldsTheSourceTheGuidePrints(string example, string marker)
    {
        var guide = File.ReadAllText(Path.Combine(DocPlot.ImageDirectory, "..", "USER_GUIDE.md"));
        var source = Regex.Matches(guide, "```asm\n(.*?)```", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .Single(code => code.Contains(marker, StringComparison.Ordinal));

        var image = Assembler68000.Assemble(source).Image(0, 0x10000);

        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        Examples.All.Single(e => e.Name == example).Build(vm);
        var roms = vm.Circuit.Components.OfType<Eeprom28C256>().ToList();

        Assert.Equal(Assembler68000.Output.Lane(image, odd: false), roms[0].Snapshot());
        Assert.Equal(Assembler68000.Output.Lane(image, odd: true), roms[1].Snapshot());
    }

    /// <summary>The designators the computer_68000 recipe names are the example's.</summary>
    [Fact]
    public void TheRecipesDesignatorsAreTheExamples()
    {
        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        Examples.All.Single(e => e.Name == "68000 Single-Board Computer").Build(vm);
        string Of<T>() => vm.Circuit.Components.OfType<T>().Cast<Cirq.Core.Topology.CircuitComponent>().First().Name;

        Assert.Equal("U2", Of<Cpu68000>());
        Assert.Equal(["U3", "U4", "U5", "U6"], vm.Circuit.Components.OfType<ParallelMemory>().Select(m => m.Name));
        Assert.Equal("U13", Of<Duart68681>());
        Assert.Equal("U14", Of<SerialTerminal>());
    }
}

/// <summary>The guide's pictures of the 68000 examples, drawn after the programs have run.</summary>
[Collection(WindowCollection.Name)]
public class Computer68000FigureTests(WindowSession session)
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
    public void Monitor() => session.Run(() =>
        Shoot("68000 Single-Board Computer", "56-68000-computer.png", 21e-3,
            vm => Assert.Contains("000400:", vm.Circuit.Components.OfType<SerialTerminal>().Single().ReceivedText)));

    [Fact]
    public void Ticks() => session.Run(() =>
        Shoot("68000 Timer Interrupts", "57-68000-mfp.png", 13e-3,
            vm => Assert.Contains("0008", vm.Circuit.Components.OfType<SerialTerminal>().Single().ReceivedText)));
}
