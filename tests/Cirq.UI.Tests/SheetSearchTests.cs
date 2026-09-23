using Cirq.Components.Ics;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.UI.Services;

namespace Cirq.UI.Tests;

/// <summary>
/// Finding something on the sheet you already have.
/// <para>
/// What matters is the <b>order</b>. Matching everything and ranking it badly is the same as not
/// matching at all: somebody typing "R1" wants R1, not the eleven other things whose description
/// happens to contain those two characters.
/// </para>
/// </summary>
public class SheetSearchTests
{
    private static Circuit Sheet()
    {
        var circuit = new Circuit();

        circuit.Add(new Resistor(1e3) { Name = "R1" });
        circuit.Add(new Resistor(4.7e3) { Name = "R17" });
        circuit.Add(new Resistor(10e3) { Name = "R2" });
        circuit.Add(new Capacitor(100e-9) { Name = "C1" });
        circuit.Add(new OperationalAmplifier { Name = "U1" });

        return circuit;
    }

    [Fact]
    public void AnExactDesignatorComesFirst()
    {
        var found = SheetSearch.Find(Sheet(), "R1");

        // R1 exactly, before R17 which merely starts with it.
        Assert.Equal("R1", found[0].Title);
        Assert.Equal(MatchKind.Designator, found[0].Kind);
        Assert.Equal("R17", found[1].Title);
    }

    [Fact]
    public void AValueFindsThePartThatHasIt()
    {
        var found = SheetSearch.Find(Sheet(), "4.7k");

        var match = Assert.Single(found);

        Assert.Equal("R17", match.Title);
        Assert.Equal(MatchKind.Value, match.Kind);
    }

    [Fact]
    public void AKindFindsAllOfThem()
    {
        var found = SheetSearch.Find(Sheet(), "resistor");

        Assert.Equal(3, found.Count);
        Assert.All(found, m => Assert.Equal(MatchKind.Type, m.Kind));
        Assert.Equal(["R1", "R17", "R2"], found.Select(m => m.Title));
    }

    /// <summary>Case is not something anybody should have to get right to find their own part.</summary>
    [Fact]
    public void CaseDoesNotMatter()
    {
        Assert.NotEmpty(SheetSearch.Find(Sheet(), "r17"));
        Assert.NotEmpty(SheetSearch.Find(Sheet(), "RESISTOR"));
    }

    /// <summary>
    /// A named net is one answer however many things are on it. Twenty rows for one rail is the
    /// same as no answer at all.
    /// </summary>
    [Fact]
    public void ANamedNetIsOneRowNotOnePerPin()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(5.0) { Name = "V1" });
        var one = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var two = circuit.Add(new Resistor(2e3) { Name = "R2" });
        var label = circuit.Add(new NetLabel { Name = "NL1", NetName = "VCC" });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, one.A);
        circuit.Connect(supply.Positive, two.A);
        circuit.Connect(supply.Positive, label.Pin);
        circuit.Connect(one.B, ground.Pin);
        circuit.Connect(two.B, ground.Pin);

        var nets = SheetSearch.Find(circuit, "VCC").Where(m => m.Kind == MatchKind.Net).ToList();

        var match = Assert.Single(nets);

        Assert.Equal("VCC", match.Title);
        Assert.Contains("parts on it", match.Detail);
    }

    [Fact]
    public void NothingMatchesNothing()
    {
        Assert.Empty(SheetSearch.Find(Sheet(), "Z99"));
        Assert.Empty(SheetSearch.Find(Sheet(), "   "));
        Assert.Empty(SheetSearch.Find(Sheet(), string.Empty));
    }

    /// <summary>
    /// A part does not stop existing because somebody grouped it into a block. "Where is R17" has
    /// the same answer whether or not the sheet has been tidied.
    /// </summary>
    [Fact]
    public void ItLooksInsideBlocks()
    {
        var circuit = new Circuit();
        var block = circuit.Add(new Cirq.Components.Hierarchy.Subcircuit { Name = "X1" });

        block.AddInner(new Resistor(4.7e3) { Name = "R99" });

        var match = Assert.Single(SheetSearch.Find(circuit, "R99"));

        Assert.Equal("R99", match.Title);
    }
}
