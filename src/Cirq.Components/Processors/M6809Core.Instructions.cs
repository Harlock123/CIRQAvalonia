namespace Cirq.Components.Processors;

/// <summary>The 6809's addressing modes and instructions, cycle by cycle.</summary>
public sealed partial class M6809Core
{
    // ---- addressing -------------------------------------------------------

    /// <summary>
    /// The cycles that work out a direct, extended or indexed effective address into
    /// <see cref="_ea"/>: everything up to (not including) the cycle that uses it. A direct address
    /// is DP and one byte, then a dead cycle; an extended one two bytes and a dead cycle.
    /// </summary>
    private IEnumerable<bool> Resolve(M6809Mode mode)
    {
        switch (mode)
        {
            case M6809Mode.Direct:
                yield return Read(PC++);
                _ea = (DP << 8) | _data;
                yield return Dead();
                break;

            case M6809Mode.Extended:
            {
                yield return Read(PC++);
                var high = _data;
                yield return Read(PC++);
                _ea = (high << 8) | _data;
                yield return Dead();
                break;
            }

            default:
                foreach (var c in Indexed()) yield return c;
                break;
        }
    }

    private ushort IndexRegister(int code) => code switch
    {
        0 => X,
        1 => Y,
        2 => U,
        _ => S,
    };

    private void SetIndexRegister(int code, int value)
    {
        var v = (ushort)value;
        switch (code)
        {
            case 0: X = v; break;
            case 1: Y = v; break;
            case 2: U = v; break;
            default: S = v; break;
        }
    }

    /// <summary>
    /// An indexed address: the postbyte, then the cycles its mode costs — the programming manual's
    /// extra cycles, each one a bus cycle. After the postbyte comes a read of the next byte: the
    /// offset where there is one, a don't-care read where there is not. Then ,R is done; a 5-bit
    /// offset, A,R and B,R take a dead cycle; an 8-bit offset reads the byte after it; an 8-bit PC
    /// offset takes a dead cycle; ,R+ and ,-R two dead cycles, ,R++ and ,--R three; a 16-bit offset
    /// three after its second byte, a 16-bit PC offset four, [n] one; D,R reads the next byte too
    /// and takes three. Indirection then reads the pointer, high byte first, and takes a dead cycle.
    /// The undefined postbytes ($x7, $xA, $xE) address $0000 with no cycles at all.
    /// </summary>
    private IEnumerable<bool> Indexed()
    {
        yield return Read(PC++);
        var post = _data;
        var reg = (post >> 5) & 3;
        var r = IndexRegister(reg);

        if ((post & 0x80) == 0)
        {
            // A five-bit signed offset.
            var offset = (post & 0x10) != 0 ? (post & 0x0F) - 16 : post & 0x0F;
            yield return Read(PC);
            yield return Dead();
            _ea = (r + offset) & 0xFFFF;
            yield break;
        }

        var indirect = (post & 0x10) != 0;
        switch (post & 0x0F)
        {
            case 0x00:
                // ,R+ (and, with the indirect bit, the undefined [,R+], read the same way here).
                yield return Read(PC);
                yield return Dead();
                yield return Dead();
                _ea = r;
                SetIndexRegister(reg, r + 1);
                break;

            case 0x01:
                yield return Read(PC);
                yield return Dead();
                yield return Dead();
                yield return Dead();
                _ea = r;
                SetIndexRegister(reg, r + 2);
                break;

            case 0x02:
                yield return Read(PC);
                yield return Dead();
                yield return Dead();
                _ea = (r - 1) & 0xFFFF;
                SetIndexRegister(reg, _ea);
                break;

            case 0x03:
                yield return Read(PC);
                yield return Dead();
                yield return Dead();
                yield return Dead();
                _ea = (r - 2) & 0xFFFF;
                SetIndexRegister(reg, _ea);
                break;

            case 0x04:
                yield return Read(PC);
                _ea = r;
                break;

            case 0x05:
                yield return Read(PC);
                yield return Dead();
                _ea = (r + (sbyte)B) & 0xFFFF;
                break;

            case 0x06:
                yield return Read(PC);
                yield return Dead();
                _ea = (r + (sbyte)A) & 0xFFFF;
                break;

            case 0x08:
                yield return Read(PC++);
                _ea = (r + (sbyte)_data) & 0xFFFF;
                yield return Read(PC);
                break;

            case 0x09:
            {
                yield return Read(PC++);
                var high = _data;
                yield return Read(PC++);
                _ea = (r + (short)((high << 8) | _data)) & 0xFFFF;
                yield return Dead();
                yield return Dead();
                yield return Dead();
                break;
            }

            case 0x0B:
                yield return Read(PC);
                yield return Read(PC + 1);
                yield return Dead();
                yield return Dead();
                yield return Dead();
                _ea = (r + D) & 0xFFFF;
                break;

            case 0x0C:
                yield return Read(PC++);
                _ea = (PC + (sbyte)_data) & 0xFFFF;
                yield return Dead();
                break;

            case 0x0D:
            {
                yield return Read(PC++);
                var high = _data;
                yield return Read(PC++);
                _ea = (PC + (short)((high << 8) | _data)) & 0xFFFF;
                yield return Dead();
                yield return Dead();
                yield return Dead();
                yield return Dead();
                break;
            }

            case 0x0F:
            {
                // [n]: an extended address to be followed, whatever the register bits say. Without the
                // indirect bit ($8F) it is undefined, and taken as the extended address itself.
                yield return Read(PC++);
                var high = _data;
                yield return Read(PC++);
                _ea = (high << 8) | _data;
                yield return Dead();
                break;
            }

            default:
                // $x7, $xA and $xE are undefined: the address is $0000, at once.
                _ea = 0;
                break;
        }

        if (indirect)
        {
            _busyNext = true;
            yield return Read(_ea);
            var high = _data;
            yield return Read((_ea + 1) & 0xFFFF);
            _ea = (high << 8) | _data;
            yield return Dead();
        }
    }

    /// <summary>An 8-bit operand: immediate, or from the effective address.</summary>
    private IEnumerable<bool> Operand8(M6809Mode mode)
    {
        if (mode == M6809Mode.Immediate8)
        {
            yield return Read(PC++);
            yield break;
        }

        foreach (var c in Resolve(mode)) yield return c;
        yield return Read(_ea);
    }

    /// <summary>A 16-bit operand into <see cref="_word"/>: immediate, or two reads from the effective address.</summary>
    private IEnumerable<bool> Operand16(M6809Mode mode)
    {
        if (mode is M6809Mode.Immediate16 or M6809Mode.Immediate8)
        {
            yield return Read(PC++);
            var high = _data;
            yield return Read(PC++);
            _word = (ushort)((high << 8) | _data);
            yield break;
        }

        foreach (var c in Resolve(mode)) yield return c;
        _busyNext = true;
        yield return Read(_ea);
        var hi = _data;
        yield return Read((_ea + 1) & 0xFFFF);
        _word = (ushort)((hi << 8) | _data);
    }

    private ushort _word;

    // ---- the flags -------------------------------------------------------------

    private void Nz8(int value)
    {
        Flag(FlagN, (value & 0x80) != 0);
        Flag(FlagZ, (value & 0xFF) == 0);
    }

    private void Nz16(int value)
    {
        Flag(FlagN, (value & 0x8000) != 0);
        Flag(FlagZ, (value & 0xFFFF) == 0);
    }

    /// <summary>A load, store or logical result: N and Z from it, V cleared.</summary>
    private byte Logic8(int value)
    {
        Nz8(value);
        Flag(FlagV, false);
        return (byte)value;
    }

    private ushort Logic16(int value)
    {
        Nz16(value);
        Flag(FlagV, false);
        return (ushort)value;
    }

    private byte Sub8(byte a, byte m, int borrow = 0)
    {
        var r = a - m - borrow;
        Nz8(r);
        Flag(FlagV, ((a ^ m) & (a ^ r) & 0x80) != 0);
        Flag(FlagC, (r & 0x100) != 0);
        return (byte)r;
    }

    private byte Add8(byte a, byte m, int carry = 0)
    {
        var r = a + m + carry;
        Nz8(r);
        Flag(FlagH, ((a ^ m ^ r) & 0x10) != 0);
        Flag(FlagV, ((a ^ r) & (m ^ r) & 0x80) != 0);
        Flag(FlagC, (r & 0x100) != 0);
        return (byte)r;
    }

    private ushort Sub16(ushort a, ushort m)
    {
        var r = a - m;
        Nz16(r);
        Flag(FlagV, ((a ^ m) & (a ^ r) & 0x8000) != 0);
        Flag(FlagC, (r & 0x10000) != 0);
        return (ushort)r;
    }

    private ushort Add16(ushort a, ushort m)
    {
        var r = a + m;
        Nz16(r);
        Flag(FlagV, ((a ^ r) & (m ^ r) & 0x8000) != 0);
        Flag(FlagC, (r & 0x10000) != 0);
        return (ushort)r;
    }

    /// <summary>
    /// The read-modify-write operations by their column: NEG, COM, LSR, ROR, ASR, ASL, ROL, DEC,
    /// INC, TST and CLR, with the undocumented columns 1, 2, 5 and B as NEG, NEG-or-COM, LSR and DEC.
    /// </summary>
    private byte Modify(int column, byte v)
    {
        switch (column)
        {
            case 0x0 or 0x1:
            {
                var r = Sub8(0, v);
                return r;
            }

            case 0x2:
                return Carry ? Modify(0x3, v) : Modify(0x0, v);

            case 0x3:
            {
                var r = (byte)~v;
                Logic8(r);
                Flag(FlagC, true);
                return r;
            }

            case 0x4 or 0x5:
            {
                Flag(FlagC, (v & 1) != 0);
                var r = (byte)(v >> 1);
                Nz8(r);
                return r;
            }

            case 0x6:
            {
                var r = (byte)((v >> 1) | (Carry ? 0x80 : 0));
                Flag(FlagC, (v & 1) != 0);
                Nz8(r);
                return r;
            }

            case 0x7:
            {
                Flag(FlagC, (v & 1) != 0);
                var r = (byte)((v & 0x80) | (v >> 1));
                Nz8(r);
                return r;
            }

            case 0x8:
            {
                var r = (byte)(v << 1);
                Flag(FlagC, (v & 0x80) != 0);
                Flag(FlagV, ((v ^ (v << 1)) & 0x80) != 0);
                Nz8(r);
                return r;
            }

            case 0x9:
            {
                var r = (byte)((v << 1) | (Carry ? 1 : 0));
                Flag(FlagC, (v & 0x80) != 0);
                Flag(FlagV, ((v ^ (v << 1)) & 0x80) != 0);
                Nz8(r);
                return r;
            }

            case 0xA or 0xB:
            {
                // The undocumented column B also sets C unless the operand was zero.
                var r = (byte)(v - 1);
                Flag(FlagV, v == 0x80);
                Nz8(r);
                if (column == 0xB) Flag(FlagC, v != 0);
                return r;
            }

            case 0xC:
            {
                var r = (byte)(v + 1);
                Flag(FlagV, v == 0x7F);
                Nz8(r);
                return r;
            }

            case 0xD:
                Logic8(v);
                return v;

            default:
                Nz8(0);
                Flag(FlagV, false);
                Flag(FlagC, false);
                return 0;
        }
    }

    /// <summary>The undocumented CLRA and CLRB at $4E and $5E: a clear that leaves C alone.</summary>
    private byte ClearKeepingCarry()
    {
        Nz8(0);
        Flag(FlagV, false);
        return 0;
    }

    /// <summary>The eight-bit ALU column of the $80-$FF rows: SUB, CMP, SBC, AND, BIT, LD, EOR, ADC, OR, ADD.</summary>
    private byte Alu8(int column, byte r, byte m)
    {
        switch (column)
        {
            case 0x0: return Sub8(r, m);
            case 0x1: Sub8(r, m); return r;
            case 0x2: return Sub8(r, m, Carry ? 1 : 0);
            case 0x4: return Logic8(r & m);
            case 0x5: Logic8(r & m); return r;
            case 0x6: return Logic8(m);
            case 0x8: return Logic8(r ^ m);
            case 0x9: return Add8(r, m, Carry ? 1 : 0);
            case 0xA: return Logic8(r | m);
            default: return Add8(r, m);
        }
    }

    private bool Condition(int code) => code switch
    {
        0x0 => true,
        0x1 => false,
        0x2 => (CC & (FlagC | FlagZ)) == 0,
        0x3 => (CC & (FlagC | FlagZ)) != 0,
        0x4 => (CC & FlagC) == 0,
        0x5 => (CC & FlagC) != 0,
        0x6 => (CC & FlagZ) == 0,
        0x7 => (CC & FlagZ) != 0,
        0x8 => (CC & FlagV) == 0,
        0x9 => (CC & FlagV) != 0,
        0xA => (CC & FlagN) == 0,
        0xB => (CC & FlagN) != 0,
        0xC => ((CC & FlagN) != 0) == ((CC & FlagV) != 0),
        0xD => ((CC & FlagN) != 0) != ((CC & FlagV) != 0),
        0xE => (CC & FlagZ) == 0 && ((CC & FlagN) != 0) == ((CC & FlagV) != 0),
        _ => (CC & FlagZ) != 0 || ((CC & FlagN) != 0) != ((CC & FlagV) != 0),
    };

    private static M6809Mode RowMode(byte opcode) => (opcode >> 4) switch
    {
        0x0 or 0x9 or 0xD => M6809Mode.Direct,
        0x6 or 0xA or 0xE => M6809Mode.Indexed,
        0x7 or 0xB or 0xF => M6809Mode.Extended,
        _ => M6809Mode.Immediate8,
    };

    // ---- execution -------------------------------------------------------------

    private IEnumerable<bool> Execute(int page, byte opcode)
    {
        if (page == 0x10)
        {
            switch (opcode)
            {
                case >= 0x20 and <= 0x2F: return LongBranch(opcode & 0x0F, opcode == 0x20);
                case 0x3F: return SoftwareInterrupt(2);
                case 0x3E: return SoftwareInterrupt(5);
                case 0x83 or 0x93 or 0xA3 or 0xB3: return Compare16(RowMode(opcode), () => D);
                case 0x8C or 0x9C or 0xAC or 0xBC: return Compare16(RowMode(opcode), () => Y);
                case 0x8E or 0x9E or 0xAE or 0xBE: return Load16(RowMode(opcode), v => Y = v);
                case 0x8F or 0x9F or 0xAF or 0xBF: return Store16(RowMode(opcode), () => Y);
                case 0xC3 or 0xD3 or 0xE3 or 0xF3: return Arithmetic16(RowMode(opcode), m => Add16(D, m));
                case 0xCE or 0xDE or 0xEE or 0xFE: return Load16(RowMode(opcode), v => { S = v; NmiArmed = true; });
                case 0xCF or 0xDF or 0xEF or 0xFF: return Store16(RowMode(opcode), () => S);
            }
        }
        else if (page == 0x11)
        {
            switch (opcode)
            {
                case 0x3F: return SoftwareInterrupt(3);
                case 0x3E: return SoftwareInterrupt(4);
                case 0x83 or 0x93 or 0xA3 or 0xB3: return Compare16(RowMode(opcode), () => U);
                case 0x8C or 0x9C or 0xAC or 0xBC: return Compare16(RowMode(opcode), () => S);
                case 0xC3 or 0xD3 or 0xE3 or 0xF3: return Arithmetic16(RowMode(opcode), m => Add16(U, m));
            }
        }

        return Page1(opcode);
    }

    private IEnumerable<bool> Page1(byte opcode)
    {
        var column = opcode & 0x0F;
        switch (opcode >> 4)
        {
            case 0x0 or 0x6 or 0x7:
                return column switch
                {
                    0xE => Jump(RowMode(opcode)),
                    _ => ModifyMemory(RowMode(opcode), column),
                };

            case 0x4 when column == 0xD:
                return InherentDead(() => Modify(column, A));
            case 0x5 when column == 0xD:
                return InherentDead(() => Modify(column, B));
            case 0x4:
                return Inherent(() => A = column == 0xE ? ClearKeepingCarry() : Modify(column, A));
            case 0x5:
                return Inherent(() => B = column == 0xE ? ClearKeepingCarry() : Modify(column, B));

            case 0x1: return Row1(opcode);
            case 0x2: return Branch(column);
            case 0x3: return Row3(opcode);

            case 0x8 or 0x9 or 0xA or 0xB:
                return column switch
                {
                    0x3 => Arithmetic16(RowMode(opcode), m => D = Sub16(D, m)),
                    0x7 => Store8(RowMode(opcode), () => A),
                    0xC => Compare16(RowMode(opcode), () => X),
                    0xD => opcode == 0x8D ? BranchToSubroutine() : JumpToSubroutine(RowMode(opcode)),
                    0xE => Load16(RowMode(opcode), v => X = v),
                    0xF => Store16(RowMode(opcode), () => X),
                    _ => Operate8(RowMode(opcode), column, () => A, v => A = v),
                };

            default:
                return column switch
                {
                    0x3 => Arithmetic16(RowMode(opcode), m => D = Add16(D, m)),
                    0x7 => Store8(RowMode(opcode), () => B),
                    0xC => Load16(RowMode(opcode), v => D = v),
                    0xD => opcode == 0xCD ? Stop() : Store16(RowMode(opcode), () => D),
                    0xE => Load16(RowMode(opcode), v => U = v),
                    0xF => Store16(RowMode(opcode), () => U),
                    _ => Operate8(RowMode(opcode), column, () => B, v => B = v),
                };
        }
    }

    private IEnumerable<bool> Row1(byte opcode) => opcode switch
    {
        0x12 or 0x1B => Inherent(() => { }),
        0x13 => Sync(),
        0x14 or 0x15 => Stop(),
        0x16 => LongBranch(0, true),
        0x17 => LongBranchToSubroutine(),
        0x18 => Inherent(Undocumented18),
        0x19 => Inherent(DecimalAdjust),
        0x1A => ChangeCc(v => CC |= v),
        0x1C => ChangeCc(v => CC &= v),
        0x1D => Inherent(() => { A = (B & 0x80) != 0 ? (byte)0xFF : (byte)0; Nz16(D); }),
        0x1E => Exchange(),
        _ => Transfer(),
    };

    private IEnumerable<bool> Row3(byte opcode) => opcode switch
    {
        0x30 => LoadEffective(v => { X = v; Flag(FlagZ, v == 0); }),
        0x31 => LoadEffective(v => { Y = v; Flag(FlagZ, v == 0); }),
        0x32 => LoadEffective(v => { S = v; NmiArmed = true; }),
        0x33 => LoadEffective(v => U = v),
        0x34 => PushRegisters(useU: false),
        0x35 => PullRegisters(useU: false),
        0x36 => PushRegisters(useU: true),
        0x37 => PullRegisters(useU: true),
        0x38 => ChangeCc(v => CC &= v, extraDead: true),
        0x39 => ReturnFromSubroutine(),
        0x3A => Abx(),
        0x3B => ReturnFromInterrupt(),
        0x3C => WaitForInterrupt(),
        0x3D => Multiply(),
        0x3E => SoftwareInterrupt(0),
        _ => SoftwareInterrupt(1),
    };

    /// <summary>A one-byte instruction: the don't-care read of the next byte, and the change.</summary>
    private IEnumerable<bool> Inherent(Action change)
    {
        yield return Read(PC);
        change();
    }

    /// <summary>TSTA and TSTB: their second cycle is dead rather than a read of the next byte.</summary>
    private IEnumerable<bool> InherentDead(Action change)
    {
        yield return Dead();
        change();
    }

    private IEnumerable<bool> ModifyMemory(M6809Mode mode, int column)
    {
        foreach (var c in Resolve(mode)) yield return c;
        _busyNext = true;
        yield return Read(_ea);
        var value = _data;

        if (column == 0xD)
        {
            // TST reads, and spends the other two cycles dead.
            yield return Dead();
            Modify(column, value);
            yield return Dead();
            yield break;
        }

        _busyNext = true;
        yield return Read(PC);

        yield return Write(_ea, Modify(column, value));
    }

    private IEnumerable<bool> Jump(M6809Mode mode)
    {
        foreach (var c in Resolve(mode)) yield return c;
        PC = (ushort)_ea;
    }

    private IEnumerable<bool> Operate8(M6809Mode mode, int column, Func<byte> register, Action<byte> set)
    {
        foreach (var c in Operand8(mode)) yield return c;
        var result = Alu8(column, register(), _data);
        set(result);
    }

    private IEnumerable<bool> Store8(M6809Mode mode, Func<byte> value)
    {
        if (mode == M6809Mode.Immediate8)
        {
            // Undocumented: the "immediate" store reads its operand byte, sets N and Z from the
            // register and clears V, and writes nothing.
            yield return Read(PC++);
            Logic8(value());
            yield break;
        }

        foreach (var c in Resolve(mode)) yield return c;
        yield return Write(_ea, Logic8(value()));
    }

    private IEnumerable<bool> Load16(M6809Mode mode, Action<ushort> set)
    {
        if (mode == M6809Mode.Immediate8) mode = M6809Mode.Immediate16;
        foreach (var c in Operand16(mode)) yield return c;
        set(Logic16(_word));
    }

    private IEnumerable<bool> Store16(M6809Mode mode, Func<ushort> value)
    {
        if (mode == M6809Mode.Immediate8)
        {
            // Undocumented: the "immediate" store writes its low byte over the instruction's last byte.
            yield return Read(PC++);
            var v = Logic16(value());
            yield return Write(PC++, (byte)v);
            yield break;
        }

        foreach (var c in Resolve(mode)) yield return c;
        var w = Logic16(value());
        _busyNext = true;
        yield return Write(_ea, (byte)(w >> 8));
        yield return Write((_ea + 1) & 0xFFFF, (byte)w);
    }

    /// <summary>SUBD, ADDD, ADDU: the operand, then a dead cycle for the 16-bit sum.</summary>
    private IEnumerable<bool> Arithmetic16(M6809Mode mode, Action<ushort> operate)
    {
        if (mode == M6809Mode.Immediate8) mode = M6809Mode.Immediate16;
        foreach (var c in Operand16(mode)) yield return c;
        yield return Read(PC);
        operate(_word);
    }

    private IEnumerable<bool> Compare16(M6809Mode mode, Func<ushort> register) =>
        Arithmetic16(mode, m => Sub16(register(), m));

    private IEnumerable<bool> JumpToSubroutine(M6809Mode mode)
    {
        foreach (var c in Resolve(mode)) yield return c;
        yield return Read(PC);
        yield return Dead();
        yield return PushS((byte)PC);
        yield return PushS((byte)(PC >> 8));
        PC = (ushort)_ea;
    }

    private IEnumerable<bool> BranchToSubroutine()
    {
        yield return Read(PC++);
        var target = (PC + (sbyte)_data) & 0xFFFF;
        yield return Dead();
        yield return Dead();
        yield return Dead();
        yield return PushS((byte)PC);
        yield return PushS((byte)(PC >> 8));
        PC = (ushort)target;
    }

    private IEnumerable<bool> LongBranchToSubroutine()
    {
        yield return Read(PC++);
        var high = _data;
        yield return Read(PC++);
        var target = (PC + (short)((high << 8) | _data)) & 0xFFFF;
        yield return Dead();
        yield return Dead();
        yield return Dead();
        yield return Dead();
        yield return PushS((byte)PC);
        yield return PushS((byte)(PC >> 8));
        PC = (ushort)target;
    }

    private IEnumerable<bool> Branch(int condition)
    {
        yield return Read(PC++);
        var offset = (sbyte)_data;
        yield return Dead();
        if (Condition(condition)) PC = (ushort)(PC + offset);
    }

    /// <summary>LBRA (page 1, always five cycles) and the page 2 long branches: a dead cycle more when taken.</summary>
    private IEnumerable<bool> LongBranch(int condition, bool always)
    {
        yield return Read(PC++);
        var high = _data;
        yield return Read(PC++);
        var offset = (short)((high << 8) | _data);
        yield return Dead();
        if (always || Condition(condition))
        {
            yield return Dead();
            PC = (ushort)(PC + offset);
        }
    }

    private IEnumerable<bool> LoadEffective(Action<ushort> set)
    {
        foreach (var c in Indexed()) yield return c;
        yield return Dead();
        set((ushort)_ea);
    }

    /// <summary>ORCC and ANDCC: the operand and a dead cycle (four cycles for the undocumented $38).</summary>
    private IEnumerable<bool> ChangeCc(Action<byte> change, bool extraDead = false)
    {
        if (extraDead) yield return Dead();
        yield return Read(PC++);
        var value = _data;
        yield return Read(PC);
        change(value);
    }

    private IEnumerable<bool> Abx()
    {
        yield return Read(PC);
        yield return Dead();
        X = (ushort)(X + B);
    }

    private IEnumerable<bool> Multiply()
    {
        yield return Read(PC);
        for (var i = 0; i < 9; i++) yield return Dead();
        D = (ushort)(A * B);
        Flag(FlagZ, D == 0);
        Flag(FlagC, (B & 0x80) != 0);
    }

    /// <summary>
    /// The undocumented $18: CC becomes CC ANDed with the byte after the opcode, shifted left one,
    /// with V set too if Z was — how the published per-instruction suite has it.
    /// </summary>
    private void Undocumented18()
    {
        var r = ((CC & _data) << 1) & 0xFC;
        if ((CC & _data & FlagC) != 0 || (CC & FlagZ) != 0) r |= FlagV;
        CC = (byte)r;
    }

    private void DecimalAdjust()
    {
        var low = A & 0x0F;
        var high = A & 0xF0;
        var correction = 0;
        if ((CC & FlagH) != 0 || low > 9) correction |= 0x06;
        if (Carry || high > 0x90 || (high > 0x80 && low > 9)) correction |= 0x60;

        var r = A + correction;
        Flag(FlagC, Carry || (r & 0x100) != 0);
        A = (byte)r;
        Nz8(A);
        Flag(FlagV, false);
    }

    // ---- TFR and EXG -----------------------------------------------------------

    /// <summary>A register by its TFR/EXG code. The undefined codes read as all ones.</summary>
    private int RegisterValue(int code) => code switch
    {
        0x0 => D,
        0x1 => X,
        0x2 => Y,
        0x3 => U,
        0x4 => S,
        0x5 => PC,
        0x8 => A,
        0x9 => B,
        0xA => CC,
        0xB => DP,
        _ => code < 8 ? 0xFFFF : 0xFF,
    };

    private void SetRegister(int code, int value)
    {
        switch (code)
        {
            case 0x0: D = (ushort)value; break;
            case 0x1: X = (ushort)value; break;
            case 0x2: Y = (ushort)value; break;
            case 0x3: U = (ushort)value; break;
            case 0x4:
                S = (ushort)value;
                NmiArmed = true;
                break;
            case 0x5: PC = (ushort)value; break;
            case 0x8: A = (byte)value; break;
            case 0x9: B = (byte)value; break;
            case 0xA: CC = (byte)value; break;
            case 0xB: DP = (byte)value; break;
        }
    }

    /// <summary>
    /// A register's value as it arrives in another: between registers of one size, itself; from an
    /// 8-bit one into a 16-bit one, in the low byte with $FF above it (CC and DP copied into both
    /// halves); from a 16-bit one into an 8-bit one, the low byte.
    /// </summary>
    private int Moved(int from, int to)
    {
        var value = RegisterValue(from);
        var wideFrom = from < 8;
        var wideTo = to < 8;
        if (wideFrom == wideTo) return value;
        if (wideTo) return 0xFF00 | value;
        return value & 0xFF;
    }

    private IEnumerable<bool> Transfer()
    {
        yield return Read(PC++);
        var post = _data;
        for (var i = 0; i < 4; i++) yield return Dead();
        SetRegister(post & 0x0F, Moved(post >> 4, post & 0x0F));
    }

    private IEnumerable<bool> Exchange()
    {
        yield return Read(PC++);
        var post = _data;
        for (var i = 0; i < 6; i++) yield return Dead();
        int first = post >> 4, second = post & 0x0F;
        var toSecond = Moved(first, second);
        var toFirst = Moved(second, first);
        SetRegister(second, toSecond);
        SetRegister(first, toFirst);
    }

    // ---- the stacks ------------------------------------------------------------

    /// <summary>PSHS or PSHU: five cycles and one a byte, PC first, CC last.</summary>
    private IEnumerable<bool> PushRegisters(bool useU)
    {
        yield return Read(PC++);
        var post = _data;
        yield return Dead();
        yield return Dead();
        yield return Read(useU ? U : S);

        bool Push(byte v) => useU ? PushU(v) : PushS(v);
        var other = useU ? S : U;
        if ((post & 0x80) != 0) { yield return Push((byte)PC); yield return Push((byte)(PC >> 8)); }
        if ((post & 0x40) != 0) { yield return Push((byte)other); yield return Push((byte)(other >> 8)); }
        if ((post & 0x20) != 0) { yield return Push((byte)Y); yield return Push((byte)(Y >> 8)); }
        if ((post & 0x10) != 0) { yield return Push((byte)X); yield return Push((byte)(X >> 8)); }
        if ((post & 0x08) != 0) yield return Push(DP);
        if ((post & 0x04) != 0) yield return Push(B);
        if ((post & 0x02) != 0) yield return Push(A);
        if ((post & 0x01) != 0) yield return Push(CC);
    }

    /// <summary>PULS or PULU: five cycles and one a byte, CC first, PC last.</summary>
    private IEnumerable<bool> PullRegisters(bool useU)
    {
        yield return Read(PC++);
        var post = _data;
        yield return Dead();
        yield return Dead();

        int pointer = useU ? U : S;

        IEnumerable<bool> Pull8(Action<byte> set)
        {
            yield return Read(pointer);
            pointer = (pointer + 1) & 0xFFFF;
            set(_data);
            if (useU) U = (ushort)pointer;
            else S = (ushort)pointer;
        }

        IEnumerable<bool> Pull16(Action<ushort> set)
        {
            yield return Read(pointer);
            pointer = (pointer + 1) & 0xFFFF;
            var high = _data;
            if (useU) U = (ushort)pointer;
            else S = (ushort)pointer;
            yield return Read(pointer);
            pointer = (pointer + 1) & 0xFFFF;
            if (useU) U = (ushort)pointer;
            else S = (ushort)pointer;
            set((ushort)((high << 8) | _data));
        }

        if ((post & 0x01) != 0) foreach (var c in Pull8(v => CC = v)) yield return c;
        if ((post & 0x02) != 0) foreach (var c in Pull8(v => A = v)) yield return c;
        if ((post & 0x04) != 0) foreach (var c in Pull8(v => B = v)) yield return c;
        if ((post & 0x08) != 0) foreach (var c in Pull8(v => DP = v)) yield return c;
        if ((post & 0x10) != 0) foreach (var c in Pull16(v => X = v)) yield return c;
        if ((post & 0x20) != 0) foreach (var c in Pull16(v => Y = v)) yield return c;
        if ((post & 0x40) != 0)
        {
            foreach (var c in Pull16(v =>
                     {
                         if (useU)
                         {
                             S = v;
                             NmiArmed = true;
                         }
                         else
                         {
                             U = v;
                         }
                     }))
                yield return c;
        }

        if ((post & 0x80) != 0) foreach (var c in Pull16(v => PC = v)) yield return c;
        yield return Read(pointer);
    }

    private IEnumerable<bool> ReturnFromSubroutine()
    {
        yield return Read(PC);
        yield return Read(S);
        var high = _data;
        S++;
        yield return Read(S);
        var low = _data;
        S++;
        yield return Read(S);
        PC = (ushort)((high << 8) | low);
    }

    private IEnumerable<bool> ReturnFromInterrupt()
    {
        yield return Read(PC);

        byte Pull()
        {
            var v = _data;
            S++;
            return v;
        }

        yield return Read(S);
        CC = Pull();
        if ((CC & FlagE) != 0)
        {
            yield return Read(S);
            A = Pull();
            yield return Read(S);
            B = Pull();
            yield return Read(S);
            DP = Pull();
            yield return Read(S);
            var xh = Pull();
            yield return Read(S);
            X = (ushort)((xh << 8) | Pull());
            yield return Read(S);
            var yh = Pull();
            yield return Read(S);
            Y = (ushort)((yh << 8) | Pull());
            yield return Read(S);
            var uh = Pull();
            yield return Read(S);
            U = (ushort)((uh << 8) | Pull());
        }

        yield return Read(S);
        var ph = Pull();
        yield return Read(S);
        PC = (ushort)((ph << 8) | Pull());
        yield return Read(S);
    }

    // ---- interrupts in software, and waiting for them ---------------------------

    /// <summary>
    /// SWI (1), SWI2 (2) and SWI3 (3): E set, the entire state stacked, the vector taken; SWI also
    /// masks IRQ and FIRQ. The undocumented $3E (0), page 3 $3E (4) and page 2 $3E (5) stack the
    /// entire state the same way and take the RESET, FIRQ and SWI2 vectors, but leave E and the
    /// masks as they were — so the CC they stack has E as the program left it.
    /// </summary>
    private IEnumerable<bool> SoftwareInterrupt(int which)
    {
        yield return Read(PC);
        yield return Dead();
        var documented = which is 1 or 2 or 3;
        if (documented) Flag(FlagE, true);
        foreach (var c in Stack(entire: true)) yield return c;

        if (which == 1) CC |= FlagI | FlagF;
        LastInterrupt = which switch { 0 => "RESET", 1 => "SWI", 2 or 5 => "SWI2", 3 => "SWI3", _ => "FIRQ" };
        var vector = which switch { 0 => VectorReset, 1 => VectorSwi, 2 or 5 => VectorSwi2, 3 => VectorSwi3, _ => VectorFirq };
        foreach (var c in Vectored(vector)) yield return c;
    }

    /// <summary>
    /// SYNC: the don't-care read, then the buses let go (BA high, BS low) until an interrupt line is
    /// asserted, masked or not, then a dead cycle. A masked one carries on with the next instruction;
    /// an unmasked one is taken as the next instruction would have been.
    /// </summary>
    private IEnumerable<bool> Sync()
    {
        yield return Read(PC);
        IsSyncing = true;
        do
        {
            yield return Post(0xFFFF, true, 0, M6809CycleKind.Released, ba: true);
        }
        while (!(_nmiLatched || IrqAsserted || FirqAsserted || _resetHeld));

        IsSyncing = false;
        yield return Dead();
    }

    /// <summary>
    /// CWAI: CC ANDed with the operand, E set, the entire state stacked, then dead cycles until an
    /// interrupt the mask lets through; it is taken with no further stacking (FIRQ included).
    /// </summary>
    private IEnumerable<bool> WaitForInterrupt()
    {
        yield return Read(PC++);
        CC &= _data;
        yield return Dead();
        Flag(FlagE, true);
        foreach (var c in Stack(entire: true)) yield return c;
        yield return Dead();

        IsWaiting = true;
        int which;
        while (true)
        {
            yield return Dead();
            if (_nmiLatched) { which = 0; break; }
            if (FirqAsserted && (CC & FlagF) == 0) { which = 1; break; }
            if (IrqAsserted && (CC & FlagI) == 0) { which = 2; break; }
        }

        IsWaiting = false;
        if (which == 0) _nmiLatched = false;
        CC |= which == 2 ? FlagI : (byte)(FlagI | FlagF);
        LastInterrupt = which switch { 0 => "NMI", 1 => "FIRQ", _ => "IRQ" };
        foreach (var c in Vectored(which switch { 0 => VectorNmi, 1 => VectorFirq, _ => VectorIrq }, leadingDead: false)) yield return c;
    }

    /// <summary>The test-mode opcodes ($14, $15, $CD): the processor stops until reset.</summary>
    private IEnumerable<bool> Stop()
    {
        yield return Read(PC);
        IsStopped = true;
    }
}
