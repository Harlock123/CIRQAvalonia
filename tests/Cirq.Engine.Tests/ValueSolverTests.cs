using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// Working backwards: what value of this part gives that reading.
/// <para>
/// Every answer is checked against the value the arithmetic says it should be, because a divider
/// can be solved on paper — which is the point of testing it on a divider.
/// </para>
/// </summary>
public class ValueSolverTests
{
    private static (CircuitSimulator Sim, Resistor Top, Resistor Bottom, SignalProbe Probe) Divider()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(10.0));
        var top = circuit.Add(new Resistor(10e3) { Name = "R1" });
        var bottom = circuit.Add(new Resistor(10e3) { Name = "R2" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);

        var probe = new SignalProbe("Mid", bottom.A, default);
        circuit.Probes.Add(probe);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.ResolveProbes();

        return (sim, top, bottom, probe);
    }

    // ---- the closed form ---------------------------------------------------

    /// <summary>
    /// Two volts out of ten across a divider needs the bottom resistor to be a quarter of the top:
    /// 10 × R2/(10k + R2) = 2 gives R2 = 2.5 kΩ.
    /// </summary>
    [Fact]
    public void ItFindsTheValueTheArithmeticSays()
    {
        var (sim, _, bottom, probe) = Divider();

        var result = new ValueSolver(sim).Run(new ValueSearch(
            new SweepTarget(bottom, nameof(Resistor.Resistance), 100, 100e3),
            probe,
            Target: 2.0));

        Assert.True(result.IsUsable, result.Problem);
        Assert.Equal(2.5e3, result.Value, 2.5e3 * 0.001);
        Assert.Equal(2.0, result.Achieved, 0.001);
    }

    /// <summary>It works on whichever part is varied, not only the one nearest the probe.</summary>
    [Fact]
    public void ItCanSearchTheOtherResistorToo()
    {
        var (sim, top, _, probe) = Divider();

        // 10 × 10k/(R1 + 10k) = 8 gives R1 = 2.5 kΩ.
        var result = new ValueSolver(sim).Run(new ValueSearch(
            new SweepTarget(top, nameof(Resistor.Resistance), 100, 100e3),
            probe,
            Target: 8.0));

        Assert.True(result.IsUsable, result.Problem);
        Assert.Equal(2.5e3, result.Value, 2.5e3 * 0.001);
    }

    /// <summary>
    /// The answer it gives is also a value you can buy. 2.5 kΩ is not an E24 part; 2.4 kΩ is, and
    /// the window says what that one actually achieves so the compromise is visible.
    /// </summary>
    [Fact]
    public void ItAlsoGivesTheNearestPartYouCanBuy()
    {
        var (sim, _, bottom, probe) = Divider();

        var result = new ValueSolver(sim).Run(new ValueSearch(
            new SweepTarget(bottom, nameof(Resistor.Resistance), 100, 100e3), probe, Target: 2.0));

        Assert.NotNull(result.Nearest);
        Assert.Equal(2.4e3, result.Nearest.Value, 1e-6);

        // 10 × 2.4k/12.4k = 1.935 V, which is about three percent off.
        Assert.NotNull(result.NearestAchieved);
        Assert.Equal(10.0 * 2.4 / 12.4, result.NearestAchieved.Value, 0.002);

        Assert.InRange(result.NearestError, 0.02, 0.05);
    }

    /// <summary>Bisection converges in a couple of dozen solves, not hundreds.</summary>
    [Fact]
    public void ItConvergesInAHandfulOfSolves()
    {
        var (sim, _, bottom, probe) = Divider();

        var result = new ValueSolver(sim).Run(new ValueSearch(
            new SweepTarget(bottom, nameof(Resistor.Resistance), 100, 1e6), probe, Target: 2.0));

        Assert.True(result.IsUsable, result.Problem);
        Assert.True(result.Steps < 40, $"{result.Steps} solves is more than it should need");
    }

    [Fact]
    public void TheParameterGoesBackWhereItWas()
    {
        var (sim, _, bottom, probe) = Divider();

        new ValueSolver(sim).Run(new ValueSearch(
            new SweepTarget(bottom, nameof(Resistor.Resistance), 100, 100e3), probe, Target: 2.0));

        Assert.Equal(10e3, bottom.Resistance, 6);
    }

    // ---- saying why there is no answer -------------------------------------

    /// <summary>
    /// A target the range cannot reach is reported with the range it <i>can</i> reach, which tells
    /// you which way to widen it — far more use than "not found".
    /// </summary>
    [Fact]
    public void ATargetOutsideTheRangeSaysWhatTheRangeCanDo()
    {
        var (sim, _, bottom, probe) = Divider();

        var result = new ValueSolver(sim).Run(new ValueSearch(
            new SweepTarget(bottom, nameof(Resistor.Resistance), 100, 1e3), probe, Target: 9.0));

        Assert.False(result.IsUsable);
        Assert.Contains("Widen the range", result.Problem!);
        Assert.Contains("runs from", result.Problem!);
    }

    [Fact]
    public void ARangeWithNoWidthIsRefused()
    {
        var (sim, _, bottom, probe) = Divider();

        var result = new ValueSolver(sim).Run(new ValueSearch(
            new SweepTarget(bottom, nameof(Resistor.Resistance), 1e3, 1e3), probe, Target: 2.0));

        Assert.False(result.IsUsable);
        Assert.Contains("no width", result.Problem!);
    }

    [Fact]
    public void APropertyThatIsNotAWritableNumberIsRefused()
    {
        var (sim, _, bottom, probe) = Divider();

        var result = new ValueSolver(sim).Run(new ValueSearch(
            new SweepTarget(bottom, "NotAProperty", 1, 2), probe, Target: 1.0));

        Assert.False(result.IsUsable);
        Assert.Contains("NotAProperty", result.Problem!);
    }

    // ---- the preferred values ----------------------------------------------

    /// <summary>
    /// E24 rounds in the logarithm, because the series is geometric: between 8.2 and 9.1 the
    /// midpoint is 8.64, not 8.65, and a linear round would put 8.6 on the wrong side of it.
    /// </summary>
    [Theory]
    [InlineData(2500.0, 2400.0)]
    [InlineData(1000.0, 1000.0)]
    [InlineData(4700.0, 4700.0)]
    [InlineData(67300.0, 68000.0)]
    [InlineData(0.047, 0.047)]
    [InlineData(9.8e6, 1e7)]
    public void ThePreferredValueIsTheNearestOneInTheLogarithm(double value, double expected)
    {
        Assert.Equal(expected, PreferredValue.Nearest(value), expected * 1e-9);
    }

    /// <summary>Every E24 value is its own nearest, at every decade.</summary>
    [Fact]
    public void EveryPreferredValueIsItsOwnNearest()
    {
        foreach (var decade in new[] { 1e-9, 1e-3, 1.0, 1e3, 1e6 })
        {
            foreach (var step in PreferredValue.E24)
            {
                var value = step * decade;

                Assert.Equal(value, PreferredValue.Nearest(value), value * 1e-9);
            }
        }
    }

    [Fact]
    public void ANonsenseValueComesBackUnchangedRatherThanThrowing()
    {
        Assert.Equal(0, PreferredValue.Nearest(0));
        Assert.Equal(-5, PreferredValue.Nearest(-5));
        Assert.True(double.IsNaN(PreferredValue.Nearest(double.NaN)));
    }
}
