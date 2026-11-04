using System.Globalization;
using Cirq.Components.Processors;

namespace Cirq.Components.Tests;

/// <summary>
/// A small two-pass 6502 assembler, for the tests only: it turns the programs the tests and the
/// guide show into bytes, so a test can check the bytes in an example are the source it claims.
/// <para>
/// Enough for those programs and no more: one instruction or directive a line, <c>;</c> comments,
/// <c>label:</c> or <c>NAME = value</c>, <c>.org</c>, <c>.byte</c> (numbers and "strings"),
/// <c>.word</c>, <c>.asciiz</c>; numbers as <c>$FF</c>, <c>%1010</c> or decimal, <c>'c'</c>
/// characters, labels, <c>&lt;</c> and <c>&gt;</c> for the low and high byte, and <c>+</c> and
/// <c>-</c>. A value known on the first pass and under $100 picks zero-page addressing where the
/// instruction has it; a label defined later is taken as absolute.
/// </para>
/// </summary>
internal static class Assembler6502
{
    /// <summary>The assembled bytes, and where the first of them goes.</summary>
    public sealed record Output(int Origin, byte[] Bytes, IReadOnlyDictionary<string, int> Labels)
    {
        /// <summary>The bytes as a 32K ROM image for $8000-$FFFF, unused bytes zero.</summary>
        public byte[] Rom32K()
        {
            var rom = new byte[0x8000];
            Array.Copy(Bytes, 0, rom, Origin - 0x8000, Bytes.Length);
            return rom;
        }
    }

    public static Output Assemble(string source, Cpu6502Variant variant = Cpu6502Variant.Wdc65C02)
    {
        var table = Opcodes6502.For(variant);
        Dictionary<string, int> labels = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<int, int> sizes = [];
        Output? result = null;

        for (var pass = 0; pass < 2; pass++)
        {
            var image = new SortedDictionary<int, byte>();
            var pc = -1;
            var origin = -1;
            var lineNumber = 0;

            foreach (var raw in source.Split('\n'))
            {
                lineNumber++;
                var line = StripComment(raw).Trim();
                if (line.Length == 0) continue;

                // NAME = value
                var equals = line.IndexOf('=');
                if (equals > 0 && !line.StartsWith('.') && IsName(line[..equals].Trim()))
                {
                    labels[line[..equals].Trim()] = Evaluate(line[(equals + 1)..], labels, pass, lineNumber) ?? 0;
                    continue;
                }

                var colon = line.IndexOf(':');
                if (colon > 0 && IsName(line[..colon].Trim()))
                {
                    labels[line[..colon].Trim()] = pc;
                    line = line[(colon + 1)..].Trim();
                    if (line.Length == 0) continue;
                }

                var space = line.IndexOfAny([' ', '\t']);
                var word = (space < 0 ? line : line[..space]).ToUpperInvariant();
                var operand = space < 0 ? string.Empty : line[(space + 1)..].Trim();

                void Emit(int value)
                {
                    if (pc < 0) throw new FormatException($"Line {lineNumber}: code before any .org.");
                    image[pc++] = (byte)value;
                }

                switch (word)
                {
                    case ".ORG":
                        pc = Evaluate(operand, labels, pass, lineNumber) ?? throw new FormatException($"Line {lineNumber}: .org needs a value.");
                        if (origin < 0) origin = pc;
                        continue;

                    case ".BYTE" or ".ASCIIZ":
                        foreach (var item in SplitList(operand))
                        {
                            if (item.StartsWith('"'))
                                foreach (var c in item.Trim('"')) Emit(c);
                            else
                                Emit(Evaluate(item, labels, pass, lineNumber) ?? 0);
                        }

                        if (word == ".ASCIIZ") Emit(0);
                        continue;

                    case ".WORD":
                        foreach (var item in SplitList(operand))
                        {
                            var v = Evaluate(item, labels, pass, lineNumber) ?? 0;
                            Emit(v);
                            Emit(v >> 8);
                        }

                        continue;
                }

                var (mode, expression) = Mode(operand);
                var at = pc;
                var value = expression is null ? 0 : Evaluate(expression, labels, pass, lineNumber);

                int? Find(AddressMode m)
                {
                    for (var i = 0; i < 256; i++)
                        if (table[i].Mnemonic == word && table[i].Mode == m && !Opcodes6502.IsUndocumented(variant, (byte)i)) return i;
                    for (var i = 0; i < 256; i++)
                        if (table[i].Mnemonic == word && table[i].Mode == m) return i;
                    return null;
                }

                // Branches are relative whatever they were written as; the bit branches take zp,label.
                if (Find(AddressMode.Relative) is { } branch)
                {
                    Emit(branch);
                    var target = value ?? at + 2;
                    var offset = target - (at + 2);
                    if (pass == 1 && offset is < -128 or > 127) throw new FormatException($"Line {lineNumber}: branch out of range.");
                    Emit(offset);
                    continue;
                }

                if (Find(AddressMode.ZeroPageRelative) is { } bitBranch)
                {
                    var parts = SplitList(operand);
                    Emit(bitBranch);
                    Emit(Evaluate(parts[0], labels, pass, lineNumber) ?? 0);
                    var target = Evaluate(parts[1], labels, pass, lineNumber) ?? at + 3;
                    Emit(target - (at + 3));
                    continue;
                }

                // A short form where the operand is known and small; on the second pass, whatever
                // size the first pass chose, so the labels do not move.
                var small = pass == 0 ? value is >= 0 and < 0x100 : sizes.GetValueOrDefault(at) == 2;
                var shortened = mode switch
                {
                    AddressMode.Absolute when small && Find(AddressMode.ZeroPage) is not null => AddressMode.ZeroPage,
                    AddressMode.AbsoluteX when small && Find(AddressMode.ZeroPageX) is not null => AddressMode.ZeroPageX,
                    AddressMode.AbsoluteY when small && Find(AddressMode.ZeroPageY) is not null => AddressMode.ZeroPageY,
                    AddressMode.Indirect when small && Find(AddressMode.ZeroPageIndirect) is not null => AddressMode.ZeroPageIndirect,
                    AddressMode.AbsoluteIndexedIndirect when Find(AddressMode.AbsoluteIndexedIndirect) is null => AddressMode.IndexedIndirect,
                    _ => mode,
                };

                if (mode == AddressMode.Implied && Find(AddressMode.Implied) is null && Find(AddressMode.Accumulator) is not null)
                    shortened = AddressMode.Accumulator;

                var opcode = Find(shortened) ?? throw new FormatException($"Line {lineNumber}: {word} has no {shortened} form.");
                var entry = table[opcode];
                Emit(opcode);
                if (entry.Length >= 2) Emit(value ?? 0);
                if (entry.Length == 3) Emit((value ?? 0) >> 8);
                if (pass == 0) sizes[at] = entry.Length;
            }

            if (pass == 1)
            {
                if (image.Count == 0) throw new FormatException("Nothing was assembled.");
                var first = image.Keys.First();
                var last = image.Keys.Last();
                var bytes = new byte[last - first + 1];
                foreach (var (address, b) in image) bytes[address - first] = b;
                result = new Output(first, bytes, labels);
            }
        }

        return result!;
    }

    private static string StripComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inString = !inString;
            if (line[i] == ';' && !inString) return line[..i];
        }

        return line;
    }

    private static bool IsName(string text) =>
        text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_') && text.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static List<string> SplitList(string text)
    {
        List<string> items = [];
        var start = 0;
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"') inString = !inString;
            if (text[i] == ',' && !inString)
            {
                items.Add(text[start..i].Trim());
                start = i + 1;
            }
        }

        items.Add(text[start..].Trim());
        return items.Where(i => i.Length > 0).ToList();
    }

    private static (AddressMode Mode, string? Expression) Mode(string operand)
    {
        var o = operand.Replace(" ", string.Empty);
        if (o.Length == 0) return (AddressMode.Implied, null);
        if (o.Equals("A", StringComparison.OrdinalIgnoreCase)) return (AddressMode.Accumulator, null);
        if (o.StartsWith('#')) return (AddressMode.Immediate, o[1..]);

        if (o.StartsWith('('))
        {
            if (o.EndsWith(",X)", StringComparison.OrdinalIgnoreCase))
                return o.Length > 0 && IsSixteenBitIndirect(o) ? (AddressMode.AbsoluteIndexedIndirect, o[1..^3]) : (AddressMode.IndexedIndirect, o[1..^3]);
            if (o.EndsWith("),Y", StringComparison.OrdinalIgnoreCase)) return (AddressMode.IndirectIndexed, o[1..^3]);
            if (o.EndsWith(')')) return (AddressMode.Indirect, o[1..^1]);
        }

        if (o.EndsWith(",X", StringComparison.OrdinalIgnoreCase)) return (AddressMode.AbsoluteX, o[..^2]);
        if (o.EndsWith(",Y", StringComparison.OrdinalIgnoreCase)) return (AddressMode.AbsoluteY, o[..^2]);
        return (AddressMode.Absolute, o);
    }

    /// <summary>(abs,X) is only JMP's; it is written with a four-digit address or a label.</summary>
    private static bool IsSixteenBitIndirect(string o)
    {
        var inner = o[1..^3];
        return !(inner.StartsWith('$') && inner.Length <= 3);
    }

    /// <summary>A value, or null when it names a label not yet defined (first pass only).</summary>
    private static int? Evaluate(string text, IReadOnlyDictionary<string, int> labels, int pass, int line)
    {
        var t = text.Trim();
        if (t.StartsWith('<')) return Evaluate(t[1..], labels, pass, line) is { } v ? v & 0xFF : null;
        if (t.StartsWith('>')) return Evaluate(t[1..], labels, pass, line) is { } v ? (v >> 8) & 0xFF : null;

        var total = 0;
        var sign = 1;
        var any = false;
        var start = 0;

        for (var i = 0; i <= t.Length; i++)
        {
            if (i < t.Length && (t[i] != '+' && t[i] != '-' || i == start || t[start] == '\'')) continue;

            var term = t[start..i].Trim();
            int? value = term switch
            {
                _ when term.StartsWith('$') => int.Parse(term[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                _ when term.StartsWith('%') => Convert.ToInt32(term[1..], 2),
                _ when term.Length == 3 && term[0] == '\'' => term[1],
                _ when char.IsDigit(term[0]) => int.Parse(term, CultureInfo.InvariantCulture),
                _ when labels.TryGetValue(term, out var l) => l,
                _ when pass == 0 => null,
                _ => throw new FormatException($"Line {line}: no label called {term}."),
            };

            if (value is null) return null;
            total += sign * value.Value;
            any = true;

            if (i < t.Length) sign = t[i] == '-' ? -1 : 1;
            start = i + 1;
        }

        return any ? total : throw new FormatException($"Line {line}: '{text}' is not a value.");
    }
}
