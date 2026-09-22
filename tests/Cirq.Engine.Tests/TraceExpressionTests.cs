using Cirq.Core.Primitives;
using Cirq.Core.Probing;

namespace Cirq.Engine.Tests;

/// <summary>
/// Arithmetic on recorded traces. Every answer is checked against the arithmetic it stands for,
/// which is the one thing an expression evaluator can be checked against.
/// </summary>
public class TraceExpressionTests
{
    private static IReadOnlyList<DataPoint> Ramp(double from, double to, int count = 5) =>
        [.. Enumerable.Range(0, count).Select(i =>
            new DataPoint(i * 1e-3, from + ((to - from) * i / (count - 1.0))))];

    private static Dictionary<string, IReadOnlyList<DataPoint>> Traces(
        params (string Name, IReadOnlyList<DataPoint> Samples)[] traces) =>
        traces.ToDictionary(t => t.Name, t => t.Samples, StringComparer.OrdinalIgnoreCase);

    private static double[] Values(IReadOnlyList<DataPoint> points) =>
        [.. points.Select(p => p.Value)];

    // ---- arithmetic --------------------------------------------------------

    [Fact]
    public void ATraceOnItsOwnComesBackUnchanged()
    {
        var traces = Traces(("In", Ramp(0, 4)));

        Assert.Equal([0, 1, 2, 3, 4], Values(TraceExpression.Evaluate("In", traces)));
    }

    [Theory]
    [InlineData("In + 1", new[] { 1.0, 2, 3, 4, 5 })]
    [InlineData("In - 1", new[] { -1.0, 0, 1, 2, 3 })]
    [InlineData("In * 2", new[] { 0.0, 2, 4, 6, 8 })]
    [InlineData("In / 2", new[] { 0.0, 0.5, 1, 1.5, 2 })]
    [InlineData("-In", new[] { 0.0, -1, -2, -3, -4 })]
    [InlineData("In ^ 2", new[] { 0.0, 1, 4, 9, 16 })]
    public void TheOperatorsDoWhatTheySay(string expression, double[] expected)
    {
        var result = Values(TraceExpression.Evaluate(expression, Traces(("In", Ramp(0, 4)))));

        Assert.Equal(expected.Length, result.Length);

        for (var i = 0; i < expected.Length; i++) Assert.Equal(expected[i], result[i], 9);
    }

    /// <summary>Two traces together, which is the point of the feature.</summary>
    [Fact]
    public void TwoTracesCanBeCombined()
    {
        var traces = Traces(("A", Ramp(0, 4)), ("B", Ramp(4, 0)));

        Assert.Equal([4, 4, 4, 4, 4], Values(TraceExpression.Evaluate("A + B", traces)));
        Assert.Equal([-4, -2, 0, 2, 4], Values(TraceExpression.Evaluate("A - B", traces)));
    }

    [Fact]
    public void PrecedenceAndParenthesesWork()
    {
        var traces = Traces(("X", Ramp(2, 2)));

        Assert.Equal(8.0, TraceExpression.Evaluate("X + X * 3", traces)[0].Value, 9);
        Assert.Equal(12.0, TraceExpression.Evaluate("(X + X) * 3", traces)[0].Value, 9);

        // Powers are right associative, so 2^3^2 is 2^9 and not 8^2.
        Assert.Equal(512.0, TraceExpression.Evaluate("X ^ 3 ^ 2", traces)[0].Value, 9);
    }

    [Fact]
    public void NumbersInExponentialFormAreNumbers()
    {
        var traces = Traces(("X", Ramp(1, 1)));

        Assert.Equal(1e-6, TraceExpression.Evaluate("X * 1e-6", traces)[0].Value, 15);
        Assert.Equal(2500.0, TraceExpression.Evaluate("X * 2.5E3", traces)[0].Value, 9);
    }

    [Theory]
    [InlineData("abs(X)", 4.0)]
    [InlineData("sqrt(abs(X))", 2.0)]
    [InlineData("sign(X)", -1.0)]
    [InlineData("db(X)", 12.0411998)]
    public void TheFunctionsWork(string expression, double expected)
    {
        var traces = Traces(("X", Ramp(-4, -4)));

        Assert.Equal(expected, TraceExpression.Evaluate(expression, traces)[0].Value, 6);
    }

    /// <summary>A name with spaces in it goes in braces, because most trace labels have them.</summary>
    [Fact]
    public void ANameWithSpacesGoesInBraces()
    {
        var traces = Traces(("AC in", Ramp(0, 4)), ("DC out", Ramp(4, 8)));

        Assert.Equal([4, 4, 4, 4, 4],
            Values(TraceExpression.Evaluate("{DC out} - {AC in}", traces)));
    }

    [Fact]
    public void NamesAreMatchedWithoutRegardToCase()
    {
        var traces = Traces(("Output", Ramp(1, 1)));

        Assert.Equal(2.0, TraceExpression.Evaluate("output + OUTPUT", traces)[0].Value, 9);
    }

    // ---- the real uses -----------------------------------------------------

    /// <summary>
    /// A ratio of two traces is the commonest expression there is, and it is a gain.
    /// </summary>
    [Fact]
    public void AGainIsARatio()
    {
        var traces = Traces(("In", Ramp(1, 1)), ("Out", Ramp(10, 10)));

        Assert.Equal([10, 10, 10, 10, 10], Values(TraceExpression.Evaluate("Out / In", traces)));
        Assert.Equal(20.0, TraceExpression.Evaluate("db(Out / In)", traces)[0].Value, 6);
    }

    /// <summary>And a power dissipation is a current squared into a resistance.</summary>
    [Fact]
    public void APowerIsACurrentSquaredIntoAResistance()
    {
        var traces = Traces(("I", Ramp(0.1, 0.1)));

        Assert.Equal(2.2, TraceExpression.Evaluate("I ^ 2 * 220", traces)[0].Value, 9);
    }

    /// <summary>
    /// A denominator passing through zero is ordinary — a gain plot with a gap in it is more
    /// useful than one that is all infinity, and NaN is how a gap is drawn.
    /// </summary>
    [Fact]
    public void DividingByZeroLeavesAGapRatherThanInfinity()
    {
        var traces = Traces(("Top", Ramp(1, 1)), ("Bottom", Ramp(-2, 2)));

        var result = Values(TraceExpression.Evaluate("Top / Bottom", traces));

        Assert.Equal(-0.5, result[0], 9);
        Assert.True(double.IsNaN(result[2]), "the middle point divides by zero");
        Assert.Equal(0.5, result[4], 9);
    }

    // ---- traces that do not line up ----------------------------------------

    /// <summary>
    /// The union of the sample times, not the intersection. Traces on one scope share a clock, but
    /// nothing guarantees it, and dropping a point because one input was not sampled there would
    /// quietly shorten the answer.
    /// </summary>
    [Fact]
    public void TracesSampledAtDifferentTimesAreInterpolated()
    {
        var coarse = new List<DataPoint> { new(0, 0), new(2, 20) };
        var fine = new List<DataPoint> { new(0, 0), new(1, 1), new(2, 2) };

        var result = TraceExpression.Evaluate("A + B", Traces(("A", coarse), ("B", fine)));

        // Three points, from the union — and at t=1 the coarse trace is halfway to twenty.
        Assert.Equal(3, result.Count);
        Assert.Equal([0, 1, 2], result.Select(p => p.Time));
        Assert.Equal([0, 11, 22], Values(result));
    }

    [Fact]
    public void WithNothingRecordedThereIsNothingToWorkOut()
    {
        Assert.Empty(TraceExpression.Evaluate("A", Traces(("A", []))));
    }

    // ---- saying what is wrong ----------------------------------------------

    [Theory]
    [InlineData("", "no expression")]
    [InlineData("   ", "no expression")]
    [InlineData("A +", "stops before it is finished")]
    [InlineData("(A", "never closed")]
    [InlineData("A)", "Did not expect")]
    [InlineData("Nope", "no trace called 'Nope'")]
    [InlineData("wibble(A)", "no function called 'wibble'")]
    [InlineData("A $ B", "Did not expect")]
    public void AnExpressionThatCannotWorkSaysWhy(string expression, string expected)
    {
        var problem = TraceExpression.Validate(expression, ["A", "B"]);

        Assert.NotNull(problem);
        Assert.Contains(expected, problem, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An unknown name lists what there is, which is usually enough to see the typo.</summary>
    [Fact]
    public void AnUnknownNameSaysWhatThereIs()
    {
        var problem = TraceExpression.Validate("Outpt", ["Input", "Output"]);

        Assert.NotNull(problem);
        Assert.Contains("Input, Output", problem);
    }

    [Fact]
    public void AGoodExpressionValidatesClean()
    {
        Assert.Null(TraceExpression.Validate("db(Output / Input)", ["Input", "Output"]));
    }

    /// <summary>A caller can ask which traces an expression needs before it evaluates it.</summary>
    [Fact]
    public void TheNamesUsedCanBeListed()
    {
        var names = TraceExpression.NamesIn("Out / In + Out", ["In", "Out", "Spare"]);

        Assert.Equal(["Out", "In", "Out"], names);
    }
}
