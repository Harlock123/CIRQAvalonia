using System.Globalization;
using System.Text.RegularExpressions;

namespace Cirq.Components.Spice;

/// <summary>One element line inside a subcircuit: what it is, what it joins, and its value.</summary>
/// <param name="Designator">The name as written, e.g. <c>R1</c>.</param>
/// <param name="Letter">The first letter, which is what decides the kind of part.</param>
/// <param name="Nodes">The nets it connects, in the order SPICE writes them.</param>
/// <param name="Value">Its numeric value, or null when it names a model instead.</param>
/// <param name="Model">The model or subcircuit it names, or null when it carries a value.</param>
public sealed record SpiceElement(
    string Designator, char Letter, IReadOnlyList<string> Nodes, double? Value, string? Model);

/// <summary>
/// A parsed <c>.subckt</c>: its name, its pins in order, and what is inside it.
/// </summary>
/// <param name="Name">What the subcircuit is called.</param>
/// <param name="Pins">The external nets, in the order the header lists them. Order is the interface.</param>
/// <param name="Elements">The parts inside, in the order they were written.</param>
/// <param name="Models">Any <c>.model</c> cards defined inside it, which belong to it.</param>
public sealed record SpiceSubcircuit(
    string Name,
    IReadOnlyList<string> Pins,
    IReadOnlyList<SpiceElement> Elements,
    IReadOnlyList<SpiceModelCard> Models);

/// <summary>
/// Reads SPICE <c>.subckt</c> definitions.
/// <para>
/// A <c>.model</c> card describes <i>one device</i>, which is why the existing importer can only
/// bring in diodes, bipolars and MOSFETs. Everything more interesting than a transistor — an
/// op-amp, a regulator, a comparator, a reference — is published as a <b>subcircuit</b>: a pin
/// list and a little netlist of primitives. Being able to read one is the difference between the
/// parts this library chose to model and the parts a manufacturer actually sells.
/// </para>
/// <para>
/// What comes in is a <b>block</b>, which is the same thing grouping a selection produces. That is
/// not a coincidence so much as the reason it works: a block already flattens before the solve,
/// already carries its contents inside a saved file, and already has a library to live in. A
/// subcircuit is a block somebody else drew.
/// </para>
/// </summary>
public static partial class SpiceSubcircuitReader
{
    /// <summary>What came back from a parse.</summary>
    /// <param name="Subcircuits">The definitions understood.</param>
    /// <param name="Problems">Lines that were part of one and could not be used.</param>
    public sealed record Result(
        IReadOnlyList<SpiceSubcircuit> Subcircuits, IReadOnlyList<string> Problems);

    /// <summary>
    /// Element letters this library has a part for. Everything else is reported by name rather
    /// than skipped quietly — a subcircuit with a piece missing is not the part it claims to be,
    /// and importing it silently would be worse than refusing.
    /// </summary>
    private static readonly Dictionary<char, int> NodeCounts = new()
    {
        ['R'] = 2, ['C'] = 2, ['L'] = 2,
        ['D'] = 2, ['V'] = 2, ['I'] = 2,
        ['Q'] = 3,
        ['M'] = 4,
        ['E'] = 4, ['G'] = 4,
    };

    /// <summary>Reads every subcircuit in some text.</summary>
    public static Result Parse(string? text)
    {
        List<SpiceSubcircuit> found = [];
        List<string> problems = [];

        SpiceSubcircuit? current = null;
        List<SpiceElement> elements = [];
        List<SpiceModelCard> models = [];

        foreach (var statement in SpiceModelReader.Statements(text ?? string.Empty))
        {
            if (statement.StartsWith(".subckt", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null)
                    problems.Add($"'{current.Name}' was never closed with .ends.");

                var header = statement.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

                if (header.Length < 3)
                {
                    problems.Add($"A .subckt line needs a name and at least one pin: {Shorten(statement)}");
                    current = null;
                    continue;
                }

                // PARAMS: and anything after it is a parameterisation this library has no way to
                // honour, so the pins stop there rather than swallowing the parameters as nets.
                List<string> pins = [];

                foreach (var word in header.Skip(2))
                {
                    if (word.StartsWith("params", StringComparison.OrdinalIgnoreCase)
                        || word.Contains('=', StringComparison.Ordinal))
                    {
                        problems.Add(
                            $"'{header[1]}' takes parameters, which this library cannot supply — " +
                            "it is imported with whatever values are written into its body.");
                        break;
                    }

                    pins.Add(word);
                }

                elements = [];
                models = [];
                current = new SpiceSubcircuit(header[1], pins, elements, models);

                continue;
            }

            if (statement.StartsWith(".ends", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null) found.Add(current);

                current = null;
                continue;
            }

            if (current is null) continue;

            // A .model inside a subcircuit belongs to it, which is how a vendor ships a
            // transistor's parameters alongside the circuit that uses it.
            if (statement.StartsWith(".model", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = SpiceModelReader.Parse(statement);

                models.AddRange(parsed.Cards);
                problems.AddRange(parsed.Problems.Where(p => !p.StartsWith("No .model", StringComparison.Ordinal)));

                continue;
            }

            if (statement.StartsWith('.'))
            {
                problems.Add($"'{statement.Split(' ')[0]}' inside '{current.Name}' is not something " +
                             "this library understands, and was left out.");
                continue;
            }

            if (Element(statement, current.Name) is { } element) elements.Add(element);
            else problems.Add(Why(statement, current.Name));
        }

        if (current is not null) problems.Add($"'{current.Name}' was never closed with .ends.");

        if (found.Count == 0 && problems.Count == 0)
            problems.Add("No .subckt definitions found. One looks like: .subckt DIVIDER in out gnd");

        return new Result(found, problems);
    }

    /// <summary>One element line, or null when it is not one this library can build.</summary>
    private static SpiceElement? Element(string statement, string owner)
    {
        _ = owner;

        var words = statement.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

        if (words.Length < 3) return null;

        var letter = char.ToUpperInvariant(words[0][0]);

        if (!NodeCounts.TryGetValue(letter, out var count)) return null;
        if (words.Length < count + 2) return null;

        var nodes = words.Skip(1).Take(count).ToList();
        var rest = words[(count + 1)..];

        if (rest.Length == 0) return null;

        // A value or a model name. R, C, L, V, I, E and G carry a number; D, Q and M name a model.
        var value = SpiceValue.Parse(rest[0]);

        return letter is 'D' or 'Q' or 'M'
            ? new SpiceElement(words[0], letter, nodes, null, rest[0])
            : value is null
                ? null
                : new SpiceElement(words[0], letter, nodes, value.Value, null);
    }

    private static string Why(string statement, string owner)
    {
        var words = statement.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var letter = words.Length > 0 ? char.ToUpperInvariant(words[0][0]) : '?';

        return NodeCounts.ContainsKey(letter)
            ? $"'{words[0]}' in '{owner}' could not be read: {Shorten(statement)}"
            : $"'{words[0]}' in '{owner}' is a {Describe(letter)}, which this library has no part " +
              "for — the subcircuit would not be the part it claims to be, so it was not imported.";
    }

    /// <summary>
    /// What a SPICE element letter means, so a refusal names the thing rather than the letter.
    /// </summary>
    private static string Describe(char letter) => letter switch
    {
        'X' => "nested subcircuit",
        'F' or 'H' => "current-controlled source",
        'B' => "behavioural source",
        'S' or 'W' => "switch",
        'T' or 'O' => "transmission line",
        'J' => "JFET",
        'K' => "coupling",
        _ => $"'{letter}' element",
    };

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..57] + "...";
}
