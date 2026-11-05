namespace Cirq.Components.Processors;

/// <summary>The 68000's effective addresses, operand reads and writes, and the arithmetic flags.</summary>
public sealed partial class M68000Core
{
    // What the last effective address resolved to.
    private enum EaKind { DataRegister, AddressRegister, Memory, Immediate }

    private EaKind _eaKind;
    private int _eaReg;
    private uint _ea;
    private uint _operand;

    private static uint Mask(int size) => size switch { 1 => 0xFFu, 2 => 0xFFFFu, _ => 0xFFFFFFFFu };

    private static uint Msb(int size) => size switch { 1 => 0x80u, 2 => 0x8000u, _ => 0x80000000u };

    private static int SizeOf(int bits) => bits switch { 0 => 1, 1 => 2, 2 => 4, _ => 0 };

    private static uint SignExtend(uint value, int size) => size switch
    {
        1 => (uint)(sbyte)value,
        2 => (uint)(short)value,
        _ => value,
    };

    /// <summary>A data register written at a size: a byte or word write leaves the rest of it alone.</summary>
    private void WriteD(int reg, uint value, int size)
    {
        var mask = Mask(size);
        _d[reg] = (_d[reg] & ~mask) | (value & mask);
    }

    /// <summary>The index register of a brief extension word, sign-extended from a word if bit 11 says so.</summary>
    private uint Index(ushort extension)
    {
        var reg = (extension >> 12) & 7;
        var value = (extension & 0x8000) != 0 ? _a[reg] : _d[reg];
        if ((extension & 0x0800) == 0) value = (uint)(short)value;
        return value + (uint)(sbyte)(extension & 0xFF);
    }

    /// <summary>
    /// Works out an effective address — reading the extension words it needs, with the clocks the
    /// predecrement and the index add take — leaving it in <see cref="_ea"/>. A register comes back
    /// as <see cref="EaKind"/>; an immediate is read into <see cref="_operand"/>.
    /// </summary>
    private IEnumerable<bool> Resolve(int mode, int reg, int size)
    {
        switch (mode)
        {
            case 0:
                _eaKind = EaKind.DataRegister;
                _eaReg = reg;
                yield break;

            case 1:
                _eaKind = EaKind.AddressRegister;
                _eaReg = reg;
                yield break;

            case 2:
                _ea = _a[reg];
                break;

            case 3:
                _ea = _a[reg];
                _a[reg] += Step(reg, size);
                break;

            case 4:
                yield return Idle(2);
                _a[reg] -= Step(reg, size);
                _ea = _a[reg];
                break;

            case 5:
            {
                var base5 = _a[reg];
                foreach (var c in Fetch()) yield return c;
                _ea = base5 + (uint)(short)_taken;
                break;
            }

            case 6:
            {
                yield return Idle(2);
                var base6 = _a[reg];
                foreach (var c in Fetch()) yield return c;
                _ea = base6 + Index(_taken);
                break;
            }

            default:
                switch (reg)
                {
                    case 0:
                        foreach (var c in Fetch()) yield return c;
                        _ea = (uint)(short)_taken;
                        break;

                    case 1:
                    {
                        foreach (var c in Fetch()) yield return c;
                        var high = _taken;
                        foreach (var c in Fetch()) yield return c;
                        _ea = (uint)((high << 16) | _taken);
                        break;
                    }

                    case 2:
                    {
                        var base2 = _pc;
                        foreach (var c in Fetch()) yield return c;
                        _ea = base2 + (uint)(short)_taken;
                        break;
                    }

                    case 3:
                    {
                        yield return Idle(2);
                        var base3 = _pc;
                        foreach (var c in Fetch()) yield return c;
                        _ea = base3 + Index(_taken);
                        break;
                    }

                    default:
                        _eaKind = EaKind.Immediate;
                        if (size == 4)
                        {
                            foreach (var c in Fetch()) yield return c;
                            var high = _taken;
                            foreach (var c in Fetch()) yield return c;
                            _operand = (uint)((high << 16) | _taken);
                        }
                        else
                        {
                            foreach (var c in Fetch()) yield return c;
                            _operand = size == 1 ? _taken & 0xFFu : _taken;
                        }

                        yield break;
                }

                break;
        }

        _eaKind = EaKind.Memory;
    }

    /// <summary>How far (An)+ and -(An) move: the size, but never one byte on the stack pointer.</summary>
    private static uint Step(int reg, int size) => size == 1 && reg == 7 ? 2u : (uint)size;

    /// <summary>Reads the operand the last <see cref="Resolve"/> found into <see cref="_operand"/>: a register, the immediate, or memory.</summary>
    private IEnumerable<bool> ReadOperand(int size)
    {
        switch (_eaKind)
        {
            case EaKind.DataRegister:
                _operand = _d[_eaReg] & Mask(size);
                yield break;
            case EaKind.AddressRegister:
                _operand = _a[_eaReg] & Mask(size);
                yield break;
            case EaKind.Immediate:
                yield break;
        }

        foreach (var c in ReadMemory(_ea, size)) yield return c;
    }

    /// <summary>Reads memory at <paramref name="address"/> into <see cref="_operand"/>: a byte, a word, or a long as two words, high first.</summary>
    private IEnumerable<bool> ReadMemory(uint address, int size)
    {
        if (size == 1)
        {
            yield return ReadByte(address, DataSpace);
            _operand = _data & 0xFFu;
        }
        else if (size == 2)
        {
            yield return ReadWord(address, DataSpace);
            _operand = _data;
        }
        else
        {
            yield return ReadWord(address, DataSpace);
            var high = _data;
            yield return ReadWord(address + 2, DataSpace);
            _operand = (uint)((high << 16) | _data);
        }
    }

    /// <summary>Writes memory: a byte, a word, or a long as two words — the high one first, unless <paramref name="lowFirst"/>.</summary>
    private IEnumerable<bool> WriteMemory(uint address, uint value, int size, bool lowFirst = false)
    {
        if (size == 1)
        {
            yield return WriteByte(address, (byte)value, DataSpace);
        }
        else if (size == 2)
        {
            yield return WriteWord(address, (ushort)value, DataSpace);
        }
        else if (lowFirst)
        {
            // The low word goes first, so an odd address is caught there, two bytes up.
            yield return WriteWord(address + 2, (ushort)value, DataSpace);
            yield return WriteWord(address, (ushort)(value >> 16), DataSpace);
        }
        else
        {
            yield return WriteWord(address, (ushort)(value >> 16), DataSpace);
            yield return WriteWord(address + 2, (ushort)value, DataSpace);
        }
    }

    /// <summary>
    /// Writes a result where the last <see cref="Resolve"/> pointed: a data register at the size, or
    /// memory — a long the low word first, as every read-modify-write instruction does.
    /// </summary>
    private IEnumerable<bool> WriteOperand(uint value, int size, bool lowFirst = true)
    {
        switch (_eaKind)
        {
            case EaKind.DataRegister:
                WriteD(_eaReg, value, size);
                yield break;
            case EaKind.AddressRegister:
                _a[_eaReg] = value;
                yield break;
        }

        foreach (var c in WriteMemory(_ea, value, size, lowFirst)) yield return c;
    }

    // ---- flags ----------------------------------------------------------------

    private void SetFlag(ushort flag, bool on) => _sr = (ushort)(on ? _sr | flag : _sr & ~flag);

    private bool Flag(ushort flag) => (_sr & flag) != 0;

    /// <summary>N and Z from a result, V and C cleared: the logic instructions, MOVE and TST.</summary>
    private void LogicFlags(uint result, int size)
    {
        var r = result & Mask(size);
        _sr = (ushort)((_sr & ~(FlagN | FlagZ | FlagV | FlagC))
                       | (r == 0 ? FlagZ : 0) | ((r & Msb(size)) != 0 ? FlagN : 0));
    }

    /// <summary>Destination plus source, with X in for ADDX; every flag set as ADD sets them (Z only cleared for ADDX).</summary>
    private uint Add(uint source, uint destination, int size, bool extend = false)
    {
        var mask = Mask(size);
        var msb = Msb(size);
        var x = extend && Flag(FlagX) ? 1u : 0u;
        var s = source & mask;
        var d = destination & mask;
        var r = (s + d + x) & mask;
        var carry = ((s & d) | ((s | d) & ~r)) & msb;
        var overflow = (~(s ^ d) & (s ^ r)) & msb;

        var flags = (ushort)((r & msb) != 0 ? FlagN : 0);
        if (carry != 0) flags |= FlagC | FlagX;
        if (overflow != 0) flags |= FlagV;
        if (extend) flags |= (ushort)(r == 0 ? _sr & FlagZ : 0);
        else if (r == 0) flags |= FlagZ;

        _sr = (ushort)((_sr & ~0x1F) | flags);
        return r;
    }

    /// <summary>Destination minus source, with X in for SUBX and NEGX; CMP keeps X by passing <paramref name="setX"/> false.</summary>
    private uint Sub(uint source, uint destination, int size, bool extend = false, bool setX = true)
    {
        var mask = Mask(size);
        var msb = Msb(size);
        var x = extend && Flag(FlagX) ? 1u : 0u;
        var s = source & mask;
        var d = destination & mask;
        var r = (d - s - x) & mask;
        var borrow = ((s & ~d) | (r & ~d) | (s & r)) & msb;
        var overflow = ((s ^ d) & (r ^ d)) & msb;

        var flags = (ushort)((r & msb) != 0 ? FlagN : 0);
        if (borrow != 0) flags |= FlagC;
        if (overflow != 0) flags |= FlagV;
        if (extend) flags |= (ushort)(r == 0 ? _sr & FlagZ : 0);
        else if (r == 0) flags |= FlagZ;

        if (setX) flags = (ushort)(flags | (borrow != 0 ? FlagX : 0));
        else flags = (ushort)(flags | (_sr & FlagX));

        _sr = (ushort)((_sr & ~0x1F) | flags);
        return r;
    }

    /// <summary>The condition a Bcc, DBcc or Scc tests, by its four-bit code.</summary>
    private bool Condition(int code)
    {
        var c = Flag(FlagC);
        var v = Flag(FlagV);
        var z = Flag(FlagZ);
        var n = Flag(FlagN);
        return code switch
        {
            0 => true,
            1 => false,
            2 => !c && !z,
            3 => c || z,
            4 => !c,
            5 => c,
            6 => !z,
            7 => z,
            8 => !v,
            9 => v,
            10 => !n,
            11 => n,
            12 => n == v,
            13 => n != v,
            14 => !z && n == v,
            _ => z || n != v,
        };
    }
}
