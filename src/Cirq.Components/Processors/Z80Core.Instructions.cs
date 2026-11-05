namespace Cirq.Components.Processors;

/// <summary>The Z80's instructions, T-state by T-state.</summary>
public sealed partial class Z80Core
{
    /// <summary>Even parity of each byte: P/V after a logical operation.</summary>
    private static readonly bool[] Parity = BuildParity();

    private static bool[] BuildParity()
    {
        var table = new bool[256];
        for (var i = 0; i < 256; i++) table[i] = System.Numerics.BitOperations.PopCount((uint)i) % 2 == 0;
        return table;
    }

    /// <summary>The index register a DD or FD prefix put in HL's place: 0 none, 1 IX, 2 IY.</summary>
    private int _index;

    private int _ea;
    private ushort _word;

    /// <summary>HL, or IX or IY after a prefix.</summary>
    private ushort IndexRegister
    {
        get => _index switch { 1 => IX, 2 => IY, _ => HL };
        set
        {
            switch (_index)
            {
                case 1: IX = value; break;
                case 2: IY = value; break;
                default: HL = value; break;
            }
        }
    }

    /// <summary>A register by its three-bit code (B C D E H L - A), H and L taken as the halves of IX or IY after a prefix.</summary>
    private byte Get8(int r) => r switch
    {
        4 when _index != 0 => (byte)(IndexRegister >> 8),
        5 when _index != 0 => (byte)IndexRegister,
        _ => GetPlain(r),
    };

    private void Set8(int r, byte value)
    {
        switch (r)
        {
            case 4 when _index != 0: IndexRegister = (ushort)((IndexRegister & 0x00FF) | (value << 8)); break;
            case 5 when _index != 0: IndexRegister = (ushort)((IndexRegister & 0xFF00) | value); break;
            default: SetPlain(r, value); break;
        }
    }

    /// <summary>A register by its code, H and L themselves whatever the prefix: what an (IX+d) instruction's other operand is.</summary>
    private byte GetPlain(int r) => r switch
    {
        0 => B,
        1 => C,
        2 => D,
        3 => E,
        4 => H,
        5 => L,
        _ => A,
    };

    private void SetPlain(int r, byte value)
    {
        switch (r)
        {
            case 0: B = value; break;
            case 1: C = value; break;
            case 2: D = value; break;
            case 3: E = value; break;
            case 4: H = value; break;
            case 5: L = value; break;
            default: A = value; break;
        }
    }

    /// <summary>BC, DE, HL (or IX, IY) or SP by its two-bit code.</summary>
    private ushort GetPair(int p) => p switch { 0 => BC, 1 => DE, 2 => IndexRegister, _ => SP };

    private void SetPair(int p, int value)
    {
        var v = (ushort)value;
        switch (p)
        {
            case 0: BC = v; break;
            case 1: DE = v; break;
            case 2: IndexRegister = v; break;
            default: SP = v; break;
        }
    }

    /// <summary>The pair PUSH and POP use: AF where the others have SP.</summary>
    private ushort GetPushPair(int p) => p == 3 ? AF : GetPair(p);

    /// <summary>NZ Z NC C PO PE P M.</summary>
    private bool Condition(int cc) => cc switch
    {
        0 => !FlagSet(FlagZ),
        1 => FlagSet(FlagZ),
        2 => !FlagSet(FlagC),
        3 => FlagSet(FlagC),
        4 => !FlagSet(FlagPV),
        5 => FlagSet(FlagPV),
        6 => !FlagSet(FlagS),
        _ => FlagSet(FlagS),
    };

    private static int Sz(byte r) => (r & (FlagS | FlagY | FlagX)) | (r == 0 ? FlagZ : 0);

    private static int Szp(byte r) => Sz(r) | (Parity[r] ? FlagPV : 0);

    // ---- operands ------------------------------------------------------------

    /// <summary>A byte at PC, which moves on.</summary>
    private IEnumerable<bool> Immediate()
    {
        foreach (var c in ReadCycle(PC)) yield return c;
        PC++;
    }

    /// <summary>Two bytes at PC, low first, into <see cref="_word"/>.</summary>
    private IEnumerable<bool> Immediate16()
    {
        foreach (var c in Immediate()) yield return c;
        var low = _data;
        foreach (var c in Immediate()) yield return c;
        _word = (ushort)((_data << 8) | low);
    }

    /// <summary>
    /// The address of an (HL) operand into <see cref="_ea"/>: HL itself, or after a prefix IX or IY
    /// plus the displacement read from PC and five internal T-states to add it, which also goes into WZ.
    /// </summary>
    private IEnumerable<bool> Effective()
    {
        if (_index == 0)
        {
            _ea = HL;
            yield break;
        }

        foreach (var c in Immediate()) yield return c;
        var displacement = (sbyte)_data;
        foreach (var c in Internal(5)) yield return c;
        _ea = (IndexRegister + displacement) & 0xFFFF;
        WZ = (ushort)_ea;
    }

    /// <summary>Two reads up the stack, low byte first, into <see cref="_word"/>.</summary>
    private IEnumerable<bool> Pop()
    {
        foreach (var c in ReadCycle(SP)) yield return c;
        SP++;
        var low = _data;
        foreach (var c in ReadCycle(SP)) yield return c;
        SP++;
        _word = (ushort)((_data << 8) | low);
    }

    /// <summary>The opcode after a prefix: another M1, R counting it.</summary>
    private IEnumerable<bool> FetchNext()
    {
        foreach (var c in FetchCycle(PC)) yield return c;
        PC++;
    }

    // ---- the instructions ------------------------------------------------------

    /// <summary>The instruction whose opcode has just been fetched (or read in an acknowledge, in mode 0).</summary>
    private IEnumerable<bool> Execute(byte op)
    {
        Opcode = op;

        if (op is 0xDD or 0xFD)
        {
            // A run of DD and FD prefixes: each an M1 of its own, the last one counting. Each ends
            // with the flags untouched, so Q is zero for the SCF or CCF after it.
            do
            {
                _index = op == 0xDD ? 1 : 2;
                Q = 0;
                foreach (var c in FetchNext()) yield return c;
                op = _data;
            }
            while (op is 0xDD or 0xFD);

            var prefix = _index == 1 ? 0xDD : 0xFD;
            Opcode = (prefix << 8) | op;
            if (op == 0xCB)
            {
                foreach (var c in IndexedBitInstruction(prefix)) yield return c;
                yield break;
            }

            if (op == 0xED) _index = 0;
        }

        switch (op)
        {
            case 0xCB:
                foreach (var c in FetchNext()) yield return c;
                Opcode = 0xCB00 | _data;
                foreach (var c in BitInstruction(_data)) yield return c;
                yield break;

            case 0xED:
                foreach (var c in FetchNext()) yield return c;
                Opcode = 0xED00 | _data;
                foreach (var c in ExtendedInstruction(_data)) yield return c;
                yield break;
        }

        var x = op >> 6;
        var y = (op >> 3) & 7;
        var z = op & 7;

        var steps = x switch
        {
            0 => Group0(y, z),
            1 => op == 0x76 ? HaltInstruction() : LoadRegister(y, z),
            2 => ArithmeticFromRegister(y, z),
            _ => Group3(op, y, z),
        };
        foreach (var c in steps) yield return c;
    }

    private IEnumerable<bool> HaltInstruction()
    {
        // PC has moved past the HALT; from here the processor fetches the byte at PC as a NOP until an interrupt.
        Halted = true;
        yield break;
    }

    /// <summary>LD r,r', with LD r,(HL) and LD (HL),r — whose other register is H or L, whatever the prefix.</summary>
    private IEnumerable<bool> LoadRegister(int y, int z)
    {
        if (y == 6)
        {
            foreach (var c in Effective()) yield return c;
            foreach (var c in WriteCycle(_ea, GetPlain(z))) yield return c;
        }
        else if (z == 6)
        {
            foreach (var c in Effective()) yield return c;
            foreach (var c in ReadCycle(_ea)) yield return c;
            SetPlain(y, _data);
        }
        else
        {
            Set8(y, Get8(z));
        }
    }

    private IEnumerable<bool> ArithmeticFromRegister(int y, int z)
    {
        if (z == 6)
        {
            foreach (var c in Effective()) yield return c;
            foreach (var c in ReadCycle(_ea)) yield return c;
            Arithmetic(y, _data);
        }
        else
        {
            Arithmetic(y, Get8(z));
        }
    }

    /// <summary>The first quarter of the opcode map: relative jumps, 16-bit loads and arithmetic, INC, DEC, LD r,n and the accumulator's rotates.</summary>
    private IEnumerable<bool> Group0(int y, int z)
    {
        var p = y >> 1;
        var q = y & 1;

        switch (z)
        {
            case 0:
                switch (y)
                {
                    case 0:
                        break;      // NOP

                    case 1:
                    {
                        // EX AF,AF'
                        var af = AF;
                        AF = AlternateAF;
                        AlternateAF = af;
                        break;
                    }

                    case 2:
                    {
                        // DJNZ: a T-state more in M1, the offset, and five more to jump.
                        yield return Internal();
                        foreach (var c in Immediate()) yield return c;
                        B--;
                        if (B != 0)
                        {
                            foreach (var c in Internal(5)) yield return c;
                            PC = (ushort)(PC + (sbyte)_data);
                            WZ = PC;
                        }

                        break;
                    }

                    default:
                    {
                        // JR, and JR NZ, Z, NC, C.
                        foreach (var c in Immediate()) yield return c;
                        if (y == 3 || Condition(y - 4))
                        {
                            foreach (var c in Internal(5)) yield return c;
                            PC = (ushort)(PC + (sbyte)_data);
                            WZ = PC;
                        }

                        break;
                    }
                }

                break;

            case 1:
                if (q == 0)
                {
                    foreach (var c in Immediate16()) yield return c;
                    SetPair(p, _word);
                }
                else
                {
                    foreach (var c in Internal(7)) yield return c;
                    IndexRegister = Add16(IndexRegister, GetPair(p));
                }

                break;

            case 2:
                foreach (var c in IndirectLoad(p, q)) yield return c;
                break;

            case 3:
                // INC rp, DEC rp: an M1 of six T-states, and no flags.
                foreach (var c in Internal(2)) yield return c;
                SetPair(p, GetPair(p) + (q == 0 ? 1 : -1));
                break;

            case 4:
            case 5:
                if (y == 6)
                {
                    foreach (var c in Effective()) yield return c;
                    foreach (var c in ReadCycle(_ea)) yield return c;
                    yield return Internal();
                    var value = z == 4 ? Increment(_data) : Decrement(_data);
                    foreach (var c in WriteCycle(_ea, value)) yield return c;
                }
                else
                {
                    Set8(y, z == 4 ? Increment(Get8(y)) : Decrement(Get8(y)));
                }

                break;

            case 6:
                if (y == 6)
                {
                    if (_index == 0)
                    {
                        foreach (var c in Immediate()) yield return c;
                        foreach (var c in WriteCycle(HL, _data)) yield return c;
                    }
                    else
                    {
                        // LD (IX+d),n: the displacement, then the byte with two T-states to add them.
                        foreach (var c in Immediate()) yield return c;
                        var displacement = (sbyte)_data;
                        foreach (var c in Immediate()) yield return c;
                        var value = _data;
                        foreach (var c in Internal(2)) yield return c;
                        _ea = (IndexRegister + displacement) & 0xFFFF;
                        WZ = (ushort)_ea;
                        foreach (var c in WriteCycle(_ea, value)) yield return c;
                    }
                }
                else
                {
                    foreach (var c in Immediate()) yield return c;
                    Set8(y, _data);
                }

                break;

            default:
                AccumulatorOperation(y);
                break;
        }
    }

    /// <summary>LD (BC),A, LD (DE),A, LD (nn),HL, LD (nn),A and the four loads the other way.</summary>
    private IEnumerable<bool> IndirectLoad(int p, int q)
    {
        switch ((q << 2) | p)
        {
            case 0:
            case 1:
            {
                // LD (BC),A, LD (DE),A: WZ is A over the address's low byte plus one.
                var address = p == 0 ? BC : DE;
                foreach (var c in WriteCycle(address, A)) yield return c;
                WZ = (ushort)((A << 8) | ((address + 1) & 0xFF));
                break;
            }

            case 2:
                foreach (var c in Immediate16()) yield return c;
                foreach (var c in WriteCycle(_word, (byte)IndexRegister)) yield return c;
                foreach (var c in WriteCycle((_word + 1) & 0xFFFF, (byte)(IndexRegister >> 8))) yield return c;
                WZ = (ushort)(_word + 1);
                break;

            case 3:
                foreach (var c in Immediate16()) yield return c;
                foreach (var c in WriteCycle(_word, A)) yield return c;
                WZ = (ushort)((A << 8) | ((_word + 1) & 0xFF));
                break;

            case 4:
            case 5:
            {
                var address = p == 0 ? BC : DE;
                foreach (var c in ReadCycle(address)) yield return c;
                A = _data;
                WZ = (ushort)(address + 1);
                break;
            }

            case 6:
            {
                foreach (var c in Immediate16()) yield return c;
                foreach (var c in ReadCycle(_word)) yield return c;
                var low = _data;
                foreach (var c in ReadCycle((_word + 1) & 0xFFFF)) yield return c;
                IndexRegister = (ushort)((_data << 8) | low);
                WZ = (ushort)(_word + 1);
                break;
            }

            default:
                foreach (var c in Immediate16()) yield return c;
                foreach (var c in ReadCycle(_word)) yield return c;
                A = _data;
                WZ = (ushort)(_word + 1);
                break;
        }
    }

    /// <summary>The last quarter of the map: returns, pops and pushes, jumps and calls, I/O to a fixed port, exchanges, DI and EI, arithmetic on n, RST.</summary>
    private IEnumerable<bool> Group3(byte op, int y, int z)
    {
        var p = y >> 1;
        var q = y & 1;

        switch (z)
        {
            case 0:
                // RET cc: a T-state more in M1, and the pop only if taken.
                yield return Internal();
                if (Condition(y))
                {
                    foreach (var c in Pop()) yield return c;
                    PC = WZ = _word;
                }

                break;

            case 1:
                if (q == 0)
                {
                    foreach (var c in Pop()) yield return c;
                    if (p == 3) AF = _word;
                    else SetPair(p, _word);
                    break;
                }

                switch (p)
                {
                    case 0:
                        foreach (var c in Pop()) yield return c;
                        PC = WZ = _word;
                        break;

                    case 1:
                    {
                        // EXX: BC, DE and HL — never IX or IY.
                        (BC, AlternateBC) = (AlternateBC, BC);
                        (DE, AlternateDE) = (AlternateDE, DE);
                        (HL, AlternateHL) = (AlternateHL, HL);
                        break;
                    }

                    case 2:
                        PC = IndexRegister;     // JP (HL): HL itself, no memory read
                        break;

                    default:
                        foreach (var c in Internal(2)) yield return c;
                        SP = IndexRegister;
                        break;
                }

                break;

            case 2:
                // JP cc,nn: the address is read, and goes into WZ, either way.
                foreach (var c in Immediate16()) yield return c;
                WZ = _word;
                if (Condition(y)) PC = _word;
                break;

            case 3:
                foreach (var c in Group3Column3(y)) yield return c;
                break;

            case 4:
                foreach (var c in Immediate16()) yield return c;
                WZ = _word;
                if (Condition(y))
                {
                    yield return Internal();
                    foreach (var c in Push(PC)) yield return c;
                    PC = _word;
                }

                break;

            case 5:
                if (q == 0)
                {
                    yield return Internal();
                    foreach (var c in Push(GetPushPair(p))) yield return c;
                }
                else
                {
                    // CALL nn ($CD); the prefixes ($DD, $ED, $FD) were taken before here.
                    foreach (var c in Immediate16()) yield return c;
                    WZ = _word;
                    yield return Internal();
                    foreach (var c in Push(PC)) yield return c;
                    PC = _word;
                }

                break;

            case 6:
                foreach (var c in Immediate()) yield return c;
                Arithmetic(y, _data);
                break;

            default:
                // RST: a T-state more in M1, then a call to y times eight.
                yield return Internal();
                foreach (var c in Push(PC)) yield return c;
                PC = (ushort)(op & 0x38);
                WZ = PC;
                break;
        }
    }

    /// <summary>JP nn, OUT (n),A, IN A,(n), EX (SP),HL, EX DE,HL, DI and EI.</summary>
    private IEnumerable<bool> Group3Column3(int y)
    {
        switch (y)
        {
            case 0:
                foreach (var c in Immediate16()) yield return c;
                PC = WZ = _word;
                break;

            case 2:
            {
                // OUT (n),A: A goes out on A8-A15 too.
                foreach (var c in Immediate()) yield return c;
                var port = (A << 8) | _data;
                foreach (var c in OutputCycle(port, A)) yield return c;
                WZ = (ushort)((A << 8) | ((port + 1) & 0xFF));
                break;
            }

            case 3:
            {
                foreach (var c in Immediate()) yield return c;
                var port = (A << 8) | _data;
                foreach (var c in InputCycle(port)) yield return c;
                A = _data;
                WZ = (ushort)(port + 1);
                break;
            }

            case 4:
            {
                // EX (SP),HL: read both bytes, a T-state, write both back high first, two T-states.
                foreach (var c in ReadCycle(SP)) yield return c;
                var low = _data;
                foreach (var c in ReadCycle((SP + 1) & 0xFFFF)) yield return c;
                var high = _data;
                yield return Internal();
                var value = IndexRegister;
                foreach (var c in WriteCycle((SP + 1) & 0xFFFF, (byte)(value >> 8))) yield return c;
                foreach (var c in WriteCycle(SP, (byte)value)) yield return c;
                foreach (var c in Internal(2)) yield return c;
                IndexRegister = (ushort)((high << 8) | low);
                WZ = IndexRegister;
                break;
            }

            case 5:
                (DE, HL) = (HL, DE);      // EX DE,HL: never IX or IY
                break;

            case 6:
                Iff1 = Iff2 = false;
                break;

            default:
                Iff1 = Iff2 = true;
                _eiNow = true;
                break;
        }
    }

    // ---- CB: rotates, shifts and bits -------------------------------------------

    /// <summary>A CB instruction on a register or (HL).</summary>
    private IEnumerable<bool> BitInstruction(byte op)
    {
        var x = op >> 6;
        var y = (op >> 3) & 7;
        var z = op & 7;

        if (z != 6)
        {
            var value = GetPlain(z);
            if (x == 1) Bit(y, value, value);
            else SetPlain(z, BitOperation(x, y, value));
            yield break;
        }

        foreach (var c in ReadCycle(HL)) yield return c;
        yield return Internal();
        if (x == 1)
        {
            // BIT n,(HL): X and Y come from WZ's high byte.
            Bit(y, _data, (byte)(WZ >> 8));
            yield break;
        }

        foreach (var c in WriteCycle(HL, BitOperation(x, y, _data))) yield return c;
    }

    /// <summary>
    /// DDCB d op and FDCB d op: the displacement, then the opcode read as data (not an M1) with two
    /// T-states to add, the operand read with one more, and the result written back — and, for the
    /// undocumented forms with a register in bits 0-2, copied into that register too.
    /// </summary>
    private IEnumerable<bool> IndexedBitInstruction(int prefix)
    {
        foreach (var c in Immediate()) yield return c;
        var displacement = (sbyte)_data;
        foreach (var c in Immediate()) yield return c;
        var op = _data;
        Opcode = (prefix << 16) | 0xCB00 | op;
        foreach (var c in Internal(2)) yield return c;
        _ea = (IndexRegister + displacement) & 0xFFFF;
        WZ = (ushort)_ea;

        var x = op >> 6;
        var y = (op >> 3) & 7;
        var z = op & 7;

        foreach (var c in ReadCycle(_ea)) yield return c;
        yield return Internal();
        if (x == 1)
        {
            Bit(y, _data, (byte)(_ea >> 8));
            yield break;
        }

        var result = BitOperation(x, y, _data);
        foreach (var c in WriteCycle(_ea, result)) yield return c;
        if (z != 6) SetPlain(z, result);
    }

    /// <summary>A rotate or shift (x = 0), RES (2) or SET (3).</summary>
    private byte BitOperation(int x, int y, byte value) => x switch
    {
        0 => Rotate(y, value),
        2 => (byte)(value & ~(1 << y)),
        _ => (byte)(value | (1 << y)),
    };

    /// <summary>RLC RRC RL RR SLA SRA SLL SRL, each setting S, Z, P/V, X and Y from the result.</summary>
    private byte Rotate(int y, byte value)
    {
        var carryIn = FlagSet(FlagC) ? 1 : 0;
        int result, carry;
        switch (y)
        {
            case 0: carry = value >> 7; result = (value << 1) | carry; break;
            case 1: carry = value & 1; result = (value >> 1) | (carry << 7); break;
            case 2: carry = value >> 7; result = (value << 1) | carryIn; break;
            case 3: carry = value & 1; result = (value >> 1) | (carryIn << 7); break;
            case 4: carry = value >> 7; result = value << 1; break;
            case 5: carry = value & 1; result = (value >> 1) | (value & 0x80); break;
            case 6: carry = value >> 7; result = (value << 1) | 1; break;   // SLL: undocumented, a 1 shifted in
            default: carry = value & 1; result = value >> 1; break;
        }

        var r = (byte)result;
        SetFlags(Szp(r) | carry);
        return r;
    }

    /// <summary>BIT n: Z and P/V the bit's complement, S for bit 7 set, H set, X and Y from <paramref name="xy"/>.</summary>
    private void Bit(int n, byte value, byte xy)
    {
        var set = (value & (1 << n)) != 0;
        var f = (_f & FlagC) | FlagH | (xy & (FlagX | FlagY));
        if (!set) f |= FlagZ | FlagPV;
        if (n == 7 && set) f |= FlagS;
        SetFlags(f);
    }

    // ---- ED -----------------------------------------------------------------

    private IEnumerable<bool> ExtendedInstruction(byte op)
    {
        var x = op >> 6;
        var y = (op >> 3) & 7;
        var z = op & 7;

        if (x == 2 && z <= 3 && y >= 4)
        {
            foreach (var c in BlockInstruction(y, z)) yield return c;
            yield break;
        }

        // Everything outside the documented block of $40-$7F and the block instructions is a NOP of two M1s.
        if (x != 1) yield break;

        var p = y >> 1;
        var q = y & 1;

        switch (z)
        {
            case 0:
            {
                // IN r,(C), and the undocumented IN F,(C) at $70 that keeps only the flags.
                foreach (var c in InputCycle(BC)) yield return c;
                var value = _data;
                WZ = (ushort)(BC + 1);
                SetFlags((_f & FlagC) | Szp(value));
                if (y != 6) SetPlain(y, value);
                break;
            }

            case 1:
            {
                // OUT (C),r, and OUT (C),0 at $71 (an NMOS part puts out zero).
                var value = y == 6 ? (byte)0 : GetPlain(y);
                foreach (var c in OutputCycle(BC, value)) yield return c;
                WZ = (ushort)(BC + 1);
                break;
            }

            case 2:
                foreach (var c in Internal(7)) yield return c;
                HL = q == 0 ? SubtractWithCarry16(HL, GetPair(p)) : AddWithCarry16(HL, GetPair(p));
                break;

            case 3:
                foreach (var c in Immediate16()) yield return c;
                if (q == 0)
                {
                    var value = GetPair(p);
                    foreach (var c in WriteCycle(_word, (byte)value)) yield return c;
                    foreach (var c in WriteCycle((_word + 1) & 0xFFFF, (byte)(value >> 8))) yield return c;
                }
                else
                {
                    foreach (var c in ReadCycle(_word)) yield return c;
                    var low = _data;
                    foreach (var c in ReadCycle((_word + 1) & 0xFFFF)) yield return c;
                    SetPair(p, (_data << 8) | low);
                }

                WZ = (ushort)(_word + 1);
                break;

            case 4:
            {
                // NEG, at $44 and seven undocumented copies.
                var value = A;
                A = 0;
                Subtract(value, false, true);
                break;
            }

            case 5:
                // RETN, and RETI at $4D: both put IFF2 back into IFF1; the peripherals tell RETI by its bytes.
                foreach (var c in Pop()) yield return c;
                PC = WZ = _word;
                Iff1 = Iff2;
                break;

            case 6:
                // IM 0, 0, 1, 2 for bits 4-3; $4E and $6E, which Zilog left out, select mode 0 here.
                InterruptMode = (y & 3) switch { 2 => 1, 3 => 2, _ => 0 };
                break;

            default:
                foreach (var c in RegisterIr(y)) yield return c;
                break;
        }
    }

    /// <summary>LD I,A, LD R,A, LD A,I, LD A,R, RRD and RLD; $77 and $7F are NOPs.</summary>
    private IEnumerable<bool> RegisterIr(int y)
    {
        switch (y)
        {
            case 0:
                yield return Internal();
                I = A;
                break;

            case 1:
                yield return Internal();
                R = A;
                break;

            case 2:
            case 3:
                // LD A,I and LD A,R: P/V is IFF2.
                yield return Internal();
                A = y == 2 ? I : R;
                SetFlags((_f & FlagC) | Sz(A) | (Iff2 ? FlagPV : 0));
                _ldAirNow = true;
                break;

            case 4:
            case 5:
            {
                // RRD, RLD: the digits rotated through A's low four bits and (HL).
                foreach (var c in ReadCycle(HL)) yield return c;
                var value = _data;
                foreach (var c in Internal(4)) yield return c;
                byte memory;
                if (y == 4)
                {
                    memory = (byte)((A << 4) | (value >> 4));
                    A = (byte)((A & 0xF0) | (value & 0x0F));
                }
                else
                {
                    memory = (byte)((value << 4) | (A & 0x0F));
                    A = (byte)((A & 0xF0) | (value >> 4));
                }

                foreach (var c in WriteCycle(HL, memory)) yield return c;
                WZ = (ushort)(HL + 1);
                SetFlags((_f & FlagC) | Szp(A));
                break;
            }
        }
    }

    /// <summary>
    /// LDI, CPI, INI, OUTI and their decrementing (y odd) and repeating (y 6 and 7) forms. A
    /// repeating one that goes round again takes five T-states more and moves PC back to itself, so
    /// it is fetched afresh — and an interrupt can come between the rounds.
    /// </summary>
    private IEnumerable<bool> BlockInstruction(int y, int z)
    {
        var step = (y & 1) == 0 ? 1 : -1;
        var repeat = y >= 6;
        bool again;

        switch (z)
        {
            case 0:
            {
                foreach (var c in ReadCycle(HL)) yield return c;
                var value = _data;
                foreach (var c in WriteCycle(DE, value)) yield return c;
                foreach (var c in Internal(2)) yield return c;
                HL = (ushort)(HL + step);
                DE = (ushort)(DE + step);
                BC--;
                var n = value + A;
                SetFlags((_f & (FlagS | FlagZ | FlagC)) | (n & FlagX) | ((n << 4) & FlagY) | (BC != 0 ? FlagPV : 0));
                again = repeat && BC != 0;
                break;
            }

            case 1:
            {
                foreach (var c in ReadCycle(HL)) yield return c;
                var value = _data;
                foreach (var c in Internal(5)) yield return c;
                var result = (byte)(A - value);
                var half = (A ^ value ^ result) & FlagH;
                var n = result - (half != 0 ? 1 : 0);
                HL = (ushort)(HL + step);
                WZ = (ushort)(WZ + step);
                BC--;
                SetFlags((_f & FlagC) | FlagN | (result & FlagS) | (result == 0 ? FlagZ : 0) | half | (n & FlagX) | ((n << 4) & FlagY) |
                         (BC != 0 ? FlagPV : 0));
                again = repeat && BC != 0 && result != 0;
                break;
            }

            case 2:
            {
                // INI: a T-state more in M1, the port's byte, B counted down, the byte written to (HL).
                yield return Internal();
                foreach (var c in InputCycle(BC)) yield return c;
                var value = _data;
                WZ = (ushort)(BC + step);
                B--;
                foreach (var c in WriteCycle(HL, value)) yield return c;
                HL = (ushort)(HL + step);
                BlockIoFlags(value, value + ((C + step) & 0xFF));
                again = repeat && B != 0;
                break;
            }

            default:
            {
                // OUTI: B counted down before the byte goes out, so A8-A15 carry the new count.
                yield return Internal();
                foreach (var c in ReadCycle(HL)) yield return c;
                var value = _data;
                B--;
                foreach (var c in OutputCycle(BC, value)) yield return c;
                HL = (ushort)(HL + step);
                WZ = (ushort)(BC + step);
                BlockIoFlags(value, value + L);
                again = repeat && B != 0;
                break;
            }
        }

        if (!again) yield break;

        foreach (var c in Internal(5)) yield return c;
        PC = (ushort)(PC - 2);
        WZ = (ushort)(PC + 1);

        // Going round again, X and Y show PC's high byte; the I/O ones change H and P/V once more.
        var f = (_f & ~(FlagX | FlagY)) | ((PC >> 8) & (FlagX | FlagY));
        if (z >= 2)
        {
            var carry = (f & FlagC) != 0;
            var negative = (f & FlagN) != 0;
            int parityTerm;
            if (carry)
            {
                if (negative)
                {
                    parityTerm = (B - 1) & 7;
                    f = (f & ~FlagH) | ((B & 0x0F) == 0x00 ? FlagH : 0);
                }
                else
                {
                    parityTerm = (B + 1) & 7;
                    f = (f & ~FlagH) | ((B & 0x0F) == 0x0F ? FlagH : 0);
                }
            }
            else
            {
                parityTerm = B & 7;
            }

            if (!Parity[parityTerm]) f ^= FlagPV;
        }

        SetFlags(f);
    }

    /// <summary>INI, IND, OUTI and OUTD's flags: S, Z, X and Y from B, N from the byte's bit 7, H and C from a sum past 255, P/V a parity.</summary>
    private void BlockIoFlags(byte value, int k)
    {
        var f = Sz(B);
        if ((value & 0x80) != 0) f |= FlagN;
        if (k > 0xFF) f |= FlagH | FlagC;
        if (Parity[(byte)((k & 7) ^ B)]) f |= FlagPV;
        SetFlags(f);
    }

    // ---- arithmetic ------------------------------------------------------------

    /// <summary>ADD ADC SUB SBC AND XOR OR CP, by bits 5-3 of the opcode.</summary>
    private void Arithmetic(int operation, byte value)
    {
        switch (operation)
        {
            case 0: Add(value, false); break;
            case 1: Add(value, true); break;
            case 2: Subtract(value, false, true); break;
            case 3: Subtract(value, true, true); break;
            case 4:
                A &= value;
                SetFlags(Szp(A) | FlagH);
                break;
            case 5:
                A ^= value;
                SetFlags(Szp(A));
                break;
            case 6:
                A |= value;
                SetFlags(Szp(A));
                break;
            default:
                Subtract(value, false, false);
                break;
        }
    }

    private void Add(byte value, bool withCarry)
    {
        var carry = withCarry && FlagSet(FlagC) ? 1 : 0;
        var result = A + value + carry;
        var r = (byte)result;
        var f = Sz(r) | ((A ^ value ^ result) & FlagH) | (result > 0xFF ? FlagC : 0);
        if (((A ^ ~value) & (A ^ result) & 0x80) != 0) f |= FlagPV;
        A = r;
        SetFlags(f);
    }

    /// <summary>SUB, SBC, and CP (which keeps A, and takes X and Y from the operand).</summary>
    private void Subtract(byte value, bool withCarry, bool store)
    {
        var carry = withCarry && FlagSet(FlagC) ? 1 : 0;
        var result = A - value - carry;
        var r = (byte)result;
        var f = FlagN | (r & FlagS) | (r == 0 ? FlagZ : 0) | ((A ^ value ^ result) & FlagH) | (result < 0 ? FlagC : 0);
        if (((A ^ value) & (A ^ result) & 0x80) != 0) f |= FlagPV;
        if (store)
        {
            A = r;
            f |= r & (FlagX | FlagY);
        }
        else
        {
            f |= value & (FlagX | FlagY);
        }

        SetFlags(f);
    }

    private byte Increment(byte value)
    {
        var r = (byte)(value + 1);
        SetFlags((_f & FlagC) | Sz(r) | ((r & 0x0F) == 0 ? FlagH : 0) | (r == 0x80 ? FlagPV : 0));
        return r;
    }

    private byte Decrement(byte value)
    {
        var r = (byte)(value - 1);
        SetFlags((_f & FlagC) | FlagN | Sz(r) | ((r & 0x0F) == 0x0F ? FlagH : 0) | (r == 0x7F ? FlagPV : 0));
        return r;
    }

    /// <summary>ADD HL,rp: H from bit 11, C from bit 15, X and Y from the result's high byte; S, Z and P/V kept.</summary>
    private ushort Add16(ushort a, ushort b)
    {
        var result = a + b;
        WZ = (ushort)(a + 1);
        SetFlags((_f & (FlagS | FlagZ | FlagPV)) | ((result >> 8) & (FlagX | FlagY)) | (((a ^ b ^ result) >> 8) & FlagH) | (result > 0xFFFF ? FlagC : 0));
        return (ushort)result;
    }

    private ushort AddWithCarry16(ushort a, ushort b)
    {
        var result = a + b + (FlagSet(FlagC) ? 1 : 0);
        var r = (ushort)result;
        WZ = (ushort)(a + 1);
        var f = ((r >> 8) & (FlagS | FlagX | FlagY)) | (r == 0 ? FlagZ : 0) | (((a ^ b ^ result) >> 8) & FlagH) | (result > 0xFFFF ? FlagC : 0);
        if (((a ^ ~b) & (a ^ result) & 0x8000) != 0) f |= FlagPV;
        SetFlags(f);
        return r;
    }

    private ushort SubtractWithCarry16(ushort a, ushort b)
    {
        var result = a - b - (FlagSet(FlagC) ? 1 : 0);
        var r = (ushort)result;
        WZ = (ushort)(a + 1);
        var f = FlagN | ((r >> 8) & (FlagS | FlagX | FlagY)) | (r == 0 ? FlagZ : 0) | (((a ^ b ^ result) >> 8) & FlagH) | (result < 0 ? FlagC : 0);
        if (((a ^ b) & (a ^ result) & 0x8000) != 0) f |= FlagPV;
        SetFlags(f);
        return r;
    }

    /// <summary>RLCA RRCA RLA RRA DAA CPL SCF CCF.</summary>
    private void AccumulatorOperation(int y)
    {
        const int Kept = FlagS | FlagZ | FlagPV;
        switch (y)
        {
            case 0:
            {
                var carry = A >> 7;
                A = (byte)((A << 1) | carry);
                SetFlags((_f & Kept) | (A & (FlagX | FlagY)) | carry);
                break;
            }

            case 1:
            {
                var carry = A & 1;
                A = (byte)((A >> 1) | (carry << 7));
                SetFlags((_f & Kept) | (A & (FlagX | FlagY)) | carry);
                break;
            }

            case 2:
            {
                var carry = A >> 7;
                A = (byte)((A << 1) | (FlagSet(FlagC) ? 1 : 0));
                SetFlags((_f & Kept) | (A & (FlagX | FlagY)) | carry);
                break;
            }

            case 3:
            {
                var carry = A & 1;
                A = (byte)((A >> 1) | (FlagSet(FlagC) ? 0x80 : 0));
                SetFlags((_f & Kept) | (A & (FlagX | FlagY)) | carry);
                break;
            }

            case 4:
                DecimalAdjust();
                break;

            case 5:
                A = (byte)~A;
                SetFlags((_f & (Kept | FlagC)) | FlagH | FlagN | (A & (FlagX | FlagY)));
                break;

            case 6:
                // SCF and CCF: X and Y from A ORed with what changed in the flags since the instruction before (Q).
                SetFlags((_f & Kept) | ((((Q ^ _f) | A)) & (FlagX | FlagY)) | FlagC);
                break;

            default:
                SetFlags((_f & Kept) | ((((Q ^ _f) | A)) & (FlagX | FlagY)) | (FlagSet(FlagC) ? FlagH : FlagC));
                break;
        }
    }

    /// <summary>DAA: the correction a BCD sum or difference needs, from A, H, N and C.</summary>
    private void DecimalAdjust()
    {
        var a = A;
        var subtract = FlagSet(FlagN);
        var correction = 0;
        var carry = FlagSet(FlagC);
        if (FlagSet(FlagH) || (a & 0x0F) > 9) correction |= 0x06;
        if (carry || a > 0x99)
        {
            correction |= 0x60;
            carry = true;
        }

        var half = subtract ? FlagSet(FlagH) && (a & 0x0F) < 6 : (a & 0x0F) > 9;
        A = (byte)(subtract ? a - correction : a + correction);
        SetFlags(Szp(A) | (subtract ? FlagN : 0) | (half ? FlagH : 0) | (carry ? FlagC : 0));
    }
}
