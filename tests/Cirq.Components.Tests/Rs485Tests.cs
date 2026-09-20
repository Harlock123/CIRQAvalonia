using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// Two transceivers on a pair, which is the only arrangement that means anything: a driver with
/// nothing listening proves nothing, and a receiver with nothing driving is the fault this part
/// exists to show.
/// </summary>
public class Rs485Tests
{
    private sealed record Rig(
        CircuitSimulator Sim,
        Rs485Transceiver Near,
        Rs485Transceiver Far,
        LogicToggle Data,
        LogicToggle Enable,
        Circuit Circuit);

    private static Rig Build(bool failSafe = true, double biasOhms = 0, double terminationOhms = 0)
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());
        var supply = circuit.Add(new DcVoltageSource(5.0));

        var data = circuit.Add(new LogicToggle(true) { Levels = LogicLevels.Cmos5V });
        var enable = circuit.Add(new LogicToggle(true) { Levels = LogicLevels.Cmos5V });

        var near = circuit.Add(new Rs485Transceiver { HasFailSafe = failSafe });
        var far = circuit.Add(new Rs485Transceiver { HasFailSafe = failSafe });

        circuit.Connect(supply.Negative, gnd.Pin);

        foreach (var t in new[] { near, far })
        {
            circuit.Connect(t.Vcc, supply.Positive);
            circuit.Connect(t.Gnd, gnd.Pin);

            // Receivers always listening: RE is active low.
            circuit.Connect(t.ReceiverEnable, gnd.Pin);
        }

        // The near end talks, the far end listens.
        circuit.Connect(near.DriverEnable, enable.Out);
        circuit.Connect(near.DriverIn, data.Out);
        circuit.Connect(far.DriverEnable, gnd.Pin);
        circuit.Connect(far.DriverIn, gnd.Pin);

        circuit.Connect(near.A, far.A);
        circuit.Connect(near.B, far.B);

        if (terminationOhms > 0)
        {
            var terminator = circuit.Add(new Resistor(terminationOhms));
            circuit.Connect(terminator.A, far.A);
            circuit.Connect(terminator.B, far.B);
        }

        if (biasOhms > 0)
        {
            // Failsafe biasing: A pulled up, B pulled down, so a quiet bus sits a couple of
            // hundred millivolts apart instead of floating.
            var pullUp = circuit.Add(new Resistor(biasOhms));
            var pullDown = circuit.Add(new Resistor(biasOhms));

            circuit.Connect(pullUp.A, supply.Positive);
            circuit.Connect(pullUp.B, near.A);
            circuit.Connect(pullDown.A, near.B);
            circuit.Connect(pullDown.B, gnd.Pin);
        }

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-7 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1e-5);

        return new Rig(sim, near, far, data, enable, circuit);
    }

    /// <summary>
    /// A one on the driver's input arrives as a one at the far end, having crossed as a
    /// difference between two wires rather than as a voltage against ground.
    /// </summary>
    [Fact]
    public void ABitGetsToTheFarEnd()
    {
        var rig = Build(terminationOhms: 120);

        Assert.True(rig.Near.IsDriving);
        Assert.True(rig.Near.Difference > 1.0, $"A should be above B, not by {rig.Near.Difference:0.00} V");
        Assert.Equal(LogicState.High, rig.Far.GetOutputState(0));

        rig.Data.State = false;
        rig.Sim.Run(1e-5);

        Assert.True(rig.Near.Difference < -1.0, $"and below it, not by {rig.Near.Difference:0.00} V");
        Assert.Equal(LogicState.Low, rig.Far.GetOutputState(0));
    }

    /// <summary>
    /// The standard's whole point: the bit is carried by the difference between the wires, so
    /// lifting <i>both</i> of them — which is what a ground offset between the two ends, or
    /// common-mode noise picked up along the cable, actually does — changes nothing.
    /// <para>
    /// The offset is injected into the pair rather than by lifting a ground pin, because every
    /// ground terminal in a netlist here is the same net by construction. Electrically it is the
    /// same thing: the far end sees both wires several volts from where it thinks ground is.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(4.0)]
    [InlineData(-5.0)]
    public void ACommonModeOffsetOnThePairChangesNothing(double offset)
    {
        var rig = BuildWithOffset(offset);

        Assert.False(rig.Far.IsOutOfCommonModeRange);
        Assert.Equal(LogicState.High, rig.Far.GetOutputState(0));

        rig.Data.State = false;
        rig.Sim.Run(1e-5);

        Assert.Equal(LogicState.Low, rig.Far.GetOutputState(0));
    }

    /// <summary>
    /// Until it does. Past the common-mode window the receiver stops being a differential
    /// amplifier, and a perfectly good difference between the wires decides nothing — which is
    /// what a long run between two buildings fails like, and why isolation exists.
    /// </summary>
    [Fact]
    public void ButNotOnceItLeavesTheCommonModeWindow()
    {
        var rig = BuildWithOffset(16.0);

        Assert.True(rig.Far.IsOutOfCommonModeRange);
        Assert.NotEmpty(rig.Far.Violations);

        // The wires are still the right distance apart. It makes no difference.
        Assert.True(rig.Far.Difference > 1.0);
        Assert.Equal(LogicState.Unknown, rig.Far.GetOutputState(0));
    }

    /// <summary>The same rig, with a source in each wire to shift the whole pair.</summary>
    private static Rig BuildWithOffset(double offset)
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());
        var supply = circuit.Add(new DcVoltageSource(5.0));

        var data = circuit.Add(new LogicToggle(true) { Levels = LogicLevels.Cmos5V });
        var enable = circuit.Add(new LogicToggle(true) { Levels = LogicLevels.Cmos5V });

        var near = circuit.Add(new Rs485Transceiver());
        var far = circuit.Add(new Rs485Transceiver());
        var terminator = circuit.Add(new Resistor(120));

        // Equal sources in both wires: whatever they add lands on the difference as nothing at
        // all, and on the common mode as all of it.
        var shiftA = circuit.Add(new DcVoltageSource(offset));
        var shiftB = circuit.Add(new DcVoltageSource(offset));

        circuit.Connect(supply.Negative, gnd.Pin);

        foreach (var t in new[] { near, far })
        {
            circuit.Connect(t.Vcc, supply.Positive);
            circuit.Connect(t.Gnd, gnd.Pin);
            circuit.Connect(t.ReceiverEnable, gnd.Pin);
        }

        circuit.Connect(near.DriverEnable, enable.Out);
        circuit.Connect(near.DriverIn, data.Out);
        circuit.Connect(far.DriverEnable, gnd.Pin);
        circuit.Connect(far.DriverIn, gnd.Pin);

        circuit.Connect(near.A, shiftA.Negative);
        circuit.Connect(shiftA.Positive, far.A);
        circuit.Connect(near.B, shiftB.Negative);
        circuit.Connect(shiftB.Positive, far.B);

        circuit.Connect(terminator.A, far.A);
        circuit.Connect(terminator.B, far.B);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-7 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1e-5);

        return new Rig(sim, near, far, data, enable, circuit);
    }

    /// <summary>
    /// Half duplex: releasing the driver lets go of the pair entirely rather than driving it low.
    /// Nothing else is on the bus, so it floats.
    /// </summary>
    [Fact]
    public void ReleasingTheDriverLetsGoOfTheBus()
    {
        var rig = Build(terminationOhms: 120);

        rig.Enable.State = false;
        rig.Sim.Run(1e-5);

        Assert.False(rig.Near.IsDriving);
        Assert.True(Math.Abs(rig.Near.Difference) < 0.2,
            $"an undriven pair should collapse together, not sit {rig.Near.Difference:0.00} V apart");
    }

    /// <summary>
    /// The classic fault. With the driver released and nothing biasing the pair, a receiver
    /// without failsafe reports the sign of whatever is there — and says so, because the only
    /// other symptom is characters nobody sent.
    /// </summary>
    [Fact]
    public void AnIdleBusWithoutBiasingIsFlaggedRatherThanTrusted()
    {
        var rig = Build(failSafe: false, terminationOhms: 120);

        rig.Enable.State = false;
        rig.Sim.Run(1e-5);

        Assert.True(rig.Far.IsBusIdle);
        Assert.NotEmpty(rig.Far.Violations);
    }

    /// <summary>A failsafe receiver holds its output high instead, which is an idle line to a UART.</summary>
    [Fact]
    public void AFailSafeReceiverIdlesHigh()
    {
        var rig = Build(failSafe: true, terminationOhms: 120);

        rig.Enable.State = false;
        rig.Sim.Run(1e-5);

        Assert.True(rig.Far.IsBusIdle);
        Assert.Empty(rig.Far.Violations);
        Assert.Equal(LogicState.High, rig.Far.GetOutputState(0));
    }

    /// <summary>
    /// And the cure for a receiver that has no failsafe of its own: a pair of bias resistors that
    /// hold the line apart when nothing is driving it. The bus is no longer idle, so there is
    /// nothing left to invent.
    /// </summary>
    [Fact]
    public void BiasResistorsFixItForAReceiverThatCannot()
    {
        var rig = Build(failSafe: false, biasOhms: 560, terminationOhms: 120);

        rig.Enable.State = false;
        rig.Sim.Run(1e-5);

        Assert.False(rig.Far.IsBusIdle);
        Assert.Empty(rig.Far.Violations);
        Assert.Equal(LogicState.High, rig.Far.GetOutputState(0));
    }

    /// <summary>
    /// Each receiver is a unit load across the pair, which is what limits how many can share one
    /// bus — thirty-two of the standard sort before the driver runs out of differential voltage.
    /// </summary>
    [Fact]
    public void TheTerminatorLoadsTheDriverFarMoreThanTheReceiversDo()
    {
        var loaded = Build(terminationOhms: 120);
        var unloaded = Build();

        Assert.True(unloaded.Near.Difference > loaded.Near.Difference,
            "120 ohms across the pair should pull the driver down further than two unit loads");

        Assert.True(loaded.Near.Difference > 1.5,
            $"but it must still make the standard's 1.5 V, not {loaded.Near.Difference:0.00} V");
    }
}
