using System.Globalization;
using Cirq.Core.Topology;

namespace Cirq.Components.Serialization;

/// <summary>What kind of change a line of a comparison describes.</summary>
public enum ChangeKind
{
    Added,
    Removed,
    Moved,
    Renamed,
    ValueChanged,
    Rewired,
    ProbeChanged,
    SettingChanged,
}

/// <summary>One difference between two versions of a circuit.</summary>
/// <param name="Kind">What sort of change it is.</param>
/// <param name="Subject">What changed — a designator, a net, "the circuit".</param>
/// <param name="Detail">The change in words.</param>
public sealed record CircuitChange(ChangeKind Kind, string Subject, string Detail)
{
    public override string ToString() => $"{Subject}: {Detail}";
}

/// <summary>
/// What changed between two versions of a circuit.
/// <para>
/// The file is JSON and diffs as text, which is worth having and is not what anybody wants to
/// read. "What did I change since I saved" is answered by <c>R2 1k → 2k2</c>, not by nine lines
/// of moved braces — and after an hour of editing, or when an autosave turns up and you have no
/// idea whether it is ahead of the file or behind it, that is the only question being asked.
/// </para>
/// <para>
/// Parts are matched by <b>identity</b> rather than by position or designator, because both of
/// those are things a person changes on purpose. A part that moved and was renamed is still the
/// same part and is reported as two changes to it, not as one part removed and another added.
/// </para>
/// </summary>
public static class CircuitDiff
{
    /// <summary>Movement smaller than this is not worth a line of a report.</summary>
    private const double Negligible = 0.5;

    /// <summary>Everything that differs, in a stable order.</summary>
    public static IReadOnlyList<CircuitChange> Compare(Circuit before, Circuit after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        List<CircuitChange> changes = [];

        var was = before.Components.ToDictionary(c => c.Id);
        var now = after.Components.ToDictionary(c => c.Id);

        foreach (var (id, part) in now.OrderBy(p => p.Value.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!was.TryGetValue(id, out var old))
            {
                changes.Add(new CircuitChange(
                    ChangeKind.Added, part.Name, $"added — {part.ComponentType} {part.ValueLabel}"));
                continue;
            }

            Compare(old, part, changes);
        }

        foreach (var (id, part) in was.OrderBy(p => p.Value.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (now.ContainsKey(id)) continue;

            changes.Add(new CircuitChange(
                ChangeKind.Removed, part.Name, $"removed — was {part.ComponentType} {part.ValueLabel}"));
        }

        CompareWires(before, after, changes);
        CompareProbes(before, after, changes);
        CompareSettings(before, after, changes);

        return changes;
    }

    /// <summary>The headline: what a person wants before reading any of it.</summary>
    public static string Summarise(IReadOnlyList<CircuitChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        if (changes.Count == 0) return "No differences.";

        var added = changes.Count(c => c.Kind == ChangeKind.Added);
        var removed = changes.Count(c => c.Kind == ChangeKind.Removed);
        var edited = changes.Count(c => c.Kind is ChangeKind.ValueChanged or ChangeKind.Renamed);
        var moved = changes.Count(c => c.Kind == ChangeKind.Moved);
        var rewired = changes.Count(c => c.Kind == ChangeKind.Rewired);

        List<string> parts = [];

        if (added > 0) parts.Add($"{added} added");
        if (removed > 0) parts.Add($"{removed} removed");
        if (edited > 0) parts.Add($"{edited} changed");
        if (rewired > 0) parts.Add($"{rewired} rewired");

        // Moves are counted but not led with. A tidy-up moves thirty parts and changes nothing
        // about what the circuit does, and putting that first buries the one value that did.
        if (parts.Count == 0 && moved > 0) return $"{moved} moved, and nothing else.";
        if (moved > 0) parts.Add($"{moved} moved");

        return string.Join(", ", parts) + ".";
    }

    private static void Compare(CircuitComponent old, CircuitComponent now, List<CircuitChange> changes)
    {
        if (!string.Equals(old.Name, now.Name, StringComparison.Ordinal))
        {
            changes.Add(new CircuitChange(
                ChangeKind.Renamed, now.Name, $"renamed from {old.Name}"));
        }

        foreach (var property in ComponentReflection.EditableProperties(now.GetType()))
        {
            var oldValue = Read(property, old);
            var newValue = Read(property, now);

            if (string.Equals(oldValue, newValue, StringComparison.Ordinal)) continue;

            changes.Add(new CircuitChange(
                ChangeKind.ValueChanged,
                now.Name,
                $"{Spaced(property.Name)} {oldValue} → {newValue}"));
        }

        if (Math.Abs(old.RotationDegrees - now.RotationDegrees) > 0.5)
        {
            changes.Add(new CircuitChange(
                ChangeKind.Moved, now.Name,
                $"rotated {old.RotationDegrees:0}° → {now.RotationDegrees:0}°"));
        }

        var shifted = Math.Abs(old.X - now.X) > Negligible || Math.Abs(old.Y - now.Y) > Negligible;

        if (shifted)
        {
            changes.Add(new CircuitChange(
                ChangeKind.Moved, now.Name,
                $"moved ({old.X:0}, {old.Y:0}) → ({now.X:0}, {now.Y:0})"));
        }
    }

    /// <summary>
    /// Wires by the pair of pins they join, because that is what a wire <i>is</i>. Comparing them
    /// by identity would report every wire as replaced whenever one was redrawn through a
    /// different corner, which is a change to the picture and not to the circuit.
    /// </summary>
    private static void CompareWires(Circuit before, Circuit after, List<CircuitChange> changes)
    {
        var was = before.Wires.Select(Describe).Where(w => w is not null).ToHashSet()!;
        var now = after.Wires.Select(Describe).Where(w => w is not null).ToHashSet()!;

        foreach (var wire in now.Except(was).Order())
            changes.Add(new CircuitChange(ChangeKind.Rewired, "Wiring", $"joined {wire}"));

        foreach (var wire in was.Except(now).Order())
            changes.Add(new CircuitChange(ChangeKind.Rewired, "Wiring", $"separated {wire}"));
    }

    private static string? Describe(WireSegment wire)
    {
        if (wire.SourceTerminal?.Owner is not { } from || wire.TargetTerminal?.Owner is not { } to)
            return null;

        var one = $"{from.Name}.{wire.SourceTerminal.Name}";
        var two = $"{to.Name}.{wire.TargetTerminal.Name}";

        // Ordered, so a wire drawn the other way round is the same wire.
        return string.CompareOrdinal(one, two) <= 0 ? $"{one} — {two}" : $"{two} — {one}";
    }

    private static void CompareProbes(Circuit before, Circuit after, List<CircuitChange> changes)
    {
        var was = before.Probes.Select(p => p.Label).ToHashSet(StringComparer.Ordinal);
        var now = after.Probes.Select(p => p.Label).ToHashSet(StringComparer.Ordinal);

        foreach (var label in now.Except(was).Order())
            changes.Add(new CircuitChange(ChangeKind.ProbeChanged, "Probes", $"added {label}"));

        foreach (var label in was.Except(now).Order())
            changes.Add(new CircuitChange(ChangeKind.ProbeChanged, "Probes", $"removed {label}"));
    }

    private static void CompareSettings(Circuit before, Circuit after, List<CircuitChange> changes)
    {
        if (!string.Equals(before.Title, after.Title, StringComparison.Ordinal))
        {
            changes.Add(new CircuitChange(
                ChangeKind.SettingChanged, "Circuit",
                $"title \"{before.Title}\" → \"{after.Title}\""));
        }

        if (Math.Abs(before.AmbientTemperatureCelsius - after.AmbientTemperatureCelsius) > 1e-9)
        {
            changes.Add(new CircuitChange(
                ChangeKind.SettingChanged, "Circuit",
                $"temperature {before.AmbientTemperatureCelsius:0.##} °C → " +
                $"{after.AmbientTemperatureCelsius:0.##} °C"));
        }

        var wasSpecs = before.Specs.Select(s => s.Describe()).ToHashSet(StringComparer.Ordinal);
        var nowSpecs = after.Specs.Select(s => s.Describe()).ToHashSet(StringComparer.Ordinal);

        foreach (var spec in nowSpecs.Except(wasSpecs).Order())
            changes.Add(new CircuitChange(ChangeKind.SettingChanged, "Requirements", $"added {spec}"));

        foreach (var spec in wasSpecs.Except(nowSpecs).Order())
            changes.Add(new CircuitChange(ChangeKind.SettingChanged, "Requirements", $"removed {spec}"));
    }

    /// <summary>
    /// A parameter as text. Doubles go through the invariant culture and a fixed number of
    /// figures, so a value that only differs in the seventeenth digit is not reported as a change
    /// — rounding in the file is not an edit.
    /// </summary>
    private static string Read(System.Reflection.PropertyInfo property, CircuitComponent part)
    {
        object? value;

        try
        {
            value = property.GetValue(part);
        }
        catch
        {
            return "—";
        }

        return value switch
        {
            null => "—",
            double d => d.ToString("G12", CultureInfo.InvariantCulture),
            float f => f.ToString("G6", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "—",
        };
    }

    /// <summary>"SeriesResistance" as "series resistance".</summary>
    private static string Spaced(string name) =>
        string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? $" {char.ToLowerInvariant(c)}" : $"{char.ToLowerInvariant(c)}"));
}
