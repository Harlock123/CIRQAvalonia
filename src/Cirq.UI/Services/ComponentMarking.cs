using Cirq.Components.Passive;
using Cirq.Core.Topology;
using Cirq.Core.Units;

namespace Cirq.UI.Services;

/// <summary>How a part's value is written on the part itself.</summary>
public enum MarkingKind
{
    /// <summary>Painted bands, as on a resistor or an inductor.</summary>
    Bands,

    /// <summary>A printed code, as on a ceramic capacitor.</summary>
    Printed,
}

/// <summary>
/// The value of a part as it appears <i>on</i> the part — the bands or the code.
/// </summary>
/// <param name="Kind">Bands or print.</param>
/// <param name="Bands">The bands, when it is banded.</param>
/// <param name="Code">The code, when it is printed.</param>
/// <param name="Caption">What the marking works out to, read back in units.</param>
/// <param name="Note">Why the marking is not exactly the value set, or null when it is.</param>
public sealed record ComponentMarking(
    MarkingKind Kind,
    IReadOnlyList<ColourBand> Bands,
    PrintedCode? Code,
    string Caption,
    string? Note)
{
    /// <summary>
    /// True when the last band is a tolerance rather than part of the value, which decides where
    /// the wider gap goes — and therefore which end a reader starts from.
    /// </summary>
    public bool HasTolerance { get; init; }

    /// <summary>
    /// The marking read out in words — "brown 1 · black 0 · orange × 1,000 · gold ± 5 %".
    /// <para>
    /// The picture alone teaches somebody to recognise the pattern; the words are what let them
    /// learn to <i>read</i> it, and they are the part that works for anybody who cannot tell the
    /// colours apart. It lives on the marking rather than on the card that draws it so that the
    /// guide's illustration and the card cannot end up saying different things.
    /// </para>
    /// </summary>
    public string Readout =>
        Kind == MarkingKind.Bands
            ? string.Join(" · ", Bands.Select(b =>
                $"{b.Colour.ToString().ToLowerInvariant()} {b.Meaning}"))
            : Code?.ToleranceText is { } tolerance
                ? $"{Code.Code} — {tolerance}"
                : Code?.Code ?? string.Empty;
}

/// <summary>
/// Works out what is written on a part, for the hover card to draw beside the numbers.
/// <para>
/// This is the one thing a schematic cannot show you and a drawer of components cannot stop
/// showing you. A resistor on the screen says <c>4k7</c>; the one in your hand says
/// yellow-violet-red, and the gap between those two facts is a thing people carry a card in their
/// wallet for. Putting the bands next to the value is how the two stop being separate: not by
/// teaching a mnemonic, but by having seen them together often enough.
/// </para>
/// <para>
/// It is deliberately only the parts that <i>are</i> marked this way. An electrolytic has its
/// value printed on the side in words and a transistor has a part number, so neither gets a
/// picture — inventing one would teach something untrue.
/// </para>
/// </summary>
public static class ComponentMarkings
{
    /// <summary>What is written on this part, or null when it carries no code at all.</summary>
    public static ComponentMarking? For(CircuitComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);

        return component switch
        {
            // An electrolytic is excluded on purpose, even though it derives from Capacitor: its
            // value is printed on the can in plain microfarads, and showing a ceramic's code for
            // one would be inventing a marking it does not carry.
            ElectrolyticCapacitor => null,

            Resistor r => Banded(r.Resistance, r.Tolerance, "Ω", 1),

            // An inductor's bands are read in microhenries, exactly as a ceramic's digits are
            // read in picofarads — 100 µH is brown-black-brown, not a code the table can reach in
            // henries at all. The unit the code is written in is part of the code.
            Inductor l => Banded(l.Inductance, l.Tolerance, "H", 1e6),
            Capacitor c => Printed(c.Capacitance, c.Tolerance),

            _ => null,
        };
    }

    /// <param name="perUnit">How many of the code's units are in one of <paramref name="unit"/>.</param>
    private static ComponentMarking? Banded(double value, double tolerance, string unit, double perUnit)
    {
        if (ColourCodes.For(value * perUnit, tolerance) is not { } code) return null;

        return new ComponentMarking(
            MarkingKind.Bands,
            code.Bands,
            null,
            SiPrefix.Format(code.Represented / perUnit, unit),
            code.Note)
        {
            HasTolerance = tolerance > 0,
        };
    }

    private static ComponentMarking? Printed(double farads, double tolerance)
    {
        if (PrintedCodes.ForCapacitance(farads, tolerance) is not { } code) return null;

        // The trap, said in the fewest words that carry it: the code is in picofarads however
        // large the part is, which is what makes 104 mean 100 nF rather than 104 of anything.
        var caption = $"{code.Digits} {code.Multiplier}, in pF = {SiPrefix.Format(code.Represented, "F")}";

        return new ComponentMarking(MarkingKind.Printed, [], code, caption, code.Note);
    }
}
