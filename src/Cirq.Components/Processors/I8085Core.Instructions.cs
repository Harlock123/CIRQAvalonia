namespace Cirq.Components.Processors;

/// <summary>The 8085's instructions, T-state by T-state.</summary>
public sealed partial class I8085Core
{
    /// <summary>Even parity of each byte: the P flag.</summary>
    private static readonly bool[] Parity = BuildParity();

    /// <summary>
    /// The opcodes whose fetch takes six T-states rather than four: INX, DCX, PUSH, CALL, the
    /// conditional calls and returns, RST, PCHL, SPHL and RSTV.
    /// </summary>
    public static readonly bool[] SixStateFetch = BuildSixState();

    private static bool[] BuildParity()
    {
        var table = new bool[256];
        for (var i = 0; i < 256; i++) table[i] = System.Numerics.BitOperations.PopCount((uint)i) % 2 == 0;
        return table;
    }

    private static bool[] BuildSixState()
    {
        var table = new bool[256];
        for (var op = 0; op < 256; op++)
        {
            var x = op >> 6;
            var z = op & 7;
            var y = (op >> 3) & 7;
            table[op] = (x == 0 && z == 3)                       // INX, DCX
                        || (x == 3 && z == 0)                    // Rcc
                        || (x == 3 && z == 4)                    // Ccc
                        || (x == 3 && z == 7)                    // RST
                        || (x == 3 && z == 5 && (y & 1) == 0)    // PUSH
                        || op is 0xCD or 0xE9 or 0xF9 or 0xCB;   // CALL, PCHL, SPHL, RSTV
        }

        return table;
    }

    private ushort _word;

    /// <summary>B C D E H L M A by their three-bit code; M (6) is not a register and is handled by the caller.</summary>
    private byte Get8(int r) => r switch
    {
        0 => B,
        1 => C,
        2 => D,
        3 => E,
        4 => H,
        5 => L,
        _ => A,
    };

    private void Set8(int r, byte value)
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

    /// <summary>B, D, H or SP by their two-bit code.</summary>
    private ushort GetPair(int p) => p switch { 0 => BC, 1 => DE, 2 => HL, _ => SP };

    private void SetPair(int p, int value)
    {
        var v = (ushort)value;
        switch (p)
        {
            case 0: BC = v; break;
            case 1: DE = v; break;
            case 2: HL = v; break;
            default: SP = v; break;
        }
    }

    /// <summary>NZ Z NC C PO PE P M.</summary>
    private bool Condition(int cc) => cc switch
    {
        0 => !FlagSet(FlagZ),
        1 => FlagSet(FlagZ),
        2 => !FlagSet(FlagC),
        3 => FlagSet(FlagC),
        4 => !FlagSet(FlagP),
        5 => FlagSet(FlagP),
        6 => !FlagSet(FlagS),
        _ => FlagSet(FlagS),
    };

    private static int Szp(int r) => (r & FlagS) | ((r & 0xFF) == 0 ? FlagZ : 0) | (Parity[r & 0xFF] ? FlagP : 0);

    /// <summary>K is V exclusive-ORed with bit 7 of the ALU's result, as the die computes it.</summary>
    private static int VK(bool v, int result) => (v ? FlagV : 0) | ((v ^ ((result & 0x80) != 0)) ? FlagK : 0);

    // ---- operands ------------------------------------------------------------

    /// <summary>
    /// A byte at PC, which moves on — or, while an INTR acknowledge is running the device's
    /// instruction, the next byte the device puts on the bus with /INTA, PC held.
    /// </summary>
    private IEnumerable<bool> Immediate()
    {
        if (_acknowledging)
        {
            foreach (var c in Transfer(I8085CycleKind.InterruptAcknowledge, PC, 0, reads: true)) yield return c;
            _acknowledged.Add(_data);
            yield break;
        }

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

    // ---- the instructions ------------------------------------------------------

    /// <summary>The instruction whose opcode has just been fetched (or read in an INTR acknowledge).</summary>
    private IEnumerable<bool> Execute(byte op)
    {
        Opcode = op;
        var x = op >> 6;
        var y = (op >> 3) & 7;
        var z = op & 7;

        var steps = x switch
        {
            0 => Group0(y, z),
            1 => op == 0x76 ? HaltInstruction() : Move(y, z),
            2 => ArithmeticFromRegister(y, z),
            _ => Group3(y, z),
        };
        foreach (var c in steps) yield return c;
    }

    /// <summary>HLT: a fifth T-state, the first HALT state, and the bus floats until an interrupt.</summary>
    private IEnumerable<bool> HaltInstruction()
    {
        yield return Post(I8085CycleKind.Halt, I8085TState.Thalt, _now.Address, samplesHold: true, floated: true);
        Halted = true;
    }

    /// <summary>MOV r,r', MOV r,M and MOV M,r.</summary>
    private IEnumerable<bool> Move(int y, int z)
    {
        if (y == 6)
        {
            foreach (var c in WriteCycle(HL, Get8(z))) yield return c;
        }
        else if (z == 6)
        {
            foreach (var c in ReadCycle(HL)) yield return c;
            Set8(y, _data);
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
            foreach (var c in ReadCycle(HL)) yield return c;
            Arithmetic(y, _data);
        }
        else
        {
            Arithmetic(y, Get8(z));
        }
    }

    /// <summary>The first quarter of the map: the undocumented 16-bit instructions, RIM and SIM, 16-bit loads and arithmetic, INR, DCR, MVI and the accumulator's operations.</summary>
    private IEnumerable<bool> Group0(int y, int z)
    {
        var p = y >> 1;
        var q = y & 1;

        switch (z)
        {
            case 0:
                foreach (var c in Column0(y)) yield return c;
                break;

            case 1:
                if (q == 0)
                {
                    foreach (var c in Immediate16()) yield return c;
                    SetPair(p, _word);
                }
                else
                {
                    // DAD: two bus idle cycles, the low bytes added then the high. Only CY is kept.
                    foreach (var c in BusIdle()) yield return c;
                    foreach (var c in BusIdle()) yield return c;
                    var sum = HL + GetPair(p);
                    HL = (ushort)sum;
                    _f = (byte)((_f & ~FlagC) | (sum > 0xFFFF ? FlagC : 0));
                }

                break;

            case 2:
                foreach (var c in IndirectLoad(p, q)) yield return c;
                break;

            case 3:
            {
                // INX, DCX: a six-state fetch; K is the incrementer's carry out (or borrow).
                foreach (var c in Extend()) yield return c;
                var value = GetPair(p) + (q == 0 ? 1 : -1);
                SetPair(p, value);
                var wrapped = q == 0 ? (value & 0xFFFF) == 0 : (value & 0xFFFF) == 0xFFFF;
                _f = (byte)((_f & ~FlagK) | (wrapped ? FlagK : 0));
                break;
            }

            case 4:
            case 5:
                if (y == 6)
                {
                    foreach (var c in ReadCycle(HL)) yield return c;
                    var value = z == 4 ? Increment(_data) : Decrement(_data);
                    foreach (var c in WriteCycle(HL, value)) yield return c;
                }
                else
                {
                    Set8(y, z == 4 ? Increment(Get8(y)) : Decrement(Get8(y)));
                }

                break;

            case 6:
                foreach (var c in Immediate()) yield return c;
                if (y == 6)
                {
                    foreach (var c in WriteCycle(HL, _data)) yield return c;
                }
                else
                {
                    Set8(y, _data);
                }

                break;

            default:
                AccumulatorOperation(y);
                break;
        }
    }

    /// <summary>NOP, the undocumented DSUB, ARHL, RDEL, LDHI and LDSI, and RIM and SIM.</summary>
    private IEnumerable<bool> Column0(int y)
    {
        switch (y)
        {
            case 0:
                break;      // NOP

            case 1:
            {
                // DSUB: HL - BC in two bus idle cycles, L - C then H - B - borrow. S, P and AC come
                // from the high byte, Z from both (the die ANDs the two zeros), CY the 16-bit borrow.
                foreach (var c in BusIdle()) yield return c;
                foreach (var c in BusIdle()) yield return c;
                var low = L - C;
                var borrow = low < 0 ? 1 : 0;
                var high = H + (~B & 0xFF) + (1 - borrow);
                var ac = ((H & 0xF) + (~B & 0xF) + (1 - borrow)) > 0xF;
                var result = ((high & 0xFF) << 8) | (low & 0xFF);
                L = (byte)low;
                H = (byte)high;
                var flags = (_f & (FlagV | FlagK)) | (high & FlagS) | (result == 0 ? FlagZ : 0) | (Parity[high & 0xFF] ? FlagP : 0) |
                            (ac ? FlagAC : 0) | (high > 0xFF ? 0 : FlagC);
                _f = (byte)flags;
                break;
            }

            case 2:
            {
                // ARHL: HL shifted right arithmetically, H7 kept, L0 into CY. Through the ALU's shift
                // right, which leaves V clear and K the last result's bit 7 — H's old bit 0.
                foreach (var c in BusIdle()) yield return c;
                var carry = L & 1;
                var oldH0 = H & 1;
                HL = (ushort)((HL >> 1) | (HL & 0x8000));
                _f = (byte)((_f & ~(FlagC | FlagV | FlagK)) | carry | (oldH0 != 0 ? FlagK : 0));
                break;
            }

            case 3:
            {
                // RDEL: DE rotated left through CY. Only CY is kept.
                foreach (var c in BusIdle()) yield return c;
                foreach (var c in BusIdle()) yield return c;
                var value = (DE << 1) | (FlagSet(FlagC) ? 1 : 0);
                DE = (ushort)value;
                _f = (byte)((_f & ~FlagC) | (value > 0xFFFF ? FlagC : 0));
                break;
            }

            case 4:
            {
                // RIM: SID, the pending interrupts, the enable (the one before a TRAP, once), the masks.
                var enabled = _trapRimPending ? _ieBeforeTrap : InterruptsEnabled;
                _trapRimPending = false;
                A = (byte)((Sid ? 0x80 : 0) | (Rst75Pending ? 0x40 : 0) | (Rst65 ? 0x20 : 0) | (Rst55 ? 0x10 : 0) | (enabled ? 0x08 : 0) | (Masks & 7));
                break;
            }

            case 5:
            case 7:
            {
                // LDHI, LDSI: DE = HL (or SP) + the byte, through the ALU with a latched carry; no flags.
                foreach (var c in Immediate()) yield return c;
                var offset = _data;
                foreach (var c in BusIdle()) yield return c;
                DE = (ushort)((y == 5 ? HL : SP) + offset);
                break;
            }

            default:
            {
                // SIM: the masks if bit 3, the RST 7.5 latch reset if bit 4, SOD from bit 7 if bit 6 —
                // SOD changing in T2 of the next fetch.
                if ((A & 0x08) != 0) Masks = A & 7;
                if ((A & 0x10) != 0) Rst75Pending = false;
                if ((A & 0x40) != 0) _sodPending = A >> 7;
                break;
            }
        }
    }

    /// <summary>STAX, SHLD, STA, LDAX, LHLD and LDA.</summary>
    private IEnumerable<bool> IndirectLoad(int p, int q)
    {
        switch ((q << 2) | p)
        {
            case 0:
            case 1:
                foreach (var c in WriteCycle(p == 0 ? BC : DE, A)) yield return c;
                break;

            case 2:
                foreach (var c in Immediate16()) yield return c;
                foreach (var c in WriteCycle(_word, L)) yield return c;
                foreach (var c in WriteCycle((_word + 1) & 0xFFFF, H)) yield return c;
                break;

            case 3:
                foreach (var c in Immediate16()) yield return c;
                foreach (var c in WriteCycle(_word, A)) yield return c;
                break;

            case 4:
            case 5:
                foreach (var c in ReadCycle(p == 0 ? BC : DE)) yield return c;
                A = _data;
                break;

            case 6:
            {
                foreach (var c in Immediate16()) yield return c;
                foreach (var c in ReadCycle(_word)) yield return c;
                var low = _data;
                foreach (var c in ReadCycle((_word + 1) & 0xFFFF)) yield return c;
                HL = (ushort)((_data << 8) | low);
                break;
            }

            default:
                foreach (var c in Immediate16()) yield return c;
                foreach (var c in ReadCycle(_word)) yield return c;
                A = _data;
                break;
        }
    }

    /// <summary>The last quarter of the map: returns, pops and pushes, jumps and calls, I/O, the exchanges, DI and EI, immediate arithmetic, RST and the undocumented RSTV, SHLX, LHLX, JNK and JK.</summary>
    private IEnumerable<bool> Group3(int y, int z)
    {
        var p = y >> 1;
        var q = y & 1;

        switch (z)
        {
            case 0:
                // Rcc: a six-state fetch, then the pop only if taken.
                foreach (var c in Extend()) yield return c;
                if (Condition(y))
                {
                    foreach (var c in Pop()) yield return c;
                    PC = _word;
                }

                break;

            case 1:
                if (q == 0)
                {
                    foreach (var c in Pop()) yield return c;
                    if (p == 3) Psw = _word;
                    else SetPair(p, _word);
                    break;
                }

                switch (p)
                {
                    case 0:
                        foreach (var c in Pop()) yield return c;
                        PC = _word;
                        break;

                    case 1:
                        // SHLX: L to (DE), H to (DE+1).
                        foreach (var c in WriteCycle(DE, L)) yield return c;
                        foreach (var c in WriteCycle((DE + 1) & 0xFFFF, H)) yield return c;
                        break;

                    case 2:
                        foreach (var c in Extend()) yield return c;
                        PC = HL;        // PCHL
                        break;

                    default:
                        foreach (var c in Extend()) yield return c;
                        SP = HL;        // SPHL
                        break;
                }

                break;

            case 2:
                foreach (var c in ConditionalJump(Condition(y))) yield return c;
                break;

            case 3:
                foreach (var c in Column3(y)) yield return c;
                break;

            case 4:
            {
                // Ccc: a six-state fetch and the address's low byte; the high byte and the pushes only if taken.
                foreach (var c in Extend()) yield return c;
                foreach (var c in Immediate()) yield return c;
                var low = _data;
                if (Condition(y))
                {
                    foreach (var c in Immediate()) yield return c;
                    var target = (ushort)((_data << 8) | low);
                    foreach (var c in Push(PC)) yield return c;
                    PC = target;
                }
                else if (!_acknowledging)
                {
                    PC++;
                }

                break;
            }

            case 5:
                if (q == 0)
                {
                    foreach (var c in Extend()) yield return c;
                    foreach (var c in Push(p == 3 ? Psw : GetPair(p))) yield return c;
                    break;
                }

                switch (p)
                {
                    case 0:
                    {
                        // CALL: S R R W W.
                        foreach (var c in Extend()) yield return c;
                        foreach (var c in Immediate16()) yield return c;
                        var target = _word;
                        foreach (var c in Push(PC)) yield return c;
                        PC = target;
                        break;
                    }

                    case 2:
                    {
                        // LHLX: L from (DE), H from (DE+1).
                        foreach (var c in ReadCycle(DE)) yield return c;
                        var low = _data;
                        foreach (var c in ReadCycle((DE + 1) & 0xFFFF)) yield return c;
                        HL = (ushort)((_data << 8) | low);
                        break;
                    }

                    default:
                        // JNK ($DD) and JK ($FD).
                        foreach (var c in ConditionalJump(FlagSet(FlagK) == (p == 3))) yield return c;
                        break;
                }

                break;

            case 6:
                foreach (var c in Immediate()) yield return c;
                Arithmetic(y, _data);
                break;

            default:
                // RST n: a six-state fetch and the pushes.
                foreach (var c in Extend()) yield return c;
                foreach (var c in Push(PC)) yield return c;
                PC = (ushort)(y * 8);
                break;
        }
    }

    /// <summary>A jump on a condition: the low byte read either way, the high byte only if taken (7 or 10 T-states).</summary>
    private IEnumerable<bool> ConditionalJump(bool taken)
    {
        foreach (var c in Immediate()) yield return c;
        var low = _data;
        if (taken)
        {
            foreach (var c in Immediate()) yield return c;
            PC = (ushort)((_data << 8) | low);
        }
        else if (!_acknowledging)
        {
            PC++;
        }
    }

    /// <summary>JMP, RSTV, OUT, IN, XTHL, XCHG, DI and EI.</summary>
    private IEnumerable<bool> Column3(int y)
    {
        switch (y)
        {
            case 0:
                foreach (var c in Immediate16()) yield return c;
                PC = _word;
                break;

            case 1:
                // RSTV: a six-state fetch, and a call to $40 only if V is set.
                foreach (var c in Extend()) yield return c;
                if (FlagSet(FlagV))
                {
                    foreach (var c in Push(PC)) yield return c;
                    PC = OverflowAddress;
                }

                break;

            case 2:
                foreach (var c in Immediate()) yield return c;
                foreach (var c in OutputCycle(_data, A)) yield return c;
                break;

            case 3:
                foreach (var c in Immediate()) yield return c;
                foreach (var c in InputCycle(_data)) yield return c;
                A = _data;
                break;

            case 4:
            {
                // XTHL: F R R W W, H to (SP+1) first.
                foreach (var c in ReadCycle(SP)) yield return c;
                var low = _data;
                foreach (var c in ReadCycle((SP + 1) & 0xFFFF)) yield return c;
                var high = _data;
                foreach (var c in WriteCycle((SP + 1) & 0xFFFF, H)) yield return c;
                foreach (var c in WriteCycle(SP, L)) yield return c;
                HL = (ushort)((high << 8) | low);
                break;
            }

            case 5:
                (DE, HL) = (HL, DE);        // XCHG
                break;

            case 6:
                InterruptsEnabled = false;  // DI
                break;

            default:
                InterruptsEnabled = true;   // EI: taken from the boundary after the next instruction
                _eiNow = true;
                break;
        }
    }

    // ---- the ALU -------------------------------------------------------------

    /// <summary>ADD, ADC, SUB, SBB, ANA, XRA, ORA, CMP by their three-bit code.</summary>
    private void Arithmetic(int operation, byte value)
    {
        int a = A, result, flags;
        switch (operation)
        {
            case 0:
            case 1:
            {
                var carry = operation == 1 && FlagSet(FlagC) ? 1 : 0;
                result = a + value + carry;
                var into7 = ((a & 0x7F) + (value & 0x7F) + carry) > 0x7F;
                var out7 = result > 0xFF;
                flags = Szp(result) | ((((a & 0xF) + (value & 0xF) + carry) > 0xF) ? FlagAC : 0) | (out7 ? FlagC : 0) | VK(into7 ^ out7, result);
                break;
            }

            case 2:
            case 3:
            case 7:
            {
                // A + ~value + 1 (less a borrow): AC is the carry out of bit 3 of that sum, CY the borrow.
                var borrow = operation == 3 && FlagSet(FlagC) ? 1 : 0;
                var inverse = ~value & 0xFF;
                result = a + inverse + (1 - borrow);
                var into7 = ((a & 0x7F) + (inverse & 0x7F) + (1 - borrow)) > 0x7F;
                var out7 = result > 0xFF;
                flags = Szp(result) | ((((a & 0xF) + (inverse & 0xF) + (1 - borrow)) > 0xF) ? FlagAC : 0) | (out7 ? 0 : FlagC) | VK(into7 ^ out7, result);
                break;
            }

            case 4:
                // ANA: CY cleared and AC set on the 8085 (the 8080 ORs bit 3 of the operands); V clear, K = S.
                result = a & value;
                flags = Szp(result) | FlagAC | VK(false, result);
                break;

            case 5:
                result = a ^ value;
                flags = Szp(result) | VK(false, result);
                break;

            default:
                result = a | value;
                flags = Szp(result) | VK(false, result);
                break;
        }

        _f = (byte)flags;
        if (operation != 7) A = (byte)result;
    }

    /// <summary>INR: everything but CY. V only for $7F to $80.</summary>
    private byte Increment(byte value)
    {
        var result = (value + 1) & 0xFF;
        _f = (byte)((_f & FlagC) | Szp(result) | ((value & 0xF) == 0xF ? FlagAC : 0) | VK(value == 0x7F, result));
        return (byte)result;
    }

    /// <summary>DCR: value + $FF in the ALU, so AC is set unless the low digit was 0. V only for $80 to $7F.</summary>
    private byte Decrement(byte value)
    {
        var result = (value - 1) & 0xFF;
        _f = (byte)((_f & FlagC) | Szp(result) | ((value & 0xF) != 0 ? FlagAC : 0) | VK(value == 0x80, result));
        return (byte)result;
    }

    /// <summary>RLC, RRC, RAL, RAR, DAA, CMA, STC, CMC.</summary>
    private void AccumulatorOperation(int y)
    {
        int a = A;
        var carry = FlagSet(FlagC) ? 1 : 0;
        const int keep = ~(FlagC | FlagV | FlagK) & 0xFF;
        switch (y)
        {
            case 0:
            {
                // RLC adds A to itself: V is A7 ^ A6, as an addition's.
                var result = ((a << 1) | (a >> 7)) & 0xFF;
                A = (byte)result;
                _f = (byte)((_f & keep) | (a >> 7) | VK(((a ^ (a << 1)) & 0x80) != 0, result));
                break;
            }

            case 1:
            {
                // RRC shifts right: V clear.
                var result = ((a >> 1) | (a << 7)) & 0xFF;
                A = (byte)result;
                _f = (byte)((_f & keep) | (a & 1) | VK(false, result));
                break;
            }

            case 2:
            {
                var result = ((a << 1) | carry) & 0xFF;
                A = (byte)result;
                _f = (byte)((_f & keep) | (a >> 7) | VK(((a ^ (a << 1)) & 0x80) != 0, result));
                break;
            }

            case 3:
            {
                var result = (a >> 1) | (carry << 7);
                A = (byte)result;
                _f = (byte)((_f & keep) | (a & 1) | VK(false, result));
                break;
            }

            case 4:
            {
                // DAA: 6 added to the low digit if it is over 9 or AC, $60 to the high if it is
                // then over 9 or CY — the ALU adding the correction, so AC, V and K are that sum's.
                var correction = 0;
                var newCarry = carry != 0;
                if ((a & 0x0F) > 9 || FlagSet(FlagAC)) correction |= 0x06;
                if (a > 0x99 || newCarry)
                {
                    correction |= 0x60;
                    newCarry = true;
                }

                var result = a + correction;
                var into7 = ((a & 0x7F) + (correction & 0x7F)) > 0x7F;
                var out7 = result > 0xFF;
                A = (byte)result;
                _f = (byte)(Szp(result) | ((((a & 0xF) + (correction & 0xF)) > 0xF) ? FlagAC : 0) | (newCarry ? FlagC : 0) | VK(into7 ^ out7, result));
                break;
            }

            case 5:
                A = (byte)~a;      // CMA: no flags
                break;

            case 6:
                _f = (byte)(_f | FlagC);
                break;

            default:
                _f = (byte)(_f ^ FlagC);
                break;
        }
    }
}
