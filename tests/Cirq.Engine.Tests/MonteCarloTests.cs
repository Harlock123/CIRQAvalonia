using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// Monte Carlo, checked against what the arithmetic of tolerance says the answer has to be.
/// </summary>
public class MonteCarloTests
{
    /// <summary>A divider of two equal resistors, probed at its midpoint.</summary>
    private static (Circuit, Resistor Top, Resistor Bottom) Divider(
        double tolerance, double supply = 10.0)
    {
        var circuit = new Circuit();
        var source = circuit.Add(new DcVoltageSource(supply));
        var top = circuit.Add(new Resistor(1e3) { Tolerance = tolerance });
        var bottom = circuit.Add(new Resistor(1e3) { Tolerance = tolerance });
        var ground = circuit.Add(new Ground());

        circuit.Connect(source.Negative, ground.Pin);
        circuit.Connect(source.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);

        circuit.Probes.Add(new SignalProbe("Mid", top.B, Color.ProbePalette[0]));

        return (circuit, top, bottom);
    }

    [Fact]
    public void ThePartsThatWillBeVariedAreTheOnesWithATolerance()
    {
        var (circuit, top, bottom) = Divider(0.05);

        bottom.Tolerance = 0;   // an exact part, for whatever reason

        var targets = MonteCarlo.Targets(circuit).ToList();

        var only = Assert.Single(targets);

        Assert.Same(top, only.Component);
        Assert.Equal(1e3, only.Nominal);
        Assert.Equal(0.05, only.Tolerance);
    }

    [Fact]
    public void EveryPartIsPutBackAtItsMarkedValueAfterwards()
    {
        var (circuit, top, bottom) = Divider(0.05);

        new MonteCarlo(circuit).Run(new MonteCarloRequest(Trials: 50));

        Assert.Equal(1e3, top.Resistance);
        Assert.Equal(1e3, bottom.Resistance);
    }

    [Fact]
    public void TheNominalAnswerIsTheOneWithEveryPartAtItsMarkedValue()
    {
        var (circuit, _, _) = Divider(0.05);

        var result = new MonteCarlo(circuit).Run(new MonteCarloRequest(Trials: 100));

        var trace = Assert.Single(result.Traces);

        // Two equal resistors across ten volts: five, with no tolerance applied. Not to the last
        // bit — every node carries a picosiemens of gmin to ground, which pulls it down by a
        // couple of nanovolts — but to very much better than any tolerance matters at.
        Assert.Equal(5.0, trace.Nominal, 7);
    }

    /// <summary>
    /// The arithmetic the whole analysis rests on. A divider's output cannot be further out than
    /// the two resistors' tolerances allow, and for equal resistors at ±t the extreme is
    /// V·(1+t)/((1+t)+(1−t)) — which for five percent is 5.25 V, not 5.5.
    /// </summary>
    [Fact]
    public void TheSpreadStaysInsideWhatTheToleranceArithmeticAllows()
    {
        const double tolerance = 0.05;

        var (circuit, _, _) = Divider(tolerance);

        var result = new MonteCarlo(circuit).Run(new MonteCarloRequest(Trials: 2000));

        var trace = result.Traces[0];

        var worst = 10.0 * (1 + tolerance) / ((1 + tolerance) + (1 - tolerance));

        Assert.InRange(trace.Maximum, 5.0, worst + 1e-9);
        Assert.InRange(trace.Minimum, 10.0 - worst - 1e-9, 5.0);

        // And it gets near the extremes, rather than huddling round the middle.
        Assert.True(trace.Maximum > 5.2, $"the highest of 2000 trials was only {trace.Maximum:F3}");
    }

    [Fact]
    public void ADividerOfExactPartsHasNoSpreadAtAll()
    {
        var (circuit, _, _) = Divider(0.0);

        var result = new MonteCarlo(circuit).Run(new MonteCarloRequest(Trials: 50));

        // Nothing has a tolerance, so there is nothing to vary and nothing to report.
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void AWiderToleranceGivesAWiderSpread()
    {
        double Spread(double tolerance)
        {
            var (circuit, _, _) = Divider(tolerance);
            var result = new MonteCarlo(circuit).Run(new MonteCarloRequest(Trials: 500));

            return result.Traces[0].Maximum - result.Traces[0].Minimum;
        }

        var tight = Spread(0.01);
        var loose = Spread(0.20);

        Assert.True(loose > tight * 5, $"20 % gave {loose:F3} V against 1 % giving {tight:F3} V");
    }

    /// <summary>An analysis whose answer changes every time you look at it decides nothing.</summary>
    [Fact]
    public void TheSameSeedGivesTheSameAnswerAndADifferentSeedDoesNot()
    {
        double[] Readings(int seed)
        {
            var (circuit, _, _) = Divider(0.05);
            var result = new MonteCarlo(circuit).Run(new MonteCarloRequest(200, seed));

            return [.. result.Traces[0].Values];
        }

        Assert.Equal(Readings(7), Readings(7));
        Assert.NotEqual(Readings(7), Readings(8));
    }

    [Fact]
    public void TheWorstErrorIsReportedAsAFractionOfNominal()
    {
        var (circuit, _, _) = Divider(0.10);

        var trace = new MonteCarlo(circuit).Run(new MonteCarloRequest(1000)).Traces[0];

        // Equal resistors at ±10 % can put the midpoint 10 % out at the very worst.
        Assert.InRange(trace.WorstFractionalError, 0.05, 0.11);
    }

    [Fact]
    public void TheYieldQuestionCanBeAskedTheOtherWayUp()
    {
        var (circuit, _, _) = Divider(0.10);

        var trace = new MonteCarlo(circuit).Run(new MonteCarloRequest(1000)).Traces[0];

        // A band wider than anything the parts can do catches nothing; a very tight one catches
        // most of them. That ordering is the whole of what the figure means.
        Assert.Equal(0, trace.OutsideBand(0.5));
        Assert.True(trace.OutsideBand(0.01) > 500);
    }

    [Fact]
    public void TheHistogramBucketsEveryReadingExactlyOnce()
    {
        var (circuit, _, _) = Divider(0.05);

        var trace = new MonteCarlo(circuit).Run(new MonteCarloRequest(500)).Traces[0];

        var histogram = trace.Histogram(20);

        Assert.Equal(20, histogram.Length);
        Assert.Equal(trace.Values.Count, histogram.Sum(b => b.Count));

        // Bucket centres climb from the lowest reading to the highest.
        Assert.True(histogram[0].Centre < histogram[^1].Centre);
    }

    /// <summary>
    /// The case the analysis is really for: a circuit where the tolerances matter much more than
    /// their face value suggests, because the answer is a difference between two large numbers.
    /// </summary>
    [Fact]
    public void ADifferenceOfTwoNearlyEqualTermsAmplifiesTheTolerance()
    {
        var circuit = new Circuit();
        var source = circuit.Add(new DcVoltageSource(10.0));

        // Two dividers, both nominally at half the rail, with the answer the gap between them.
        var a1 = circuit.Add(new Resistor(1e3) { Tolerance = 0.05 });
        var a2 = circuit.Add(new Resistor(1e3) { Tolerance = 0.05 });
        var b1 = circuit.Add(new Resistor(1e3) { Tolerance = 0.05 });
        var b2 = circuit.Add(new Resistor(1e3) { Tolerance = 0.05 });
        var ground = circuit.Add(new Ground());

        circuit.Connect(source.Negative, ground.Pin);

        foreach (var (top, bottom) in new[] { (a1, a2), (b1, b2) })
        {
            circuit.Connect(source.Positive, top.A);
            circuit.Connect(top.B, bottom.A);
            circuit.Connect(bottom.B, ground.Pin);
        }

        circuit.Probes.Add(new SignalProbe("Bridge", a1.B, Color.ProbePalette[0])
        {
            Kind = ProbeKind.Differential,
            ReferenceTerminal = b1.B,
        });

        var trace = new MonteCarlo(circuit).Run(new MonteCarloRequest(1000)).Traces[0];

        // Nominally the two midpoints are identical and the bridge reads nothing at all.
        Assert.Equal(0.0, trace.Nominal, 9);

        // In practice it reads hundreds of millivolts, in either direction — which is why a real
        // bridge is trimmed rather than built from marked parts and hoped over.
        Assert.True(trace.Maximum > 0.2, $"the bridge only reached {trace.Maximum:F3} V");
        Assert.True(trace.Minimum < -0.2, $"the bridge only reached {trace.Minimum:F3} V");
    }
}
