using System.Reflection;
using Cirq.Core.Topology;
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

    public NumericParameterViewModel(
        object target, PropertyInfo property, string label, string unit, bool isNullable,
        Circuit? circuit = null)
        : base(target, property, label)
    {
        Unit = unit;
        _isNullable = isNullable;
        _circuit = circuit;

        Text = Bound is { } expression ? Prefix + expression : Format(property.GetValue(target));
    }

    private readonly Circuit? _circuit;

    /// <summary>
    /// What a value has to start with to be an expression rather than a number.
    /// <para>
    /// A spreadsheet's convention, and used here for a spreadsheet's reason: the box already
    /// accepts <c>4k7</c> and <c>100n</c>, so an expression needs a mark that cannot be confused
    /// with a value. Nothing anybody would type as a resistance begins with an equals sign.
    /// </para>
    /// </summary>
    public const string Prefix = "=";

    /// <summary>The expression driving this setting, or null when it is an ordinary typed number.</summary>
    private string? Bound =>
        Target is CircuitComponent part && part.Expressions.TryGetValue(Property.Name, out var e)
            ? e
            : null;

    /// <summary>True when this setting is driven by a parameter rather than typed in.</summary>
    public bool IsBound => Bound is not null;

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
        // An expression rather than a number: remember it on the part, work it out, and let the
        // ordinary path below write whatever it came to. The property itself always holds a plain
        // number, so everything that reads a component goes on reading one.
        if (value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            if (Target is not CircuitComponent part || _circuit is null)
            {
                HasError = true;
                return;
            }

            var expression = value[Prefix.Length..].Trim();

            part.Expressions[Property.Name] = expression;

            var applied = CircuitParameters.Apply(_circuit.Parameters, _circuit.Components);

            HasError = applied.Problems.ContainsKey($"{part.Name}.{Property.Name}");

            OnPropertyChanged(nameof(IsBound));
            NotifyCommitted();
            return;
        }

        // Typing a plain number over an expression breaks the binding, which is the only way to
        // undo one and the way somebody would expect to.
        if (Target is CircuitComponent owner && owner.Expressions.Remove(Property.Name))
            OnPropertyChanged(nameof(IsBound));

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
    public void Refresh() =>
        Text = Bound is { } expression ? Prefix + expression : Format(Property.GetValue(Target));

    /// <summary>What the expression currently works out to, for the hint beside the box.</summary>
    public string Worked => Bound is null ? string.Empty : Format(Property.GetValue(Target));
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
