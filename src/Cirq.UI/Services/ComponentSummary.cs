using System.Reflection;
using Cirq.Components.Serialization;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Services;

/// <summary>One line of a hover card: a property and what it is set to.</summary>
public sealed record SummaryRow(string Label, string Value);

/// <summary>
/// What a component says about itself when the pointer rests on it — its designator and type, what
/// it is currently doing, the settings that matter, and anything it is unhappy about.
/// <para>
/// The point of it is the question "what is this one set to?", which otherwise costs a click to
/// select the part and a look across at the properties panel. On a schematic with eight resistors
/// in it that is eight clicks to find the one that is 4k7.
/// </para>
/// <para>
/// Built here rather than in the canvas so it can be tested without a window, and read through the
/// same reflection the properties panel and the file format use — a part that gains a setting gains
/// it here too, with nothing to keep in step.
/// </para>
/// </summary>
/// <param name="Marking">
/// How the value is written on the part itself — the bands on a resistor, the code on a ceramic
/// capacitor — or null for a part that carries no such marking.
/// </param>
public sealed record ComponentSummary(
    string Title,
    string Subtitle,
    IReadOnlyList<SummaryRow> Rows,
    IReadOnlyList<string> Warnings,
    ComponentMarking? Marking = null)
{
    /// <summary>
    /// How many settings are listed before the card gives up and says how many are left. A card
    /// taller than the part it describes stops being a glance and starts being a panel.
    /// </summary>
    public const int MaximumRows = 7;

    private static readonly Dictionary<Type, PropertyInfo?> ViolationProperties = [];

    public static ComponentSummary For(CircuitComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);

        List<SummaryRow> rows = [];
        var hidden = 0;

        foreach (var property in Ordered(component.GetType()))
        {
            var value = Describe(property, component);
            if (value is null) continue;

            if (rows.Count == MaximumRows) { hidden++; continue; }

            rows.Add(new SummaryRow(ParameterNaming.Humanise(property.Name), value));
        }

        if (hidden > 0) rows.Add(new SummaryRow(string.Empty, $"+{hidden} more"));

        return new ComponentSummary(
            $"{component.Name} · {component.ComponentType}",
            component.ValueLabel,
            rows,
            ViolationsOf(component),
            ComponentMarkings.For(component));
    }

    /// <summary>
    /// The settings in the order they are worth reading, which is not alphabetical.
    /// <para>
    /// Whatever the part marks <see cref="OperableAttribute"/> comes first, because those are the
    /// ones its author singled out as the things you work — a generator's frequency and amplitude
    /// rather than its small-signal analysis magnitude. Sorted alphabetically, a generator's card
    /// led with <c>Ac Magnitude</c> and <c>Ac Phase Degrees</c> and had run out of room before it
    /// reached the waveform.
    /// </para>
    /// </summary>
    private static IEnumerable<PropertyInfo> Ordered(Type type)
    {
        var properties = ComponentReflection.EditableProperties(type).ToList();
        var operable = ControlPanelViewModel.OperableProperties(type).Select(p => p.Property.Name).ToHashSet();

        return properties.Where(p => operable.Contains(p.Name))
            .Concat(properties.Where(p => !operable.Contains(p.Name)));
    }

    /// <summary>
    /// A property's value as it should read on the card, or null when it is not worth a line —
    /// an empty string, or a nullable that has not been set.
    /// </summary>
    private static string? Describe(PropertyInfo property, CircuitComponent component)
    {
        object? value;

        try
        {
            value = property.GetValue(component);
        }
        catch (TargetInvocationException)
        {
            // A property that throws while being looked at is not worth taking the card down for.
            return null;
        }

        return value switch
        {
            null => null,
            bool flag => flag ? "yes" : "no",
            double number => ParameterNaming.Format(number, ParameterNaming.UnitFor(property.Name)),
            int number => number.ToString(),
            string text => text.Length == 0 ? null : Shorten(text),
            _ => Shorten(value.ToString() ?? string.Empty),
        };
    }

    /// <summary>
    /// Long values are cut. A board's pin script or a bus transaction list runs to hundreds of
    /// characters, and a card is not where anybody reads those.
    /// </summary>
    private static string Shorten(string text, int limit = 40)
    {
        var flattened = text.ReplaceLineEndings(" ").Trim();

        return flattened.Length <= limit ? flattened : flattened[..(limit - 1)] + "…";
    }

    /// <summary>
    /// Whatever the part is reporting as wrong with how it is being used.
    /// <para>
    /// Found by reflection because <c>Violations</c> is declared on the twenty-five types that
    /// have something to say rather than on the base class, and a card that showed them for some
    /// parts and not others would be worse than one that showed none. This is the one place in the
    /// UI that wants them generically; the alternative is an interface on every one of those
    /// types for the sake of a tooltip.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> ViolationsOf(CircuitComponent component)
    {
        var type = component.GetType();

        if (!ViolationProperties.TryGetValue(type, out var property))
        {
            property = type.GetProperty("Violations", BindingFlags.Public | BindingFlags.Instance);

            if (property is not null && !typeof(IEnumerable<string>).IsAssignableFrom(property.PropertyType))
                property = null;

            ViolationProperties[type] = property;
        }

        if (property is null) return [];

        try
        {
            return property.GetValue(component) is IEnumerable<string> found ? [.. found] : [];
        }
        catch (TargetInvocationException)
        {
            return [];
        }
    }
}
