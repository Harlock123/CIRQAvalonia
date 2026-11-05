using System.Globalization;

namespace Cirq.Components.Processors;

/// <summary>What an operand field of an 8085 instruction is, for <see cref="Opcodes8085.Pattern"/>.</summary>
public enum I8085OperandKind
{
    /// <summary>An 8-bit value or port.</summary>
    Byte,

    /// <summary>A 16-bit value or address, low byte first.</summary>
    Word,
}

/// <summary>
/// The 8085 disassembler, in Intel's mnemonics with Motorola-style hex: <c>MVI A,$41</c>,
/// <c>LXI H,$8000</c>, <c>MOV M,A</c>, <c>JNZ $0012</c>, <c>IN $40</c>, <c>RST 7</c>. Every one of
/// the 256 opcodes means something on the 8085, and the ten Intel left out of the manual are marked
/// with an asterisk: DSUB, ARHL, RDEL, LDHI, LDSI, RSTV, SHLX, LHLX, JNK and JK, named as Tundra's
/// CA80C85B datasheet names them (LDSI is sometimes written LDSP, JNK and JK JNX5 and JX5).
/// </summary>
public static class Opcodes8085
{
    private static readonly string[] Registers = ["B", "C", "D", "E", "H", "L", "M", "A"];
    private static readonly string[] Pairs = ["B", "D", "H", "SP"];
    private static readonly string[] PushPairs = ["B", "D", "H", "PSW"];
    private static readonly string[] Conditions = ["NZ", "Z", "NC", "C", "PO", "PE", "P", "M"];
    private static readonly string[] Arithmetic = ["ADD", "ADC", "SUB", "SBB", "ANA", "XRA", "ORA", "CMP"];
    private static readonly string[] Immediates = ["ADI", "ACI", "SUI", "SBI", "ANI", "XRI", "ORI", "CPI"];
    private static readonly string[] Accumulator = ["RLC", "RRC", "RAL", "RAR", "DAA", "CMA", "STC", "CMC"];

    /// <summary>The bytes each opcode's instruction takes, the opcode included.</summary>
    public static readonly int[] Lengths = BuildLengths();

    private static int[] BuildLengths()
    {
        var lengths = new int[256];
        for (var op = 0; op < 256; op++) lengths[op] = Disassemble(0, a => a == 0 ? (byte)op : (byte)0).Length;
        return lengths;
    }

    /// <summary>The instruction at <paramref name="address"/>, and how many bytes it takes.</summary>
    public static (string Text, int Length) Disassemble(int address, Func<int, byte> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var reader = new Reader(address, read, pattern: false);
        var text = Decode(reader);
        return (text, reader.At);
    }

    /// <summary>
    /// The instruction at <paramref name="address"/> with each operand a <c>#</c>, and where each
    /// operand's bytes are — what an assembler matches its source against: <c>LXI H,#</c> with a
    /// word at offset 1.
    /// </summary>
    public static (string Text, int Length, IReadOnlyList<(I8085OperandKind Kind, int Offset)> Fields) Pattern(int address, Func<int, byte> read)
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

        public List<(I8085OperandKind Kind, int Offset)> Fields { get; } = [];

        public byte Next() => read((address + At++) & 0xFFFF);

        public string Byte()
        {
            var offset = At;
            var value = Next();
            if (!pattern) return "$" + value.ToString("X2", CultureInfo.InvariantCulture);
            Fields.Add((I8085OperandKind.Byte, offset));
            return "#";
        }

        public string Word()
        {
            var offset = At;
            var low = Next();
            var value = (Next() << 8) | low;
            if (!pattern) return "$" + value.ToString("X4", CultureInfo.InvariantCulture);
            Fields.Add((I8085OperandKind.Word, offset));
            return "#";
        }
    }

    private static string Decode(Reader reader)
    {
        var op = reader.Next();
        var x = op >> 6;
        var y = (op >> 3) & 7;
        var z = op & 7;
        var p = y >> 1;
        var q = y & 1;

        switch (x)
        {
            case 0:
                return z switch
                {
                    0 => y switch
                    {
                        0 => "NOP",
                        1 => "DSUB *",
                        2 => "ARHL *",
                        3 => "RDEL *",
                        4 => "RIM",
                        5 => $"LDHI {reader.Byte()} *",
                        6 => "SIM",
                        _ => $"LDSI {reader.Byte()} *",
                    },
                    1 => q == 0 ? $"LXI {Pairs[p]},{reader.Word()}" : "DAD " + Pairs[p],
                    2 => ((q << 2) | p) switch
                    {
                        0 => "STAX B",
                        1 => "STAX D",
                        2 => $"SHLD {reader.Word()}",
                        3 => $"STA {reader.Word()}",
                        4 => "LDAX B",
                        5 => "LDAX D",
                        6 => $"LHLD {reader.Word()}",
                        _ => $"LDA {reader.Word()}",
                    },
                    3 => (q == 0 ? "INX " : "DCX ") + Pairs[p],
                    4 => "INR " + Registers[y],
                    5 => "DCR " + Registers[y],
                    6 => $"MVI {Registers[y]},{reader.Byte()}",
                    _ => Accumulator[y],
                };

            case 1:
                return op == 0x76 ? "HLT" : $"MOV {Registers[y]},{Registers[z]}";

            case 2:
                return $"{Arithmetic[y]} {Registers[z]}";

            default:
                return z switch
                {
                    0 => "R" + Conditions[y],
                    1 => q == 0
                        ? "POP " + PushPairs[p]
                        : p switch
                        {
                            0 => "RET",
                            1 => "SHLX *",
                            2 => "PCHL",
                            _ => "SPHL",
                        },
                    2 => $"J{Conditions[y]} {reader.Word()}",
                    3 => y switch
                    {
                        0 => "JMP " + reader.Word(),
                        1 => "RSTV *",
                        2 => $"OUT {reader.Byte()}",
                        3 => $"IN {reader.Byte()}",
                        4 => "XTHL",
                        5 => "XCHG",
                        6 => "DI",
                        _ => "EI",
                    },
                    4 => $"C{Conditions[y]} {reader.Word()}",
                    5 => q == 0
                        ? "PUSH " + PushPairs[p]
                        : p switch
                        {
                            0 => "CALL " + reader.Word(),
                            1 => $"JNK {reader.Word()} *",
                            2 => "LHLX *",
                            _ => $"JK {reader.Word()} *",
                        },
                    6 => $"{Immediates[y]} {reader.Byte()}",
                    _ => "RST " + y.ToString(CultureInfo.InvariantCulture),
                };
        }
    }
}
