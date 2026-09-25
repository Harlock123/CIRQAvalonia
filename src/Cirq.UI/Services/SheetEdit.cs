using System.Reflection;
using Cirq.Components.Serialization;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Services;

/// <summary>What a bulk edit did, or why it did not.</summary>
/// <param name="Changed">How many parts were altered.</param>
/// <param name="Skipped">
/// Parts the edit did not apply to, and why — a part with no such setting, a value that would not
/// parse. Named rather than counted, because "three parts were skipped" is the beginning of a
/// question rather than the end of one.
/// </param>
public sealed record SheetEditResult(int Changed, IReadOnlyList<string> Skipped)
{
    public static SheetEditResult Nothing { get; } = new(0, []);

    /// <summary>The result in a sentence.</summary>
    public string Summary() => Changed switch
    {
        0 when Skipped.Count == 0 => "Nothing to change.",
        0 => $"Nothing changed. {string.Join(" ", Skipped)}",
        1 when Skipped.Count == 0 => "One part changed.",
        _ when Skipped.Count == 0 => $"{Changed} parts changed.",
        _ => $"{Changed} changed. {string.Join(" ", Skipped)}",
    };
}

/// <summary>
/// Changing several parts at once.
/// <para>
/// Finding things on the sheet has been possible for a while and changing what was found has not,
/// so "every 10k becomes 12k" or "give these eight a one percent tolerance" meant clicking each in
/// turn and editing it — which is slow, and which is how one of them ends up different from the
/// others without anybody noticing.
/// </para>
/// <para>
/// The second edit here is the more interesting one, and it exists because parameters do. A circuit
/// drawn before parameters existed has its choices typed into a dozen boxes as literals, and no
/// amount of naming a parameter afterwards connects them to it. Binding them in bulk is the
/// migration path — without it, parameters only ever help circuits started after them.
/// </para>
/// </summary>
public static class SheetEdit
{
    /// <summary>
    /// Sets one property on every part given, from text the properties panel would accept.
    /// </summary>
    public static SheetEditResult Set(
        IEnumerable<CircuitComponent> parts, string property, string value)
    {
        ArgumentNullException.ThrowIfNull(parts);

        if (string.IsNullOrWhiteSpace(property)) return SheetEditResult.Nothing;

        if (!SiPrefix.TryParse(value, out var number))
            return new SheetEditResult(0, [$"\"{value}\" is not a number."]);

        var changed = 0;
        List<string> skipped = [];

        foreach (var part in parts)
        {
            var target = Writable(part, property);

            if (target is null)
            {
                skipped.Add($"{part.Name} has no {ParameterNaming.Humanise(property)}.");
                continue;
            }

            // A binding and a typed value are two ways of setting the same thing, and setting it
            // by hand is the way to say you no longer want the binding.
            part.Expressions.Remove(target.Name);

            Write(part, target, number);
            changed++;
        }

        return new SheetEditResult(changed, skipped);
    }

    /// <summary>
    /// Binds one property on every part given to an expression over the circuit's parameters,
    /// which is how a circuit full of literals becomes a circuit with a parameter in it.
    /// </summary>
    public static SheetEditResult Bind(
        Circuit circuit, IEnumerable<CircuitComponent> parts, string property, string expression)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(parts);

        if (string.IsNullOrWhiteSpace(property) || string.IsNullOrWhiteSpace(expression))
            return SheetEditResult.Nothing;

        var chosen = parts.ToList();
        var changed = 0;
        List<string> skipped = [];

        foreach (var part in chosen)
        {
            var target = Writable(part, property);

            if (target is null)
            {
                skipped.Add($"{part.Name} has no {ParameterNaming.Humanise(property)}.");
                continue;
            }

            part.Expressions[target.Name] = expression.Trim();
            changed++;
        }

        if (changed == 0) return new SheetEditResult(0, skipped);

        // Worked out straight away, so a mistake is reported here rather than the next time the
        // circuit happens to be opened.
        foreach (var (_, problem) in CircuitParameters.Apply(circuit.Parameters, circuit.Components).Problems)
            skipped.Add(problem);

        return new SheetEditResult(changed, skipped);
    }

    /// <summary>
    /// Takes the value these parts already have, gives it a name, and binds them all to it.
    /// <para>
    /// The one-click version of the migration: four resistors that are all 10k become a parameter
    /// called whatever you say and four bindings to it. Refused when they do not all agree, because
    /// a name for a number they do not share would be a name for a number that is not there.
    /// </para>
    /// </summary>
    public static SheetEditResult Extract(
        Circuit circuit, IEnumerable<CircuitComponent> parts, string property, string name)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(parts);

        if (string.IsNullOrWhiteSpace(name)) return SheetEditResult.Nothing;

        var chosen = parts.ToList();

        List<double> values = [];
        List<string> skipped = [];

        foreach (var part in chosen)
        {
            var target = Writable(part, property);

            if (target is null || target.GetValue(part) is not { } raw)
            {
                skipped.Add($"{part.Name} has no {ParameterNaming.Humanise(property)}.");
                continue;
            }

            values.Add(Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture));
        }

        if (values.Count == 0) return new SheetEditResult(0, skipped);

        var first = values[0];

        if (values.Any(v => Math.Abs(v - first) > Math.Abs(first) * 1e-9))
        {
            return new SheetEditResult(0,
                [$"These parts are not all set to the same {ParameterNaming.Humanise(property)}, " +
                 "so there is no one value to give a name to."]);
        }

        if (circuit.Parameters.Any(p => p.Name == name.Trim()))
            return new SheetEditResult(0, [$"There is already a parameter called \"{name.Trim()}\"."]);

        circuit.Parameters.Add(new CircuitParameter
        {
            Name = name.Trim(),
            Expression = SiPrefix.Format(first),
            Note = $"was typed into {chosen.Count} part{(chosen.Count == 1 ? "" : "s")}",
        });

        return Bind(circuit, chosen, property, name.Trim());
    }

    /// <summary>
    /// The property by that name, if the part has one and it can be written as a number. Matched
    /// without case, because somebody typing "resistance" means <c>Resistance</c>.
    /// </summary>
    private static PropertyInfo? Writable(CircuitComponent part, string property) =>
        ComponentReflection.EditableProperties(part.GetType())
            .FirstOrDefault(p => p.CanWrite
                && string.Equals(p.Name, property.Trim(), StringComparison.OrdinalIgnoreCase)
                && (Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType) is var t
                && (t == typeof(double) || t == typeof(int)));

    private static void Write(CircuitComponent part, PropertyInfo property, double value)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        property.SetValue(part, type == typeof(int)
            ? (int)Math.Round(value)
            : Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The settings every one of these parts has in common, so a picker can offer only the ones
    /// that could be changed for all of them.
    /// </summary>
    public static IReadOnlyList<string> SharedProperties(IEnumerable<CircuitComponent> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var chosen = parts.ToList();

        if (chosen.Count == 0) return [];

        IEnumerable<string> Numbers(CircuitComponent part) =>
            ComponentReflection.EditableProperties(part.GetType())
                .Where(p => p.CanWrite)
                .Where(p => (Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType) is var t
                            && (t == typeof(double) || t == typeof(int)))
                .Where(p => p.Name is not ("X" or "Y" or "RotationDegrees"))
                .Select(p => p.Name);

        var shared = Numbers(chosen[0]).ToHashSet(StringComparer.Ordinal);

        foreach (var part in chosen.Skip(1)) shared.IntersectWith(Numbers(part));

        return [.. shared.OrderBy(n => n, StringComparer.Ordinal)];
    }
}
