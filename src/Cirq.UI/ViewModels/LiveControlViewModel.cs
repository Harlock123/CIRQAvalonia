using System.Reflection;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.UI.ViewModels;

/// <summary>
/// One control a person can work while the circuit runs.
/// <para>
/// Writing to it sets the property on the component directly, which is all it takes: components
/// read their own properties while the engine stamps them, so the next time point is solved with
/// the new value. The simulation does not have to be stopped, restarted or told.
/// </para>
/// </summary>
public abstract partial class LiveControlViewModel : ObservableObject
{
    protected LiveControlViewModel(CircuitComponent component, PropertyInfo property, OperableAttribute operable)
    {
        Component = component;
        Property = property;
        Operable = operable;
    }

    public CircuitComponent Component { get; }

    protected PropertyInfo Property { get; }

    protected OperableAttribute Operable { get; }

    /// <summary>The part this belongs to, e.g. "SW1".</summary>
    public string Designator => Component.Name;

    /// <summary>What the control is called, e.g. "Closed" or "Wiper".</summary>
    public string Label => Operable.Label;

    /// <summary>
    /// True when the part contributes more than one control, so each row has to say which one it
    /// is. A DIP switch needs it on all eight rows; a single switch would only be repeating itself.
    /// </summary>
    public bool LabelInTitle { get; set; }

    /// <summary>The heading of the row.</summary>
    public string Title => LabelInTitle ? $"{Designator} · {Label}" : Designator;

    /// <summary>
    /// The line under it. The control's own name goes here when the title has not already used
    /// it, so a row reads "SW1 / Switch (SPST) · Closed" rather than saying "Closed" beside a
    /// toggle that is plainly off.
    /// </summary>
    public string Subtitle =>
        LabelInTitle ? Component.ComponentType : $"{Component.ComponentType} · {Label}";

    /// <summary>Raised after a value is written, so the engine and canvas can react.</summary>
    public event EventHandler<bool>? Changed;

    protected void Commit() => Changed?.Invoke(this, Operable.IsStructural);

    /// <summary>Re-reads the underlying property, for when something else has moved it.</summary>
    public abstract void Refresh();
}

/// <summary>A control that is on or off: a switch, a push button, a logic level.</summary>
public sealed partial class ToggleControlViewModel : LiveControlViewModel
{
    private bool _updating;

    public ToggleControlViewModel(CircuitComponent component, PropertyInfo property, OperableAttribute operable)
        : base(component, property, operable)
    {
        // Guarded, because the setter writes back. Without this, building the panel writes every
        // control's own value into the component it came from and reports each one as a change —
        // which marked a freshly opened document as modified before anybody had touched it.
        _updating = true;
        Value = (bool)(property.GetValue(component) ?? false);
        _updating = false;
    }

    [ObservableProperty]
    public partial bool Value { get; set; }

    public override void Refresh()
    {
        var current = (bool)(Property.GetValue(Component) ?? false);
        if (current == Value) return;

        _updating = true;
        Value = current;
        _updating = false;
    }

    partial void OnValueChanged(bool value)
    {
        if (_updating) return;

        Property.SetValue(Component, value);
        Commit();
    }
}

/// <summary>
/// A control with a range: a wiper, a temperature, a light level.
/// <para>
/// The slider works in a position from zero to one rather than in the value itself, because the
/// ranges are not all linear — light spans six decades, and a slider laid out linearly across
/// those puts everything a circuit actually responds to in the first half-millimetre of travel.
/// </para>
/// </summary>
public sealed partial class SliderControlViewModel : LiveControlViewModel
{
    private bool _updating;

    public SliderControlViewModel(CircuitComponent component, PropertyInfo property, OperableAttribute operable)
        : base(component, property, operable)
    {
        // As with the toggle: the setter writes back, so building the panel must not go through it.
        _updating = true;
        Position = ToPosition(Read());
        _updating = false;

        Display = Format(Read());
    }

    /// <summary>Where the slider sits, from zero to one.</summary>
    [ObservableProperty]
    public partial double Position { get; set; }

    /// <summary>The value the slider is at, written out with its unit.</summary>
    [ObservableProperty]
    public partial string Display { get; private set; }

    /// <summary>True when the range is wide enough that the slider is logarithmic.</summary>
    public bool IsLogarithmic => Operable.IsLogarithmic;

    private double Read() => Convert.ToDouble(Property.GetValue(Component) ?? 0.0);

    private double Minimum => Operable.Minimum;

    private double Maximum => Operable.Maximum;

    private double ToPosition(double value)
    {
        if (Maximum <= Minimum) return 0;

        if (!Operable.IsLogarithmic)
            return Math.Clamp((value - Minimum) / (Maximum - Minimum), 0, 1);

        var low = Math.Log10(Math.Max(Minimum, 1e-9));
        var high = Math.Log10(Math.Max(Maximum, Minimum * 10));

        return Math.Clamp((Math.Log10(Math.Max(value, 1e-9)) - low) / (high - low), 0, 1);
    }

    private double ToValue(double position)
    {
        if (!Operable.IsLogarithmic)
            return Minimum + (position * (Maximum - Minimum));

        var low = Math.Log10(Math.Max(Minimum, 1e-9));
        var high = Math.Log10(Math.Max(Maximum, Minimum * 10));

        return Math.Pow(10, low + (position * (high - low)));
    }

    private string Format(double value)
    {
        // Integers are counts of something — a detent, a position — and a decimal point on one
        // reads as a mistake.
        if (Property.PropertyType == typeof(int)) return value.ToString("0");

        var text = Math.Abs(value) >= 1000
            ? SiPrefix.Format(value, string.Empty)
            : value.ToString(Math.Abs(value) >= 10 ? "0.#" : "0.##");

        return string.IsNullOrEmpty(Operable.Unit) ? text : $"{text} {Operable.Unit}";
    }

    public override void Refresh()
    {
        var current = Read();
        Display = Format(current);

        var position = ToPosition(current);
        if (Math.Abs(position - Position) < 1e-6) return;

        _updating = true;
        Position = position;
        _updating = false;
    }

    partial void OnPositionChanged(double value)
    {
        if (_updating) return;

        var raw = ToValue(value);

        // Two statements rather than a conditional expression. int and double have a common type,
        // so a conditional unifies to double however it is assigned, and an int property handed a
        // boxed double is refused outright by SetValue.
        if (Property.PropertyType == typeof(int))
            Property.SetValue(Component, (int)Math.Round(raw));
        else
            Property.SetValue(Component, raw);

        Display = Format(raw);
        Commit();
    }
}
