using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Verification;

namespace Cirq.Engine.Tests;

/// <summary>
/// The measurements that need two traces: propagation delay, skew, setup and hold.
/// <para>
/// Every waveform here is built from a formula with the answer put into it, so the expected figure
/// is arithmetic rather than a recording. A square wave delayed by a known amount has that delay;
/// if this says otherwise it is this that is wrong.
/// </para>
/// </summary>
public class TraceTimingTests
{
    /// <summary>A square wave, optionally shifted in time and optionally inverted.</summary>
    private static List<DataPoint> Square(
        double hertz = 1e6, double shift = 0, bool invert = false,
        double duration = 10e-6, int count = 20_000)
    {
        List<DataPoint> points = [];

        for (var i = 0; i < count; i++)
        {
            var t = duration * i / (count - 1.0);
            var phase = (t - shift) * hertz;

            phase -= Math.Floor(phase);

            var high = phase < 0.5;

            points.Add(new DataPoint(t, (high != invert) ? 1.0 : 0.0));
        }

        return points;
    }

    /// <summary>A twenty nanosecond delay measures as twenty nanoseconds.</summary>
    [Theory]
    [InlineData(20e-9)]
    [InlineData(75e-9)]
    [InlineData(150e-9)]
    public void APropagationDelayIsTheDelayThatWasPutIn(double delay)
    {
        var input = Square();
        var output = Square(shift: delay);

        var measured = TraceTiming.PropagationDelay(input, output);

        Assert.NotNull(measured);
        Assert.Equal(delay, measured.Value, 2e-9);
    }

    /// <summary>
    /// And an inverting gate's delay is the same delay. A NAND's output moves the other way from
    /// its input, and the time between them is still the time between them — which is why the
    /// default times from any edge to any edge rather than insisting they match.
    /// </summary>
    [Fact]
    public void AnInvertingStageIsTimedTheSameWay()
    {
        var measured = TraceTiming.PropagationDelay(Square(), Square(shift: 40e-9, invert: true));

        Assert.NotNull(measured);
        Assert.Equal(40e-9, measured.Value, 2e-9);
    }

    /// <summary>Asked for one direction only, it waits for an edge going that way.</summary>
    [Fact]
    public void ADirectionCanBeInsistedOn()
    {
        var input = Square();
        var output = Square(shift: 30e-9);

        var rising = TraceTiming.PropagationDelay(input, output, EdgeDirection.Rising);
        var falling = TraceTiming.PropagationDelay(input, output, EdgeDirection.Falling);

        Assert.Equal(30e-9, rising!.Value, 2e-9);
        Assert.Equal(30e-9, falling!.Value, 2e-9);
    }

    /// <summary>
    /// A trace that never moves has no edges, so there is nothing to time from and the answer is
    /// that rather than zero. Zero is a delay somebody would believe.
    /// </summary>
    [Fact]
    public void NoEdgeMeansNoAnswerRatherThanZero()
    {
        List<DataPoint> flat = [.. Enumerable.Range(0, 1000).Select(i => new DataPoint(i * 1e-9, 1.0))];

        Assert.Null(TraceTiming.PropagationDelay(flat, Square()));
        Assert.Null(TraceTiming.PropagationDelay(Square(), flat));
        Assert.Null(TraceTiming.Skew(flat, Square()));
    }

    /// <summary>
    /// Noise on a trace does not become a measurement of the noise. Without hysteresis a signal
    /// with a wobble on it crosses its midpoint many times per edge, and the first "edge" found is
    /// whichever way the noise happened to go.
    /// </summary>
    [Fact]
    public void NoiseDoesNotInventEdges()
    {
        var random = new Random(1);

        List<DataPoint> noisy = [.. Square().Select(p =>
            new DataPoint(p.Time, p.Value + ((random.NextDouble() - 0.5) * 0.15)))];

        var clean = Square(shift: 50e-9);

        var measured = TraceTiming.PropagationDelay(noisy, clean);

        Assert.NotNull(measured);
        Assert.Equal(50e-9, measured.Value, 5e-9);
    }

    /// <summary>
    /// Skew is the worst pairing across the capture rather than the first, because it is a limit
    /// and a limit is about the worst case.
    /// </summary>
    [Fact]
    public void SkewIsTheWorstPairing()
    {
        var measured = TraceTiming.Skew(Square(), Square(shift: 12e-9));

        Assert.NotNull(measured);
        Assert.Equal(12e-9, measured.Value, 2e-9);
    }

    /// <summary>
    /// A small skew stays small however the capture happens to be cut, which is the whole reason
    /// the pairing is what it is.
    /// <para>
    /// Both simpler rules fail one of these. Pairing purely by nearest lets an edge whose partner
    /// is past the end of the capture pair with the previous cycle's, so a twelve nanosecond skew
    /// reads as very nearly a whole period. Pairing purely in order fails when the shift puts the
    /// second trace in the opposite state at t=0, giving it one extra leading edge and putting
    /// every pair after that a cycle out. These shifts were chosen to produce both.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(12e-9)]
    [InlineData(-12e-9)]
    [InlineData(40e-9)]
    [InlineData(-250e-9)]
    public void ASmallSkewStaysSmallHoweverTheCaptureIsCut(double shift)
    {
        var measured = TraceTiming.Skew(Square(), Square(shift: shift));

        Assert.NotNull(measured);
        Assert.Equal(Math.Abs(shift), measured.Value, 3e-9);
    }

    /// <summary>Two traces moving together have no skew worth the name.</summary>
    [Fact]
    public void TracesThatMoveTogetherHaveNoSkew()
    {
        var measured = TraceTiming.Skew(Square(), Square());

        Assert.NotNull(measured);
        Assert.True(measured.Value < 1e-9, $"identical traces are not {measured.Value} apart");
    }

    /// <summary>
    /// Data that settles a known time before each clock edge has that setup time — and the hold
    /// time is what is left of the period.
    /// </summary>
    [Fact]
    public void SetupAndHoldAreTheTwoHalvesOfTheDataWindow()
    {
        // Data at a megahertz, clock at the same rate shifted a quarter period later: the data
        // settles 250 ns before each clock edge and changes 750 ns after it.
        var data = Square(hertz: 1e6);
        var clock = Square(hertz: 1e6, shift: 250e-9);

        var setup = TraceTiming.SetupTime(data, clock);
        var hold = TraceTiming.HoldTime(data, clock);

        Assert.NotNull(setup);
        Assert.NotNull(hold);

        Assert.Equal(250e-9, setup.Value, 10e-9);
        Assert.Equal(250e-9, hold.Value, 10e-9);
    }

    /// <summary>
    /// Setup is the tightest one that occurred, not the average. A part demands a minimum, so what
    /// matters is the closest the design ever came to breaking it.
    /// </summary>
    [Fact]
    public void SetupIsTheTightestThatOccurred()
    {
        // A clock at a megahertz and data at a third of that: the data's edges land at varying
        // distances before the clock's, and the answer has to be the smallest of them.
        var data = Square(hertz: 333e3);
        var clock = Square(hertz: 1e6, shift: 30e-9);

        var setup = TraceTiming.SetupTime(data, clock);

        Assert.NotNull(setup);

        // Every gap the measurement could have returned, worked out independently.
        var gaps = new List<double>();

        foreach (var edge in TraceTiming.Edges(clock).Where(e => e.Rising).Select(e => e.Time))
        {
            var last = TraceTiming.Edges(data)
                .Select(e => e.Time)
                .Where(t => t <= edge)
                .DefaultIfEmpty(double.NaN)
                .Max();

            if (!double.IsNaN(last)) gaps.Add(edge - last);
        }

        Assert.Equal(gaps.Min(), setup.Value, 1e-12);
    }

    // ---- through a requirement ---------------------------------------------

    private static Func<string, IReadOnlyList<DataPoint>?> Traces(
        params (string Name, IReadOnlyList<DataPoint> Samples)[] traces) =>
        name => traces.FirstOrDefault(t => t.Name == name).Samples;

    /// <summary>A delay requirement is checked like any other, once it says what it is against.</summary>
    [Fact]
    public void ADelayRequirementIsChecked()
    {
        var samples = Traces(("in", Square()), ("out", Square(shift: 40e-9)));

        var spec = new DesignSpec
        {
            Name = "Gate delay",
            Trace = "out",
            Against = "in",
            Quantity = SpecQuantity.PropagationDelay,
            Comparison = SpecComparison.AtMost,
            Limit = 50e-9,
            Unit = "s",
        };

        Assert.True(SpecCheck.EvaluateFrom(spec, samples).Passed);

        spec.Limit = 30e-9;

        Assert.False(SpecCheck.EvaluateFrom(spec, samples).Passed);
    }

    /// <summary>
    /// A requirement that needs two traces and names one says so, rather than failing. Naming only
    /// half of a measurement is a requirement that is not finished, not a circuit that is wrong.
    /// </summary>
    [Fact]
    public void ARequirementMissingItsSecondTraceSaysSo()
    {
        var result = SpecCheck.EvaluateFrom(
            new DesignSpec
            {
                Trace = "out",
                Quantity = SpecQuantity.PropagationDelay,
                Limit = 50e-9,
            },
            Traces(("out", Square())));

        Assert.Null(result.Passed);
        Assert.Contains("names only one", result.Explanation, StringComparison.Ordinal);
    }

    /// <summary>And one pointing at a trace that is not there says which.</summary>
    [Fact]
    public void ARequirementPointingAtNothingSaysWhich()
    {
        var result = SpecCheck.EvaluateFrom(
            new DesignSpec
            {
                Trace = "out",
                Against = "clk",
                Quantity = SpecQuantity.SetupTime,
                Limit = 1e-9,
            },
            Traces(("out", Square())));

        Assert.Null(result.Passed);
        Assert.Contains("clk", result.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sentence a paired requirement reads back as names both traces, because "out propagation
    /// delay" does not say what it is a delay from.
    /// </summary>
    [Fact]
    public void ThePairedSentenceNamesBothTraces()
    {
        var spec = new DesignSpec
        {
            Trace = "out",
            Against = "in",
            Quantity = SpecQuantity.PropagationDelay,
            Comparison = SpecComparison.AtMost,
            Limit = 50e-9,
            Unit = "s",
        };

        Assert.Equal("out propagation delay from in at most 50ns", spec.Describe());
    }

    /// <summary>Single-trace requirements still work through the samples path, unchanged.</summary>
    [Fact]
    public void TheSamplesPathStillHandlesOrdinaryRequirements()
    {
        var spec = new DesignSpec
        {
            Trace = "out",
            Quantity = SpecQuantity.PeakToPeak,
            Comparison = SpecComparison.AtMost,
            Limit = 2.0,
            Unit = "V",
        };

        Assert.True(SpecCheck.EvaluateFrom(spec, Traces(("out", Square()))).Passed);
    }
}
