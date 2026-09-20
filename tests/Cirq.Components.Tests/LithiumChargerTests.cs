using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// Charging a lithium cell is a procedure, not a connection, and these check that the procedure
/// is the one in the datasheet: current first, then voltage, then stop.
/// </summary>
public class LithiumChargerTests
{
    private sealed record Rig(
        CircuitSimulator Sim,
        LithiumCharger Charger,
        DcVoltageSource Supply,
        DcVoltageSource Cell,
        Resistor ChargeLed,
        Resistor DoneLed);

    /// <summary>
    /// A voltage source stands in for the cell, so its voltage can be set to whatever state of
    /// charge is under test rather than waiting hours to get there.
    /// </summary>
    private static Rig Build(double input = 5.0, double cell = 3.5, double current = 1.0)
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(input));
        var battery = circuit.Add(new DcVoltageSource(cell));
        var charger = circuit.Add(new LithiumCharger { ChargeCurrent = current });

        var chargeLed = circuit.Add(new Resistor(1e3));
        var doneLed = circuit.Add(new Resistor(1e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(battery.Negative, gnd.Pin);

        circuit.Connect(charger.Input, supply.Positive);
        circuit.Connect(charger.Battery, battery.Positive);
        circuit.Connect(charger.Gnd, gnd.Pin);

        // The two indicator LEDs, as resistors up to the input rail.
        circuit.Connect(supply.Positive, chargeLed.A);
        circuit.Connect(chargeLed.B, charger.Charging);
        circuit.Connect(supply.Positive, doneLed.A);
        circuit.Connect(doneLed.B, charger.Standby);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-4 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1e-3);

        return new Rig(sim, charger, supply, battery, chargeLed, doneLed);
    }

    /// <summary>
    /// Most of a charge is constant current: a flat push in, whatever the cell's voltage happens
    /// to be doing.
    /// </summary>
    [Theory]
    [InlineData(3.0)]
    [InlineData(3.5)]
    [InlineData(3.9)]
    public void WellBelowFullItPushesAConstantCurrent(double cell)
    {
        var rig = Build(cell: cell, current: 0.5);

        Assert.Equal(ChargePhase.ConstantCurrent, rig.Charger.Phase);
        Assert.Equal(0.5, rig.Charger.Current, 0.02);
    }

    /// <summary>
    /// As the cell reaches its float voltage the current falls away. The charger is not choosing
    /// to reduce it — it is holding 4.2 V and the cell is taking less.
    /// </summary>
    [Fact]
    public void ApproachingFullTheCurrentTapersOff()
    {
        var full = Build(cell: 4.19, current: 1.0);
        var nearly = Build(cell: 4.15, current: 1.0);
        var low = Build(cell: 3.6, current: 1.0);

        Assert.True(full.Charger.Current < nearly.Charger.Current);
        Assert.True(nearly.Charger.Current < low.Charger.Current);
        Assert.Equal(ChargePhase.ConstantVoltage, full.Charger.Phase);
    }

    /// <summary>At the float voltage there is nothing left to push in.</summary>
    [Fact]
    public void AtTheFloatVoltageItStops()
    {
        var rig = Build(cell: 4.2, current: 1.0);

        Assert.True(rig.Charger.Current < 0.01);
    }

    /// <summary>
    /// A linear charger cannot lift a voltage, only drop one. With no headroom above the cell
    /// nothing happens at all, which is why a sagging USB supply stops charging.
    /// </summary>
    [Fact]
    public void WithoutHeadroomAboveTheCellNothingHappens()
    {
        var rig = Build(input: 3.4, cell: 3.5);

        Assert.Equal(ChargePhase.Idle, rig.Charger.Phase);
        Assert.Equal(0.0, rig.Charger.Current, 1e-6);
    }

    /// <summary>The charging light is on while it charges, and the done light is not.</summary>
    [Fact]
    public void TheChargingLightIsOnWhileItCharges()
    {
        var rig = Build(cell: 3.5);

        Assert.Equal(ChargePhase.ConstantCurrent, rig.Charger.Phase);

        // Pulled down means the LED has a voltage across it.
        Assert.True(rig.Sim.NodeVoltage(rig.Charger.Charging) < 1.0);
        Assert.True(rig.Sim.NodeVoltage(rig.Charger.Standby) > 4.0);
    }

    /// <summary>
    /// Everything between the input and the cell is thrown away inside the chip, because a linear
    /// charger has nothing else to do with it. A full amp from five volts into a cell at three and
    /// a half is one and a half watts in a part the size of a grain of rice.
    /// </summary>
    [Fact]
    public void TheHeatIsWhatTheInputDoesNotPutInTheCell()
    {
        var rig = Build(input: 5.0, cell: 3.5, current: 1.0);

        Assert.Equal(1.5, rig.Charger.Dissipation, 0.05);
        Assert.NotEmpty(rig.Charger.Violations);
        Assert.Contains("linear charger", rig.Charger.Violations[0]);
    }

    /// <summary>Charge gently from a lower supply and there is nothing to complain about.</summary>
    [Fact]
    public void AGentlerChargeRunsCool()
    {
        var rig = Build(input: 4.5, cell: 3.9, current: 0.2);

        Assert.True(rig.Charger.Dissipation < 0.2);
        Assert.Empty(rig.Charger.Violations);
    }

    /// <summary>
    /// A real cell being charged: current in, voltage rising, and the charger handing over from
    /// one phase to the next on its own.
    /// </summary>
    [Fact]
    public void ARealCellGoesThroughBothPhases()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(5.0));
        var charger = circuit.Add(new LithiumCharger { ChargeCurrent = 2.0 });

        // A small capacitor stands in for the cell so the whole charge fits in a test.
        var cell = circuit.Add(new Capacitor(1e-3) { InitialVoltage = 3.6 });
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(charger.Input, supply.Positive);
        circuit.Connect(charger.Gnd, gnd.Pin);
        circuit.Connect(charger.Battery, cell.A);
        circuit.Connect(cell.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-6, MaxTimeStep = 5e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();

        sim.Run(1e-4);
        Assert.Equal(ChargePhase.ConstantCurrent, charger.Phase);

        var seenConstantVoltage = false;
        for (var i = 0; i < 400; i++)
        {
            sim.Run(5e-6);
            if (charger.Phase == ChargePhase.ConstantVoltage) seenConstantVoltage = true;
        }

        Assert.True(seenConstantVoltage, "it should have handed over to constant voltage");

        // And it never pushes the cell past the float voltage, which is the point of the whole
        // arrangement.
        Assert.True(charger.CellVoltage <= charger.FloatVoltage + 0.02,
            $"{charger.CellVoltage:0.000} V is past the float voltage");
    }

    /// <summary>
    /// Once it decides the cell is full it latches off rather than restarting the moment the
    /// voltage sags, which is what stops a charger cycling a cell to death.
    /// </summary>
    [Fact]
    public void TerminationLatchesRatherThanCycling()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(5.0));
        var charger = circuit.Add(new LithiumCharger { ChargeCurrent = 2.0 });
        var cell = circuit.Add(new Capacitor(1e-4) { InitialVoltage = 4.15 });
        var bleed = circuit.Add(new Resistor(1e4));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(charger.Input, supply.Positive);
        circuit.Connect(charger.Gnd, gnd.Pin);
        circuit.Connect(charger.Battery, cell.A);
        circuit.Connect(cell.B, gnd.Pin);
        circuit.Connect(charger.Battery, bleed.A);
        circuit.Connect(bleed.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-6, MaxTimeStep = 5e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();

        for (var i = 0; i < 2000 && charger.Phase != ChargePhase.Complete; i++)
            sim.Run(5e-6);

        Assert.Equal(ChargePhase.Complete, charger.Phase);

        // Let the bleed resistor pull the cell back down; it must not start again.
        sim.Run(5e-3);

        Assert.Equal(ChargePhase.Complete, charger.Phase);
        Assert.True(charger.Current < 1e-6);
    }
}
