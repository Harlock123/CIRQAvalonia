using Cirq.Components.Electromechanical;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class RelayTests
{
    /// <summary>Coil across a supply through a switch, with the contact switching a lamp.</summary>
    private static (CircuitSimulator Sim, Relay Relay, ToggleSwitch Drive, Resistor Lamp)
        Rig(double coilVolts, bool withFlyback)
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(coilVolts));
        var drive = circuit.Add(new ToggleSwitch(true));
        var relay = circuit.Add(new Relay());
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, relay.CoilA);
        circuit.Connect(relay.CoilB, drive.A);
        circuit.Connect(drive.B, gnd.Pin);

        if (withFlyback)
        {
            // Cathode to the supply end, anode to the switched end: reverse-biased in normal
            // operation, conducting only on the turn-off spike.
            var flyback = circuit.Add(new Diode(DiodeModel.D1N4148));
            circuit.Connect(flyback.Anode, relay.CoilB);
            circuit.Connect(flyback.Cathode, relay.CoilA);
        }

        // Contact switches a lamp from its own supply.
        var lampSupply = circuit.Add(new DcVoltageSource(12.0));
        var lamp = circuit.Add(new Resistor(100));
        circuit.Connect(lampSupply.Negative, gnd.Pin);
        circuit.Connect(lampSupply.Positive, relay.Common);
        circuit.Connect(relay.NormallyOpen, lamp.A);
        circuit.Connect(lamp.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { UseInitialConditions = true });
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, relay, drive, lamp);
    }

    [Fact]
    public void TheCoilPullsInOnceItsCurrentPassesThePullInThreshold()
    {
        var (sim, relay, _, _) = Rig(5.0, withFlyback: true);

        Assert.False(relay.IsEnergised);

        sim.Run(50e-3);        // L/R = 0.15/70 = 2.1 ms, so this is well settled

        // 5 V across 70 R is 71 mA, comfortably past the 35 mA pull-in.
        Assert.True(relay.IsEnergised);
        Assert.Equal(5.0 / 70.0, Math.Abs(relay.CoilCurrent), 0.005);
    }

    [Fact]
    public void TooLittleCoilVoltageNeverThrowsTheContact()
    {
        // 2 V across 70 R is 29 mA, short of the 35 mA it takes to pull in.
        var (sim, relay, _, _) = Rig(2.0, withFlyback: true);

        sim.Run(50e-3);

        Assert.False(relay.IsEnergised);
    }

    [Fact]
    public void TheContactActuallySwitchesTheLoad()
    {
        var (sim, relay, drive, lamp) = Rig(5.0, withFlyback: true);

        sim.Run(50e-3);
        Assert.True(relay.IsEnergised);
        Assert.True(sim.NodeVoltage(lamp.A) > 11.0, "the lamp should be fed through the closed contact");

        drive.IsClosed = false;
        sim.Run(50e-3);

        Assert.False(relay.IsEnergised);
        Assert.True(sim.NodeVoltage(lamp.A) < 0.5, "the lamp should be isolated once the relay drops out");
    }

    /// <summary>
    /// Pull-in and drop-out are different currents on purpose, so a coil sitting near the
    /// threshold does not chatter. Between the two the relay holds whatever state it is in.
    /// </summary>
    [Fact]
    public void ItHoldsItsStateBetweenPullInAndDropOut()
    {
        // 1.75 V across 70 R is 25 mA: below pull-in, above drop-out.
        var (sim, relay, _, _) = Rig(1.75, withFlyback: true);
        sim.Run(50e-3);
        Assert.False(relay.IsEnergised);          // never pulled in from rest

        var (sim2, relay2, _, _) = Rig(5.0, withFlyback: true);
        sim2.Run(50e-3);
        Assert.True(relay2.IsEnergised);
    }

    /// <summary>
    /// The headline. Interrupting an inductive coil with nowhere for the current to go produces
    /// hundreds of volts backwards — which is what kills the transistor driving it. This is the
    /// single most common way people destroy a relay driver.
    /// </summary>
    [Fact]
    public void SwitchingTheCoilOffWithNoFlybackDiodeProducesAHugeSpike()
    {
        var (sim, relay, drive, _) = Rig(5.0, withFlyback: false);

        sim.Run(50e-3);
        Assert.True(relay.IsEnergised);

        drive.IsClosed = false;
        sim.Run(5e-3);

        Assert.True(relay.PeakCoilVoltage > relay.KickbackLimit,
            $"the coil only reached {relay.PeakCoilVoltage:0} V on turn-off");
        Assert.Contains("flyback diode", string.Join(" | ", relay.Violations));
    }

    /// <summary>The other half: with the diode fitted, the spike is clamped and nothing complains.</summary>
    [Fact]
    public void AFlybackDiodeClampsTheSpikeAndClearsTheWarning()
    {
        var (sim, relay, drive, _) = Rig(5.0, withFlyback: true);

        sim.Run(50e-3);
        drive.IsClosed = false;
        sim.Run(5e-3);

        Assert.True(relay.PeakCoilVoltage < relay.KickbackLimit,
            $"the clamped coil still reached {relay.PeakCoilVoltage:0} V");
        Assert.Empty(relay.Violations);
    }

    [Fact]
    public void TheRestingContactFeedsNormallyClosed()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(12.0));
        var relay = circuit.Add(new Relay());
        var load = circuit.Add(new Resistor(100));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, relay.Common);
        circuit.Connect(relay.NormallyClosed, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.SolveOperatingPoint();

        Assert.False(relay.IsEnergised);
        Assert.True(sim.NodeVoltage(load.A) > 11.0);
    }
}

public class FuseTests
{
    private static (CircuitSimulator Sim, Fuse Fuse, Resistor Load) Rig(
        double supplyVolts, double loadOhms, double rated = 1.0, double i2t = 0.5)
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(supplyVolts));
        var fuse = circuit.Add(new Fuse(rated) { MeltingIntegral = i2t });
        var load = circuit.Add(new Resistor(loadOhms));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, fuse.A);
        circuit.Connect(fuse.B, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { UseInitialConditions = true });
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, fuse, load);
    }

    [Fact]
    public void AtOrBelowItsRatingItCarriesCurrentIndefinitely()
    {
        // 10 V into 20 R is 500 mA through a 1 A fuse.
        var (sim, fuse, _) = Rig(10.0, 20.0);

        sim.Run(10.0);        // ten seconds

        Assert.False(fuse.HasBlown);
        Assert.Equal(0.0, fuse.MeltFraction, 1e-9);
        Assert.Equal(0.5, Math.Abs(fuse.Current), 0.01);
    }

    /// <summary>
    /// The melting integral is what makes a fuse survive an inrush many times its rating: the
    /// heat has to accumulate. Twice the rated current should take about I2t/(i^2 - rated^2)
    /// seconds, which for a 1 A fuse of 0.5 A2s at 2 A is 1/6 of a second.
    /// </summary>
    [Fact]
    public void ItBlowsAfterTheTimeItsMeltingIntegralImplies()
    {
        var (sim, fuse, _) = Rig(10.0, 5.0);      // 2 A through a 1 A fuse

        double? blewAt = null;
        sim.TimePointAccepted += s =>
        {
            if (blewAt is null && fuse.HasBlown) blewAt = s.Time;
        };

        sim.Run(1.0);

        Assert.True(fuse.HasBlown);
        Assert.NotNull(blewAt);

        // 0.5 / (2^2 - 1^2) = 0.1667 s.
        Assert.Equal(0.1667, blewAt!.Value, 0.02);
    }

    [Fact]
    public void AHeavierOverloadBlowsItSooner()
    {
        var (sim, fuse, _) = Rig(10.0, 2.0);      // 5 A through a 1 A fuse

        double? blewAt = null;
        sim.TimePointAccepted += s =>
        {
            if (blewAt is null && fuse.HasBlown) blewAt = s.Time;
        };

        sim.Run(1.0);

        // 0.5 / (5^2 - 1^2) = 20.8 ms.
        Assert.True(fuse.HasBlown);
        Assert.Equal(0.0208, blewAt!.Value, 0.005);
    }

    [Fact]
    public void OnceBlownItStopsTheCurrentAndStaysBlown()
    {
        var (sim, fuse, load) = Rig(10.0, 5.0);

        sim.Run(1.0);
        Assert.True(fuse.HasBlown);

        sim.Run(1.0);

        Assert.True(Math.Abs(fuse.Current) < 1e-6, $"{fuse.Current:0.000000} A still flowing");
        Assert.True(sim.NodeVoltage(load.A) < 0.01, "the load should be dead");
        Assert.Contains("blown", string.Join(" | ", fuse.Violations));
    }

    [Fact]
    public void ABriefInrushWellOverTheRatingDoesNotBlowIt()
    {
        // 5 A for 5 ms is 0.12 A2s of excess, well inside a 0.5 A2s element. A fuse that blew on
        // instantaneous current would open here, which is why capacitive loads need this.
        var (sim, fuse, _) = Rig(10.0, 2.0);

        sim.Run(5e-3);

        Assert.False(fuse.HasBlown);
        Assert.InRange(fuse.MeltFraction, 0.05, 0.5);
    }
}

public class OptocouplerTests
{
    /// <summary>
    /// Two supplies with no connection between them but the optocoupler: the input side drives the
    /// LED through a resistor, the output side pulls up the collector.
    /// </summary>
    private static (CircuitSimulator Sim, Optocoupler Opto, Resistor PullUp)
        Isolated(double driveVolts, double ledResistor = 1e3, double pullUp = 10e3)
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());

        var drive = circuit.Add(new DcVoltageSource(driveVolts));
        var series = circuit.Add(new Resistor(ledResistor));
        var opto = circuit.Add(new Optocoupler(OptocouplerModel.Pc817));

        circuit.Connect(drive.Negative, gnd.Pin);
        circuit.Connect(drive.Positive, series.A);
        circuit.Connect(series.B, opto.Anode);
        circuit.Connect(opto.Cathode, gnd.Pin);

        var rail = circuit.Add(new DcVoltageSource(5.0));
        var resistor = circuit.Add(new Resistor(pullUp));
        circuit.Connect(rail.Positive, resistor.A);
        circuit.Connect(resistor.B, opto.Collector);
        circuit.Connect(opto.Emitter, rail.Negative);

        // The output side needs its own reference; isolation does not remove that requirement.
        circuit.Connect(opto.Emitter, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.SolveOperatingPoint();
        return (sim, opto, resistor);
    }

    [Fact]
    public void WithNoLedCurrentTheOutputStaysPulledUp()
    {
        var (sim, opto, _) = Isolated(0.0);

        Assert.True(sim.NodeVoltage(opto.Collector) > 4.9,
            $"the collector sat at {sim.NodeVoltage(opto.Collector):0.00} V with the LED dark");
        Assert.False(opto.IsConducting);
    }

    [Fact]
    public void DrivingTheLedPullsTheOutputDown()
    {
        // 5 V through 1k with a 1.2 V emitter is about 3.8 mA; at 100% CTR that is far more than
        // the 0.5 mA the 10k pull-up can supply, so the output saturates low.
        var (sim, opto, _) = Isolated(5.0);

        Assert.True(opto.LedCurrent > 2e-3, $"only {opto.LedCurrent * 1000:0.0} mA of LED current");
        Assert.True(sim.NodeVoltage(opto.Collector) < 0.5,
            $"the collector only fell to {sim.NodeVoltage(opto.Collector):0.00} V");
        Assert.True(opto.IsConducting);
    }

    /// <summary>
    /// The transfer ratio is the number on the datasheet: collector current per amp of LED
    /// current, while the transistor is out of saturation.
    /// </summary>
    [Fact]
    public void TheCollectorCurrentFollowsTheTransferRatio()
    {
        // A large pull-up would saturate; a small one keeps the transistor in its linear region
        // so the ratio is observable.
        var (_, opto, _) = Isolated(5.0, ledResistor: 10e3, pullUp: 100);

        var ratio = opto.CollectorCurrent / opto.LedCurrent;

        Assert.Equal(OptocouplerModel.Pc817.CurrentTransferRatio, ratio, 0.15);
    }

    [Fact]
    public void ALowerTransferRatioPartPullsDownLessHard()
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());
        var drive = circuit.Add(new DcVoltageSource(5.0));
        var series = circuit.Add(new Resistor(10e3));
        var opto = circuit.Add(new Optocoupler(OptocouplerModel.FourN25));   // 50% CTR
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var pull = circuit.Add(new Resistor(100));

        circuit.Connect(drive.Negative, gnd.Pin);
        circuit.Connect(drive.Positive, series.A);
        circuit.Connect(series.B, opto.Anode);
        circuit.Connect(opto.Cathode, gnd.Pin);
        circuit.Connect(rail.Positive, pull.A);
        circuit.Connect(pull.B, opto.Collector);
        circuit.Connect(opto.Emitter, rail.Negative);
        circuit.Connect(opto.Emitter, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.SolveOperatingPoint();

        Assert.Equal(0.5, opto.CollectorCurrent / opto.LedCurrent, 0.15);
    }

    /// <summary>
    /// The point of the part. The two halves share no conductive path, so the output side can sit
    /// at a completely different potential from the input side — which is how a 3.3 V board
    /// switches something at mains potential without the two meeting.
    /// </summary>
    [Fact]
    public void TheTwoSidesAreGalvanicallyIsolated()
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());

        var drive = circuit.Add(new DcVoltageSource(5.0));
        var series = circuit.Add(new Resistor(1e3));
        var opto = circuit.Add(new Optocoupler());

        circuit.Connect(drive.Negative, gnd.Pin);
        circuit.Connect(drive.Positive, series.A);
        circuit.Connect(series.B, opto.Anode);
        circuit.Connect(opto.Cathode, gnd.Pin);

        // Output side floating 100 V above the input side's ground.
        var lift = circuit.Add(new DcVoltageSource(100.0));
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var pull = circuit.Add(new Resistor(10e3));

        circuit.Connect(lift.Negative, gnd.Pin);
        circuit.Connect(lift.Positive, opto.Emitter);
        circuit.Connect(opto.Emitter, rail.Negative);
        circuit.Connect(rail.Positive, pull.A);
        circuit.Connect(pull.B, opto.Collector);

        var sim = new CircuitSimulator(circuit);
        sim.SolveOperatingPoint();

        // The input side is still referenced to 0 while the output side floats at 100 V.
        Assert.Equal(0.0, sim.NodeVoltage(opto.Cathode), 0.01);
        Assert.Equal(100.0, sim.NodeVoltage(opto.Emitter), 0.1);

        // And the LED is still conducting, so the isolation has not broken the drive.
        Assert.True(opto.LedCurrent > 1e-3);

        // Almost nothing crosses the barrier: 100 V across a gigohm.
        var leakage = (sim.NodeVoltage(opto.Emitter) - sim.NodeVoltage(opto.Cathode)) / opto.IsolationResistance;
        Assert.True(Math.Abs(leakage) < 1e-6, $"{leakage * 1e6:0.0} uA crossed the barrier");
    }
}
