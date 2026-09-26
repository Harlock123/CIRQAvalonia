using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;

namespace Cirq.Components.Tests;

/// <summary>
/// Where a net goes when it leaves the page.
/// <para>
/// Sheets are joined by naming a net, which is the one thing about them that can silently fail: a
/// <c>VCC</c> label that reaches another page looks exactly like a <c>VCC</c> label that goes
/// nowhere. These check the statement the drawing makes about itself — worked out from the labels
/// that are there, so there is no second thing to keep in step with the first.
/// </para>
/// </summary>
public class SheetCrossingTests
{
    private static Circuit Split()
    {
        var circuit = new Circuit();

        circuit.Sheets.Add("Power");
        circuit.Sheets.Add("Logic");
        circuit.Sheets.Add("IO");

        return circuit;
    }

    private static NetLabel Label(Circuit circuit, string net, string sheet) =>
        circuit.Add(new NetLabel(net) { Sheet = sheet });

    [Fact]
    public void ANetIsFoundOnEveryPageThatNamesIt()
    {
        var circuit = Split();

        Label(circuit, "VCC", "Power");
        Label(circuit, "VCC", "Logic");
        Label(circuit, "SDA", "IO");

        Assert.Equal(["Power", "Logic"], SheetCrossings.SheetsCarrying(circuit, "VCC"));
        Assert.Equal(["IO"], SheetCrossings.SheetsCarrying(circuit, "SDA"));
        Assert.Empty(SheetCrossings.SheetsCarrying(circuit, "Nowhere"));
    }

    /// <summary>
    /// The same rule the netlist uses. Two people typing the same rail are not going to agree about
    /// capitals, and a drawing where they disagree is still one net.
    /// </summary>
    [Fact]
    public void TheNameIsMatchedTheWayTheNetlistMatchesIt()
    {
        var circuit = Split();

        Label(circuit, "VCC", "Power");
        Label(circuit, " vcc ", "Logic");

        Assert.Equal(["Power", "Logic"], SheetCrossings.SheetsCarrying(circuit, "Vcc"));
    }

    [Fact]
    public void ALabelSaysWhichOtherPagesItsNetIsOn()
    {
        var circuit = Split();

        var power = Label(circuit, "VCC", "Power");
        Label(circuit, "VCC", "Logic");
        Label(circuit, "VCC", "IO");

        Assert.Equal(["Logic", "IO"], SheetCrossings.Elsewhere(circuit, power));
        Assert.Equal("also on Logic, IO", SheetCrossings.Describe(circuit, power));
    }

    [Fact]
    public void ALabelWhoseNetStaysPutSaysNothing()
    {
        var circuit = Split();

        var alone = Label(circuit, "SENSE", "Power");
        Label(circuit, "VCC", "Logic");

        Assert.Empty(SheetCrossings.Elsewhere(circuit, alone));
        Assert.Empty(SheetCrossings.Describe(circuit, alone));
    }

    /// <summary>
    /// A drawing that has never been split has one page, and a note saying a net is "also on" that
    /// page would be a note about nothing.
    /// </summary>
    [Fact]
    public void AnUnsplitDrawingHasNoCrossings()
    {
        var circuit = new Circuit();

        var first = circuit.Add(new NetLabel("VCC"));
        circuit.Add(new NetLabel("VCC"));

        Assert.Empty(SheetCrossings.SheetsCarrying(circuit, "VCC"));
        Assert.Empty(SheetCrossings.Elsewhere(circuit, first));
        Assert.Null(SheetCrossings.Continuation(circuit, first));
    }

    [Fact]
    public void FollowingANetWalksThePagesItIsOn()
    {
        var circuit = Split();

        var power = Label(circuit, "VCC", "Power");
        var logic = Label(circuit, "VCC", "Logic");
        var io = Label(circuit, "VCC", "IO");

        // Round the houses and back, rather than bouncing between two pages.
        Assert.Same(logic, SheetCrossings.Continuation(circuit, power));
        Assert.Same(io, SheetCrossings.Continuation(circuit, logic));
        Assert.Same(power, SheetCrossings.Continuation(circuit, io));
    }

    [Fact]
    public void ANetOnOnePageHasNowhereToGo()
    {
        var circuit = Split();

        var alone = Label(circuit, "SENSE", "Power");
        Label(circuit, "SENSE", "Power");

        Assert.Null(SheetCrossings.Continuation(circuit, alone));
    }

    [Fact]
    public void SomethingThatIsNotALabelIsNotACrossing()
    {
        var circuit = Split();

        var resistor = circuit.Add(new Resistor(1e3) { Sheet = "Power" });

        Assert.Empty(SheetCrossings.Elsewhere(circuit, resistor));
        Assert.Null(SheetCrossings.Continuation(circuit, resistor));
    }

    /// <summary>
    /// A part that names no page is on the first one, and so is its net — the same rule everything
    /// else about sheets follows.
    /// </summary>
    [Fact]
    public void ALabelThatNamesNoPageIsOnTheFirst()
    {
        var circuit = Split();

        var implicitly = circuit.Add(new NetLabel("VCC"));
        var logic = Label(circuit, "VCC", "Logic");

        Assert.Equal(["Power", "Logic"], SheetCrossings.SheetsCarrying(circuit, "VCC"));
        Assert.Equal(["Logic"], SheetCrossings.Elsewhere(circuit, implicitly));
        Assert.Same(logic, SheetCrossings.Continuation(circuit, implicitly));
    }
}
