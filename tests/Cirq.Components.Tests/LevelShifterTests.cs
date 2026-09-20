using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The level shifter passes signals both ways through a transistor that only conducts one way,
/// so these check it really does work in both directions — and that it stops working for the
/// reasons the hardware stops working.
/// </summary>
public class LevelShifterTests
{
    private sealed record Rig(
        CircuitSimulator Sim,
        LevelShifter Shifter,
        ToggleSwitch LowPull,
        ToggleSwitch HighPull);

    /// <summary>
    /// Three-and-three-tenths on one side, five on the other, and a switch on each side of
    /// channel one that can pull it down — which is all either end of an open-drain bus ever does.
    /// </summary>
    private static Rig Build(double lowRail = 3.3, double highRail = 5.0)
    {
        var circuit = new Circuit();

        var low = circuit.Add(new DcVoltageSource(lowRail));
        var high = circuit.Add(new DcVoltageSource(highRail));
        var gnd = circuit.Add(new Ground());
        var shifter = circuit.Add(new LevelShifter());

        var lowPull = circuit.Add(new ToggleSwitch { IsClosed = false });
        var highPull = circuit.Add(new ToggleSwitch { IsClosed = false });

        circuit.Connect(low.Negative, gnd.Pin);
        circuit.Connect(high.Negative, gnd.Pin);
        circuit.Connect(shifter.Gnd, gnd.Pin);
        circuit.Connect(shifter.LowReference, low.Positive);
        circuit.Connect(shifter.HighReference, high.Positive);

        circuit.Connect(shifter.Low(0), lowPull.A);
        circuit.Connect(lowPull.B, gnd.Pin);

        circuit.Connect(shifter.High(0), highPull.A);
        circuit.Connect(highPull.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit,
            new SimulationSettings { TimeStep = 1e-6, MaxTimeStep = 5e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();

        return new Rig(sim, shifter, lowPull, highPull);
    }

    /// <summary>Released, each side sits at its own rail. That is what the pull-ups are for.</summary>
    [Fact]
    public void ReleasedEachSideRestsAtItsOwnRail()
    {
        var rig = Build();
        rig.Sim.Run(1e-3);

        Assert.Equal(3.3, rig.Sim.NodeVoltage(rig.Shifter.Low(0)), 0.2);
        Assert.Equal(5.0, rig.Sim.NodeVoltage(rig.Shifter.High(0)), 0.2);
    }

    /// <summary>
    /// Pulling the low side down turns the MOSFET on — gate at the low rail, source dragged to
    /// ground — and the high side follows it down.
    /// </summary>
    [Fact]
    public void PullingTheLowSideDownTakesTheHighSideWithIt()
    {
        var rig = Build();

        rig.LowPull.IsClosed = true;
        rig.Sim.Run(1e-3);

        Assert.True(rig.Sim.NodeVoltage(rig.Shifter.Low(0)) < 0.5);
        Assert.True(rig.Sim.NodeVoltage(rig.Shifter.High(0)) < 1.0,
            "the high side should have been pulled down through the channel");
    }

    /// <summary>
    /// The direction that looks impossible. The transistor is the wrong way round to conduct, but
    /// its body diode is not: the diode pulls the low side down far enough for the MOSFET to turn
    /// on properly and finish the job.
    /// </summary>
    [Fact]
    public void PullingTheHighSideDownTakesTheLowSideWithIt()
    {
        var rig = Build();

        rig.HighPull.IsClosed = true;
        rig.Sim.Run(1e-3);

        Assert.True(rig.Sim.NodeVoltage(rig.Shifter.High(0)) < 0.5);
        Assert.True(rig.Sim.NodeVoltage(rig.Shifter.Low(0)) < 1.0,
            "the body diode should have carried the low side down");
    }

    /// <summary>Channels are independent — pulling one does not disturb the next.</summary>
    [Fact]
    public void OneChannelDoesNotDisturbAnother()
    {
        var rig = Build();

        rig.LowPull.IsClosed = true;
        rig.Sim.Run(1e-3);

        Assert.Equal(3.3, rig.Sim.NodeVoltage(rig.Shifter.Low(1)), 0.2);
        Assert.Equal(5.0, rig.Sim.NodeVoltage(rig.Shifter.High(1)), 0.2);
    }

    /// <summary>
    /// The gates are tied to LV, so without that rail nothing conducts at all — which is the
    /// failure people hit when they power the board from one side only.
    /// </summary>
    [Fact]
    public void WithoutTheLowRailNothingPassesAndItSaysSo()
    {
        var rig = Build(lowRail: 0.0);

        rig.HighPull.IsClosed = true;
        rig.Sim.Run(1e-3);

        Assert.NotEmpty(rig.Shifter.Violations);
        Assert.Contains("LV", rig.Shifter.Violations[0]);
    }

    [Fact]
    public void WithoutTheHighRailItSaysSoToo()
    {
        var rig = Build(highRail: 0.0);
        rig.Sim.Run(1e-3);

        Assert.NotEmpty(rig.Shifter.Violations);
        Assert.Contains("HV", rig.Shifter.Violations[0]);
    }

    /// <summary>The rails the wrong way round is a real mistake, and a silent one without this.</summary>
    [Fact]
    public void RailsTheWrongWayRoundAreReported()
    {
        var rig = Build(lowRail: 5.0, highRail: 3.3);
        rig.Sim.Run(1e-3);

        Assert.NotEmpty(rig.Shifter.Violations);
        Assert.Contains("wrong way round", rig.Shifter.Violations[0]);
    }

    /// <summary>An unbuilt circuit is not a fault; silence until something is powered.</summary>
    [Fact]
    public void AnUnpoweredShifterComplainsAboutNothing()
    {
        var rig = Build(lowRail: 0.0, highRail: 0.0);
        rig.Sim.Run(1e-3);

        Assert.Empty(rig.Shifter.Violations);
    }
}
