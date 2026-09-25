using Cirq.Components.Passive;
using Cirq.Components.Serialization;
using Cirq.Core.Topology;

namespace Cirq.Components.Tests;

/// <summary>
/// Pages of one drawing.
/// <para>
/// Sheets are a drawing concern and nothing else: the solver is handed every part on every sheet at
/// once, and two sheets are joined the way two ends of one large sheet already are — by naming a
/// net. Most of what is checked here is that the split is invisible to everything that is not the
/// canvas.
/// </para>
/// </summary>
public class SheetTests
{
    private static Circuit Split()
    {
        var circuit = new Circuit();

        circuit.Sheets.Add("Power");
        circuit.Sheets.Add("Logic");

        circuit.Add(new Resistor(1e3) { Name = "R1", Sheet = "Power" });
        circuit.Add(new Resistor(2e3) { Name = "R2", Sheet = "Logic" });
        circuit.Add(new Resistor(3e3) { Name = "R3", Sheet = "Logic" });

        return circuit;
    }

    [Fact]
    public void EachSheetShowsItsOwnParts()
    {
        var circuit = Split();

        Assert.Equal(["R1"], circuit.OnSheet("Power").Select(c => c.Name));
        Assert.Equal(["R2", "R3"], circuit.OnSheet("Logic").Select(c => c.Name));
    }

    /// <summary>
    /// A circuit that has never been split is one page, whatever anything is labelled — which is
    /// every circuit written before sheets existed.
    /// </summary>
    [Fact]
    public void ACircuitWithNoSheetsIsAllOnePage()
    {
        var circuit = new Circuit();

        circuit.Add(new Resistor(1e3) { Name = "R1" });
        circuit.Add(new Resistor(1e3) { Name = "R2", Sheet = "Somewhere" });

        Assert.Equal(2, circuit.OnSheet("anything").Count());
        Assert.Equal(2, circuit.OnSheet(null).Count());
    }

    /// <summary>
    /// A part with no sheet, or one naming a sheet that has been deleted, lands on the first —
    /// better somewhere visible than nowhere at all.
    /// </summary>
    [Fact]
    public void AnOrphanedPartLandsOnTheFirstSheet()
    {
        var circuit = Split();

        circuit.Add(new Resistor(1e3) { Name = "R4" });
        circuit.Add(new Resistor(1e3) { Name = "R5", Sheet = "Deleted" });

        var first = circuit.OnSheet("Power").Select(c => c.Name).ToList();

        Assert.Contains("R4", first);
        Assert.Contains("R5", first);
        Assert.DoesNotContain("R4", circuit.OnSheet("Logic").Select(c => c.Name));
    }

    /// <summary>
    /// Every part is on exactly one sheet, so nothing is invisible and nothing is drawn twice.
    /// </summary>
    [Fact]
    public void EveryPartIsOnExactlyOneSheet()
    {
        var circuit = Split();

        circuit.Add(new Resistor(1e3) { Name = "R4" });
        circuit.Add(new Resistor(1e3) { Name = "R5", Sheet = "Deleted" });

        foreach (var part in circuit.Components)
            Assert.Equal(1, circuit.Sheets.Count(s => circuit.Belongs(part, s)));
    }

    /// <summary>The solver is handed every part, wherever it is drawn.</summary>
    [Fact]
    public void TheCircuitIsStillOneCircuit()
    {
        var circuit = Split();

        Assert.Equal(3, circuit.Components.Count);
    }

    // ---- through the file --------------------------------------------------

    [Fact]
    public void SheetsSurviveARoundTrip()
    {
        var back = CircuitSerializer.FromJson(CircuitSerializer.ToJson(Split())).Circuit;

        Assert.Equal(["Power", "Logic"], back.Sheets);
        Assert.Equal("Logic", back.Components.Single(c => c.Name == "R2").Sheet);
        Assert.Equal(["R2", "R3"], back.OnSheet("Logic").Select(c => c.Name));
    }

    /// <summary>A circuit on one sheet writes nothing about sheets at all.</summary>
    [Fact]
    public void ACircuitOnOneSheetIsUnchangedOnDisk()
    {
        var circuit = new Circuit();

        circuit.Add(new Resistor(1e3) { Name = "R1" });

        var json = CircuitSerializer.ToJson(circuit);

        Assert.DoesNotContain("\"sheets\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"sheet\"", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The sheet is written once, as a field of the part's record — not as one of its settings.
    /// Which page something is drawn on is not a property of the component any more than where on
    /// the page it is.
    /// </summary>
    [Fact]
    public void TheSheetIsNotOneOfThePartsSettings()
    {
        var json = CircuitSerializer.ToJson(Split());

        // Once per part that has one, and no more. Counted without case, like the assertions
        // around it: the document's own fields are written in the casing the records declare.
        var occurrences = System.Text.RegularExpressions.Regex
            .Matches(json, "\"sheet\"\\s*:", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Count;

        Assert.Equal(3, occurrences);
    }

    /// <summary>
    /// Adding sheets does not move the file format's version: an older build ignores what it does
    /// not recognise and opens every part, which is one page of a drawing that used to be three.
    /// </summary>
    [Fact]
    public void SheetsDoNotMoveTheVersion()
    {
        Assert.Equal(1, CircuitSerializer.CurrentVersion);
        Assert.Contains($"\"version\": {CircuitSerializer.CurrentVersion}",
            CircuitSerializer.ToJson(Split()), StringComparison.OrdinalIgnoreCase);
    }

    // ---- adding, renaming and removing -------------------------------------

    [Fact]
    public void TheFirstNewSheetSplitsTheDrawing()
    {
        var circuit = new Circuit();
        circuit.Add(new Resistor(1e3) { Name = "R1" });

        var added = circuit.AddSheet();

        // Two pages out of one call: the drawing that was there is page one, and the call was for
        // somewhere to put what comes next.
        Assert.Equal(["Sheet 1", "Sheet 2"], circuit.Sheets);
        Assert.Equal("Sheet 2", added);

        // And R1 did not have to be touched to end up on page one.
        Assert.Empty(circuit.Components[0].Sheet);
        Assert.Equal(["R1"], circuit.OnSheet("Sheet 1").Select(c => c.Name));
        Assert.Empty(circuit.OnSheet("Sheet 2"));
    }

    [Fact]
    public void ANameAlreadyTakenIsMadeUnique()
    {
        var circuit = Split();

        Assert.Equal("Power 2", circuit.AddSheet("Power"));
        // The suffix goes on what was typed, so a name is never quietly recapitalised.
        Assert.Equal("power 3", circuit.AddSheet("power"));
    }

    [Fact]
    public void RenamingASheetBringsItsPartsAlong()
    {
        var circuit = Split();

        Assert.True(circuit.RenameSheet("Logic", "Digital"));

        Assert.Equal(["Power", "Digital"], circuit.Sheets);
        Assert.Equal(["R2", "R3"], circuit.OnSheet("Digital").Select(c => c.Name));
        Assert.Equal(["R1"], circuit.OnSheet("Power").Select(c => c.Name));
    }

    [Fact]
    public void ARenameCannotCollideOrBlank()
    {
        var circuit = Split();

        Assert.False(circuit.RenameSheet("Logic", "Power"));
        Assert.False(circuit.RenameSheet("Logic", "   "));
        Assert.False(circuit.RenameSheet("Nowhere", "Logic"));

        Assert.Equal(["Power", "Logic"], circuit.Sheets);
    }

    [Fact]
    public void RemovingASheetKeepsWhatWasOnIt()
    {
        var circuit = Split();

        Assert.True(circuit.RemoveSheet("Logic"));

        Assert.Equal(["Power"], circuit.Sheets);
        Assert.Equal(3, circuit.Components.Count);
        Assert.Equal(["R1", "R2", "R3"], circuit.OnSheet("Power").Select(c => c.Name));
    }

    [Fact]
    public void RemovingTheFirstSheetMovesItsPartsForward()
    {
        var circuit = Split();

        // R1 is on the first page by naming it; a part that named nothing would be there too.
        circuit.Add(new Resistor(4e3) { Name = "R4" });

        Assert.True(circuit.RemoveSheet("Power"));

        Assert.Equal(["Logic"], circuit.Sheets);
        Assert.Equal(["R1", "R2", "R3", "R4"], circuit.OnSheet("Logic").Select(c => c.Name));
    }

    [Fact]
    public void TheLastSheetCannotBeRemoved()
    {
        var circuit = new Circuit();
        circuit.Sheets.Add("Only");

        Assert.False(circuit.RemoveSheet("Only"));
        Assert.Equal(["Only"], circuit.Sheets);
    }

    [Fact]
    public void AWireIsOnThePageItsEndsAreOn()
    {
        var circuit = Split();

        var logic = circuit.Components[1];
        var other = circuit.Components[2];

        var onLogic = circuit.Connect(logic.Terminals[0], other.Terminals[0]);

        Assert.Equal([onLogic], circuit.WiresOn("Logic"));
        Assert.Empty(circuit.WiresOn("Power"));

        // And a drawing that was never split has all of them wherever it is asked.
        var whole = new Circuit();
        whole.Add(new Resistor(1e3) { Name = "R1" });
        whole.Add(new Resistor(1e3) { Name = "R2" });
        var wire = whole.Connect(whole.Components[0].Terminals[0], whole.Components[1].Terminals[0]);

        Assert.Equal([wire], whole.WiresOn("anything"));
    }
}
