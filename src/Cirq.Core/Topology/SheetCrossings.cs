namespace Cirq.Core.Topology;

/// <summary>
/// Which pages a named net appears on.
/// <para>
/// Sheets are joined by naming a net, and that is the one thing about them that can silently fail.
/// A <c>VCC</c> label on the supply page looks exactly like a <c>VCC</c> label that goes nowhere:
/// the drawing has no way of showing that the net leaves the page, and a name typed <c>VCC1</c> on
/// one page and <c>VCC</c> on another is two nets that look like one.
/// </para>
/// <para>
/// So a label is told what it is: the pages its net is on, worked out from the drawing rather than
/// declared by hand. That is what makes it impossible to get wrong — there is no connector symbol to
/// forget to place, and no second name to keep in step with the first.
/// </para>
/// </summary>
public static class SheetCrossings
{
    /// <summary>
    /// The sheets a net appears on, in document order. Empty for a drawing that has not been split,
    /// and for a name nothing else carries.
    /// </summary>
    public static IReadOnlyList<string> SheetsCarrying(Circuit circuit, string netName)
    {
        ArgumentNullException.ThrowIfNull(circuit);

        if (circuit.Sheets.Count == 0 || string.IsNullOrWhiteSpace(netName)) return [];

        var wanted = netName.Trim();

        List<string> found = [];

        foreach (var sheet in circuit.Sheets)
        {
            var carries = circuit.OnSheet(sheet)
                .OfType<INetNaming>()
                .Any(label => string.Equals(label.NetName.Trim(), wanted, StringComparison.OrdinalIgnoreCase));

            if (carries) found.Add(sheet);
        }

        return found;
    }

    /// <summary>
    /// The other sheets this label's net is on — what an off-page connector would have to be
    /// labelled with. Empty when the net stays on this page, which is the ordinary case.
    /// </summary>
    public static IReadOnlyList<string> Elsewhere(Circuit circuit, CircuitComponent label)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(label);

        if (label is not INetNaming naming) return [];

        var here = circuit.Sheets.FirstOrDefault(s => circuit.Belongs(label, s));

        return [.. SheetsCarrying(circuit, naming.NetName)
            .Where(s => !string.Equals(s, here, StringComparison.Ordinal))];
    }

    /// <summary>
    /// The label on another sheet that continues this net, for going to it. The first one on the
    /// next sheet that carries the net, wrapping round — so repeated jumps walk the pages the net is
    /// on rather than bouncing between two of them.
    /// </summary>
    public static CircuitComponent? Continuation(Circuit circuit, CircuitComponent label)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(label);

        if (label is not INetNaming naming) return null;

        var carrying = SheetsCarrying(circuit, naming.NetName);

        if (carrying.Count < 2) return null;

        var here = circuit.Sheets.FirstOrDefault(s => circuit.Belongs(label, s));
        var at = here is null ? -1 : carrying.ToList().FindIndex(s => string.Equals(s, here, StringComparison.Ordinal));

        var next = carrying[(at + 1) % carrying.Count];

        return circuit.OnSheet(next)
            .FirstOrDefault(c => c is INetNaming other
                                 && !ReferenceEquals(c, label)
                                 && string.Equals(other.NetName.Trim(), naming.NetName.Trim(),
                                     StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// How a label says where else its net goes — "also on Logic, I/O" — or empty when it goes
    /// nowhere else.
    /// </summary>
    public static string Describe(Circuit circuit, CircuitComponent label)
    {
        var elsewhere = Elsewhere(circuit, label);

        return elsewhere.Count == 0 ? string.Empty : $"also on {string.Join(", ", elsewhere)}";
    }
}
