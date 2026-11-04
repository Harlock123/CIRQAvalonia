using Cirq.Components.Digital;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Core.Probing;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The examples built for the 0.41 parts — the lamp, the logic blocks, the Darlington, the
/// analog thermometers, the pixel strip, the keypad, the two new boards and the adder and
/// up/down counters. Each is run the way the menu runs it and held to what it is there to show.
/// </summary>
public class ToolboxExampleTests
{
    private static MainWindowViewModel Load(string name)
    {
        var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        vm.Scope.ClearProbes();
        Examples.All.Single(e => e.Name == name).Build(vm);
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);
        return vm;
    }

    /// <summary>
    /// The editor starts a run at switch-on, so the first few milliseconds are the inrush: about
    /// ten times the current the lamp settles at.
    /// </summary>
    [Fact]
    public void TheLampDrawsItsInrushAndSettles()
    {
        using var vm = Load("Lamp Inrush");
        var sim = vm.Simulation.Simulator!;
        var lamp = vm.Circuit.Components.OfType<IncandescentLamp>().Single();

        var peak = 0.0;
        while (sim.Time < 5e-3)
        {
            sim.Step();
            peak = Math.Max(peak, lamp.Current);
        }

        sim.Run(0.3);

        Assert.True(peak > 3.5, $"the inrush peaked at only {peak:0.00} A");
        Assert.Equal(5.0 / 12.0, lamp.Current, 0.02);
        Assert.Contains(vm.Scope.Probes, p => p.Kind is ProbeKind.Current);
    }

    /// <summary>Eleven clock periods in, the four stages read eleven.</summary>
    [Fact]
    public void TheRippleCounterCountsTheClockInBinary()
    {
        using var vm = Load("Ripple Counter");
        var sim = vm.Simulation.Simulator!;
        var stages = vm.Circuit.Components.OfType<TFlipFlop>().OrderBy(s => s.X).ToArray();

        // The clock's first rising edge is at the start, and the counter counts falling ones of Q'.
        sim.Run(11.25e-3);

        var count = stages.Select((s, i) => s.State == Cirq.Core.Digital.LogicState.High ? 1 << i : 0).Sum();

        Assert.Equal(11, count);
        Assert.Contains(vm.Circuit.Components.OfType<Led>(), l => l.Brightness > 0.3);
    }

    [Fact]
    public void TheTriStateBusIsHandedFromOneBufferToTheOther()
    {
        using var vm = Load("Tri-State Bus");
        var sim = vm.Simulation.Simulator!;
        var buffers = vm.Circuit.Components.OfType<TriStateBuffer>().OrderBy(b => b.Y).ToArray();

        // Step 4 of six: B has it, and B's input is a steady high.
        sim.Run(3.5e-3);
        Assert.False(buffers[0].IsEnabled);
        Assert.True(buffers[1].IsEnabled);
        Assert.True(sim.NodeVoltage(buffers[1].Out) > 3.0);

        // Step 6: nobody has it, and the pull-down does.
        sim.Run(2e-3);
        Assert.False(buffers[0].IsEnabled || buffers[1].IsEnabled);
        Assert.True(sim.NodeVoltage(buffers[1].Out) < 0.1);
        Assert.DoesNotContain(buffers, b => b.IsContending);
    }
}
