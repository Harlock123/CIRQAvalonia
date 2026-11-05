using System.Globalization;

namespace Cirq.Components.Processors;

/// <summary>What an operand field of a Z80 instruction is, for <see cref="OpcodesZ80.Pattern"/>.</summary>
public enum Z80OperandKind
{
    /// <summary>An 8-bit value or port.</summary>
    Byte,

    /// <summary>A 16-bit value or address, low byte first.</summary>
    Word,

    /// <summary>An (IX+d) or (IY+d) displacement, signed.</summary>
    Displacement,

    /// <summary>A JR or DJNZ offset, written as the address it reaches.</summary>
    Relative,
}

/// <summary>
/// The Z80 disassembler, in Zilog's syntax with Motorola-style hex: <c>LD A,$41</c>,
/// <c>LD (IX+$05),$20</c>, <c>JR NZ,$1004</c>, <c>IN A,($80)</c>, <c>EX AF,AF'</c>, <c>RST $38</c>.
/// It decodes the opcode the way the processor does — by its bit fields, x (bits 7-6), y (5-3) and
/// z (2-0), and the four prefixes — so every opcode has a meaning, and those Zilog left out of the
/// manual are marked with an asterisk: the IXH, IXL, IYH and IYL halves, SLL, the DDCB forms that
/// also copy the result into a register, IN F,(C), OUT (C),0, the duplicates of NEG, RETN and IM,
/// the ED opcodes that do nothing, and a DD or FD prefix in front of an instruction it does not
/// change. A run of DD and FD prefixes counts as the last, as the processor takes it.
/// </summary>
public static class OpcodesZ80
{
    private static readonly string[] Registers = ["B", "C", "D", "E", "H", "L", "(HL)", "A"];
    private static readonly string[] Pairs = ["BC", "DE", "HL", "SP"];
    private static readonly string[] PushPairs = ["BC", "DE", "HL", "AF"];
    private static readonly string[] Conditions = ["NZ", "Z", "NC", "C", "PO", "PE", "P", "M"];
    private static readonly string[] Arithmetic = ["ADD A,", "ADC A,", "SUB ", "SBC A,", "AND ", "XOR ", "OR ", "CP "];
    private static readonly string[] Rotates = ["RLC", "RRC", "RL", "RR", "SLA", "SRA", "SLL", "SRL"];
    private static readonly string[] Accumulator = ["RLCA", "RRCA", "RLA", "RRA", "DAA", "CPL", "SCF", "CCF"];
    private static readonly string[] Modes = ["0", "0", "1", "2", "0", "0", "1", "2"];

    private static readonly string[,] Blocks =
    {
        { "LDI", "CPI", "INI", "OUTI" },
        { "LDD", "CPD", "IND", "OUTD" },
        { "LDIR", "CPIR", "INIR", "OTIR" },
        { "LDDR", "CPDR", "INDR", "OTDR" },
    };

    /// <summary>The instruction at <paramref name="address"/>, and how many bytes it takes, prefixes and operands included.</summary>
    public static (string Text, int Length) Disassemble(int address, Func<int, byte> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var reader = new Reader(address, read, pattern: false);
        var text = Decode(reader);
        return (text, reader.At);
    }

    /// <summary>The bytes the instruction at <paramref name="address"/> takes.</summary>
    public static int Length(int address, Func<int, byte> read) => Disassemble(address, read).Length;

    /// <summary>
    /// The instruction at <paramref name="address"/> with each operand a <c>#</c>, and where each
    /// operand's bytes are — what an assembler matches its source against: <c>LD (IX+#),#</c> with a
    /// displacement at offset 2 and a byte at offset 3.
    /// </summary>
    public static (string Text, int Length, IReadOnlyList<(Z80OperandKind Kind, int Offset)> Fields) Pattern(int address, Func<int, byte> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var reader = new Reader(address, read, pattern: true);
        var text = Decode(reader);
        return (text, reader.At, reader.Fields);
    }

    /// <summary>Reads the instruction's bytes in order, and formats its operands (or marks them, for a pattern).</summary>
    private sealed class Reader(int address, Func<int, byte> read, bool pattern)
    {
        public int At { get; private set; }

        public List<(Z80OperandKind Kind, int Offset)> Fields { get; } = [];

        public byte Next() => read((address + At++) & 0xFFFF);

        public string Byte()
        {
            var offset = At;
            var value = Next();
            if (!pattern) return "$" + value.ToString("X2", CultureInfo.InvariantCulture);
            Fields.Add((Z80OperandKind.Byte, offset));
            return "#";
        }

        public string Word()
        {
            var offset = At;
            var low = Next();
            var value = (Next() << 8) | low;
            if (!pattern) return "$" + value.ToString("X4", CultureInfo.InvariantCulture);
            Fields.Add((Z80OperandKind.Word, offset));
            return "#";
        }

        public string Relative()
        {
            var offset = At;
            var value = (sbyte)Next();
            if (!pattern) return "$" + ((address + At + value) & 0xFFFF).ToString("X4", CultureInfo.InvariantCulture);
            Fields.Add((Z80OperandKind.Relative, offset));
            return "#";
        }

        /// <summary>A displacement already read at <paramref name="offset"/>, as <c>(IX+$05)</c> or <c>(IY-$03)</c>.</summary>
        public string Indexed(string register, int offset, sbyte displacement)
        {
            if (pattern)
            {
                Fields.Add((Z80OperandKind.Displacement, offset));
                return $"({register}+#)";
            }

            var sign = displacement < 0 ? '-' : '+';
            return $"({register}{sign}${Math.Abs((int)displacement).ToString("X2", CultureInfo.InvariantCulture)})";
        }
    }

    private static string Decode(Reader reader)
    {
        var op = reader.Next();
        string? index = null;
        var prefixes = 0;
        while (op is 0xDD or 0xFD)
        {
            index = op == 0xDD ? "IX" : "IY";
            prefixes++;
            op = reader.Next();
        }

        if (index is not null && op == 0xCB) return Mark(IndexedBit(reader, index), prefixes > 1);
        if (op == 0xCB) return Bit(reader.Next());
        if (op == 0xED)
        {
            var text = Extended(reader);
            return index is null ? text : Mark(text, true);
        }

        var (main, usedIndex) = Main(reader, op, index);
        return Mark(main, index is not null && (!usedIndex || prefixes > 1));
    }

    private static string Mark(string text, bool undocumented) =>
        undocumented && !text.EndsWith(" *", StringComparison.Ordinal) ? text + " *" : text;

    /// <summary>
    /// An unprefixed instruction, or one after DD or FD with HL, H, L and (HL) read as IX (IY), its
    /// halves and (IX+d). Says whether the prefix changed anything; the halves are undocumented.
    /// </summary>
    private static (string Text, bool UsedIndex) Main(Reader reader, byte op, string? index)
    {
        var x = op >> 6;
        var y = (op >> 3) & 7;
        var z = op & 7;
        var p = y >> 1;
        var q = y & 1;
        var used = false;
        var halves = false;

        string Pair(int code)
        {
            if (code != 2 || index is null) return Pairs[code];
            used = true;
            return index;
        }

        string PushPair(int code)
        {
            if (code != 2 || index is null) return PushPairs[code];
            used = true;
            return index;
        }

        // A register operand; with (HL) among the operands, H and L stay H and L.
        string Register(int code, bool memoryOperand)
        {
            if (index is null || memoryOperand) return Registers[code];
            if (code is 4 or 5)
            {
                used = true;
                halves = true;
                return index + (code == 4 ? "H" : "L");
            }

            return Registers[code];
        }

        // (HL), or (IX+d) with its displacement read now.
        string Memory()
        {
            if (index is null) return "(HL)";
            used = true;
            var offset = reader.At;
            var displacement = (sbyte)reader.Next();
            return reader.Indexed(index, offset, displacement);
        }

        string text;
        switch (x)
        {
            case 0:
                text = z switch
                {
                    0 => y switch
                    {
                        0 => "NOP",
                        1 => "EX AF,AF'",
                        2 => "DJNZ " + reader.Relative(),
                        3 => "JR " + reader.Relative(),
                        _ => $"JR {Conditions[y - 4]},{reader.Relative()}",
                    },
                    1 => q == 0 ? $"LD {Pair(p)},{reader.Word()}" : $"ADD {Pair(2)},{Pair(p)}",
                    2 => ((q << 2) | p) switch
                    {
                        0 => "LD (BC),A",
                        1 => "LD (DE),A",
                        2 => $"LD ({reader.Word()}),{Pair(2)}",
                        3 => $"LD ({reader.Word()}),A",
                        4 => "LD A,(BC)",
                        5 => "LD A,(DE)",
                        6 => $"LD {Pair(2)},({reader.Word()})",
                        _ => $"LD A,({reader.Word()})",
                    },
                    3 => (q == 0 ? "INC " : "DEC ") + Pair(p),
                    4 => "INC " + (y == 6 ? Memory() : Register(y, false)),
                    5 => "DEC " + (y == 6 ? Memory() : Register(y, false)),
                    6 => y == 6 ? $"LD {Memory()},{reader.Byte()}" : $"LD {Register(y, false)},{reader.Byte()}",
                    _ => Accumulator[y],
                };
                break;

            case 1:
                if (op == 0x76) text = "HALT";
                else if (y == 6) text = $"LD {Memory()},{Register(z, true)}";
                else if (z == 6) text = $"LD {Register(y, true)},{Memory()}";
                else text = $"LD {Register(y, false)},{Register(z, false)}";
                break;

            case 2:
                text = Arithmetic[y] + (z == 6 ? Memory() : Register(z, false));
                break;

            default:
                text = z switch
                {
                    0 => "RET " + Conditions[y],
                    1 => q == 0
                        ? "POP " + PushPair(p)
                        : p switch
                        {
                            0 => "RET",
                            1 => "EXX",
                            2 => $"JP ({Pair(2)})",
                            _ => $"LD SP,{Pair(2)}",
                        },
                    2 => $"JP {Conditions[y]},{reader.Word()}",
                    3 => y switch
                    {
                        0 => "JP " + reader.Word(),
                        2 => $"OUT ({reader.Byte()}),A",
                        3 => $"IN A,({reader.Byte()})",
                        4 => $"EX (SP),{Pair(2)}",
                        5 => "EX DE,HL",
                        6 => "DI",
                        _ => "EI",
                    },
                    4 => $"CALL {Conditions[y]},{reader.Word()}",
                    5 => q == 0 ? "PUSH " + PushPair(p) : "CALL " + reader.Word(),
                    6 => Arithmetic[y] + reader.Byte(),
                    _ => "RST $" + (op & 0x38).ToString("X2", CultureInfo.InvariantCulture),
                };
                break;
        }

        return (halves ? text + " *" : text, used);
    }

    /// <summary>A CB instruction on a register or (HL).</summary>
    private static string Bit(byte op)
    {
        var x = op >> 6;
        var y = (op >> 3) & 7;
        var operand = Registers[op & 7];
        return x switch
        {
            0 => $"{Rotates[y]} {operand}{(y == 6 ? " *" : "")}",
            1 => $"BIT {y},{operand}",
            2 => $"RES {y},{operand}",
            _ => $"SET {y},{operand}",
        };
    }

    /// <summary>DDCB d op and FDCB d op: with a register in bits 0-2 the result is also copied there (undocumented); BIT ignores those bits.</summary>
    private static string IndexedBit(Reader reader, string index)
    {
        var offset = reader.At;
        var displacement = (sbyte)reader.Next();
        var op = reader.Next();
        var x = op >> 6;
        var y = (op >> 3) & 7;
        var z = op & 7;
        var memory = reader.Indexed(index, offset, displacement);
        var copy = z == 6 ? string.Empty : "," + Registers[z];
        var text = x switch
        {
            0 => $"{Rotates[y]} {memory}{copy}",
            1 => $"BIT {y},{memory}",
            2 => $"RES {y},{memory}{copy}",
            _ => $"SET {y},{memory}{copy}",
        };
        return z != 6 || (x == 0 && y == 6) ? text + " *" : text;
    }

    /// <summary>An ED instruction; outside $40-$7F and the block instructions, a two-byte NOP.</summary>
    private static string Extended(Reader reader)
    {
        var op = reader.Next();
        var x = op >> 6;
        var y = (op >> 3) & 7;
        var z = op & 7;
        var p = y >> 1;
        var q = y & 1;

        if (x == 2 && z <= 3 && y >= 4) return Blocks[y - 4, z];
        if (x != 1) return "NOP *";

        return z switch
        {
            0 => y == 6 ? "IN F,(C) *" : $"IN {Registers[y]},(C)",
            1 => y == 6 ? "OUT (C),0 *" : $"OUT (C),{Registers[y]}",
            2 => $"{(q == 0 ? "SBC" : "ADC")} HL,{Pairs[p]}",
            3 => q == 0 ? $"LD ({reader.Word()}),{Pairs[p]}" : $"LD {Pairs[p]},({reader.Word()})",
            4 => y == 0 ? "NEG" : "NEG *",
            5 => y switch { 0 => "RETN", 1 => "RETI", _ => "RETN *" },
            6 => "IM " + Modes[y] + (y is 0 or 2 or 3 ? string.Empty : " *"),
            _ => y switch
            {
                0 => "LD I,A",
                1 => "LD R,A",
                2 => "LD A,I",
                3 => "LD A,R",
                4 => "RRD",
                5 => "RLD",
                _ => "NOP *",
            },
        };
    }
}
