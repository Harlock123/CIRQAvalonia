using System.Globalization;
using Cirq.Components.Processors;

namespace Cirq.Components.Tests;

/// <summary>
/// A 6809 assembler, for the tests only: it turns the programs the tests and the guide show into
/// bytes, so a test can check the bytes in an example are the source it claims, and it reads back
/// what <see cref="Opcodes6809.Disassemble"/> prints.
/// <para>
/// Motorola syntax, one instruction or directive a line: <c>;</c> comments (or a <c>*</c> line),
/// <c>label:</c> (or a label alone in the first column), <c>NAME equ value</c> or <c>NAME = value</c>,
/// <c>org</c>, <c>fcb</c> (numbers and "strings"), <c>fdb</c>, <c>fcc "text"</c>, <c>rmb</c>, and
/// <c>setdp</c>. Numbers are <c>$FF</c>, <c>%1010</c>, decimal or <c>'c'</c>; expressions have
/// <c>+ - * / &amp; | &lt;&lt; &gt;&gt;</c>, unary minus, parentheses and <c>*</c> for here. Every
/// 6809 instruction and addressing mode: <c>#n</c>; <c>&lt;n</c> for direct (and a bare address on
/// the page <c>setdp</c> names); <c>&gt;n</c> or a bare address for extended; <c>,X</c>,
/// <c>,X+</c>, <c>,X++</c>, <c>,-X</c>, <c>,--X</c>, <c>A,X</c>, <c>B,X</c>, <c>D,X</c>,
/// <c>n,X</c> (five, eight or sixteen bits by its size, or eight with <c>&lt;</c> and sixteen with
/// <c>&gt;</c>), <c>label,PCR</c>, and any of those in brackets for indirect, <c>[n]</c> included;
/// register lists for PSHS and the rest; LSL, BHS, BLO, LBHS and LBLO as aliases. The passes repeat
/// until every label stops moving, so a forward label can shrink an offset safely.
/// </para>
/// </summary>
internal static class Assembler6809
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

    /// <summary>Each documented mnemonic's opcodes by addressing mode, page prefix in the high byte.</summary>
    private static readonly Dictionary<string, Dictionary<M6809Mode, int>> Table = BuildTable();

    private static Dictionary<string, Dictionary<M6809Mode, int>> BuildTable()
    {
        Dictionary<string, Dictionary<M6809Mode, int>> table = new(StringComparer.OrdinalIgnoreCase);
        foreach (var prefix in new[] { 0, 0x10, 0x11 })
        {
            for (var op = 0; op < 256; op++)
            {
                var e = Opcodes6809.Get(prefix, (byte)op);
                if (e.Undocumented || e.Mode == M6809Mode.Prefix) continue;

                // Pages 2 and 3 repeat page 1 where nothing was assigned: keep page 1's.
                if (prefix != 0 && Opcodes6809.Get(0, (byte)op) == e) continue;

                if (!table.TryGetValue(e.Mnemonic, out var modes)) table[e.Mnemonic] = modes = [];
                modes.TryAdd(e.Mode, (prefix << 8) | op);
            }
        }

        foreach (var (alias, name) in new[] { ("LSL", "ASL"), ("LSLA", "ASLA"), ("LSLB", "ASLB"), ("BHS", "BCC"), ("BLO", "BCS"), ("LBHS", "LBCC"), ("LBLO", "LBCS") })
            table[alias] = table[name];
        return table;
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
            var dp = -1;
            var lineNumber = 0;
            var final = pass > 0 && pass == 11;

            foreach (var raw in source.Split('\n'))
            {
                lineNumber++;
                var stripped = StripComment(raw).TrimEnd();
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

                    var bytes = Line(word.ToUpperInvariant(), rest, labels, final, ref pc, ref dp);
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
        "ORG", "FCB", "FDB", "FCC", "RMB", "SETDP", "EQU", ".BYTE", ".WORD", ".ORG",
    };

    private static bool IsKeyword(string word) => Directives.Contains(word) || Table.ContainsKey(word);

    private static string StripComment(string line)
    {
        if (line.TrimStart().StartsWith('*')) return string.Empty;
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

    // ---- one line ------------------------------------------------------------------

    private static List<(int, byte)> Line(string word, string operands, Dictionary<string, long> labels, bool final, ref int pc, ref int dp)
    {
        List<(int, byte)> output = [];
        var here = pc;
        long Value(string text) => Evaluate(text, labels, final, here) ?? 0;

        switch (word)
        {
            case "ORG" or ".ORG":
                pc = (int)(Evaluate(operands, labels, true, pc) ?? 0);
                return output;

            case "SETDP":
                dp = (int)Value(operands) & 0xFF;
                return output;

            case "RMB":
                pc += (int)Value(operands);
                return output;

            case "FCB" or ".BYTE":
                foreach (var item in SplitList(operands))
                {
                    if (item.StartsWith('"'))
                    {
                        foreach (var ch in item[1..^1]) output.Add((pc++, (byte)ch));
                    }
                    else
                    {
                        output.Add((pc++, (byte)Value(item)));
                    }
                }

                return output;

            case "FCC":
            {
                var text = operands.Trim();
                foreach (var ch in text[1..^1]) output.Add((pc++, (byte)ch));
                return output;
            }

            case "FDB" or ".WORD":
                foreach (var item in SplitList(operands))
                {
                    var v = Value(item);
                    output.Add((pc++, (byte)(v >> 8)));
                    output.Add((pc++, (byte)v));
                }

                return output;
        }

        var bytes = Instruction(word, operands.Trim(), labels, final, pc, dp);
        foreach (var b in bytes) output.Add((pc++, b));
        return output;
    }

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
            else if (!inString && c is '(' or '[') depth++;
            else if (!inString && c is ')' or ']') depth--;
            else if (!inString && depth == 0 && c == ',')
            {
                items.Add(text[start..i].Trim());
                start = i + 1;
            }
        }

        items.Add(text[start..].Trim());
        return items.Where(s => s.Length > 0).ToList();
    }

    // ---- instructions --------------------------------------------------------------

    private static readonly Dictionary<string, int> PairCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["D"] = 0, ["X"] = 1, ["Y"] = 2, ["U"] = 3, ["S"] = 4, ["PC"] = 5, ["A"] = 8, ["B"] = 9, ["CC"] = 10, ["DP"] = 11,
    };

    private static List<byte> Instruction(string mnemonic, string operand, Dictionary<string, long> labels, bool final, int pc, int dp)
    {
        if (!Table.TryGetValue(mnemonic, out var modes)) throw new FormatException($"unknown instruction {mnemonic}");
        long Value(string text) => Evaluate(text, labels, final, pc) ?? 0;

        List<byte> Opcode(int code)
        {
            List<byte> b = [];
            if (code > 0xFF) b.Add((byte)(code >> 8));
            b.Add((byte)code);
            return b;
        }

        if (modes.TryGetValue(M6809Mode.Inherent, out var inherent) && operand.Length == 0) return Opcode(inherent);

        if (modes.TryGetValue(M6809Mode.RegisterPair, out var pair))
        {
            var regs = SplitList(operand);
            if (regs.Count != 2 || !PairCodes.TryGetValue(regs[0], out var from) || !PairCodes.TryGetValue(regs[1], out var to))
                throw new FormatException($"{mnemonic} wants two registers");
            var b = Opcode(pair);
            b.Add((byte)((from << 4) | to));
            return b;
        }

        if (modes.TryGetValue(M6809Mode.RegisterList, out var list))
        {
            var other = mnemonic.EndsWith('S') ? "U" : "S";
            var mask = 0;
            foreach (var reg in SplitList(operand))
            {
                mask |= reg.ToUpperInvariant() switch
                {
                    "CC" => 0x01,
                    "A" => 0x02,
                    "B" => 0x04,
                    "D" => 0x06,
                    "DP" => 0x08,
                    "X" => 0x10,
                    "Y" => 0x20,
                    "PC" => 0x80,
                    var r when r == other => 0x40,
                    _ => throw new FormatException($"{mnemonic} cannot push or pull {reg}"),
                };
            }

            var b = Opcode(list);
            b.Add((byte)mask);
            return b;
        }

        if (modes.TryGetValue(M6809Mode.Relative8, out var rel8))
        {
            var b = Opcode(rel8);
            var offset = Value(operand) - (pc + b.Count + 1);
            if (final && offset is < -128 or > 127) throw new FormatException($"{mnemonic} cannot reach {operand} ({offset} bytes): use the long branch");
            b.Add((byte)offset);
            return b;
        }

        if (modes.TryGetValue(M6809Mode.Relative16, out var rel16))
        {
            var b = Opcode(rel16);
            var offset = Value(operand) - (pc + b.Count + 2);
            b.Add((byte)(offset >> 8));
            b.Add((byte)offset);
            return b;
        }

        if (operand.StartsWith('#'))
        {
            var v = Value(operand[1..]);
            if (modes.TryGetValue(M6809Mode.Immediate8, out var imm8))
            {
                var b = Opcode(imm8);
                b.Add((byte)v);
                return b;
            }

            if (modes.TryGetValue(M6809Mode.Immediate16, out var imm16))
            {
                var b = Opcode(imm16);
                b.Add((byte)(v >> 8));
                b.Add((byte)v);
                return b;
            }

            throw new FormatException($"{mnemonic} has no immediate mode");
        }

        if (operand.StartsWith('[') || operand.Contains(','))
        {
            if (!modes.TryGetValue(M6809Mode.Indexed, out var indexed)) throw new FormatException($"{mnemonic} has no indexed mode");
            var b = Opcode(indexed);
            b.AddRange(Indexed(operand, labels, final, pc + b.Count));
            return b;
        }

        if (operand.StartsWith('<'))
        {
            var b = Opcode(modes[M6809Mode.Direct]);
            b.Add((byte)Value(operand[1..]));
            return b;
        }

        var forced = operand.StartsWith('>');
        var address = Value(forced ? operand[1..] : operand);
        if (!forced && dp >= 0 && ((address >> 8) & 0xFF) == dp && modes.TryGetValue(M6809Mode.Direct, out var direct))
        {
            var b = Opcode(direct);
            b.Add((byte)address);
            return b;
        }

        if (!modes.TryGetValue(M6809Mode.Extended, out var extended)) throw new FormatException($"{mnemonic} has no extended mode");
        var e = Opcode(extended);
        e.Add((byte)(address >> 8));
        e.Add((byte)address);
        return e;
    }

    /// <summary>The postbyte and offset of an indexed operand; <paramref name="at"/> is where the postbyte goes.</summary>
    private static List<byte> Indexed(string operand, Dictionary<string, long> labels, bool final, int at)
    {
        var indirect = operand.StartsWith('[');
        var body = indirect ? operand.TrimStart('[').TrimEnd(']').Trim() : operand;
        var ind = indirect ? 0x10 : 0;
        long Value(string text) => Evaluate(text, labels, final, at - 1) ?? 0;

        if (indirect && !body.Contains(','))
        {
            var address = Value(body);
            return [0x9F, (byte)(address >> 8), (byte)address];
        }

        var comma = body.LastIndexOf(',');
        var offset = body[..comma].Trim();
        var register = body[(comma + 1)..].Trim().ToUpperInvariant();

        int Reg(string r) => r switch
        {
            "X" => 0x00,
            "Y" => 0x20,
            "U" => 0x40,
            "S" => 0x60,
            _ => throw new FormatException($"no index register {r}"),
        };

        if (register is "PCR" or "PC")
        {
            // label,PCR names the target; n,PC gives the offset itself.
            var force8 = offset.StartsWith('<');
            var force16 = offset.StartsWith('>');
            var value = Value(force8 || force16 ? offset[1..] : offset);
            var relative = register == "PCR";
            var short8 = relative ? value - (at + 2) : value;
            if (force8 || (!force16 && short8 is >= -128 and <= 127))
                return [(byte)(0x8C | ind), (byte)short8];
            var long16 = relative ? value - (at + 3) : value;
            return [(byte)(0x8D | ind), (byte)(long16 >> 8), (byte)long16];
        }

        if (offset.Length == 0)
        {
            if (register.StartsWith("--")) return [(byte)(0x83 | ind | Reg(register[2..]))];
            if (register.StartsWith('-')) return [(byte)(0x82 | ind | Reg(register[1..]))];
            if (register.EndsWith("++")) return [(byte)(0x81 | ind | Reg(register[..^2]))];
            if (register.EndsWith('+')) return [(byte)(0x80 | ind | Reg(register[..^1]))];
            return [(byte)(0x84 | ind | Reg(register))];
        }

        switch (offset.ToUpperInvariant())
        {
            case "A": return [(byte)(0x86 | ind | Reg(register))];
            case "B": return [(byte)(0x85 | ind | Reg(register))];
            case "D": return [(byte)(0x8B | ind | Reg(register))];
        }

        var r8 = offset.StartsWith('<') && !offset.StartsWith("<<");
        var r5 = offset.StartsWith("<<");
        var r16 = offset.StartsWith('>');
        var n = Value(r5 ? offset[2..] : r8 || r16 ? offset[1..] : offset);
        var reg = Reg(register);

        if (r5 || (!r8 && !r16 && !indirect && n is >= -16 and <= 15)) return [(byte)(reg | (int)(n & 0x1F))];
        if (r8 || (!r16 && n is >= -128 and <= 127)) return [(byte)(0x88 | ind | reg), (byte)n];
        return [(byte)(0x89 | ind | reg), (byte)(n >> 8), (byte)n];
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

        if (c == '*')
        {
            position++;
            return pc;
        }

        if (c == '\'' && position + 2 < text.Length && text[position + 2] == '\'')
        {
            position += 3;
            return text[position - 2];
        }

        var start = position;
        if (c is '$' or '%') position++;
        while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] is '_' or '.')) position++;
        var token = text[start..position];

        if (token.StartsWith('$')) return long.Parse(token[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (token.StartsWith('%')) return Convert.ToInt64(token[1..], 2);
        if (token.Length > 0 && char.IsDigit(token[0])) return long.Parse(token, CultureInfo.InvariantCulture);
        if (labels.TryGetValue(token, out var known)) return known;
        if (token.Length == 0) throw new FormatException($"cannot read '{text}'");
        unknown = true;
        return 0;
    }
}
