using Cirq.Components.Passive;
using Cirq.Components.Serialization;
using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Topology;

namespace Cirq.Components.Tests;

/// <summary>
/// A baseline survives being saved and opened again, and a circuit without one is untouched.
/// </summary>
public class BaselineSerializationTests
{
    private static List<DataPoint> Ramp(int count = 500) =>
        [.. Enumerable.Range(0, count).Select(i => new DataPoint(i * 1e-6, i / (double)count))];

    private static Circuit WithBaseline(string note = "after the filter went in")
    {
        var circuit = new Circuit();

        circuit.Add(new Resistor(1e3) { Name = "R1" });

        circuit.Baseline = TraceBaseline.From(
            [("out", "V", (IReadOnlyList<DataPoint>)Ramp())], note);

        return circuit;
    }

    [Fact]
    public void ABaselineSurvivesARoundTrip()
    {
        var circuit = WithBaseline();

        var was = circuit.Baseline;

        var back = CircuitSerializer.FromJson(CircuitSerializer.ToJson(circuit)).Circuit.Baseline;

        Assert.Equal(was.Note, back.Note);
        Assert.Equal(was.Taken, back.Taken, TimeSpan.FromSeconds(1));

        var trace = Assert.Single(back.Traces);

        Assert.Equal("out", trace.Label);
        Assert.Equal("V", trace.Unit);
        Assert.Equal(was.Traces[0].Samples.Count, trace.Samples.Count);

        // And it is the same waveform, not merely the same number of points.
        for (var i = 0; i < trace.Samples.Count; i++)
        {
            Assert.Equal(was.Traces[0].Samples[i].Time, trace.Samples[i].Time, 1e-12);
            Assert.Equal(was.Traces[0].Samples[i].Value, trace.Samples[i].Value, 1e-12);
        }
    }

    /// <summary>
    /// A circuit with no baseline writes no baseline. Adding a section that every file carries
    /// whether or not it means anything is how a format gets heavier for nothing.
    /// </summary>
    [Fact]
    public void ACircuitWithoutOneIsUnchangedOnDisk()
    {
        var circuit = new Circuit();

        circuit.Add(new Resistor(1e3) { Name = "R1" });

        Assert.DoesNotContain("\"baseline\"", CircuitSerializer.ToJson(circuit),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The measurements are worked out again on the way in rather than stored. A release that fixes
    /// a measurement then fixes it for baselines taken before the fix, instead of holding new runs
    /// against an old bug forever.
    /// </summary>
    [Fact]
    public void TheMeasurementsAreWorkedOutAgainRatherThanStored()
    {
        var json = CircuitSerializer.ToJson(WithBaseline());

        Assert.DoesNotContain("\"measurements\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"riseTime\"", json, StringComparison.OrdinalIgnoreCase);

        var back = CircuitSerializer.FromJson(json).Circuit.Baseline;

        Assert.True(back.Traces[0].Measurements.IsValid);
        Assert.Equal(1.0, back.Traces[0].Measurements.PeakToPeak, 0.01);
    }

    /// <summary>
    /// A record whose two arrays disagree is dropped and reported, rather than truncated. The
    /// pairing between a time and a value is the whole of what it means, and half a pairing is not
    /// a shorter waveform — it is an unknown one.
    /// </summary>
    [Fact]
    public void AMismatchedRecordIsDroppedAndReported()
    {
        var json = CircuitSerializer.ToJson(WithBaseline());

        // Take one value away, leaving the times one longer than the values.
        var index = json.LastIndexOf(',');

        Assert.True(index > 0);

        var broken = json[..index] + json[(json.IndexOf(']', index))..];

        var result = CircuitSerializer.FromJson(broken);

        Assert.Empty(result.Circuit.Baseline.Traces);
        Assert.Contains(result.Warnings, w => w.Contains("out", StringComparison.Ordinal));
    }

    /// <summary>
    /// Times and values are two flat arrays rather than a list of pairs, which is about three times
    /// smaller for the same numbers — and the file is one somebody may open in an editor.
    /// </summary>
    [Fact]
    public void TheShapeIsStoredAsTwoArrays()
    {
        var json = CircuitSerializer.ToJson(WithBaseline());

        Assert.Contains("\"times\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"values\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"time\":", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Taking a baseline does not move the file format's version. Adding a section is exactly the
    /// kind of change an older build already survives — it ignores what it does not recognise.
    /// </summary>
    [Fact]
    public void ABaselineDoesNotMoveTheVersion()
    {
        var json = CircuitSerializer.ToJson(WithBaseline());

        Assert.Contains($"\"version\": {CircuitSerializer.CurrentVersion}", json,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, CircuitSerializer.CurrentVersion);
    }
}
