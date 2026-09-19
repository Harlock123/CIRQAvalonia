using Cirq.Components.Ics;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class OpAmpTests
{
    /// <summary>Adds +/-15 V rails and returns the two supply nodes.</summary>
    private static (DcVoltageSource Pos, DcVoltageSource Neg, Ground Gnd) AddSupplies(Circuit circuit, double rail = 15.0)
    {
        var pos = circuit.Add(new DcVoltageSource(rail));
        var neg = circuit.Add(new DcVoltageSource(-rail));
        var gnd = circuit.Add(new Ground());
        circuit.Connect(pos.Negative, gnd.Pin);
        circuit.Connect(neg.Negative, gnd.Pin);
        return (pos, neg, gnd);
    }

    /// <summary>Inverting amplifier: gain = -Rf/Rin.</summary>
    private static (CircuitSimulator Sim, OperationalAmplifier U, DcVoltageSource Input)
        InvertingAmplifier(double rin, double rf, double vin)
    {
        var circuit = new Circuit();
        var (pos, neg, gnd) = AddSupplies(circuit);
        var u = circuit.Add(new OpAmp741());
        var input = circuit.Add(new DcVoltageSource(vin));
        var ri = circuit.Add(new Resistor(rin));
        var rfb = circuit.Add(new Resistor(rf));

        circuit.Connect(u.PositiveSupply, pos.Positive);
        circuit.Connect(u.NegativeSupply, neg.Positive);
        circuit.Connect(u.NonInverting, gnd.Pin);
        circuit.Connect(input.Negative, gnd.Pin);
        circuit.Connect(input.Positive, ri.A);
        circuit.Connect(ri.B, u.Inverting);
        circuit.Connect(u.Inverting, rfb.A);
        circuit.Connect(rfb.B, u.Output);

        return (new CircuitSimulator(circuit), u, input);
    }

    [Fact]
    public void InvertingAmplifierMatchesItsIdealGain()
    {
        var (sim, u, _) = InvertingAmplifier(10e3, 100e3, 0.1);
        sim.SolveOperatingPoint();

        // Ideal gain is -10, so 0.1 V in gives -1 V out.
        Assert.Equal(-1.0, sim.NodeVoltage(u.Output), 0.01);
        Assert.False(u.IsSaturated);
    }

    [Theory]
    [InlineData(1e3, 1e3, -1.0)]
    [InlineData(10e3, 100e3, -10.0)]
    [InlineData(1e3, 47e3, -47.0)]
    public void GainFollowsTheFeedbackRatio(double rin, double rf, double expectedGain)
    {
        const double vin = 0.05;
        var (sim, u, _) = InvertingAmplifier(rin, rf, vin);
        sim.SolveOperatingPoint();

        var measured = sim.NodeVoltage(u.Output) / vin;
        Assert.Equal(expectedGain, measured, Math.Abs(expectedGain) * 0.02);
    }

    [Fact]
    public void VirtualGroundHoldsAtTheInvertingInput()
    {
        var (sim, u, _) = InvertingAmplifier(10e3, 100e3, 0.1);
        sim.SolveOperatingPoint();

        // Feedback drives the summing junction to within Vout/Aol of ground.
        Assert.Equal(0.0, sim.NodeVoltage(u.Inverting), 5e-3);
    }

    [Fact]
    public void OutputSaturatesAtTheSupplyRails()
    {
        // A gain of -100 on 1 V would demand -100 V; the rails stop it well short.
        var (sim, u, _) = InvertingAmplifier(1e3, 100e3, 1.0);
        sim.SolveOperatingPoint();

        var output = sim.NodeVoltage(u.Output);
        Assert.InRange(output, -14.0, -12.5);
        Assert.True(u.IsSaturated, "Expected the output stage to report saturation.");
    }

    [Fact]
    public void VoltageFollowerTracksItsInput()
    {
        var circuit = new Circuit();
        var (pos, neg, gnd) = AddSupplies(circuit);
        var u = circuit.Add(new OpAmp741());
        var input = circuit.Add(new DcVoltageSource(2.5));

        circuit.Connect(u.PositiveSupply, pos.Positive);
        circuit.Connect(u.NegativeSupply, neg.Positive);
        circuit.Connect(input.Negative, gnd.Pin);
        circuit.Connect(input.Positive, u.NonInverting);
        circuit.Connect(u.Output, u.Inverting);

        var sim = new CircuitSimulator(circuit);
        sim.SolveOperatingPoint();

        Assert.Equal(2.5, sim.NodeVoltage(u.Output), 0.01);
    }

    [Fact]
    public void NonInvertingAmplifierAddsUnityToTheFeedbackRatio()
    {
        var circuit = new Circuit();
        var (pos, neg, gnd) = AddSupplies(circuit);
        var u = circuit.Add(new OpAmp741());
        var input = circuit.Add(new DcVoltageSource(1.0));
        var rg = circuit.Add(new Resistor(10e3));
        var rf = circuit.Add(new Resistor(20e3));

        circuit.Connect(u.PositiveSupply, pos.Positive);
        circuit.Connect(u.NegativeSupply, neg.Positive);
        circuit.Connect(input.Negative, gnd.Pin);
        circuit.Connect(input.Positive, u.NonInverting);
        circuit.Connect(u.Inverting, rg.A);
        circuit.Connect(rg.B, gnd.Pin);
        circuit.Connect(u.Inverting, rf.A);
        circuit.Connect(rf.B, u.Output);

        var sim = new CircuitSimulator(circuit);
        sim.SolveOperatingPoint();

        // Gain = 1 + Rf/Rg = 3.
        Assert.Equal(3.0, sim.NodeVoltage(u.Output), 0.03);
    }

    [Fact]
    public void ComparatorSwingsRailToRailAroundItsThreshold()
    {
        var circuit = new Circuit();
        var (pos, neg, gnd) = AddSupplies(circuit);
        var u = circuit.Add(new OpAmp741());
        var reference = circuit.Add(new DcVoltageSource(1.0));
        var input = circuit.Add(new DcVoltageSource(0.5));

        circuit.Connect(u.PositiveSupply, pos.Positive);
        circuit.Connect(u.NegativeSupply, neg.Positive);
        circuit.Connect(reference.Negative, gnd.Pin);
        circuit.Connect(input.Negative, gnd.Pin);
        circuit.Connect(reference.Positive, u.Inverting);
        circuit.Connect(input.Positive, u.NonInverting);

        var sim = new CircuitSimulator(circuit);

        sim.SolveOperatingPoint();
        Assert.InRange(sim.NodeVoltage(u.Output), -14.0, -12.5);

        input.Voltage = 1.5;
        sim.Reset();
        sim.SolveOperatingPoint();
        Assert.InRange(sim.NodeVoltage(u.Output), 12.5, 14.0);
    }

    [Fact]
    public void OpenLoopGainMatchesTheModelAtDc()
    {
        var circuit = new Circuit();
        var (pos, neg, gnd) = AddSupplies(circuit);
        var u = circuit.Add(new OperationalAmplifier(OpAmpModel.Lm741 with { InputOffsetVoltage = 0, InputBiasCurrent = 0 }));
        var input = circuit.Add(new DcVoltageSource(20e-6));   // 20 uV -> 4 V out at Aol = 200k

        circuit.Connect(u.PositiveSupply, pos.Positive);
        circuit.Connect(u.NegativeSupply, neg.Positive);
        circuit.Connect(input.Negative, gnd.Pin);
        circuit.Connect(input.Positive, u.NonInverting);
        circuit.Connect(u.Inverting, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.SolveOperatingPoint();

        Assert.Equal(4.0, sim.NodeVoltage(u.Output), 0.2);
    }

    [Fact]
    public void SlewRateLimitsHowFastTheOutputCanMove()
    {
        // A follower driven by a big fast step can only move at the datasheet slew rate.
        var circuit = new Circuit();
        var (pos, neg, gnd) = AddSupplies(circuit);
        var u = circuit.Add(new OpAmp741());
        var input = circuit.Add(new FunctionGenerator(Waveform.Square, 20e3, 20.0) { EdgeTime = 1e-9 });

        circuit.Connect(u.PositiveSupply, pos.Positive);
        circuit.Connect(u.NegativeSupply, neg.Positive);
        circuit.Connect(input.Return, gnd.Pin);
        circuit.Connect(input.Output, u.NonInverting);
        circuit.Connect(u.Output, u.Inverting);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 20e-9, MaxTimeStep = 20e-9 });
        sim.Reset();
        sim.SolveOperatingPoint();

        var previous = sim.NodeVoltage(u.Output);
        var fastestSlew = 0.0;
        while (sim.Time < 100e-6)
        {
            var dt = sim.Step();
            var now = sim.NodeVoltage(u.Output);
            fastestSlew = Math.Max(fastestSlew, Math.Abs(now - previous) / dt);
            previous = now;
        }

        // 741 slews at 0.5 V/us. Allow headroom for the discrete step, but it must be nowhere
        // near the 20 V/ns the ideal input edge demands.
        Assert.InRange(fastestSlew, 0.2e6, 1.2e6);
    }

    [Fact]
    public void ClosedLoopBandwidthFollowsTheGainBandwidthProduct()
    {
        // A gain of 10 from a 1 MHz GBW part rolls off at about 100 kHz, so at 100 kHz the
        // response should be down roughly 3 dB.
        static double GainAt(double frequency)
        {
            var circuit = new Circuit();
            var pos = circuit.Add(new DcVoltageSource(15));
            var neg = circuit.Add(new DcVoltageSource(-15));
            var gnd = circuit.Add(new Ground());
            circuit.Connect(pos.Negative, gnd.Pin);
            circuit.Connect(neg.Negative, gnd.Pin);

            var u = circuit.Add(new OpAmp741());
            var gen = circuit.Add(new FunctionGenerator(Waveform.Sine, frequency, 0.02));
            var rg = circuit.Add(new Resistor(1e3));
            var rf = circuit.Add(new Resistor(9e3));

            circuit.Connect(u.PositiveSupply, pos.Positive);
            circuit.Connect(u.NegativeSupply, neg.Positive);
            circuit.Connect(gen.Return, gnd.Pin);
            circuit.Connect(gen.Output, u.NonInverting);
            circuit.Connect(u.Inverting, rg.A);
            circuit.Connect(rg.B, gnd.Pin);
            circuit.Connect(u.Inverting, rf.A);
            circuit.Connect(rf.B, u.Output);

            var dt = 1.0 / (frequency * 400);
            var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = dt, MaxTimeStep = dt });
            sim.Reset();
            sim.SolveOperatingPoint();

            // Let the loop settle, then measure the output swing over two cycles.
            sim.Run(10.0 / frequency);
            var min = double.MaxValue;
            var max = double.MinValue;
            var end = sim.Time + 2.0 / frequency;
            while (sim.Time < end)
            {
                sim.Step();
                var v = sim.NodeVoltage(u.Output);
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }

            return (max - min) / 0.02;
        }

        var lowFrequency = GainAt(1e3);
        var atCorner = GainAt(100e3);

        Assert.Equal(10.0, lowFrequency, 0.3);
        // -3 dB is a factor of 0.707.
        Assert.InRange(atCorner / lowFrequency, 0.6, 0.85);
    }
}
