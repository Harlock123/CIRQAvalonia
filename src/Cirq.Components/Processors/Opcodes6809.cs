using System.Globalization;

namespace Cirq.Components.Processors;

/// <summary>How a 6809 instruction finds its operand.</summary>
public enum M6809Mode
{
    /// <summary>No operand, or a register named by the opcode.</summary>
    Inherent,

    /// <summary>#n: an 8-bit operand after the opcode.</summary>
    Immediate8,

    /// <summary>#nn: a 16-bit operand after the opcode.</summary>
    Immediate16,

    /// <summary>&lt;n: the low byte of an address whose high byte is DP.</summary>
    Direct,

    /// <summary>nn: a 16-bit address.</summary>
    Extended,

    /// <summary>A postbyte, and up to two bytes more: offsets from X, Y, U, S or PC, auto increment and decrement, indirection.</summary>
    Indexed,

    /// <summary>A branch with an 8-bit offset.</summary>
    Relative8,

    /// <summary>A branch with a 16-bit offset.</summary>
    Relative16,

    /// <summary>TFR and EXG: a postbyte naming two registers.</summary>
    RegisterPair,

    /// <summary>PSHS, PULS, PSHU and PULU: a postbyte with a bit for each register.</summary>
    RegisterList,

    /// <summary>$10 or $11: the opcode is the next byte, on page 2 or 3.</summary>
    Prefix,
}

/// <summary>
/// What each 6809 opcode is, on its three pages — page 1, page 2 after $10 and page 3 after $11:
/// the mnemonic and the addressing mode. The disassembler prints from this table; the core decodes
/// the same opcodes by their columns, as the chip does, and a test checks the two agree on every
/// opcode's length.
/// <para>
/// The opcodes Motorola left out are marked: the ones the silicon is reported to run as a neighbour
/// (NEG, LSR and DEC at $01, $05 and $0B and their columns, $02 as NEG or COM by the carry, CLRA and
/// CLRB at $4E and $5E, the store-immediates at $87, $8F, $C7 and $CF, NOP at $1B, a four-cycle
/// ANDCC at $38, the reset vector taken as an interrupt at $3E, the test mode at $14, $15 and $CD)
/// carry those names with an asterisk; a page 2 or 3 opcode with nothing assigned runs as the page 1
/// one and prints as such.
/// </para>
/// </summary>
public static class Opcodes6809
{
    /// <summary>One opcode: its mnemonic, its addressing mode, and whether Motorola documented it.</summary>
    public readonly record struct Entry(string Mnemonic, M6809Mode Mode, bool Undocumented = false);

    private static readonly string[] Rmw = ["NEG", "NEG", "XNC", "COM", "LSR", "LSR", "ROR", "ASR", "ASL", "ROL", "DEC", "DEC", "INC", "TST", "JMP", "CLR"];

    private static readonly string[] AluA = ["SUBA", "CMPA", "SBCA", "SUBD", "ANDA", "BITA", "LDA", "STA", "EORA", "ADCA", "ORA", "ADDA", "CMPX", "JSR", "LDX", "STX"];

    private static readonly string[] AluB = ["SUBB", "CMPB", "SBCB", "ADDD", "ANDB", "BITB", "LDB", "STB", "EORB", "ADCB", "ORB", "ADDB", "LDD", "STD", "LDU", "STU"];

    private static readonly string[] Branches = ["BRA", "BRN", "BHI", "BLS", "BCC", "BCS", "BNE", "BEQ", "BVC", "BVS", "BPL", "BMI", "BGE", "BLT", "BGT", "BLE"];

    /// <summary>The register names TFR and EXG use, by their four-bit code; the unused codes print as ?.</summary>
    public static readonly string[] RegisterNames = ["D", "X", "Y", "U", "S", "PC", "?", "?", "A", "B", "CC", "DP", "?", "?", "?", "?"];

    private static readonly Entry[] Page1 = BuildPage1();
    private static readonly Entry[] Page2 = BuildPage2();
    private static readonly Entry[] Page3 = BuildPage3();

    /// <summary>The entry for an opcode on page 1 (prefix 0), 2 (prefix $10) or 3 (prefix $11).</summary>
    public static Entry Get(int prefix, byte opcode) => prefix switch
    {
        0x10 => Page2[opcode],
        0x11 => Page3[opcode],
        _ => Page1[opcode],
    };

    private static Entry[] BuildPage1()
    {
        var t = new Entry[256];
        for (var lo = 0; lo < 16; lo++)
        {
            var undocumented = lo is 1 or 2 or 5 or 11;
            t[0x00 + lo] = new(Rmw[lo], M6809Mode.Direct, undocumented);
            t[0x40 + lo] = new(lo == 14 ? "CLRA" : Rmw[lo] + "A", M6809Mode.Inherent, undocumented || lo == 14);
            t[0x50 + lo] = new(lo == 14 ? "CLRB" : Rmw[lo] + "B", M6809Mode.Inherent, undocumented || lo == 14);
            t[0x60 + lo] = new(Rmw[lo], M6809Mode.Indexed, undocumented);
            t[0x70 + lo] = new(Rmw[lo], M6809Mode.Extended, undocumented);
            t[0x20 + lo] = new(Branches[lo], M6809Mode.Relative8);

            for (var row = 0; row < 4; row++)
            {
                var mode = row switch { 0 => M6809Mode.Immediate8, 1 => M6809Mode.Direct, 2 => M6809Mode.Indexed, _ => M6809Mode.Extended };
                var wide = lo is 3 or 12 or 14 or 15;
                var aMode = row == 0 && wide ? M6809Mode.Immediate16 : mode;
                t[0x80 + (row << 4) + lo] = new(AluA[lo], aMode);
                t[0xC0 + (row << 4) + lo] = new(AluB[lo], aMode);
            }
        }

        // The immediate column's odd ones out: BSR where JSR would be, and the stores.
        t[0x8D] = new("BSR", M6809Mode.Relative8);
        t[0x87] = new("STA", M6809Mode.Immediate8, true);
        t[0xC7] = new("STB", M6809Mode.Immediate8, true);
        t[0x8F] = new("STX", M6809Mode.Immediate16, true);
        t[0xCF] = new("STU", M6809Mode.Immediate16, true);
        t[0xCD] = new("HCF", M6809Mode.Inherent, true);

        t[0x10] = new("PAGE2", M6809Mode.Prefix);
        t[0x11] = new("PAGE3", M6809Mode.Prefix);
        t[0x12] = new("NOP", M6809Mode.Inherent);
        t[0x13] = new("SYNC", M6809Mode.Inherent);
        t[0x14] = new("HCF", M6809Mode.Inherent, true);
        t[0x15] = new("HCF", M6809Mode.Inherent, true);
        t[0x16] = new("LBRA", M6809Mode.Relative16);
        t[0x17] = new("LBSR", M6809Mode.Relative16);
        t[0x18] = new("X18", M6809Mode.Inherent, true);
        t[0x19] = new("DAA", M6809Mode.Inherent);
        t[0x1A] = new("ORCC", M6809Mode.Immediate8);
        t[0x1B] = new("NOP", M6809Mode.Inherent, true);
        t[0x1C] = new("ANDCC", M6809Mode.Immediate8);
        t[0x1D] = new("SEX", M6809Mode.Inherent);
        t[0x1E] = new("EXG", M6809Mode.RegisterPair);
        t[0x1F] = new("TFR", M6809Mode.RegisterPair);

        t[0x30] = new("LEAX", M6809Mode.Indexed);
        t[0x31] = new("LEAY", M6809Mode.Indexed);
        t[0x32] = new("LEAS", M6809Mode.Indexed);
        t[0x33] = new("LEAU", M6809Mode.Indexed);
        t[0x34] = new("PSHS", M6809Mode.RegisterList);
        t[0x35] = new("PULS", M6809Mode.RegisterList);
        t[0x36] = new("PSHU", M6809Mode.RegisterList);
        t[0x37] = new("PULU", M6809Mode.RegisterList);
        t[0x38] = new("ANDCC", M6809Mode.Immediate8, true);
        t[0x39] = new("RTS", M6809Mode.Inherent);
        t[0x3A] = new("ABX", M6809Mode.Inherent);
        t[0x3B] = new("RTI", M6809Mode.Inherent);
        t[0x3C] = new("CWAI", M6809Mode.Immediate8);
        t[0x3D] = new("MUL", M6809Mode.Inherent);
        t[0x3E] = new("RESET", M6809Mode.Inherent, true);
        t[0x3F] = new("SWI", M6809Mode.Inherent);
        return t;
    }

    private static Entry[] BuildPage2()
    {
        // What Motorola left unassigned runs as the page 1 opcode.
        var t = (Entry[])Page1.Clone();
        t[0x20] = new("LBRA", M6809Mode.Relative16, true);
        for (var lo = 1; lo < 16; lo++) t[0x20 + lo] = new("L" + Branches[lo], M6809Mode.Relative16);
        t[0x3E] = new("SWI2", M6809Mode.Inherent, true);
        t[0x3F] = new("SWI2", M6809Mode.Inherent);

        for (var row = 0; row < 4; row++)
        {
            var mode = row switch { 0 => M6809Mode.Immediate16, 1 => M6809Mode.Direct, 2 => M6809Mode.Indexed, _ => M6809Mode.Extended };
            var at = row << 4;
            t[0x83 + at] = new("CMPD", mode);
            t[0x8C + at] = new("CMPY", mode);
            t[0x8E + at] = new("LDY", mode);
            t[0x8F + at] = new("STY", mode, row == 0);
            t[0xC3 + at] = new("ADDD", mode, true);
            t[0xCE + at] = new("LDS", mode);
            t[0xCF + at] = new("STS", mode, row == 0);
        }

        return t;
    }

    private static Entry[] BuildPage3()
    {
        var t = (Entry[])Page1.Clone();
        t[0x3E] = new("FIRQ", M6809Mode.Inherent, true);
        t[0x3F] = new("SWI3", M6809Mode.Inherent);

        for (var row = 0; row < 4; row++)
        {
            var mode = row switch { 0 => M6809Mode.Immediate16, 1 => M6809Mode.Direct, 2 => M6809Mode.Indexed, _ => M6809Mode.Extended };
            var at = row << 4;
            t[0x83 + at] = new("CMPU", mode);
            t[0x8C + at] = new("CMPS", mode);
            t[0xC3 + at] = new("ADDU", mode, true);
        }

        return t;
    }

    /// <summary>
    /// How many bytes an indexed postbyte brings with it: none, one (an 8-bit offset) or two (a 16-bit
    /// offset or address).
    /// </summary>
    public static int IndexedExtraBytes(byte postbyte)
    {
        if ((postbyte & 0x80) == 0) return 0;
        return (postbyte & 0x0F) switch
        {
            0x08 or 0x0C => 1,
            0x09 or 0x0D or 0x0F => 2,
            _ => 0,
        };
    }

    /// <summary>The bytes the instruction at <paramref name="address"/> takes, prefix and operands included.</summary>
    public static int Length(int address, Func<int, byte> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return Disassemble(address, read).Length;
    }

    /// <summary>
    /// The instruction at <paramref name="address"/> in Motorola's syntax, and its length:
    /// <c>LDA #$41</c>, <c>STA &lt;$10</c>, <c>JSR $E123</c>, <c>LDX [$02,Y]</c>, <c>BNE $C014</c>.
    /// Direct addresses carry &lt;; an 8-bit offset small enough for five bits carries &lt;, and a
    /// 16-bit one small enough for eight carries &gt;, so the text assembles back to the same bytes.
    /// Undocumented opcodes end with an asterisk.
    /// </summary>
    public static (string Text, int Length) Disassemble(int address, Func<int, byte> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        byte B(int i) => read((address + i) & 0xFFFF);

        var at = 0;
        var prefix = 0;
        var first = B(0);
        Entry e;
        if (first is 0x10 or 0x11)
        {
            prefix = first;
            at = 1;

            // A run of prefixes counts as the first of them.
            while (B(at) is 0x10 or 0x11 && at < 8) at++;
            e = Get(prefix, B(at));
        }
        else
        {
            e = Get(0, first);
        }

        var opcodeEnd = at + 1;
        var (operand, length) = Operand(e, address, opcodeEnd, B);
        var text = operand.Length == 0 ? e.Mnemonic : $"{e.Mnemonic} {operand}";
        if (e.Undocumented) text += " *";
        return (text, length);
    }

    private static (string Text, int Length) Operand(Entry e, int address, int at, Func<int, byte> b)
    {
        string H2(int v) => "$" + v.ToString("X2", CultureInfo.InvariantCulture);
        string H4(int v) => "$" + (v & 0xFFFF).ToString("X4", CultureInfo.InvariantCulture);
        var word = (b(at) << 8) | b(at + 1);

        switch (e.Mode)
        {
            case M6809Mode.Inherent:
            case M6809Mode.Prefix:
                return (string.Empty, at);
            case M6809Mode.Immediate8:
                return ("#" + H2(b(at)), at + 1);
            case M6809Mode.Immediate16:
                return ("#" + H4(word), at + 2);
            case M6809Mode.Direct:
                return ("<" + H2(b(at)), at + 1);
            case M6809Mode.Extended:
                return (H4(word), at + 2);
            case M6809Mode.Relative8:
                return (H4(address + at + 1 + (sbyte)b(at)), at + 1);
            case M6809Mode.Relative16:
                return (H4(address + at + 2 + (short)word), at + 2);
            case M6809Mode.RegisterPair:
            {
                var post = b(at);
                return ($"{RegisterNames[post >> 4]},{RegisterNames[post & 15]}", at + 1);
            }

            case M6809Mode.RegisterList:
            {
                var post = b(at);
                var other = e.Mnemonic.EndsWith('S') ? "U" : "S";
                string[] names = ["CC", "A", "B", "DP", "X", "Y", other, "PC"];
                var list = string.Join(",", Enumerable.Range(0, 8).Where(i => (post & (1 << i)) != 0).Select(i => names[i]));
                return (list, at + 1);
            }

            default:
                return Indexed(address, at, b);
        }
    }

    /// <summary>The indexed operand that starts with the postbyte at offset <paramref name="at"/>.</summary>
    private static (string Text, int Length) Indexed(int address, int at, Func<int, byte> b)
    {
        string Signed(int v, int digits) =>
            (v < 0 ? "-$" : "$") + Math.Abs(v).ToString(digits == 2 ? "X2" : "X4", CultureInfo.InvariantCulture);

        var post = b(at);
        var r = "XYUS"[(post >> 5) & 3].ToString();
        var next = at + 1;

        if ((post & 0x80) == 0)
        {
            // A five-bit offset, -16 to 15, written in decimal as Motorola's listings do.
            var offset = (post & 0x10) != 0 ? (post & 0x0F) - 16 : post & 0x0F;
            return (string.Create(CultureInfo.InvariantCulture, $"{offset},{r}"), next);
        }

        var indirect = (post & 0x10) != 0;
        string body;
        var length = next;
        switch (post & 0x0F)
        {
            case 0x00: body = $",{r}+"; break;
            case 0x01: body = $",{r}++"; break;
            case 0x02: body = $",-{r}"; break;
            case 0x03: body = $",--{r}"; break;
            case 0x04: body = $",{r}"; break;
            case 0x05: body = $"B,{r}"; break;
            case 0x06: body = $"A,{r}"; break;
            case 0x08:
            {
                var offset = (sbyte)b(next);
                body = (offset is >= -16 and <= 15 && !indirect ? "<" : "") + Signed(offset, 2) + "," + r;
                length = next + 1;
                break;
            }

            case 0x09:
            {
                var offset = (short)((b(next) << 8) | b(next + 1));
                body = (offset is >= -128 and <= 127 ? ">" : "") + Signed(offset, 4) + "," + r;
                length = next + 2;
                break;
            }

            case 0x0B: body = $"D,{r}"; break;
            case 0x0C:
            {
                var target = (address + next + 1 + (sbyte)b(next)) & 0xFFFF;
                body = "<$" + target.ToString("X4", CultureInfo.InvariantCulture) + ",PCR";
                length = next + 1;
                break;
            }

            case 0x0D:
            {
                var offset = (short)((b(next) << 8) | b(next + 1));
                var target = (address + next + 2 + offset) & 0xFFFF;
                body = (offset is >= -128 and <= 127 ? ">" : "") + "$" + target.ToString("X4", CultureInfo.InvariantCulture) + ",PCR";
                length = next + 2;
                break;
            }

            case 0x0F when indirect:
                body = "$" + ((b(next) << 8) | b(next + 1)).ToString("X4", CultureInfo.InvariantCulture);
                length = next + 2;
                break;

            default:
                // A postbyte Motorola left undefined: the core's reading of it is in M6809Core.
                body = "?" + post.ToString("X2", CultureInfo.InvariantCulture);
                break;
        }

        if (indirect && (post & 0x0F) is 0x00 or 0x02) body = "?" + post.ToString("X2", CultureInfo.InvariantCulture);
        return (indirect ? $"[{body}]" : body, length);
    }
}
