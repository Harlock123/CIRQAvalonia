using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The baseline window: record, compare, and say what moved. The comparison itself is tested
/// against the engine; what is checked here is that it reaches the window intact and that the
/// document is marked changed when it should be.
/// </summary>
public class BaselineViewModelTests
{
    private static (Circuit Circuit, SignalProbe Probe) WithProbe()
    {
        var circuit = new Circuit();

        var r = circuit.Add(new Resistor(1e3) { Name = "R1" });
        circuit.Add(new Ground { Name = "GND1" });

        var probe = new SignalProbe { Label = "out", TargetTerminal = r.A };

        circuit.Probes.Add(probe);

        return (circuit, probe);
    }

    private static void Fill(SignalProbe probe, double amplitude, int count = 500)
    {
        probe.ResetHistory();

        for (var i = 0; i < count; i++)
        {
            var t = i * 1e-5;
            probe.Record(t, amplitude * Math.Sin(2 * Math.PI * 1000 * t));
        }
    }

    [Fact]
    public void WithNothingRecordedItSaysSoRatherThanThatNothingChanged()
    {
        var (circuit, probe) = WithProbe();

        Fill(probe, 1.0);

        var model = new BaselineViewModel(circuit);

        Assert.False(model.HasBaseline);
        Assert.Contains("No baseline yet", model.Summary, StringComparison.Ordinal);
        Assert.Empty(model.Rows);
    }

    [Fact]
    public void RecordingThenComparingFindsNothingChanged()
    {
        var (circuit, probe) = WithProbe();

        Fill(probe, 1.0);

        var model = new BaselineViewModel(circuit);
        model.Record();

        Assert.True(model.HasBaseline);
        Assert.Contains("Nothing moved", model.Summary, StringComparison.Ordinal);
        Assert.All(model.Rows, r => Assert.Equal("Same", r.Status));
    }

    /// <summary>And a circuit that changed says which trace moved, and by how much.</summary>
    [Fact]
    public void AChangedCircuitSaysWhatMoved()
    {
        var (circuit, probe) = WithProbe();

        Fill(probe, 1.0);

        var model = new BaselineViewModel(circuit);
        model.Record();

        Fill(probe, 2.0);
        model.Compare();

        var row = Assert.Single(model.Rows);

        Assert.Equal("Moved", row.Status);
        Assert.Contains(row.Changes, c => c.Quantity == "peak to peak");
        Assert.Contains("of swing", row.Deviation, StringComparison.Ordinal);
    }

    /// <summary>
    /// A figure that appeared says so rather than showing a percentage. There is no percentage
    /// between a number and nothing.
    /// </summary>
    [Fact]
    public void AnAppearingFigureSaysSoRatherThanShowingAPercentage()
    {
        var (circuit, probe) = WithProbe();

        probe.ResetHistory();
        for (var i = 0; i < 500; i++) probe.Record(i * 1e-5, 1.0);

        var model = new BaselineViewModel(circuit);
        model.Record();

        Fill(probe, 1.0);
        model.Compare();

        var change = model.Rows.Single().Changes.SingleOrDefault(c => c.Quantity == "frequency");

        Assert.NotNull(change);
        Assert.Equal("appeared", change.Moved);
        Assert.Equal("—", change.Was);
    }

    /// <summary>
    /// A rise time reads in seconds whatever the probe measures, and a duty cycle in nothing at
    /// all. Printing "0.45 V" for a duty cycle would be a unit taken from the wrong place.
    /// </summary>
    [Fact]
    public void EachQuantityIsWrittenInItsOwnUnit()
    {
        var (circuit, probe) = WithProbe();

        Fill(probe, 1.0);

        var model = new BaselineViewModel(circuit);
        model.Record();

        Fill(probe, 4.0);
        model.Compare();

        var row = model.Rows.Single();

        Assert.EndsWith("V", row.Changes.Single(c => c.Quantity == "peak to peak").Now,
            StringComparison.Ordinal);

        if (row.Changes.SingleOrDefault(c => c.Quantity == "duty cycle") is { } duty)
            Assert.DoesNotContain("V", duty.Now, StringComparison.Ordinal);
    }

    /// <summary>
    /// Taking or forgetting a baseline edits the document, and a document that changed without
    /// being marked changed is a document somebody closes without being asked.
    /// </summary>
    [Fact]
    public void TakingOrForgettingMarksTheDocumentChanged()
    {
        var (circuit, probe) = WithProbe();

        Fill(probe, 1.0);

        var model = new BaselineViewModel(circuit);

        var edits = 0;
        model.Changed += (_, _) => edits++;

        model.Record();
        Assert.Equal(1, edits);

        model.Clear();
        Assert.Equal(2, edits);
        Assert.False(model.HasBaseline);
    }

    /// <summary>The note is kept with the baseline, for whoever reads it later.</summary>
    [Fact]
    public void TheNoteIsKept()
    {
        var (circuit, probe) = WithProbe();

        Fill(probe, 1.0);

        var model = new BaselineViewModel(circuit) { Note = "after the filter went in" };
        model.Record();

        Assert.Equal("after the filter went in", circuit.Baseline.Note);
        Assert.Contains("after the filter went in", model.Recorded, StringComparison.Ordinal);
    }
}
