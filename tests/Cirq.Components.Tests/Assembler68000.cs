using System.Globalization;

namespace Cirq.Components.Tests;

/// <summary>
/// A two-pass 68000 assembler, for the tests only: it turns the programs the tests and the guide
/// show into bytes, so a test can check the bytes in an example are the source it claims.
/// <para>
/// Motorola syntax, one instruction or directive a line: <c>;</c> comments (or a <c>*</c> line),
/// <c>label:</c>, <c>NAME equ value</c> or <c>NAME = value</c>, <c>org</c>, <c>dc.b</c> (numbers
/// and "strings"), <c>dc.w</c>, <c>dc.l</c>, <c>ds.b</c>/<c>ds.w</c>/<c>ds.l</c>, <c>even</c>.
/// Numbers are <c>$FF</c>, <c>%1010</c>, decimal or <c>'c'</c>; expressions have <c>+ - * / &amp; |
/// &lt;&lt; &gt;&gt;</c>, unary minus and parentheses. Every 68000 instruction and addressing mode:
/// <c>d16(An)</c> or <c>(d16,An)</c>, <c>d8(An,Xn.W)</c>, <c>label(PC)</c> (the assembler works out
/// the displacement), <c>$1234.W</c> for an absolute short address (long otherwise), <c>#imm</c>,
/// <c>D0-D3/A0/A6</c> register lists. ADD, SUB, AND, OR, EOR and CMP with an immediate source become
/// the immediate forms, and with an address register destination the A forms. A branch without
/// <c>.S</c> takes a word displacement, so sizes never change between the passes.
/// </para>
/// </summary>
internal static class Assembler68000
{
    /// <summary>The assembled bytes by address, and the labels.</summary>
    public sealed record Output(SortedDictionary<uint, byte> Bytes, IReadOnlyDictionary<string, long> Labels)
    {
        /// <summary>The bytes from <paramref name="start"/>, <paramref name="length"/> of them, unused bytes zero.</summary>
        public byte[] Image(uint start, int length)
        {
            var image = new byte[length];
            foreach (var (address, value) in Bytes)
                if (address >= start && address < start + length) image[address - start] = value;
            return image;
        }

        /// <summary>The even bytes of an image (the upper byte, D8-D15) or the odd ones (D0-D7), as two ROMs hold them.</summary>
        public static byte[] Lane(byte[] image, bool odd)
        {
            var lane = new byte[image.Length / 2];
            for (var i = 0; i < lane.Length; i++) lane[i] = image[i * 2 + (odd ? 1 : 0)];
            return lane;
        }

        /// <summary>The word at <paramref name="address"/>.</summary>
        public ushort Word(uint address) =>
            (ushort)((Bytes.GetValueOrDefault(address) << 8) | Bytes.GetValueOrDefault(address + 1));

        public long Label(string name) => Labels[name];
    }

    private static readonly string[] Conditions = ["T", "F", "HI", "LS", "CC", "CS", "NE", "EQ", "VC", "VS", "PL", "MI", "GE", "LT", "GT", "LE"];

    public static Output Assemble(string source)
    {
        Dictionary<string, long> labels = new(StringComparer.OrdinalIgnoreCase);
        SortedDictionary<uint, byte> image = [];

        for (var pass = 0; pass < 2; pass++)
        {
            image = [];
            uint pc = 0;
            var lineNumber = 0;

            foreach (var raw in source.Split('\n'))
            {
                lineNumber++;
                var line = StripComment(raw).Trim();
                if (line.Length == 0) continue;

                try
                {
                    // label:
                    var colon = line.IndexOf(':');
                    if (colon > 0 && IsName(line[..colon].Trim()))
                    {
                        labels[line[..colon].Trim()] = pc;
                        line = line[(colon + 1)..].Trim();
                        if (line.Length == 0) continue;
                    }

                    var parts = SplitWord(line);
                    var word = parts.Word;
                    var rest = parts.Tail;

                    // NAME = value, NAME equ value
                    if (rest.StartsWith('=') && IsName(word))
                    {
                        labels[word] = Evaluate(rest[1..], labels, pass, pc) ?? 0;
                        continue;
                    }

                    var second = SplitWord(rest);
                    if (IsName(word) && second.Word.Equals("EQU", StringComparison.OrdinalIgnoreCase))
                    {
                        labels[word] = Evaluate(second.Tail, labels, pass, pc) ?? 0;
                        continue;
                    }

                    var bytes = Line(word.ToUpperInvariant(), rest, labels, pass, ref pc);
                    foreach (var (address, value) in bytes) image[address & 0xFFFFFF] = value;
                }
                catch (FormatException e)
                {
                    throw new FormatException($"Line {lineNumber}: {e.Message} ({raw.Trim()})");
                }
            }
        }

        return new Output(image, labels);
    }

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

    private static List<(uint, byte)> Line(string word, string operands, Dictionary<string, long> labels, int pass, ref uint pc)
    {
        List<(uint, byte)> output = [];
        var at = pc;

        void Byte(long v) => output.Add((at++, (byte)v));

        void Word(long v)
        {
            Byte(v >> 8);
            Byte(v);
        }

        var here = pc;
        long Value(string text) => Evaluate(text, labels, pass, here) ?? 0;

        switch (word)
        {
            case "ORG":
                pc = (uint)(Evaluate(operands, labels, 0, pc) ?? throw new FormatException("org needs a value known at once"));
                return output;

            case "EVEN":
                if ((pc & 1) != 0) pc++;
                return output;

            case "DC.B":
                foreach (var item in SplitList(operands))
                {
                    if (item.StartsWith('"'))
                        foreach (var c in item.Trim('"')) Byte(c);
                    else Byte(Value(item));
                }

                pc = at;
                return output;

            case "DC.W":
                foreach (var item in SplitList(operands)) Word(Value(item));
                pc = at;
                return output;

            case "DC.L":
                foreach (var item in SplitList(operands))
                {
                    var v = Value(item);
                    Word(v >> 16);
                    Word(v);
                }

                pc = at;
                return output;

            case "DS.B" or "DS.W" or "DS.L":
            {
                var count = Value(operands) * (word[^1] switch { 'B' => 1, 'W' => 2, _ => 4 });
                for (var i = 0; i < count; i++) Byte(0);
                pc = at;
                return output;
            }
        }

        if ((pc & 1) != 0) throw new FormatException("an instruction at an odd address");

        var words = Instruction(word, operands, labels, pass, pc);
        foreach (var w in words) Word(w);
        pc = at;
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
            if (inString) continue;
            if (c == '(') depth++;
            if (c == ')') depth--;
            if (c == ',' && depth == 0)
            {
                items.Add(text[start..i].Trim());
                start = i + 1;
            }
        }

        if (text.Trim().Length > 0) items.Add(text[start..].Trim());
        return items;
    }

    // ---- operands ---------------------------------------------------------------------

    private enum Kind { Dn, An, Ind, PostInc, PreDec, Disp, Index, AbsW, AbsL, PcDisp, PcIndex, Imm, Sr, Ccr, Usp, List }

    private sealed record Operand(Kind Kind, int Reg = 0, long Value = 0, int IndexReg = 0, bool IndexLong = false, ushort Mask = 0)
    {
        public int Mode => Kind switch
        {
            Kind.Dn => 0,
            Kind.An => 1,
            Kind.Ind => 2,
            Kind.PostInc => 3,
            Kind.PreDec => 4,
            Kind.Disp => 5,
            Kind.Index => 6,
            _ => 7,
        };

        public int RegField => Kind switch
        {
            Kind.AbsW => 0,
            Kind.AbsL => 1,
            Kind.PcDisp => 2,
            Kind.PcIndex => 3,
            Kind.Imm => 4,
            _ => Reg,
        };

        public int Ea => (Mode << 3) | RegField;

        public bool IsMemory => Kind is not (Kind.Dn or Kind.An or Kind.Imm);
    }

    private static int? Register(string text, char letter)
    {
        var t = text.Trim().ToUpperInvariant();
        if (letter == 'A' && t == "SP") return 7;
        if (t.Length == 2 && t[0] == letter && t[1] is >= '0' and <= '7') return t[1] - '0';
        return null;
    }

    private static (int Reg, bool Long)? IndexRegister(string text)
    {
        var t = text.Trim().ToUpperInvariant();
        var isLong = false;
        if (t.EndsWith(".L", StringComparison.Ordinal))
        {
            isLong = true;
            t = t[..^2];
        }
        else if (t.EndsWith(".W", StringComparison.Ordinal))
        {
            t = t[..^2];
        }

        if (Register(t, 'D') is { } d) return (d, isLong);
        if (Register(t, 'A') is { } a) return (8 + a, isLong);
        return null;
    }

    private static Operand Parse(string text, Dictionary<string, long> labels, int pass, uint pc)
    {
        var t = text.Trim();
        var upper = t.ToUpperInvariant();
        long Value(string e) => Evaluate(e, labels, pass, pc) ?? 0;

        if (t.StartsWith('#')) return new Operand(Kind.Imm, Value: Value(t[1..]));
        if (Register(t, 'D') is { } d) return new Operand(Kind.Dn, d);
        if (Register(t, 'A') is { } a) return new Operand(Kind.An, a);
        if (upper == "SR") return new Operand(Kind.Sr);
        if (upper == "CCR") return new Operand(Kind.Ccr);
        if (upper == "USP") return new Operand(Kind.Usp);
        if (RegisterList(t) is { } mask) return new Operand(Kind.List, Mask: mask);

        if (upper.StartsWith("-(", StringComparison.Ordinal) && upper.EndsWith(')') && Register(t[2..^1], 'A') is { } pre)
            return new Operand(Kind.PreDec, pre);
        if (upper.StartsWith('(') && upper.EndsWith(")+", StringComparison.Ordinal) && Register(t[1..^2], 'A') is { } post)
            return new Operand(Kind.PostInc, post);

        // d(An), d(An,Xn), (d,An), (d,An,Xn), (An), and the same with PC.
        if (t.EndsWith(')'))
        {
            var open = FindOpen(t);
            if (open >= 0)
            {
                var outside = t[..open].Trim();
                var inside = SplitList(t[(open + 1)..^1]);
                string displacement;
                List<string> registers;

                if (outside.Length == 0 && inside.Count > 0 && Register(inside[0], 'A') is null && !inside[0].Trim().Equals("PC", StringComparison.OrdinalIgnoreCase))
                {
                    displacement = inside[0];
                    registers = inside.Skip(1).ToList();
                }
                else
                {
                    displacement = outside;
                    registers = inside;
                }

                if (registers.Count >= 1)
                {
                    var baseText = registers[0].Trim().ToUpperInvariant();
                    var isPc = baseText == "PC";
                    var baseReg = isPc ? 0 : Register(baseText, 'A');
                    if (baseReg is not null)
                    {
                        var dispValue = displacement.Length == 0 ? 0 : Value(displacement);
                        if (registers.Count == 1)
                        {
                            if (isPc) return new Operand(Kind.PcDisp, Value: dispValue);
                            return displacement.Length == 0 ? new Operand(Kind.Ind, baseReg.Value) : new Operand(Kind.Disp, baseReg.Value, dispValue);
                        }

                        var index = IndexRegister(registers[1]) ?? throw new FormatException($"'{registers[1]}' is no index register");
                        return new Operand(isPc ? Kind.PcIndex : Kind.Index, baseReg.Value, dispValue, index.Reg, index.Long);
                    }
                }
            }
        }

        if (upper.EndsWith(".W", StringComparison.Ordinal)) return new Operand(Kind.AbsW, Value: Value(t[..^2]));
        if (upper.EndsWith(".L", StringComparison.Ordinal)) return new Operand(Kind.AbsL, Value: Value(t[..^2]));
        return new Operand(Kind.AbsL, Value: Value(t));
    }

    /// <summary>The '(' that opens the addressing part at the end of an operand.</summary>
    private static int FindOpen(string text)
    {
        var depth = 0;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (text[i] == ')') depth++;
            if (text[i] == '(')
            {
                depth--;
                if (depth == 0)
                {
                    var inside = text[(i + 1)..^1];
                    return inside.Split(',').Any(p => Register(p.Trim(), 'A') is not null || p.Trim().Equals("PC", StringComparison.OrdinalIgnoreCase)) ? i : -1;
                }
            }
        }

        return -1;
    }

    /// <summary>D0-D3/A0/A6 as a mask, D0 in bit 0 and A7 in bit 15; null if it is not a list.</summary>
    private static ushort? RegisterList(string text)
    {
        var mask = 0;
        foreach (var part in text.Split('/'))
        {
            var range = part.Split('-');
            int? Index(string r) => Register(r, 'D') is { } d ? d : Register(r, 'A') is { } a ? 8 + a : null;
            if (range.Length == 1)
            {
                if (Index(range[0]) is not { } one) return null;
                mask |= 1 << one;
            }
            else if (range.Length == 2)
            {
                if (Index(range[0]) is not { } first || Index(range[1]) is not { } last || last < first) return null;
                for (var i = first; i <= last; i++) mask |= 1 << i;
            }
            else
            {
                return null;
            }
        }

        // A single register on its own is a register, not a list.
        return text.Contains('/') || text.Contains('-') ? (ushort)mask : null;
    }

    /// <summary>The extension words an operand adds, for an instruction whose extension words start at <paramref name="at"/>.</summary>
    private static List<long> Extension(Operand o, int size, uint at)
    {
        switch (o.Kind)
        {
            case Kind.Disp:
                return [o.Value & 0xFFFF];
            case Kind.Index:
                return [Brief(o, o.Value)];
            case Kind.AbsW:
                return [o.Value & 0xFFFF];
            case Kind.AbsL:
                return [(o.Value >> 16) & 0xFFFF, o.Value & 0xFFFF];
            case Kind.PcDisp:
                return [(o.Value - at) & 0xFFFF];
            case Kind.PcIndex:
                return [Brief(o, o.Value - at)];
            case Kind.Imm:
                return size == 4 ? [(o.Value >> 16) & 0xFFFF, o.Value & 0xFFFF] : [size == 1 ? o.Value & 0xFF : o.Value & 0xFFFF];
            default:
                return [];
        }
    }

    private static long Brief(Operand o, long displacement) =>
        ((long)o.IndexReg << 12) | (o.IndexLong ? 0x0800L : 0L) | (displacement & 0xFFL);

    // ---- instructions -------------------------------------------------------------------

    private static int SizeCode(int size) => size switch { 1 => 0, 2 => 1, _ => 2 };

    private static List<long> Instruction(string mnemonic, string operandText, Dictionary<string, long> labels, int pass, uint pc)
    {
        var dot = mnemonic.IndexOf('.');
        var name = dot < 0 ? mnemonic : mnemonic[..dot];
        var suffix = dot < 0 ? "" : mnemonic[(dot + 1)..];
        var size = suffix switch { "B" => 1, "W" => 2, "L" => 4, "S" => 1, "" => 0, _ => throw new FormatException($"no size .{suffix}") };
        var operands = SplitList(operandText).Select(o => Parse(o, labels, pass, pc)).ToList();

        List<long> words = [0];
        void Op(long value) => words[0] = value;

        // Extension words for an operand, placed after what is there already.
        void Ext(Operand o, int sz) => words.AddRange(Extension(o, sz, pc + (uint)(words.Count * 2)));

        Operand One() => operands.Count == 1 ? operands[0] : throw new FormatException($"{name} takes one operand");

        (Operand, Operand) Two() => operands.Count == 2 ? (operands[0], operands[1]) : throw new FormatException($"{name} takes two operands");

        int Sz(int fallback) => size == 0 ? fallback : size;

        var cc = Array.FindIndex(Conditions, c => name.Length > 1 && name[1..] == c);
        if (name is "BHS") cc = 4;
        if (name is "BLO") cc = 5;

        switch (name)
        {
            case "NOP": Op(0x4E71); return words;
            case "RTS": Op(0x4E75); return words;
            case "RTE": Op(0x4E73); return words;
            case "RTR": Op(0x4E77); return words;
            case "RESET": Op(0x4E70); return words;
            case "TRAPV": Op(0x4E76); return words;
            case "ILLEGAL": Op(0x4AFC); return words;
            case "STOP":
                Op(0x4E72);
                words.Add(One().Value & 0xFFFF);
                return words;
            case "TRAP": Op(0x4E40 | (One().Value & 0xF)); return words;
            case "SWAP": Op(0x4840 | One().Reg); return words;
            case "EXT": Op((Sz(2) == 4 ? 0x48C0 : 0x4880) | One().Reg); return words;
            case "UNLK": Op(0x4E58 | One().Reg); return words;
            case "LINK":
            {
                var (an, d) = Two();
                Op(0x4E50 | an.Reg);
                words.Add(d.Value & 0xFFFF);
                return words;
            }

            case "BRA" or "BSR" or "BHS" or "BLO":
            case var _ when name.Length >= 2 && name[0] == 'B' && cc >= 2 && Conditions.Contains(name[1..]):
            {
                var code = name switch { "BRA" => 0, "BSR" => 1, _ => cc };
                var target = One().Value;
                var displacement = target - (pc + 2);
                if (suffix == "S")
                {
                    if (pass == 1 && (displacement is < -128 or > 127 || displacement == 0)) throw new FormatException("a short branch out of range");
                    Op((long)(0x6000 | (code << 8)) | (displacement & 0xFFL));
                    return words;
                }

                Op(0x6000 | (code << 8));
                words.Add(displacement & 0xFFFF);
                return words;
            }

            case var _ when name.StartsWith("DB", StringComparison.Ordinal):
            {
                var code = name == "DBRA" ? 1 : Array.IndexOf(Conditions, name[2..]);
                if (code < 0) break;
                var (dn, target) = Two();
                Op(0x50C8 | (code << 8) | dn.Reg);
                words.Add((target.Value - (pc + 2)) & 0xFFFF);
                return words;
            }

            case var _ when name.Length >= 2 && name[0] == 'S' && Conditions.Contains(name[1..]) && name != "SUB":
            {
                var o = One();
                Op(0x50C0 | (Array.IndexOf(Conditions, name[1..]) << 8) | o.Ea);
                Ext(o, 1);
                return words;
            }

            case "MOVEQ":
            {
                var (imm, dn) = Two();
                Op((long)(0x7000 | (dn.Reg << 9)) | (imm.Value & 0xFFL));
                return words;
            }

            case "MOVE" or "MOVEA":
                return Move(words, operands, Sz(2), pc);

            case "MOVEM":
            {
                var (a, b) = Two();
                var isLong = Sz(2) == 4;
                if (a.Kind is Kind.List or Kind.Dn or Kind.An)
                {
                    var mask = a.Kind == Kind.List ? a.Mask : (ushort)(1 << (a.Kind == Kind.Dn ? a.Reg : a.Reg + 8));
                    if (b.Kind == Kind.PreDec) mask = Reverse(mask);
                    Op(0x4880 | (isLong ? 0x40 : 0) | b.Ea);
                    words.Add(mask);
                    Ext(b, 2);
                }
                else
                {
                    var mask = b.Kind == Kind.List ? b.Mask : (ushort)(1 << (b.Kind == Kind.Dn ? b.Reg : b.Reg + 8));
                    Op(0x4C80 | (isLong ? 0x40 : 0) | a.Ea);
                    words.Add(mask);
                    Ext(a, 2);
                }

                return words;
            }

            case "MOVEP":
            {
                var (a, b) = Two();
                var isLong = Sz(2) == 4;
                if (a.Kind == Kind.Dn)
                {
                    Op(0x0108 | (a.Reg << 9) | ((isLong ? 7 : 6) << 6) | b.Reg);
                    words.Add(b.Value & 0xFFFF);
                }
                else
                {
                    Op(0x0108 | (b.Reg << 9) | ((isLong ? 5 : 4) << 6) | a.Reg);
                    words.Add(a.Value & 0xFFFF);
                }

                return words;
            }

            case "LEA":
            {
                var (ea, an) = Two();
                Op(0x41C0 | (an.Reg << 9) | ea.Ea);
                Ext(ea, 4);
                return words;
            }

            case "PEA" or "JMP" or "JSR":
            {
                var ea = One();
                Op(name switch { "PEA" => 0x4840, "JMP" => 0x4EC0, _ => 0x4E80 } | ea.Ea);
                Ext(ea, 4);
                return words;
            }

            case "CLR" or "NEG" or "NEGX" or "NOT" or "TST":
            {
                var ea = One();
                var sz = Sz(2);
                Op(name switch { "CLR" => 0x4200, "NEG" => 0x4400, "NEGX" => 0x4000, "NOT" => 0x4600, _ => 0x4A00 } | (SizeCode(sz) << 6) | ea.Ea);
                Ext(ea, sz);
                return words;
            }

            case "NBCD" or "TAS":
            {
                var ea = One();
                Op((name == "NBCD" ? 0x4800 : 0x4AC0) | ea.Ea);
                Ext(ea, 1);
                return words;
            }

            case "CHK" or "DIVU" or "DIVS" or "MULU" or "MULS":
            {
                var (ea, dn) = Two();
                Op(name switch { "CHK" => 0x4180, "DIVU" => 0x80C0, "DIVS" => 0x81C0, "MULU" => 0xC0C0, _ => 0xC1C0 } | (dn.Reg << 9) | ea.Ea);
                Ext(ea, 2);
                return words;
            }

            case "ABCD" or "SBCD" or "ADDX" or "SUBX":
            {
                var (y, x) = Two();
                var memory = y.Kind == Kind.PreDec;
                var basis = name switch { "ABCD" => 0xC100, "SBCD" => 0x8100, "ADDX" => 0xD100 | (SizeCode(Sz(2)) << 6), _ => 0x9100 | (SizeCode(Sz(2)) << 6) };
                Op(basis | (x.Reg << 9) | (memory ? 8 : 0) | y.Reg);
                return words;
            }

            case "CMPM":
            {
                var (y, x) = Two();
                Op(0xB108 | (x.Reg << 9) | (SizeCode(Sz(2)) << 6) | y.Reg);
                return words;
            }

            case "EXG":
            {
                var (a, b) = Two();
                if (a.Kind == Kind.An && b.Kind == Kind.Dn) (a, b) = (b, a);
                var mode = (a.Kind, b.Kind) switch
                {
                    (Kind.Dn, Kind.Dn) => 0xC140,
                    (Kind.An, Kind.An) => 0xC148,
                    _ => 0xC188,
                };
                Op(mode | (a.Reg << 9) | b.Reg);
                return words;
            }

            case "ADD" or "SUB" or "AND" or "OR" or "EOR" or "CMP" or "ADDA" or "SUBA" or "CMPA"
                or "ADDI" or "SUBI" or "ANDI" or "ORI" or "EORI" or "CMPI":
                return Arithmetic(words, name, operands, Sz(2), pc);

            case "ADDQ" or "SUBQ":
            {
                var (imm, ea) = Two();
                var sz = Sz(2);
                Op(((imm.Value & 7L) << 9) | (long)(0x5000 | (name == "SUBQ" ? 0x100 : 0) | (SizeCode(sz) << 6) | ea.Ea));
                Ext(ea, sz);
                return words;
            }

            case "BTST" or "BCHG" or "BCLR" or "BSET":
            {
                var (bit, ea) = Two();
                var kind = Array.IndexOf(new[] { "BTST", "BCHG", "BCLR", "BSET" }, name);
                if (bit.Kind == Kind.Dn)
                {
                    Op(0x0100 | (bit.Reg << 9) | (kind << 6) | ea.Ea);
                }
                else
                {
                    Op(0x0800 | (kind << 6) | ea.Ea);
                    words.Add(bit.Value & 0xFF);
                }

                Ext(ea, 1);
                return words;
            }

            case "ASL" or "ASR" or "LSL" or "LSR" or "ROL" or "ROR" or "ROXL" or "ROXR":
            {
                var type = name[..^1] switch { "AS" => 0, "LS" => 1, "ROX" => 2, _ => 3 };
                var left = name[^1] == 'L' ? 0x100 : 0;
                if (operands.Count == 1)
                {
                    var ea = operands[0];
                    Op(0xE0C0 | (type << 9) | left | ea.Ea);
                    Ext(ea, 2);
                    return words;
                }

                var (count, dn) = Two();
                var sz = Sz(2);
                if (count.Kind == Kind.Dn) Op(0xE000 | (count.Reg << 9) | left | (SizeCode(sz) << 6) | 0x20 | (type << 3) | dn.Reg);
                else Op(((count.Value & 7L) << 9) | (long)(0xE000 | left | (SizeCode(sz) << 6) | (type << 3) | dn.Reg));
                return words;
            }
        }

        throw new FormatException($"'{mnemonic}' is not a 68000 instruction this assembler knows");
    }

    private static ushort Reverse(ushort mask)
    {
        var reversed = 0;
        for (var i = 0; i < 16; i++)
            if ((mask & (1 << i)) != 0) reversed |= 1 << (15 - i);
        return (ushort)reversed;
    }

    private static List<long> Move(List<long> words, List<Operand> operands, int size, uint pc)
    {
        if (operands.Count != 2) throw new FormatException("MOVE takes two operands");
        var (source, destination) = (operands[0], operands[1]);

        void Ext(Operand o, int sz) => words.AddRange(Extension(o, sz, pc + (uint)(words.Count * 2)));

        if (destination.Kind == Kind.Ccr)
        {
            words[0] = 0x44C0 | source.Ea;
            Ext(source, 2);
            return words;
        }

        if (destination.Kind == Kind.Sr)
        {
            words[0] = 0x46C0 | source.Ea;
            Ext(source, 2);
            return words;
        }

        if (source.Kind == Kind.Sr)
        {
            words[0] = 0x40C0 | destination.Ea;
            Ext(destination, 2);
            return words;
        }

        if (destination.Kind == Kind.Usp)
        {
            words[0] = 0x4E60 | source.Reg;
            return words;
        }

        if (source.Kind == Kind.Usp)
        {
            words[0] = 0x4E68 | destination.Reg;
            return words;
        }

        var code = size switch { 1 => 1, 2 => 3, _ => 2 };
        words[0] = (code << 12) | (destination.RegField << 9) | (destination.Mode << 6) | source.Ea;
        Ext(source, size);
        Ext(destination, size);
        return words;
    }

    private static List<long> Arithmetic(List<long> words, string name, List<Operand> operands, int size, uint pc)
    {
        if (operands.Count != 2) throw new FormatException($"{name} takes two operands");
        var (source, destination) = (operands[0], operands[1]);
        var root = name.TrimEnd('A', 'I');
        if (name == "ANDI") root = "AND";
        if (name == "ORI") root = "OR";
        if (name == "EORI") root = "EOR";
        if (name == "CMPA" || name == "CMPI") root = "CMP";

        void Ext(Operand o, int sz) => words.AddRange(Extension(o, sz, pc + (uint)(words.Count * 2)));

        // To CCR and SR.
        if (destination.Kind is Kind.Ccr or Kind.Sr)
        {
            var basis = root switch { "AND" => 0x0200, "OR" => 0x0000, "EOR" => 0x0A00, _ => throw new FormatException($"{name} cannot write {destination.Kind}") };
            words[0] = basis | (destination.Kind == Kind.Ccr ? 0x3C : 0x7C);
            words.Add(source.Value & (destination.Kind == Kind.Ccr ? 0xFF : 0xFFFF));
            return words;
        }

        // The A forms: ADDA, SUBA, CMPA.
        if (destination.Kind == Kind.An)
        {
            var basis = root switch { "ADD" => 0xD000, "SUB" => 0x9000, "CMP" => 0xB000, _ => throw new FormatException($"{name} to an address register") };
            words[0] = basis | (destination.Reg << 9) | (size == 4 ? 0x1C0 : 0x0C0) | source.Ea;
            Ext(source, size);
            return words;
        }

        // The immediate forms.
        if (source.Kind == Kind.Imm)
        {
            var basis = root switch { "OR" => 0x0000, "AND" => 0x0200, "SUB" => 0x0400, "ADD" => 0x0600, "EOR" => 0x0A00, _ => 0x0C00 };
            words[0] = basis | (SizeCode(size) << 6) | destination.Ea;
            Ext(source, size);
            Ext(destination, size);
            return words;
        }

        var opcode = root switch { "ADD" => 0xD000, "SUB" => 0x9000, "AND" => 0xC000, "OR" => 0x8000, "EOR" => 0xB000, _ => 0xB000 };
        if (root == "EOR")
        {
            words[0] = 0xB100 | (source.Reg << 9) | (SizeCode(size) << 6) | destination.Ea;
            Ext(destination, size);
            return words;
        }

        if (destination.Kind == Kind.Dn)
        {
            words[0] = opcode | (destination.Reg << 9) | (SizeCode(size) << 6) | source.Ea;
            Ext(source, size);
            return words;
        }

        if (root == "CMP") throw new FormatException("CMP needs a register destination");
        words[0] = opcode | 0x100 | (source.Reg << 9) | (SizeCode(size) << 6) | destination.Ea;
        Ext(destination, size);
        return words;
    }

    // ---- expressions ----------------------------------------------------------------------

    /// <summary>The value of an expression; null on the first pass when it needs a label not yet defined.</summary>
    private static long? Evaluate(string text, Dictionary<string, long> labels, int pass, uint pc)
    {
        var position = 0;
        var unknown = false;
        var value = Expression(text, ref position, labels, ref unknown, pc, 0);
        while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
        if (position != text.Length) throw new FormatException($"cannot read '{text}'");
        if (unknown)
        {
            if (pass == 1) throw new FormatException($"unknown label in '{text}'");
            return null;
        }

        return value;
    }

    private static readonly string[][] Precedence = [["|"], ["&"], ["<<", ">>"], ["+", "-"], ["*", "/"]];

    private static long Expression(string text, ref int position, Dictionary<string, long> labels, ref bool unknown, uint pc, int level)
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

    private static long Unary(string text, ref int position, Dictionary<string, long> labels, ref bool unknown, uint pc)
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
