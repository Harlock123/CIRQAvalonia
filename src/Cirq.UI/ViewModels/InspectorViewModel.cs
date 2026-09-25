using System.Collections.ObjectModel;
using System.Reflection;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.UI.ViewModels;

/// <summary>
/// Builds the properties panel for whatever is selected by reflecting over the component's public
/// settable properties. Every component therefore gets a full editor for free, and a new component
/// type needs no inspector code at all.
/// </summary>
public sealed partial class InspectorViewModel : ObservableObject
{
    /// <summary>Properties handled by the placement section, or not meaningful to edit by hand.</summary>
    private static readonly HashSet<string> Excluded =
    [
        nameof(CircuitComponent.Id),
        nameof(CircuitComponent.Name),
        nameof(CircuitComponent.X),
        nameof(CircuitComponent.Y),
        nameof(CircuitComponent.RotationDegrees),
        nameof(CircuitComponent.IsSelected),
        nameof(CircuitComponent.Terminals),
    ];

    /// <summary>
    /// The circuit the selected part belongs to, so a setting can be bound to one of its
    /// parameters. Null until the inspector is given one, and a part in a circuit it does not know
    /// about simply cannot be bound — it can still be edited as ordinary numbers.
    /// </summary>
    public Circuit? Circuit { get; set; }

    [ObservableProperty]
    public partial CircuitComponent? Component { get; set; }

    [ObservableProperty]
    public partial string Title { get; private set; } = "Nothing selected";

    [ObservableProperty]
    public partial string Subtitle { get; private set; } = "Select a component to edit its parameters";

    public ObservableCollection<ParameterViewModel> Placement { get; } = [];

    public ObservableCollection<ParameterViewModel> Parameters { get; } = [];

    public ObservableCollection<TerminalSummary> Terminals { get; } = [];

    /// <summary>Raised when an edit needs the canvas repainted or the engine rebuilt.</summary>
    public event EventHandler<bool>? ParameterChanged;

    partial void OnComponentChanged(CircuitComponent? value) => Rebuild(value);

    /// <summary>
    /// Re-reads every box from the part, for when something other than the inspector has changed
    /// it — a parameter being edited rewrites the values of every setting bound to it, and the
    /// boxes would otherwise go on showing what they last wrote.
    /// </summary>
    public void Refresh()
    {
        foreach (var parameter in Parameters.OfType<NumericParameterViewModel>()) parameter.Refresh();
    }

    private void Rebuild(CircuitComponent? component)
    {
        Placement.Clear();
        Parameters.Clear();
        Terminals.Clear();

        if (component is null)
        {
            Title = "Nothing selected";
            Subtitle = "Select a component to edit its parameters";
            return;
        }

        Title = component.Name;
        Subtitle = component.ComponentType;

        var type = component.GetType();

        Add(Placement, Build(component, type.GetProperty(nameof(CircuitComponent.Name))!, "Designator"));
        Add(Placement, Build(component, type.GetProperty(nameof(CircuitComponent.X))!, "X"));
        Add(Placement, Build(component, type.GetProperty(nameof(CircuitComponent.Y))!, "Y"));
        Add(Placement, Build(component, type.GetProperty(nameof(CircuitComponent.RotationDegrees))!, "Rotation"));

        // Only properties with a genuinely public setter are parameters. A private setter marks a
        // readout the component maintains itself — a decoded value, a measured current — and
        // letting the inspector write to those would both mislead and corrupt state.
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanRead && p.SetMethod is { IsPublic: true })
                     .Where(p => p.GetIndexParameters().Length == 0)
                     .Where(p => !Excluded.Contains(p.Name))
                     .OrderBy(p => p.Name))
        {
            Add(Parameters, Build(component, property, ParameterNaming.Humanise(property.Name)));
        }

        foreach (var terminal in component.Terminals)
            Terminals.Add(new TerminalSummary(terminal.Name, terminal.Type.ToString()));
    }

    private void Add(ObservableCollection<ParameterViewModel> target, ParameterViewModel? parameter)
    {
        if (parameter is null) return;

        parameter.ValueCommitted += (_, _) =>
        {
            // A model swap can change the pin roles, and renaming a net label changes what is
            // connected to what — so both rebuild the engine rather than being picked up on the
            // next time point.
            var structural =
                parameter is OptionParameterViewModel ||
                parameter.Owner is Cirq.Core.Topology.INetNaming;
            ParameterChanged?.Invoke(this, structural);
        };

        target.Add(parameter);
    }

    private ParameterViewModel? Build(CircuitComponent component, PropertyInfo property, string label)
    {
        var type = property.PropertyType;
        var underlying = Nullable.GetUnderlyingType(type);
        var isNullable = underlying is not null;
        var effective = underlying ?? type;

        if (effective == typeof(double) || effective == typeof(int))
            return new NumericParameterViewModel(
                component, property, label, ParameterNaming.UnitFor(property.Name), isNullable, Circuit);

        if (effective == typeof(bool))
            return new BooleanParameterViewModel(component, property, label);

        if (effective == typeof(string))
            return new TextParameterViewModel(component, property, label);

        if (effective.IsEnum)
            return new OptionParameterViewModel(component, property, label, Enum.GetValues(effective).Cast<object>().ToList());

        // Device models expose their presets through a static Library property.
        if (LibraryFor(effective) is { Count: > 0 } library)
            return new OptionParameterViewModel(component, property, label, library);

        return null;
    }

    private static IReadOnlyList<object>? LibraryFor(Type type)
    {
        var library = type.GetProperty("Library", BindingFlags.Public | BindingFlags.Static)
                      ?? (PropertyInfo?)null;

        var value = library?.GetValue(null)
                    ?? type.GetField("Library", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

        return value is System.Collections.IEnumerable items
            ? items.Cast<object>().ToList()
            : null;
    }

}

/// <summary>A read-only pin listing shown under the editable parameters.</summary>
public sealed record TerminalSummary(string Name, string Role);
