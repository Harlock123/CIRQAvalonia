using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.UI.Controls;
using Cirq.UI.Tests.Support;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// Editing the sheet without a mouse.
/// <para>
/// The canvas has always answered R, W, V, P, F, Delete and Escape, and has always needed a pointer
/// for everything that actually changes the drawing: nothing moved a part, nothing moved between
/// parts, nothing placed one. That is a fair thing for somebody to check before calling an
/// application finished, and it is also simply faster than aiming a drag.
/// </para>
/// <para>
/// These press real keys at a real canvas inside a real window, so what is tested is the path the
/// keyboard actually takes rather than the methods underneath it.
/// </para>
/// </summary>
[Collection(WindowCollection.Name)]
public class KeyboardEditingTests(WindowSession session)
{
    private static (Window Window, CircuitCanvas Canvas, Circuit Circuit) Sheet(int parts = 3)
    {
        var circuit = new Circuit();

        for (var i = 0; i < parts; i++)
            circuit.Add(new Resistor(1e3) { Name = $"R{i + 1}", X = 100 + (i * 80), Y = 100 });

        var canvas = new CircuitCanvas
        {
            Circuit = circuit,
            GridSize = 10,
            Zoom = 1.0,
            SnapToGrid = true,
        };

        var window = new Window { Width = 800, Height = 600, Content = canvas };

        window.Show();

        canvas.Measure(new Size(800, 600));
        canvas.Arrange(new Rect(0, 0, 800, 600));
        canvas.Focus();

        return (window, canvas, circuit);
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    /// <summary>An arrow moves the selection by one grid square.</summary>
    [Theory]
    [InlineData(Key.Right, 10, 0)]
    [InlineData(Key.Left, -10, 0)]
    [InlineData(Key.Down, 0, 10)]
    [InlineData(Key.Up, 0, -10)]
    public void AnArrowMovesTheSelectionByOneSquare(Key key, double dx, double dy) => session.Run(() =>
    {
        var (window, canvas, circuit) = Sheet();

        var part = circuit.Components[0];

        canvas.SetSelection([part]);

        var (wasX, wasY) = (part.X, part.Y);

        Press(window, key);

        Assert.Equal(wasX + dx, part.X, 1e-9);
        Assert.Equal(wasY + dy, part.Y, 1e-9);

        window.Close();
    });

    /// <summary>
    /// And with Shift held it moves by one unit, which is the only way to put something off the
    /// grid deliberately.
    /// </summary>
    [Fact]
    public void ShiftMovesItByOneUnit() => session.Run(() =>
    {
        var (window, canvas, circuit) = Sheet();

        var part = circuit.Components[0];

        canvas.SetSelection([part]);

        var wasX = part.X;

        Press(window, Key.Right, RawInputModifiers.Shift);

        Assert.Equal(wasX + 1, part.X, 1e-9);

        window.Close();
    });

    /// <summary>Everything selected moves together, not just the one the inspector is showing.</summary>
    [Fact]
    public void AWholeSelectionMovesTogether() => session.Run(() =>
    {
        var (window, canvas, circuit) = Sheet();

        canvas.SetSelection(circuit.Components);

        var was = circuit.Components.Select(c => c.X).ToList();

        Press(window, Key.Right);

        Assert.Equal(was.Select(x => x + 10), circuit.Components.Select(c => c.X));

        window.Close();
    });

    /// <summary>Nothing selected, nothing moves — and nothing throws.</summary>
    [Fact]
    public void WithNothingSelectedNothingMoves() => session.Run(() =>
    {
        var (window, _, circuit) = Sheet();

        var was = circuit.Components.Select(c => (c.X, c.Y)).ToList();

        Press(window, Key.Right);

        Assert.Equal(was, circuit.Components.Select(c => (c.X, c.Y)));

        window.Close();
    });

    /// <summary>Tab steps through the parts, and wraps.</summary>
    [Fact]
    public void TabStepsThroughThePartsAndWraps() => session.Run(() =>
    {
        var (window, canvas, circuit) = Sheet();

        Press(window, Key.Tab);
        Assert.Equal(circuit.Components[0], canvas.SelectedComponent);

        Press(window, Key.Tab);
        Assert.Equal(circuit.Components[1], canvas.SelectedComponent);

        Press(window, Key.Tab);
        Press(window, Key.Tab);

        Assert.Equal(circuit.Components[0], canvas.SelectedComponent);

        window.Close();
    });

    /// <summary>And Shift+Tab goes the other way, starting from the end.</summary>
    [Fact]
    public void ShiftTabGoesBackwards() => session.Run(() =>
    {
        var (window, canvas, circuit) = Sheet();

        Press(window, Key.Tab, RawInputModifiers.Shift);

        Assert.Equal(circuit.Components[^1], canvas.SelectedComponent);

        Press(window, Key.Tab, RawInputModifiers.Shift);

        Assert.Equal(circuit.Components[^2], canvas.SelectedComponent);

        window.Close();
    });

    /// <summary>
    /// Stepping to a part that is off screen brings it into view, and stepping between parts that
    /// are already on screen leaves the view alone — moving the drawing about under somebody who
    /// is reading it is worse than not scrolling at all.
    /// </summary>
    [Fact]
    public void SteppingScrollsOnlyWhenItHasTo() => session.Run(() =>
    {
        var (window, canvas, circuit) = Sheet(parts: 2);

        // The second part is a long way off to the right.
        circuit.Components[1].X = 5000;

        canvas.SetSelection([circuit.Components[0]]);

        var before = canvas.WorldToScreen(new Cirq.Core.Primitives.Point(0, 0));

        Press(window, Key.Tab);

        var after = canvas.WorldToScreen(new Cirq.Core.Primitives.Point(0, 0));

        Assert.NotEqual(before.X, after.X);

        // Back to the first, which is now off to the left, so it scrolls again.
        Press(window, Key.Tab);

        Assert.Equal(circuit.Components[0], canvas.SelectedComponent);

        window.Close();
    });

    /// <summary>Enter drops an armed part in the middle of the view.</summary>
    [Fact]
    public void EnterPlacesTheArmedPart() => session.Run(() =>
    {
        var (window, canvas, circuit) = Sheet(parts: 0);

        canvas.PendingItem = new PaletteItem("Resistor", "A resistor", () => new Resistor(1e3));

        Press(window, Key.Enter);

        var placed = Assert.Single(circuit.Components);

        Assert.IsType<Resistor>(placed);

        window.Close();
    });

    /// <summary>With nothing armed, Enter does nothing rather than dropping something.</summary>
    [Fact]
    public void WithNothingArmedEnterPlacesNothing() => session.Run(() =>
    {
        var (window, _, circuit) = Sheet(parts: 0);

        Press(window, Key.Enter);

        Assert.Empty(circuit.Components);

        window.Close();
    });

    /// <summary>
    /// Annotations are skipped when stepping: a note is on the drawing rather than in the circuit,
    /// and stepping through eight of them to reach a resistor is not stepping through the parts.
    /// </summary>
    [Fact]
    public void SteppingSkipsAnnotations() => session.Run(() =>
    {
        var (window, canvas, circuit) = Sheet(parts: 1);

        circuit.Add(new Cirq.Components.Annotations.SchematicNote("a note") { Name = "N1" });

        Press(window, Key.Tab);
        Press(window, Key.Tab);

        Assert.Equal("R1", canvas.SelectedComponent?.Name);

        window.Close();
    });
}
