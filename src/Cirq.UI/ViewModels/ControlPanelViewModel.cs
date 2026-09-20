using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reflection;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.UI.ViewModels;

/// <summary>
/// Every control in the circuit, gathered into one place.
/// <para>
/// Without this, working a running circuit means finding the part on the canvas and double-
/// clicking it, which is fine for one switch and hopeless for a board with eight. A real circuit's
/// controls are on its front panel, not scattered across its schematic, and this is that panel.
/// </para>
/// <para>
/// What appears here is decided by the components themselves: any property carrying
/// <see cref="OperableAttribute"/> shows up, so adding a control to a new part is one attribute
/// and nothing here changes.
/// </para>
/// </summary>
public sealed partial class ControlPanelViewModel : ObservableObject, IDisposable
{
    private readonly Circuit _circuit;

    public ControlPanelViewModel(Circuit circuit)
    {
        _circuit = circuit;
        _circuit.Components.CollectionChanged += OnComponentsChanged;

        Rebuild();
    }

    /// <summary>The controls, in the order the parts were placed.</summary>
    public ObservableCollection<LiveControlViewModel> Controls { get; } = [];

    /// <summary>True when the circuit has nothing to operate, so the panel can say so.</summary>
    public bool IsEmpty => Controls.Count == 0;

    /// <summary>
    /// Raised when a control is worked. The flag says whether the change altered the shape of the
    /// circuit rather than a value in it, which is the difference between rebuilding the engine's
    /// topology and simply carrying on.
    /// </summary>
    public event EventHandler<bool>? ControlChanged;

    /// <summary>
    /// Re-reads every control from its component. The parts can be worked from the canvas as
    /// well — double-clicking a switch still flips it — so the panel has to follow rather than
    /// assume it is the only thing touching them.
    /// </summary>
    public void Refresh()
    {
        foreach (var control in Controls) control.Refresh();
    }

    private void OnComponentsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        foreach (var control in Controls) control.Changed -= OnControlChanged;

        Controls.Clear();

        foreach (var component in _circuit.Components)
        {
            var operables = OperableProperties(component.GetType());

            foreach (var (property, operable) in operables)
            {
                var control = Create(component, property, operable);
                if (control is null) continue;

                // Only worth naming the control in the heading when the part has more than one.
                control.LabelInTitle = operables.Count > 1;
                control.Changed += OnControlChanged;

                Controls.Add(control);
            }
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private void OnControlChanged(object? sender, bool structural) =>
        ControlChanged?.Invoke(this, structural);

    private static LiveControlViewModel? Create(
        CircuitComponent component, PropertyInfo property, OperableAttribute operable)
    {
        if (property.PropertyType == typeof(bool))
            return new ToggleControlViewModel(component, property, operable);

        if (property.PropertyType == typeof(double) || property.PropertyType == typeof(int))
            return operable.HasRange ? new SliderControlViewModel(component, property, operable) : null;

        return null;
    }

    /// <summary>
    /// The operable properties of a type, declared order. Cached because the panel is rebuilt
    /// every time a component is placed or removed, and reflection is not free.
    /// </summary>
    private static readonly Dictionary<Type, IReadOnlyList<(PropertyInfo, OperableAttribute)>> Cache = [];

    public static IReadOnlyList<(PropertyInfo Property, OperableAttribute Operable)> OperableProperties(Type type)
    {
        if (Cache.TryGetValue(type, out var cached)) return cached;

        List<(PropertyInfo, OperableAttribute)> found = [];

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var operable = property.GetCustomAttribute<OperableAttribute>();
            if (operable is null || !property.CanWrite || !property.CanRead) continue;

            found.Add((property, operable));
        }

        Cache[type] = found;
        return found;
    }

    public void Dispose()
    {
        _circuit.Components.CollectionChanged -= OnComponentsChanged;

        foreach (var control in Controls) control.Changed -= OnControlChanged;
    }
}
