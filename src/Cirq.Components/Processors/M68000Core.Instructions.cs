namespace Cirq.Components.Processors;

/// <summary>The 68000's instructions, each the bus cycles it runs and the clocks between them.</summary>
public sealed partial class M68000Core
{
    /// <summary>Effective address modes, as bits, for checking which an instruction allows.</summary>
    [Flags]
    private enum Modes
    {
        None = 0,
        Dn = 1,
        An = 2,
        Ind = 4,
        PostInc = 8,
        PreDec = 16,
        Disp = 32,
        Index = 64,
        AbsW = 128,
        AbsL = 256,
        PcDisp = 512,
        PcIndex = 1024,
        Imm = 2048,

        All = 0xFFF,
        Data = All & ~An,
        Memory = All & ~(Dn | An),
        Control = Ind | Disp | Index | AbsW | AbsL | PcDisp | PcIndex,
        Alterable = Dn | An | Ind | PostInc | PreDec | Disp | Index | AbsW | AbsL,
        DataAlterable = Alterable & ~An,
        MemoryAlterable = Alterable & ~(Dn | An),
    }

    private static Modes ModeOf(int mode, int reg) => mode < 7 ? (Modes)(1 << mode) : reg <= 4 ? (Modes)(1 << (7 + reg)) : Modes.None;

    private static bool Allowed(int mode, int reg, Modes allowed) => (ModeOf(mode, reg) & allowed) != 0;

    private IEnumerable<bool> Execute(ushort op)
    {
        var mode = (op >> 3) & 7;
        var reg = op & 7;

        switch (op >> 12)
        {
            case 0x0: return Line0(op, mode, reg);
            case 0x1: return Move(op, 1);
            case 0x2: return Move(op, 4);
            case 0x3: return Move(op, 2);
            case 0x4: return Line4(op, mode, reg);
            case 0x5: return Line5(op, mode, reg);
            case 0x6: return Branch(op);
            case 0x7: return (op & 0x0100) == 0 ? MoveQuick(op) : Illegal();
            case 0x8: return Line8(op, mode, reg);
            case 0x9: return AddSub(op, mode, reg, subtract: true);
            case 0xA: return Exception(10, InstructionAddress, 4);
            case 0xB: return LineB(op, mode, reg);
            case 0xC: return LineC(op, mode, reg);
            case 0xD: return AddSub(op, mode, reg, subtract: false);
            case 0xE: return Shift(op, mode, reg);
            default: return Exception(11, InstructionAddress, 4);
        }
    }

    private IEnumerable<bool> Illegal() => Exception(4, InstructionAddress, 4);

    private IEnumerable<bool> Privileged() => Exception(8, InstructionAddress, 4);

    // ---- line 0: immediates, bit operations, MOVEP ---------------------------------

    private IEnumerable<bool> Line0(ushort op, int mode, int reg)
    {
        if ((op & 0x0100) != 0)
        {
            if (mode == 1) return MoveP(op, reg);
            return Bit(op, mode, reg, dynamic: true);
        }

        var kind = (op >> 9) & 7;
        if (kind == 4) return Bit(op, mode, reg, dynamic: false);

        var sizeBits = (op >> 6) & 3;

        // ORI, ANDI and EORI to CCR ($xx3C) and to SR ($xx7C).
        if (mode == 7 && reg == 4 && kind is 0 or 1 or 5 && sizeBits < 2)
            return ImmediateToStatus(kind, toSr: sizeBits == 1);

        if (sizeBits == 3 || kind is 7) return Illegal();
        var size = SizeOf(sizeBits);

        return kind switch
        {
            0 or 1 or 2 or 3 or 5 => Allowed(mode, reg, Modes.DataAlterable) ? Immediate(kind, size, mode, reg) : Illegal(),
            6 => Allowed(mode, reg, Modes.DataAlterable) ? Immediate(kind, size, mode, reg) : Illegal(),
            _ => Illegal(),
        };
    }

    /// <summary>ORI, ANDI, SUBI, ADDI, EORI and CMPI: the immediate first, then the destination.</summary>
    private IEnumerable<bool> Immediate(int kind, int size, int mode, int reg)
    {
        uint immediate;
        if (size == 4)
        {
            foreach (var c in Fetch()) yield return c;
            var high = _taken;
            foreach (var c in Fetch()) yield return c;
            immediate = (uint)((high << 16) | _taken);
        }
        else
        {
            foreach (var c in Fetch()) yield return c;
            immediate = size == 1 ? _taken & 0xFFu : _taken;
        }

        foreach (var c in Resolve(mode, reg, size)) yield return c;
        foreach (var c in ReadOperand(size)) yield return c;
        var destination = _operand;

        uint result = kind switch
        {
            0 => destination | immediate,
            1 => destination & immediate,
            2 => Sub(immediate, destination, size),
            3 => Add(immediate, destination, size),
            5 => destination ^ immediate,
            _ => Sub(immediate, destination, size, setX: false),
        };
        if (kind is 0 or 1 or 5) LogicFlags(result, size);

        foreach (var c in Prefetch()) yield return c;

        if (_eaKind == EaKind.DataRegister)
        {
            if (size == 4) yield return Idle(kind == 6 ? 2 : 4);
            if (kind != 6) WriteD(_eaReg, result, size);
            yield break;
        }

        if (kind != 6)
            foreach (var c in WriteOperand(result, size)) yield return c;
    }

    /// <summary>ORI, ANDI and EORI to CCR or (privileged) SR.</summary>
    private IEnumerable<bool> ImmediateToStatus(int kind, bool toSr)
    {
        if (toSr && !Supervisor)
        {
            foreach (var c in Privileged()) yield return c;
            yield break;
        }

        foreach (var c in Fetch()) yield return c;
        var value = toSr ? _taken : (ushort)(_taken & 0xFF | 0xFF00);
        var sr = kind switch
        {
            0 => _sr | (value & (toSr ? 0xFFFF : 0x00FF)),
            1 => _sr & value,
            _ => _sr ^ (value & (toSr ? 0xFFFF : 0x00FF)),
        };

        yield return Idle(8);
        SR = (ushort)sr;
        foreach (var c in Refill(_pc, 0)) yield return c;
    }

    /// <summary>BTST, BCHG, BCLR and BSET, with the bit number in a register or an immediate.</summary>
    private IEnumerable<bool> Bit(ushort op, int mode, int reg, bool dynamic)
    {
        var kind = (op >> 6) & 3;
        var allowed = kind == 0 ? (dynamic ? Modes.Data : Modes.Data & ~Modes.Imm) : Modes.DataAlterable;
        if (!Allowed(mode, reg, allowed))
        {
            foreach (var c in Illegal()) yield return c;
            yield break;
        }

        int bit;
        if (dynamic) bit = (int)_d[(op >> 9) & 7];
        else
        {
            foreach (var c in Fetch()) yield return c;
            bit = _taken & 0xFF;
        }

        if (mode == 0)
        {
            bit &= 31;
            var value = _d[reg];
            SetFlag(FlagZ, (value & (1u << bit)) == 0);
            foreach (var c in Prefetch()) yield return c;

            switch (kind)
            {
                case 0:
                    yield return Idle(2);
                    break;
                case 1:
                    _d[reg] = value ^ (1u << bit);
                    yield return Idle(bit < 16 ? 2 : 4);
                    break;
                case 2:
                    _d[reg] = value & ~(1u << bit);
                    yield return Idle(bit < 16 ? 4 : 6);
                    break;
                default:
                    _d[reg] = value | (1u << bit);
                    yield return Idle(bit < 16 ? 2 : 4);
                    break;
            }

            yield break;
        }

        bit &= 7;
        foreach (var c in Resolve(mode, reg, 1)) yield return c;
        foreach (var c in ReadOperand(1)) yield return c;
        var operand = _operand;
        SetFlag(FlagZ, (operand & (1u << bit)) == 0);
        foreach (var c in Prefetch()) yield return c;

        if (kind == 0)
        {
            if (_eaKind == EaKind.Immediate) yield return Idle(2);
            yield break;
        }

        var result = kind switch
        {
            1 => operand ^ (1u << bit),
            2 => operand & ~(1u << bit),
            _ => operand | (1u << bit),
        };
        foreach (var c in WriteMemory(_ea, result, 1)) yield return c;
    }

    /// <summary>MOVEP: a word or long between a data register and alternate bytes of memory.</summary>
    private IEnumerable<bool> MoveP(ushort op, int reg)
    {
        var data = (op >> 9) & 7;
        var toMemory = (op & 0x0080) != 0;
        var bytes = (op & 0x0040) != 0 ? 4 : 2;

        var base0 = _a[reg];
        foreach (var c in Fetch()) yield return c;
        var address = base0 + (uint)(short)_taken;

        if (toMemory)
        {
            var value = _d[data];
            for (var i = bytes - 1; i >= 0; i--)
            {
                yield return WriteByte(address, (byte)(value >> (i * 8)), DataSpace);
                address += 2;
            }

            foreach (var c in Prefetch()) yield return c;
        }
        else
        {
            uint value = 0;
            for (var i = 0; i < bytes; i++)
            {
                yield return ReadByte(address, DataSpace);
                value = (value << 8) | (_data & 0xFFu);
                address += 2;
            }

            WriteD(data, value, bytes);
            foreach (var c in Prefetch()) yield return c;
        }
    }

    // ---- MOVE, MOVEA, MOVEQ ------------------------------------------------------------

    private IEnumerable<bool> Move(ushort op, int size)
    {
        var srcMode = (op >> 3) & 7;
        var srcReg = op & 7;
        var dstReg = (op >> 9) & 7;
        var dstMode = (op >> 6) & 7;

        var sourceOk = Allowed(srcMode, srcReg, Modes.All) && !(size == 1 && srcMode == 1);
        var destOk = dstMode == 1 ? size != 1 : Allowed(dstMode, dstReg, Modes.DataAlterable);
        if (!sourceOk || !destOk)
        {
            foreach (var c in Illegal()) yield return c;
            yield break;
        }

        foreach (var c in Resolve(srcMode, srcReg, size)) yield return c;
        foreach (var c in ReadOperand(size)) yield return c;
        var value = _operand;
        var sourceInMemory = _eaKind == EaKind.Memory;

        if (dstMode == 1)
        {
            _a[dstReg] = SignExtend(value, size);
            foreach (var c in Prefetch()) yield return c;
            yield break;
        }

        if (dstMode == 0)
        {
            LogicFlags(value, size);
            WriteD(dstReg, value, size);
            foreach (var c in Prefetch()) yield return c;
            yield break;
        }

        switch (dstMode)
        {
            case 2 or 3:
            {
                var address = _a[dstReg];
                LogicFlags(value, size);
                foreach (var c in WriteMemory(address, value, size)) yield return c;
                if (dstMode == 3) _a[dstReg] += Step(dstReg, size);
                foreach (var c in Prefetch()) yield return c;
                break;
            }

            case 4:
            {
                LogicFlags(value, size);
                foreach (var c in Prefetch()) yield return c;
                if (size == 4)
                {
                    // A long goes down a word at a time, the low word first, the register moving
                    // two bytes before each: an odd address faults with it moved only once.
                    _a[dstReg] -= 2;
                    yield return WriteWord(_a[dstReg], (ushort)value, DataSpace);
                    _a[dstReg] -= 2;
                    yield return WriteWord(_a[dstReg], (ushort)(value >> 16), DataSpace);
                    break;
                }

                _a[dstReg] -= Step(dstReg, size);
                foreach (var c in WriteMemory(_a[dstReg], value, size)) yield return c;
                break;
            }

            case 7 when dstReg == 1 && sourceInMemory:
            {
                // An absolute long destination after a memory source: the low word of the address
                // is used from the prefetch register, and read past only after the write.
                foreach (var c in Fetch()) yield return c;
                var address = (uint)((_taken << 16) | _irc);
                LogicFlags(value, size);
                foreach (var c in WriteMemory(address, value, size)) yield return c;
                foreach (var c in Fetch()) yield return c;
                foreach (var c in Prefetch()) yield return c;
                break;
            }

            default:
            {
                foreach (var c in Resolve(dstMode, dstReg, size)) yield return c;
                LogicFlags(value, size);
                foreach (var c in WriteMemory(_ea, value, size)) yield return c;
                foreach (var c in Prefetch()) yield return c;
                break;
            }
        }
    }

    private IEnumerable<bool> MoveQuick(ushort op)
    {
        var value = (uint)(sbyte)(op & 0xFF);
        _d[(op >> 9) & 7] = value;
        LogicFlags(value, 4);
        foreach (var c in Prefetch()) yield return c;
    }

    // ---- line 4: the miscellany ---------------------------------------------------------

    private IEnumerable<bool> Line4(ushort op, int mode, int reg)
    {
        if ((op & 0x01C0) == 0x01C0) return Allowed(mode, reg, Modes.Control) ? LoadEffectiveAddress(op, mode, reg) : Illegal();
        if ((op & 0x01C0) == 0x0180) return Allowed(mode, reg, Modes.Data) ? Check(op, mode, reg) : Illegal();
        if ((op & 0x0100) != 0) return Illegal();

        var sizeBits = (op >> 6) & 3;

        switch ((op >> 8) & 0xF)
        {
            case 0x0:
                if (sizeBits == 3) return Allowed(mode, reg, Modes.DataAlterable) ? MoveFromSr(mode, reg) : Illegal();
                return Allowed(mode, reg, Modes.DataAlterable) ? Single(SingleKind.NegX, SizeOf(sizeBits), mode, reg) : Illegal();

            case 0x2:
                if (sizeBits == 3) return Illegal();
                return Allowed(mode, reg, Modes.DataAlterable) ? Single(SingleKind.Clear, SizeOf(sizeBits), mode, reg) : Illegal();

            case 0x4:
                if (sizeBits == 3) return Allowed(mode, reg, Modes.Data) ? MoveToStatus(mode, reg, toSr: false) : Illegal();
                return Allowed(mode, reg, Modes.DataAlterable) ? Single(SingleKind.Negate, SizeOf(sizeBits), mode, reg) : Illegal();

            case 0x6:
                if (sizeBits == 3) return Allowed(mode, reg, Modes.Data) ? MoveToStatus(mode, reg, toSr: true) : Illegal();
                return Allowed(mode, reg, Modes.DataAlterable) ? Single(SingleKind.Not, SizeOf(sizeBits), mode, reg) : Illegal();

            case 0x8:
                switch (sizeBits)
                {
                    case 0: return Allowed(mode, reg, Modes.DataAlterable) ? Single(SingleKind.Nbcd, 1, mode, reg) : Illegal();
                    case 1:
                        if (mode == 0) return Swap(reg);
                        return Allowed(mode, reg, Modes.Control) ? PushEffectiveAddress(mode, reg) : Illegal();
                    default:
                        if (mode == 0) return Extend(reg, sizeBits == 3);
                        return Allowed(mode, reg, (Modes.Control & Modes.Alterable) | Modes.PreDec) ? MoveMultiple(op, mode, reg, toMemory: true) : Illegal();
                }

            case 0xA:
                if (op == 0x4AFC) return Illegal();
                if (sizeBits == 3) return Allowed(mode, reg, Modes.DataAlterable) ? TestAndSet(mode, reg) : Illegal();
                return Allowed(mode, reg, Modes.Data) && !(sizeBits == 0 && mode == 1) ? Test(SizeOf(sizeBits), mode, reg) : Illegal();

            case 0xC:
                if (sizeBits >= 2)
                    return Allowed(mode, reg, Modes.Control | Modes.PostInc) ? MoveMultiple(op, mode, reg, toMemory: false) : Illegal();
                return Illegal();

            case 0xE:
                return Line4E(op, mode, reg, sizeBits);

            default:
                return Illegal();
        }
    }

    private IEnumerable<bool> Line4E(ushort op, int mode, int reg, int sizeBits)
    {
        if (sizeBits == 2) return Allowed(mode, reg, Modes.Control) ? JumpSubroutine(mode, reg) : Illegal();
        if (sizeBits == 3) return Allowed(mode, reg, Modes.Control) ? JumpTo(mode, reg) : Illegal();
        if (sizeBits == 0) return Illegal();

        switch ((op >> 4) & 0xF)
        {
            case 0x4: return Trap(op & 0xF);
            case 0x5: return (op & 8) == 0 ? Link(reg) : Unlink(reg);
            case 0x6: return MoveUsp(op);
            case 0x7:
                return (op & 0xF) switch
                {
                    0 => ResetInstruction(),
                    1 => Nop(),
                    2 => Stop(),
                    3 => ReturnFromException(),
                    5 => ReturnFromSubroutine(restoreCcr: false),
                    6 => TrapOnOverflow(),
                    7 => ReturnFromSubroutine(restoreCcr: true),
                    _ => Illegal(),
                };
            default: return Illegal();
        }
    }

    private enum SingleKind { NegX, Clear, Negate, Not, Nbcd }

    /// <summary>NEGX, CLR, NEG, NOT and NBCD: one operand read, changed and written back (CLR reads it too).</summary>
    private IEnumerable<bool> Single(SingleKind kind, int size, int mode, int reg)
    {
        foreach (var c in Resolve(mode, reg, size)) yield return c;
        foreach (var c in ReadOperand(size)) yield return c;
        var value = _operand;

        uint result;
        switch (kind)
        {
            case SingleKind.NegX: result = Sub(value, 0, size, extend: true); break;
            case SingleKind.Clear:
                result = 0;
                LogicFlags(0, size);
                break;
            case SingleKind.Negate: result = Sub(value, 0, size); break;
            case SingleKind.Not:
                result = ~value & Mask(size);
                LogicFlags(result, size);
                break;
            default: result = SubtractBcd(value, 0); break;
        }

        foreach (var c in Prefetch()) yield return c;

        if (_eaKind == EaKind.DataRegister)
        {
            if (kind == SingleKind.Nbcd) yield return Idle(2);
            else if (size == 4) yield return Idle(2);
            WriteD(reg, result, size);
            yield break;
        }

        foreach (var c in WriteOperand(result, size)) yield return c;
    }

    private IEnumerable<bool> MoveFromSr(int mode, int reg)
    {
        foreach (var c in Resolve(mode, reg, 2)) yield return c;
        foreach (var c in ReadOperand(2)) yield return c;
        var value = _sr;
        foreach (var c in Prefetch()) yield return c;

        if (_eaKind == EaKind.DataRegister)
        {
            yield return Idle(2);
            WriteD(reg, value, 2);
            yield break;
        }

        foreach (var c in WriteOperand(value, 2)) yield return c;
    }

    /// <summary>MOVE to CCR, and (privileged) MOVE to SR: the queue is refilled after it.</summary>
    private IEnumerable<bool> MoveToStatus(int mode, int reg, bool toSr)
    {
        if (toSr && !Supervisor)
        {
            foreach (var c in Privileged()) yield return c;
            yield break;
        }

        foreach (var c in Resolve(mode, reg, 2)) yield return c;
        foreach (var c in ReadOperand(2)) yield return c;
        var value = (ushort)_operand;

        yield return Idle(4);
        SR = toSr ? value : (ushort)((_sr & 0xFF00) | (value & 0xFF));
        foreach (var c in Refill(_pc, 0)) yield return c;
    }

    private IEnumerable<bool> Swap(int reg)
    {
        var value = (_d[reg] << 16) | (_d[reg] >> 16);
        _d[reg] = value;
        LogicFlags(value, 4);
        foreach (var c in Prefetch()) yield return c;
    }

    private IEnumerable<bool> Extend(int reg, bool toLong)
    {
        if (toLong)
        {
            _d[reg] = (uint)(short)_d[reg];
            LogicFlags(_d[reg], 4);
        }
        else
        {
            WriteD(reg, (uint)(sbyte)_d[reg], 2);
            LogicFlags(_d[reg], 2);
        }

        foreach (var c in Prefetch()) yield return c;
    }

    private IEnumerable<bool> Test(int size, int mode, int reg)
    {
        foreach (var c in Resolve(mode, reg, size)) yield return c;
        foreach (var c in ReadOperand(size)) yield return c;
        LogicFlags(_operand, size);
        foreach (var c in Prefetch()) yield return c;
    }

    /// <summary>TAS: an indivisible read-modify-write, setting bit 7.</summary>
    private IEnumerable<bool> TestAndSet(int mode, int reg)
    {
        foreach (var c in Resolve(mode, reg, 1)) yield return c;

        if (_eaKind == EaKind.DataRegister)
        {
            LogicFlags(_d[reg], 1);
            _d[reg] |= 0x80;
            foreach (var c in Prefetch()) yield return c;
            yield break;
        }

        ReadByte(_ea, DataSpace);
        ReadModifyWrite = true;
        yield return true;
        var value = (uint)(_data & 0xFF);
        LogicFlags(value, 1);
        yield return Idle(2);
        WriteByte(_ea, (byte)(value | 0x80), DataSpace);
        ReadModifyWrite = true;
        yield return true;
        foreach (var c in Prefetch()) yield return c;
    }

    /// <summary>CHK: a trap if the register's low word is negative or above the bound.</summary>
    private IEnumerable<bool> Check(ushort op, int mode, int reg)
    {
        foreach (var c in Resolve(mode, reg, 2)) yield return c;
        foreach (var c in ReadOperand(2)) yield return c;
        var bound = (short)_operand;
        var value = (short)_d[(op >> 9) & 7];
        foreach (var c in Prefetch()) yield return c;

        // Z, V and C are cleared; N says which bound was broken, and is left alone if neither was.
        _sr = (ushort)(_sr & ~(FlagZ | FlagV | FlagC));

        if (value > bound)
        {
            SetFlag(FlagN, value < 0);
            foreach (var c in Exception(6, PC, 4)) yield return c;
        }
        else if (value < 0)
        {
            SetFlag(FlagN, true);
            foreach (var c in Exception(6, PC, 6)) yield return c;
        }
        else
        {
            yield return Idle(6);
        }
    }

    private IEnumerable<bool> LoadEffectiveAddress(ushort op, int mode, int reg)
    {
        foreach (var c in Resolve(mode, reg, 4)) yield return c;
        if (mode == 6 || (mode == 7 && reg == 3)) yield return Idle(2);
        _a[(op >> 9) & 7] = _ea;
        foreach (var c in Prefetch()) yield return c;
    }

    private IEnumerable<bool> PushEffectiveAddress(int mode, int reg)
    {
        foreach (var c in Resolve(mode, reg, 4)) yield return c;
        if (mode == 6 || (mode == 7 && reg == 3)) yield return Idle(2);
        var value = _ea;

        // With an absolute address the push comes before the prefetch; otherwise after it.
        if (mode == 7 && reg < 2)
        {
            _a[7] -= 4;
            foreach (var c in WriteMemory(_a[7], value, 4)) yield return c;
            foreach (var c in Prefetch()) yield return c;
            yield break;
        }

        foreach (var c in Prefetch()) yield return c;
        _a[7] -= 4;
        foreach (var c in WriteMemory(_a[7], value, 4)) yield return c;
    }

    /// <summary>MOVEM: the registers in the mask to or from consecutive memory.</summary>
    private IEnumerable<bool> MoveMultiple(ushort op, int mode, int reg, bool toMemory)
    {
        var size = (op & 0x0040) != 0 ? 4 : 2;
        foreach (var c in Fetch()) yield return c;
        var mask = _taken;

        if (toMemory && mode == 4)
        {
            // -(An): the mask is reversed, A7 in bit 0, and the registers go downwards.
            var address = _a[reg];
            for (var i = 0; i < 16; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                var r = 15 - i;
                var value = r < 8 ? _d[r] : _a[r - 8];
                address -= (uint)size;
                foreach (var c in WriteMemory(address, value, size, lowFirst: true)) yield return c;
            }

            _a[reg] = address;
            foreach (var c in Prefetch()) yield return c;
            yield break;
        }

        uint at;
        if (mode == 3) at = _a[reg];
        else
        {
            foreach (var c in Resolve(mode, reg, size)) yield return c;
            at = _ea;
        }

        if (toMemory)
        {
            for (var r = 0; r < 16; r++)
            {
                if ((mask & (1 << r)) == 0) continue;
                var value = r < 8 ? _d[r] : _a[r - 8];
                foreach (var c in WriteMemory(at, value, size)) yield return c;
                at += (uint)size;
            }

            foreach (var c in Prefetch()) yield return c;
            yield break;
        }

        if (mode == 3 && mask != 0) _a[reg] = at + 2;
        for (var r = 0; r < 16; r++)
        {
            if ((mask & (1 << r)) == 0) continue;
            foreach (var c in ReadMemory(at, size)) yield return c;
            var value = SignExtend(_operand, size);
            if (r < 8) _d[r] = value;
            else _a[r - 8] = value;
            at += (uint)size;
        }

        // One more word is read past the last register, and thrown away.
        yield return ReadWord(at, DataSpace);
        if (mode == 3) _a[reg] = at;
        foreach (var c in Prefetch()) yield return c;
    }

    private IEnumerable<bool> Trap(int vector)
    {
        foreach (var c in Exception(32 + vector, _pc, 4)) yield return c;
    }

    private IEnumerable<bool> TrapOnOverflow()
    {
        foreach (var c in Prefetch()) yield return c;
        if (Flag(FlagV))
            foreach (var c in Exception(7, PC, 0)) yield return c;
    }

    private IEnumerable<bool> Link(int reg)
    {
        foreach (var c in Fetch()) yield return c;
        var displacement = (uint)(short)_taken;
        var value = _a[reg];
        _a[7] -= 4;
        var frame = _a[7];
        foreach (var c in WriteMemory(frame, reg == 7 ? frame : value, 4)) yield return c;
        _a[reg] = frame;
        _a[7] += displacement;
        foreach (var c in Prefetch()) yield return c;
    }

    private IEnumerable<bool> Unlink(int reg)
    {
        _a[7] = _a[reg];
        foreach (var c in ReadMemory(_a[7], 4)) yield return c;
        _a[7] += 4;
        _a[reg] = _operand;
        foreach (var c in Prefetch()) yield return c;
    }

    private IEnumerable<bool> MoveUsp(ushort op)
    {
        if (!Supervisor)
        {
            foreach (var c in Privileged()) yield return c;
            yield break;
        }

        var reg = op & 7;
        if ((op & 8) == 0) _inactiveSp = _a[reg];
        else _a[reg] = _inactiveSp;
        foreach (var c in Prefetch()) yield return c;
    }

    /// <summary>RESET: /RESET driven low for 124 clocks, resetting everything on it but the processor.</summary>
    private IEnumerable<bool> ResetInstruction()
    {
        if (!Supervisor)
        {
            foreach (var c in Privileged()) yield return c;
            yield break;
        }

        yield return Idle(4);
        Action = M68000BusAction.ResetOut;
        IdleClocks = 124;
        yield return true;
        foreach (var c in Prefetch()) yield return c;
    }

    private IEnumerable<bool> Nop()
    {
        foreach (var c in Prefetch()) yield return c;
    }

    /// <summary>STOP: the immediate into SR, then nothing until an interrupt, a trace or a reset.</summary>
    private IEnumerable<bool> Stop()
    {
        if (!Supervisor)
        {
            foreach (var c in Privileged()) yield return c;
            yield break;
        }

        SR = _irc;
        _pc += 4;
        IsStopped = true;
        yield return Idle(4);
    }

    private IEnumerable<bool> ReturnFromException()
    {
        if (!Supervisor)
        {
            foreach (var c in Privileged()) yield return c;
            yield break;
        }

        var sp = _a[7];
        yield return ReadWord(sp + 2, DataSpace);
        var high = _data;
        yield return ReadWord(sp, DataSpace);
        var sr = _data;
        yield return ReadWord(sp + 4, DataSpace);
        var target = (uint)((high << 16) | _data);
        _a[7] = sp + 6;
        SR = sr;
        foreach (var c in Refill(target, 0)) yield return c;
    }

    private IEnumerable<bool> ReturnFromSubroutine(bool restoreCcr)
    {
        var sp = _a[7];
        uint target;
        if (restoreCcr)
        {
            // RTR reads the PC's high word, then the CCR beneath it, then the low word.
            yield return ReadWord(sp + 2, DataSpace);
            var high = _data;
            yield return ReadWord(sp, DataSpace);
            _sr = (ushort)((_sr & 0xFF00) | (_data & 0x1F));
            yield return ReadWord(sp + 4, DataSpace);
            target = (uint)((high << 16) | _data);
            _a[7] = sp + 6;
        }
        else
        {
            yield return ReadWord(sp, DataSpace);
            var high = _data;
            yield return ReadWord(sp + 2, DataSpace);
            target = (uint)((high << 16) | _data);
            _a[7] = sp + 4;
        }

        foreach (var c in Refill(target, 0)) yield return c;
    }

    /// <summary>The control address of JMP and JSR, taking a displacement from the prefetch without reading past it.</summary>
    private IEnumerable<bool> ControlAddress(int mode, int reg)
    {
        switch (mode)
        {
            case 2:
                _ea = _a[reg];
                yield break;
            case 5:
                _ea = _a[reg] + (uint)(short)_irc;
                yield return Idle(2);
                yield break;
            case 6:
                _ea = _a[reg] + Index(_irc);
                yield return Idle(6);
                yield break;
        }

        switch (reg)
        {
            case 0:
                _ea = (uint)(short)_irc;
                yield return Idle(2);
                break;
            case 1:
                foreach (var c in Fetch()) yield return c;
                _ea = (uint)((_taken << 16) | _irc);
                break;
            case 2:
                _ea = _pc + (uint)(short)_irc;
                yield return Idle(2);
                break;
            default:
                _ea = _pc + Index(_irc);
                yield return Idle(6);
                break;
        }
    }

    /// <summary>How far past the opcode a control-mode instruction's extension words go, for its return address.</summary>
    private static uint ControlLength(int mode, int reg) => mode switch
    {
        2 => 0,
        5 or 6 => 2,
        _ => reg == 1 ? 4u : 2u,
    };

    private IEnumerable<bool> JumpTo(int mode, int reg)
    {
        foreach (var c in ControlAddress(mode, reg)) yield return c;
        foreach (var c in Refill(_ea, 0)) yield return c;
    }

    private IEnumerable<bool> JumpSubroutine(int mode, int reg)
    {
        var next = InstructionAddress + 2 + ControlLength(mode, reg);
        foreach (var c in ControlAddress(mode, reg)) yield return c;
        var target = _ea;

        _pc = target - 2;
        yield return ReadWord(target, ProgramSpace, program: true);
        _irc = _data;
        _pc = target;
        _a[7] -= 4;
        foreach (var c in WriteMemory(_a[7], next, 4)) yield return c;
        foreach (var c in Prefetch()) yield return c;
    }

    // ---- line 5: ADDQ, SUBQ, Scc, DBcc -------------------------------------------------

    private IEnumerable<bool> Line5(ushort op, int mode, int reg)
    {
        var sizeBits = (op >> 6) & 3;
        if (sizeBits == 3)
        {
            if (mode == 1) return DecrementAndBranch(op, reg);
            return Allowed(mode, reg, Modes.DataAlterable) ? SetConditionally(op, mode, reg) : Illegal();
        }

        var size = SizeOf(sizeBits);
        if (!Allowed(mode, reg, Modes.Alterable) || (mode == 1 && size == 1)) return Illegal();
        return AddSubQuick(op, size, mode, reg);
    }

    private IEnumerable<bool> AddSubQuick(ushort op, int size, int mode, int reg)
    {
        var data = (uint)((op >> 9) & 7);
        if (data == 0) data = 8;
        var subtract = (op & 0x0100) != 0;

        if (mode == 1)
        {
            _a[reg] = subtract ? _a[reg] - data : _a[reg] + data;
            foreach (var c in Prefetch()) yield return c;
            yield return Idle(4);
            yield break;
        }

        foreach (var c in Resolve(mode, reg, size)) yield return c;
        foreach (var c in ReadOperand(size)) yield return c;
        var result = subtract ? Sub(data, _operand, size) : Add(data, _operand, size);
        foreach (var c in Prefetch()) yield return c;

        if (_eaKind == EaKind.DataRegister)
        {
            if (size == 4) yield return Idle(4);
            WriteD(reg, result, size);
            yield break;
        }

        foreach (var c in WriteOperand(result, size)) yield return c;
    }

    private IEnumerable<bool> SetConditionally(ushort op, int mode, int reg)
    {
        var value = Condition((op >> 8) & 0xF) ? 0xFFu : 0u;
        foreach (var c in Resolve(mode, reg, 1)) yield return c;

        if (_eaKind == EaKind.DataRegister)
        {
            foreach (var c in Prefetch()) yield return c;
            if (value != 0) yield return Idle(2);
            WriteD(reg, value, 1);
            yield break;
        }

        foreach (var c in ReadOperand(1)) yield return c;
        foreach (var c in Prefetch()) yield return c;
        foreach (var c in WriteOperand(value, 1)) yield return c;
    }

    private IEnumerable<bool> DecrementAndBranch(ushort op, int reg)
    {
        var target = _pc + (uint)(short)_irc;

        if (Condition((op >> 8) & 0xF))
        {
            yield return Idle(4);
            foreach (var c in Fetch()) yield return c;
            foreach (var c in Prefetch()) yield return c;
            yield break;
        }

        var count = (ushort)(_d[reg] - 1);
        WriteD(reg, count, 2);
        yield return Idle(2);

        if (count != 0xFFFF)
        {
            foreach (var c in Refill(target, 0)) yield return c;
            yield break;
        }

        // Expired: the branch target was already being fetched, and is thrown away for the
        // instruction after the DBcc.
        yield return ReadWord(target, ProgramSpace, program: true);
        foreach (var c in Refill(InstructionAddress + 4, 0)) yield return c;
    }

    // ---- line 6: Bcc, BRA, BSR -----------------------------------------------------------

    private IEnumerable<bool> Branch(ushort op)
    {
        var condition = (op >> 8) & 0xF;
        var displacement = (uint)(sbyte)(op & 0xFF);
        var word = displacement == 0;
        if (word) displacement = (uint)(short)_irc;
        var target = _pc + displacement;

        if (condition == 1)
        {
            var next = _pc + (word ? 2u : 0u);
            yield return Idle(2);
            _a[7] -= 4;
            foreach (var c in WriteMemory(_a[7], next, 4)) yield return c;
            foreach (var c in Refill(target, 0)) yield return c;
            yield break;
        }

        if (Condition(condition))
        {
            yield return Idle(2);
            foreach (var c in Refill(target, 0)) yield return c;
            yield break;
        }

        yield return Idle(4);
        if (word) foreach (var c in Fetch()) yield return c;
        foreach (var c in Prefetch()) yield return c;
    }

    // ---- lines 8, 9, B, C, D: the two-operand arithmetic --------------------------------

    private IEnumerable<bool> Line8(ushort op, int mode, int reg)
    {
        var opmode = (op >> 6) & 7;
        if (opmode == 3) return Allowed(mode, reg, Modes.Data) ? Divide(op, mode, reg, signed: false) : Illegal();
        if (opmode == 7) return Allowed(mode, reg, Modes.Data) ? Divide(op, mode, reg, signed: true) : Illegal();
        if ((op & 0x01F0) == 0x0100) return Bcd(op, subtract: true);
        return Logical(op, mode, reg, or: true);
    }

    private IEnumerable<bool> LineC(ushort op, int mode, int reg)
    {
        var opmode = (op >> 6) & 7;
        if (opmode == 3) return Allowed(mode, reg, Modes.Data) ? Multiply(op, mode, reg, signed: false) : Illegal();
        if (opmode == 7) return Allowed(mode, reg, Modes.Data) ? Multiply(op, mode, reg, signed: true) : Illegal();
        if ((op & 0x01F0) == 0x0100) return Bcd(op, subtract: false);
        if ((op & 0x01F8) is 0x0140 or 0x0148 or 0x0188) return Exchange(op);
        return Logical(op, mode, reg, or: false);
    }

    /// <summary>AND and OR, either way round.</summary>
    private IEnumerable<bool> Logical(ushort op, int mode, int reg, bool or)
    {
        var dn = (op >> 9) & 7;
        var size = SizeOf((op >> 6) & 3);
        var toMemory = (op & 0x0100) != 0;

        if (!(toMemory ? Allowed(mode, reg, Modes.MemoryAlterable) : Allowed(mode, reg, Modes.Data)))
        {
            foreach (var c in Illegal()) yield return c;
            yield break;
        }

        foreach (var c in Resolve(mode, reg, size)) yield return c;
        foreach (var c in ReadOperand(size)) yield return c;
        var result = or ? _operand | _d[dn] : _operand & _d[dn];
        LogicFlags(result, size);
        var registerOrImmediate = _eaKind != EaKind.Memory;
        foreach (var c in Prefetch()) yield return c;

        if (!toMemory)
        {
            if (size == 4) yield return Idle(registerOrImmediate ? 4 : 2);
            WriteD(dn, result, size);
            yield break;
        }

        foreach (var c in WriteOperand(result, size)) yield return c;
    }

    /// <summary>ADD, ADDA, ADDX, SUB, SUBA and SUBX.</summary>
    private IEnumerable<bool> AddSub(ushort op, int mode, int reg, bool subtract)
    {
        var rn = (op >> 9) & 7;
        var opmode = (op >> 6) & 7;

        if (opmode is 3 or 7)
        {
            // ADDA and SUBA: the whole address register, the source sign-extended from a word.
            if (!Allowed(mode, reg, Modes.All))
            {
                foreach (var c in Illegal()) yield return c;
                yield break;
            }

            var size = opmode == 3 ? 2 : 4;
            foreach (var c in Resolve(mode, reg, size)) yield return c;
            foreach (var c in ReadOperand(size)) yield return c;
            var source = SignExtend(_operand, size);
            var registerOrImmediate = _eaKind != EaKind.Memory;
            _a[rn] = subtract ? _a[rn] - source : _a[rn] + source;
            foreach (var c in Prefetch()) yield return c;
            yield return Idle(size == 2 || registerOrImmediate ? 4 : 2);
            yield break;
        }

        var sz = SizeOf(opmode & 3);

        if ((op & 0x0130) == 0x0100)
        {
            foreach (var c in Extended(op, sz, subtract)) yield return c;
            yield break;
        }

        var toMemory = (op & 0x0100) != 0;
        var ok = toMemory ? Allowed(mode, reg, Modes.MemoryAlterable) : Allowed(mode, reg, Modes.All) && !(sz == 1 && mode == 1);
        if (!ok)
        {
            foreach (var c in Illegal()) yield return c;
            yield break;
        }

        foreach (var c in Resolve(mode, reg, sz)) yield return c;
        foreach (var c in ReadOperand(sz)) yield return c;
        var inMemory = _eaKind == EaKind.Memory;

        if (!toMemory)
        {
            var result = subtract ? Sub(_operand, _d[rn], sz) : Add(_operand, _d[rn], sz);
            foreach (var c in Prefetch()) yield return c;
            if (sz == 4) yield return Idle(inMemory ? 2 : 4);
            WriteD(rn, result, sz);
            yield break;
        }

        var value = subtract ? Sub(_d[rn], _operand, sz) : Add(_d[rn], _operand, sz);
        foreach (var c in Prefetch()) yield return c;
        foreach (var c in WriteOperand(value, sz)) yield return c;
    }

    /// <summary>ADDX and SUBX: registers, or -(Ay) to -(Ax).</summary>
    private IEnumerable<bool> Extended(ushort op, int size, bool subtract)
    {
        var rx = (op >> 9) & 7;
        var ry = op & 7;

        if ((op & 8) == 0)
        {
            var result = subtract ? Sub(_d[ry], _d[rx], size, extend: true) : Add(_d[ry], _d[rx], size, extend: true);
            foreach (var c in Prefetch()) yield return c;
            if (size == 4) yield return Idle(4);
            WriteD(rx, result, size);
            yield break;
        }

        yield return Idle(2);
        foreach (var c in ReadDown(ry, size)) yield return c;
        var source = _operand;
        foreach (var c in ReadDown(rx, size)) yield return c;
        var value = subtract ? Sub(source, _operand, size, extend: true) : Add(source, _operand, size, extend: true);

        if (size == 4)
        {
            yield return WriteWord(_a[rx] + 2, (ushort)value, DataSpace);
            foreach (var c in Prefetch()) yield return c;
            yield return WriteWord(_a[rx], (ushort)(value >> 16), DataSpace);
            yield break;
        }

        foreach (var c in Prefetch()) yield return c;
        foreach (var c in WriteMemory(_a[rx], value, size)) yield return c;
    }

    /// <summary>
    /// -(An) for ADDX and SUBX: a long is read a word at a time from the top down, the register
    /// moving two bytes before each, so an odd address faults with it moved only once.
    /// </summary>
    private IEnumerable<bool> ReadDown(int reg, int size)
    {
        if (size != 4)
        {
            _a[reg] -= Step(reg, size);
            foreach (var c in ReadMemory(_a[reg], size)) yield return c;
            yield break;
        }

        _a[reg] -= 2;
        yield return ReadWord(_a[reg], DataSpace);
        var low = _data;
        _a[reg] -= 2;
        yield return ReadWord(_a[reg], DataSpace);
        _operand = (uint)((_data << 16) | low);
    }

    private IEnumerable<bool> LineB(ushort op, int mode, int reg)
    {
        var rn = (op >> 9) & 7;
        var opmode = (op >> 6) & 7;

        if (opmode is 3 or 7) return Allowed(mode, reg, Modes.All) ? CompareAddress(op, mode, reg) : Illegal();
        if (opmode < 3) return Allowed(mode, reg, Modes.All) && !(opmode == 0 && mode == 1) ? Compare(rn, SizeOf(opmode), mode, reg) : Illegal();
        if (mode == 1) return CompareMemory(op, SizeOf(opmode & 3));
        return Allowed(mode, reg, Modes.DataAlterable) ? ExclusiveOr(rn, SizeOf(opmode & 3), mode, reg) : Illegal();
    }

    private IEnumerable<bool> Compare(int rn, int size, int mode, int reg)
    {
        foreach (var c in Resolve(mode, reg, size)) yield return c;
        foreach (var c in ReadOperand(size)) yield return c;
        Sub(_operand, _d[rn], size, setX: false);
        foreach (var c in Prefetch()) yield return c;
        if (size == 4) yield return Idle(2);
    }

    private IEnumerable<bool> CompareAddress(ushort op, int mode, int reg)
    {
        var size = (op & 0x0100) != 0 ? 4 : 2;
        foreach (var c in Resolve(mode, reg, size)) yield return c;
        foreach (var c in ReadOperand(size)) yield return c;
        Sub(SignExtend(_operand, size), _a[(op >> 9) & 7], 4, setX: false);
        foreach (var c in Prefetch()) yield return c;
        yield return Idle(2);
    }

    private IEnumerable<bool> CompareMemory(ushort op, int size)
    {
        var ry = op & 7;
        var rx = (op >> 9) & 7;
        var source = _a[ry];
        _a[ry] += Step(ry, size);
        foreach (var c in ReadMemory(source, size)) yield return c;
        var s = _operand;
        var destination = _a[rx];
        _a[rx] += Step(rx, size);
        foreach (var c in ReadMemory(destination, size)) yield return c;
        Sub(s, _operand, size, setX: false);
        foreach (var c in Prefetch()) yield return c;
    }

    private IEnumerable<bool> ExclusiveOr(int rn, int size, int mode, int reg)
    {
        foreach (var c in Resolve(mode, reg, size)) yield return c;
        foreach (var c in ReadOperand(size)) yield return c;
        var result = _operand ^ _d[rn];
        LogicFlags(result, size);
        foreach (var c in Prefetch()) yield return c;

        if (_eaKind == EaKind.DataRegister)
        {
            if (size == 4) yield return Idle(4);
            WriteD(reg, result, size);
            yield break;
        }

        foreach (var c in WriteOperand(result, size)) yield return c;
    }

    private IEnumerable<bool> Exchange(ushort op)
    {
        var rx = (op >> 9) & 7;
        var ry = op & 7;
        switch ((op >> 3) & 0x1F)
        {
            case 0x08:
                (_d[rx], _d[ry]) = (_d[ry], _d[rx]);
                break;
            case 0x09:
                (_a[rx], _a[ry]) = (_a[ry], _a[rx]);
                break;
            default:
                (_d[rx], _a[ry]) = (_a[ry], _d[rx]);
                break;
        }

        foreach (var c in Prefetch()) yield return c;
        yield return Idle(2);
    }

    // ---- multiply and divide ---------------------------------------------------------

    private IEnumerable<bool> Multiply(ushort op, int mode, int reg, bool signed)
    {
        foreach (var c in Resolve(mode, reg, 2)) yield return c;
        foreach (var c in ReadOperand(2)) yield return c;
        var source = (ushort)_operand;
        var dn = (op >> 9) & 7;

        uint result;
        int bits;
        if (signed)
        {
            result = (uint)((short)source * (short)_d[dn]);

            // The clocks follow the 01 and 10 pairs in the source, with a zero to its right.
            var pattern = (uint)source << 1;
            bits = 0;
            for (var i = 0; i < 16; i++)
                if ((((pattern >> i) ^ (pattern >> (i + 1))) & 1) != 0) bits++;
        }
        else
        {
            result = (uint)(source * (ushort)_d[dn]);
            bits = System.Numerics.BitOperations.PopCount(source);
        }

        _d[dn] = result;
        LogicFlags(result, 4);
        foreach (var c in Prefetch()) yield return c;
        yield return Idle(34 + 2 * bits);
    }

    private IEnumerable<bool> Divide(ushort op, int mode, int reg, bool signed)
    {
        foreach (var c in Resolve(mode, reg, 2)) yield return c;
        foreach (var c in ReadOperand(2)) yield return c;
        var divisor = (ushort)_operand;
        var dn = (op >> 9) & 7;
        var dividend = _d[dn];

        if (divisor == 0)
        {
            // The trap stacks the address of the instruction after the divide.
            _sr = (ushort)(_sr & ~(FlagN | FlagZ | FlagV | FlagC));
            foreach (var c in Exception(5, _pc, 8)) yield return c;
            yield break;
        }

        int clocks;
        if (signed)
        {
            var negativeDividend = (int)dividend < 0;
            var magnitude = (uint)Math.Abs((long)(int)dividend);
            var by = (uint)Math.Abs((int)(short)divisor);

            // The quotient's magnitude must fit in fifteen bits, and the chip checks that first.
            if (magnitude >= by << 15)
            {
                clocks = negativeDividend ? 18 : 16;
                SetFlag(FlagV, true);
                SetFlag(FlagC, false);
            }
            else
            {
                var quotient = (int)dividend / (short)divisor;
                var remainder = (int)dividend % (short)divisor;
                clocks = DivsClocks(magnitude, by, negativeDividend, (short)divisor < 0);
                _d[dn] = ((uint)(ushort)remainder << 16) | (ushort)quotient;
                _sr = (ushort)((_sr & ~(FlagN | FlagZ | FlagV | FlagC)) | ((short)quotient < 0 ? FlagN : 0) | (quotient == 0 ? FlagZ : 0));
            }
        }
        else
        {
            if ((dividend >> 16) >= divisor)
            {
                clocks = 10;
                SetFlag(FlagV, true);
                SetFlag(FlagC, false);
            }
            else
            {
                var quotient = dividend / divisor;
                var remainder = dividend % divisor;
                clocks = DivuClocks(dividend, divisor);
                _d[dn] = (remainder << 16) | quotient;
                _sr = (ushort)((_sr & ~(FlagN | FlagZ | FlagV | FlagC)) | ((quotient & 0x8000) != 0 ? FlagN : 0) | (quotient == 0 ? FlagZ : 0));
            }
        }

        yield return Idle(clocks - 4);
        foreach (var c in Prefetch()) yield return c;
    }

    /// <summary>
    /// DIVU's clocks, beyond its operand, when it does not overflow: the chip divides a bit at a
    /// time, sixteen times, and of the first fifteen steps one that has to subtract because a bit
    /// came off the top of the remainder saves four clocks, and one that subtracts otherwise saves
    /// two, from 136.
    /// </summary>
    private static int DivuClocks(uint dividend, ushort divisor)
    {
        var remainder = dividend >> 16;
        var low = dividend & 0xFFFF;
        var clocks = 136;
        for (var i = 0; i < 15; i++)
        {
            var carry = (remainder & 0x8000) != 0;
            remainder = ((remainder << 1) | (low >> 15)) & 0x1FFFF;
            low = (low << 1) & 0xFFFF;
            if (remainder >= divisor)
            {
                remainder -= divisor;
                clocks -= carry ? 4 : 2;
            }
        }

        return clocks;
    }

    /// <summary>
    /// DIVS's clocks, beyond its operand, when it does not overflow: the magnitudes are divided as
    /// DIVU would, and each of the first fifteen quotient bits that comes out zero costs two
    /// clocks, on a base set by the signs.
    /// </summary>
    private static int DivsClocks(uint magnitude, uint by, bool negativeDividend, bool negativeDivisor)
    {
        var remainder = magnitude >> 16;
        var low = magnitude & 0xFFFF;
        var clocks = (negativeDividend, negativeDivisor) switch
        {
            (false, false) => 120,
            (false, true) => 122,
            (true, false) => 126,
            _ => 124,
        };

        for (var i = 0; i < 15; i++)
        {
            remainder = ((remainder << 1) | (low >> 15)) & 0x1FFFF;
            low = (low << 1) & 0xFFFF;
            if (remainder >= by) remainder -= by;
            else clocks += 2;
        }

        return clocks;
    }

    // ---- BCD -------------------------------------------------------------------------

    private IEnumerable<bool> Bcd(ushort op, bool subtract)
    {
        var rx = (op >> 9) & 7;
        var ry = op & 7;

        if ((op & 8) == 0)
        {
            var result = subtract ? SubtractBcd(_d[ry] & 0xFF, _d[rx] & 0xFF) : AddBcd(_d[ry] & 0xFF, _d[rx] & 0xFF);
            foreach (var c in Prefetch()) yield return c;
            yield return Idle(2);
            WriteD(rx, result, 1);
            yield break;
        }

        yield return Idle(2);
        _a[ry] -= Step(ry, 1);
        foreach (var c in ReadMemory(_a[ry], 1)) yield return c;
        var source = _operand;
        _a[rx] -= Step(rx, 1);
        foreach (var c in ReadMemory(_a[rx], 1)) yield return c;
        var value = subtract ? SubtractBcd(source, _operand) : AddBcd(source, _operand);
        foreach (var c in Prefetch()) yield return c;
        foreach (var c in WriteMemory(_a[rx], value, 1)) yield return c;
    }

    /// <summary>Destination + source + X in BCD; Z only cleared; N from the result; V as the correction sets bit 7.</summary>
    private uint AddBcd(uint source, uint destination)
    {
        var x = Flag(FlagX) ? 1u : 0u;
        var binary = source + destination + x;
        var low = (source & 0x0F) + (destination & 0x0F) + x;
        var result = binary;
        if (low > 9) result += 6;
        var carry = binary > 0x99;
        if (carry) result += 0x60;

        var r = result & 0xFF;
        var flags = (ushort)(_sr & ~(FlagX | FlagN | FlagV | FlagC) & 0xFFFF);
        if (carry) flags |= FlagX | FlagC;
        if ((r & 0x80) != 0) flags |= FlagN;
        if ((~binary & r & 0x80) != 0) flags |= FlagV;
        if (r != 0) flags &= unchecked((ushort)~FlagZ);
        _sr = flags;
        return r;
    }

    /// <summary>Destination − source − X in BCD, as SBCD and NBCD (from zero) do it.</summary>
    private uint SubtractBcd(uint source, uint destination)
    {
        var x = Flag(FlagX) ? 1u : 0u;
        var binary = (destination - source - x) & 0x1FF;
        var low = (int)(destination & 0x0F) - (int)(source & 0x0F) - (int)x;
        var result = binary;
        if (low < 0) result -= 6;
        var borrow = (int)destination - (int)source - (int)x < 0;
        if (borrow) result -= 0x60;

        var r = result & 0xFF;
        var flags = (ushort)(_sr & ~(FlagX | FlagN | FlagV | FlagC) & 0xFFFF);
        if (borrow || (result & 0x100) != 0) flags |= FlagX | FlagC;
        if ((r & 0x80) != 0) flags |= FlagN;
        if ((binary & ~r & 0x80) != 0) flags |= FlagV;
        if (r != 0) flags &= unchecked((ushort)~FlagZ);
        _sr = flags;
        return r;
    }

    // ---- line E: shifts and rotates ---------------------------------------------------

    private IEnumerable<bool> Shift(ushort op, int mode, int reg)
    {
        var sizeBits = (op >> 6) & 3;
        var left = (op & 0x0100) != 0;

        if (sizeBits == 3)
        {
            if ((op & 0x0800) != 0 || !Allowed(mode, reg, Modes.MemoryAlterable))
            {
                foreach (var c in Illegal()) yield return c;
                yield break;
            }

            foreach (var c in Resolve(mode, reg, 2)) yield return c;
            foreach (var c in ReadOperand(2)) yield return c;
            var result = ShiftValue((op >> 9) & 3, left, _operand, 1, 2);
            foreach (var c in Prefetch()) yield return c;
            foreach (var c in WriteMemory(_ea, result, 2)) yield return c;
            yield break;
        }

        var size = SizeOf(sizeBits);
        var countField = (op >> 9) & 7;
        var count = (op & 0x0020) != 0 ? (int)(_d[countField] & 63) : countField == 0 ? 8 : countField;
        var value = ShiftValue((op >> 3) & 3, left, _d[reg] & Mask(size), count, size);
        WriteD(reg, value, size);
        foreach (var c in Prefetch()) yield return c;
        yield return Idle((size == 4 ? 4 : 2) + 2 * count);
    }

    /// <summary>One of the eight shifts and rotates, by <paramref name="count"/>, setting the flags as it goes.</summary>
    private uint ShiftValue(int type, bool left, uint value, int count, int size)
    {
        var mask = Mask(size);
        var msb = Msb(size);
        var bits = size * 8;
        value &= mask;
        var flags = (ushort)(_sr & FlagX);
        var c = false;
        var v = false;

        if (count == 0)
        {
            // No shift: C clears, except ROXL and ROXR, which copy X into it.
            if (type == 2) c = Flag(FlagX);
        }
        else
        {
            for (var i = 0; i < count; i++)
            {
                var top = (value & msb) != 0;
                var bottom = (value & 1) != 0;
                switch (type)
                {
                    case 0:
                        if (left)
                        {
                            c = top;
                            value = (value << 1) & mask;
                            if (((value & msb) != 0) != top) v = true;
                        }
                        else
                        {
                            c = bottom;
                            value = (value >> 1) | (top ? msb : 0);
                        }

                        flags = (ushort)(c ? FlagX : 0);
                        break;

                    case 1:
                        c = left ? top : bottom;
                        value = left ? (value << 1) & mask : value >> 1;
                        flags = (ushort)(c ? FlagX : 0);
                        break;

                    case 2:
                    {
                        var x = (flags & FlagX) != 0;
                        c = left ? top : bottom;
                        value = left ? ((value << 1) & mask) | (x ? 1u : 0) : (value >> 1) | (x ? msb : 0);
                        flags = (ushort)(c ? FlagX : 0);
                        break;
                    }

                    default:
                        c = left ? top : bottom;
                        value = left ? ((value << 1) & mask) | (top ? 1u : 0) : (value >> 1) | (bottom ? msb : 0);
                        break;
                }
            }

            if (bits == 0) c = false;
        }

        if (type == 3) flags = (ushort)(_sr & FlagX);
        flags |= (ushort)(c ? FlagC : 0);
        if (v) flags |= FlagV;
        if ((value & msb) != 0) flags |= FlagN;
        if (value == 0) flags |= FlagZ;
        _sr = (ushort)((_sr & ~0x1F) | flags);
        return value;
    }
}
