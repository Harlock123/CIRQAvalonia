using Cirq.Components.Passive;
using Cirq.Components.Serialization;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Core.Verification;

namespace Cirq.Components.Tests;

/// <summary>
/// What changed between two versions of a circuit.
/// <para>
/// The point of this is that it reads like a person would say it — "R2 1k → 2k2" — rather than
/// like a text diff of the file. What is checked is that it says the right thing, and that it
/// does <b>not</b> say things that did not happen: a part that moved is not a part replaced, and
/// a wire redrawn through a different corner is not a rewiring.
/// </para>
/// </summary>
public class CircuitDiffTests
{
    /// <summary>A divider, and a copy of it through a save and load — so the ids match.</summary>
    private static (Circuit Before, Circuit After) Pair()
    {
        var circuit = new Circuit { Title = "Divider" };

        var supply = circuit.Add(new DcVoltageSource(10.0) { Name = "V1" });
        var top = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var bottom = circuit.Add(new Resistor(1e3) { Name = "R2" });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);

        var loaded = CircuitSerializer.FromJson(CircuitSerializer.ToJson(circuit));

        Assert.True(loaded.IsClean);

        return (circuit, loaded.Circuit);
    }

    private static Resistor Named(Circuit circuit, string name) =>
        circuit.Components.OfType<Resistor>().Single(r => r.Name == name);

    [Fact]
    public void TwoCopiesOfTheSameCircuitDifferInNothing()
    {
        var (before, after) = Pair();

        Assert.Empty(CircuitDiff.Compare(before, after));
        Assert.Equal("No differences.", CircuitDiff.Summarise([]));
    }

    [Fact]
    public void AChangedValueIsReportedAsOneLine()
    {
        var (before, after) = Pair();

        Named(after, "R2").Resistance = 2.2e3;

        var change = Assert.Single(CircuitDiff.Compare(before, after));

        Assert.Equal(ChangeKind.ValueChanged, change.Kind);
        Assert.Equal("R2", change.Subject);
        Assert.Equal("resistance 1000 → 2200", change.Detail);
    }

    [Fact]
    public void AddingAndRemovingAreBothReported()
    {
        var (before, after) = Pair();

        after.Add(new Capacitor(100e-9) { Name = "C1" });
        after.Remove(Named(after, "R1"));

        var changes = CircuitDiff.Compare(before, after);

        Assert.Contains(changes, c => c.Kind == ChangeKind.Added && c.Subject == "C1");
        Assert.Contains(changes, c => c.Kind == ChangeKind.Removed && c.Subject == "R1");

        // Removing R1 also separates the wires it was on, which is a real consequence and is said.
        Assert.Contains(changes, c => c.Kind == ChangeKind.Rewired);
    }

    /// <summary>
    /// A part is matched by identity, not by name or place, so one that was renamed and moved is
    /// still the same part — two changes to it rather than one removed and another added.
    /// </summary>
    [Fact]
    public void ARenamedPartIsStillTheSamePart()
    {
        var (before, after) = Pair();

        var part = Named(after, "R2");

        part.Name = "R20";
        part.X += 40;

        var changes = CircuitDiff.Compare(before, after);

        Assert.DoesNotContain(changes, c => c.Kind is ChangeKind.Added or ChangeKind.Removed);
        Assert.Contains(changes, c => c.Kind == ChangeKind.Renamed && c.Detail == "renamed from R2");
        Assert.Contains(changes, c => c.Kind == ChangeKind.Moved);
    }

    /// <summary>
    /// A move is counted but never led with. A tidy-up moves thirty parts and changes nothing
    /// about what the circuit does; putting that first buries the one value that did.
    /// </summary>
    [Fact]
    public void ASummaryLeadsWithWhatMatters()
    {
        var (before, after) = Pair();

        foreach (var part in after.Components) part.X += 20;

        Named(after, "R1").Resistance = 4.7e3;

        var summary = CircuitDiff.Summarise(CircuitDiff.Compare(before, after));

        Assert.StartsWith("1 changed", summary);
        Assert.Contains("moved", summary);
    }

    [Fact]
    public void AMoveOnItsOwnSaysSo()
    {
        var (before, after) = Pair();

        foreach (var part in after.Components) part.Y += 10;

        Assert.Equal("4 moved, and nothing else.",
            CircuitDiff.Summarise(CircuitDiff.Compare(before, after)));
    }

    /// <summary>
    /// A wire is the pair of pins it joins. Redrawing one through a different corner changes the
    /// picture and not the circuit, and must not be reported as rewiring.
    /// </summary>
    [Fact]
    public void RedrawingAWireIsNotRewiring()
    {
        var (before, after) = Pair();

        var wire = after.Wires[0];

        wire.Waypoints.Add(new Cirq.Core.Primitives.Point(500, 500));

        Assert.Empty(CircuitDiff.Compare(before, after));
    }

    /// <summary>And a wire drawn the other way round is the same wire.</summary>
    [Fact]
    public void AWireIsTheSameBothWaysRound()
    {
        var (before, after) = Pair();

        var wire = after.Wires[0];

        (wire.SourceTerminal, wire.TargetTerminal) = (wire.TargetTerminal, wire.SourceTerminal);

        Assert.Empty(CircuitDiff.Compare(before, after));
    }

    [Fact]
    public void ProbesRequirementsAndSettingsAreCompared()
    {
        var (before, after) = Pair();

        after.Title = "Potential divider";
        after.AmbientTemperatureCelsius = 85.0;
        after.Specs.Add(new DesignSpec { Name = "Out", Trace = "Out", Limit = 5 });

        var changes = CircuitDiff.Compare(before, after);

        Assert.Contains(changes, c => c.Subject == "Circuit" && c.Detail.Contains("title"));
        Assert.Contains(changes, c => c.Subject == "Circuit" && c.Detail.Contains("°C"));
        Assert.Contains(changes, c => c.Subject == "Requirements" && c.Detail.StartsWith("added"));
    }

    /// <summary>
    /// Rounding in the file is not an edit. A value that survives a save and load unchanged to
    /// twelve figures is unchanged.
    /// </summary>
    [Fact]
    public void RoundingIsNotAnEdit()
    {
        var circuit = new Circuit();

        circuit.Add(new Resistor(1.0 / 3.0) { Name = "R1" });

        var loaded = CircuitSerializer.FromJson(CircuitSerializer.ToJson(circuit)).Circuit;

        Assert.Empty(CircuitDiff.Compare(circuit, loaded));
    }
}
