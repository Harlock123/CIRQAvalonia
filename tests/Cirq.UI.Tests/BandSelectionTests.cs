using Avalonia;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.UI.Controls;

namespace Cirq.UI.Tests;

/// <summary>
/// What a dragged box catches. The rule is that a part has to be <i>wholly</i> inside it, which is
/// the rule people expect and the one that makes a copied group well defined.
/// </summary>
public class BandSelectionTests
{
    private static Circuit ThreeInARow()
    {
        var circuit = new Circuit();

        circuit.Add(new Resistor(1e3) { Name = "R1", X = 0, Y = 0 });
        circuit.Add(new Resistor(2e3) { Name = "R2", X = 200, Y = 0 });
        circuit.Add(new Resistor(3e3) { Name = "R3", X = 400, Y = 0 });

        return circuit;
    }

    [Fact]
    public void ABoxRoundEverythingCatchesEverything()
    {
        var circuit = ThreeInARow();

        var caught = CircuitCanvas.ComponentsWithin(circuit, new Rect(-100, -100, 700, 200));

        Assert.Equal(3, caught.Count);
    }

    [Fact]
    public void ABoxRoundTwoOfThemCatchesTwo()
    {
        var circuit = ThreeInARow();

        var caught = CircuitCanvas.ComponentsWithin(circuit, new Rect(-100, -100, 400, 200));

        Assert.Equal(2, caught.Count);
        Assert.Equal(["R1", "R2"], caught.Select(c => c.Name));
    }

    /// <summary>
    /// Clipping a part is not catching it. A box that half covers something leaves it out, which
    /// is what stops a copied group from depending on exactly where the drag stopped.
    /// </summary>
    [Fact]
    public void APartOnlyHalfInsideIsNotCaught()
    {
        var circuit = new Circuit();
        var resistor = circuit.Add(new Resistor(1e3) { X = 0, Y = 0 });

        var bounds = CircuitCanvas.BoundsOf(resistor);
        var half = new Rect(bounds.X, bounds.Y, bounds.Width / 2, bounds.Height);

        Assert.Empty(CircuitCanvas.ComponentsWithin(circuit, half));

        // And the whole of it is caught, so the test above is about the clipping and not about
        // the box being too small for anything.
        Assert.Single(CircuitCanvas.ComponentsWithin(circuit, bounds.Inflate(1)));
    }

    [Fact]
    public void AnEmptyBoxCatchesNothing()
    {
        var circuit = ThreeInARow();

        Assert.Empty(CircuitCanvas.ComponentsWithin(circuit, new Rect(1000, 1000, 50, 50)));
    }

    /// <summary>
    /// The caption under a part does not count. A value label can hang well below the symbol, and
    /// judging the box against it would catch far less than the box visibly encloses.
    /// </summary>
    [Fact]
    public void TheCaptionUnderAPartDoesNotHaveToBeInside()
    {
        var circuit = new Circuit();

        // A generator's caption is long — shape, frequency and amplitude.
        var generator = circuit.Add(new FunctionGenerator(Waveform.Square, 500, 4.0) { X = 0, Y = 0 });

        var symbol = CircuitCanvas.BoundsOf(generator);
        var visual = CircuitCanvas.VisualBoundsOf(generator);

        Assert.True(visual.Height > symbol.Height, "this part has no caption to speak of");

        // A box around the symbol alone catches it, even though the caption hangs outside.
        Assert.Single(CircuitCanvas.ComponentsWithin(circuit, symbol.Inflate(1)));
    }
}
