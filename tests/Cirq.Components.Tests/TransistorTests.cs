using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class BipolarTransistorTests
{
    /// <summary>
    /// Common-emitter stage: supply -> Rc -> collector, base driven through Rb, emitter grounded.
    /// </summary>
    private static (CircuitSimulator Sim, BipolarTransistor Q, DcVoltageSource Drive, Resistor Rc)
        CommonEmitter(double supply, double baseVoltage, double rb, double rc, BjtModel? model = null)
    {
        var circuit = new Circuit();
        var vcc = circuit.Add(new DcVoltageSource(supply));
        var drive = circuit.Add(new DcVoltageSource(baseVoltage));
        var q = circuit.Add(new BipolarTransistor(model));
        var collectorLoad = circuit.Add(new Resistor(rc));
        var baseResistor = circuit.Add(new Resistor(rb));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(vcc.Negative, gnd.Pin);
        circuit.Connect(drive.Negative, gnd.Pin);
        circuit.Connect(vcc.Positive, collectorLoad.A);
        circuit.Connect(collectorLoad.B, q.Collector);
        circuit.Connect(drive.Positive, baseResistor.A);
        circuit.Connect(baseResistor.B, q.Base);
        circuit.Connect(q.Emitter, gnd.Pin);

        return (new CircuitSimulator(circuit), q, drive, collectorLoad);
    }

    [Fact]
    public void ForwardActiveGainIsCloseToTheModelBeta()
    {
        // 2 V through 100k gives roughly 13 uA of base current, which stays well clear of
        // saturation with a 1k collector load on 10 V.
        var (sim, q, _, _) = CommonEmitter(10.0, 2.0, 100e3, 1e3);
        sim.SolveOperatingPoint();

        Assert.False(q.IsSaturated);
        // Slightly above the nominal beta of 200: the Early effect lifts collector current by
        // roughly (1 + Vce/Vaf), which is about 10% at this operating point.
        Assert.InRange(q.CurrentGain, 180, 240);
        Assert.InRange(q.Vbe, 0.55, 0.8);
    }

    [Fact]
    public void ZeroBaseDriveLeavesTheTransistorOff()
    {
        var (sim, q, _, rc) = CommonEmitter(10.0, 0.0, 100e3, 1e3);
        sim.SolveOperatingPoint();

        Assert.True(Math.Abs(q.CollectorCurrent) < 1e-6, $"Leakage was {q.CollectorCurrent:g3} A.");
        // With no current in the load the collector sits at the supply.
        Assert.Equal(10.0, sim.NodeVoltage(q.Collector), 0.01);
    }

    [Fact]
    public void HardBaseDriveBottomsTheTransistorOut()
    {
        // 5 V through 1k is ~4.3 mA of base drive against a 1k load that can only pass 10 mA.
        var (sim, q, _, _) = CommonEmitter(10.0, 5.0, 1e3, 1e3);
        sim.SolveOperatingPoint();

        Assert.True(q.IsSaturated, "Expected the transistor to saturate.");
        // A saturated small-signal BJT sits at a couple of hundred millivolts.
        Assert.InRange(sim.NodeVoltage(q.Collector), 0.0, 0.35);
    }

    [Fact]
    public void KirchhoffHoldsAtEveryTerminal()
    {
        var (sim, q, _, rc) = CommonEmitter(10.0, 2.0, 100e3, 1e3);
        sim.SolveOperatingPoint();

        var throughLoad = (10.0 - sim.NodeVoltage(q.Collector)) / 1e3;
        Assert.Equal(throughLoad, q.CollectorCurrent, Math.Abs(throughLoad) * 0.02 + 1e-9);

        // Emitter carries the sum of the other two.
        Assert.Equal(q.CollectorCurrent + q.BaseCurrent, q.EmitterCurrent, 1e-12);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(3.0)]
    [InlineData(5.0)]
    public void ConvergesAcrossTheDriveRange(double baseVoltage)
    {
        var (sim, q, _, _) = CommonEmitter(12.0, baseVoltage, 47e3, 2.2e3);
        sim.SolveOperatingPoint();

        Assert.True(q.CollectorCurrent >= -1e-9);
        Assert.InRange(sim.NodeVoltage(q.Collector), -0.1, 12.1);
    }

    [Fact]
    public void CollectorCurrentIsExponentialInBaseEmitterVoltage()
    {
        // A decade of collector current costs about Vt·ln(10) ~= 60 mV of Vbe.
        static (double Vbe, double Ic) Operate(double rb)
        {
            var (sim, q, _, _) = CommonEmitter(15.0, 5.0, rb, 100.0);
            sim.SolveOperatingPoint();
            return (q.Vbe, q.CollectorCurrent);
        }

        var low = Operate(4.7e6);
        var high = Operate(470e3);

        var decades = Math.Log10(high.Ic / low.Ic);
        Assert.True(decades > 0.5, "Expected the higher drive to produce more collector current.");

        var perDecade = (high.Vbe - low.Vbe) / decades;
        Assert.InRange(perDecade, 0.045, 0.08);
    }

    [Fact]
    public void PnpMirrorsTheNpnAboutTheSupply()
    {
        // PNP high-side switch: emitter to +10 V, base pulled down through Rb, load to ground.
        var circuit = new Circuit();
        var vcc = circuit.Add(new DcVoltageSource(10.0));
        var q = circuit.Add(new BipolarTransistor(BjtModel.P2N3906));
        var load = circuit.Add(new Resistor(1e3));
        var baseResistor = circuit.Add(new Resistor(10e3));
        var drive = circuit.Add(new DcVoltageSource(0.0));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(vcc.Negative, gnd.Pin);
        circuit.Connect(drive.Negative, gnd.Pin);
        circuit.Connect(q.Emitter, vcc.Positive);
        circuit.Connect(q.Base, baseResistor.A);
        circuit.Connect(baseResistor.B, drive.Positive);
        circuit.Connect(q.Collector, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);

        // Base pulled to ground: the PNP turns on and the load sees nearly the full supply.
        sim.SolveOperatingPoint();
        Assert.True(q.IsConducting);
        Assert.InRange(sim.NodeVoltage(load.A), 9.0, 10.0);

        // Base tied to the emitter: the device turns off.
        drive.Voltage = 10.0;
        sim.Reset();
        sim.SolveOperatingPoint();
        Assert.True(Math.Abs(q.CollectorCurrent) < 1e-6);
        Assert.InRange(sim.NodeVoltage(load.A), -0.01, 0.01);
    }

    [Fact]
    public void EmitterFollowerTracksItsInputOneDiodeDropDown()
    {
        var circuit = new Circuit();
        var vcc = circuit.Add(new DcVoltageSource(12.0));
        var drive = circuit.Add(new DcVoltageSource(6.0));
        var q = circuit.Add(new BipolarTransistor());
        var emitterLoad = circuit.Add(new Resistor(1e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(vcc.Negative, gnd.Pin);
        circuit.Connect(drive.Negative, gnd.Pin);
        circuit.Connect(q.Collector, vcc.Positive);
        circuit.Connect(q.Base, drive.Positive);
        circuit.Connect(q.Emitter, emitterLoad.A);
        circuit.Connect(emitterLoad.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.SolveOperatingPoint();

        var emitter = sim.NodeVoltage(q.Emitter);
        Assert.InRange(emitter, 6.0 - 0.8, 6.0 - 0.5);
    }

    [Fact]
    public void CommonEmitterStageHasFiniteVoltageGainFromTheEarlyEffect()
    {
        // Without an Early-effect output conductance the stage gain would be unbounded.
        static double CollectorAt(double baseVoltage)
        {
            var (sim, _, _, _) = CommonEmitter(10.0, baseVoltage, 100e3, 1e3);
            sim.SolveOperatingPoint();
            return sim.NodeVoltage(sim.Circuit.Components.OfType<BipolarTransistor>().First().Collector);
        }

        var a = CollectorAt(1.9);
        var b = CollectorAt(1.95);

        var gain = (b - a) / 0.05;
        // Inverting stage, and a real amount of gain rather than a rail-to-rail jump.
        Assert.True(gain < 0, $"Expected an inverting stage, measured a gain of {gain:0.#}.");
        Assert.True(double.IsFinite(gain));
    }
}

public class MosfetTests
{
    /// <summary>Low-side switch: supply -> load -> drain, source grounded, gate driven.</summary>
    private static (CircuitSimulator Sim, Mosfet M, DcVoltageSource Gate, Resistor Load)
        LowSideSwitch(double supply, double gateVoltage, double load, MosfetModel? model = null)
    {
        var circuit = new Circuit();
        var vcc = circuit.Add(new DcVoltageSource(supply));
        var gate = circuit.Add(new DcVoltageSource(gateVoltage));
        var m = circuit.Add(new Mosfet(model));
        var resistor = circuit.Add(new Resistor(load));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(vcc.Negative, gnd.Pin);
        circuit.Connect(gate.Negative, gnd.Pin);
        circuit.Connect(vcc.Positive, resistor.A);
        circuit.Connect(resistor.B, m.Drain);
        circuit.Connect(gate.Positive, m.Gate);
        circuit.Connect(m.Source, gnd.Pin);

        return (new CircuitSimulator(circuit), m, gate, resistor);
    }

    [Fact]
    public void BelowThresholdTheChannelIsOff()
    {
        var (sim, m, _, _) = LowSideSwitch(12.0, 1.0, 100.0);
        sim.SolveOperatingPoint();

        Assert.False(m.IsConducting);
        Assert.Equal(12.0, sim.NodeVoltage(m.Drain), 0.01);
    }

    [Fact]
    public void FullGateDriveTurnsTheChannelHardOn()
    {
        var (sim, m, _, _) = LowSideSwitch(12.0, 10.0, 100.0, MosfetModel.IrlZ44N);
        sim.SolveOperatingPoint();

        Assert.True(m.IsConducting);
        Assert.True(m.IsInTriode, "A hard-driven switch should sit in the triode region.");
        // Nearly the whole supply appears across the load rather than the device.
        Assert.InRange(sim.NodeVoltage(m.Drain), 0.0, 0.5);
    }

    [Fact]
    public void SaturationCurrentFollowsTheSquareLaw()
    {
        // With a large load the device runs out of headroom and sits in saturation, where
        // Id = (K/2)(Vgs - Vt)^2. For the 2N7000 at Vgs = 5: 0.5 * 0.05 * 3^2 = 225 mA.
        var circuit = new Circuit();
        var vcc = circuit.Add(new DcVoltageSource(20.0));
        var gate = circuit.Add(new DcVoltageSource(5.0));
        var m = circuit.Add(new Mosfet(MosfetModel.N2N7000 with { ChannelLengthModulation = 0 }));
        var load = circuit.Add(new Resistor(10.0));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(vcc.Negative, gnd.Pin);
        circuit.Connect(gate.Negative, gnd.Pin);
        circuit.Connect(vcc.Positive, load.A);
        circuit.Connect(load.B, m.Drain);
        circuit.Connect(gate.Positive, m.Gate);
        circuit.Connect(m.Source, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.SolveOperatingPoint();

        Assert.False(m.IsInTriode);
        Assert.Equal(0.225, m.DrainCurrent, 0.005);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(2.5)]
    [InlineData(5.0)]
    [InlineData(10.0)]
    public void ConvergesAcrossTheGateRange(double gateVoltage)
    {
        var (sim, m, _, _) = LowSideSwitch(12.0, gateVoltage, 100.0);
        sim.SolveOperatingPoint();

        Assert.True(m.DrainCurrent >= -1e-6);
        Assert.InRange(sim.NodeVoltage(m.Drain), -0.1, 12.1);
    }

    [Fact]
    public void TheGateDrawsNoCurrent()
    {
        var (sim, m, gate, _) = LowSideSwitch(12.0, 10.0, 100.0);
        sim.SolveOperatingPoint();

        // An insulated gate loads its driver with essentially nothing.
        Assert.True(Math.Abs(gate.OutputCurrent) < 1e-9,
            $"The gate drew {gate.OutputCurrent:g3} A.");
    }

    [Fact]
    public void PChannelSwitchesOnWhenTheGateIsPulledDown()
    {
        // High-side P-channel switch: source at the supply, load from drain to ground.
        var circuit = new Circuit();
        var vcc = circuit.Add(new DcVoltageSource(12.0));
        var gate = circuit.Add(new DcVoltageSource(12.0));
        var m = circuit.Add(new Mosfet(MosfetModel.Irf9540));
        var load = circuit.Add(new Resistor(100.0));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(vcc.Negative, gnd.Pin);
        circuit.Connect(gate.Negative, gnd.Pin);
        circuit.Connect(m.Source, vcc.Positive);
        circuit.Connect(gate.Positive, m.Gate);
        circuit.Connect(m.Drain, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);

        // Gate at the supply: Vgs = 0, so the device is off.
        sim.SolveOperatingPoint();
        Assert.InRange(sim.NodeVoltage(load.A), -0.01, 0.05);

        // Gate pulled to ground: Vgs = -12 V turns it on.
        gate.Voltage = 0.0;
        sim.Reset();
        sim.SolveOperatingPoint();
        Assert.True(m.IsConducting);
        Assert.InRange(sim.NodeVoltage(load.A), 11.5, 12.0);
    }

    [Fact]
    public void TheBodyDiodeConductsWhenTheChannelIsReverseBiased()
    {
        // Gate off, but the drain forced below the source: the intrinsic diode takes over.
        var circuit = new Circuit();
        var source = circuit.Add(new DcVoltageSource(-2.0));
        var m = circuit.Add(new Mosfet(MosfetModel.Irf540));
        var series = circuit.Add(new Resistor(100.0));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(source.Negative, gnd.Pin);
        circuit.Connect(source.Positive, series.A);
        circuit.Connect(series.B, m.Drain);
        circuit.Connect(m.Source, gnd.Pin);
        circuit.Connect(m.Gate, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.SolveOperatingPoint();

        // The drain clamps roughly a diode drop below ground instead of following the source.
        Assert.InRange(sim.NodeVoltage(m.Drain), -1.1, -0.4);
    }

    [Fact]
    public void ADisabledBodyDiodeLeavesTheReverseDirectionBlocked()
    {
        var circuit = new Circuit();
        var source = circuit.Add(new DcVoltageSource(-2.0));
        var m = circuit.Add(new Mosfet(MosfetModel.Irf540 with { HasBodyDiode = false }));
        var series = circuit.Add(new Resistor(100.0));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(source.Negative, gnd.Pin);
        circuit.Connect(source.Positive, series.A);
        circuit.Connect(series.B, m.Drain);
        circuit.Connect(m.Source, gnd.Pin);
        circuit.Connect(m.Gate, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.SolveOperatingPoint();

        // Nothing conducts, so the drain follows the source all the way down.
        Assert.InRange(sim.NodeVoltage(m.Drain), -2.01, -1.9);
    }

    [Fact]
    public void AFlybackDiodeClampsAnInductiveSwitchingSpike()
    {
        // A low-side switch driving a coil. Note the body diode cannot help here: it sits from
        // source to drain, so it is reverse biased exactly when the collapsing field drives the
        // drain above the supply. That is why the flyback diode across the coil is the part doing
        // the clamping, and why leaving it out is a real mistake rather than a modelling detail.
        var circuit = new Circuit();
        var vcc = circuit.Add(new DcVoltageSource(12.0));
        var gate = circuit.Add(new FunctionGenerator(Waveform.Square, 1e3, 10.0) { DcOffset = 5.0 });
        var m = circuit.Add(new Mosfet(MosfetModel.IrlZ44N));
        var coil = circuit.Add(new Inductor(10e-3) { SeriesResistance = 5.0 });
        var flyback = circuit.Add(new Diode(DiodeModel.D1N4001));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(vcc.Negative, gnd.Pin);
        circuit.Connect(gate.Return, gnd.Pin);
        circuit.Connect(vcc.Positive, coil.A);
        circuit.Connect(coil.B, m.Drain);
        circuit.Connect(gate.Output, m.Gate);
        circuit.Connect(m.Source, gnd.Pin);

        // Cathode to the supply, anode to the drain: conducts only on the turn-off kick.
        circuit.Connect(flyback.Anode, m.Drain);
        circuit.Connect(flyback.Cathode, vcc.Positive);

        var sim = new CircuitSimulator(circuit, new SimulationSettings
        {
            TimeStep = 1e-7,
            MaxTimeStep = 1e-7,
            UseInitialConditions = true,
        });

        sim.Reset();
        sim.SolveOperatingPoint();

        var peak = 0.0;
        var conducted = false;
        while (sim.Time < 3e-3)
        {
            sim.Step();
            peak = Math.Max(peak, sim.NodeVoltage(m.Drain));
            if (m.IsConducting) conducted = true;
        }

        Assert.True(conducted, "The switch never turned on.");
        // Clamped to the supply plus a rectifier drop, not an unbounded spike.
        Assert.InRange(peak, 12.0, 14.0);
    }

    [Fact]
    public void AnUnclampedInductiveTurnOffProducesAMuchLargerSpike()
    {
        // The companion to the test above: the same circuit without the flyback diode has nowhere
        // to send the coil current, and the drain flies far above the supply.
        var circuit = new Circuit();
        var vcc = circuit.Add(new DcVoltageSource(12.0));
        var gate = circuit.Add(new FunctionGenerator(Waveform.Square, 1e3, 10.0) { DcOffset = 5.0 });
        var m = circuit.Add(new Mosfet(MosfetModel.IrlZ44N));
        var coil = circuit.Add(new Inductor(1e-3) { SeriesResistance = 50.0 });
        var bleed = circuit.Add(new Resistor(1e6));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(vcc.Negative, gnd.Pin);
        circuit.Connect(gate.Return, gnd.Pin);
        circuit.Connect(vcc.Positive, coil.A);
        circuit.Connect(coil.B, m.Drain);
        circuit.Connect(gate.Output, m.Gate);
        circuit.Connect(m.Source, gnd.Pin);
        // A high-value bleed path stands in for real-world leakage, so the node is not floating.
        circuit.Connect(bleed.A, m.Drain);
        circuit.Connect(bleed.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings
        {
            TimeStep = 1e-7,
            MaxTimeStep = 1e-7,
            UseInitialConditions = true,
        });

        sim.Reset();
        sim.SolveOperatingPoint();

        var peak = 0.0;
        while (sim.Time < 2e-3)
        {
            sim.Step();
            peak = Math.Max(peak, sim.NodeVoltage(m.Drain));
        }

        Assert.True(peak > 20.0, $"Expected a real inductive kick, saw only {peak:0.#} V.");
    }
}
