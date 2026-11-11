using System.Globalization;

namespace Cirq.Components.Tests;

/// <summary>
/// A two-pass MCS-51 assembler for the tests and the guide's examples, in the usual syntax: labels ending in a colon,
/// ORG, EQU (<c>NAME EQU value</c>), DB and DW, ; comments, numbers in decimal, 0x, $ or a trailing H, and the SFR and bit
/// names (ACC, B, PSW, SP, DPL, DPH, P1, P3, TCON, TMOD, TL0-TH1, SCON, SBUF, IE, IP, PCON; TR0, TF1, RI, TI, EA, ES and
/// the rest; P1.0 and ACC.7 for a bit of a register).
/// </summary>
public static class Assembler8051
{
    private static readonly Dictionary<string, int> Sfr = new(StringComparer.OrdinalIgnoreCase)
    {
        ["P0"] = 0x80, ["SP"] = 0x81, ["DPL"] = 0x82, ["DPH"] = 0x83, ["PCON"] = 0x87, ["TCON"] = 0x88, ["TMOD"] = 0x89, ["TL0"] = 0x8A,
        ["TL1"] = 0x8B, ["TH0"] = 0x8C, ["TH1"] = 0x8D, ["P1"] = 0x90, ["SCON"] = 0x98, ["SBUF"] = 0x99, ["P2"] = 0xA0, ["IE"] = 0xA8,
        ["P3"] = 0xB0, ["IP"] = 0xB8, ["PSW"] = 0xD0, ["ACC"] = 0xE0, ["B"] = 0xF0,
    };

    private static readonly Dictionary<string, int> Bits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IT0"] = 0x88, ["IE0"] = 0x89, ["IT1"] = 0x8A, ["IE1"] = 0x8B, ["TR0"] = 0x8C, ["TF0"] = 0x8D, ["TR1"] = 0x8E, ["TF1"] = 0x8F,
        ["RI"] = 0x98, ["TI"] = 0x99, ["RB8"] = 0x9A, ["TB8"] = 0x9B, ["REN"] = 0x9C, ["SM2"] = 0x9D, ["SM1"] = 0x9E, ["SM0"] = 0x9F,
        ["EX0"] = 0xA8, ["ET0"] = 0xA9, ["EX1"] = 0xAA, ["ET1"] = 0xAB, ["ES"] = 0xAC, ["EA"] = 0xAF,
        ["PX0"] = 0xB8, ["PT0"] = 0xB9, ["PX1"] = 0xBA, ["PT1"] = 0xBB, ["PS"] = 0xBC,
        ["P"] = 0xD0, ["OV"] = 0xD2, ["RS0"] = 0xD3, ["RS1"] = 0xD4, ["F0"] = 0xD5, ["AC"] = 0xD6, ["CY"] = 0xD7,
    };

    /// <summary>Assembles <paramref name="source"/>; the image starts at 0, gaps filled with 0xFF.</summary>
    public static byte[] Assemble(string source)
    {
        var lines = source.Split('\n').Select(l => { var c = l.IndexOf(';', StringComparison.Ordinal); return (c >= 0 ? l[..c] : l).Trim(); }).ToList();
        var labels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var equ = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var image = new SortedDictionary<int, byte>();
        for (var pass = 0; pass < 2; pass++)
        {
            var pc = 0;
            image.Clear();
            foreach (var raw in lines)
            {
                var line = raw;
                while (line.Length > 0)
                {
                    var colon = line.IndexOf(':', StringComparison.Ordinal);
                    if (colon <= 0 || line[..colon].Contains(' ', StringComparison.Ordinal) || line[..colon].Contains('\'', StringComparison.Ordinal)) break;
                    labels[line[..colon].Trim()] = pc;
                    line = line[(colon + 1)..].Trim();
                }

                if (line.Length == 0) continue;
                var parts = line.Split((char[])[' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[1].TrimStart().StartsWith("EQU", StringComparison.OrdinalIgnoreCase))
                {
                    equ[parts[0]] = Value(parts[1].Trim()[3..], labels, equ, pass);
                    continue;
                }

                var mnemonic = parts[0].ToUpperInvariant();
                var args = parts.Length > 1 ? Split(parts[1]) : [];
                switch (mnemonic)
                {
                    case "ORG": pc = Value(args[0], labels, equ, pass); continue;
                    case "DB":
                        foreach (var a in args)
                        {
                            if (a.StartsWith('"') && a.EndsWith('"') && a.Length >= 2) foreach (var ch in a[1..^1]) image[pc++] = (byte)ch;
                            else image[pc++] = (byte)Value(a, labels, equ, pass);
                        }

                        continue;
                    case "DW":
                        foreach (var a in args)
                        {
                            var v = Value(a, labels, equ, pass);
                            image[pc++] = (byte)(v >> 8);
                            image[pc++] = (byte)v;
                        }

                        continue;
                }

                var bytes = Encode(mnemonic, args, pc, labels, equ, pass);
                foreach (var b in bytes) image[pc++] = b;
            }
        }

        var size = image.Count == 0 ? 0 : image.Keys.Max() + 1;
        var output = Enumerable.Repeat((byte)0xFF, size).ToArray();
        foreach (var (address, b) in image) output[address] = b;
        return output;
    }

    private static List<string> Split(string text)
    {
        var list = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in text)
        {
            if (ch is '"' or '\'') quoted = !quoted;
            if (ch == ',' && !quoted)
            {
                list.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0) list.Add(current.ToString().Trim());
        return list;
    }

    private static int Value(string text, Dictionary<string, int> labels, Dictionary<string, int> equ, int pass)
    {
        text = text.Trim();
        foreach (var op in new[] { '+', '-' })
        {
            var at = text.LastIndexOf(op);
            if (at > 0) return op == '+' ? Value(text[..at], labels, equ, pass) + Value(text[(at + 1)..], labels, equ, pass)
                : Value(text[..at], labels, equ, pass) - Value(text[(at + 1)..], labels, equ, pass);
        }

        if (text.StartsWith('#')) text = text[1..];
        if (text.StartsWith("LOW(", StringComparison.OrdinalIgnoreCase)) return Value(text[4..^1], labels, equ, pass) & 0xFF;
        if (text.StartsWith("HIGH(", StringComparison.OrdinalIgnoreCase)) return (Value(text[5..^1], labels, equ, pass) >> 8) & 0xFF;
        if (text.Length == 3 && text[0] == '\'' && text[2] == '\'') return text[1];
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return int.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (text.StartsWith('$')) return int.Parse(text[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (text.EndsWith('H') || text.EndsWith('h'))
            if (int.TryParse(text[..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) && char.IsAsciiDigit(text[0])) return hex;
        if (text.EndsWith('B') && text[..^1].All(c => c is '0' or '1') && text.Length > 1) return Convert.ToInt32(text[..^1], 2);
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
        if (equ.TryGetValue(text, out var e)) return e;
        if (labels.TryGetValue(text, out var l)) return l;
        if (Sfr.TryGetValue(text, out var s)) return s;
        if (Bits.TryGetValue(text, out var bit)) return bit;
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0 && int.TryParse(text[(dot + 1)..], out var b))
        {
            var reg = Sfr.TryGetValue(text[..dot], out var sfr) ? sfr : Value(text[..dot], labels, equ, pass);
            return reg >= 0x80 ? reg + b : ((reg - 0x20) * 8) + b;
        }
        if (pass == 0) return 0;
        throw new FormatException($"Unknown symbol '{text}'.");
    }

    private static int? Reg(string a) => a.Length == 2 && (a[0] is 'R' or 'r') && a[1] is >= '0' and <= '7' ? a[1] - '0' : null;

    private static int? Indirect(string a) => a.Equals("@R0", StringComparison.OrdinalIgnoreCase) ? 0 : a.Equals("@R1", StringComparison.OrdinalIgnoreCase) ? 1 : null;

    private static byte[] Encode(string m, List<string> a, int pc, Dictionary<string, int> labels, Dictionary<string, int> equ, int pass)
    {
        int V(int i) => Value(a[i], labels, equ, pass);
        bool Is(int i, string s) => a.Count > i && a[i].Equals(s, StringComparison.OrdinalIgnoreCase);
        bool Imm(int i) => a.Count > i && a[i].StartsWith('#');
        byte Rel(int i, int length) => (byte)(V(i) - (pc + length));

        // The ALU row: op A,#d / A,dir / A,@Ri / A,Rn at base+4, +5, +6/7, +8-F.
        byte[]? Row(int @base)
        {
            if (!Is(0, "A")) return null;
            if (Imm(1)) return [(byte)(@base + 4), (byte)V(1)];
            if (Indirect(a[1]) is { } ri) return [(byte)(@base + 6 + ri)];
            if (Reg(a[1]) is { } rn) return [(byte)(@base + 8 + rn)];
            return [(byte)(@base + 5), (byte)V(1)];
        }

        switch (m)
        {
            case "NOP": return [0x00];
            case "RET": return [0x22];
            case "RETI": return [0x32];
            case "AJMP": { var t = V(0); return [(byte)(0x01 | ((t >> 3) & 0xE0)), (byte)t]; }
            case "ACALL": { var t = V(0); return [(byte)(0x11 | ((t >> 3) & 0xE0)), (byte)t]; }
            case "LJMP": { var t = V(0); return [0x02, (byte)(t >> 8), (byte)t]; }
            case "LCALL" or "CALL": { var t = V(0); return [0x12, (byte)(t >> 8), (byte)t]; }
            case "SJMP": return [0x80, Rel(0, 2)];
            case "JMP" when Is(0, "@A+DPTR"): return [0x73];
            case "JMP": { var t = V(0); return [0x02, (byte)(t >> 8), (byte)t]; }
            case "JC": return [0x40, Rel(0, 2)];
            case "JNC": return [0x50, Rel(0, 2)];
            case "JZ": return [0x60, Rel(0, 2)];
            case "JNZ": return [0x70, Rel(0, 2)];
            case "JB": return [0x20, (byte)V(0), Rel(1, 3)];
            case "JNB": return [0x30, (byte)V(0), Rel(1, 3)];
            case "JBC": return [0x10, (byte)V(0), Rel(1, 3)];
            case "RR": return [0x03];
            case "RRC": return [0x13];
            case "RL": return [0x23];
            case "RLC": return [0x33];
            case "SWAP": return [0xC4];
            case "DA": return [0xD4];
            case "MUL": return [0xA4];
            case "DIV": return [0x84];
            case "INC" when Is(0, "A"): return [0x04];
            case "INC" when Is(0, "DPTR"): return [0xA3];
            case "INC" when Indirect(a[0]) is { } ri: return [(byte)(0x06 + ri)];
            case "INC" when Reg(a[0]) is { } rn: return [(byte)(0x08 + rn)];
            case "INC": return [0x05, (byte)V(0)];
            case "DEC" when Is(0, "A"): return [0x14];
            case "DEC" when Indirect(a[0]) is { } ri: return [(byte)(0x16 + ri)];
            case "DEC" when Reg(a[0]) is { } rn: return [(byte)(0x18 + rn)];
            case "DEC": return [0x15, (byte)V(0)];
            case "ADD": return Row(0x20)!;
            case "ADDC": return Row(0x30)!;
            case "SUBB": return Row(0x90)!;
            case "ORL" or "ANL" or "XRL":
            {
                var @base = m switch { "ORL" => 0x40, "ANL" => 0x50, _ => 0x60 };
                if (Is(0, "C")) return a[1].StartsWith('/') ? [(byte)(m == "ORL" ? 0xA0 : 0xB0), (byte)Value(a[1][1..], labels, equ, pass)] : [(byte)(m == "ORL" ? 0x72 : 0x82), (byte)V(1)];
                if (Is(0, "A")) return Row(@base)!;
                return Is(1, "A") ? [(byte)(@base + 2), (byte)V(0)] : [(byte)(@base + 3), (byte)V(0), (byte)V(1)];
            }

            case "CLR" when Is(0, "A"): return [0xE4];
            case "CLR" when Is(0, "C"): return [0xC3];
            case "CLR": return [0xC2, (byte)V(0)];
            case "SETB" when Is(0, "C"): return [0xD3];
            case "SETB": return [0xD2, (byte)V(0)];
            case "CPL" when Is(0, "A"): return [0xF4];
            case "CPL" when Is(0, "C"): return [0xB3];
            case "CPL": return [0xB2, (byte)V(0)];
            case "PUSH": return [0xC0, (byte)V(0)];
            case "POP": return [0xD0, (byte)V(0)];
            case "XCH" when Indirect(a[1]) is { } ri: return [(byte)(0xC6 + ri)];
            case "XCH" when Reg(a[1]) is { } rn: return [(byte)(0xC8 + rn)];
            case "XCH": return [0xC5, (byte)V(1)];
            case "XCHD": return [(byte)(0xD6 + Indirect(a[1])!.Value)];
            case "DJNZ" when Reg(a[0]) is { } rn: return [(byte)(0xD8 + rn), Rel(1, 2)];
            case "DJNZ": return [0xD5, (byte)V(0), Rel(1, 3)];
            case "CJNE" when Is(0, "A") && Imm(1): return [0xB4, (byte)V(1), Rel(2, 3)];
            case "CJNE" when Is(0, "A"): return [0xB5, (byte)V(1), Rel(2, 3)];
            case "CJNE" when Indirect(a[0]) is { } ri: return [(byte)(0xB6 + ri), (byte)V(1), Rel(2, 3)];
            case "CJNE": return [(byte)(0xB8 + Reg(a[0])!.Value), (byte)V(1), Rel(2, 3)];
            case "MOVC": return Is(1, "@A+DPTR") ? [0x93] : [0x83];
            case "MOVX" when Is(0, "A"): return Is(1, "@DPTR") ? [0xE0] : [(byte)(0xE2 + Indirect(a[1])!.Value)];
            case "MOVX": return Is(0, "@DPTR") ? [0xF0] : [(byte)(0xF2 + Indirect(a[0])!.Value)];
            case "MOV": return Mov(a, V, Is, Imm, labels, equ, pass);
        }

        throw new FormatException($"Unknown instruction '{m} {string.Join(", ", a)}'.");
    }

    private static byte[] Mov(List<string> a, Func<int, int> V, Func<int, string, bool> Is, Func<int, bool> Imm, Dictionary<string, int> labels, Dictionary<string, int> equ, int pass)
    {
        if (Is(0, "DPTR")) { var v = V(1); return [0x90, (byte)(v >> 8), (byte)v]; }
        if (Is(0, "C")) return [0xA2, (byte)V(1)];
        if (Is(1, "C")) return [0x92, (byte)V(0)];
        if (Is(0, "A"))
        {
            if (Imm(1)) return [0x74, (byte)V(1)];
            if (Indirect(a[1]) is { } ri) return [(byte)(0xE6 + ri)];
            if (Reg(a[1]) is { } rn) return [(byte)(0xE8 + rn)];
            return [0xE5, (byte)V(1)];
        }

        if (Indirect(a[0]) is { } di)
        {
            if (Is(1, "A")) return [(byte)(0xF6 + di)];
            if (Imm(1)) return [(byte)(0x76 + di), (byte)V(1)];
            return [(byte)(0xA6 + di), (byte)V(1)];
        }

        if (Reg(a[0]) is { } dn)
        {
            if (Is(1, "A")) return [(byte)(0xF8 + dn)];
            if (Imm(1)) return [(byte)(0x78 + dn), (byte)V(1)];
            return [(byte)(0xA8 + dn), (byte)V(1)];
        }

        // A direct destination.
        if (Is(1, "A")) return [0xF5, (byte)V(0)];
        if (Imm(1)) return [0x75, (byte)V(0), (byte)V(1)];
        if (Indirect(a[1]) is { } si) return [(byte)(0x86 + si), (byte)V(0)];
        if (Reg(a[1]) is { } sn) return [(byte)(0x88 + sn), (byte)V(0)];
        return [0x85, (byte)V(1), (byte)V(0)];
    }
}
