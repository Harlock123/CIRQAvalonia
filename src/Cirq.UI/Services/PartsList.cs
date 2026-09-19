using System.Globalization;
using System.Text;
using Cirq.Components.Sources;
using Cirq.Core.Topology;

namespace Cirq.UI.Services;

/// <summary>One line of a parts list: every part of a kind, and where they are on the drawing.</summary>
/// <param name="Quantity">How many of them there are.</param>
/// <param name="Designators">Their reference designators, in order, e.g. "R1, R2, R7".</param>
/// <param name="Part">What the part is, e.g. "Resistor" or "NE555".</param>
/// <param name="Value">Its value as the schematic shows it, e.g. "10k". Empty for parts without one.</param>
public sealed record PartsListRow(int Quantity, string Designators, string Part, string Value);

/// <summary>
/// Turns a circuit into the list of things you would have to buy to build it.
/// <para>
/// Grouped the way a bill of materials is grouped — by what the part is <i>and</i> what it is set
/// to, because three 10k resistors are one line and a 4k7 among them is another. A schematic with
/// twenty parts is a table of eight lines, which is the point.
/// </para>
/// </summary>
public static class PartsList
{
    /// <summary>The rows for a circuit, ordered by designator.</summary>
    public static IReadOnlyList<PartsListRow> For(Circuit circuit)
    {
        ArgumentNullException.ThrowIfNull(circuit);

        var groups = circuit.Components
            .Where(IsPart)
            .GroupBy(c => (c.ComponentType, c.ValueLabel))
            .Select(group =>
            {
                var ordered = group.OrderBy(c => c.DesignatorPrefix, StringComparer.Ordinal)
                                   .ThenBy(NumberIn)
                                   .ThenBy(c => c.Name, StringComparer.Ordinal)
                                   .ToList();

                return new
                {
                    Row = new PartsListRow(
                        ordered.Count,
                        string.Join(", ", ordered.Select(Designator)),
                        group.Key.ComponentType,
                        group.Key.ValueLabel),
                    First = ordered[0],
                };
            })
            .OrderBy(g => g.First.DesignatorPrefix, StringComparer.Ordinal)
            .ThenBy(g => NumberIn(g.First))
            .Select(g => g.Row);

        return [.. groups];
    }

    /// <summary>
    /// A ground symbol is a net label rather than something you can buy, so it is left out. Every
    /// other component earns a line, instruments included: a function generator standing in for a
    /// mains secondary is part of what the drawing says, and silently dropping it would make the
    /// list disagree with the schematic beside it.
    /// </summary>
    private static bool IsPart(CircuitComponent component) => component is not Ground;

    private static string Designator(CircuitComponent component) =>
        string.IsNullOrWhiteSpace(component.Name) ? component.DesignatorPrefix + "?" : component.Name;

    /// <summary>
    /// The number in a designator, so R10 sorts after R2 rather than between R1 and R3. Anything
    /// without one sorts first, which is where an unnamed part belongs.
    /// </summary>
    private static int NumberIn(CircuitComponent component)
    {
        var name = component.Name;
        if (string.IsNullOrEmpty(name)) return -1;

        var digits = new StringBuilder();
        foreach (var c in name)
            if (char.IsDigit(c))
                digits.Append(c);

        return digits.Length > 0 && int.TryParse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : -1;
    }
}
