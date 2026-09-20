using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// A varactor is a capacitance that follows a voltage, so both halves of that are checked: the
/// capacitance against the junction law, and a tuned circuit whose resonance moves when the bias
/// does.
/// </summary>
public class VaractorTests
{
    /// <summary>C = C0 / (1 + V/Vj)^m, which is the whole component.</summary>
    [Theory]
    [InlineData(0.0, 100e-12)]
    [InlineData(0.7, 50e-12)]
    [InlineData(2.1, 25e-12)]
    [InlineData(6.3, 10e-12)]
    public void TheCapacitanceFollowsTheJunctionLaw(double reverse, double expected)
    {
        var varactor = new Varactor
        {
            ZeroBiasCapacitance = 100e-12,
            JunctionPotential = 0.7,
            GradingCoefficient = 1.0,
        };

        Assert.Equal(expected, varactor.CapacitanceAt(reverse), expected * 0.01);
    }

    /// <summary>
    /// An abrupt junction gives about two to one over a usable bias; the hyperabrupt profiles made
    /// for tuning give ten to one, and that difference is the reason tuning varactors exist as a
    /// separate part rather than people using any old diode.
    /// </summary>
    [Fact]
    public void TheGradingCoefficientIsWhatBuysTheRange()
    {
        var abrupt = new Varactor { GradingCoefficient = 0.5 };
        var hyperabrupt = new Varactor { GradingCoefficient = 1.5 };

        static double Ratio(Varactor v) => v.CapacitanceAt(0.5) / v.CapacitanceAt(8.0);

        Assert.True(Ratio(abrupt) < 4.0, $"an abrupt junction gives {Ratio(abrupt):0.0}:1");
        Assert.True(Ratio(hyperabrupt) > 10.0, $"a hyperabrupt one gives {Ratio(hyperabrupt):0.0}:1");
    }

    private sealed record Tank(Circuit Circuit, Varactor Varactor, Terminal Output);

    /// <summary>
    /// A parallel tank tuned by a varactor. The blocking capacitor is the part that has to be
    /// there and is easy to leave out: without it the inductor is a short at DC and grounds the
    /// cathode, so no bias ever develops across the junction and the tuning control does nothing
    /// whatever. It is large compared with the varactor, so it is in series with it without
    /// changing what the tank sees.
    /// </summary>
    private static Tank BuildTank(double biasVolts)
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());
        var source = circuit.Add(new FunctionGenerator { OutputResistance = 10e3 });

        var bias = circuit.Add(new DcVoltageSource(biasVolts));
        var feed = circuit.Add(new Resistor(100e3));
        var block = circuit.Add(new Capacitor(10e-9));

        var inductor = circuit.Add(new Inductor(10e-6) { SeriesResistance = 0.5 });
        var varactor = circuit.Add(new Varactor
        {
            ZeroBiasCapacitance = 100e-12,
            JunctionPotential = 0.7,
            GradingCoefficient = 1.0,
        });

        circuit.Connect(source.Return, gnd.Pin);
        circuit.Connect(bias.Negative, gnd.Pin);

        // The tank node, driven through a high resistance so the source does not damp it.
        circuit.Connect(source.Output, inductor.A);
        circuit.Connect(inductor.B, gnd.Pin);

        // Tank -> blocking capacitor -> varactor cathode -> anode -> ground.
        circuit.Connect(source.Output, block.A);
        circuit.Connect(block.B, varactor.Cathode);
        circuit.Connect(varactor.Anode, gnd.Pin);

        // Bias onto the cathode through a resistance large enough not to load the tank.
        circuit.Connect(bias.Positive, feed.A);
        circuit.Connect(feed.B, varactor.Cathode);

        circuit.Probes.Add(new SignalProbe { TargetTerminal = inductor.A, Label = "Tank" });

        return new Tank(circuit, varactor, inductor.A);
    }

    private static double ResonanceOf(Circuit circuit)
    {
        var simulator = new CircuitSimulator(circuit);

        simulator.Reset();
        simulator.SolveOperatingPoint();
        simulator.ResolveProbes();

        var sweep = new AcSweep(simulator).Run(new AcSweepRequest(1e6, 3e8, 300));
        var trace = sweep.Traces[0];

        var peak = 0;
        for (var i = 1; i < sweep.Frequencies.Count; i++)
            if (trace.Decibels(i) > trace.Decibels(peak)) peak = i;

        return sweep.Frequencies[peak];
    }

    /// <summary>
    /// The thing itself: turn the bias up and the resonance goes up with it, because the
    /// capacitance has come down. This is a radio's tuning control.
    /// </summary>
    [Fact]
    public void RaisingTheBiasRaisesTheResonance()
    {
        var low = ResonanceOf(BuildTank(1.0).Circuit);
        var mid = ResonanceOf(BuildTank(5.0).Circuit);
        var high = ResonanceOf(BuildTank(15.0).Circuit);

        Assert.True(mid > low * 1.2, $"{low / 1e6:0.0} MHz to {mid / 1e6:0.0} MHz is not much of a tune");
        Assert.True(high > mid * 1.2, $"{mid / 1e6:0.0} MHz to {high / 1e6:0.0} MHz is not much of a tune");
    }

    /// <summary>And it lands where 1/(2π√(LC)) says, with C the capacitance the bias gives.</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(5.0)]
    [InlineData(15.0)]
    public void AndLandsWhereTheArithmeticSaysItShould(double bias)
    {
        var tank = BuildTank(bias);
        var measured = ResonanceOf(tank.Circuit);

        // The bias sits almost entirely across the junction: the feed resistor carries only the
        // reverse leakage, so the drop across it is negligible.
        // The blocking capacitor is in series with the junction, which takes about a percent off.
        var junction = tank.Varactor.CapacitanceAt(bias);
        var capacitance = junction * 10e-9 / (junction + 10e-9);
        var expected = 1.0 / (2.0 * Math.PI * Math.Sqrt(10e-6 * capacitance));

        Assert.Equal(expected, measured, expected * 0.05);
    }

    /// <summary>
    /// And the failure everybody meets: let the signal swing the junction forward and the part
    /// stops being a capacitor. It reports that rather than quietly giving a wrong answer.
    /// </summary>
    [Fact]
    public void ItSaysSoWhenTheBiasHasGone()
    {
        var tank = BuildTank(0.0);
        var simulator = new CircuitSimulator(tank.Circuit);

        simulator.Reset();
        simulator.SolveOperatingPoint();

        Assert.True(tank.Varactor.HasLostReverseBias);
    }
}
