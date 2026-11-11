using System.Globalization;
using System.Text.RegularExpressions;

namespace Cirq.Components.Tests;

/// <summary>
/// A small two-pass AVR assembler for tests: the instructions avr-gcc's output uses, labels, <c>.org</c> (a word
/// address), <c>.equ NAME = value</c>, <c>lo8()</c> and <c>hi8()</c>. Branch and jump targets are labels or word
/// addresses. Writes little-endian words, as a .hex file holds them.
/// </summary>
public static partial class AssemblerAvr
{
    [GeneratedRegex(@"^\s*([A-Za-z_][\w]*)\s*:")]
    private static partial Regex LabelPattern();

    public static byte[] Assemble(string source)
    {
        var lines = source.Replace("\r", "", StringComparison.Ordinal).Split('\n')
            .Select(l => { var c = l.IndexOf(';', StringComparison.Ordinal); return (c >= 0 ? l[..c] : l).Trim(); }).ToList();
        var labels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var equ = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var words = new List<ushort>();

        for (var pass = 0; pass < 2; pass++)
        {
            words.Clear();
            foreach (var raw in lines)
            {
                var line = raw;
                while (LabelPattern().Match(line) is { Success: true } m)
                {
                    if (pass == 0) labels[m.Groups[1].Value] = words.Count;
                    line = line[m.Length..].Trim();
                }

                if (line.Length == 0) continue;
                var space = line.IndexOfAny([' ', '\t']);
                var op = (space < 0 ? line : line[..space]).ToLowerInvariant();
                var args = space < 0 ? [] : line[(space + 1)..].Split(',').Select(a => a.Trim()).ToArray();

                if (op == ".org")
                {
                    var at = Value(args[0], labels, equ, pass);
                    while (words.Count < at) words.Add(0);
                    continue;
                }

                if (op == ".equ")
                {
                    var parts = line[(space + 1)..].Split('=');
                    equ[parts[0].Trim()] = Value(parts[1].Trim(), labels, equ, pass);
                    continue;
                }

                if (op == ".dw")
                {
                    foreach (var a in args) words.Add((ushort)Value(a, labels, equ, pass));
                    continue;
                }

                words.AddRange(Encode(op, args, words.Count, labels, equ, pass));
            }
        }

        var bytes = new byte[words.Count * 2];
        for (var i = 0; i < words.Count; i++)
        {
            bytes[i * 2] = (byte)words[i];
            bytes[(i * 2) + 1] = (byte)(words[i] >> 8);
        }

        return bytes;
    }

    private static int Value(string text, Dictionary<string, int> labels, Dictionary<string, int> equ, int pass)
    {
        text = text.Trim();
        if (text.StartsWith("lo8(", StringComparison.OrdinalIgnoreCase)) return Value(text[4..^1], labels, equ, pass) & 0xFF;
        if (text.StartsWith("hi8(", StringComparison.OrdinalIgnoreCase)) return (Value(text[4..^1], labels, equ, pass) >> 8) & 0xFF;
        if (text.StartsWith('-')) return -Value(text[1..], labels, equ, pass);
        if (text.Length == 3 && text[0] == '\'' && text[2] == '\'') return text[1];
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return int.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (text.StartsWith('$')) return int.Parse(text[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) return Convert.ToInt32(text[2..], 2);
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
        if (equ.TryGetValue(text, out var e)) return e;
        if (labels.TryGetValue(text, out var l)) return l;
        if (pass == 0) return 0;
        throw new FormatException($"Unknown symbol '{text}'.");
    }

    private static int R(string text)
    {
        if (text.Length < 2 || char.ToLowerInvariant(text[0]) != 'r') throw new FormatException($"'{text}' isn't a register.");
        return int.Parse(text[1..], CultureInfo.InvariantCulture);
    }

    private static IEnumerable<ushort> Encode(string op, string[] a, int pc, Dictionary<string, int> labels, Dictionary<string, int> equ, int pass)
    {
        int Val(int i) => Value(a[i], labels, equ, pass);
        ushort Two(int code, int d, int r) => (ushort)(code | ((r & 0x10) << 5) | ((d & 0x1F) << 4) | (r & 0x0F));
        ushort Imm(int code, int d, int k) => (ushort)(code | ((k & 0xF0) << 4) | (((d - 16) & 0xF) << 4) | (k & 0x0F));
        ushort One(int code, int d) => (ushort)(code | ((d & 0x1F) << 4));
        int Rel(int target, int bits) => ((target - (pc + 1)) & ((1 << bits) - 1));

        switch (op)
        {
            case "nop": return [0x0000];
            case "add": return [Two(0x0C00, R(a[0]), R(a[1]))];
            case "adc": return [Two(0x1C00, R(a[0]), R(a[1]))];
            case "sub": return [Two(0x1800, R(a[0]), R(a[1]))];
            case "sbc": return [Two(0x0800, R(a[0]), R(a[1]))];
            case "and": return [Two(0x2000, R(a[0]), R(a[1]))];
            case "or": return [Two(0x2800, R(a[0]), R(a[1]))];
            case "eor": return [Two(0x2400, R(a[0]), R(a[1]))];
            case "mov": return [Two(0x2C00, R(a[0]), R(a[1]))];
            case "cp": return [Two(0x1400, R(a[0]), R(a[1]))];
            case "cpc": return [Two(0x0400, R(a[0]), R(a[1]))];
            case "cpse": return [Two(0x1000, R(a[0]), R(a[1]))];
            case "mul": return [Two(0x9C00, R(a[0]), R(a[1]))];
            case "lsl": return [Two(0x0C00, R(a[0]), R(a[0]))];
            case "rol": return [Two(0x1C00, R(a[0]), R(a[0]))];
            case "tst": return [Two(0x2000, R(a[0]), R(a[0]))];
            case "clr": return [Two(0x2400, R(a[0]), R(a[0]))];
            case "movw": return [(ushort)(0x0100 | ((R(a[0]) / 2) << 4) | (R(a[1]) / 2))];
            case "muls": return [(ushort)(0x0200 | ((R(a[0]) - 16) << 4) | (R(a[1]) - 16))];
            case "ldi": return [Imm(0xE000, R(a[0]), Val(1))];
            case "ser": return [Imm(0xE000, R(a[0]), 0xFF)];
            case "cpi": return [Imm(0x3000, R(a[0]), Val(1))];
            case "subi": return [Imm(0x5000, R(a[0]), Val(1))];
            case "sbci": return [Imm(0x4000, R(a[0]), Val(1))];
            case "ori" or "sbr": return [Imm(0x6000, R(a[0]), Val(1))];
            case "andi": return [Imm(0x7000, R(a[0]), Val(1))];
            case "cbr": return [Imm(0x7000, R(a[0]), ~Val(1) & 0xFF)];
            case "com": return [One(0x9400, R(a[0]))];
            case "neg": return [One(0x9401, R(a[0]))];
            case "swap": return [One(0x9402, R(a[0]))];
            case "inc": return [One(0x9403, R(a[0]))];
            case "asr": return [One(0x9405, R(a[0]))];
            case "lsr": return [One(0x9406, R(a[0]))];
            case "ror": return [One(0x9407, R(a[0]))];
            case "dec": return [One(0x940A, R(a[0]))];
            case "push": return [One(0x920F, R(a[0]))];
            case "pop": return [One(0x900F, R(a[0]))];
            case "adiw" or "sbiw":
            {
                var k = Val(1);
                return [(ushort)((op == "adiw" ? 0x9600 : 0x9700) | ((k & 0x30) << 2) | (((R(a[0]) - 24) / 2) << 4) | (k & 0x0F))];
            }

            case "in": { var p = Val(1); return [(ushort)(0xB000 | ((p & 0x30) << 5) | ((R(a[0]) & 0x1F) << 4) | (p & 0x0F))]; }
            case "out": { var p = Val(0); return [(ushort)(0xB800 | ((p & 0x30) << 5) | ((R(a[1]) & 0x1F) << 4) | (p & 0x0F))]; }
            case "sbi": return [(ushort)(0x9A00 | (Val(0) << 3) | Val(1))];
            case "cbi": return [(ushort)(0x9800 | (Val(0) << 3) | Val(1))];
            case "sbis": return [(ushort)(0x9B00 | (Val(0) << 3) | Val(1))];
            case "sbic": return [(ushort)(0x9900 | (Val(0) << 3) | Val(1))];
            case "sbrs": return [(ushort)(0xFE00 | (R(a[0]) << 4) | Val(1))];
            case "sbrc": return [(ushort)(0xFC00 | (R(a[0]) << 4) | Val(1))];
            case "bst": return [(ushort)(0xFA00 | (R(a[0]) << 4) | Val(1))];
            case "bld": return [(ushort)(0xF800 | (R(a[0]) << 4) | Val(1))];
            case "rjmp": return [(ushort)(0xC000 | Rel(Val(0), 12))];
            case "rcall": return [(ushort)(0xD000 | Rel(Val(0), 12))];
            case "jmp" or "call":
            {
                var k = Val(0);
                return [(ushort)((op == "jmp" ? 0x940C : 0x940E) | ((k >> 13) & 0x1F0) | ((k >> 16) & 1)), (ushort)k];
            }

            case "ijmp": return [0x9409];
            case "icall": return [0x9509];
            case "ret": return [0x9508];
            case "reti": return [0x9518];
            case "sleep": return [0x9588];
            case "lpm" when a.Length == 0: return [0x95C8];
            case "lpm": return [One(a[1].EndsWith('+') ? 0x9005 : 0x9004, R(a[0]))];
            case "sei": return [0x9478];
            case "cli": return [0x94F8];
            case "sec": return [0x9408];
            case "clc": return [0x9488];
            case "set": return [0x9468];
            case "clt": return [0x94E8];
            case "lds": return [One(0x9000, R(a[0])), (ushort)Val(1)];
            case "sts": return [One(0x9200, R(a[1])), (ushort)Val(0)];
            case "ld": return [Pointer(R(a[0]), a[1], store: false)];
            case "st": return [Pointer(R(a[1]), a[0], store: true)];
            case "ldd": return [Displaced(R(a[0]), a[1], store: false)];
            case "std": return [Displaced(R(a[1]), a[0], store: true)];
        }

        var branches = new Dictionary<string, (bool Set, int Bit)>
        {
            ["breq"] = (true, 1), ["brne"] = (false, 1), ["brcs"] = (true, 0), ["brlo"] = (true, 0), ["brcc"] = (false, 0), ["brsh"] = (false, 0),
            ["brmi"] = (true, 2), ["brpl"] = (false, 2), ["brvs"] = (true, 3), ["brvc"] = (false, 3), ["brlt"] = (true, 4), ["brge"] = (false, 4),
            ["brhs"] = (true, 5), ["brhc"] = (false, 5), ["brts"] = (true, 6), ["brtc"] = (false, 6), ["brie"] = (true, 7), ["brid"] = (false, 7),
        };
        if (branches.TryGetValue(op, out var branch))
            return [(ushort)((branch.Set ? 0xF000 : 0xF400) | (Rel(Val(0), 7) << 3) | branch.Bit)];

        throw new FormatException($"Unknown instruction '{op}'.");
    }

    private static ushort Pointer(int reg, string pointer, bool store)
    {
        var p = pointer.ToUpperInvariant();
        var low = p switch
        {
            "X" => 0xC, "X+" => 0xD, "-X" => 0xE,
            "Y" => 0x8, "Y+" => 0x9, "-Y" => 0xA,
            "Z" => 0x0, "Z+" => 0x1, "-Z" => 0x2,
            _ => throw new FormatException($"'{pointer}' isn't a pointer."),
        };
        var plain = p is "Y" or "Z";
        var code = plain ? (store ? 0x8200 : 0x8000) : (store ? 0x9200 : 0x9000);
        return (ushort)(code | ((reg & 0x1F) << 4) | low);
    }

    private static ushort Displaced(int reg, string pointer, bool store)
    {
        var y = pointer.StartsWith('Y') || pointer.StartsWith('y');
        var q = int.Parse(pointer[2..], CultureInfo.InvariantCulture);
        return (ushort)(0x8000 | ((q & 0x20) << 8) | ((q & 0x18) << 7) | (store ? 0x0200 : 0) | ((reg & 0x1F) << 4) | (y ? 0x08 : 0) | (q & 0x7));
    }
}
