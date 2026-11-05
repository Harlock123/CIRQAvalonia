using System.Globalization;
using System.Text;

namespace Cirq.Components.Processors;

/// <summary>
/// Turns 68000 machine code back into Motorola assembly language, for the bus decoder, the agent
/// tools and the processor's own readout: <c>MOVE.L D0,(A1)+</c>, <c>LEA $10(A0),A1</c>,
/// <c>BNE.S $000412</c>, <c>MOVEM.L D0-D7/A0-A6,-(A7)</c>.
/// <para>
/// Addresses and immediates are hexadecimal with a <c>$</c>; an absolute short address is written
/// with <c>.W</c> and a long one without; a PC-relative operand and a branch show the address they
/// reach. A word that is no 68000 instruction comes out as <c>DC.W $xxxx</c>, and line A and line F
/// words as <c>LINEA</c> and <c>LINEF</c> with their word.
/// </para>
/// </summary>
public static class M68000Disassembler
{
    /// <summary>One instruction at <paramref name="address"/>: its text, and its length in bytes.</summary>
    public static (string Text, int Length) Disassemble(uint address, Func<uint, ushort> word)
    {
        ArgumentNullException.ThrowIfNull(word);
        var reader = new Reader(address, word);
        string? text;
        try
        {
            text = Decode(reader);
        }
        catch (InvalidOperationException)
        {
            text = null;
        }

        if (text is null) return (string.Create(CultureInfo.InvariantCulture, $"DC.W ${word(address):X4}"), 2);
        return (text, (int)(reader.Position - address));
    }

    private sealed class Reader(uint address, Func<uint, ushort> word)
    {
        public uint Position { get; private set; } = address;

        public ushort Next()
        {
            var value = word(Position);
            Position += 2;
            return value;
        }
    }

    private static string Hex(uint value) => string.Create(CultureInfo.InvariantCulture, $"${value:X}");

    private static string Signed(int value) => value < 0 ? "-" + Hex((uint)-value) : Hex((uint)value);

    private static string Address(uint value) => string.Create(CultureInfo.InvariantCulture, $"${value & 0xFFFFFF:X6}");

    private static string SizeName(int size) => size switch { 1 => ".B", 2 => ".W", _ => ".L" };

    private static int SizeOf(int bits) => bits switch { 0 => 1, 1 => 2, 2 => 4, _ => 0 };

    /// <summary>An effective address as text, reading its extension words. Null for one that does not exist.</summary>
    private static string? Ea(Reader r, int mode, int reg, int size)
    {
        switch (mode)
        {
            case 0: return $"D{reg}";
            case 1: return $"A{reg}";
            case 2: return $"(A{reg})";
            case 3: return $"(A{reg})+";
            case 4: return $"-(A{reg})";
            case 5: return $"{Signed((short)r.Next())}(A{reg})";
            case 6: return Indexed(r.Next(), $"A{reg}", null);
        }

        switch (reg)
        {
            case 0: return $"{Hex(r.Next())}.W";
            case 1:
            {
                var high = r.Next();
                var value = (uint)((high << 16) | r.Next());
                return value > 0xFFFFFF ? Hex(value) : Address(value);
            }

            case 2:
            {
                var at = r.Position;
                return $"{Address(at + (uint)(short)r.Next())}(PC)";
            }

            case 3:
            {
                var at = r.Position;
                return Indexed(r.Next(), "PC", at);
            }

            case 4:
                if (size == 4)
                {
                    var high = r.Next();
                    return "#" + Hex((uint)((high << 16) | r.Next()));
                }

                if (size == 0) return null;
                return "#" + Hex(size == 1 ? r.Next() & 0xFFu : r.Next());

            default:
                return null;
        }
    }

    private static string Indexed(ushort extension, string baseRegister, uint? pc)
    {
        var index = $"{((extension & 0x8000) != 0 ? 'A' : 'D')}{(extension >> 12) & 7}.{((extension & 0x0800) != 0 ? 'L' : 'W')}";
        var displacement = (sbyte)(extension & 0xFF);
        var prefix = pc is { } at ? Address(at + (uint)displacement) : Signed(displacement);
        return $"{prefix}({baseRegister},{index})";
    }

    [Flags]
    private enum Modes
    {
        Dn = 1, An = 2, Ind = 4, PostInc = 8, PreDec = 16, Disp = 32, Index = 64, AbsW = 128, AbsL = 256,
        PcDisp = 512, PcIndex = 1024, Imm = 2048,
        All = 0xFFF,
        Data = All & ~An,
        Control = Ind | Disp | Index | AbsW | AbsL | PcDisp | PcIndex,
        Alterable = Dn | An | Ind | PostInc | PreDec | Disp | Index | AbsW | AbsL,
        DataAlterable = Alterable & ~An,
        MemoryAlterable = Alterable & ~(Dn | An),
    }

    private static bool Valid(int mode, int reg, Modes allowed)
    {
        var bit = mode < 7 ? (Modes)(1 << mode) : reg <= 4 ? (Modes)(1 << (7 + reg)) : 0;
        return (bit & allowed) != 0;
    }

    private static readonly string[] Conditions = ["T", "F", "HI", "LS", "CC", "CS", "NE", "EQ", "VC", "VS", "PL", "MI", "GE", "LT", "GT", "LE"];

    private static string? Decode(Reader r)
    {
        var start = r.Position;
        var op = r.Next();
        var mode = (op >> 3) & 7;
        var reg = op & 7;
        var rx = (op >> 9) & 7;

        switch (op >> 12)
        {
            case 0x0: return Line0(r, op, mode, reg, rx);

            case 0x1 or 0x2 or 0x3:
            {
                var size = (op >> 12) switch { 1 => 1, 2 => 4, _ => 2 };
                var dstMode = (op >> 6) & 7;
                if (size == 1 && (mode == 1 || dstMode == 1)) return null;
                if (!Valid(mode, reg, Modes.All)) return null;
                if (dstMode != 1 && !Valid(dstMode, rx, Modes.DataAlterable)) return null;
                var source = Ea(r, mode, reg, size);
                var destination = Ea(r, dstMode, rx, size);
                return dstMode == 1 ? $"MOVEA{SizeName(size)} {source},{destination}" : $"MOVE{SizeName(size)} {source},{destination}";
            }

            case 0x4: return Line4(r, op, mode, reg, rx, start);

            case 0x5:
            {
                var sizeBits = (op >> 6) & 3;
                var cc = Conditions[(op >> 8) & 0xF];
                if (sizeBits == 3)
                {
                    if (mode == 1)
                    {
                        var at = r.Position;
                        var target = at + (uint)(short)r.Next();
                        return $"DB{(cc == "F" ? "RA" : cc)} D{reg},{Address(target)}";
                    }

                    if (!Valid(mode, reg, Modes.DataAlterable)) return null;
                    return $"S{cc} {Ea(r, mode, reg, 1)}";
                }

                var size = SizeOf(sizeBits);
                if (!Valid(mode, reg, Modes.Alterable) || (mode == 1 && size == 1)) return null;
                var data = rx == 0 ? 8 : rx;
                return $"{((op & 0x0100) != 0 ? "SUBQ" : "ADDQ")}{SizeName(size)} #{data},{Ea(r, mode, reg, size)}";
            }

            case 0x6:
            {
                var cc = (op >> 8) & 0xF;
                var name = cc switch { 0 => "BRA", 1 => "BSR", _ => "B" + Conditions[cc] };
                var at = r.Position;
                var displacement = (sbyte)(op & 0xFF);
                if (displacement == 0) return $"{name}.W {Address(at + (uint)(short)r.Next())}";
                return $"{name}.S {Address(at + (uint)displacement)}";
            }

            case 0x7:
                if ((op & 0x0100) != 0) return null;
                return $"MOVEQ #{Signed((sbyte)(op & 0xFF))},D{rx}";

            case 0x8 or 0xC:
            {
                var opmode = (op >> 6) & 7;
                var isOr = (op >> 12) == 0x8;
                if (opmode == 3 || opmode == 7)
                {
                    if (!Valid(mode, reg, Modes.Data)) return null;
                    var name = isOr ? (opmode == 3 ? "DIVU" : "DIVS") : (opmode == 3 ? "MULU" : "MULS");
                    return $"{name}.W {Ea(r, mode, reg, 2)},D{rx}";
                }

                if ((op & 0x01F0) == 0x0100)
                {
                    var name = isOr ? "SBCD" : "ABCD";
                    return (op & 8) == 0 ? $"{name} D{reg},D{rx}" : $"{name} -(A{reg}),-(A{rx})";
                }

                if (!isOr && (op & 0x01F8) is 0x0140 or 0x0148 or 0x0188)
                {
                    return ((op >> 3) & 0x1F) switch
                    {
                        0x08 => $"EXG D{rx},D{reg}",
                        0x09 => $"EXG A{rx},A{reg}",
                        _ => $"EXG D{rx},A{reg}",
                    };
                }

                return Logical(r, isOr ? "OR" : "AND", op, mode, reg, rx);
            }

            case 0x9 or 0xD:
            {
                var name = (op >> 12) == 0x9 ? "SUB" : "ADD";
                var opmode = (op >> 6) & 7;
                if (opmode is 3 or 7)
                {
                    var size = opmode == 3 ? 2 : 4;
                    if (!Valid(mode, reg, Modes.All)) return null;
                    return $"{name}A{SizeName(size)} {Ea(r, mode, reg, size)},A{rx}";
                }

                var sz = SizeOf(opmode & 3);
                if ((op & 0x0130) == 0x0100)
                    return (op & 8) == 0 ? $"{name}X{SizeName(sz)} D{reg},D{rx}" : $"{name}X{SizeName(sz)} -(A{reg}),-(A{rx})";

                if ((op & 0x0100) != 0)
                {
                    if (!Valid(mode, reg, Modes.MemoryAlterable)) return null;
                    return $"{name}{SizeName(sz)} D{rx},{Ea(r, mode, reg, sz)}";
                }

                if (!Valid(mode, reg, Modes.All) || (sz == 1 && mode == 1)) return null;
                return $"{name}{SizeName(sz)} {Ea(r, mode, reg, sz)},D{rx}";
            }

            case 0xA: return string.Create(CultureInfo.InvariantCulture, $"LINEA ${op:X4}");

            case 0xB:
            {
                var opmode = (op >> 6) & 7;
                if (opmode is 3 or 7)
                {
                    var size = opmode == 3 ? 2 : 4;
                    if (!Valid(mode, reg, Modes.All)) return null;
                    return $"CMPA{SizeName(size)} {Ea(r, mode, reg, size)},A{rx}";
                }

                if (opmode < 3)
                {
                    var size = SizeOf(opmode);
                    if (!Valid(mode, reg, Modes.All) || (size == 1 && mode == 1)) return null;
                    return $"CMP{SizeName(size)} {Ea(r, mode, reg, size)},D{rx}";
                }

                var sz = SizeOf(opmode & 3);
                if (mode == 1) return $"CMPM{SizeName(sz)} (A{reg})+,(A{rx})+";
                if (!Valid(mode, reg, Modes.DataAlterable)) return null;
                return $"EOR{SizeName(sz)} D{rx},{Ea(r, mode, reg, sz)}";
            }

            case 0xE: return Shift(r, op, mode, reg, rx);

            default: return string.Create(CultureInfo.InvariantCulture, $"LINEF ${op:X4}");
        }
    }

    private static string? Logical(Reader r, string name, ushort op, int mode, int reg, int rx)
    {
        var size = SizeOf((op >> 6) & 3);
        if ((op & 0x0100) != 0)
        {
            if (!Valid(mode, reg, Modes.MemoryAlterable)) return null;
            return $"{name}{SizeName(size)} D{rx},{Ea(r, mode, reg, size)}";
        }

        if (!Valid(mode, reg, Modes.Data)) return null;
        return $"{name}{SizeName(size)} {Ea(r, mode, reg, size)},D{rx}";
    }

    private static string? Line0(Reader r, ushort op, int mode, int reg, int rx)
    {
        string[] bitNames = ["BTST", "BCHG", "BCLR", "BSET"];

        if ((op & 0x0100) != 0)
        {
            if (mode == 1)
            {
                var at = r.Position;
                var displacement = Signed((short)r.Next());
                var size = (op & 0x0040) != 0 ? ".L" : ".W";
                return (op & 0x0080) != 0 ? $"MOVEP{size} D{rx},{displacement}(A{reg})" : $"MOVEP{size} {displacement}(A{reg}),D{rx}";
            }

            var kind = (op >> 6) & 3;
            if (!Valid(mode, reg, kind == 0 ? Modes.Data : Modes.DataAlterable)) return null;
            return $"{bitNames[kind]} D{rx},{Ea(r, mode, reg, 1)}";
        }

        var which = (op >> 9) & 7;
        if (which == 4)
        {
            var kind = (op >> 6) & 3;
            if (!Valid(mode, reg, kind == 0 ? Modes.Data & ~Modes.Imm : Modes.DataAlterable)) return null;
            var bit = r.Next() & 0xFF;
            return $"{bitNames[kind]} #{bit},{Ea(r, mode, reg, 1)}";
        }

        var sizeBits = (op >> 6) & 3;
        string[] names = ["ORI", "ANDI", "SUBI", "ADDI", "", "EORI", "CMPI", ""];
        if (mode == 7 && reg == 4 && which is 0 or 1 or 5 && sizeBits < 2)
        {
            var value = r.Next();
            return sizeBits == 0 ? $"{names[which]} #{Hex(value & 0xFFu)},CCR" : $"{names[which]} #{Hex(value)},SR";
        }

        if (sizeBits == 3 || names[which].Length == 0 || !Valid(mode, reg, Modes.DataAlterable)) return null;
        var sz = SizeOf(sizeBits);
        var immediate = Ea(r, 7, 4, sz);
        return $"{names[which]}{SizeName(sz)} {immediate},{Ea(r, mode, reg, sz)}";
    }

    private static string? Line4(Reader r, ushort op, int mode, int reg, int rx, uint start)
    {
        if ((op & 0x01C0) == 0x01C0)
        {
            if (!Valid(mode, reg, Modes.Control)) return null;
            return $"LEA {Ea(r, mode, reg, 4)},A{rx}";
        }

        if ((op & 0x01C0) == 0x0180)
        {
            if (!Valid(mode, reg, Modes.Data)) return null;
            return $"CHK.W {Ea(r, mode, reg, 2)},D{rx}";
        }

        if ((op & 0x0100) != 0) return null;
        var sizeBits = (op >> 6) & 3;

        switch ((op >> 8) & 0xF)
        {
            case 0x0:
                if (!Valid(mode, reg, Modes.DataAlterable)) return null;
                return sizeBits == 3 ? $"MOVE SR,{Ea(r, mode, reg, 2)}" : $"NEGX{SizeName(SizeOf(sizeBits))} {Ea(r, mode, reg, SizeOf(sizeBits))}";
            case 0x2:
                if (sizeBits == 3 || !Valid(mode, reg, Modes.DataAlterable)) return null;
                return $"CLR{SizeName(SizeOf(sizeBits))} {Ea(r, mode, reg, SizeOf(sizeBits))}";
            case 0x4:
                if (sizeBits == 3) return !Valid(mode, reg, Modes.Data) ? null : $"MOVE {Ea(r, mode, reg, 2)},CCR";
                if (!Valid(mode, reg, Modes.DataAlterable)) return null;
                return $"NEG{SizeName(SizeOf(sizeBits))} {Ea(r, mode, reg, SizeOf(sizeBits))}";
            case 0x6:
                if (sizeBits == 3) return !Valid(mode, reg, Modes.Data) ? null : $"MOVE {Ea(r, mode, reg, 2)},SR";
                if (!Valid(mode, reg, Modes.DataAlterable)) return null;
                return $"NOT{SizeName(SizeOf(sizeBits))} {Ea(r, mode, reg, SizeOf(sizeBits))}";
            case 0x8:
                switch (sizeBits)
                {
                    case 0: return !Valid(mode, reg, Modes.DataAlterable) ? null : $"NBCD {Ea(r, mode, reg, 1)}";
                    case 1:
                        if (mode == 0) return $"SWAP D{reg}";
                        return Valid(mode, reg, Modes.Control) ? $"PEA {Ea(r, mode, reg, 4)}" : null;
                    default:
                        if (mode == 0) return $"EXT{(sizeBits == 3 ? ".L" : ".W")} D{reg}";
                        if (!Valid(mode, reg, (Modes.Control & Modes.Alterable) | Modes.PreDec)) return null;
                        var mask = r.Next();
                        return $"MOVEM{(sizeBits == 3 ? ".L" : ".W")} {RegisterList(mask, mode == 4)},{Ea(r, mode, reg, 2)}";
                }

            case 0xA:
                if (op == 0x4AFC) return "ILLEGAL";
                if (sizeBits == 3) return !Valid(mode, reg, Modes.DataAlterable) ? null : $"TAS {Ea(r, mode, reg, 1)}";
                if (mode == 1 && sizeBits == 0 || !Valid(mode, reg, Modes.Data)) return null;
                return $"TST{SizeName(SizeOf(sizeBits))} {Ea(r, mode, reg, SizeOf(sizeBits))}";
            case 0xC:
                if (sizeBits < 2 || !Valid(mode, reg, Modes.Control | Modes.PostInc)) return null;
                {
                    var mask = r.Next();
                    return $"MOVEM{(sizeBits == 3 ? ".L" : ".W")} {Ea(r, mode, reg, 2)},{RegisterList(mask, false)}";
                }

            case 0xE:
                switch (sizeBits)
                {
                    case 2: return Valid(mode, reg, Modes.Control) ? $"JSR {Ea(r, mode, reg, 4)}" : null;
                    case 3: return Valid(mode, reg, Modes.Control) ? $"JMP {Ea(r, mode, reg, 4)}" : null;
                    case 0: return null;
                }

                switch ((op >> 4) & 0xF)
                {
                    case 0x4: return $"TRAP #{op & 0xF}";
                    case 0x5:
                        if ((op & 8) != 0) return $"UNLK A{reg}";
                        return $"LINK A{reg},#{Signed((short)r.Next())}";
                    case 0x6: return (op & 8) == 0 ? $"MOVE A{reg},USP" : $"MOVE USP,A{reg}";
                    case 0x7:
                        return (op & 0xF) switch
                        {
                            0 => "RESET",
                            1 => "NOP",
                            2 => $"STOP #{Hex(r.Next())}",
                            3 => "RTE",
                            5 => "RTS",
                            6 => "TRAPV",
                            7 => "RTR",
                            _ => null,
                        };
                }

                return null;
        }

        return null;
    }

    private static string? Shift(Reader r, ushort op, int mode, int reg, int rx)
    {
        string[] names = ["AS", "LS", "ROX", "RO"];
        var direction = (op & 0x0100) != 0 ? "L" : "R";
        var sizeBits = (op >> 6) & 3;

        if (sizeBits == 3)
        {
            if ((op & 0x0800) != 0 || !Valid(mode, reg, Modes.MemoryAlterable)) return null;
            return $"{names[(op >> 9) & 3]}{direction}.W {Ea(r, mode, reg, 2)}";
        }

        var size = SizeOf(sizeBits);
        var count = (op & 0x0020) != 0 ? $"D{rx}" : $"#{(rx == 0 ? 8 : rx)}";
        return $"{names[(op >> 3) & 3]}{direction}{SizeName(size)} {count},D{reg}";
    }

    /// <summary>A MOVEM mask as a register list, <c>D0-D3/A0/A6</c>; with -(An) the mask is reversed.</summary>
    public static string RegisterList(ushort mask, bool reversed)
    {
        if (reversed)
        {
            var flipped = 0;
            for (var i = 0; i < 16; i++)
                if ((mask & (1 << i)) != 0) flipped |= 1 << (15 - i);
            mask = (ushort)flipped;
        }

        if (mask == 0) return "0";

        var parts = new List<string>();
        foreach (var (offset, letter) in new[] { (0, 'D'), (8, 'A') })
        {
            var i = 0;
            while (i < 8)
            {
                if ((mask & (1 << (offset + i))) == 0)
                {
                    i++;
                    continue;
                }

                var first = i;
                while (i + 1 < 8 && (mask & (1 << (offset + i + 1))) != 0) i++;
                parts.Add(first == i ? $"{letter}{first}" : $"{letter}{first}-{letter}{i}");
                i++;
            }
        }

        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (builder.Length > 0) builder.Append('/');
            builder.Append(part);
        }

        return builder.ToString();
    }
}
