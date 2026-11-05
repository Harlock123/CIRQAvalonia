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
/// The 8085 examples run for real: the monitor's banner and its dump of the EPROM on the terminal
/// through the 8251A, clocked from the 8156's timer; the 8253's interrupts coming through the 8259A
/// as CALLs and moving a count on the 8255A's LEDs, and the button on RST 7.5 turning it round —
/// and the programs in the EPROMs being exactly the source the guide prints, round-tripped through
/// the disassembler.
/// </summary>
public class Computer8085ExampleTests
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

    /// <summary>The monitor's banner, the terminal's command echoed, and the eight bytes at $0000 dumped, at 19,200 baud.</summary>
    [Fact]
    public void TheMonitorDumpsMemoryForTheTerminal()
    {
        using var vm = Run("8085 Single-Board Computer", 34e-3);

        var terminal = vm.Circuit.Components.OfType<SerialTerminal>().Single();
        Assert.StartsWith("CIRQ 8085 monitor", terminal.ReceivedText);
        Assert.Contains("> D 0000", terminal.ReceivedText);
        var image = Examples.Image8085(Examples.Monitor8085Program);
        Assert.Contains("0000: " + string.Join(" ", image.Take(8).Select(b => b.ToString("X2"))), terminal.ReceivedText);
        Assert.Equal(0, terminal.FramingErrors);

        var cpu = vm.Circuit.Components.OfType<Cpu8085>().Single();
        Assert.Equal(0, cpu.UndefinedReads);
        var uart = vm.Circuit.Components.OfType<Usart8251>().Single();
        Assert.Equal("D 0000\r", System.Text.Encoding.ASCII.GetString([.. uart.ReceivedBytes]));
        Assert.Equal("8N1", uart.Format);
        Assert.InRange(uart.TransmitBaud, 19150, 19250);
        var ram = vm.Circuit.Components.OfType<Ram8156>().Single();
        Assert.True(ram.TimerRunning);
        Assert.Equal((byte)'D', ram.ReadByte(0x00));
    }

    /// <summary>The 8253 interrupts through the 8259A about once a millisecond; a press of the button on RST 7.5 turns the count round.</summary>
    [Fact]
    public void TheTimerCountsOnTheLedsAndTheButtonTurnsItRound()
    {
        using var vm = Run("8085 Timer Interrupts", 6e-3);
        var sim = vm.Simulation.Simulator!;
        var ppi = vm.Circuit.Components.OfType<Ppi8255>().Single();
        var pic = vm.Circuit.Components.OfType<Pic8259>().Single();
        var cpu = vm.Circuit.Components.OfType<Cpu8085>().Single();

        var count = ppi.Describe().OutputA;
        Assert.InRange(count, 4, 7);
        Assert.InRange(pic.Acknowledges - count, 0, 1);
        Assert.Equal([0xCD, 0x40, 0x00], cpu.Core.LastAcknowledgeBytes);
        Assert.Contains(cpu.BusLog, c => c.Kind == Cirq.Components.Processors.I8085CycleKind.Halt);
        Assert.InRange(sim.NodeVoltage(ppi.PortA[0]), (count & 1) != 0 ? 1.5 : -0.1, (count & 1) != 0 ? 5.1 : 0.5);

        var button = vm.Circuit.Components.OfType<PushButton>().Last();     // the first is the reset button
        button.IsPressed = true;
        sim.Run(0.2e-3);
        button.IsPressed = false;
        var turned = ppi.Describe().OutputA;
        sim.Run(3e-3);
        Assert.True(ppi.Describe().OutputA < turned, $"{ppi.Describe().OutputA} after {turned}");
        Assert.Equal(0, cpu.UndefinedReads);
    }

    /// <summary>The 8085 bus decode: instructions where they start, the 8251A's set-up writes, and the summary.</summary>
    [Fact]
    public void TheBusDecoderReadsThe8085()
    {
        using var vm = Run("8085 Single-Board Computer", 150e-6);

        var decode = new BusDecodeViewModel(vm.Circuit) { Protocol = BusProtocol.Cpu8085 };
        decode.Run();

        Assert.False(decode.UsesA);
        Assert.Contains(decode.Rows, r => r.Text == "$0000 LXI SP,$8100");
        Assert.Contains(decode.Rows, r => r.Text == "OUT $84 06");
        Assert.Contains(decode.Rows, r => r.Text == "OUT $01 4E");
        Assert.StartsWith("U2 (8085): ", decode.Summary);
        Assert.Contains("instructions", decode.Summary);
    }

    /// <summary>
    /// Each example's EPROM holds the program the guide prints, assembled; and every instruction in
    /// its code disassembles to text that assembles back to the same bytes.
    /// </summary>
    [Theory]
    [InlineData("8085 Single-Board Computer", "UDATA   equ $00", "start", "banner")]
    [InlineData("8085 Timer Interrupts", "CNT0    equ $20", "start", "")]
    public void TheRomHoldsTheSourceTheGuidePrints(string example, string marker, string from, string to)
    {
        var guide = File.ReadAllText(Path.Combine(DocPlot.ImageDirectory, "..", "USER_GUIDE.md"));
        var source = Regex.Matches(guide, "```asm\n(.*?)```", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .Single(code => code.Contains(marker, StringComparison.Ordinal));

        var output = Assembler8085.Assemble(source);
        var image = output.Image(0x0000, 0x2000);

        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        Examples.All.Single(e => e.Name == example).Build(vm);
        Assert.Equal(image, vm.Circuit.Components.OfType<Eprom2764>().Single().Snapshot());

        var end = to.Length > 0 ? (int)output.Label(to) : output.Bytes.Keys.Max() + 1;
        for (var at = (int)output.Label(from); at < end;)
        {
            var (text, length) = Opcodes8085.Disassemble(at, a => image[a & 0x1FFF]);
            var again = Assembler8085.Assemble($"    org ${at:X4}\n    {text}\n").Image(at, length);
            Assert.Equal(image.Skip(at).Take(length), again);
            at += length;
        }
    }

    /// <summary>The designators the computer_8085 recipe names are the example's.</summary>
    [Fact]
    public void TheRecipesDesignatorsAreTheExamples()
    {
        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        Examples.All.Single(e => e.Name == "8085 Single-Board Computer").Build(vm);
        string Of<T>() => vm.Circuit.Components.OfType<T>().Cast<Cirq.Core.Topology.CircuitComponent>().First().Name;

        Assert.Equal("U2", Of<Cpu8085>());
        Assert.Equal("U3", Of<Ic74373>());
        Assert.Equal("U4", Of<Eprom2764>());
        Assert.Equal("U5", Of<Ram8156>());
        Assert.Equal("U7", Of<Usart8251>());
        Assert.Equal("U8", Of<SerialTerminal>());
    }
}

/// <summary>The guide's pictures of the 8085 examples, drawn after the programs have run.</summary>
[Collection(WindowCollection.Name)]
public class Computer8085FigureTests(WindowSession session)
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
        Shoot("8085 Single-Board Computer", "62-8085-computer.png", vm =>
        {
            vm.Simulation.Simulator!.Run(34e-3);
            Assert.Contains("0000:", vm.Circuit.Components.OfType<SerialTerminal>().Single().ReceivedText);
        }));

    [Fact]
    public void Counter() => session.Run(() =>
        Shoot("8085 Timer Interrupts", "63-8085-interrupts.png", vm =>
        {
            vm.Simulation.Simulator!.Run(12e-3);
            Assert.True(vm.Circuit.Components.OfType<Ppi8255>().Single().Describe().OutputA >= 8);
        }));
}
