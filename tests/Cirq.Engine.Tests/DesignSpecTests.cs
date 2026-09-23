using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Verification;

namespace Cirq.Engine.Tests;

/// <summary>
/// Requirements written down so they can be checked, rather than remembered.
/// <para>
/// The arithmetic is trivial; what matters is the three-way answer. A requirement is met, or it
/// is not met, or there was nothing to measure — and the third is not the second. A design that
/// has not been shown to break a requirement has not broken it, and reporting "failed" for a
/// frequency that needed two cycles and got one would be the kind of wrong that gets ignored.
/// </para>
/// </summary>
public class DesignSpecTests
{
    private static TraceMeasurements Sine(double amplitude, double offset, double hertz, double cycles = 8)
    {
        List<DataPoint> samples = [];

        var period = 1.0 / hertz;
        var step = period / 200.0;

        for (var t = 0.0; t <= cycles * period; t += step)
            samples.Add(new DataPoint(t, offset + (amplitude * Math.Sin(2 * Math.PI * hertz * t))));

        return TraceMeasurements.OfAll(samples);
    }

    [Fact]
    public void AtMostPassesBelowItsLimitAndFailsAbove()
    {
        var spec = new DesignSpec
        {
            Name = "Ripple",
            Trace = "Rail",
            Quantity = SpecQuantity.PeakToPeak,
            Comparison = SpecComparison.AtMost,
            Limit = 0.05,
        };

        // A hundred millivolts peak to peak, against a fifty millivolt limit.
        var tooBig = SpecCheck.Evaluate(spec, Sine(0.05, 5.0, 100));

        Assert.False(tooBig.Passed);
        Assert.Equal(0.1, tooBig.Measured!.Value, 1e-3);
        Assert.Contains("not met", tooBig.Explanation);

        // Twenty, which is inside it.
        var small = SpecCheck.Evaluate(spec, Sine(0.01, 5.0, 100));

        Assert.True(small.Passed);
        Assert.Contains("met", small.Explanation);
    }

    [Fact]
    public void AtLeastIsTheOtherWayRound()
    {
        var spec = new DesignSpec
        {
            Trace = "Out",
            Quantity = SpecQuantity.PeakToPeak,
            Comparison = SpecComparison.AtLeast,
            Limit = 10.0,
        };

        Assert.True(SpecCheck.Evaluate(spec, Sine(6.0, 0, 1e3)).Passed);
        Assert.False(SpecCheck.Evaluate(spec, Sine(4.0, 0, 1e3)).Passed);
    }

    /// <summary>A regulated output is a band, not a ceiling: too low fails as surely as too high.</summary>
    [Fact]
    public void WithinFailsOnBothSides()
    {
        var spec = new DesignSpec
        {
            Trace = "Out",
            Quantity = SpecQuantity.Mean,
            Comparison = SpecComparison.Within,
            Limit = 5.0,
            Tolerance = 0.1,
        };

        Assert.True(SpecCheck.Evaluate(spec, Sine(0.01, 5.05, 100)).Passed);
        Assert.False(SpecCheck.Evaluate(spec, Sine(0.01, 5.30, 100)).Passed);
        Assert.False(SpecCheck.Evaluate(spec, Sine(0.01, 4.70, 100)).Passed);
    }

    /// <summary>
    /// A requirement pointed at a trace nobody probed has no result — not a failure. The circuit
    /// has not been shown to break it.
    /// </summary>
    [Fact]
    public void AMissingTraceIsUnknownRatherThanFailed()
    {
        var spec = new DesignSpec { Name = "Ripple", Trace = "Rail" };

        var result = SpecCheck.Evaluate(spec, null);

        Assert.Null(result.Passed);
        Assert.Null(result.Measured);
        Assert.Contains("No trace called \"Rail\"", result.Explanation);
    }

    /// <summary>
    /// And neither is a quantity the recording cannot yield. A frequency needs cycles; one that
    /// has not happened yet is not a circuit running at the wrong frequency.
    /// </summary>
    [Fact]
    public void AnUnmeasurableQuantityIsUnknownToo()
    {
        var spec = new DesignSpec
        {
            Trace = "Out",
            Quantity = SpecQuantity.Frequency,
            Comparison = SpecComparison.Within,
            Limit = 1e3,
            Tolerance = 10,
        };

        // A fraction of one cycle: there is no frequency to read off it.
        var result = SpecCheck.Evaluate(spec, Sine(1.0, 0, 1e3, cycles: 0.25));

        Assert.Null(result.Passed);
        Assert.Contains("Nothing to measure yet", result.Explanation);
    }

    /// <summary>
    /// Margin says how much room is left, which is the number that decides whether a design is
    /// finished or merely passing today.
    /// </summary>
    [Fact]
    public void MarginSaysHowMuchRoomIsLeft()
    {
        var spec = new DesignSpec
        {
            Trace = "Rail",
            Quantity = SpecQuantity.PeakToPeak,
            Comparison = SpecComparison.AtMost,
            Limit = 0.1,
        };

        // Half the limit is half the limit's worth of room.
        var comfortable = SpecCheck.Evaluate(spec, Sine(0.025, 0, 100));

        Assert.Equal(0.5, comfortable.Margin!.Value, 0.01);

        // Over it, and the margin goes negative by how far.
        var over = SpecCheck.Evaluate(spec, Sine(0.075, 0, 100));

        Assert.Equal(-0.5, over.Margin!.Value, 0.01);
    }

    /// <summary>A requirement nobody is checking has no result, rather than a passing one.</summary>
    [Fact]
    public void DisabledRequirementsAreLeftOutRatherThanPassed()
    {
        List<DesignSpec> specs =
        [
            new() { Name = "On", Trace = "A", Comparison = SpecComparison.AtMost, Limit = 1 },
            new() { Name = "Off", Trace = "A", Comparison = SpecComparison.AtMost, Limit = 1, IsEnabled = false },
        ];

        var results = SpecCheck.EvaluateAll(specs, _ => Sine(0.1, 0, 100));

        var only = Assert.Single(results);

        Assert.Equal("On", only.Spec.Name);
    }

    /// <summary>
    /// The one-line verdict keeps "not met" and "not measurable" apart, because they mean opposite
    /// things about a design.
    /// </summary>
    [Fact]
    public void TheSummaryKeepsFailuresApartFromGaps()
    {
        List<DesignSpec> specs =
        [
            new() { Name = "Fine", Trace = "A", Quantity = SpecQuantity.PeakToPeak, Comparison = SpecComparison.AtMost, Limit = 10 },
            new() { Name = "Broken", Trace = "A", Quantity = SpecQuantity.PeakToPeak, Comparison = SpecComparison.AtMost, Limit = 0.01 },
            new() { Name = "Missing", Trace = "Nowhere", Quantity = SpecQuantity.PeakToPeak, Comparison = SpecComparison.AtMost, Limit = 1 },
        ];

        var results = SpecCheck.EvaluateAll(
            specs, label => label == "A" ? Sine(1.0, 0, 100) : null);

        var summary = SpecCheck.Summarise(results);

        Assert.Contains("1 not met", summary);
        Assert.Contains("1 not measurable", summary);
        Assert.Contains("1 met", summary);

        Assert.Equal("All 2 requirements met.", SpecCheck.Summarise(
            [.. results.Where(r => r.Passed == true), .. results.Where(r => r.Passed == true)]));
    }

    /// <summary>A requirement reads back as the sentence somebody would have written.</summary>
    [Fact]
    public void ItReadsBackAsASentence()
    {
        Assert.Equal("Rail peak to peak at most 50mV", new DesignSpec
        {
            Trace = "Rail",
            Quantity = SpecQuantity.PeakToPeak,
            Comparison = SpecComparison.AtMost,
            Limit = 0.05,
        }.Describe());

        Assert.Equal("Out mean within 100mV of 5V", new DesignSpec
        {
            Trace = "Out",
            Quantity = SpecQuantity.Mean,
            Comparison = SpecComparison.Within,
            Limit = 5.0,
            Tolerance = 0.1,
        }.Describe());
    }
}
