using Cirq.Components.Passive;
using Cirq.Core.Topology;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The controls panel is a front panel for the circuit, so what it shows has to be what the
/// components actually say — and working it has to move them, without moving anything else.
/// </summary>
public class ControlPanelTests
{
    private static Circuit With(params CircuitComponent[] components)
    {
        var circuit = new Circuit();
        foreach (var c in components) circuit.Add(c);
        return circuit;
    }

    /// <summary>
    /// Building the panel must not disturb what it is looking at. A closed switch is still closed
    /// after the panel has been built round it.
    /// </summary>
    [Fact]
    public void BuildingThePanelDoesNotChangeAnything()
    {
        var closed = new ToggleSwitch { IsClosed = true };
        var open = new ToggleSwitch { IsClosed = false };

        using var panel = new ControlPanelViewModel(With(closed, open));

        Assert.True(closed.IsClosed);
        Assert.False(open.IsClosed);

        var controls = panel.Controls.OfType<ToggleControlViewModel>().ToList();

        Assert.Equal(2, controls.Count);
        Assert.True(controls[0].Value);
        Assert.False(controls[1].Value);
    }

    /// <summary>
    /// Nor may building it raise a change. The setters write back, so an unguarded constructor
    /// reported every control as edited and marked a freshly opened document modified before
    /// anybody had touched it.
    /// </summary>
    [Fact]
    public void BuildingThePanelRaisesNoChanges()
    {
        var circuit = With(new ToggleSwitch { IsClosed = true }, new Potentiometer { Position = 0.4 });

        using var panel = new ControlPanelViewModel(circuit);

        var raised = 0;
        panel.ControlChanged += (_, _) => raised++;

        // Nothing has been worked, so nothing should have been reported. Rebuilding counts too.
        circuit.Add(new ToggleSwitch());

        Assert.Equal(0, raised);
    }

    /// <summary>And a slider must not move the value it was built to show.</summary>
    [Fact]
    public void BuildingASliderDoesNotMoveIt()
    {
        var pot = new Potentiometer { Position = 0.37 };
        var ldr = new LightDependentResistor { Illuminance = 1234 };

        using var panel = new ControlPanelViewModel(With(pot, ldr));

        Assert.Equal(0.37, pot.Position, 1e-9);
        Assert.Equal(1234, ldr.Illuminance, 1e-6);
    }

    /// <summary>
    /// A control over a whole-number property has to write a whole number back. Written as a
    /// conditional expression the two branches unify to double, and the property refuses it.
    /// </summary>
    [Fact]
    public void AWholeNumberControlWritesAWholeNumber()
    {
        var encoder = new RotaryEncoder();
        using var panel = new ControlPanelViewModel(With(encoder));

        var control = Assert.Single(panel.Controls.OfType<SliderControlViewModel>());

        control.Position = 0.75;

        Assert.Equal(25, encoder.Detent);
    }

    /// <summary>Working a control moves the component it belongs to.</summary>
    [Fact]
    public void WorkingAControlMovesTheComponent()
    {
        var part = new ToggleSwitch { IsClosed = false };
        using var panel = new ControlPanelViewModel(With(part));

        var control = Assert.Single(panel.Controls.OfType<ToggleControlViewModel>());
        control.Value = true;

        Assert.True(part.IsClosed);
    }

    /// <summary>And it says so, because the engine may need to rebuild rather than carry on.</summary>
    [Fact]
    public void WorkingAControlRaisesItsChange()
    {
        var part = new ToggleSwitch();
        using var panel = new ControlPanelViewModel(With(part));

        var raised = 0;
        panel.ControlChanged += (_, _) => raised++;

        panel.Controls.OfType<ToggleControlViewModel>().Single().Value = true;

        Assert.Equal(1, raised);
    }

    /// <summary>
    /// The parts can be worked from the canvas too, so the panel follows them rather than
    /// assuming it is the only thing touching them.
    /// </summary>
    [Fact]
    public void ThePanelFollowsTheComponentWhenSomethingElseMovesIt()
    {
        var part = new ToggleSwitch { IsClosed = false };
        using var panel = new ControlPanelViewModel(With(part));

        var control = panel.Controls.OfType<ToggleControlViewModel>().Single();
        Assert.False(control.Value);

        // As a double-click on the canvas would.
        part.Interact();
        panel.Refresh();

        Assert.True(control.Value);
    }

    /// <summary>Following a component must not then write back and raise a change of its own.</summary>
    [Fact]
    public void FollowingDoesNotRaiseAChange()
    {
        var part = new ToggleSwitch();
        using var panel = new ControlPanelViewModel(With(part));

        var raised = 0;
        panel.ControlChanged += (_, _) => raised++;

        part.Interact();
        panel.Refresh();

        Assert.Equal(0, raised);
    }

    /// <summary>A range comes out as a slider, and the slider's travel spans the range.</summary>
    [Fact]
    public void ARangeBecomesASlider()
    {
        var pot = new Potentiometer { Position = 0.25 };
        using var panel = new ControlPanelViewModel(With(pot));

        var control = Assert.Single(panel.Controls.OfType<SliderControlViewModel>());

        Assert.Equal(0.25, control.Position, 0.001);

        control.Position = 0.75;
        Assert.Equal(0.75, pot.Position, 0.001);
    }

    /// <summary>
    /// A range spanning six decades is laid out logarithmically, or everything a circuit responds
    /// to would sit in the first half-millimetre of the slider.
    /// </summary>
    [Fact]
    public void AWideRangeIsLogarithmic()
    {
        var ldr = new LightDependentResistor { Illuminance = 100 };
        using var panel = new ControlPanelViewModel(With(ldr));

        var control = Assert.Single(panel.Controls.OfType<SliderControlViewModel>());
        Assert.True(control.IsLogarithmic);

        // Halfway along a 0.1 to 100000 log slider is about a hundred lux, not fifty thousand.
        control.Position = 0.5;

        Assert.InRange(ldr.Illuminance, 50, 200);
    }

    /// <summary>A circuit with nothing to operate says so rather than showing an empty box.</summary>
    [Fact]
    public void ACircuitWithNothingToOperateIsEmpty()
    {
        using var panel = new ControlPanelViewModel(With(new Resistor(1e3)));

        Assert.True(panel.IsEmpty);
        Assert.Empty(panel.Controls);
    }

    /// <summary>Placing a part with a control adds one; removing it takes it away again.</summary>
    [Fact]
    public void ThePanelFollowsTheCircuit()
    {
        var circuit = With(new Resistor(1e3));
        using var panel = new ControlPanelViewModel(circuit);

        Assert.True(panel.IsEmpty);

        var part = new ToggleSwitch();
        circuit.Add(part);

        Assert.Single(panel.Controls);

        circuit.Components.Remove(part);

        Assert.True(panel.IsEmpty);
    }

    /// <summary>Eight switches in one package come out as eight controls, each named for its position.</summary>
    [Fact]
    public void EveryPositionOfADipSwitchGetsItsOwnControl()
    {
        using var panel = new ControlPanelViewModel(With(new DipSwitch()));

        Assert.Equal(8, panel.Controls.Count);
        Assert.Equal(8, panel.Controls.Select(c => c.Label).Distinct().Count());
    }
}
