using System.Reflection;
using Cirq.Core.Topology;

namespace Cirq.Components.Serialization;

/// <summary>
/// The single definition of what counts as a component's editable parameter.
/// <para>
/// Both the properties inspector and the file format are built on this, deliberately: if a value
/// can be edited it is saved, and if it is saved it can be edited. Letting those two drift apart
/// is how you end up with a setting that silently resets when a circuit is reopened.
/// </para>
/// </summary>
public static class ComponentReflection
{
    /// <summary>
    /// Handled by placement or identity rather than as a parameter, or maintained by the component
    /// itself and not meaningful to write to.
    /// </summary>
    private static readonly HashSet<string> Excluded =
    [
        nameof(CircuitComponent.Id),
        nameof(CircuitComponent.Name),
        nameof(CircuitComponent.X),
        nameof(CircuitComponent.Y),
        nameof(CircuitComponent.RotationDegrees),
        nameof(CircuitComponent.IsSelected),

        // Which page it is drawn on is placement, like X and Y, and is written as a field of the
        // part's record rather than as one of its settings. Left in, it would appear in the
        // properties panel as a thing to type into and in the file twice.
        nameof(CircuitComponent.Sheet),
        nameof(CircuitComponent.Terminals),
    ];

    /// <summary>
    /// Parameters of a component: public properties with a genuinely public setter. A private
    /// setter marks a readout the component maintains itself — a decoded value, a measured
    /// current — which must be neither edited nor persisted.
    /// </summary>
    public static IEnumerable<PropertyInfo> EditableProperties(Type componentType) =>
        componentType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.SetMethod is { IsPublic: true })
            .Where(p => p.GetIndexParameters().Length == 0)
            .Where(p => !Excluded.Contains(p.Name))
            .OrderBy(p => p.Name, StringComparer.Ordinal);

    /// <summary>
    /// The preset list a device-model type exposes as a static <c>Library</c>, or null when the
    /// type is not one. Models are persisted and chosen by name against this list.
    /// </summary>
    public static IReadOnlyList<object>? ModelLibrary(Type type)
    {
        var value = type.GetProperty("Library", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                    ?? type.GetField("Library", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

        return value is System.Collections.IEnumerable items ? items.Cast<object>().ToList() : null;
    }

    /// <summary>True when a property holds a device model rather than a plain value.</summary>
    public static bool IsModelProperty(PropertyInfo property) =>
        ModelLibrary(Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) is { Count: > 0 };

    /// <summary>
    /// Constructs a component by its type, tolerating constructors whose parameters are all
    /// optional — which most of the library uses, since <c>new Resistor()</c> reads better than
    /// requiring a value at the call site.
    /// </summary>
    public static CircuitComponent Instantiate(Type componentType)
    {
        var constructor = componentType.GetConstructors()
            .Where(c => c.GetParameters().All(p => p.HasDefaultValue))
            .MinBy(c => c.GetParameters().Length)
            ?? throw new InvalidOperationException(
                $"{componentType.Name} has no constructor that can be called without arguments.");

        var arguments = constructor.GetParameters().Select(p => p.DefaultValue).ToArray();
        return (CircuitComponent)constructor.Invoke(arguments);
    }
}
