using Cirq.Components.Ics;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// What a circuit looks like from one pair of points.
/// <para>
/// Every impedance here has a closed form, because that is the only way to know the sweep is
/// right: a resistor is R at every frequency, a capacitor is 1/ωC, a divider seen from its tap is
/// the two in parallel, and a tuned circuit peaks or dips at 1/2π√(LC). Nothing is compared
/// against a previous run.
/// </para>
/// </summary>
public class ImpedanceTests
{
    private static CircuitSimulator Solved(Circuit circuit)
    {
        var sim = new CircuitSimulator(circuit);

        sim.Reset();
        sim.SolveOperatingPoint();
        sim.ResolveProbes();

        return sim;
    }

    private static ImpedanceResult Look(
        CircuitSimulator sim, Terminal at, Terminal? against = null,
        double start = 10, double stop = 1e6, int perDecade = 20) =>
        new ImpedanceAnalysis(sim).Run(
            new ImpedanceRequest(new AcSweepRequest(start, stop, perDecade), at, against));

    /// <summary>A resistor to ground is that many ohms, at every frequency, with no phase.</summary>
    [Fact]
    public void AResistorIsItsOwnValueAtEveryFrequency()
    {
        var circuit = new Circuit();

        var resistor = circuit.Add(new Resistor(4.7e3) { Name = "R1" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(resistor.B, ground.Pin);

        var result = Look(Solved(circuit), resistor.A);

        Assert.True(result.IsUsable);

        for (var i = 0; i < result.Frequencies.Count; i++)
        {
            Assert.Equal(4700.0, result.Ohms(i), 6);
            Assert.Equal(0.0, result.Degrees(i), 6);
        }
    }

    /// <summary>
    /// A capacitor is 1/ωC and ninety degrees the capacitive way, and the reactance read back as a
    /// component gives the capacitor you started with.
    /// </summary>
    [Fact]
    public void ACapacitorIsOneOverOmegaC()
    {
        var circuit = new Circuit();

        var capacitor = circuit.Add(new Capacitor(100e-9) { Name = "C1" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(capacitor.B, ground.Pin);

        var result = Look(Solved(circuit), capacitor.A, start: 100, stop: 1e5);

        for (var i = 0; i < result.Frequencies.Count; i++)
        {
            var expected = 1.0 / (2.0 * Math.PI * result.Frequencies[i] * 100e-9);

            Assert.Equal(expected, result.Ohms(i), expected * 1e-9);
            Assert.Equal(-90.0, result.Degrees(i), 6);

            var (value, unit) = result.Equivalent(i)!.Value;

            Assert.Equal("F", unit);
            Assert.Equal(100e-9, value, 1e-15);
        }
    }

    /// <summary>
    /// A divider seen from its tap is its two resistors in parallel. The supply is a short for
    /// small signals, which is the whole of why a Thévenin resistance is what it is — and is also
    /// the thing people get wrong by hand.
    /// </summary>
    [Fact]
    public void ADividerLooksLikeItsResistorsInParallel()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(10.0) { Name = "V1" });
        var top = circuit.Add(new Resistor(6.8e3) { Name = "R1" });
        var bottom = circuit.Add(new Resistor(3.3e3) { Name = "R2" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);

        var result = Look(Solved(circuit), top.B);

        var parallel = 1.0 / ((1.0 / 6.8e3) + (1.0 / 3.3e3));

        Assert.Equal(parallel, result.Ohms(0), parallel * 1e-9);
        Assert.Equal(parallel, result.Ohms(result.Frequencies.Count - 1), parallel * 1e-9);
    }

    /// <summary>
    /// A series RC: R at the top of the band, rising as 1/ωC below it, and exactly R√2 at the
    /// corner where the two are equal — which is the same corner a filter made of them turns at.
    /// </summary>
    [Fact]
    public void ASeriesRcCrossesAtItsOwnCorner()
    {
        var circuit = new Circuit();

        var resistor = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var capacitor = circuit.Add(new Capacitor(100e-9) { Name = "C1" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(resistor.B, capacitor.A);
        circuit.Connect(capacitor.B, ground.Pin);

        var corner = 1.0 / (2.0 * Math.PI * 1e3 * 100e-9);

        var result = Look(Solved(circuit), resistor.A, start: corner, stop: corner * 10, perDecade: 1);

        Assert.Equal(1e3 * Math.Sqrt(2.0), result.Ohms(0), 1e-6);
        Assert.Equal(-45.0, result.Degrees(0), 6);
    }

    /// <summary>
    /// A parallel tuned circuit peaks at 1/2π√(LC), and the peak is the loss resistance across it.
    /// The reactance falls through zero there — inductive below, capacitive above — which is what
    /// makes it a <b>parallel</b> resonance.
    /// </summary>
    [Fact]
    public void AParallelTunedCircuitPeaksAtItsResonance()
    {
        var circuit = new Circuit();

        var inductor = circuit.Add(new Inductor(100e-6) { Name = "L1", SeriesResistance = 0 });
        var capacitor = circuit.Add(new Capacitor(1e-9) { Name = "C1" });
        var damping = circuit.Add(new Resistor(10e3) { Name = "R1" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(inductor.A, capacitor.A);
        circuit.Connect(inductor.A, damping.A);
        circuit.Connect(inductor.B, ground.Pin);
        circuit.Connect(capacitor.B, ground.Pin);
        circuit.Connect(damping.B, ground.Pin);

        var expected = 1.0 / (2.0 * Math.PI * Math.Sqrt(100e-6 * 1e-9));

        var result = Look(Solved(circuit), inductor.A, start: expected / 10, stop: expected * 10, perDecade: 400);

        var resonance = Assert.Single(result.Resonances);

        Assert.False(resonance.Series);
        Assert.Equal(expected, resonance.Hertz, expected * 1e-3);

        // At resonance the two reactances cancel and only the damping resistor is left.
        var peak = result.Maximum!.Value;

        Assert.Equal(expected, peak.Hertz, expected * 1e-2);
        Assert.Equal(10e3, peak.Ohms, 10.0);
    }

    /// <summary>
    /// A capacitor in series with its own parasitic inductance is a capacitor below its series
    /// resonance and an inductor above it, which is why a decoupling capacitor stops working long
    /// before the frequency people assume. The dip is the equivalent series resistance.
    /// </summary>
    [Fact]
    public void ADecouplingCapacitorStopsBeingOneAboveItsSeriesResonance()
    {
        var circuit = new Circuit();

        var capacitor = circuit.Add(new Capacitor(100e-9) { Name = "C1" });
        var parasitic = circuit.Add(new Inductor(5e-9) { Name = "L1", SeriesResistance = 0 });
        var esr = circuit.Add(new Resistor(0.05) { Name = "R1" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(capacitor.B, parasitic.A);
        circuit.Connect(parasitic.B, esr.A);
        circuit.Connect(esr.B, ground.Pin);

        var expected = 1.0 / (2.0 * Math.PI * Math.Sqrt(5e-9 * 100e-9));

        var result = Look(Solved(circuit), capacitor.A, start: 1e4, stop: 1e9, perDecade: 200);

        var resonance = Assert.Single(result.Resonances);

        Assert.True(resonance.Series);
        Assert.Equal(expected, resonance.Hertz, expected * 1e-2);

        // Everything cancels at the dip but the series resistance.
        var dip = result.Minimum!.Value;

        Assert.Equal(0.05, dip.Ohms, 0.002);

        // And above it the part is an inductor: the reactance reads back in henries.
        var last = result.Frequencies.Count - 1;
        var (value, unit) = result.Equivalent(last)!.Value;

        Assert.Equal("H", unit);
        Assert.Equal(5e-9, value, 1e-12);
    }

    /// <summary>
    /// Feedback divides the output impedance by the loop gain, so a follower's output looks like
    /// a fraction of a milliohm at DC and climbs as the loop runs out of gain. Above the loop's
    /// crossover there is no feedback left and what is left is the op-amp's own output resistance.
    /// </summary>
    [Fact]
    public void FeedbackDividesAFollowersOutputImpedance()
    {
        var circuit = new Circuit();

        var positive = circuit.Add(new DcVoltageSource(15.0) { Name = "V1" });
        var negative = circuit.Add(new DcVoltageSource(-15.0) { Name = "V2" });
        var amp = circuit.Add(new OperationalAmplifier { Name = "U1", Model = OpAmpModel.Lm741 });
        var ground = circuit.Add(new Ground());

        circuit.Connect(positive.Negative, ground.Pin);
        circuit.Connect(negative.Positive, ground.Pin);
        circuit.Connect(amp.PositiveSupply, positive.Positive);
        circuit.Connect(amp.NegativeSupply, negative.Negative);

        circuit.Connect(amp.NonInverting, ground.Pin);
        circuit.Connect(amp.Output, amp.Inverting);

        var model = OpAmpModel.Lm741;

        var result = Look(Solved(circuit), amp.Output, start: 1, stop: 1e8, perDecade: 40);

        // Zout = rout / (1 + Aol), with the whole output fed back so β is one.
        var expected = model.OutputResistance / (1.0 + model.OpenLoopGain);

        Assert.Equal(expected, result.Ohms(0), expected * 0.05);

        // Well past the gain-bandwidth product the loop has nothing left, and the bare output
        // resistance is what a load sees.
        var last = result.Frequencies.Count - 1;

        Assert.Equal(model.OutputResistance, result.Ohms(last), model.OutputResistance * 0.05);

        // And it climbs all the way between the two, which is the part that surprises people.
        Assert.True(result.Ohms(last) > result.Ohms(0) * 1e4);
    }

    /// <summary>Probing a point against itself is a short, and says so rather than dividing by it.</summary>
    [Fact]
    public void ItRefusesAProbeWithBothEndsOnOneNet()
    {
        var circuit = new Circuit();

        var resistor = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(resistor.B, ground.Pin);

        var sim = Solved(circuit);

        Assert.False(Look(sim, resistor.A, resistor.A).IsUsable);
        Assert.False(Look(sim, resistor.B, ground.Pin).IsUsable);
    }
}
