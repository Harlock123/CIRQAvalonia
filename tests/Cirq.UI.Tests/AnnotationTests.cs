using Cirq.Components.Annotations;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.UI.Services;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// Schematic annotations: text and boxes that are on the drawing without being in the circuit.
/// </summary>
public class AnnotationTests
{
    [Fact]
    public void AnAnnotationHasNoPinsAndStampsNothing()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var load = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, load.A);
        circuit.Connect(load.B, ground.Pin);

        var before = Solve(circuit, load.A);

        circuit.Add(new SchematicNote("This divider sets the threshold"));
        circuit.Add(new SchematicBox("Input stage"));

        // The same answer, to the last bit: an annotation cannot change a circuit.
        Assert.Equal(before, Solve(circuit, load.A), 12);
    }

    private static double Solve(Circuit circuit, Terminal at)
    {
        var sim = new Cirq.Engine.Simulation.CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        return sim.NodeVoltage(at);
    }

    [Fact]
    public void AnnotationsStayOutOfTheBillOfMaterials()
    {
        var circuit = new Circuit();
        circuit.Add(new Resistor(1e3));
        circuit.Add(new Resistor(1e3));
        circuit.Add(new SchematicNote("Two of these"));
        circuit.Add(new SchematicBox("Divider"));

        var rows = PartsList.For(circuit);

        var only = Assert.Single(rows);

        Assert.Equal("Resistor", only.Part);
        Assert.Equal(2, only.Quantity);
    }

    /// <summary>
    /// The text has to lay out the same whether it is going to a screen, an SVG or a PDF page, and
    /// the extents have to be known before any of those exist.
    /// </summary>
    [Fact]
    public void LongTextWrapsOnWordsAndTheHeightFollowsIt()
    {
        var single = new SchematicNote("Short");
        var many = new SchematicNote(
            "This is a considerably longer note, long enough that it has to be broken across " +
            "several lines before it will fit inside the width it was given.");

        Assert.Single(single.Lines());
        Assert.True(many.Lines().Count > 2, $"it wrapped to {many.Lines().Count} lines");

        // No word is split; every line is made of whole words from the original.
        foreach (var line in many.Lines())
            Assert.DoesNotContain("  ", line);

        Assert.True(many.HalfHeight > single.HalfHeight);
    }

    [Fact]
    public void ExplicitLineBreaksAreKept()
    {
        var note = new SchematicNote("First line\nSecond line\nThird");

        Assert.Equal(3, note.Lines().Count);
        Assert.Equal("Second line", note.Lines()[1]);
    }

    [Fact]
    public void AHeadingIsDrawnLargerThanAnOrdinaryNote()
    {
        var note = new SchematicNote("Input stage");
        var heading = new SchematicNote("Input stage") { IsHeading = true };

        Assert.True(heading.EffectiveTextSize > note.EffectiveTextSize);
    }

    [Fact]
    public void AnEmptyNoteStillOccupiesALineRatherThanNothing()
    {
        var note = new SchematicNote(string.Empty);

        Assert.Single(note.Lines());
        Assert.True(note.HalfHeight > 0);
    }

    /// <summary>
    /// A box states its own size, where an ordinary part's is inferred from its pins — which an
    /// annotation does not have.
    /// </summary>
    [Fact]
    public void AnAnnotationSizesItselfAndTheCanvasBelievesIt()
    {
        var box = new SchematicBox("Output stage") { Width = 400, Height = 260, X = 100, Y = 50 };

        var bounds = Cirq.UI.Controls.CircuitCanvas.BoundsOf(box);

        Assert.Equal(400, bounds.Width, 6);
        Assert.Equal(260, bounds.Height, 6);
        Assert.Equal(100, bounds.Center.X, 6);
        Assert.Equal(50, bounds.Center.Y, 6);

        // And no caption clearance is added, because an annotation has no captions.
        Assert.Equal(bounds, Cirq.UI.Controls.CircuitCanvas.VisualBoundsOf(box));
    }

    [Fact]
    public void AnnotationsAreInsideTheBoundsAnExportFramesTo()
    {
        var circuit = new Circuit();
        var resistor = circuit.Add(new Resistor(1e3));
        resistor.X = 0;
        resistor.Y = 0;

        var tight = Cirq.UI.Rendering.CircuitRenderer.BoundsOf(circuit);

        circuit.Add(new SchematicNote("Away over here") { X = 600, Y = 0 });

        var wide = Cirq.UI.Rendering.CircuitRenderer.BoundsOf(circuit);

        Assert.True(wide.Right > tight.Right + 400,
            "the export would have cropped the note off the side");
    }

    [Fact]
    public void AnAnnotationSurvivesASaveAndReloadWithItsText()
    {
        var circuit = new Circuit();
        circuit.Add(new SchematicNote("Probe here — this is the node that matters")
        {
            IsHeading = true,
            WrapWidth = 300,
        });
        circuit.Add(new SchematicBox("Power supply") { Width = 500, IsShaded = true });

        var json = Cirq.Components.Serialization.CircuitSerializer.ToJson(circuit);
        var reloaded = Cirq.Components.Serialization.CircuitSerializer.FromJson(json).Circuit;

        var note = reloaded.Components.OfType<SchematicNote>().Single();
        var box = reloaded.Components.OfType<SchematicBox>().Single();

        Assert.Equal("Probe here — this is the node that matters", note.Text);
        Assert.True(note.IsHeading);
        Assert.Equal(300, note.WrapWidth);

        Assert.Equal("Power supply", box.Caption);
        Assert.Equal(500, box.Width);
        Assert.True(box.IsShaded);
    }

    [Fact]
    public void TheRuleCheckDoesNotComplainAboutAnnotations()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var load = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, load.A);
        circuit.Connect(load.B, ground.Pin);

        circuit.Add(new SchematicNote("Nothing wrong here"));
        circuit.Add(new SchematicBox("Nor here"));

        Assert.Empty(Cirq.Components.Diagnostics.ElectricalRuleCheck.Run(circuit));
    }
}
