using Cirq.Components.Electromechanical;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The reed switch and the PIR sensor: the two parts here whose interesting behaviour is in the
/// time domain rather than in a voltage.
/// </summary>
public class ReedSwitchTests
{
    private sealed record Rig(CircuitSimulator Sim, ReedSwitch Switch, Terminal Out);

    private static Rig Build(double step = 20e-6)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());
        var pullUp = circuit.Add(new Resistor(10e3));
        var reed = circuit.Add(new ReedSwitch());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(pullUp.A, rail.Positive);
        circuit.Connect(pullUp.B, reed.A);
        circuit.Connect(reed.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit,
            new SimulationSettings { TimeStep = step, MaxTimeStep = step });

        sim.Reset();
        sim.SolveOperatingPoint();

        return new Rig(sim, reed, reed.A);
    }

    /// <summary>It needs no supply at all, which is the point of it: it is a contact.</summary>
    [Fact]
    public void AMagnetClosesItAndTakingItAwayOpensIt()
    {
        var rig = Build();
        Assert.Equal(5.0, rig.Sim.NodeVoltage(rig.Out), 0.05);

        rig.Switch.FluxDensity = 12.0;
        rig.Sim.Run(5e-3);
        Assert.True(rig.Sim.NodeVoltage(rig.Out) < 0.5, "a magnet should close it");

        rig.Switch.FluxDensity = 0.0;
        rig.Sim.Run(5e-3);
        Assert.Equal(5.0, rig.Sim.NodeVoltage(rig.Out), 0.05);
    }

    /// <summary>
    /// The blades are ferrous, not magnetised, so they are pulled together whichever way round the
    /// magnet is held. Most Hall switches are not like that, and turning a magnet over is the
    /// half-hour people lose to one.
    /// </summary>
    [Fact]
    public void EitherPoleWorks()
    {
        var rig = Build();

        rig.Switch.FluxDensity = -12.0;
        rig.Sim.Run(5e-3);

        Assert.True(rig.Sim.NodeVoltage(rig.Out) < 0.5, "a reed switch does not care which pole");
    }

    /// <summary>It holds between the two thresholds, as anything with hysteresis does.</summary>
    [Fact]
    public void ItHoldsBetweenTheThresholds()
    {
        var rig = Build();

        rig.Switch.FluxDensity = 12.0;
        rig.Sim.Run(5e-3);

        // Below the operate point but above the release point: it stays where it was.
        rig.Switch.FluxDensity = 6.0;
        rig.Sim.Run(5e-3);
        Assert.True(rig.Switch.IsClosed, "it should still be closed at 6 mT");

        rig.Switch.FluxDensity = 2.0;
        rig.Sim.Run(5e-3);
        Assert.False(rig.Switch.IsClosed, "and open again below the release point");
    }

    /// <summary>
    /// The thing a Hall switch does not do. The blades snap together, spring apart and snap back
    /// several times over the first half millisecond, so a single magnet passing produces a burst
    /// of edges — which is why anything counting them has to debounce.
    /// </summary>
    [Fact]
    public void ClosingItBouncesSeveralTimes()
    {
        var rig = Build(step: 10e-6);

        rig.Switch.FluxDensity = 12.0;

        var edges = 0;
        var wasHigh = true;

        while (rig.Sim.Time < 3e-3)
        {
            rig.Sim.Step();

            var high = rig.Sim.NodeVoltage(rig.Out) > 2.5;
            if (high != wasHigh) edges++;
            wasHigh = high;
        }

        // Five stretches of contact means four changes on the way down, and it ends closed.
        Assert.True(edges >= 4, $"expected a burst of edges from the bounce, got {edges}");
        Assert.False(wasHigh, "and it should have settled closed");
    }

    /// <summary>Opening does not bounce: there is nothing for the blades to rebound off.</summary>
    [Fact]
    public void OpeningDoesNot()
    {
        var rig = Build(step: 10e-6);

        rig.Switch.FluxDensity = 12.0;
        rig.Sim.Run(5e-3);

        rig.Switch.FluxDensity = 0.0;

        var edges = 0;
        var wasHigh = false;

        while (rig.Sim.Time < 10e-3)
        {
            rig.Sim.Step();

            var high = rig.Sim.NodeVoltage(rig.Out) > 2.5;
            if (high != wasHigh) edges++;
            wasHigh = high;
        }

        Assert.Equal(1, edges);
    }

    /// <summary>And it can be turned off, for circuits where the bounce is beside the point.</summary>
    [Fact]
    public void TheBounceCanBeTurnedOff()
    {
        var rig = Build(step: 10e-6);
        rig.Switch.BounceCount = 1;
        rig.Switch.FluxDensity = 12.0;

        var edges = 0;
        var wasHigh = true;

        while (rig.Sim.Time < 3e-3)
        {
            rig.Sim.Step();

            var high = rig.Sim.NodeVoltage(rig.Out) > 2.5;
            if (high != wasHigh) edges++;
            wasHigh = high;
        }

        Assert.Equal(1, edges);
    }
}

/// <summary>
/// The PIR, whose whole character is in what it does <i>after</i> the movement stops.
/// </summary>
public class PirSensorTests
{
    private sealed record Rig(CircuitSimulator Sim, PirSensor Sensor, Terminal Out);

    private static Rig Build(double hold = 5.0, bool retriggerable = true, double warmUp = 30.0)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());
        var load = circuit.Add(new Resistor(100e3));

        var sensor = circuit.Add(new PirSensor
        {
            HoldSeconds = hold, IsRetriggerable = retriggerable, WarmUpSeconds = warmUp,
        });

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(sensor.Vcc, rail.Positive);
        circuit.Connect(sensor.Gnd, gnd.Pin);
        circuit.Connect(sensor.Output, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit,
            new SimulationSettings { TimeStep = 10e-3, MaxTimeStep = 50e-3 });

        sim.Reset();
        sim.SolveOperatingPoint();

        return new Rig(sim, sensor, sensor.Output);
    }

    private static bool IsHigh(Rig rig) => rig.Sim.NodeVoltage(rig.Out) > 2.0;

    /// <summary>
    /// A sensor that appears dead for the first minute. It is warming up, and nothing it says
    /// during that time means anything.
    /// </summary>
    [Fact]
    public void ItIgnoresEverythingWhileItWarmsUp()
    {
        var rig = Build(warmUp: 30.0);

        rig.Sim.Run(5.0);
        rig.Sensor.Movement = true;
        rig.Sim.Run(1.0);

        Assert.True(rig.Sensor.IsWarmingUp);
        Assert.False(IsHigh(rig), "it should not trigger while warming up");
    }

    /// <summary>Past the warm-up, movement takes the output high.</summary>
    [Fact]
    public void MovementTriggersIt()
    {
        var rig = Build(warmUp: 1.0);

        rig.Sim.Run(2.0);
        rig.Sensor.Movement = true;
        rig.Sim.Run(0.5);

        Assert.True(IsHigh(rig), "movement should take the output high");
    }

    /// <summary>
    /// And then it holds. The output is not reporting what is happening now, it is reporting that
    /// something happened recently — which is what anything sampling the pin has to allow for.
    /// </summary>
    [Fact]
    public void ItHoldsLongAfterTheMovementStops()
    {
        var rig = Build(hold: 5.0, warmUp: 1.0);

        rig.Sim.Run(2.0);
        rig.Sensor.Movement = true;
        rig.Sim.Run(0.5);
        rig.Sensor.Movement = false;

        rig.Sim.Run(3.0);
        Assert.True(IsHigh(rig), "it should still be high three seconds after the movement stopped");

        rig.Sim.Run(3.0);
        Assert.False(IsHigh(rig), "and low once the hold has run out");
    }

    /// <summary>
    /// The jumper, and the light that goes out while you are standing under it. Retriggerable
    /// restarts the hold on each fresh movement; single-shot does not.
    /// </summary>
    [Fact]
    public void RetriggerableRestartsTheHoldAndSingleShotDoesNot()
    {
        static bool StillOnAfterASecondMovement(bool retriggerable)
        {
            var rig = Build(hold: 5.0, retriggerable: retriggerable, warmUp: 1.0);

            rig.Sim.Run(2.0);

            // Move, then move again four seconds later — before the first hold runs out.
            rig.Sensor.Movement = true;
            rig.Sim.Run(0.2);
            rig.Sensor.Movement = false;
            rig.Sim.Run(3.8);
            rig.Sensor.Movement = true;
            rig.Sim.Run(0.2);
            rig.Sensor.Movement = false;

            // Two seconds past where the first hold would have ended.
            rig.Sim.Run(3.0);

            return IsHigh(rig);
        }

        Assert.True(StillOnAfterASecondMovement(true), "retriggerable should have restarted the hold");
        Assert.False(StillOnAfterASecondMovement(false), "single-shot should have given up");
    }

    /// <summary>Push-pull, not open collector — no pull-up needed, unlike the Hall switch.</summary>
    [Fact]
    public void TheOutputDrivesBothWays()
    {
        var rig = Build(warmUp: 1.0);

        rig.Sim.Run(2.0);
        Assert.Equal(LogicState.Low, rig.Sensor.GetOutputState(0));
        Assert.True(rig.Sim.NodeVoltage(rig.Out) < 0.5, "a released output would float here");
    }
}
