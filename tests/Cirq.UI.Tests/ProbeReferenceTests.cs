using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Probing;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>Setting a probe's reference point, which on the canvas is a shift-click.</summary>
public class ProbeReferenceTests
{
    private static (MainWindowViewModel, Resistor) Rig()
    {
        var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        vm.Scope.ClearProbes();

        var supply = vm.Circuit.Add(new DcVoltageSource(12.0));
        var resistor = vm.Circuit.Add(new Resistor(100.0));
        var load = vm.Circuit.Add(new Resistor(200.0));
        var ground = vm.Circuit.Add(new Ground());

        vm.Circuit.Connect(supply.Negative, ground.Pin);
        vm.Circuit.Connect(supply.Positive, resistor.A);
        vm.Circuit.Connect(resistor.B, load.A);
        vm.Circuit.Connect(load.B, ground.Pin);

        return (vm, resistor);
    }

    [Fact]
    public void SettingAReferenceMakesTheProbeDifferential()
    {
        var (vm, resistor) = Rig();

        vm.AttachProbe(resistor.A);
        vm.SetProbeReference(resistor.B);

        var probe = Assert.Single(vm.Circuit.Probes);

        Assert.Equal(ProbeKind.Differential, probe.Kind);
        Assert.Same(resistor.B, probe.ReferenceTerminal);
        Assert.True(probe.IsDerived);
        Assert.Contains(resistor.B.ToString(), vm.StatusMessage);
    }

    [Fact]
    public void SettingTheSameTerminalAgainClearsItBackToGround()
    {
        var (vm, resistor) = Rig();

        vm.AttachProbe(resistor.A);
        vm.SetProbeReference(resistor.B);
        vm.SetProbeReference(resistor.B);

        var probe = Assert.Single(vm.Circuit.Probes);

        Assert.Null(probe.ReferenceTerminal);
        Assert.Equal("ground", probe.ReferenceLabel);
        Assert.Contains("ground", vm.StatusMessage);
    }

    /// <summary>
    /// A power probe already measures between two points, so pointing it somewhere else must not
    /// quietly turn it back into a voltage measurement.
    /// </summary>
    [Fact]
    public void AReferenceOnAPowerProbeLeavesItAPowerProbe()
    {
        var (vm, resistor) = Rig();

        vm.AttachProbe(resistor.A);
        vm.Circuit.Probes[0].Kind = ProbeKind.Power;

        vm.SetProbeReference(resistor.B);

        Assert.Equal(ProbeKind.Power, vm.Circuit.Probes[0].Kind);
        Assert.Same(resistor.B, vm.Circuit.Probes[0].ReferenceTerminal);
    }

    [Fact]
    public void WithNoTraceSelectedItSaysSoRatherThanDoingNothing()
    {
        var (vm, resistor) = Rig();

        vm.Scope.SelectedProbe = null;
        vm.SetProbeReference(resistor.B);

        Assert.Empty(vm.Circuit.Probes);
        Assert.Contains("Select a trace", vm.StatusMessage);
    }

    [Fact]
    public void ThePowerProbeReadsWhatTheResistorIsDissipating()
    {
        var (vm, resistor) = Rig();

        vm.AttachProbe(resistor.A);
        vm.Circuit.Probes[0].Kind = ProbeKind.Power;
        vm.SetProbeReference(resistor.B);

        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var sim = vm.Simulation.Simulator!;
        var watts = Math.Abs(sim.SampleProbe(vm.Circuit.Probes[0]));

        // 12 V across 300 Ω is 40 mA; 40 mA squared through 100 Ω is 160 mW.
        Assert.Equal(0.16, watts, 4);
        Assert.Equal("W", vm.Circuit.Probes[0].Unit);
    }
}
