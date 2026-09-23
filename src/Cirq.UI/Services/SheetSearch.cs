using Cirq.Components.Hierarchy;
using Cirq.Core.Topology;

namespace Cirq.UI.Services;

/// <summary>Why something matched, which decides where it comes in the list.</summary>
public enum MatchKind
{
    /// <summary>The designator — R17, U3, C12. What somebody is nearly always looking for.</summary>
    Designator,

    /// <summary>The value printed under it — 4k7, 100nF, 1N4148.</summary>
    Value,

    /// <summary>What kind of part it is — Resistor, Op-Amp.</summary>
    Type,

    /// <summary>A net's name, from a label on the drawing.</summary>
    Net,
}

/// <summary>One thing the search found.</summary>
/// <param name="Kind">Why it matched.</param>
/// <param name="Part">The part to go to.</param>
/// <param name="Title">What to show as the heading of the row.</param>
/// <param name="Detail">The rest of the row.</param>
public sealed record SheetMatch(MatchKind Kind, CircuitComponent Part, string Title, string Detail);

/// <summary>
/// Finding something on the sheet you already have.
/// <para>
/// The palette's search finds a part to <b>place</b>. Nothing until now searched the drawing, and
/// past about twenty parts "where is R17", "which one is the 4k7" and "what is on the 5 V rail"
/// are constant questions with no answer but reading the whole sheet.
/// </para>
/// <para>
/// Ranked by what people mean. A designator is nearly always what is being looked for — it is the
/// thing printed in an error message, quoted in a parts list, and written on a note — so an exact
/// designator comes first, then a designator that starts with the term, then values, types and
/// nets. Matching everything and ordering it badly is the same as not matching at all.
/// </para>
/// </summary>
public static class SheetSearch
{
    /// <summary>Everything on the sheet matching a term, best first.</summary>
    public static IReadOnlyList<SheetMatch> Find(Circuit circuit, string term)
    {
        ArgumentNullException.ThrowIfNull(circuit);

        var wanted = (term ?? string.Empty).Trim();
        if (wanted.Length == 0) return [];

        // Inside blocks too: a part does not stop existing because it was grouped, and "where is
        // R17" has the same answer whether or not somebody tidied it away.
        var parts = Flattening.Flatten(circuit.Components).ToList();

        List<(int Rank, SheetMatch Match)> found = [];

        foreach (var part in parts)
        {
            var designator = part.Name ?? string.Empty;
            var value = part.ValueLabel ?? string.Empty;
            var type = part.ComponentType ?? string.Empty;

            if (Is(designator, wanted))
            {
                Add(0, MatchKind.Designator, part, designator, $"{type}  ·  {value}");
                continue;
            }

            if (Starts(designator, wanted))
            {
                Add(1, MatchKind.Designator, part, designator, $"{type}  ·  {value}");
                continue;
            }

            if (Has(value, wanted))
            {
                Add(2, MatchKind.Value, part, designator, $"{type}  ·  {value}");
                continue;
            }

            if (Has(type, wanted)) Add(3, MatchKind.Type, part, designator, $"{type}  ·  {value}");
        }

        // Nets last, and one row per net rather than one per pin on it — a rail with twenty
        // things on it should be one answer, not twenty.
        foreach (var net in circuit.BuildNetlist().Nets)
        {
            if (net.Label is not { } label || !Has(label, wanted)) continue;

            var owner = net.Terminals.Select(t => t.Owner).FirstOrDefault(o => o is not null);
            if (owner is null) continue;

            var others = net.Terminals
                .Select(t => t.Owner?.Name ?? string.Empty)
                .Where(n => n.Length > 0)
                .Distinct()
                .Order()
                .ToList();

            Add(4, MatchKind.Net, owner, label,
                others.Count == 1 ? $"net · {others[0]}" : $"net · {others.Count} parts on it");
        }

        return [.. found.OrderBy(f => f.Rank).ThenBy(f => f.Match.Title, StringComparer.OrdinalIgnoreCase)
            .Select(f => f.Match)];

        void Add(int rank, MatchKind kind, CircuitComponent part, string title, string detail) =>
            found.Add((rank, new SheetMatch(kind, part, title, detail)));
    }

    private static bool Is(string value, string term) =>
        value.Equals(term, StringComparison.OrdinalIgnoreCase);

    private static bool Starts(string value, string term) =>
        value.StartsWith(term, StringComparison.OrdinalIgnoreCase);

    private static bool Has(string value, string term) =>
        value.Contains(term, StringComparison.OrdinalIgnoreCase);
}
