using Avalonia;
using Cirq.Components.Passive;
using Cirq.Core.Topology;
using Cirq.UI.Controls;

namespace Cirq.UI.Tests;

/// <summary>
/// The canvas on a drawing split into pages. What is not drawn must also be unreachable: a part on
/// another page that could still be caught by a rubber band, or left selected when the page changed,
/// would be a part the Delete key can reach without anybody being able to see it.
/// </summary>
public class SheetCanvasTests
{
    private static Circuit TwoPages()
    {
        var circuit = new Circuit();

        circuit.Sheets.Add("Power");
        circuit.Sheets.Add("Logic");

        circuit.Add(new Resistor(1e3) { Name = "R1", X = 0, Y = 0, Sheet = "Power" });
        circuit.Add(new Resistor(2e3) { Name = "R2", X = 200, Y = 0, Sheet = "Logic" });

        return circuit;
    }

    [Fact]
    public void ABandCatchesOnlyWhatIsOnThePage()
    {
        var circuit = TwoPages();
        var everywhere = new Rect(-100, -100, 700, 200);

        Assert.Equal(["R1"], CircuitCanvas.ComponentsWithin(circuit, everywhere, "Power").Select(c => c.Name));
        Assert.Equal(["R2"], CircuitCanvas.ComponentsWithin(circuit, everywhere, "Logic").Select(c => c.Name));

        // No sheet asked for is the whole drawing, which is what every caller that has no pages to
        // think about passes.
        Assert.Equal(2, CircuitCanvas.ComponentsWithin(circuit, everywhere).Count);
    }

    [Fact]
    public void LeavingAPageDropsWhatWasSelectedOnIt()
    {
        var circuit = TwoPages();
        var canvas = new CircuitCanvas { Circuit = circuit, Sheet = "Power" };

        var power = circuit.Components[0];
        power.IsSelected = true;
        canvas.SelectedComponent = power;

        canvas.Sheet = "Logic";

        Assert.False(power.IsSelected);
        Assert.Null(canvas.SelectedComponent);
    }

    [Fact]
    public void APartOnThePageBeingGoneToStaysChosen()
    {
        var circuit = TwoPages();
        var canvas = new CircuitCanvas { Circuit = circuit, Sheet = "Power" };

        // The inspector is editing something on the page about to be shown — going there should not
        // clear the very thing that was being looked at.
        var logic = circuit.Components[1];
        canvas.SelectedComponent = logic;

        canvas.Sheet = "Logic";

        Assert.Same(logic, canvas.SelectedComponent);
    }
}
