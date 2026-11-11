using System.Text.RegularExpressions;
using Cirq.Components.Processors;
using Cirq.Components.Tests;
using Cirq.UI.Tests.Support;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>The ATmega328P example: its firmware the guide's source assembled, blinking the LED twice a second.</summary>
public class AtmegaExampleTests
{
    [Fact]
    public void The_firmware_is_the_source_the_guide_prints()
    {
        var guide = File.ReadAllText(Path.Combine(DocPlot.ImageDirectory, "..", "USER_GUIDE.md"));
        var source = Regex.Matches(guide, "```asm\n(.*?)```", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .Single(code => code.Contains("TIMER1_COMPA", StringComparison.Ordinal));
        Assert.Equal(Examples.AtmegaBlinkFirmware, AssemblerAvr.Assemble(source));
    }

    [Fact]
    public void The_example_blinks_its_led_every_half_second()
    {
        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        vm.Scope.ClearProbes();
        Examples.All.Single(e => e.Name == "ATmega328P Blink").Build(vm);
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);
        Assert.True(vm.Simulation.SteppingByEvents);

        var chip = vm.Circuit.Components.OfType<Atmega328p>().Single();
        Assert.Null(chip.FirmwareError);
        var pb5 = chip.TerminalByName("PB5");
        var sim = vm.Simulation.Simulator!;
        var edges = new List<double>();
        var was = sim.NodeVoltage(pb5) > 2.5;
        while (sim.Time < 1.2)
        {
            sim.Step();
            var high = sim.NodeVoltage(pb5) > 2.5;
            if (high != was) edges.Add(sim.Time);
            was = high;
        }

        Assert.Equal(2, edges.Count);
        Assert.Equal(0.5, edges[0], 0.01);
        Assert.Equal(1.0, edges[1], 0.01);
    }
}
