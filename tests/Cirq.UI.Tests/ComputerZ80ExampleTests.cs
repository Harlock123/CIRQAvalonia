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
/// The Z80 examples run for real: the monitor's banner and its dump of the ROM on the terminal
/// through the SIO, the CTC's interrupts moving a count on the PIO's LEDs and the button turning it
/// round through the daisy chain — and the programs in the ROMs being exactly the source the guide
/// prints, round-tripped through the disassembler.
/// </summary>
public class ComputerZ80ExampleTests
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

    /// <summary>The monitor's banner, the terminal's command echoed, and the eight bytes at $0000 dumped.</summary>
    [Fact]
    public void TheMonitorDumpsMemoryForTheTerminal()
    {
        using var vm = Run("Z80 Single-Board Computer", 6.5e-3);

        var terminal = vm.Circuit.Components.OfType<SerialTerminal>().Single();
        Assert.StartsWith("CIRQ Z80 monitor", terminal.ReceivedText);
        Assert.Contains("> D 0000", terminal.ReceivedText);
        var image = Examples.ImageZ80(Examples.MonitorZ80Program);
        Assert.Contains("0000: " + string.Join(" ", image.Take(8).Select(b => b.ToString("X2"))), terminal.ReceivedText);
        Assert.Equal(0, terminal.FramingErrors);

        var cpu = vm.Circuit.Components.OfType<CpuZ80>().Single();
        Assert.Equal(0, cpu.UndefinedReads);
        var sio = vm.Circuit.Components.OfType<Z80Sio>().Single();
        Assert.Equal("D 0000\r", System.Text.Encoding.ASCII.GetString([.. sio.ReceivedBytes(false)]));
        Assert.InRange(sio.Baud(false).Transmit, 115000, 115400);
    }

    /// <summary>The CTC's channel 1 moves the count on the LEDs about once a millisecond; a press of the button turns it round.</summary>
    [Fact]
    public void TheCtcCountsOnThePiosLedsAndTheButtonTurnsItRound()
    {
        using var vm = Run("Z80 CTC and PIO Interrupts", 6e-3);
        var sim = vm.Simulation.Simulator!;
        var pio = vm.Circuit.Components.OfType<Z80Pio>().Single();
        var ctc = vm.Circuit.Components.OfType<Z80Ctc>().Single();
        var cpu = vm.Circuit.Components.OfType<CpuZ80>().Single();

        var count = pio.Describe(false).Output;
        Assert.InRange(count, 4, 7);
        Assert.InRange(ctc.Describe(1).ZeroCounts - count, 0, 1);
        Assert.Equal(0x12, cpu.Core.LastAcknowledgeByte);
        Assert.Contains(cpu.BusLog, c => c.Halted);
        Assert.InRange(sim.NodeVoltage(pio.PortA[0]), (count & 1) != 0 ? 1.5 : -0.1, (count & 1) != 0 ? 5.1 : 0.5);

        var button = vm.Circuit.Components.OfType<PushButton>().Last();     // the first is the reset button
        button.IsPressed = true;
        sim.Run(0.2e-3);
        button.IsPressed = false;
        var turned = pio.Describe(false).Output;
        sim.Run(3e-3);
        Assert.True(pio.Describe(false).Output < turned, $"{pio.Describe(false).Output} after {turned}");
        Assert.Equal(1, pio.Acknowledges);
        Assert.Equal(0, cpu.UndefinedReads);
    }

    /// <summary>The Z80 bus decode: instructions where they start, refreshes, the SIO's writes and the summary.</summary>
    [Fact]
    public void TheBusDecoderReadsTheZ80()
    {
        using var vm = Run("Z80 Single-Board Computer", 40e-6);

        var decode = new BusDecodeViewModel(vm.Circuit) { Protocol = BusProtocol.CpuZ80 };
        decode.Run();

        Assert.False(decode.UsesA);
        Assert.Contains(decode.Rows, r => r.Text == "$0000 LD SP,$0000");
        Assert.Contains(decode.Rows, r => r.Text == "$000A OTIR");
        Assert.Contains(decode.Rows, r => r.Text == "OUT $82 18");
        Assert.Contains(decode.Rows, r => r.Text.StartsWith("RFSH $00", StringComparison.Ordinal));
        Assert.StartsWith("U2 (Z80): ", decode.Summary);
        Assert.Contains("refreshes", decode.Summary);
    }

    /// <summary>
    /// Each example's ROM holds the program the guide prints, assembled; and every instruction in
    /// its code disassembles to text that assembles back to the same bytes.
    /// </summary>
    [Theory]
    [InlineData("Z80 Single-Board Computer", "SIOAD   equ $80", "start", "siotab")]
    [InlineData("Z80 CTC and PIO Interrupts", "CTC0    equ $A0", "start", "")]
    public void TheRomHoldsTheSourceTheGuidePrints(string example, string marker, string from, string to)
    {
        var guide = File.ReadAllText(Path.Combine(DocPlot.ImageDirectory, "..", "USER_GUIDE.md"));
        var source = Regex.Matches(guide, "```asm\n(.*?)```", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .Single(code => code.Contains(marker, StringComparison.Ordinal));

        var output = AssemblerZ80.Assemble(source);
        var image = output.Image(0x0000, 0x8000);

        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        Examples.All.Single(e => e.Name == example).Build(vm);
        Assert.Equal(image, vm.Circuit.Components.OfType<Eprom27C256>().Single().Snapshot());

        // The code, from its start to its first data, through the disassembler and back.
        var end = to.Length > 0 ? (int)output.Label(to) : output.Bytes.Keys.Max() + 1;
        for (var at = (int)output.Label(from); at < end;)
        {
            var (text, length) = OpcodesZ80.Disassemble(at, a => image[a & 0x7FFF]);
            var again = AssemblerZ80.Assemble($"    org ${at:X4}\n    {text}\n").Image(at, length);
            Assert.Equal(image.Skip(at).Take(length), again);
            at += length;
        }
    }

    /// <summary>The designators the computer_z80 recipe names are the example's.</summary>
    [Fact]
    public void TheRecipesDesignatorsAreTheExamples()
    {
        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        Examples.All.Single(e => e.Name == "Z80 Single-Board Computer").Build(vm);
        string Of<T>() => vm.Circuit.Components.OfType<T>().Cast<Cirq.Core.Topology.CircuitComponent>().First().Name;

        Assert.Equal("U2", Of<CpuZ80>());
        Assert.Equal("U3", Of<Eprom27C256>());
        Assert.Equal("U4", Of<Sram62256>());
        Assert.Equal("U7", Of<Z80Sio>());
        Assert.Equal("U8", Of<SerialTerminal>());
    }
}

/// <summary>The guide's pictures of the Z80 examples, drawn after the programs have run.</summary>
[Collection(WindowCollection.Name)]
public class ComputerZ80FigureTests(WindowSession session)
{
    private static void Shoot(string example, string file, Action<MainWindowViewModel> run)
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
            run(vm);

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
        Shoot("Z80 Single-Board Computer", "60-z80-computer.png", vm =>
        {
            vm.Simulation.Simulator!.Run(6.5e-3);
            Assert.Contains("0000:", vm.Circuit.Components.OfType<SerialTerminal>().Single().ReceivedText);
        }));

    [Fact]
    public void Counter() => session.Run(() =>
        Shoot("Z80 CTC and PIO Interrupts", "61-z80-ctc-pio.png", vm =>
        {
            vm.Simulation.Simulator!.Run(12e-3);
            Assert.True(vm.Circuit.Components.OfType<Z80Pio>().Single().Describe(false).Output >= 8);
        }));
}
