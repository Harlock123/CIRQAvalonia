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
/// The 6809 examples run for real: the monitor's banner and its dump of the EPROM on the terminal,
/// the PIA counting a button's presses by interrupt and showing them on the LCD — and the programs
/// in the EPROMs being exactly the source the guide prints, round-tripped through the disassembler.
/// </summary>
public class Computer6809ExampleTests
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

    /// <summary>The monitor's banner, the terminal's command echoed, and the eight bytes at $E000 dumped.</summary>
    [Fact]
    public void TheMonitorDumpsMemoryForTheTerminal()
    {
        using var vm = Run("6809 Single-Board Computer", 6.5e-3);

        var terminal = vm.Circuit.Components.OfType<SerialTerminal>().Single();
        Assert.StartsWith("CIRQ 6809 monitor", terminal.ReceivedText);
        Assert.Contains("> D E000", terminal.ReceivedText);
        Assert.Contains("E000: 10 CE 80 00 86 03 B7 A0", terminal.ReceivedText);
        Assert.Equal(0, terminal.FramingErrors);

        var cpu = vm.Circuit.Components.OfType<Cpu6809>().Single();
        Assert.Equal(0, cpu.UndefinedReads);
        var acia = vm.Circuit.Components.OfType<Acia6850>().Single();
        Assert.Equal("D E000\r", System.Text.Encoding.ASCII.GetString([.. acia.ReceivedBytes]));
        Assert.InRange(acia.TransmitBaud, 115000, 115400);
    }

    /// <summary>Each press of the button on CA1 interrupts, and the count reaches the LCD's second line.</summary>
    [Fact]
    public void ThePiaCountsButtonPresses()
    {
        using var vm = Run("6809 PIA Button Counter", 2.2e-3);
        var sim = vm.Simulation.Simulator!;
        var lcd = vm.Circuit.Components.OfType<CharacterLcd>().Single();
        Assert.Equal("CIRQ 6809 + 6821", lcd.Line(0));

        var button = vm.Circuit.Components.OfType<PushButton>().Last();     // the first is the reset button
        for (var i = 0; i < 3; i++)
        {
            button.IsPressed = true;
            sim.Run(0.1e-3);
            button.IsPressed = false;
            sim.Run(1.2e-3);
        }

        Assert.Equal("Presses: 03", lcd.Line(1).TrimEnd());
        var cpu = vm.Circuit.Components.OfType<Cpu6809>().Single();
        Assert.Equal("IRQ", cpu.Core.LastInterrupt);
        Assert.Equal(0, cpu.UndefinedReads);
    }

    /// <summary>The 6809 bus decode: instructions where they start, the reset vector, the dead cycles and the ACIA's writes.</summary>
    [Fact]
    public void TheBusDecoderReadsThe6809()
    {
        using var vm = Run("6809 Single-Board Computer", 40e-6);

        var decode = new BusDecodeViewModel(vm.Circuit) { Protocol = BusProtocol.Cpu6809 };
        decode.Run();

        Assert.False(decode.UsesA);
        Assert.Contains(decode.Rows, r => r.Text == "V $FFFE E0");
        Assert.Contains(decode.Rows, r => r.Text == "$E000 LDS #$8000");
        Assert.Contains(decode.Rows, r => r.Text == "$E006 STA $A000");
        Assert.Contains(decode.Rows, r => r.Text == "W $A000 03");
        Assert.Contains(decode.Rows, r => r.Text == "dead $FFFF");
        Assert.StartsWith("U2 (MC6809): ", decode.Summary);
        Assert.Contains("dead", decode.Summary);
    }

    /// <summary>
    /// Each example's EPROM holds the program the guide prints, assembled; and every instruction
    /// in it disassembles to text that assembles back to the same bytes.
    /// </summary>
    [Theory]
    [InlineData("6809 Single-Board Computer", "ACIA    equ $A000")]
    [InlineData("6809 PIA Button Counter", "PORTA   equ $C000")]
    public void TheEpromHoldsTheSourceTheGuidePrints(string example, string marker)
    {
        var guide = File.ReadAllText(Path.Combine(DocPlot.ImageDirectory, "..", "USER_GUIDE.md"));
        var source = Regex.Matches(guide, "```asm\n(.*?)```", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .Single(code => code.Contains(marker, StringComparison.Ordinal));

        var output = Assembler6809.Assemble(source);
        var image = output.Image(0xE000, 0x2000);

        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        Examples.All.Single(e => e.Name == example).Build(vm);
        Assert.Equal(image, vm.Circuit.Components.OfType<Eprom2764>().Single().Snapshot());

        // The code, up to the first text, through the disassembler and back.
        var end = output.Labels.Where(l => l.Key is "banner" or "setup").Min(l => (int)l.Value);
        for (var at = 0xE000; at < end;)
        {
            var (text, length) = Opcodes6809.Disassemble(at, a => image[(a - 0xE000) & 0x1FFF]);
            var again = Assembler6809.Assemble($"    org ${at:X4}\n    {text}\n").Image(at, length);
            Assert.Equal(image.Skip(at - 0xE000).Take(length), again);
            at += length;
        }
    }

    /// <summary>The designators the computer_6809 recipe names are the example's.</summary>
    [Fact]
    public void TheRecipesDesignatorsAreTheExamples()
    {
        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        Examples.All.Single(e => e.Name == "6809 Single-Board Computer").Build(vm);
        string Of<T>() => vm.Circuit.Components.OfType<T>().Cast<Cirq.Core.Topology.CircuitComponent>().First().Name;

        Assert.Equal("U2", Of<Cpu6809>());
        Assert.Equal("U3", Of<Eprom2764>());
        Assert.Equal("U4", Of<Sram62256>());
        Assert.Equal("U7", Of<Acia6850>());
        Assert.Equal("U8", Of<SerialTerminal>());
    }
}

/// <summary>The guide's pictures of the 6809 examples, drawn after the programs have run.</summary>
[Collection(WindowCollection.Name)]
public class Computer6809FigureTests(WindowSession session)
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
        Shoot("6809 Single-Board Computer", "58-6809-computer.png", vm =>
        {
            vm.Simulation.Simulator!.Run(6.5e-3);
            Assert.Contains("E000:", vm.Circuit.Components.OfType<SerialTerminal>().Single().ReceivedText);
        }));

    [Fact]
    public void Buttons() => session.Run(() =>
        Shoot("6809 PIA Button Counter", "59-6809-pia.png", vm =>
        {
            var sim = vm.Simulation.Simulator!;
            sim.Run(2.2e-3);
            var button = vm.Circuit.Components.OfType<PushButton>().Last();
            for (var i = 0; i < 5; i++)
            {
                button.IsPressed = true;
                sim.Run(0.1e-3);
                button.IsPressed = false;
                sim.Run(0.4e-3);
            }

            // A redraw takes about a millisecond, and the last press may arrive in the middle of one.
            sim.Run(2.5e-3);

            Assert.StartsWith("Presses: 05", vm.Circuit.Components.OfType<CharacterLcd>().Single().Line(1));
        }));
}
