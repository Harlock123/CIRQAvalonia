using Cirq.Components.Buses;
using Cirq.Components.Electromechanical;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// A phototransistor is a current source commanded by light, not a resistance — which is the
/// whole difference from the LDR beside it, and what these check.
/// </summary>
public class PhototransistorTests
{
    private static (CircuitSimulator Sim, Phototransistor Part, Resistor Load) Rig(
        double illuminance, double load = 10e3, double supply = 5.0)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(supply));
        var part = circuit.Add(new Phototransistor { Illuminance = illuminance });
        var resistor = circuit.Add(new Resistor(load));
        var gnd = circuit.Add(new Ground());

        // Collector up through the load, emitter to ground: the usual way round.
        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, resistor.A);
        circuit.Connect(resistor.B, part.Collector);
        circuit.Connect(part.Emitter, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, part, resistor);
    }

    /// <summary>
    /// The current follows the light and, until it runs out of voltage, does not care what it is
    /// wired to. That is what makes it a current source rather than a resistor.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(400)]
    public void TheCurrentFollowsTheLight(double lux)
    {
        var (sim, part, _) = Rig(lux, load: 1e3);

        sim.Run(1e-3);

        // A microamp per lux, and the dark current on top.
        Assert.Equal((lux * 1e-6) + 100e-9, part.CollectorCurrent, lux * 1e-6 * 0.05);
    }

    /// <summary>Doubling the light doubles the current, which a resistance would not do.</summary>
    [Fact]
    public void TwiceTheLightIsTwiceTheCurrent()
    {
        var (dimSim, dim, _) = Rig(200, load: 1e3);
        var (brightSim, bright, _) = Rig(400, load: 1e3);

        dimSim.Run(1e-3);
        brightSim.Run(1e-3);

        Assert.Equal(2.0, bright.CollectorCurrent / dim.CollectorCurrent, 0.05);
    }

    /// <summary>
    /// The same light through a bigger load makes a bigger voltage — up to the point where there
    /// is no voltage left, and then the output stops following the light at all.
    /// </summary>
    [Fact]
    public void TooLargeALoadSaturatesItInOrdinaryLight()
    {
        var (sim, part, load) = Rig(500, load: 1e6);

        sim.Run(1e-3);

        Assert.True(part.IsSaturated, "half a milliamp through a megohm cannot fit in five volts");
        Assert.True(part.CollectorVoltage < 0.5);

        // It is the load deciding the current now, not the light.
        Assert.True(part.CollectorCurrent < part.PhotoCurrent * 0.8);
    }

    /// <summary>In the dark it passes its dark current and no more.</summary>
    [Fact]
    public void InTheDarkItPassesAlmostNothing()
    {
        var (sim, part, _) = Rig(0, load: 10e3);

        sim.Run(1e-3);

        Assert.Equal(100e-9, part.CollectorCurrent, 20e-9);
        Assert.True(part.CollectorVoltage > 4.9, "the load drop should be microvolts");
    }

    /// <summary>Double-clicking shades it, which is how you work one on the canvas.</summary>
    [Fact]
    public void ItCanBeLitAndShaded()
    {
        var part = new Phototransistor { Illuminance = 300, AlternateIlluminance = 5 };

        part.Interact();
        Assert.Equal(5, part.Illuminance);

        part.Interact();
        Assert.Equal(300, part.Illuminance);
    }
}

/// <summary>
/// The two things that catch people with a Hall switch: the output is open collector, and it has
/// hysteresis on purpose.
/// </summary>
public class HallSensorTests
{
    private static (CircuitSimulator Sim, HallSensor Part, Resistor PullUp) Rig(
        double flux, bool withPullUp = true)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var part = circuit.Add(new HallSensor { FluxDensity = flux });
        var pullUp = circuit.Add(new Resistor(10e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(part.Vcc, rail.Positive);
        circuit.Connect(part.Gnd, gnd.Pin);

        if (withPullUp)
        {
            circuit.Connect(rail.Positive, pullUp.A);
            circuit.Connect(pullUp.B, part.Output);
        }
        else
        {
            // Something has to be on the pin or the node floats; a meter's worth of load stands
            // in for whatever is listening, and pulls nothing up.
            circuit.Connect(part.Output, pullUp.A);
            circuit.Connect(pullUp.B, gnd.Pin);
        }

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, part, pullUp);
    }

    /// <summary>A magnet close enough pulls the output down.</summary>
    [Fact]
    public void AStrongEnoughFieldPullsTheOutputDown()
    {
        var (sim, part, _) = Rig(35);

        sim.Run(1e-4);

        Assert.True(part.IsDetecting);
        Assert.True(sim.NodeVoltage(part.Output) < 1.0);
    }

    /// <summary>With no magnet the pull-up has the pin, and the output reads high.</summary>
    [Fact]
    public void WithNoFieldThePullUpHasIt()
    {
        var (sim, part, _) = Rig(0);

        sim.Run(1e-4);

        Assert.False(part.IsDetecting);
        Assert.True(sim.NodeVoltage(part.Output) > 4.0);
    }

    /// <summary>
    /// The hysteresis. Between the two thresholds it holds whatever it was doing, so the answer
    /// depends on which way the magnet was moving — which is the entire reason a wheel magnet
    /// gives one pulse rather than a burst.
    /// </summary>
    [Fact]
    public void BetweenTheThresholdsItRemembersWhatItWasDoing()
    {
        var (sim, part, _) = Rig(0);

        // Up past the operate point, then back to the middle: still on.
        part.FluxDensity = 30;
        sim.Run(1e-4);
        Assert.True(part.IsDetecting);

        part.FluxDensity = 15;
        sim.Run(1e-4);
        Assert.True(part.IsDetecting, "fifteen is below operate but above release, so it should hold on");

        // Down past release, then back to the middle: still off.
        part.FluxDensity = 5;
        sim.Run(1e-4);
        Assert.False(part.IsDetecting);

        part.FluxDensity = 15;
        sim.Run(1e-4);
        Assert.False(part.IsDetecting, "the same field should now read the other way round");
    }

    /// <summary>A unipolar part ignores the other pole entirely.</summary>
    [Fact]
    public void TheWrongPoleDoesNothingToAUnipolarPart()
    {
        var (sim, part, _) = Rig(-40);

        sim.Run(1e-4);

        Assert.False(part.IsDetecting);
    }

    /// <summary>Told it is omnipolar, it answers to either.</summary>
    [Fact]
    public void AnOmnipolarPartAnswersToEitherPole()
    {
        var (sim, part, _) = Rig(-40);
        part.IsUnipolar = false;

        sim.Run(1e-4);

        Assert.True(part.IsDetecting);
    }

    /// <summary>
    /// Open collector with nothing pulling it up can only ever read low, which is the usual
    /// reason one of these looks dead.
    /// </summary>
    [Fact]
    public void WithoutAPullUpItIsReportedRatherThanSilentlyUseless()
    {
        var (sim, part, _) = Rig(0, withPullUp: false);

        sim.Run(1e-4);

        Assert.True(part.HasNoPullUp);
        Assert.NotEmpty(part.Violations);
        Assert.Contains("pulling it up", part.Violations[0]);
    }

    /// <summary>Unpowered it releases the pin rather than holding it.</summary>
    [Fact]
    public void UnpoweredItLetsGo()
    {
        var circuit = new Circuit();
        var dead = circuit.Add(new DcVoltageSource(0.0));
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var part = circuit.Add(new HallSensor { FluxDensity = 50 });
        var pullUp = circuit.Add(new Resistor(10e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(dead.Negative, gnd.Pin);
        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(part.Vcc, dead.Positive);
        circuit.Connect(part.Gnd, gnd.Pin);
        circuit.Connect(rail.Positive, pullUp.A);
        circuit.Connect(pullUp.B, part.Output);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1e-4);

        Assert.False(part.IsDetecting);
        Assert.True(sim.NodeVoltage(part.Output) > 4.0);
    }
}
