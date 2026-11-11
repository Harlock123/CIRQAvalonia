namespace Cirq.Components.Processors;

/// <summary>
/// The 8051's insides with no circuit, as an AT89C2051 has them: the whole MCS-51 instruction set with its machine-cycle
/// counts (twelve clocks each), 128 bytes of RAM with the four register banks and the bit-addressable area, the SFRs,
/// ports P1 and P3 (quasi-bidirectional: a 1 in the latch lets the pin be pulled up, a 0 holds it low), timers 0 and 1
/// in modes 0-3, the UART in mode 1 (eight bits at a rate Timer 1 sets) and mode 0's shift register left out, and the
/// five interrupts with their two priority levels.
/// <para>
/// It runs an instruction at a time (<see cref="Step"/>, returning the machine cycles it took) against whoever holds it,
/// which samples the pins into <see cref="P1Pins"/> and <see cref="P3Pins"/> and reads <see cref="P1Latch"/>,
/// <see cref="P3Latch"/> and <see cref="TxLevel"/> back. Read-modify-write instructions read the latch, the rest the pins,
/// as the datasheet says.
/// </para>
/// </summary>
public sealed class I8051Core
{
    public const int CodeSize = 0x10000;

    // SFR addresses.
    public const int P0 = 0x80, SP = 0x81, DPL = 0x82, DPH = 0x83, PCON = 0x87, TCON = 0x88, TMOD = 0x89, TL0 = 0x8A, TL1 = 0x8B,
        TH0 = 0x8C, TH1 = 0x8D, P1 = 0x90, SCON = 0x98, SBUF = 0x99, P2 = 0xA0, IE = 0xA8, P3 = 0xB0, IP = 0xB8, PSW = 0xD0, ACC = 0xE0, B = 0xF0;

    public byte[] Code { get; } = new byte[CodeSize];

    private readonly byte[] _ram = new byte[128];
    private readonly byte[] _sfr = new byte[128];

    public int Pc { get; set; }

    public long Cycles { get; private set; }

    /// <summary>The pins as last sampled.</summary>
    public byte P1Pins { get; set; } = 0xFF;

    public byte P3Pins { get; set; } = 0xFF;

    public byte P1Latch => _sfr[P1 - 0x80];

    public byte P3Latch => _sfr[P3 - 0x80];

    /// <summary>TXD's level while the UART sends (P3.1's latch otherwise).</summary>
    public bool TxLevel => _txBits > 0 ? _txLine : (P3Latch & 0x02) != 0;

    public bool Transmitting => _txBits > 0;

    /// <summary>Machine cycles to a UART bit at the present setting.</summary>
    public int BitCycles => Math.Max(1, (256 - _sfr[TH1 - 0x80]) * ((_sfr[PCON - 0x80] & 0x80) != 0 ? 16 : 32));

    private int _inService;           // bit 0 low priority in service, bit 1 high
    private bool _lastT0 = true, _lastT1 = true, _lastInt0 = true, _lastInt1 = true;
    private int _txBits, _txFrame, _txCount;
    private bool _txLine = true;
    private int _rxState, _rxCount, _rxBits, _rxData;
    private bool _rxLast = true;

    public byte this[int address]
    {
        get => address < 0x80 ? _ram[address & 0x7F] : _sfr[address & 0x7F];
        set
        {
            if (address < 0x80) _ram[address & 0x7F] = value;
            else _sfr[address & 0x7F] = value;
        }
    }

    public void Reset()
    {
        Array.Clear(_ram);
        Array.Clear(_sfr);
        _sfr[SP - 0x80] = 0x07;
        _sfr[P0 - 0x80] = _sfr[P1 - 0x80] = _sfr[P2 - 0x80] = _sfr[P3 - 0x80] = 0xFF;
        Pc = 0;
        Cycles = 0;
        _inService = 0;
        (_txBits, _txLine, _rxState) = (0, true, 0);
    }

    public void LoadCode(IEnumerable<(int Address, byte[] Bytes)> blocks)
    {
        Array.Clear(Code);
        foreach (var (address, bytes) in blocks)
            for (var i = 0; i < bytes.Length && address + i < CodeSize; i++) Code[address + i] = bytes[i];
    }

    // ───── registers and memory ─────

    private byte Acc { get => _sfr[ACC - 0x80]; set => _sfr[ACC - 0x80] = value; }

    private byte Psw { get => _sfr[PSW - 0x80]; set => _sfr[PSW - 0x80] = value; }

    private bool Carry { get => (Psw & 0x80) != 0; set => Psw = (byte)(value ? Psw | 0x80 : Psw & 0x7F); }

    private int Bank => Psw & 0x18;

    private byte R(int n) => _ram[Bank + n];

    private void SetR(int n, byte v) => _ram[Bank + n] = v;

    private int Dptr { get => (_sfr[DPH - 0x80] << 8) | _sfr[DPL - 0x80]; set { _sfr[DPH - 0x80] = (byte)(value >> 8); _sfr[DPL - 0x80] = (byte)value; } }

    public int StackPointer => _sfr[SP - 0x80];

    public byte StatusWord => Psw;

    public byte Accumulator => Acc;

    /// <summary>A direct address: RAM below 0x80, an SFR above; a port read from its pins, or (for read-modify-write) its latch.</summary>
    private byte Direct(int address, bool latch = false)
    {
        if (address < 0x80) return _ram[address];
        if (!latch && address == P1) return (byte)(P1Pins & 0xFF);
        if (!latch && address == P3) return P3Pins;
        return _sfr[address - 0x80];
    }

    private void SetDirect(int address, byte value)
    {
        if (address < 0x80)
        {
            _ram[address] = value;
            return;
        }

        if (address == SBUF)
        {
            // Writing SBUF starts a transmission in mode 1: start bit, eight data bits LSB first, the stop bit.
            _txFrame = (value << 1) | 0x200;
            _txBits = 10;
            _txCount = 0;
            _txLine = true;
            return;
        }

        _sfr[address - 0x80] = value;
    }

    private byte _rxBuffer;

    private byte ReadDirect(int address, bool latch = false) => address == SBUF ? _rxBuffer : Direct(address, latch);

    private byte Indirect(int r) => _ram[R(r) & 0x7F];

    private void SetIndirect(int r, byte v) => _ram[R(r) & 0x7F] = v;

    private (int Address, int Mask) BitAddress(int bit) =>
        bit < 0x80 ? (0x20 + (bit >> 3), 1 << (bit & 7)) : (bit & 0xF8, 1 << (bit & 7));

    private bool GetBit(int bit, bool latch = false)
    {
        var (address, mask) = BitAddress(bit);
        return (ReadDirect(address, latch) & mask) != 0;
    }

    private void SetBit(int bit, bool value)
    {
        var (address, mask) = BitAddress(bit);
        var current = Direct(address, latch: true);
        SetDirect(address, (byte)(value ? current | mask : current & ~mask));
    }

    private byte Fetch() => Code[Pc++ & 0xFFFF];

    private void Push(byte v)
    {
        var sp = (byte)(_sfr[SP - 0x80] + 1);
        _sfr[SP - 0x80] = sp;
        _ram[sp & 0x7F] = v;
    }

    private byte Pop()
    {
        var sp = _sfr[SP - 0x80];
        _sfr[SP - 0x80] = (byte)(sp - 1);
        return _ram[sp & 0x7F];
    }

    private int Relative(int offset) => (Pc + (sbyte)offset) & 0xFFFF;

    // ───── arithmetic ─────

    private void Add(byte operand, bool withCarry)
    {
        var a = Acc;
        var c = withCarry && Carry ? 1 : 0;
        var sum = a + operand + c;
        var psw = Psw & 0x3B;
        if (sum > 0xFF) psw |= 0x80;
        if (((a & 0x0F) + (operand & 0x0F) + c) > 0x0F) psw |= 0x40;
        if ((~(a ^ operand) & (a ^ sum) & 0x80) != 0) psw |= 0x04;
        Psw = (byte)psw;
        Acc = (byte)sum;
    }

    private void Subtract(byte operand)
    {
        var a = Acc;
        var c = Carry ? 1 : 0;
        var diff = a - operand - c;
        var psw = Psw & 0x3B;
        if (diff < 0) psw |= 0x80;
        if (((a & 0x0F) - (operand & 0x0F) - c) < 0) psw |= 0x40;
        if (((a ^ operand) & (a ^ diff) & 0x80) != 0) psw |= 0x04;
        Psw = (byte)psw;
        Acc = (byte)diff;
    }

    private void UpdateParity()
    {
        var a = Acc;
        a ^= (byte)(a >> 4);
        a ^= (byte)(a >> 2);
        a ^= (byte)(a >> 1);
        Psw = (byte)((Psw & 0xFE) | (a & 1));
    }

    // ───── running ─────

    /// <summary>Runs one instruction (or takes an interrupt), then the timers and the UART for the cycles it took.</summary>
    public int Step()
    {
        var cycles = TakeInterrupt() ?? Execute();
        UpdateParity();
        for (var i = 0; i < cycles; i++) MachineCycle();
        Cycles += cycles;
        return cycles;
    }

    private int? TakeInterrupt()
    {
        var ie = _sfr[IE - 0x80];
        if ((ie & 0x80) == 0) return null;
        var tcon = _sfr[TCON - 0x80];
        var scon = _sfr[SCON - 0x80];
        var ip = _sfr[IP - 0x80];
        (bool Pending, int Vector, int Mask)[] sources =
        [
            ((ie & 0x01) != 0 && (tcon & 0x02) != 0, 0x03, 0x01),
            ((ie & 0x02) != 0 && (tcon & 0x20) != 0, 0x0B, 0x02),
            ((ie & 0x04) != 0 && (tcon & 0x08) != 0, 0x13, 0x04),
            ((ie & 0x08) != 0 && (tcon & 0x80) != 0, 0x1B, 0x08),
            ((ie & 0x10) != 0 && (scon & 0x03) != 0, 0x23, 0x10),
        ];
        foreach (var high in new[] { true, false })
        {
            var level = high ? 2 : 1;
            if ((_inService & 2) != 0 || ((_inService & level) != 0)) continue;
            foreach (var (pending, vector, mask) in sources)
            {
                if (!pending || ((ip & mask) != 0) != high) continue;

                // Taking it clears an edge-triggered INTx's flag and a timer's overflow flag; the UART's stay for software.
                if (vector == 0x03 && (tcon & 0x01) != 0) _sfr[TCON - 0x80] &= 0xFD;
                if (vector == 0x13 && (tcon & 0x04) != 0) _sfr[TCON - 0x80] &= 0xF7;
                if (vector == 0x0B) _sfr[TCON - 0x80] &= 0xDF;
                if (vector == 0x1B) _sfr[TCON - 0x80] &= 0x7F;
                Push((byte)Pc);
                Push((byte)(Pc >> 8));
                Pc = vector;
                _inService |= level;
                return 2;
            }
        }

        return null;
    }

    /// <summary>Samples the external interrupt and counter pins: INT0 P3.2, INT1 P3.3, T0 P3.4, T1 P3.5.</summary>
    public void SampleEdges()
    {
        var int0 = (P3Pins & 0x04) != 0;
        var int1 = (P3Pins & 0x08) != 0;
        var tcon = _sfr[TCON - 0x80];
        if ((tcon & 0x01) != 0) { if (_lastInt0 && !int0) tcon |= 0x02; }
        else tcon = (byte)(int0 ? tcon & 0xFD : tcon | 0x02);
        if ((tcon & 0x04) != 0) { if (_lastInt1 && !int1) tcon |= 0x08; }
        else tcon = (byte)(int1 ? tcon & 0xF7 : tcon | 0x08);
        _sfr[TCON - 0x80] = tcon;
        (_lastInt0, _lastInt1) = (int0, int1);
    }

    private void MachineCycle()
    {
        var tmod = _sfr[TMOD - 0x80];
        var tcon = _sfr[TCON - 0x80];
        var t0Pin = (P3Pins & 0x10) != 0;
        var t1Pin = (P3Pins & 0x20) != 0;
        var mode0 = tmod & 3;

        // Timer 0, and in mode 3 TH0 as a timer of its own on TR1.
        if ((tcon & 0x10) != 0 && ((tmod & 0x08) == 0 || (P3Pins & 0x04) != 0))
        {
            var count = (tmod & 0x04) == 0 || (_lastT0 && !t0Pin);
            if (count) Timer(0, mode0);
        }

        if (mode0 == 3 && (tcon & 0x40) != 0)
        {
            var th = (byte)(_sfr[TH0 - 0x80] + 1);
            _sfr[TH0 - 0x80] = th;
            if (th == 0) _sfr[TCON - 0x80] |= 0x80;
        }

        // Timer 1 (stopped in mode 3); its overflows clock the UART.
        var mode1 = (tmod >> 4) & 3;
        if (mode1 != 3 && (mode0 == 3 || (tcon & 0x40) != 0) && ((tmod & 0x80) == 0 || (P3Pins & 0x08) != 0))
        {
            var count = (tmod & 0x40) == 0 || (_lastT1 && !t1Pin);
            if (count) Timer(1, mode1);
        }

        (_lastT0, _lastT1) = (t0Pin, t1Pin);
        Uart();
    }

    /// <summary>One count of a timer in its mode; true when it overflowed.</summary>
    private bool Timer(int which, int mode)
    {
        var (tl, th, flag) = which == 0 ? (TL0 - 0x80, TH0 - 0x80, 0x20) : (TL1 - 0x80, TH1 - 0x80, 0x80);
        var overflow = false;
        switch (mode)
        {
            case 0:
                var v13 = (((_sfr[th] << 5) | (_sfr[tl] & 0x1F)) + 1) & 0x1FFF;
                (_sfr[th], _sfr[tl]) = ((byte)(v13 >> 5), (byte)((_sfr[tl] & 0xE0) | (v13 & 0x1F)));
                overflow = v13 == 0;
                break;
            case 1:
                var v16 = (((_sfr[th] << 8) | _sfr[tl]) + 1) & 0xFFFF;
                (_sfr[th], _sfr[tl]) = ((byte)(v16 >> 8), (byte)v16);
                overflow = v16 == 0;
                break;
            default:
                var v8 = (byte)(_sfr[tl] + 1);
                overflow = v8 == 0;
                _sfr[tl] = overflow && mode == 2 ? _sfr[th] : v8;
                break;
        }

        if (overflow && !(which == 1 && (_sfr[TMOD - 0x80] & 3) == 3)) _sfr[TCON - 0x80] |= (byte)flag;
        return overflow;
    }

    /// <summary>The UART in mode 1 (or 3 without its ninth bit), timed in machine cycles at the bit rate Timer 1 sets.</summary>
    private void Uart()
    {
        var bit = BitCycles;
        if (_txBits > 0)
        {
            // Each bit goes on the line as its time starts, and holds for a bit's worth of machine cycles.
            if (_txCount == 0)
            {
                _txLine = (_txFrame & 1) != 0;
                _txFrame >>= 1;
            }

            if (++_txCount >= bit)
            {
                _txCount = 0;
                if (--_txBits == 0)
                {
                    _txLine = true;
                    _sfr[SCON - 0x80] |= 0x02;   // TI, once the stop bit is out
                }
            }
        }

        var rx = (P3Pins & 0x01) != 0;
        if ((_sfr[SCON - 0x80] & 0x10) != 0)
        {
            switch (_rxState)
            {
                case 0:
                    if (_rxLast && !rx) (_rxState, _rxCount, _rxBits, _rxData) = (1, bit + (bit / 2), 0, 0);
                    break;
                default:
                    if (--_rxCount > 0) break;
                    if (_rxBits < 8)
                    {
                        if (rx) _rxData |= 1 << _rxBits;
                        _rxBits++;
                        _rxCount = bit;
                    }
                    else
                    {
                        if (rx && (_sfr[SCON - 0x80] & 0x01) == 0)
                        {
                            _rxBuffer = (byte)_rxData;
                            _sfr[SCON - 0x80] |= 0x01;   // RI
                        }

                        _rxState = 0;
                    }

                    break;
            }
        }

        _rxLast = rx;
    }

    // ───── the instructions ─────

    private static readonly int[] TwoCycle =
    [
        0x02, 0x12, 0x22, 0x32, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80, 0x43, 0x53, 0x63, 0x72, 0x82, 0xA0, 0xB0, 0x92, 0x73,
        0x75, 0x83, 0x93, 0x85, 0x86, 0x87, 0x90, 0xA3, 0xA6, 0xA7, 0xC0, 0xD0, 0xD5, 0xE0, 0xE2, 0xE3, 0xF0, 0xF2, 0xF3,
        0xB4, 0xB5, 0xB6, 0xB7,
    ];

    private static readonly bool[] TwoCycles = BuildCycles();

    private static bool[] BuildCycles()
    {
        var two = new bool[256];
        foreach (var op in TwoCycle) two[op] = true;
        for (var op = 0; op < 256; op++)
        {
            if ((op & 0x1F) == 0x01 || (op & 0x1F) == 0x11) two[op] = true;      // AJMP, ACALL
            if (op is >= 0x88 and <= 0x8F or >= 0xA8 and <= 0xAF or >= 0xB8 and <= 0xBF or >= 0xD8 and <= 0xDF) two[op] = true;
        }

        return two;
    }

    private int Execute()
    {
        var op = Fetch();
        var cycles = op is 0xA4 or 0x84 ? 4 : TwoCycles[op] ? 2 : 1;
        var low = op & 0x0F;
        var rn = op & 0x07;
        var ri = op & 0x01;

        // AJMP and ACALL: an 11-bit address within the 2 KB page of the next instruction.
        if ((op & 0x1F) == 0x01 || (op & 0x1F) == 0x11)
        {
            var target = ((Pc + 1) & 0xF800) | ((op & 0xE0) << 3) | Fetch();
            if ((op & 0x10) != 0)
            {
                Push((byte)Pc);
                Push((byte)(Pc >> 8));
            }

            Pc = target;
            return cycles;
        }

        // The regular rows: operations on A with #data, direct, @Ri or Rn in the low nibble's 4-F.
        byte Operand(bool rmw = false) => low switch
        {
            4 => Fetch(),
            5 => ReadDirect(Fetch(), rmw),
            6 or 7 => Indirect(ri),
            _ => R(rn),
        };

        var high = op >> 4;
        if (low >= 4)
        {
            switch (high)
            {
                case 0x0 when low >= 5: // INC
                    Modify(low, ri, rn, v => (byte)(v + 1));
                    return cycles;
                case 0x1 when low >= 5: // DEC
                    Modify(low, ri, rn, v => (byte)(v - 1));
                    return cycles;
                case 0x2: Add(Operand(), false); return cycles;
                case 0x3: Add(Operand(), true); return cycles;
                case 0x4 when low >= 4: Acc = (byte)(Acc | Operand()); return cycles;
                case 0x5 when low >= 4: Acc = (byte)(Acc & Operand()); return cycles;
                case 0x6 when low >= 4: Acc = (byte)(Acc ^ Operand()); return cycles;
                case 0x9: Subtract(Operand()); return cycles;
                case 0xE when low >= 5: Acc = Operand(); return cycles;
            }
        }

        switch (op)
        {
            case 0x00: break;
            case 0x02: { var hi = Fetch(); Pc = (hi << 8) | Fetch(); break; }
            case 0x12:
            {
                var target = (Fetch() << 8) | Fetch();
                Push((byte)Pc);
                Push((byte)(Pc >> 8));
                Pc = target;
                break;
            }

            case 0x22: { var hi = Pop(); Pc = (hi << 8) | Pop(); break; }
            case 0x32:
            {
                var hi = Pop();
                Pc = (hi << 8) | Pop();
                _inService = (_inService & 2) != 0 ? _inService & ~2 : _inService & ~1;
                break;
            }

            case 0x03: Acc = (byte)((Acc >> 1) | (Acc << 7)); break;
            case 0x13: { var c = Carry; Carry = (Acc & 1) != 0; Acc = (byte)((Acc >> 1) | (c ? 0x80 : 0)); break; }
            case 0x23: Acc = (byte)((Acc << 1) | (Acc >> 7)); break;
            case 0x33: { var c = Carry; Carry = (Acc & 0x80) != 0; Acc = (byte)((Acc << 1) | (c ? 1 : 0)); break; }
            case 0x04: Acc++; break;
            case 0x14: Acc--; break;
            case 0x10: { var bit = Fetch(); var rel = Fetch(); if (GetBit(bit, latch: true)) { SetBit(bit, false); Pc = Relative(rel); } break; }
            case 0x20: { var bit = Fetch(); var rel = Fetch(); if (GetBit(bit)) Pc = Relative(rel); break; }
            case 0x30: { var bit = Fetch(); var rel = Fetch(); if (!GetBit(bit)) Pc = Relative(rel); break; }
            case 0x40: { var rel = Fetch(); if (Carry) Pc = Relative(rel); break; }
            case 0x50: { var rel = Fetch(); if (!Carry) Pc = Relative(rel); break; }
            case 0x60: { var rel = Fetch(); if (Acc == 0) Pc = Relative(rel); break; }
            case 0x70: { var rel = Fetch(); if (Acc != 0) Pc = Relative(rel); break; }
            case 0x80: { var rel = Fetch(); Pc = Relative(rel); break; }
            case 0x42: { var a = Fetch(); SetDirect(a, (byte)(Direct(a, true) | Acc)); break; }
            case 0x43: { var a = Fetch(); SetDirect(a, (byte)(Direct(a, true) | Fetch())); break; }
            case 0x52: { var a = Fetch(); SetDirect(a, (byte)(Direct(a, true) & Acc)); break; }
            case 0x53: { var a = Fetch(); SetDirect(a, (byte)(Direct(a, true) & Fetch())); break; }
            case 0x62: { var a = Fetch(); SetDirect(a, (byte)(Direct(a, true) ^ Acc)); break; }
            case 0x63: { var a = Fetch(); SetDirect(a, (byte)(Direct(a, true) ^ Fetch())); break; }
            case 0x72: Carry |= GetBit(Fetch()); break;
            case 0x82: Carry &= GetBit(Fetch()); break;
            case 0xA0: Carry |= !GetBit(Fetch()); break;
            case 0xB0: Carry &= !GetBit(Fetch()); break;
            case 0x73: Pc = (Acc + Dptr) & 0xFFFF; break;
            case 0x74: Acc = Fetch(); break;
            case 0x75: { var a = Fetch(); SetDirect(a, Fetch()); break; }
            case 0x76 or 0x77: SetIndirect(ri, Fetch()); break;
            case >= 0x78 and <= 0x7F: SetR(rn, Fetch()); break;
            case 0x83: Acc = Code[(Acc + Pc) & 0xFFFF]; break;
            case 0x84:
            {
                var b = _sfr[B - 0x80];
                var psw = Psw & 0x7B;
                if (b == 0) psw |= 0x04;
                else
                {
                    var (q, r) = (Acc / b, Acc % b);
                    Acc = (byte)q;
                    _sfr[B - 0x80] = (byte)r;
                }

                Psw = (byte)psw;
                break;
            }

            case 0x85: { var src = Fetch(); var dst = Fetch(); SetDirect(dst, ReadDirect(src)); break; }
            case 0x86 or 0x87: SetDirect(Fetch(), Indirect(ri)); break;
            case >= 0x88 and <= 0x8F: SetDirect(Fetch(), R(rn)); break;
            case 0x90: { var hi = Fetch(); Dptr = (hi << 8) | Fetch(); break; }
            case 0x92: SetBit(Fetch(), Carry); break;
            case 0x93: Acc = Code[(Acc + Dptr) & 0xFFFF]; break;
            case 0xA2: Carry = GetBit(Fetch()); break;
            case 0xA3: Dptr = (Dptr + 1) & 0xFFFF; break;
            case 0xA4:
            {
                var product = Acc * _sfr[B - 0x80];
                Acc = (byte)product;
                _sfr[B - 0x80] = (byte)(product >> 8);
                Psw = (byte)((Psw & 0x7B) | (product > 0xFF ? 0x04 : 0));
                break;
            }

            case 0xA6 or 0xA7: SetIndirect(ri, ReadDirect(Fetch())); break;
            case >= 0xA8 and <= 0xAF: SetR(rn, ReadDirect(Fetch())); break;
            case 0xB2: { var bit = Fetch(); SetBit(bit, !GetBit(bit, latch: true)); break; }
            case 0xB3: Carry = !Carry; break;
            case 0xB4: Compare(Acc, Fetch()); break;
            case 0xB5: Compare(Acc, ReadDirect(Fetch())); break;
            case 0xB6 or 0xB7: Compare(Indirect(ri), Fetch()); break;
            case >= 0xB8 and <= 0xBF: Compare(R(rn), Fetch()); break;
            case 0xC0: Push(ReadDirect(Fetch())); break;
            case 0xC2: SetBit(Fetch(), false); break;
            case 0xC3: Carry = false; break;
            case 0xC4: Acc = (byte)((Acc << 4) | (Acc >> 4)); break;
            case 0xC5: { var a = Fetch(); var v = ReadDirect(a, true); SetDirect(a, Acc); Acc = v; break; }
            case 0xC6 or 0xC7: { var v = Indirect(ri); SetIndirect(ri, Acc); Acc = v; break; }
            case >= 0xC8 and <= 0xCF: { var v = R(rn); SetR(rn, Acc); Acc = v; break; }
            case 0xD0: SetDirect(Fetch(), Pop()); break;
            case 0xD2: SetBit(Fetch(), true); break;
            case 0xD3: Carry = true; break;
            case 0xD4:
            {
                var a = (int)Acc;
                if ((a & 0x0F) > 9 || (Psw & 0x40) != 0) a += 6;
                if (a > 0xFF) Carry = true;
                if (((a >> 4) & 0x1F) > 9 || Carry) a += 0x60;
                if (a > 0xFF) Carry = true;
                Acc = (byte)a;
                break;
            }

            case 0xD5: { var a = Fetch(); var rel = Fetch(); var v = (byte)(Direct(a, true) - 1); SetDirect(a, v); if (v != 0) Pc = Relative(rel); break; }
            case 0xD6 or 0xD7: { var v = Indirect(ri); SetIndirect(ri, (byte)((v & 0xF0) | (Acc & 0x0F))); Acc = (byte)((Acc & 0xF0) | (v & 0x0F)); break; }
            case >= 0xD8 and <= 0xDF: { var rel = Fetch(); var v = (byte)(R(rn) - 1); SetR(rn, v); if (v != 0) Pc = Relative(rel); break; }
            case 0xE0 or 0xE2 or 0xE3: Acc = 0xFF; break;   // no external memory on a 2051
            case 0xE4: Acc = 0; break;
            case 0xF0 or 0xF2 or 0xF3: break;
            case 0xF4: Acc = (byte)~Acc; break;
            case 0xF5: SetDirect(Fetch(), Acc); break;
            case 0xF6 or 0xF7: SetIndirect(ri, Acc); break;
            case >= 0xF8 and <= 0xFF: SetR(rn, Acc); break;
            default: break;                                  // 0xA5 is reserved: a NOP
        }

        return cycles;
    }

    private void Modify(int low, int ri, int rn, Func<byte, byte> change)
    {
        switch (low)
        {
            case 5: { var a = Fetch(); SetDirect(a, change(Direct(a, true))); break; }
            case 6 or 7: SetIndirect(ri, change(Indirect(ri))); break;
            default: SetR(rn, change(R(rn))); break;
        }
    }

    private void Compare(byte value, byte with)
    {
        var rel = Fetch();
        Carry = value < with;
        if (value != with) Pc = Relative(rel);
    }
}
