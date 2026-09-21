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

    /// <summary>
    /// Gain measured as a <b>slope</b> rather than as a single ratio of output to input.
    /// <para>
    /// A real amplifier's output is its gain times the input <i>plus a constant</i> — the input
    /// offset multiplied by the noise gain, and the bias current through the feedback resistor.
    /// Dividing one output by one input folds all of that into the answer, which is why nobody
    /// measures gain that way on a bench either: you move the input and see how far the output
    /// moves. Two points, and every constant term cancels.
    /// </para>
    /// </summary>
    private static double MeasuredGain(double rin, double rf)
    {
        double OutputAt(double vin)
        {
            var (sim, u, _) = InvertingAmplifier(rin, rf, vin);
            sim.SolveOperatingPoint();

            Assert.False(u.IsSaturated);
            return sim.NodeVoltage(u.Output);
        }

        return (OutputAt(0.1) - OutputAt(0.05)) / 0.05;
    }

    [Fact]
    public void InvertingAmplifierMatchesItsIdealGain()
    {
        Assert.Equal(-10.0, MeasuredGain(10e3, 100e3), 0.1);
    }

    [Theory]
    [InlineData(1e3, 1e3, -1.0)]
    [InlineData(10e3, 100e3, -10.0)]
    [InlineData(1e3, 47e3, -47.0)]
    public void GainFollowsTheFeedbackRatio(double rin, double rf, double expectedGain)
    {
        Assert.Equal(expectedGain, MeasuredGain(rin, rf), Math.Abs(expectedGain) * 0.02);
    }

    /// <summary>
    /// The constant the slope measurement above is cancelling. A 741 has a millivolt of input
    /// offset, and at a noise gain of eleven that is eleven millivolts at the output with nothing
    /// at all on the input — which is the whole reason precision circuits trim it out.
    /// </summary>
    [Fact]
    public void TheInputOffsetAppearsAtTheOutputMultipliedByTheNoiseGain()
    {
        var (sim, u, _) = InvertingAmplifier(10e3, 100e3, 0.0);
        sim.SolveOperatingPoint();

        var expected = OpAmpModel.Lm741.InputOffsetVoltage * (1 + (100e3 / 10e3));

        // Plus the bias current through the feedback resistor, which is the other term.
        var bias = OpAmpModel.Lm741.InputBiasCurrent * 100e3;

        Assert.Equal(expected + bias, sim.NodeVoltage(u.Output), Math.Abs(expected) * 0.15);
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

        // Measured over the ramp rather than step by step. A slew rate is a sustained rate, and
        // the first step after an edge is a breakpoint a nanosecond wide where the integration is
        // still settling — reading the answer off that one step measures the time step, not the
        // amplifier.
        var travelled = 0.0;
        var elapsed = 0.0;
        var previous = sim.NodeVoltage(u.Output);
        while (sim.Time < 100e-6)
        {
            var dt = sim.Step();
            var now = sim.NodeVoltage(u.Output);
            var rate = Math.Abs(now - previous) / dt;

            // Only while it is actually slewing: a 20 V square into a follower spends most of
            // each half-cycle parked against a rail with the output not moving at all.
            if (rate > 0.3e6 && dt > 1e-9)
            {
                travelled += Math.Abs(now - previous);
                elapsed += dt;
            }

            previous = now;
        }

        Assert.True(elapsed > 10e-6, $"expected a long ramp to measure, got {elapsed * 1e6:0.0} us");

        // 741 slews at 0.5 V/us, and it must be nowhere near the 20 V/ns the ideal input edge asks
        // for: the tail current into the compensation capacitor is the only thing setting this.
        Assert.Equal(0.5e6, travelled / elapsed, 0.05e6);
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
