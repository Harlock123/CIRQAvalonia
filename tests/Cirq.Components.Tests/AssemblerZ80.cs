using System.Globalization;
using System.Text;
using Cirq.Components.Processors;

namespace Cirq.Components.Tests;

/// <summary>
/// A Z80 assembler, for the tests only: it turns the programs the tests and the guide show into
/// bytes, so a test can check the bytes in an example are the source it claims, and it reads back
/// what <see cref="OpcodesZ80.Disassemble"/> prints.
/// <para>
/// Zilog syntax, one instruction or directive a line: <c>;</c> comments, <c>label:</c> (or a label
/// alone in the first column), <c>NAME equ value</c> or <c>NAME = value</c>, <c>org</c>, <c>db</c> or
/// <c>defb</c> (numbers and "strings"), <c>dw</c> or <c>defw</c>, <c>defm "text"</c>, and <c>ds</c> or
/// <c>defs</c>. Numbers are <c>$FF</c>, <c>0FFh</c>, <c>%1010</c>, decimal or <c>'c'</c>; expressions
/// have <c>+ - * / &amp; | &lt;&lt; &gt;&gt;</c>, unary minus, parentheses and <c>$</c> for here.
/// Every instruction the disassembler knows, the undocumented ones included: the table is every
/// opcode's pattern from <see cref="OpcodesZ80.Pattern"/> (<c>LD (IX+#),#</c>), the first opcode to
/// give a pattern keeping it, so a duplicate assembles to the documented opcode. A trailing
/// <c>*</c>, the disassembler's mark for an undocumented opcode, is ignored.
/// </para>
/// </summary>
internal static class AssemblerZ80
{
    /// <summary>The assembled bytes by address, and the labels.</summary>
    public sealed record Output(SortedDictionary<int, byte> Bytes, IReadOnlyDictionary<string, long> Labels)
    {
        /// <summary>The bytes from <paramref name="start"/>, <paramref name="length"/> of them, unused bytes zero.</summary>
        public byte[] Image(int start, int length)
        {
            var image = new byte[length];
            foreach (var (address, value) in Bytes)
                if (address >= start && address < start + length) image[address - start] = value;
            return image;
        }

        public long Label(string name) => Labels[name];
    }

    private sealed record Form(byte[] Bytes, IReadOnlyList<(Z80OperandKind Kind, int Offset)> Fields);

    /// <summary>Each pattern's opcode bytes (operands zero) and its fields, in the order they are written.</summary>
    private static readonly Dictionary<string, Form> Table = BuildTable();

    private static readonly HashSet<string> Mnemonics = new(Table.Keys.Select(k => k.Split(' ')[0]), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, Form> BuildTable()
    {
        Dictionary<string, Form> table = new(StringComparer.Ordinal);

        void Add(byte[] bytes)
        {
            var (text, length, fields) = OpcodesZ80.Pattern(0, a => a < bytes.Length ? bytes[a] : (byte)0);
            var key = Normalise(text.EndsWith(" *", StringComparison.Ordinal) ? text[..^2] : text);
            table.TryAdd(key, new Form([.. bytes.Take(length)], fields));
        }

        for (var op = 0; op < 256; op++)
            if (op is not (0xCB or 0xDD or 0xED or 0xFD)) Add([(byte)op, 0, 0, 0]);
        for (var op = 0; op < 256; op++) Add([0xCB, (byte)op, 0, 0]);
        for (var op = 0; op < 256; op++) Add([0xED, (byte)op, 0, 0]);
        foreach (var prefix in new byte[] { 0xDD, 0xFD })
        {
            for (var op = 0; op < 256; op++)
                if (op is not (0xCB or 0xDD or 0xED or 0xFD)) Add([prefix, (byte)op, 0, 0, 0]);
            for (var op = 0; op < 256; op++) Add([prefix, 0xCB, 0, (byte)op]);
        }

        return table;
    }

    /// <summary>A pattern as a key: upper case, single spaces, no space after a comma.</summary>
    private static string Normalise(string text)
    {
        var parts = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var mnemonic = parts[0].ToUpperInvariant();
        if (parts.Length == 1) return mnemonic;
        var operands = string.Join(",", SplitList(parts[1]).Select(o => o.Trim()));
        return mnemonic + " " + operands;
    }

    public static Output Assemble(string source)
    {
        Dictionary<string, long> labels = new(StringComparer.OrdinalIgnoreCase);
        SortedDictionary<int, byte> image = [];

        for (var pass = 0; pass < 12; pass++)
        {
            var before = new Dictionary<string, long>(labels, StringComparer.OrdinalIgnoreCase);
            image = [];
            var pc = 0;
            var lineNumber = 0;
            var final = pass > 0 && pass == 11;

            foreach (var raw in source.Split('\n'))
            {
                lineNumber++;
                var stripped = StripComment(raw).TrimEnd();
                if (stripped.EndsWith(" *", StringComparison.Ordinal)) stripped = stripped[..^2].TrimEnd();
                var line = stripped.Trim();
                if (line.Length == 0) continue;

                try
                {
                    // label: — or a name in the first column that is not an instruction or directive.
                    var colon = line.IndexOf(':');
                    if (colon > 0 && IsName(line[..colon].Trim()))
                    {
                        labels[line[..colon].Trim()] = pc;
                        line = line[(colon + 1)..].Trim();
                        if (line.Length == 0) continue;
                    }
                    else if (!char.IsWhiteSpace(stripped[0]))
                    {
                        var first = SplitWord(line);
                        var second = SplitWord(first.Tail);
                        var isEqu = second.Word.Equals("EQU", StringComparison.OrdinalIgnoreCase) || first.Tail.StartsWith('=');
                        if (IsName(first.Word) && !IsKeyword(first.Word) && !isEqu)
                        {
                            labels[first.Word] = pc;
                            line = first.Tail;
                            if (line.Length == 0) continue;
                        }
                    }

                    var parts = SplitWord(line);
                    var word = parts.Word;
                    var rest = parts.Tail;

                    if (rest.StartsWith('=') && IsName(word))
                    {
                        labels[word] = Evaluate(rest[1..], labels, final, pc) ?? 0;
                        continue;
                    }

                    var next = SplitWord(rest);
                    if (IsName(word) && next.Word.Equals("EQU", StringComparison.OrdinalIgnoreCase))
                    {
                        labels[word] = Evaluate(next.Tail, labels, final, pc) ?? 0;
                        continue;
                    }

                    var bytes = Line(word.ToUpperInvariant(), rest, labels, final, ref pc);
                    foreach (var (address, value) in bytes) image[address & 0xFFFF] = value;
                }
                catch (FormatException e)
                {
                    throw new FormatException($"Line {lineNumber}: {e.Message} ({raw.Trim()})");
                }
            }

            if (final) break;
            if (pass > 0 && labels.Count == before.Count && labels.All(l => before.TryGetValue(l.Key, out var v) && v == l.Value)) pass = 10;
        }

        return new Output(image, labels);
    }

    private static readonly HashSet<string> Directives = new(StringComparer.OrdinalIgnoreCase)
    {
        "ORG", "DB", "DEFB", "DW", "DEFW", "DEFM", "DS", "DEFS", "EQU",
    };

    private static bool IsKeyword(string word) => Directives.Contains(word) || Mnemonics.Contains(word);

    private static string StripComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inString = !inString;
            else if (!inString && line[i] == '\'' && i + 2 < line.Length && line[i + 2] == '\'') i += 2;
            else if (line[i] == ';' && !inString) return line[..i];
        }

        return line;
    }

    private static bool IsName(string text) =>
        text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_' || text[0] == '.') && text.All(c => char.IsLetterOrDigit(c) || c is '_' or '.');

    private static (string Word, string Tail) SplitWord(string line)
    {
        var space = line.IndexOfAny([' ', '\t', '=']);
        if (space < 0) return (line, string.Empty);
        return (line[..space], line[space..].Trim());
    }

    /// <summary>Comma-separated items, commas inside quotes or parentheses not counting.</summary>
    private static List<string> SplitList(string text)
    {
        List<string> items = [];
        var depth = 0;
        var inString = false;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"') inString = !inString;
            else if (!inString && c == '\'' && i + 2 < text.Length && text[i + 2] == '\'') i += 2;
            else if (!inString && c == '(') depth++;
            else if (!inString && c == ')') depth--;
            else if (!inString && depth == 0 && c == ',')
            {
                items.Add(text[start..i].Trim());
                start = i + 1;
            }
        }

        if (text.Trim().Length > 0) items.Add(text[start..].Trim());
        return items;
    }

    // ---- one line ------------------------------------------------------------------

    private static List<(int, byte)> Line(string word, string operands, Dictionary<string, long> labels, bool final, ref int pc)
    {
        List<(int, byte)> output = [];
        var here = pc;
        long Value(string text) => Evaluate(text, labels, final, here) ?? 0;

        switch (word)
        {
            case "ORG":
                pc = (int)(Evaluate(operands, labels, true, pc) ?? 0);
                return output;

            case "DS" or "DEFS":
                pc += (int)Value(operands);
                return output;

            case "DB" or "DEFB" or "DEFM":
                foreach (var item in SplitList(operands))
                {
                    if (item.StartsWith('"') && item.EndsWith('"') && item.Length >= 2)
                    {
                        foreach (var ch in Encoding.ASCII.GetBytes(item[1..^1])) output.Add((pc++, ch));
                    }
                    else
                    {
                        output.Add((pc++, (byte)Value(item)));
                    }
                }

                return output;

            case "DW" or "DEFW":
                foreach (var item in SplitList(operands))
                {
                    var v = Value(item);
                    output.Add((pc++, (byte)v));
                    output.Add((pc++, (byte)(v >> 8)));
                }

                return output;
        }

        var items = SplitList(operands);
        List<string> values = [];
        var keyOperands = new List<string>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var upper = item.ToUpperInvariant().Replace(" ", "", StringComparison.Ordinal);

            // The numbers that are part of the opcode: BIT/RES/SET's bit, RST's address, IM's mode.
            if (i == 0 && word is "BIT" or "RES" or "SET" or "IM")
            {
                keyOperands.Add(Value(item).ToString(CultureInfo.InvariantCulture));
                continue;
            }

            if (i == 0 && word == "RST")
            {
                keyOperands.Add("$" + Value(item).ToString("X2", CultureInfo.InvariantCulture));
                continue;
            }

            if (IsRegisterText(upper))
            {
                keyOperands.Add(upper);
                continue;
            }

            // (IX+d), (IY-d), (IX)
            if (upper.StartsWith("(IX", StringComparison.Ordinal) || upper.StartsWith("(IY", StringComparison.Ordinal))
            {
                var inside = item.Trim()[1..^1].Trim();
                var register = inside[..2].ToUpperInvariant();
                var offset = inside[2..].Trim();
                values.Add(offset.Length == 0 ? "0" : offset.StartsWith('+') ? offset[1..] : offset);
                keyOperands.Add($"({register}+#)");
                continue;
            }

            if (item.StartsWith('(') && item.EndsWith(')') && Balanced(item[1..^1]))
            {
                values.Add(item[1..^1]);
                keyOperands.Add("(#)");
                continue;
            }

            values.Add(item);
            keyOperands.Add("#");
        }

        var key = keyOperands.Count == 0 ? word : word + " " + string.Join(",", keyOperands);
        if (!Table.TryGetValue(key, out var form))
        {
            // A literal the pattern spells out, such as OUT (C),0.
            var literal = word + (items.Count == 0 ? "" : " " + string.Join(",", items.Select(o => o.ToUpperInvariant().Replace(" ", "", StringComparison.Ordinal))));
            if (!Table.TryGetValue(literal, out form)) throw new FormatException($"no Z80 instruction '{key}'");
            values.Clear();
        }

        if (form.Fields.Count != values.Count) throw new FormatException($"'{key}' wants {form.Fields.Count} operand(s)");

        var bytes = (byte[])form.Bytes.Clone();
        for (var i = 0; i < values.Count; i++)
        {
            var (kind, offset) = form.Fields[i];
            var v = Value(values[i]);
            switch (kind)
            {
                case Z80OperandKind.Word:
                    bytes[offset] = (byte)v;
                    bytes[offset + 1] = (byte)(v >> 8);
                    break;

                case Z80OperandKind.Relative:
                {
                    var distance = v - (here + bytes.Length);
                    if (final && distance is < -128 or > 127) throw new FormatException($"a relative jump of {distance} is out of range");
                    bytes[offset] = (byte)distance;
                    break;
                }

                case Z80OperandKind.Displacement:
                    if (final && v is < -128 or > 127) throw new FormatException($"a displacement of {v} is out of range");
                    bytes[offset] = (byte)v;
                    break;

                default:
                    if (final && v is < -128 or > 255) throw new FormatException($"{v} does not fit a byte");
                    bytes[offset] = (byte)v;
                    break;
            }
        }

        foreach (var b in bytes) output.Add((pc++, b));
        return output;
    }

    private static readonly HashSet<string> RegisterTexts =
    [
        "A", "B", "C", "D", "E", "H", "L", "I", "R", "F", "AF", "AF'", "BC", "DE", "HL", "SP", "IX", "IY", "IXH", "IXL", "IYH", "IYL",
        "NZ", "Z", "NC", "PO", "PE", "P", "M", "(C)", "(HL)", "(BC)", "(DE)", "(SP)", "(IX)", "(IY)",
    ];

    private static bool IsRegisterText(string upper) => RegisterTexts.Contains(upper) && upper is not ("(IX)" or "(IY)");

    private static bool Balanced(string text)
    {
        var depth = 0;
        foreach (var c in text)
        {
            if (c == '(') depth++;
            else if (c == ')' && --depth < 0) return false;
        }

        return depth == 0;
    }

    // ---- expressions ---------------------------------------------------------------

    private static long? Evaluate(string text, Dictionary<string, long> labels, bool final, int pc)
    {
        var position = 0;
        var unknown = false;
        var value = Expression(text, ref position, labels, ref unknown, pc, 0);
        while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
        if (position != text.Length) throw new FormatException($"cannot read '{text}'");
        if (unknown)
        {
            if (final) throw new FormatException($"unknown label in '{text}'");
            return null;
        }

        return value;
    }

    private static readonly string[][] Precedence = [["|"], ["&"], ["<<", ">>"], ["+", "-"], ["*", "/"]];

    private static long Expression(string text, ref int position, Dictionary<string, long> labels, ref bool unknown, int pc, int level)
    {
        if (level == Precedence.Length) return Unary(text, ref position, labels, ref unknown, pc);

        var left = Expression(text, ref position, labels, ref unknown, pc, level + 1);
        while (true)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
            var p = position;
            var op = Precedence[level].FirstOrDefault(o => string.CompareOrdinal(text, p, o, 0, o.Length) == 0);
            if (op is null) return left;
            position += op.Length;
            var right = Expression(text, ref position, labels, ref unknown, pc, level + 1);
            left = op switch
            {
                "|" => left | right,
                "&" => left & right,
                "<<" => left << (int)right,
                ">>" => left >> (int)right,
                "+" => left + right,
                "-" => left - right,
                "*" => left * right,
                _ => right == 0 ? 0 : left / right,
            };
        }
    }

    private static long Unary(string text, ref int position, Dictionary<string, long> labels, ref bool unknown, int pc)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
        if (position >= text.Length) throw new FormatException("an expression ends too soon");

        var c = text[position];
        if (c == '-')
        {
            position++;
            return -Unary(text, ref position, labels, ref unknown, pc);
        }

        if (c == '(')
        {
            position++;
            var value = Expression(text, ref position, labels, ref unknown, pc, 0);
            if (position >= text.Length || text[position] != ')') throw new FormatException("a ')' is missing");
            position++;
            return value;
        }

        if (c == '\'' && position + 2 < text.Length && text[position + 2] == '\'')
        {
            position += 3;
            return text[position - 2];
        }

        // $ alone is here; $ followed by hex digits is a number.
        if (c == '$' && (position + 1 >= text.Length || !Uri.IsHexDigit(text[position + 1])))
        {
            position++;
            return pc;
        }

        var start = position;
        if (c is '$' or '%') position++;
        while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] is '_' or '.')) position++;
        var token = text[start..position];

        if (token.StartsWith('$')) return long.Parse(token[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (token.StartsWith('%')) return Convert.ToInt64(token[1..], 2);
        if (token.Length > 1 && char.IsDigit(token[0]) && (token[^1] is 'h' or 'H'))
            return long.Parse(token[..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (token.Length > 0 && char.IsDigit(token[0])) return long.Parse(token, CultureInfo.InvariantCulture);
        if (labels.TryGetValue(token, out var known)) return known;
        if (token.Length == 0) throw new FormatException($"cannot read '{text}'");
        unknown = true;
        return 0;
    }
}
