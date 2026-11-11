namespace Cirq.Components.Processors;

/// <summary>
/// An ATmega328P's insides with no circuit: the AVR instruction set with the datasheet's cycle counts, the 32 KB flash,
/// 2 KB of SRAM, 1 KB of EEPROM, and the peripherals a sketch uses: the three GPIO ports, timers 0, 1 and 2 with their
/// PWM modes, the USART, the SPI master, the two-wire interface (I2C) as a master, the ADC, the external and pin-change
/// interrupts, and sleep.
/// <para>
/// It runs a cycle at a time against whoever holds it (<see cref="Atmega328p"/>), which samples the pins into
/// <see cref="PinInputs"/> and the analog inputs into <see cref="AnalogInputs"/>, calls <see cref="Step"/>, and watches
/// what <see cref="Driven"/> says each pin does change. Everything it holds is in <see cref="State"/>, so a run can be copied and put back.
/// </para>
/// </summary>
public sealed class AvrCore
{
    public const int FlashWords = 16384;
    public const int DataSize = 0x900;
    public const int EepromSize = 1024;

    // ---- data-space addresses of the registers that do something ----
    public const int PINB = 0x23, DDRB = 0x24, PORTB = 0x25, PINC = 0x26, DDRC = 0x27, PORTC = 0x28, PIND = 0x29, DDRD = 0x2A, PORTD = 0x2B;
    public const int TIFR0 = 0x35, TIFR1 = 0x36, TIFR2 = 0x37, PCIFR = 0x3B, EIFR = 0x3C, EIMSK = 0x3D;
    public const int EECR = 0x3F, EEDR = 0x40, EEARL = 0x41, EEARH = 0x42;
    public const int TCCR0A = 0x44, TCCR0B = 0x45, TCNT0 = 0x46, OCR0A = 0x47, OCR0B = 0x48;
    public const int SMCR = 0x53, SPL = 0x5D, SPH = 0x5E, SREG = 0x5F;
    public const int PCICR = 0x68, EICRA = 0x69, PCMSK0 = 0x6B, PCMSK1 = 0x6C, PCMSK2 = 0x6D;
    public const int TIMSK0 = 0x6E, TIMSK1 = 0x6F, TIMSK2 = 0x70;
    public const int ADCL = 0x78, ADCH = 0x79, ADCSRA = 0x7A, ADCSRB = 0x7B, ADMUX = 0x7C;
    public const int TCCR1A = 0x80, TCCR1B = 0x81, TCCR1C = 0x82, TCNT1L = 0x84, TCNT1H = 0x85, ICR1L = 0x86, ICR1H = 0x87;
    public const int OCR1AL = 0x88, OCR1AH = 0x89, OCR1BL = 0x8A, OCR1BH = 0x8B;
    public const int TCCR2A = 0xB0, TCCR2B = 0xB1, TCNT2 = 0xB2, OCR2A = 0xB3, OCR2B = 0xB4;
    public const int UCSR0A = 0xC0, UCSR0B = 0xC1, UCSR0C = 0xC2, UBRR0L = 0xC4, UBRR0H = 0xC5, UDR0 = 0xC6;
    public const int SPCR = 0x4C, SPSR = 0x4D, SPDR = 0x4E;
    public const int TWBR = 0xB8, TWSR = 0xB9, TWAR = 0xBA, TWDR = 0xBB, TWCR = 0xBC;

    // ---- interrupt vectors, as the datasheet numbers them ----
    public const int VectorInt0 = 1, VectorInt1 = 2, VectorPcint0 = 3, VectorPcint1 = 4, VectorPcint2 = 5;
    public const int VectorTimer2CompA = 7, VectorTimer2CompB = 8, VectorTimer2Ovf = 9;
    public const int VectorTimer1CompA = 11, VectorTimer1CompB = 12, VectorTimer1Ovf = 13;
    public const int VectorTimer0CompA = 14, VectorTimer0CompB = 15, VectorTimer0Ovf = 16;
    public const int VectorUsartRx = 18, VectorUsartUdre = 19, VectorUsartTx = 20, VectorAdc = 21;
    public const int VectorSpi = 17, VectorTwi = 24;

    /// <summary>Everything the chip holds: copied whole to keep a run's end state.</summary>
    public sealed class Registers
    {
        public ushort[] Flash = new ushort[FlashWords];
        public byte[] Data = new byte[DataSize];
        public byte[] Eeprom = new byte[EepromSize];
        public int Pc;
        public long Cycles;
        public bool Sleeping;

        // Prescaler counts, the 16-bit timer's TEMP, the counting direction of phase-correct modes.
        public int Prescale0, Prescale2, Prescale1, AdcPrescale;
        public byte Temp1;
        public bool Down0, Down1, Down2;
        public bool[] Oc = new bool[6];

        // The USART: the transmit shift register and the line; the receiver's progress.
        public int TxBits, TxFrame, TxBitCycles, TxCountdown;
        public byte TxBuffer;
        public bool TxPending;
        public bool TxLine = true;
        public int RxState, RxBits, RxData, RxCountdown, RxBitCycles;
        public bool RxLast = true;

        // The ADC: cycles left of a conversion.
        public int AdcCycles = -1;
        public bool AdcFirst = true;

        // The SPI master: half clocks left of a byte, the countdown to the next, the shift registers and the pins.
        public int SpiHalves, SpiCountdown, SpiOut, SpiIn;
        public bool SpiSck, SpiMosi;

        // The two-wire interface: what it's doing (0 idle, 1 start, 2 stop, 3 a byte), its step in that, the countdown,
        // the bit, the byte, whether it's reading; whether it owns the bus, has sent an address and is receiving; and
        // whether it's pulling each line low.
        public int TwiAction, TwiPhase, TwiCountdown, TwiBit, TwiByte;
        public bool TwiOwns, TwiAddressed, TwiReceiving, TwiSdaLow, TwiSclLow, TwiAck;

        // Last seen levels, for edges.
        public byte LastPinB, LastPinC, LastPinD;
        public bool LastInt0 = true, LastInt1 = true;

        public Registers Clone()
        {
            var r = (Registers)MemberwiseClone();
            r.Flash = (ushort[])Flash.Clone();
            r.Data = (byte[])Data.Clone();
            r.Eeprom = (byte[])Eeprom.Clone();
            r.Oc = (bool[])Oc.Clone();
            return r;
        }
    }

    public Registers State { get; set; } = new();

    /// <summary>
    /// An ATtiny85 rather than an ATmega328P: the same core, its I/O registers at the tiny's addresses (translated here to
    /// the mega's, their bits moved where they differ), its 512 bytes of SRAM at 0x60-0x25F, one-word interrupt vectors,
    /// Timer0's outputs on PB0 and PB1. The registers it has and the mega doesn't (Timer1's, the USI's) are held, not run.
    /// </summary>
    public bool Tiny { get; init; }

    private readonly byte[] _tinyShadow = new byte[0x40];

    private static readonly Dictionary<int, int> TinyToMega = new()
    {
        [0x36] = PINB, [0x37] = DDRB, [0x38] = PORTB,
        [0x4A] = TCCR0A, [0x53] = TCCR0B, [0x52] = TCNT0, [0x49] = OCR0A, [0x48] = OCR0B,
        [0x35] = PCMSK0, [0x26] = ADCSRA, [0x25] = ADCH, [0x24] = ADCL, [0x23] = ADCSRB,
        [0x3C] = EECR, [0x3D] = EEDR, [0x3E] = EEARL, [0x3F] = EEARH,
        [0x5D] = SPL, [0x5E] = SPH, [0x5F] = SREG,
    };

    private static int Bit(int value, int from, int to) => ((value >> from) & 1) << to;

    private byte ReadTiny(int address)
    {
        if (TinyToMega.TryGetValue(address, out var mega)) return ReadMega(mega);
        var shadow = _tinyShadow[address - 0x20];
        return address switch
        {
            0x59 => (byte)((shadow & 0x64) | Bit(D[TIMSK0], 1, 4) | Bit(D[TIMSK0], 2, 3) | Bit(D[TIMSK0], 0, 1)),
            0x58 => (byte)((shadow & 0x64) | Bit(D[TIFR0], 1, 4) | Bit(D[TIFR0], 2, 3) | Bit(D[TIFR0], 0, 1)),
            0x5B => (byte)(Bit(D[EIMSK], 0, 6) | Bit(D[PCICR], 0, 5)),
            0x5A => (byte)(Bit(D[EIFR], 0, 6) | Bit(D[PCIFR], 0, 5)),
            0x55 => (byte)((D[EICRA] & 3) | Bit(D[SMCR], 0, 5) | (((D[SMCR] >> 1) & 3) << 3) | Bit(D[0x55], 4, 6)),
            0x27 => shadow,
            _ => shadow,
        };
    }

    private void WriteTiny(int address, byte value)
    {
        if (TinyToMega.TryGetValue(address, out var mega))
        {
            WriteMega(mega, value);
            return;
        }

        _tinyShadow[address - 0x20] = value;
        switch (address)
        {
            case 0x59: D[TIMSK0] = (byte)(Bit(value, 4, 1) | Bit(value, 3, 2) | Bit(value, 1, 0)); break;
            case 0x58: WriteMega(TIFR0, (byte)(Bit(value, 4, 1) | Bit(value, 3, 2) | Bit(value, 1, 0))); break;
            case 0x5B:
                D[EIMSK] = (byte)Bit(value, 6, 0);
                D[PCICR] = (byte)Bit(value, 5, 0);
                break;
            case 0x5A:
                WriteMega(EIFR, (byte)Bit(value, 6, 0));
                WriteMega(PCIFR, (byte)Bit(value, 5, 0));
                break;
            case 0x55:
                D[EICRA] = (byte)(value & 3);
                D[SMCR] = (byte)((((value >> 3) & 3) << 1) | Bit(value, 5, 0));
                D[0x55] = (byte)Bit(value, 6, 4);
                break;
            case 0x27:
                // REFS: VCC, AREF, the 1.1 V bandgap. MUX 0x0C is the bandgap, as the mega's 0x0E.
                var reference = ((value >> 6) & 3) switch { 0 => 1, 1 => 0, _ => 3 };
                var mux = value & 0x0F;
                D[ADMUX] = (byte)((reference << 6) | (value & 0x20) | (mux == 0x0C ? 0x0E : mux));
                break;
        }
    }

    /// <summary>The pins' levels as last sampled, by port: B, C, D.</summary>
    public byte[] PinInputs { get; } = new byte[3];

    /// <summary>ADC0-ADC7, the 1.1 V bandgap (index 8) and the reference: what the ADC converts, in volts.</summary>
    public double[] AnalogInputs { get; } = new double[9];

    /// <summary>The voltage of AVCC and AREF, for the ADC's reference.</summary>
    public double AvccVolts { get; set; } = 5.0;

    public double ArefVolts { get; set; } = 5.0;

    /// <summary>A byte the USART finished sending, for a listener (the terminal's own decoder reads the TX pin).</summary>
    public event Action<byte>? Transmitted;

    // ───────────────────────────── memory ─────────────────────────────

    private byte[] D => State.Data;

    public byte this[int address]
    {
        get => D[address];
        set => D[address] = value;
    }

    private int SP
    {
        get => D[SPL] | (D[SPH] << 8);
        set
        {
            D[SPL] = (byte)value;
            D[SPH] = (byte)(value >> 8);
        }
    }

    private byte Sreg
    {
        get => D[SREG];
        set => D[SREG] = value;
    }

    private bool Flag(int bit) => (D[SREG] & (1 << bit)) != 0;

    private void SetFlag(int bit, bool on)
    {
        if (on) D[SREG] |= (byte)(1 << bit);
        else D[SREG] &= (byte)~(1 << bit);
    }

    private const int C = 0, Z = 1, N = 2, V = 3, S = 4, H = 5, T = 6, I = 7;

    /// <summary>Puts a firmware image in the flash, by byte address, little-endian words as avr-gcc writes them.</summary>
    public void LoadFlash(IEnumerable<(int Address, byte[] Bytes)> blocks)
    {
        Array.Clear(State.Flash);
        foreach (var (address, bytes) in blocks)
            for (var i = 0; i < bytes.Length; i++)
            {
                var at = address + i;
                if (at < 0 || at >= FlashWords * 2) throw new InvalidDataException($"Address ${at:X4} is beyond the 32 KB flash.");
                var word = at >> 1;
                State.Flash[word] = (at & 1) == 0 ? (ushort)((State.Flash[word] & 0xFF00) | bytes[i]) : (ushort)((State.Flash[word] & 0x00FF) | (bytes[i] << 8));
            }
    }

    /// <summary>What a power-on reset does: the registers cleared, SP to the top of SRAM, PC to 0. Flash and EEPROM stay.</summary>
    public void Reset()
    {
        var flash = State.Flash;
        var eeprom = State.Eeprom;
        State = new Registers { Flash = flash, Eeprom = eeprom };
        SP = Tiny ? 0x25F : DataSize - 1;
        Array.Clear(_tinyShadow);
        D[UCSR0A] = 0x20;
        D[UCSR0C] = 0x06;
        D[TWSR] = 0xF8;
        D[TWDR] = 0xFF;
    }

    private byte Read(int address)
    {
        address &= 0xFFFF;
        if (Tiny)
        {
            if (address >= 0x260) return 0;
            if (address >= 0x60) return D[address + 0xA0];
            if (address >= 0x20) return ReadTiny(address);
        }

        return ReadMega(address);
    }

    private byte ReadMega(int address)
    {
        if (address >= DataSize) return 0;
        switch (address)
        {
            case PINB: return PinInputs[0];
            case PINC: return (byte)(PinInputs[1] & 0x7F);
            case PIND: return PinInputs[2];
            case TCNT1L:
                State.Temp1 = D[TCNT1H];
                return D[TCNT1L];
            case TCNT1H: return State.Temp1;
            case UDR0:
                D[UCSR0A] &= 0x7F;
                return D[UDR0];
            case ADCL: return D[ADCL];
            case SPDR:
                D[SPSR] &= 0x3F;   // SPIF and WCOL go once SPDR is read after SPSR
                return D[SPDR];
            default: return D[address];
        }
    }

    private void Write(int address, byte value)
    {
        address &= 0xFFFF;
        if (Tiny)
        {
            if (address >= 0x260) return;
            if (address >= 0x60)
            {
                D[address + 0xA0] = value;
                return;
            }

            if (address >= 0x20)
            {
                WriteTiny(address, value);
                return;
            }
        }

        WriteMega(address, value);
    }

    private void WriteMega(int address, byte value)
    {
        if (address >= DataSize) return;
        switch (address)
        {
            // Writing a 1 to PINx toggles the PORTx bit.
            case PINB: D[PORTB] ^= value; return;
            case PINC: D[PORTC] ^= value; return;
            case PIND: D[PORTD] ^= value; return;

            // Interrupt flags are cleared by writing a one to them.
            case TIFR0 or TIFR1 or TIFR2 or PCIFR or EIFR:
                D[address] &= (byte)~value;
                return;
            case TCNT1H or OCR1AH or OCR1BH or ICR1H:
                State.Temp1 = value;
                return;
            case TCNT1L or OCR1AL or OCR1BL or ICR1L:
                D[address] = value;
                D[address + 1] = State.Temp1;
                return;
            case UDR0:
                D[UCSR0A] &= unchecked((byte)~0x20);
                State.TxBuffer = value;
                State.TxPending = true;
                return;
            case UCSR0A:
                D[UCSR0A] = (byte)((D[UCSR0A] & ~0x03) | (value & 0x03));
                if ((value & 0x40) != 0) D[UCSR0A] &= unchecked((byte)~0x40);
                return;
            case ADCSRA:
                // ADIF is cleared by writing a one to it, and otherwise kept as it is.
                D[ADCSRA] = (byte)((value & ~0x10) | ((value & 0x10) != 0 ? 0 : D[ADCSRA] & 0x10));
                if ((value & 0x40) != 0 && (value & 0x80) != 0 && State.AdcCycles < 0) StartConversion();
                return;
            case SPDR:
                D[SPSR] &= 0x3F;
                if (State.SpiHalves > 0) { D[SPSR] |= 0x40; return; }   // WCOL: written mid-transfer
                if ((D[SPCR] & 0x50) != 0x50) { D[SPDR] = value; return; }
                StartSpi(value);
                return;
            case SPSR:
                D[SPSR] = (byte)((D[SPSR] & 0xFE) | (value & 0x01));
                return;
            case TWCR:
                WriteTwcr(value);
                return;
            case TWSR:
                D[TWSR] = (byte)((D[TWSR] & 0xF8) | (value & 0x03));
                return;
            case TWDR:
                if ((D[TWCR] & 0x80) == 0 && State.TwiAction != 0) { D[TWCR] |= 0x08; return; }   // TWWC
                D[TWDR] = value;
                return;
            case EECR:
                D[EECR] = (byte)(value & 0x3F);
                if ((value & 0x01) != 0) D[EEDR] = State.Eeprom[(D[EEARL] | (D[EEARH] << 8)) & (EepromSize - 1)];
                if ((value & 0x02) != 0)
                {
                    State.Eeprom[(D[EEARL] | (D[EEARH] << 8)) & (EepromSize - 1)] = D[EEDR];
                    D[EECR] &= unchecked((byte)~0x06);
                }

                return;
            default:
                D[address] = value;
                return;
        }
    }

    private ushort Fetch() => State.Flash[State.Pc++ & (FlashWords - 1)];

    /// <summary>Where an SRAM address is in <see cref="Registers.Data"/>: the tiny's 0x60 is the mega's 0x100.</summary>
    private int RamIndex(int address) => Tiny ? (address & 0x3FF) + 0xA0 : address & 0xFFF;

    private void Push(byte value)
    {
        D[RamIndex(SP)] = value;
        SP--;
    }

    private byte Pop()
    {
        SP++;
        return D[RamIndex(SP)];
    }

    private void PushPc(int pc)
    {
        Push((byte)pc);
        Push((byte)(pc >> 8));
    }

    private int PopPc()
    {
        var high = Pop();
        var low = Pop();
        return (high << 8) | low;
    }

    /// <summary>The length in words of the instruction at <paramref name="pc"/>: two for JMP, CALL, LDS and STS.</summary>
    private int Length(int pc)
    {
        var op = State.Flash[pc & (FlashWords - 1)];
        return (op & 0xFE0E) == 0x940C || (op & 0xFE0E) == 0x940E || (op & 0xFE0F) == 0x9000 || (op & 0xFE0F) == 0x9200 ? 2 : 1;
    }

    // ───────────────────────────── running ─────────────────────────────

    /// <summary>
    /// Runs one instruction (or, asleep, one cycle; or takes an interrupt) and lets the peripherals catch up with the cycles
    /// it took. Returns how many cycles.
    /// </summary>
    public int Step()
    {
        int cycles;
        if (TakeInterrupt() is { } taken) cycles = taken;
        else if (State.Sleeping) cycles = 1;
        else cycles = Execute();

        State.Cycles += cycles;
        Tick(cycles);
        return cycles;
    }

    private int? TakeInterrupt()
    {
        if (!Flag(I)) return null;
        var vector = PendingVector();
        if (vector == 0) return null;
        Acknowledge(vector);
        var wake = State.Sleeping ? 4 : 0;
        State.Sleeping = false;
        PushPc(State.Pc);
        SetFlag(I, false);
        State.Pc = Tiny ? vector switch
        {
            VectorInt0 => 1,
            VectorPcint0 => 2,
            VectorTimer0Ovf => 5,
            VectorAdc => 8,
            VectorTimer0CompA => 10,
            VectorTimer0CompB => 11,
            _ => 0,
        } : vector * 2;
        return 4 + wake;
    }

    /// <summary>The highest-priority interrupt both flagged and enabled (the lowest vector), or 0.</summary>
    public int PendingVector()
    {
        if ((D[EIMSK] & 0x01) != 0 && (D[EIFR] & 0x01) != 0) return VectorInt0;
        if ((D[EIMSK] & 0x02) != 0 && (D[EIFR] & 0x02) != 0) return VectorInt1;
        for (var i = 0; i < 3; i++)
            if ((D[PCICR] & (1 << i)) != 0 && (D[PCIFR] & (1 << i)) != 0) return VectorPcint0 + i;
        if ((D[TIMSK2] & 0x02) != 0 && (D[TIFR2] & 0x02) != 0) return VectorTimer2CompA;
        if ((D[TIMSK2] & 0x04) != 0 && (D[TIFR2] & 0x04) != 0) return VectorTimer2CompB;
        if ((D[TIMSK2] & 0x01) != 0 && (D[TIFR2] & 0x01) != 0) return VectorTimer2Ovf;
        if ((D[TIMSK1] & 0x02) != 0 && (D[TIFR1] & 0x02) != 0) return VectorTimer1CompA;
        if ((D[TIMSK1] & 0x04) != 0 && (D[TIFR1] & 0x04) != 0) return VectorTimer1CompB;
        if ((D[TIMSK1] & 0x01) != 0 && (D[TIFR1] & 0x01) != 0) return VectorTimer1Ovf;
        if ((D[TIMSK0] & 0x02) != 0 && (D[TIFR0] & 0x02) != 0) return VectorTimer0CompA;
        if ((D[TIMSK0] & 0x04) != 0 && (D[TIFR0] & 0x04) != 0) return VectorTimer0CompB;
        if ((D[TIMSK0] & 0x01) != 0 && (D[TIFR0] & 0x01) != 0) return VectorTimer0Ovf;
        if ((D[SPCR] & 0x80) != 0 && (D[SPSR] & 0x80) != 0) return VectorSpi;
        if ((D[UCSR0B] & 0x80) != 0 && (D[UCSR0A] & 0x80) != 0) return VectorUsartRx;
        if ((D[UCSR0B] & 0x20) != 0 && (D[UCSR0A] & 0x20) != 0) return VectorUsartUdre;
        if ((D[UCSR0B] & 0x40) != 0 && (D[UCSR0A] & 0x40) != 0) return VectorUsartTx;
        if ((D[ADCSRA] & 0x08) != 0 && (D[ADCSRA] & 0x10) != 0) return VectorAdc;
        if ((D[TWCR] & 0x05) == 0x05 && (D[TWCR] & 0x80) != 0) return VectorTwi;
        return 0;
    }

    /// <summary>What taking an interrupt clears by itself: the flag of an edge or a timer, as the hardware does.</summary>
    private void Acknowledge(int vector)
    {
        switch (vector)
        {
            case VectorInt0: D[EIFR] &= unchecked((byte)~0x01); break;
            case VectorInt1: D[EIFR] &= unchecked((byte)~0x02); break;
            case >= VectorPcint0 and <= VectorPcint2: D[PCIFR] &= (byte)~(1 << (vector - VectorPcint0)); break;
            case VectorTimer2CompA: D[TIFR2] &= unchecked((byte)~0x02); break;
            case VectorTimer2CompB: D[TIFR2] &= unchecked((byte)~0x04); break;
            case VectorTimer2Ovf: D[TIFR2] &= unchecked((byte)~0x01); break;
            case VectorTimer1CompA: D[TIFR1] &= unchecked((byte)~0x02); break;
            case VectorTimer1CompB: D[TIFR1] &= unchecked((byte)~0x04); break;
            case VectorTimer1Ovf: D[TIFR1] &= unchecked((byte)~0x01); break;
            case VectorTimer0CompA: D[TIFR0] &= unchecked((byte)~0x02); break;
            case VectorTimer0CompB: D[TIFR0] &= unchecked((byte)~0x04); break;
            case VectorTimer0Ovf: D[TIFR0] &= unchecked((byte)~0x01); break;
            case VectorUsartTx: D[UCSR0A] &= unchecked((byte)~0x40); break;
            case VectorAdc: D[ADCSRA] &= unchecked((byte)~0x10); break;
            case VectorSpi: D[SPSR] &= 0x7F; break;
        }
    }

    // ───────────────────────────── the instructions ─────────────────────────────

    private int Execute()
    {
        var pc = State.Pc;
        var op = Fetch();
        var d5 = (op >> 4) & 0x1F;
        var r5 = (op & 0x0F) | ((op >> 5) & 0x10);
        var d4 = 16 + ((op >> 4) & 0x0F);
        var k8 = (byte)((op & 0x0F) | ((op >> 4) & 0xF0));

        switch (op >> 12)
        {
            case 0x0:
                if (op == 0) return 1; // NOP
                switch ((op >> 10) & 0x3)
                {
                    case 0:
                        if ((op & 0xFF00) == 0x0100) // MOVW
                        {
                            var dd = ((op >> 4) & 0xF) * 2;
                            var rr = (op & 0xF) * 2;
                            D[dd] = D[rr];
                            D[dd + 1] = D[rr + 1];
                            return 1;
                        }

                        if ((op & 0xFF00) == 0x0200) // MULS
                        {
                            Product((sbyte)D[16 + ((op >> 4) & 0xF)] * (sbyte)D[16 + (op & 0xF)], false);
                            return 2;
                        }

                        if ((op & 0xFF00) == 0x0300) // MULSU, FMUL, FMULS, FMULSU
                        {
                            var a = D[16 + ((op >> 4) & 0x7)];
                            var b = D[16 + (op & 0x7)];
                            var product = ((op >> 7) & 1, (op >> 3) & 1) switch
                            {
                                (0, 0) => (sbyte)a * b,
                                (0, 1) => a * b,
                                (1, 0) => (sbyte)a * (sbyte)b,
                                _ => (sbyte)a * b,
                            };
                            Product(product, (op & 0x88) != 0);
                            return 2;
                        }

                        return 1;
                    case 1: Subtract(d5, D[d5], D[r5], true, store: false); return 1; // CPC
                    case 2: Subtract(d5, D[d5], D[r5], true, store: true); return 1; // SBC
                    default: D[d5] = Add(D[d5], D[r5], false); return 1; // ADD (LSL)
                }

            case 0x1:
                switch ((op >> 10) & 0x3)
                {
                    case 0: // CPSE
                        if (D[d5] == D[r5]) return Skip();
                        return 1;
                    case 1: Subtract(d5, D[d5], D[r5], false, store: false); return 1; // CP
                    case 2: Subtract(d5, D[d5], D[r5], false, store: true); return 1; // SUB
                    default: D[d5] = Add(D[d5], D[r5], Flag(C)); return 1; // ADC (ROL)
                }

            case 0x2:
                switch ((op >> 10) & 0x3)
                {
                    case 0: D[d5] = Logic((byte)(D[d5] & D[r5])); return 1; // AND (TST)
                    case 1: D[d5] = Logic((byte)(D[d5] ^ D[r5])); return 1; // EOR (CLR)
                    case 2: D[d5] = Logic((byte)(D[d5] | D[r5])); return 1; // OR
                    default: D[d5] = D[r5]; return 1; // MOV
                }

            case 0x3: Subtract(d4, D[d4], k8, false, store: false); return 1; // CPI
            case 0x4: Subtract(d4, D[d4], k8, true, store: true); return 1; // SBCI
            case 0x5: Subtract(d4, D[d4], k8, false, store: true); return 1; // SUBI
            case 0x6: D[d4] = Logic((byte)(D[d4] | k8)); return 1; // ORI
            case 0x7: D[d4] = Logic((byte)(D[d4] & k8)); return 1; // ANDI

            case 0x8 or 0xA: // LDD / STD with displacement (LD/ST Y, Z)
            {
                var q = (op & 0x7) | ((op >> 7) & 0x18) | ((op >> 8) & 0x20);
                var pointer = (op & 0x08) != 0 ? Y : ZValue;
                if ((op & 0x0200) != 0) Write(pointer + q, D[d5]);
                else D[d5] = Read(pointer + q);
                return 2;
            }

            case 0x9: return Nine(op, d5, r5, pc);
            case 0xB: // IN / OUT
            {
                var a = (op & 0x0F) | ((op >> 5) & 0x30);
                if ((op & 0x0800) != 0) Write(0x20 + a, D[d5]);
                else D[d5] = Read(0x20 + a);
                return 1;
            }

            case 0xC: // RJMP
                State.Pc = (State.Pc + SignExtend(op & 0x0FFF, 12)) & (FlashWords - 1);
                return 2;
            case 0xD: // RCALL
                PushPc(State.Pc);
                State.Pc = (State.Pc + SignExtend(op & 0x0FFF, 12)) & (FlashWords - 1);
                return 3;
            case 0xE: D[d4] = k8; return 1; // LDI (SER)
            default: return Fifteen(op, d5);
        }
    }

    private int X
    {
        get => D[26] | (D[27] << 8);
        set
        {
            D[26] = (byte)value;
            D[27] = (byte)(value >> 8);
        }
    }

    private int Y
    {
        get => D[28] | (D[29] << 8);
        set
        {
            D[28] = (byte)value;
            D[29] = (byte)(value >> 8);
        }
    }

    private int ZValue
    {
        get => D[30] | (D[31] << 8);
        set
        {
            D[30] = (byte)value;
            D[31] = (byte)(value >> 8);
        }
    }

    private static int SignExtend(int value, int bits) => (value << (32 - bits)) >> (32 - bits);

    /// <summary>Skips the next instruction: one cycle more for a one-word instruction, two for a two-word one.</summary>
    private int Skip()
    {
        var words = Length(State.Pc);
        State.Pc = (State.Pc + words) & (FlashWords - 1);
        return 1 + words;
    }

    private int Nine(int op, int d5, int r5, int pc)
    {
        switch ((op >> 9) & 0x7)
        {
            case 0: // LD, LDS, LPM, POP
            case 1: // ST, STS, PUSH
            {
                var store = (op & 0x0200) != 0;
                switch (op & 0xF)
                {
                    case 0x0: // LDS / STS
                    {
                        var address = Fetch();
                        if (store) Write(address, D[d5]);
                        else D[d5] = Read(address);
                        return 2;
                    }

                    case 0x1 or 0x2: // Z+ / -Z
                    {
                        if ((op & 0xF) == 0x2) ZValue = (ZValue - 1) & 0xFFFF;
                        if (store) Write(ZValue, D[d5]);
                        else D[d5] = Read(ZValue);
                        if ((op & 0xF) == 0x1) ZValue = (ZValue + 1) & 0xFFFF;
                        return 2;
                    }

                    case 0x4 or 0x5 when !store: // LPM Rd,Z / Z+
                    {
                        var z = ZValue;
                        var word = State.Flash[(z >> 1) & (FlashWords - 1)];
                        D[d5] = (z & 1) == 0 ? (byte)word : (byte)(word >> 8);
                        if ((op & 0xF) == 0x5) ZValue = (z + 1) & 0xFFFF;
                        return 3;
                    }

                    case 0x9 or 0xA: // Y+ / -Y
                    {
                        if ((op & 0xF) == 0xA) Y = (Y - 1) & 0xFFFF;
                        if (store) Write(Y, D[d5]);
                        else D[d5] = Read(Y);
                        if ((op & 0xF) == 0x9) Y = (Y + 1) & 0xFFFF;
                        return 2;
                    }

                    case 0xC or 0xD or 0xE: // X, X+, -X
                    {
                        if ((op & 0xF) == 0xE) X = (X - 1) & 0xFFFF;
                        if (store) Write(X, D[d5]);
                        else D[d5] = Read(X);
                        if ((op & 0xF) == 0xD) X = (X + 1) & 0xFFFF;
                        return 2;
                    }

                    case 0xF:
                        if (store) Push(D[d5]);
                        else D[d5] = Pop();
                        return 2;
                    default:
                        return 1;
                }
            }

            case 2: // one-operand instructions and the 0x94xx/0x95xx group
            {
                switch (op & 0xF)
                {
                    case 0x0: D[d5] = Logic((byte)~D[d5]); SetFlag(C, true); UpdateS(); return 1; // COM
                    case 0x1: // NEG
                    {
                        var rd = D[d5];
                        var r = (byte)(0 - rd);
                        D[d5] = r;
                        SetFlag(H, ((r | rd) & 0x08) != 0);
                        SetFlag(V, r == 0x80);
                        SetFlag(C, r != 0);
                        SetFlag(N, (r & 0x80) != 0);
                        SetFlag(Z, r == 0);
                        UpdateS();
                        return 1;
                    }

                    case 0x2: D[d5] = (byte)((D[d5] << 4) | (D[d5] >> 4)); return 1; // SWAP
                    case 0x3: // INC
                    {
                        var r = (byte)(D[d5] + 1);
                        D[d5] = r;
                        SetFlag(V, r == 0x80);
                        SetFlag(N, (r & 0x80) != 0);
                        SetFlag(Z, r == 0);
                        UpdateS();
                        return 1;
                    }

                    case 0x5: // ASR
                    {
                        var rd = D[d5];
                        var r = (byte)((rd >> 1) | (rd & 0x80));
                        D[d5] = Shifted(r, (rd & 1) != 0);
                        return 1;
                    }

                    case 0x6: { var rd = D[d5]; D[d5] = Shifted((byte)(rd >> 1), (rd & 1) != 0); return 1; } // LSR
                    case 0x7: { var rd = D[d5]; D[d5] = Shifted((byte)((rd >> 1) | (Flag(C) ? 0x80 : 0)), (rd & 1) != 0); return 1; } // ROR
                    case 0x8:
                        if (op == 0x9508) { State.Pc = PopPc(); return 4; } // RET
                        if (op == 0x9518) { State.Pc = PopPc(); SetFlag(I, true); return 4; } // RETI
                        if (op == 0x9588) { if ((D[SMCR] & 1) != 0) State.Sleeping = true; return 1; } // SLEEP
                        if (op == 0x95C8) { var w = State.Flash[(ZValue >> 1) & (FlashWords - 1)]; D[0] = (ZValue & 1) == 0 ? (byte)w : (byte)(w >> 8); return 3; } // LPM
                        if ((op & 0xFF0F) == 0x9408) // BSET / BCLR (SEI, CLI, SEC...)
                        {
                            SetFlag((op >> 4) & 0x7, (op & 0x0080) == 0);
                            return 1;
                        }

                        return 1; // WDR, BREAK, SPM: nothing here
                    case 0x9:
                        if (op == 0x9409) { State.Pc = ZValue & (FlashWords - 1); return 2; } // IJMP
                        if (op == 0x9509) { PushPc(State.Pc); State.Pc = ZValue & (FlashWords - 1); return 3; } // ICALL
                        return 1;
                    case 0xA: // DEC
                    {
                        var r = (byte)(D[d5] - 1);
                        D[d5] = r;
                        SetFlag(V, r == 0x7F);
                        SetFlag(N, (r & 0x80) != 0);
                        SetFlag(Z, r == 0);
                        UpdateS();
                        return 1;
                    }

                    case 0xC or 0xD: // JMP
                    {
                        var high = ((op >> 3) & 0x3E) | (op & 1);
                        State.Pc = ((high << 16) | Fetch()) & (FlashWords - 1);
                        return 3;
                    }

                    case 0xE or 0xF: // CALL
                    {
                        var high = ((op >> 3) & 0x3E) | (op & 1);
                        var target = (high << 16) | Fetch();
                        PushPc(State.Pc);
                        State.Pc = target & (FlashWords - 1);
                        return 4;
                    }

                    default:
                        return 1;
                }
            }

            case 3: // ADIW / SBIW
            {
                var reg = 24 + (((op >> 4) & 0x3) * 2);
                var k = (op & 0xF) | ((op >> 2) & 0x30);
                var value = D[reg] | (D[reg + 1] << 8);
                var high = D[reg + 1];
                int r;
                if ((op & 0x0100) == 0)
                {
                    r = (value + k) & 0xFFFF;
                    SetFlag(V, (high & 0x80) == 0 && (r & 0x8000) != 0);
                    SetFlag(C, (r & 0x8000) == 0 && (high & 0x80) != 0);
                }
                else
                {
                    r = (value - k) & 0xFFFF;
                    SetFlag(V, (high & 0x80) != 0 && (r & 0x8000) == 0);
                    SetFlag(C, (r & 0x8000) != 0 && (high & 0x80) == 0);
                }

                D[reg] = (byte)r;
                D[reg + 1] = (byte)(r >> 8);
                SetFlag(N, (r & 0x8000) != 0);
                SetFlag(Z, r == 0);
                UpdateS();
                return 2;
            }

            case 4 or 5: // CBI, SBIC, SBI, SBIS
            {
                var a = 0x20 + ((op >> 3) & 0x1F);
                var bit = 1 << (op & 0x7);
                switch ((op >> 8) & 0x3)
                {
                    case 0: Write(a, (byte)(Read(a) & ~bit)); return 2;
                    case 1: return (Read(a) & bit) == 0 ? Skip() : 1;
                    case 2: Write(a, (byte)(Read(a) | bit)); return 2;
                    default: return (Read(a) & bit) != 0 ? Skip() : 1;
                }
            }

            default: // MUL
                Product(D[d5] * D[r5], false);
                return 2;
        }
    }

    private int Fifteen(int op, int d5)
    {
        var bit = op & 0x7;
        switch ((op >> 9) & 0x7)
        {
            case 0 or 1: // BRBS
                if (Flag(bit)) { State.Pc = (State.Pc + SignExtend((op >> 3) & 0x7F, 7)) & (FlashWords - 1); return 2; }
                return 1;
            case 2 or 3: // BRBC
                if (!Flag(bit)) { State.Pc = (State.Pc + SignExtend((op >> 3) & 0x7F, 7)) & (FlashWords - 1); return 2; }
                return 1;
            case 4: // BLD
                if (Flag(T)) D[d5] |= (byte)(1 << bit);
                else D[d5] &= (byte)~(1 << bit);
                return 1;
            case 5: SetFlag(T, (D[d5] & (1 << bit)) != 0); return 1; // BST
            case 6: return (D[d5] & (1 << bit)) == 0 ? Skip() : 1; // SBRC
            default: return (D[d5] & (1 << bit)) != 0 ? Skip() : 1; // SBRS
        }
    }

    // ---- the arithmetic and its flags ----

    private void UpdateS() => SetFlag(S, Flag(N) ^ Flag(V));

    private byte Add(byte a, byte b, bool carry)
    {
        var r = (byte)(a + b + (carry ? 1 : 0));
        SetFlag(H, (((a & b) | (b & ~r) | (~r & a)) & 0x08) != 0);
        SetFlag(V, (((a & b & ~r) | (~a & ~b & r)) & 0x80) != 0);
        SetFlag(C, (((a & b) | (b & ~r) | (~r & a)) & 0x80) != 0);
        SetFlag(N, (r & 0x80) != 0);
        SetFlag(Z, r == 0);
        UpdateS();
        return r;
    }

    private void Subtract(int d, byte a, byte b, bool withCarry, bool store)
    {
        var r = (byte)(a - b - (withCarry && Flag(C) ? 1 : 0));
        SetFlag(H, (((~a & b) | (b & r) | (r & ~a)) & 0x08) != 0);
        SetFlag(V, (((a & ~b & ~r) | (~a & b & r)) & 0x80) != 0);
        SetFlag(C, (((~a & b) | (b & r) | (r & ~a)) & 0x80) != 0);
        SetFlag(N, (r & 0x80) != 0);
        SetFlag(Z, withCarry ? r == 0 && Flag(Z) : r == 0);
        UpdateS();
        if (store) D[d] = r;
    }

    private byte Logic(byte r)
    {
        SetFlag(V, false);
        SetFlag(N, (r & 0x80) != 0);
        SetFlag(Z, r == 0);
        UpdateS();
        return r;
    }

    private byte Shifted(byte r, bool carry)
    {
        SetFlag(C, carry);
        SetFlag(N, (r & 0x80) != 0);
        SetFlag(Z, r == 0);
        SetFlag(V, Flag(N) ^ carry);
        UpdateS();
        return r;
    }

    private void Product(int product, bool fractional)
    {
        var p = product & 0xFFFF;
        var carry = (p & 0x8000) != 0;
        if (fractional) p = (p << 1) & 0xFFFF;
        D[0] = (byte)p;
        D[1] = (byte)(p >> 8);
        SetFlag(C, carry);
        SetFlag(Z, p == 0);
    }

    // ───────────────────────────── the peripherals ─────────────────────────────

    private void Tick(int cycles)
    {
        for (var i = 0; i < cycles; i++)
        {
            Timer8(0);
            Timer8(2);
            Timer16();
            Usart();
            Spi();
            Twi();
            Adc();
        }
    }

    private static readonly int[] Prescale01 = [0, 1, 8, 64, 256, 1024, 0, 0];
    private static readonly int[] Prescale2 = [0, 1, 8, 32, 64, 128, 256, 1024];

    /// <summary>Timer 0 or 2 one CPU cycle on: prescaled, counted, compared, flags and OC pins as the mode says.</summary>
    private void Timer8(int which)
    {
        var (ctrlA, ctrlB, count, ocrA, ocrB, flags) = which == 0 ? (TCCR0A, TCCR0B, TCNT0, OCR0A, OCR0B, TIFR0) : (TCCR2A, TCCR2B, TCNT2, OCR2A, OCR2B, TIFR2);
        var cs = D[ctrlB] & 0x7;
        var divide = which == 0 ? Prescale01[cs] : Prescale2[cs];
        if (divide == 0) return;
        if (which == 0)
        {
            if (++State.Prescale0 < divide) return;
            State.Prescale0 = 0;
        }
        else
        {
            if (++State.Prescale2 < divide) return;
            State.Prescale2 = 0;
        }

        var wgm = (D[ctrlA] & 0x3) | ((D[ctrlB] >> 1) & 0x4);
        var top = wgm is 2 or 5 or 7 ? D[ocrA] : 0xFF;
        var phase = wgm is 1 or 5;
        ref var down = ref which == 0 ? ref State.Down0 : ref State.Down2;
        var oc = which == 0 ? 0 : 4;
        var value = D[count];

        if (phase)
        {
            if (!down)
            {
                if (value >= top) { down = true; value = (byte)Math.Max(0, top - 1); }
                else value++;
            }
            else
            {
                if (value == 0) { down = false; value = 1; }
                else
                {
                    value--;
                    if (value == 0) D[flags] |= 0x01;
                }
            }
        }
        else
        {
            if (value == top)
            {
                value = 0;
                if (wgm != 2 || top == 0xFF) D[flags] |= 0x01;
                if (wgm is 3 or 7) BottomPwm(oc, D[ctrlA]);
            }
            else value++;
        }

        D[count] = value;
        if (value == D[ocrA]) { D[flags] |= 0x02; Compare(oc, (D[ctrlA] >> 6) & 3, wgm, phase, down); }
        if (value == D[ocrB]) { D[flags] |= 0x04; Compare(oc + 1, (D[ctrlA] >> 4) & 3, wgm, phase, down); }
    }

    /// <summary>Timer 1 one CPU cycle on: the sixteen-bit counter in every mode the 328P has.</summary>
    private void Timer16()
    {
        var cs = D[TCCR1B] & 0x7;
        var divide = Prescale01[cs];
        if (divide == 0) return;
        if (++State.Prescale1 < divide) return;
        State.Prescale1 = 0;

        var wgm = (D[TCCR1A] & 0x3) | ((D[TCCR1B] >> 1) & 0xC);
        var ocrA = D[OCR1AL] | (D[OCR1AH] << 8);
        var ocrB = D[OCR1BL] | (D[OCR1BH] << 8);
        var icr = D[ICR1L] | (D[ICR1H] << 8);
        var top = wgm switch
        {
            1 or 5 => 0xFF,
            2 or 6 => 0x1FF,
            3 or 7 => 0x3FF,
            4 or 9 or 11 or 15 => ocrA,
            8 or 10 or 12 or 14 => icr,
            _ => 0xFFFF,
        };
        var phase = wgm is 1 or 2 or 3 or 8 or 9 or 10 or 11;
        var value = D[TCNT1L] | (D[TCNT1H] << 8);

        if (phase)
        {
            if (!State.Down1)
            {
                if (value >= top) { State.Down1 = true; value = Math.Max(0, top - 1); }
                else value++;
            }
            else
            {
                if (value == 0) { State.Down1 = false; value = 1; }
                else
                {
                    value--;
                    if (value == 0) D[TIFR1] |= 0x01;
                }
            }
        }
        else
        {
            if (value == top)
            {
                value = 0;
                if (wgm is not (4 or 12) || top == 0xFFFF) D[TIFR1] |= 0x01;
                if (wgm is 5 or 6 or 7 or 14 or 15) BottomPwm(2, D[TCCR1A]);
            }
            else value = (value + 1) & 0xFFFF;
        }

        D[TCNT1L] = (byte)value;
        D[TCNT1H] = (byte)(value >> 8);
        if (value == ocrA) { D[TIFR1] |= 0x02; Compare(2, (D[TCCR1A] >> 6) & 3, wgm is 5 or 6 or 7 or 14 or 15 ? 3 : wgm is 4 or 12 or 0 ? 0 : 1, phase, State.Down1); }
        if (value == ocrB) { D[TIFR1] |= 0x04; Compare(3, (D[TCCR1A] >> 4) & 3, wgm is 5 or 6 or 7 or 14 or 15 ? 3 : wgm is 4 or 12 or 0 ? 0 : 1, phase, State.Down1); }
    }

    /// <summary>The OC pins of a fast-PWM timer at BOTTOM: set (non-inverting) or cleared (inverting).</summary>
    private void BottomPwm(int first, byte control)
    {
        var a = (control >> 6) & 3;
        var b = (control >> 4) & 3;
        if (a == 2) State.Oc[first] = true;
        else if (a == 3) State.Oc[first] = false;
        if (b == 2) State.Oc[first + 1] = true;
        else if (b == 3) State.Oc[first + 1] = false;
    }

    /// <summary>An OC pin at its compare match, as COM says for the mode.</summary>
    private void Compare(int pin, int com, int wgm, bool phase, bool down)
    {
        if (com == 0) return;
        var pwm = wgm is 1 or 3 or 5 or 7;
        if (!pwm && !phase)
        {
            State.Oc[pin] = com switch { 1 => !State.Oc[pin], 2 => false, _ => true };
            return;
        }

        if (com == 1) return;
        if (phase) State.Oc[pin] = com == 2 ? down : !down;
        else State.Oc[pin] = com == 3;
    }

    /// <summary>The OC pin states: OC0A, OC0B, OC1A, OC1B, OC2A, OC2B, and whether each is driving its pin.</summary>
    public bool OcLevel(int pin) => State.Oc[pin];

    public bool OcDriving(int pin)
    {
        var (control, shift) = pin switch
        {
            0 => (TCCR0A, 6),
            1 => (TCCR0A, 4),
            2 => (TCCR1A, 6),
            3 => (TCCR1A, 4),
            4 => (TCCR2A, 6),
            _ => (TCCR2A, 4),
        };
        return ((D[control] >> shift) & 3) != 0;
    }

    // ---- the USART ----

    /// <summary>Whether the transmitter owns TXD (PD1), and the line's level.</summary>
    public bool TxEnabled => (D[UCSR0B] & 0x08) != 0;

    public bool RxEnabled => (D[UCSR0B] & 0x10) != 0;

    public bool TxLevel => State.TxLine;

    /// <summary>CPU cycles per bit at the baud rate set: 16 (or 8 with U2X0) times UBRR0 + 1.</summary>
    public int BitCycles => ((D[UBRR0L] | ((D[UBRR0H] & 0x0F) << 8)) + 1) * ((D[UCSR0A] & 0x02) != 0 ? 8 : 16);

    private int FrameBits => 5 + ((D[UCSR0C] >> 1) & 0x3);

    private void Usart()
    {
        if (TxEnabled)
        {
            // The next byte goes once the last one's stop bit has run its full length.
            if (State.TxBits == 0 && State.TxCountdown <= 0 && State.TxPending)
            {
                // Start bit, the data bits LSB first, the stop bit(s).
                var bits = FrameBits;
                var stop = (D[UCSR0C] & 0x08) != 0 ? 2 : 1;
                State.TxFrame = ((State.TxBuffer & ((1 << bits) - 1)) << 1) | (((1 << stop) - 1) << (bits + 1));
                State.TxBits = 1 + bits + stop;
                State.TxBitCycles = BitCycles;
                State.TxCountdown = 0;
                State.TxPending = false;
                D[UCSR0A] |= 0x20;
                Transmitted?.Invoke(State.TxBuffer);
            }

            if (State.TxBits > 0)
            {
                if (State.TxCountdown <= 0)
                {
                    State.TxLine = (State.TxFrame & 1) != 0;
                    State.TxFrame >>= 1;
                    State.TxBits--;
                    State.TxCountdown = State.TxBitCycles;
                    if (State.TxBits == 0) State.TxCountdown = State.TxBitCycles;
                }

                State.TxCountdown--;
                if (State.TxBits == 0 && State.TxCountdown <= 0 && !State.TxPending) D[UCSR0A] |= 0x40;
            }
            else if (State.TxCountdown > 0 && --State.TxCountdown == 0 && !State.TxPending)
            {
                D[UCSR0A] |= 0x40;   // TXC: the frame is out and nothing waits behind it
            }
        }
        else
        {
            State.TxLine = true;
        }

        if (!RxEnabled)
        {
            State.RxState = 0;
            return;
        }

        var line = (PinInputs[2] & 0x01) != 0;
        switch (State.RxState)
        {
            case 0:
                if (State.RxLast && !line)
                {
                    State.RxState = 1;
                    State.RxBitCycles = BitCycles;
                    State.RxCountdown = State.RxBitCycles + (State.RxBitCycles / 2);
                    State.RxBits = 0;
                    State.RxData = 0;
                }

                break;
            default:
                if (--State.RxCountdown > 0) break;
                if (State.RxBits < FrameBits)
                {
                    if (line) State.RxData |= 1 << State.RxBits;
                    State.RxBits++;
                    State.RxCountdown = State.RxBitCycles;
                }
                else
                {
                    // The stop bit: a framing error if it isn't high; the byte into UDR0 either way.
                    if (!line) D[UCSR0A] |= 0x10;
                    else D[UCSR0A] &= unchecked((byte)~0x10);
                    if ((D[UCSR0A] & 0x80) != 0) D[UCSR0A] |= 0x08;
                    D[UDR0] = (byte)State.RxData;
                    D[UCSR0A] |= 0x80;
                    State.RxState = 0;
                }

                break;
        }

        State.RxLast = line;
    }

    // ---- the SPI master ----

    /// <summary>SPI on and the master: SPE and MSTR.</summary>
    public bool SpiMaster => (D[SPCR] & 0x50) == 0x50;

    /// <summary>CPU cycles to half an SPI clock: the divider SPR1:0 gives, halved by SPI2X.</summary>
    private int SpiHalf => Math.Max(1, (D[SPCR] & 0x03) switch { 0 => 4, 1 => 16, 2 => 64, _ => 128 } / ((D[SPSR] & 0x01) != 0 ? 2 : 1) / 2);

    private void StartSpi(byte value)
    {
        State.SpiOut = value;
        State.SpiIn = 0;
        State.SpiHalves = 16;
        State.SpiCountdown = SpiHalf;
        State.SpiSck = (D[SPCR] & 0x08) != 0;
        if ((D[SPCR] & 0x04) == 0) State.SpiMosi = SpiBit(0);   // CPHA 0: the first bit is out before the first edge
    }

    /// <summary>The <paramref name="index"/>th bit of the byte going out, in the order DORD says.</summary>
    private bool SpiBit(int index) => (State.SpiOut & (1 << ((D[SPCR] & 0x20) != 0 ? index : 7 - index))) != 0;

    private void Spi()
    {
        // In master mode, SS as an input pulled low makes the chip a slave, as the datasheet says.
        if (SpiMaster && (D[DDRB] & 0x04) == 0 && (PinInputs[0] & 0x04) == 0)
        {
            D[SPCR] &= unchecked((byte)~0x10);
            D[SPSR] |= 0x80;
            State.SpiHalves = 0;
            return;
        }

        if (State.SpiHalves == 0 || --State.SpiCountdown > 0) return;
        State.SpiCountdown = SpiHalf;
        var half = 16 - State.SpiHalves;           // 0 to 15: even halves end in a leading edge, odd in a trailing one
        var bit = half / 2;
        var leading = half % 2 == 0;
        State.SpiSck = !State.SpiSck;
        var cpha = (D[SPCR] & 0x04) != 0;
        var sample = leading != cpha;
        if (sample)
        {
            if ((PinInputs[0] & 0x10) != 0) State.SpiIn |= 1 << ((D[SPCR] & 0x20) != 0 ? bit : 7 - bit);
        }
        else if (cpha) State.SpiMosi = SpiBit(bit);
        else if (bit < 7) State.SpiMosi = SpiBit(bit + 1);

        if (--State.SpiHalves > 0) return;
        D[SPDR] = (byte)State.SpiIn;
        D[SPSR] |= 0x80;
    }

    // ---- the two-wire interface (I2C), as a master ----

    /// <summary>TWEN: the two-wire interface has SDA and SCL.</summary>
    public bool TwiEnabled => (D[TWCR] & 0x04) != 0;

    /// <summary>Whether the two-wire interface is part way through something: the pins want watching closely.</summary>
    public bool TwiBusy => TwiEnabled && State.TwiAction != 0;

    /// <summary>CPU cycles to a quarter of an I2C bit: the datasheet's F_CPU / (16 + 2·TWBR·4^TWPS), in quarters.</summary>
    public int TwiQuarter => Math.Max(1, (16 + (2 * D[TWBR] * (1 << (2 * (D[TWSR] & 0x03))))) / 4);

    private bool Sda => (PinInputs[1] & 0x10) != 0;

    private bool Scl => (PinInputs[1] & 0x20) != 0;

    private void WriteTwcr(byte value)
    {
        // TWINT is cleared by writing a one to it; TWWC isn't written.
        var flag = (value & 0x80) != 0 ? 0 : D[TWCR] & 0x80;
        D[TWCR] = (byte)((value & 0x77) | flag);
        if ((value & 0x04) == 0)
        {
            State.TwiAction = 0;
            State.TwiOwns = State.TwiSdaLow = State.TwiSclLow = false;
            return;
        }

        if ((value & 0x80) == 0) return;
        State.TwiPhase = 0;
        State.TwiCountdown = TwiQuarter * 2;
        if ((value & 0x20) != 0) State.TwiAction = 1;
        else if ((value & 0x10) != 0) State.TwiAction = 2;
        else if (State.TwiOwns)
        {
            State.TwiAction = 3;
            State.TwiBit = 0;
            State.TwiReceiving = !State.TwiAddressed ? false : State.TwiReceiving;
            State.TwiByte = State.TwiReceiving ? 0 : D[TWDR];
            State.TwiAck = (value & 0x40) != 0;
        }
    }

    private void Done(int status)
    {
        D[TWSR] = (byte)((status & 0xF8) | (D[TWSR] & 0x03));
        D[TWCR] |= 0x80;
        State.TwiAction = 0;
    }

    /// <summary>Waits out the countdown; true when it's done.</summary>
    private bool Wait() => --State.TwiCountdown <= 0;

    private void Next(int phase, int quarters = 2)
    {
        State.TwiPhase = phase;
        State.TwiCountdown = TwiQuarter * quarters;
    }

    private void Twi()
    {
        if (!TwiEnabled || State.TwiAction == 0) return;
        switch (State.TwiAction)
        {
            case 1: Start(); break;
            case 2: Stop(); break;
            case 3: Byte(); break;
        }
    }

    /// <summary>START, or a repeated START when it already has the bus: SDA falling while SCL is high.</summary>
    private void Start()
    {
        switch (State.TwiPhase)
        {
            case 0:
                // Already holding the bus (SCL low): let SDA go, then SCL, before falling again.
                if (State.TwiOwns) { State.TwiSdaLow = false; Next(1); }
                else if (Sda && Scl) { State.TwiSdaLow = true; Next(3); }
                break;
            case 1:
                if (!Wait()) break;
                State.TwiSclLow = false;
                Next(2);
                break;
            case 2:
                if (!Scl) break;   // a slave stretching the clock
                if (!Wait()) break;
                State.TwiSdaLow = true;
                Next(3);
                break;
            case 3:
                if (!Wait()) break;
                State.TwiSclLow = true;
                Next(4);
                break;
            case 4:
                if (!Wait()) break;
                var repeated = State.TwiOwns;
                State.TwiOwns = true;
                State.TwiAddressed = false;
                D[TWCR] &= unchecked((byte)~0x20);
                Done(repeated ? 0x10 : 0x08);
                break;
        }
    }

    /// <summary>STOP: SDA rising while SCL is high, and the bus let go.</summary>
    private void Stop()
    {
        switch (State.TwiPhase)
        {
            case 0:
                State.TwiSdaLow = true;
                Next(1);
                break;
            case 1:
                if (!Wait()) break;
                State.TwiSclLow = false;
                Next(2);
                break;
            case 2:
                if (!Scl) break;
                if (!Wait()) break;
                State.TwiSdaLow = false;
                Next(3);
                break;
            case 3:
                if (!Wait()) break;
                State.TwiOwns = false;
                State.TwiAddressed = false;
                D[TWCR] &= unchecked((byte)~0x10);
                D[TWSR] = (byte)(0xF8 | (D[TWSR] & 0x03));
                State.TwiAction = 0;   // no TWINT after a STOP
                break;
        }
    }

    /// <summary>A byte and its acknowledge: eight bits out (or in) and a ninth, SDA set while SCL is low and read while it's high.</summary>
    private void Byte()
    {
        switch (State.TwiPhase)
        {
            case 0:
                // SCL low: put the bit on SDA. Sending, it's the byte's; receiving, SDA is let go, but for our acknowledge.
                var bit = State.TwiBit;
                State.TwiSdaLow = State.TwiReceiving
                    ? bit == 8 && State.TwiAck
                    : bit < 8 && (State.TwiByte & (0x80 >> bit)) == 0;
                Next(1);
                break;
            case 1:
                if (!Wait()) break;
                State.TwiSclLow = false;
                Next(2);
                break;
            case 2:
                if (!Scl) break;   // clock stretching
                if (!Wait()) break;
                if (State.TwiBit < 8 && State.TwiReceiving && Sda) State.TwiByte |= 0x80 >> State.TwiBit;
                var acked = !Sda;
                State.TwiSclLow = true;
                if (State.TwiBit++ < 8)
                {
                    Next(0, 0);
                    State.TwiCountdown = 1;
                    break;
                }

                State.TwiSdaLow = false;
                Finish(acked);
                break;
        }
    }

    private void Finish(bool acked)
    {
        if (!State.TwiAddressed)
        {
            // The address byte: its low bit says which way the rest goes.
            State.TwiAddressed = true;
            State.TwiReceiving = (State.TwiByte & 1) != 0;
            Done(State.TwiReceiving ? acked ? 0x40 : 0x48 : acked ? 0x18 : 0x20);
        }
        else if (State.TwiReceiving)
        {
            D[TWDR] = (byte)State.TwiByte;
            Done(State.TwiAck ? 0x50 : 0x58);
        }
        else Done(acked ? 0x28 : 0x30);
    }

    // ---- the ADC ----

    private void StartConversion()
    {
        var divide = 1 << Math.Max(1, D[ADCSRA] & 0x7);
        State.AdcCycles = divide * (State.AdcFirst ? 25 : 13);
        State.AdcFirst = false;
    }

    private void Adc()
    {
        if (State.AdcCycles < 0) return;
        if (--State.AdcCycles > 0) return;
        State.AdcCycles = -1;

        var channel = D[ADMUX] & 0x0F;
        var input = channel switch
        {
            <= 7 => AnalogInputs[channel],
            14 => 1.1,
            15 => 0,
            _ => 0,
        };
        var reference = ((D[ADMUX] >> 6) & 3) switch
        {
            0 => ArefVolts,
            1 => AvccVolts,
            3 => 1.1,
            _ => AvccVolts,
        };
        var result = reference <= 0 ? 0 : (int)Math.Clamp(Math.Round(input / reference * 1024), 0, 1023);
        if ((D[ADMUX] & 0x20) != 0)
        {
            D[ADCH] = (byte)(result >> 2);
            D[ADCL] = (byte)((result & 3) << 6);
        }
        else
        {
            D[ADCL] = (byte)result;
            D[ADCH] = (byte)(result >> 8);
        }

        D[ADCSRA] = (byte)((D[ADCSRA] & ~0x40) | 0x10);

        // Free running: the next conversion starts at once.
        if ((D[ADCSRA] & 0x20) != 0 && (D[ADCSRB] & 0x07) == 0) { D[ADCSRA] |= 0x40; StartConversion(); }
    }

    // ---- the pins' edges: INT0, INT1 and the pin-change interrupts ----

    /// <summary>
    /// Takes in a fresh sample of the pins: what changed since the last flags its interrupts. Called by the chip each time it
    /// samples them.
    /// </summary>
    public void SampleEdges()
    {
        var (b, c, d) = (PinInputs[0], PinInputs[1], PinInputs[2]);
        if (((b ^ State.LastPinB) & D[PCMSK0]) != 0) D[PCIFR] |= 0x01;
        if (((c ^ State.LastPinC) & D[PCMSK1]) != 0) D[PCIFR] |= 0x02;
        if (((d ^ State.LastPinD) & D[PCMSK2]) != 0) D[PCIFR] |= 0x04;

        var int0 = (d & 0x04) != 0;
        var int1 = (d & 0x08) != 0;
        Edge(int0, State.LastInt0, D[EICRA] & 0x3, 0x01);
        Edge(int1, State.LastInt1, (D[EICRA] >> 2) & 0x3, 0x02);
        (State.LastPinB, State.LastPinC, State.LastPinD, State.LastInt0, State.LastInt1) = (b, c, d, int0, int1);
    }

    private void Edge(bool now, bool before, int sense, byte flag)
    {
        var fire = sense switch
        {
            0 => !now,
            1 => now != before,
            2 => before && !now,
            _ => !before && now,
        };
        if (fire) D[EIFR] |= flag;
        else if (sense == 0) D[EIFR] &= (byte)~flag;
    }

    /// <summary>The level a port pin is driven to, or null when it isn't driven (an input).</summary>
    public bool? Driven(int port, int bit)
    {
        var (ddr, portRegister) = port switch { 0 => (DDRB, PORTB), 1 => (DDRC, PORTC), _ => (DDRD, PORTD) };
        if (port == 2 && bit == 1 && TxEnabled) return State.TxLine;
        if (port == 2 && bit == 0 && RxEnabled) return null;
        var oc = Tiny ? (port, bit) switch { (0, 0) => 0, (0, 1) => 1, _ => -1 } : (port, bit) switch
        {
            (2, 6) => 0,
            (2, 5) => 1,
            (0, 1) => 2,
            (0, 2) => 3,
            (0, 3) => 4,
            (2, 3) => 5,
            _ => -1,
        };
        if (TwiEnabled && port == 1 && bit is 4 or 5) return (bit == 4 ? State.TwiSdaLow : State.TwiSclLow) ? false : null;
        if (SpiMaster && port == 0)
        {
            if (bit == 4) return null;
            if (bit is 3 or 5 && (D[ddr] & (1 << bit)) != 0) return bit == 3 ? State.SpiMosi : State.SpiSck;
        }

        if ((D[ddr] & (1 << bit)) == 0) return null;
        if (oc >= 0 && OcDriving(oc)) return State.Oc[oc];
        return (D[portRegister] & (1 << bit)) != 0;
    }

    /// <summary>Whether an input pin's pull-up is on: PORTx set with DDRx clear.</summary>
    public bool PulledUp(int port, int bit)
    {
        var (ddr, portRegister) = port switch { 0 => (DDRB, PORTB), 1 => (DDRC, PORTC), _ => (DDRD, PORTD) };
        return (D[ddr] & (1 << bit)) == 0 && (D[portRegister] & (1 << bit)) != 0 && (D[0x55] & 0x10) == 0;
    }

    public int Pc => State.Pc;

    public int StackPointer => SP;

    public byte StatusRegister => Sreg;
}
