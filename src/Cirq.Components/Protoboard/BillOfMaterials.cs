using System.Globalization;
using System.Text;
using Cirq.Components.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Protoboard;

/// <summary>One line of a bill of materials: the same part, value and package, with every designator that uses it.</summary>
public sealed record BomLine(IReadOnlyList<string> Designators, string Part, string Value, string Package)
{
    public int Quantity => Designators.Count;
}

/// <summary>
/// A bill of materials: what to buy to build the circuit. Parts that go on the board, grouped by what they are, their
/// value and their package, designators in order (R1, R2, R10); then the bench equipment, listed apart, since you don't
/// buy a power supply per board. Symbols that are no part (grounds, net labels, probes) are left out.
/// </summary>
public static class BillOfMaterials
{
    public static IReadOnlyList<BomLine> Lines(Circuit circuit, bool bench = false)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        var parts = Flattening.Flatten(circuit.Components)
            .Select(c => (Part: c, Package: BoardPackages.For(c)))
            .Where(p => p.Package.Form != BoardForm.None && (p.Package.Form == BoardForm.Bench) == bench);
        return [.. parts
            .GroupBy(p => (What(p.Part), p.Part.ValueLabel, Label: PackageName(p.Package)))
            .Select(g => new BomLine([.. g.Select(p => p.Part.Name).Order(Designator.Comparer)], g.Key.Item1, g.Key.ValueLabel, g.Key.Label))
            .OrderBy(l => l.Designators[0], Designator.Comparer)];
    }

    /// <summary>The package a part comes in, as a catalogue names it.</summary>
    public static string PackageName(BoardPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var pins = package.Pins.Count;
        return package.Form switch
        {
            BoardForm.Dip => package.Span >= 6 ? $"DIP-{pins} (0.{package.Span} in wide)" : $"DIP-{pins}",
            BoardForm.Bench => "bench equipment",
            _ => package.Look switch
            {
                BoardLook.Resistor => "axial resistor",
                BoardLook.Ceramic or BoardLook.Disc => "ceramic capacitor, radial",
                BoardLook.Electrolytic => "electrolytic capacitor, radial",
                BoardLook.Inductor or BoardLook.Ferrite => "axial inductor",
                BoardLook.Diode => "axial diode (DO-41 or DO-35)",
                BoardLook.Led => "5 mm LED",
                BoardLook.Crystal => "HC-49 crystal",
                BoardLook.To92 => "TO-92",
                BoardLook.To220 => "TO-220",
                BoardLook.Trimmer => "trimmer potentiometer",
                BoardLook.Button => "6 mm tactile switch",
                BoardLook.Header => $"{pins}-pin header module",
                BoardLook.SevenSegment => "seven-segment display",
                BoardLook.DipSwitch => $"DIP switch, {pins / 2} way",
                _ => package.Form == BoardForm.TwoLead ? "two-lead, through hole" : $"{pins}-pin, through hole",
            },
        };
    }

    /// <summary>What a part is, as a buyer would look it up: a chip by its part number, anything else by its kind.</summary>
    private static string What(CircuitComponent part) => part is DigitalIc ic ? ic.PartNumber : Spaced(part.ComponentType);

    private static string Spaced(string type)
    {
        var text = new StringBuilder();
        for (var i = 0; i < type.Length; i++)
        {
            if (i > 0 && char.IsUpper(type[i]) && char.IsLower(type[i - 1])) text.Append(' ');
            text.Append(type[i]);
        }

        return text.ToString();
    }

    /// <summary>The bill as CSV: the parts, then the bench equipment under a heading of its own.</summary>
    public static string Csv(Circuit circuit)
    {
        var csv = new StringBuilder();
        csv.Append("Designators,Quantity,Part,Value,Package\r\n");
        foreach (var line in Lines(circuit)) Row(csv, line);
        var bench = Lines(circuit, bench: true);
        if (bench.Count > 0)
        {
            csv.Append("\r\nOn the bench,,,,\r\n");
            foreach (var line in bench) Row(csv, line);
        }

        return csv.ToString();
    }

    private static void Row(StringBuilder csv, BomLine line) =>
        csv.Append(string.Join(",", new[] { string.Join(" ", line.Designators), line.Quantity.ToString(CultureInfo.InvariantCulture), line.Part, line.Value, line.Package }.Select(Quote))).Append("\r\n");

    private static string Quote(string field) =>
        field.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : field;

    /// <summary>Designators in the order people read them: by prefix, then by number, so R2 comes before R10.</summary>
    private sealed class Designator : IComparer<string>
    {
        public static Designator Comparer { get; } = new();

        public int Compare(string? x, string? y)
        {
            var (px, nx) = Split(x ?? "");
            var (py, ny) = Split(y ?? "");
            var c = string.CompareOrdinal(px, py);
            return c != 0 ? c : nx != ny ? nx.CompareTo(ny) : string.CompareOrdinal(x, y);
        }

        private static (string Prefix, long Number) Split(string name)
        {
            var i = name.Length;
            while (i > 0 && char.IsAsciiDigit(name[i - 1])) i--;
            return (name[..i], i < name.Length && long.TryParse(name[i..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1);
        }
    }
}
