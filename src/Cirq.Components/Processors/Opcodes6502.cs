using System.Globalization;

namespace Cirq.Components.Processors;

/// <summary>Which 6502 a core is: the NMOS original or the WDC CMOS one.</summary>
public enum Cpu6502Variant
{
    /// <summary>The MOS Technology 6502 of 1975, undocumented opcodes and all.</summary>
    Nmos6502,

    /// <summary>The Western Design Center W65C02S: the CMOS part, with its extra instructions and fixed bugs.</summary>
    Wdc65C02,
}

/// <summary>How an instruction finds its operand.</summary>
public enum AddressMode
{
    Implied,
    Accumulator,
    Immediate,
    ZeroPage,
    ZeroPageX,
    ZeroPageY,
    Absolute,
    AbsoluteX,
    AbsoluteY,

    /// <summary>(zp,X): a pointer in zero page, indexed before it is followed.</summary>
    IndexedIndirect,

    /// <summary>(zp),Y: a pointer in zero page, followed and then indexed.</summary>
    IndirectIndexed,

    /// <summary>(abs): JMP's pointer.</summary>
    Indirect,

    /// <summary>(zp): the 65C02's unindexed zero-page pointer.</summary>
    ZeroPageIndirect,

    /// <summary>(abs,X): the 65C02's indexed jump.</summary>
    AbsoluteIndexedIndirect,

    Relative,

    /// <summary>zp,rel: the BBR and BBS bit branches.</summary>
    ZeroPageRelative,
}

/// <summary>
/// What each opcode is, for both variants: the mnemonic and the addressing mode. The core executes
/// from this table and the disassembler prints from it, so the two cannot disagree about what a
/// byte means.
/// <para>
/// The NMOS table includes the undocumented opcodes under the names most references use (SLO,
/// RLA, SRE, RRA, SAX, LAX, DCP, ISC, ANC, ALR, ARR, SBX, ANE, LXA, LAS, TAS, SHA, SHX, SHY, the
/// extra NOPs and JAM). On the 65C02 every opcode is defined; the ones WDC left unassigned are
/// NOPs of one, two or three bytes.
/// </para>
/// </summary>
public static class Opcodes6502
{
    /// <summary>One opcode: its mnemonic and how it addresses.</summary>
    public readonly record struct Entry(string Mnemonic, AddressMode Mode)
    {
        /// <summary>Bytes the instruction occupies, opcode included.</summary>
        public int Length => Mode switch
        {
            AddressMode.Implied or AddressMode.Accumulator => 1,
            AddressMode.Absolute or AddressMode.AbsoluteX or AddressMode.AbsoluteY or AddressMode.Indirect
                or AddressMode.AbsoluteIndexedIndirect or AddressMode.ZeroPageRelative => 3,
            _ => 2,
        };
    }

    private static readonly string[] NmosText =
    [
        // x0      x1        x2        x3        x4        x5        x6        x7        x8        x9        xA        xB        xC        xD        xE        xF
        "BRK imp", "ORA izx", "JAM imp", "SLO izx", "NOP zp",  "ORA zp",  "ASL zp",  "SLO zp",  "PHP imp", "ORA imm", "ASL acc", "ANC imm", "NOP abs", "ORA abs", "ASL abs", "SLO abs",
        "BPL rel", "ORA izy", "JAM imp", "SLO izy", "NOP zpx", "ORA zpx", "ASL zpx", "SLO zpx", "CLC imp", "ORA aby", "NOP imp", "SLO aby", "NOP abx", "ORA abx", "ASL abx", "SLO abx",
        "JSR abs", "AND izx", "JAM imp", "RLA izx", "BIT zp",  "AND zp",  "ROL zp",  "RLA zp",  "PLP imp", "AND imm", "ROL acc", "ANC imm", "BIT abs", "AND abs", "ROL abs", "RLA abs",
        "BMI rel", "AND izy", "JAM imp", "RLA izy", "NOP zpx", "AND zpx", "ROL zpx", "RLA zpx", "SEC imp", "AND aby", "NOP imp", "RLA aby", "NOP abx", "AND abx", "ROL abx", "RLA abx",
        "RTI imp", "EOR izx", "JAM imp", "SRE izx", "NOP zp",  "EOR zp",  "LSR zp",  "SRE zp",  "PHA imp", "EOR imm", "LSR acc", "ALR imm", "JMP abs", "EOR abs", "LSR abs", "SRE abs",
        "BVC rel", "EOR izy", "JAM imp", "SRE izy", "NOP zpx", "EOR zpx", "LSR zpx", "SRE zpx", "CLI imp", "EOR aby", "NOP imp", "SRE aby", "NOP abx", "EOR abx", "LSR abx", "SRE abx",
        "RTS imp", "ADC izx", "JAM imp", "RRA izx", "NOP zp",  "ADC zp",  "ROR zp",  "RRA zp",  "PLA imp", "ADC imm", "ROR acc", "ARR imm", "JMP ind", "ADC abs", "ROR abs", "RRA abs",
        "BVS rel", "ADC izy", "JAM imp", "RRA izy", "NOP zpx", "ADC zpx", "ROR zpx", "RRA zpx", "SEI imp", "ADC aby", "NOP imp", "RRA aby", "NOP abx", "ADC abx", "ROR abx", "RRA abx",
        "NOP imm", "STA izx", "NOP imm", "SAX izx", "STY zp",  "STA zp",  "STX zp",  "SAX zp",  "DEY imp", "NOP imm", "TXA imp", "ANE imm", "STY abs", "STA abs", "STX abs", "SAX abs",
        "BCC rel", "STA izy", "JAM imp", "SHA izy", "STY zpx", "STA zpx", "STX zpy", "SAX zpy", "TYA imp", "STA aby", "TXS imp", "TAS aby", "SHY abx", "STA abx", "SHX aby", "SHA aby",
        "LDY imm", "LDA izx", "LDX imm", "LAX izx", "LDY zp",  "LDA zp",  "LDX zp",  "LAX zp",  "TAY imp", "LDA imm", "TAX imp", "LXA imm", "LDY abs", "LDA abs", "LDX abs", "LAX abs",
        "BCS rel", "LDA izy", "JAM imp", "LAX izy", "LDY zpx", "LDA zpx", "LDX zpy", "LAX zpy", "CLV imp", "LDA aby", "TSX imp", "LAS aby", "LDY abx", "LDA abx", "LDX aby", "LAX aby",
        "CPY imm", "CMP izx", "NOP imm", "DCP izx", "CPY zp",  "CMP zp",  "DEC zp",  "DCP zp",  "INY imp", "CMP imm", "DEX imp", "SBX imm", "CPY abs", "CMP abs", "DEC abs", "DCP abs",
        "BNE rel", "CMP izy", "JAM imp", "DCP izy", "NOP zpx", "CMP zpx", "DEC zpx", "DCP zpx", "CLD imp", "CMP aby", "NOP imp", "DCP aby", "NOP abx", "CMP abx", "DEC abx", "DCP abx",
        "CPX imm", "SBC izx", "NOP imm", "ISC izx", "CPX zp",  "SBC zp",  "INC zp",  "ISC zp",  "INX imp", "SBC imm", "NOP imp", "SBC imm", "CPX abs", "SBC abs", "INC abs", "ISC abs",
        "BEQ rel", "SBC izy", "JAM imp", "ISC izy", "NOP zpx", "SBC zpx", "INC zpx", "ISC zpx", "SED imp", "SBC aby", "NOP imp", "ISC aby", "NOP abx", "SBC abx", "INC abx", "ISC abx",
    ];

    private static readonly string[] CmosText =
    [
        // x0      x1        x2        x3        x4        x5        x6        x7        x8        x9        xA        xB        xC        xD        xE        xF
        "BRK imp", "ORA izx", "NOP imm", "NOP imp", "TSB zp",  "ORA zp",  "ASL zp",  "RMB0 zp", "PHP imp", "ORA imm", "ASL acc", "NOP imp", "TSB abs", "ORA abs", "ASL abs", "BBR0 zpr",
        "BPL rel", "ORA izy", "ORA zpi", "NOP imp", "TRB zp",  "ORA zpx", "ASL zpx", "RMB1 zp", "CLC imp", "ORA aby", "INC acc", "NOP imp", "TRB abs", "ORA abx", "ASL abx", "BBR1 zpr",
        "JSR abs", "AND izx", "NOP imm", "NOP imp", "BIT zp",  "AND zp",  "ROL zp",  "RMB2 zp", "PLP imp", "AND imm", "ROL acc", "NOP imp", "BIT abs", "AND abs", "ROL abs", "BBR2 zpr",
        "BMI rel", "AND izy", "AND zpi", "NOP imp", "BIT zpx", "AND zpx", "ROL zpx", "RMB3 zp", "SEC imp", "AND aby", "DEC acc", "NOP imp", "BIT abx", "AND abx", "ROL abx", "BBR3 zpr",
        "RTI imp", "EOR izx", "NOP imm", "NOP imp", "NOP zp",  "EOR zp",  "LSR zp",  "RMB4 zp", "PHA imp", "EOR imm", "LSR acc", "NOP imp", "JMP abs", "EOR abs", "LSR abs", "BBR4 zpr",
        "BVC rel", "EOR izy", "EOR zpi", "NOP imp", "NOP zpx", "EOR zpx", "LSR zpx", "RMB5 zp", "CLI imp", "EOR aby", "PHY imp", "NOP imp", "NOP abs", "EOR abx", "LSR abx", "BBR5 zpr",
        "RTS imp", "ADC izx", "NOP imm", "NOP imp", "STZ zp",  "ADC zp",  "ROR zp",  "RMB6 zp", "PLA imp", "ADC imm", "ROR acc", "NOP imp", "JMP ind", "ADC abs", "ROR abs", "BBR6 zpr",
        "BVS rel", "ADC izy", "ADC zpi", "NOP imp", "STZ zpx", "ADC zpx", "ROR zpx", "RMB7 zp", "SEI imp", "ADC aby", "PLY imp", "NOP imp", "JMP iax", "ADC abx", "ROR abx", "BBR7 zpr",
        "BRA rel", "STA izx", "NOP imm", "NOP imp", "STY zp",  "STA zp",  "STX zp",  "SMB0 zp", "DEY imp", "BIT imm", "TXA imp", "NOP imp", "STY abs", "STA abs", "STX abs", "BBS0 zpr",
        "BCC rel", "STA izy", "STA zpi", "NOP imp", "STY zpx", "STA zpx", "STX zpy", "SMB1 zp", "TYA imp", "STA aby", "TXS imp", "NOP imp", "STZ abs", "STA abx", "STZ abx", "BBS1 zpr",
        "LDY imm", "LDA izx", "LDX imm", "NOP imp", "LDY zp",  "LDA zp",  "LDX zp",  "SMB2 zp", "TAY imp", "LDA imm", "TAX imp", "NOP imp", "LDY abs", "LDA abs", "LDX abs", "BBS2 zpr",
        "BCS rel", "LDA izy", "LDA zpi", "NOP imp", "LDY zpx", "LDA zpx", "LDX zpy", "SMB3 zp", "CLV imp", "LDA aby", "TSX imp", "NOP imp", "LDY abx", "LDA abx", "LDX aby", "BBS3 zpr",
        "CPY imm", "CMP izx", "NOP imm", "NOP imp", "CPY zp",  "CMP zp",  "DEC zp",  "SMB4 zp", "INY imp", "CMP imm", "DEX imp", "WAI imp", "CPY abs", "CMP abs", "DEC abs", "BBS4 zpr",
        "BNE rel", "CMP izy", "CMP zpi", "NOP imp", "NOP zpx", "CMP zpx", "DEC zpx", "SMB5 zp", "CLD imp", "CMP aby", "PHX imp", "STP imp", "NOP abs", "CMP abx", "DEC abx", "BBS5 zpr",
        "CPX imm", "SBC izx", "NOP imm", "NOP imp", "CPX zp",  "SBC zp",  "INC zp",  "SMB6 zp", "INX imp", "SBC imm", "NOP imp", "NOP imp", "CPX abs", "SBC abs", "INC abs", "BBS6 zpr",
        "BEQ rel", "SBC izy", "SBC zpi", "NOP imp", "NOP zpx", "SBC zpx", "INC zpx", "SMB7 zp", "SED imp", "SBC aby", "PLX imp", "NOP imp", "NOP abs", "SBC abx", "INC abx", "BBS7 zpr",
    ];

    private static readonly Entry[] Nmos = Parse(NmosText);
    private static readonly Entry[] Cmos = Parse(CmosText);

    /// <summary>The 256 opcodes of a variant.</summary>
    public static IReadOnlyList<Entry> For(Cpu6502Variant variant) => variant == Cpu6502Variant.Nmos6502 ? Nmos : Cmos;

    /// <summary>
    /// True for the NMOS opcodes the datasheet never listed. The core runs them; the disassembler
    /// marks them, because a program that uses one by accident is the usual reason to see one.
    /// </summary>
    public static bool IsUndocumented(Cpu6502Variant variant, byte opcode)
    {
        if (variant != Cpu6502Variant.Nmos6502) return false;
        var e = Nmos[opcode];
        return e.Mnemonic is "SLO" or "RLA" or "SRE" or "RRA" or "SAX" or "LAX" or "DCP" or "ISC" or "ANC" or "ALR"
                   or "ARR" or "SBX" or "ANE" or "LXA" or "LAS" or "TAS" or "SHA" or "SHX" or "SHY" or "JAM"
               || (e.Mnemonic == "NOP" && opcode != 0xEA)
               || opcode == 0xEB;
    }

    private static Entry[] Parse(string[] text)
    {
        if (text.Length != 256) throw new InvalidOperationException("An opcode table has 256 entries.");

        return [.. text.Select(t =>
        {
            var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var mode = parts[1] switch
            {
                "imp" => AddressMode.Implied,
                "acc" => AddressMode.Accumulator,
                "imm" => AddressMode.Immediate,
                "zp" => AddressMode.ZeroPage,
                "zpx" => AddressMode.ZeroPageX,
                "zpy" => AddressMode.ZeroPageY,
                "abs" => AddressMode.Absolute,
                "abx" => AddressMode.AbsoluteX,
                "aby" => AddressMode.AbsoluteY,
                "izx" => AddressMode.IndexedIndirect,
                "izy" => AddressMode.IndirectIndexed,
                "ind" => AddressMode.Indirect,
                "zpi" => AddressMode.ZeroPageIndirect,
                "iax" => AddressMode.AbsoluteIndexedIndirect,
                "rel" => AddressMode.Relative,
                "zpr" => AddressMode.ZeroPageRelative,
                _ => throw new InvalidOperationException($"Unknown mode {parts[1]}."),
            };
            return new Entry(parts[0], mode);
        })];
    }

    /// <summary>
    /// One instruction as assembly text, read through <paramref name="read"/>: <c>LDA ($20),Y</c>,
    /// <c>BNE $8012</c>. Returns the text and how many bytes the instruction takes.
    /// </summary>
    public static (string Text, int Length) Disassemble(Cpu6502Variant variant, int address, Func<int, byte> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        var opcode = read(address & 0xFFFF);
        var e = For(variant)[opcode];
        byte B(int i) => read((address + i) & 0xFFFF);
        string H2(int v) => "$" + v.ToString("X2", CultureInfo.InvariantCulture);
        string H4(int v) => "$" + v.ToString("X4", CultureInfo.InvariantCulture);
        var word = B(1) | (B(2) << 8);

        var operand = e.Mode switch
        {
            AddressMode.Implied => string.Empty,
            AddressMode.Accumulator => "A",
            AddressMode.Immediate => "#" + H2(B(1)),
            AddressMode.ZeroPage => H2(B(1)),
            AddressMode.ZeroPageX => H2(B(1)) + ",X",
            AddressMode.ZeroPageY => H2(B(1)) + ",Y",
            AddressMode.Absolute => H4(word),
            AddressMode.AbsoluteX => H4(word) + ",X",
            AddressMode.AbsoluteY => H4(word) + ",Y",
            AddressMode.IndexedIndirect => $"({H2(B(1))},X)",
            AddressMode.IndirectIndexed => $"({H2(B(1))}),Y",
            AddressMode.Indirect => $"({H4(word)})",
            AddressMode.ZeroPageIndirect => $"({H2(B(1))})",
            AddressMode.AbsoluteIndexedIndirect => $"({H4(word)},X)",
            AddressMode.Relative => H4((address + 2 + (sbyte)B(1)) & 0xFFFF),
            _ => $"{H2(B(1))},{H4((address + 3 + (sbyte)B(2)) & 0xFFFF)}",
        };

        var text = operand.Length == 0 ? e.Mnemonic : $"{e.Mnemonic} {operand}";
        if (IsUndocumented(variant, opcode)) text += " *";
        return (text, e.Length);
    }
}
