using Cirq.Components.Passive;
using Cirq.Core.Topology;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// Keeping a trace to compare against.
/// <para>
/// "Is that better than what I had" is the question after every edit, and the only honest way to
/// answer it is to have both curves on the same axes. Two separate pictures tell you much less,
/// because what you are looking for is the difference between them.
/// </para>
/// </summary>
public class ReferenceTraceTests
{
    private static (Circuit Circuit, ScopeViewModel Scope) Rig(int samples = 50, double scale = 1.0)
    {
        var circuit = new Circuit();
        var scope = new ScopeViewModel(circuit);
        var resistor = circuit.Add(new Resistor(1e3));

        var probe = scope.AddProbe(resistor.A, "Sig");

        for (var i = 0; i < samples; i++)
            probe.Record(i * 1e-4, scale * Math.Sin(2 * Math.PI * 1e3 * i * 1e-4));

        return (circuit, scope);
    }

    [Fact]
    public void ThereIsNoReferenceUntilOneIsTaken()
    {
        var (_, scope) = Rig();

        Assert.False(scope.HasReferences);
        Assert.Empty(scope.References);
        Assert.Equal("No reference", scope.ReferenceSummary);
    }

    [Fact]
    public void KeepingOneCopiesWhatIsOnScreen()
    {
        var (_, scope) = Rig();

        scope.CaptureReferenceCommand.Execute(null);

        var reference = Assert.Single(scope.References);

        Assert.True(scope.HasReferences);
        Assert.Equal("Sig", reference.Label);
        Assert.Equal(50, reference.Samples.Count);
        Assert.Contains("Sig", reference.LegendText);
        Assert.Contains("ref", reference.LegendText);
    }

    /// <summary>
    /// Copied, not referenced. The probes keep recording, and a reference that moved with them
    /// would not be one — it would just be the live trace drawn twice.
    /// </summary>
    [Fact]
    public void AKeptTraceDoesNotFollowTheProbeAfterwards()
    {
        var (circuit, scope) = Rig();

        scope.CaptureReferenceCommand.Execute(null);

        var before = scope.References[0].Samples.Count;
        var kept = scope.References[0].Samples[10].Value;

        // Keep running, and at a different amplitude.
        var probe = circuit.Probes[0];
        for (var i = 50; i < 150; i++) probe.Record(i * 1e-4, 9.0);

        Assert.Equal(before, scope.References[0].Samples.Count);
        Assert.Equal(kept, scope.References[0].Samples[10].Value, 12);
    }

    /// <summary>Each visible probe gets its own reference, so a multi-trace comparison works.</summary>
    [Fact]
    public void EveryVisibleProbeIsKept()
    {
        var circuit = new Circuit();
        var scope = new ScopeViewModel(circuit);

        // Three separate parts, because a probe belongs to a terminal.
        foreach (var label in new[] { "A", "B", "C" })
        {
            var resistor = circuit.Add(new Resistor(1e3));
            var probe = scope.AddProbe(resistor.A, label);

            for (var i = 0; i < 20; i++) probe.Record(i * 1e-4, i);
        }

        Assert.Equal(3, scope.Probes.Count);

        // One of them switched off: what is not on screen is not kept.
        scope.Probes[1].IsVisible = false;

        scope.CaptureReferenceCommand.Execute(null);

        Assert.Equal(["A", "C"], scope.References.Select(r => r.Label));
    }

    /// <summary>A probe with nothing recorded has nothing to keep.</summary>
    [Fact]
    public void AProbeWithNoTraceIsNotKept()
    {
        var circuit = new Circuit();
        var scope = new ScopeViewModel(circuit);
        var resistor = circuit.Add(new Resistor(1e3));

        scope.AddProbe(resistor.A, "Empty");

        scope.CaptureReferenceCommand.Execute(null);

        Assert.False(scope.HasReferences);
    }

    /// <summary>Several can be kept, and the summary counts them.</summary>
    [Fact]
    public void SeveralCanBeKeptAndTheSummaryCountsThem()
    {
        var (_, scope) = Rig();

        scope.CaptureReferenceCommand.Execute(null);
        scope.CaptureReferenceCommand.Execute(null);

        Assert.Equal(2, scope.References.Count);
        Assert.Equal("2 references", scope.ReferenceSummary);
    }

    [Fact]
    public void ClearingThrowsThemAway()
    {
        var (_, scope) = Rig();

        scope.CaptureReferenceCommand.Execute(null);
        scope.ClearReferencesCommand.Execute(null);

        Assert.False(scope.HasReferences);
        Assert.Empty(scope.References);
        Assert.Equal("No reference", scope.ReferenceSummary);
    }

    /// <summary>
    /// The one that matters end to end: keep a trace, change the circuit, and the old shape is
    /// still there to be compared against the new one.
    /// </summary>
    [Fact]
    public void TheKeptTraceSurvivesAChangeToTheCircuit()
    {
        var (circuit, scope) = Rig(scale: 1.0);

        scope.CaptureReferenceCommand.Execute(null);

        // A different answer entirely: clear the traces and record something else.
        scope.ClearTracesCommand.Execute(null);

        // Time carries on from where the first run stopped, as a real run would.
        var probe = scope.Probes[0];
        for (var i = 50; i < 100; i++)
            probe.Record(i * 1e-4, 5.0 * Math.Sin(2 * Math.PI * 1e3 * i * 1e-4));

        var reference = Assert.Single(scope.References);

        Assert.Equal(50, reference.Samples.Count);

        // The kept one is the smaller signal and the live one is five times it. Compared as a
        // ratio rather than against absolute numbers, because ten samples a cycle never lands on
        // the peak of a sine — the largest sample is 0.95 of the amplitude, not 1.0.
        var kept = reference.Samples.Max(s => s.Value);
        var live = scope.Probes[0].HistoryBuffer.ToArray().Max(s => s.Value);

        Assert.Equal(5.0, live / kept, 0.01);
    }
}
