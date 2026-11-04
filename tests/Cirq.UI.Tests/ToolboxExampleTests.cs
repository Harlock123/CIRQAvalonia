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
}
