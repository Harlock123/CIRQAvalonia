using System.Reflection;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.UI.ViewModels;

/// <summary>Base for one editable row in the properties inspector.</summary>
public abstract partial class ParameterViewModel : ObservableObject
{
    protected ParameterViewModel(object target, PropertyInfo property, string label)
    {
        Target = target;
        Property = property;
        Label = label;
    }

    protected object Target { get; }

    /// <summary>
    /// What this parameter belongs to. Exposed so the inspector can tell a value change from a
    /// change of shape without knowing every part in the library.
    /// </summary>
    public object Owner => Target;

    protected PropertyInfo Property { get; }

    public string Label { get; }

    /// <summary>Raised after the value is written back, so the canvas and engine can react.</summary>
    public event EventHandler? ValueCommitted;

    protected void NotifyCommitted() => ValueCommitted?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// A numeric parameter edited as engineering notation, so "4k7", "100n" and "2.2M" are all valid
/// input rather than only bare decimals.
/// </summary>
public sealed partial class NumericParameterViewModel : ParameterViewModel
{
    private readonly bool _isNullable;

    public NumericParameterViewModel(object target, PropertyInfo property, string label, string unit, bool isNullable)
        : base(target, property, label)
    {
        Unit = unit;
        _isNullable = isNullable;
        Text = Format(property.GetValue(target));
    }

    public string Unit { get; }

    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; private set; }

    private string Format(object? raw) => raw switch
    {
        null => string.Empty,
        double d => SiPrefix.Format(d, Unit),
        _ => raw.ToString() ?? string.Empty,
    };

    partial void OnTextChanged(string value)
    {
        if (_isNullable && string.IsNullOrWhiteSpace(value))
        {
            Property.SetValue(Target, null);
            HasError = false;
            NotifyCommitted();
            return;
        }

        if (!SiPrefix.TryParse(value, out var parsed))
        {
            HasError = true;
            return;
        }

        HasError = false;

        // Parsing always yields a double, but the property may be an int or a float, so convert
        // to whatever it actually declares before writing.
        var target = Nullable.GetUnderlyingType(Property.PropertyType) ?? Property.PropertyType;
        Property.SetValue(Target, target == typeof(double)
            ? parsed
            : Convert.ChangeType(parsed, target, System.Globalization.CultureInfo.InvariantCulture));

        NotifyCommitted();
    }

    /// <summary>Re-reads the underlying value, used after the engine changes it.</summary>
    public void Refresh() => Text = Format(Property.GetValue(Target));
}

/// <summary>A boolean parameter rendered as a toggle switch.</summary>
public sealed partial class BooleanParameterViewModel : ParameterViewModel
{
    public BooleanParameterViewModel(object target, PropertyInfo property, string label)
        : base(target, property, label)
    {
        Value = property.GetValue(target) as bool? ?? false;
    }

    [ObservableProperty]
    public partial bool Value { get; set; }

    partial void OnValueChanged(bool value)
    {
        Property.SetValue(Target, value);
        NotifyCommitted();
    }
}

/// <summary>A parameter chosen from a fixed list: an enum, or a device model from its library.</summary>
public sealed partial class OptionParameterViewModel : ParameterViewModel
{
    public OptionParameterViewModel(
        object target, PropertyInfo property, string label, IReadOnlyList<object> options)
        : base(target, property, label)
    {
        Options = options;
        var current = property.GetValue(target);
        Selected = options.FirstOrDefault(o => Equals(o, current)) ?? current;
    }

    public IReadOnlyList<object> Options { get; }

    [ObservableProperty]
    public partial object? Selected { get; set; }

    partial void OnSelectedChanged(object? value)
    {
        if (value is null) return;
        Property.SetValue(Target, value);
        NotifyCommitted();
    }
}

/// <summary>A free-text parameter, used for the reference designator.</summary>
public sealed partial class TextParameterViewModel : ParameterViewModel
{
    public TextParameterViewModel(object target, PropertyInfo property, string label)
        : base(target, property, label)
    {
        Value = property.GetValue(target) as string ?? string.Empty;
    }

    [ObservableProperty]
    public partial string Value { get; set; }

    partial void OnValueChanged(string value)
    {
        Property.SetValue(Target, value);
        NotifyCommitted();
    }
}
