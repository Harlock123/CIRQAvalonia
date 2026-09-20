using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Components.Electromechanical;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The gate driver, and the arithmetic behind it: a gate is a capacitor, so the time to switch is
/// charge over current, and the whole part exists because a logic pin has not got the current.
/// </summary>
public class GateDriverTests
{
    private sealed record Rig(CircuitSimulator Sim, Terminal Gate);

    /// <summary>
    /// A logic source charging a gate capacitance, either straight from its pin or through a
    /// driver. Returns how long the gate took to get to ten volts, or the run time if it never did.
    /// </summary>
    private static double RiseTime(bool throughDriver, double target, double gateCapacitance = 47e-9, double supply = 12.0)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(supply));
        var gnd = circuit.Add(new Ground());
        // A microcontroller pin: 3.3 V behind the couple of hundred ohms that twenty milliamps
        // into that rail works out to.
        var logic = circuit.Add(new LogicToggle(false)
        {
            Levels = LogicLevels.Cmos33V with { OutputResistance = 165.0 },
        });
        var gate = circuit.Add(new Capacitor(gateCapacitance));

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(gate.B, gnd.Pin);

        if (throughDriver)
        {
            var driver = circuit.Add(new GateDriver());

            circuit.Connect(driver.Vcc, rail.Positive);
            circuit.Connect(driver.Gnd, gnd.Pin);
            circuit.Connect(logic.Out, driver.Input);
            circuit.Connect(driver.Output, gate.A);
        }
        else
        {
            // Straight from the pin, as people do the first time.
            circuit.Connect(logic.Out, gate.A);
        }

        var sim = new CircuitSimulator(circuit,
            new SimulationSettings { TimeStep = 2e-9, MaxTimeStep = 5e-9 });

        sim.Reset();
        sim.SolveOperatingPoint();

        logic.State = true;

        const double limit = 200e-6;

        while (sim.Time < limit)
        {
            sim.Step();
            if (sim.NodeVoltage(gate.A) >= target) return sim.Time;
        }

        return limit;
    }

    /// <summary>
    /// The number that makes the part necessary. A gate is tens of nanofarads; taking 47 nF to
    /// 10 V at the two amps a driver can push takes a couple of hundred nanoseconds, where the
    /// twenty milliamps a logic pin manages would take a couple of hundred microseconds.
    /// </summary>
    [Fact]
    public void ADriverSwitchesAGateOrdersOfMagnitudeFaster()
    {
        var driven = RiseTime(throughDriver: true, target: 3.0);
        var direct = RiseTime(throughDriver: false, target: 3.0);

        Assert.True(direct > driven * 20,
            $"straight from a pin took {direct * 1e6:0.0} µs against the driver's {driven * 1e9:0} ns");
    }

    /// <summary>
    /// Q = CV. Taking 47 nF to ten volts is 470 nanocoulombs, and two amps moves that in 235 ns —
    /// which is the arithmetic the whole part exists to satisfy.
    /// </summary>
    [Fact]
    public void ItMovesTheGateChargeInChargeOverCurrent()
    {
        var driven = RiseTime(throughDriver: true, target: 10.0);

        // A little longer than 235 ns, because the last volts are charged through a falling
        // voltage difference rather than at the full peak current.
        Assert.InRange(driven, 200e-9, 900e-9);
    }

    /// <summary>
    /// And the part a faster pin could not fix. A logic pin cannot take the gate above its own
    /// rail at all, so a FET whose on-resistance is quoted at ten volts never gets there — it sits
    /// half on, which is the expensive place to be.
    /// </summary>
    [Fact]
    public void ALogicPinCannotReachTheGateVoltageAtAll()
    {
        var direct = RiseTime(throughDriver: false, target: 10.0);

        Assert.Equal(200e-6, direct, 1e-9);
    }

    /// <summary>
    /// And the other half of what it is for: the gate goes to the <b>driver's</b> supply, not the
    /// logic rail. A FET whose on-resistance is specified at 10 V does not get there on 3.3.
    /// </summary>
    [Fact]
    public void TheGateGoesToTheDriversRailNotTheLogicRail()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(12.0));
        var gnd = circuit.Add(new Ground());
        var logic = circuit.Add(new LogicToggle(true) { Levels = LogicLevels.Cmos33V });
        var driver = circuit.Add(new GateDriver());
        var load = circuit.Add(new Resistor(100e3));

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(driver.Vcc, rail.Positive);
        circuit.Connect(driver.Gnd, gnd.Pin);
        circuit.Connect(logic.Out, driver.Input);
        circuit.Connect(driver.Output, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1e-6);

        Assert.Equal(12.0, sim.NodeVoltage(driver.Output), 0.1);
    }

    /// <summary>
    /// Real drivers sink harder than they source, because getting a FET off in a hurry is what
    /// keeps a half-bridge from shooting through.
    /// </summary>
    [Fact]
    public void ItPullsDownHarderThanItPullsUp()
    {
        var driver = new GateDriver { PeakSourceCurrent = 2.0, PeakSinkCurrent = 3.0 };

        Assert.True(driver.SinkResistance < driver.SourceResistance,
            "the turn-off path should be the stiffer one");
    }

    /// <summary>The resistance behind the pin is just the supply over the rated peak current.</summary>
    [Fact]
    public void TheDriveStrengthIsTheSupplyOverThePeakCurrent()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(12.0));
        var gnd = circuit.Add(new Ground());
        var driver = circuit.Add(new GateDriver { PeakSourceCurrent = 2.0 });
        var load = circuit.Add(new Resistor(100e3));

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(driver.Vcc, rail.Positive);
        circuit.Connect(driver.Gnd, gnd.Pin);
        circuit.Connect(driver.Output, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        Assert.Equal(6.0, driver.SourceResistance, 0.1);
    }

    /// <summary>
    /// Run from too little supply it will not take a gate high enough to turn a FET properly on,
    /// and a half-on FET is exactly where the heat is. The part says so rather than pretending.
    /// </summary>
    [Fact]
    public void ItComplainsBelowItsLockoutVoltage()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());
        var driver = circuit.Add(new GateDriver { UnderVoltageLockout = 8.0 });
        var load = circuit.Add(new Resistor(100e3));

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(driver.Vcc, rail.Positive);
        circuit.Connect(driver.Gnd, gnd.Pin);
        circuit.Connect(driver.Output, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1e-6);

        Assert.True(driver.IsLockedOut);
        Assert.NotEmpty(driver.Violations);
        Assert.Equal(0.0, sim.NodeVoltage(driver.Output), 0.1);
    }

    /// <summary>A floating input holds the gate off rather than guessing.</summary>
    [Fact]
    public void AFloatingInputLeavesTheGateOff()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(12.0));
        var gnd = circuit.Add(new Ground());
        var driver = circuit.Add(new GateDriver());
        var divider = circuit.Add(new Resistor(1e3));
        var load = circuit.Add(new Resistor(100e3));

        // Held halfway up the logic range: neither a one nor a zero.
        var mid = circuit.Add(new DcVoltageSource(1.6));

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(mid.Negative, gnd.Pin);
        circuit.Connect(driver.Vcc, rail.Positive);
        circuit.Connect(driver.Gnd, gnd.Pin);
        circuit.Connect(mid.Positive, divider.A);
        circuit.Connect(divider.B, driver.Input);
        circuit.Connect(driver.Output, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1e-6);

        Assert.Equal(0.0, sim.NodeVoltage(driver.Output), 0.1);
    }
}
