using System.Globalization;
using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Processors;

/// <summary>One counter of an 8253 or 8254, as a readout sees it.</summary>
/// <param name="Programmed">A control word has been written since power-on.</param>
/// <param name="Mode">The mode, 0 to 5.</param>
/// <param name="Bcd">Counting in BCD (four decades) rather than binary.</param>
/// <param name="ReadWrite">RW1 RW0: 1 LSB only, 2 MSB only, 3 LSB then MSB.</param>
/// <param name="CountRegister">The count last written, as written (0 meaning 65536, or 10000 in BCD).</param>
/// <param name="CountingElement">The counter as it stands, as a read would give it.</param>
/// <param name="Output">OUT is high.</param>
/// <param name="Gate">GATE is high.</param>
/// <param name="CountLatched">A latch command (or read-back) is holding a count for reading.</param>
/// <param name="StatusLatched">An 8254 read-back is holding a status byte for reading.</param>
/// <param name="NullCount">A count has been written but not yet loaded into the counting element.</param>
/// <param name="Running">The counter has a count loaded and is counting (or waiting for its trigger, in modes 1 and 5).</param>
/// <param name="Expirations">How many times it has reached its terminal count since power-on.</param>
public readonly record struct IntervalTimerCounter(bool Programmed, int Mode, bool Bcd, int ReadWrite, int CountRegister, int CountingElement,
    bool Output, bool Gate, bool CountLatched, bool StatusLatched, bool NullCount, bool Running, long Expirations);

/// <summary>
/// The Intel 8253 and 8254 programmable interval timers: three independent 16-bit down counters,
/// each with its own CLK, GATE and OUT pins, on an 8080 or 8085 bus — the <see cref="Pit8253"/> and
/// the <see cref="Pit8254"/>, which adds the read-back command and a status byte.
/// <para>
/// <b>The bus.</b> A1 and A0 pick counter 0, 1 or 2 (a count to write, or the count to read), or —
/// 11 — the control word, which is written only. See <see cref="IntelPeripheral"/> for the cycle. A
/// control word's bits 7-6 pick the counter (11 is the 8254's read-back command), bits 5-4 how it is
/// read and written (00 a counter latch command, 01 the low byte only, 10 the high byte only, 11 the
/// low then the high), bits 3-1 the mode (x10 is mode 2, x11 mode 3) and bit 0 BCD. Writing it sets
/// OUT to the mode's starting level and leaves the counter waiting for a count; a count of 0 is
/// 65536 in binary and 10000 in BCD.
/// </para>
/// <para>
/// <b>Counting.</b> Each counter counts the falling edges of its CLK, GATE being sampled on the
/// rising edges before them; a "CLK pulse" is a rising edge and the falling edge after it. A count
/// written is loaded into the counting element on the next pulse (mode 0 and 4), on the pulse after
/// a GATE rising edge (modes 1 and 5), or — for modes 2 and 3 already counting — at the end of the
/// period under way.
/// </para>
/// <list type="bullet">
/// <item><b>Mode 0</b>, interrupt on terminal count: OUT low from the control word, high when the
/// count reaches 0, N + 1 pulses after it was written; GATE low pauses the count. Writing the first
/// byte of a two-byte count stops it and takes OUT low.</item>
/// <item><b>Mode 1</b>, hardware retriggerable one-shot: a GATE rising edge takes OUT low on the next
/// pulse for N pulses; another edge on the way starts it again.</item>
/// <item><b>Mode 2</b>, rate generator: OUT high, low for one pulse each time the count reaches 1,
/// then the count reloaded — divide by N. GATE low stops it with OUT high; GATE rising reloads.</item>
/// <item><b>Mode 3</b>, square wave: OUT high for (N + 1) / 2 pulses and low for N / 2. GATE as
/// mode 2.</item>
/// <item><b>Mode 4</b>, software triggered strobe: OUT low for one pulse when the count written
/// reaches 0. GATE low pauses the count.</item>
/// <item><b>Mode 5</b>, hardware triggered strobe: as mode 4, the count started by a GATE rising
/// edge, and retriggerable.</item>
/// </list>
/// <para>
/// <b>Reading.</b> A read gives the counting element as it stands, low or high byte as RW says
/// (alternately for 11). A counter latch command holds the count as it was until it has been read;
/// the 8254's read-back command latches the counts and status bytes (OUT, NULL COUNT, RW, mode and
/// BCD) of any of the three at once, the status read first.
/// </para>
/// <para>
/// Simplifications: the counters change as the CLK edge comes, not after the chip's delays; in
/// mode 3 the counting element reads as the half-period's count doubled (the chip counts down by
/// two, loading N - 1 for an odd N), which only shows in a read of a running mode 3 counter; a count
/// of 1 in modes 2 and 3, which Intel calls illegal, divides by 1; reading the control word address
/// gives $FF where the chip floats the bus; OUT is low at power-on, where the chip's is undefined.
/// The 8253's and 8254's maximum clock rates are not enforced.
/// </para>
/// <para>
/// Pinout (DIP-24): 1 = D7, 2 = D6, 3 = D5, 4 = D4, 5 = D3, 6 = D2, 7 = D1, 8 = D0, 9 = CLK0,
/// 10 = OUT0, 11 = GATE0, 12 = GND, 13 = OUT1, 14 = GATE1, 15 = CLK1, 16 = GATE2, 17 = OUT2,
/// 18 = CLK2, 19 = A0, 20 = A1, 21 = /CS, 22 = /RD, 23 = /WR, 24 = +5V.
/// </para>
/// </summary>
public abstract class IntervalTimer : IntelPeripheral, IRunState
{
    // Outputs: D0-D7 = 0-7, OUT0-OUT2 = 8-10.
    private const int OutIndex = 8;

    private static readonly int[] DataPins = [8, 7, 6, 5, 4, 3, 2, 1];

    private readonly Terminal[] _clocks = new Terminal[3];
    private readonly Terminal[] _gates = new Terminal[3];
    private Counter[] _counters = [new(), new(), new()];

    /// <summary>One counter's registers and counting.</summary>
    private sealed class Counter
    {
        public bool Programmed;
        public int Mode;
        public bool Bcd;
        public int Rw = 3;
        public int Cr;                  // the count register, as written
        public bool WriteHighNext;
        public bool ReadHighNext;
        public int Ce;                  // the counting element, 0 to modulus - 1
        public bool Output;
        public bool NullCount;
        public bool LoadPending;
        public bool Running;
        public bool Armed;              // modes 1 and 5: a count is there for a trigger
        public bool Strobing;           // modes 4 and 5: OUT is low for this pulse
        public bool Stopped;            // mode 0: the first of two bytes written
        public int Phase;               // mode 3: pulses left in this half
        public int Latched = -1;        // the count a latch command holds
        public bool LatchHighNext;
        public int Status = -1;         // the status byte a read-back holds
        public bool GateLevel;
        public bool GateSampled;
        public bool GateEdge;
        public bool TriggerSampled;
        public bool LastClock;
        public long Expirations;

        public int Modulus => Bcd ? 10000 : 65536;

        /// <summary>The count register as a number of pulses: 0 is the modulus.</summary>
        public int Initial
        {
            get
            {
                var n = Bcd ? FromBcd(Cr) : Cr;
                return n == 0 ? Modulus : n;
            }
        }

        public Counter Copy() => (Counter)MemberwiseClone();
    }

    private protected IntervalTimer() : base(24)
    {
        PropagationDelay = 50e-9;

        var pins = new Terminal[25];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 24));

        for (var i = 0; i < 8; i++) SetDataPin(i, Pin(DataPins[i], $"D{i}", TerminalType.Bidirectional));
        _clocks[0] = Pin(9, "CLK0", TerminalType.Input);
        var out0 = Pin(10, "OUT0", TerminalType.Output);
        _gates[0] = Pin(11, "GATE0", TerminalType.Input);
        Gnd = Pin(12, "GND", TerminalType.Ground);
        var out1 = Pin(13, "OUT1", TerminalType.Output);
        _gates[1] = Pin(14, "GATE1", TerminalType.Input);
        _clocks[1] = Pin(15, "CLK1", TerminalType.Input);
        _gates[2] = Pin(16, "GATE2", TerminalType.Input);
        var out2 = Pin(17, "OUT2", TerminalType.Output);
        _clocks[2] = Pin(18, "CLK2", TerminalType.Input);
        A0 = Pin(19, "A0", TerminalType.Input);
        A1 = Pin(20, "A1", TerminalType.Input);
        ChipSelect = Pin(21, "CS", TerminalType.Input);
        ReadStrobe = Pin(22, "RD", TerminalType.Input);
        WriteStrobe = Pin(23, "WR", TerminalType.Input);
        Vcc = Pin(24, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([ChipSelect, ReadStrobe, WriteStrobe, A0, A1, .. _clocks, .. _gates, .. Data], [.. Data, out0, out1, out2]);
    }

    /// <summary>True for the 8254: the read-back command and the status byte.</summary>
    private protected abstract bool HasReadBack { get; }

    public override string ComponentType => PartNumber;

    public Terminal A0 { get; }

    public Terminal A1 { get; }

    /// <summary>CLK0 to CLK2: each counter counts its falling edges.</summary>
    public IReadOnlyList<Terminal> Clocks => _clocks;

    /// <summary>GATE0 to GATE2: enable (modes 0, 2, 3, 4) or trigger (modes 1, 2, 3, 5).</summary>
    public IReadOnlyList<Terminal> Gates => _gates;

    /// <summary>OUT0 to OUT2.</summary>
    public IReadOnlyList<Terminal> Outputs => [.. OutputTerminals.Skip(OutIndex).Take(3)];

    // ---- what a test or an agent wants to see -----------------------------------

    /// <summary>A counter's control, count register, counting element, pins and latches.</summary>
    public IntervalTimerCounter Describe(int counter)
    {
        var c = _counters[counter];
        return new IntervalTimerCounter(c.Programmed, c.Mode, c.Bcd, c.Rw, c.Cr, Encode(c, c.Ce), OutputLevel(c), c.GateLevel,
            c.Latched >= 0, c.Status >= 0, c.NullCount, c.Running || c.Armed, c.Expirations);
    }

    /// <summary>The status byte an 8254 read-back would latch for <paramref name="counter"/>: OUT, NULL COUNT, RW1 RW0, M2-M0, BCD.</summary>
    public byte StatusByte(int counter) => StatusOf(_counters[counter]);

    public override string ValueLabel =>
        string.Join(" ", _counters.Select((c, i) => c.Programmed
            ? string.Create(CultureInfo.InvariantCulture, $"{i}:M{c.Mode}{(c.Running || c.Armed ? $" /{c.Initial}" : "")}")
            : $"{i}:-"));

    public override string PinLabel(Terminal pin) => pin.Name switch
    {
        "CS" or "RD" or "WR" => "/" + pin.Name,
        "VCC" => "+5V",
        _ => pin.Name,
    };

    // ---- counting ---------------------------------------------------------------------

    private static int FromBcd(int value) =>
        ((value >> 12) & 0xF) * 1000 + ((value >> 8) & 0xF) * 100 + ((value >> 4) & 0xF) * 10 + (value & 0xF);

    private static int ToBcd(int value) =>
        ((value / 1000 % 10) << 12) | ((value / 100 % 10) << 8) | ((value / 10 % 10) << 4) | (value % 10);

    /// <summary>A count as the register reads it: binary, or four BCD digits.</summary>
    private static int Encode(Counter c, int value) => c.Bcd ? ToBcd(value % 10000) : value & 0xFFFF;

    /// <summary>OUT as the pin has it: modes 2 and 3 force it high while GATE is low.</summary>
    private static bool OutputLevel(Counter c) =>
        c.Programmed && c.Mode is 2 or 3 && !c.GateLevel || c.Output;

    private static byte StatusOf(Counter c) =>
        (byte)((OutputLevel(c) ? 0x80 : 0) | (c.NullCount ? 0x40 : 0) | ((c.Rw & 3) << 4) | ((c.Mode & 7) << 1) | (c.Bcd ? 1 : 0));

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        if (!context.IsInitializing)
        {
            for (var i = 0; i < 3; i++)
            {
                var c = _counters[i];
                var gate = context.ReadInput(_gates[i], Levels).IsHigh();
                if (gate && !c.GateLevel) c.GateEdge = true;
                if (!gate && c.GateLevel && c.Programmed && c.Mode is 2 or 3) NotifyValueChanged();
                c.GateLevel = gate;

                var clock = context.ReadInput(_clocks[i], Levels).IsHigh();
                if (clock && !c.LastClock)
                {
                    // GATE is sampled as CLK rises; a rising edge on it is taken at the rise after.
                    c.GateSampled = c.GateLevel;
                    c.TriggerSampled = c.GateEdge;
                    c.GateEdge = false;
                }
                else if (!clock && c.LastClock)
                {
                    Pulse(c);
                }

                c.LastClock = clock;
            }
        }
        else
        {
            for (var i = 0; i < 3; i++) _counters[i].GateLevel = context.ReadInput(_gates[i], Levels).IsHigh();
        }

        EvaluateBus(context);

        var delay = DelayFor(context);
        for (var i = 0; i < 3; i++) Request(context, OutIndex + i, OutputLevel(_counters[i]) ? LogicState.High : LogicState.Low, delay);
    }

    /// <summary>The falling edge of a CLK pulse: the counter loads, counts, and moves OUT.</summary>
    private void Pulse(Counter c)
    {
        if (!c.Programmed) return;
        var trigger = c.TriggerSampled;
        c.TriggerSampled = false;
        var before = c.Output;

        // A strobe lasts one pulse.
        if (c.Strobing)
        {
            c.Strobing = false;
            c.Output = true;
        }

        switch (c.Mode)
        {
            case 0:
            case 4:
                if (c.LoadPending)
                {
                    Load(c);
                    break;
                }

                if (!c.Running || c.Stopped || !c.GateSampled) break;
                c.Ce = c.Ce == 0 ? c.Modulus - 1 : c.Ce - 1;
                if (c.Ce == 0 && !c.Armed)
                {
                    // Terminal count, once per count written: mode 0 OUT high, mode 4 a strobe.
                    c.Armed = true;
                    c.Expirations++;
                    if (c.Mode == 0) c.Output = true;
                    else
                    {
                        c.Output = false;
                        c.Strobing = true;
                    }
                }

                break;

            case 1:
            case 5:
                if (trigger && c.Armed)
                {
                    Load(c);
                    c.Running = true;
                    if (c.Mode == 1) c.Output = false;
                    break;
                }

                if (!c.Running) break;
                c.Ce = c.Ce == 0 ? c.Modulus - 1 : c.Ce - 1;
                if (c.Ce == 0)
                {
                    c.Running = false;
                    c.Expirations++;
                    if (c.Mode == 1) c.Output = true;
                    else
                    {
                        c.Output = false;
                        c.Strobing = true;
                    }
                }

                break;

            case 2:
                if (!c.GateSampled) break;
                if (c.LoadPending || trigger)
                {
                    Load(c);
                    c.Output = true;
                    break;
                }

                if (!c.Running) break;
                if (c.Ce == 1)
                {
                    // The count was 1 for a pulse with OUT low: reload, OUT high.
                    Load(c);
                    c.Output = true;
                    break;
                }

                c.Ce = c.Ce == 0 ? c.Modulus - 1 : c.Ce - 1;
                if (c.Ce == 1)
                {
                    c.Output = false;
                    c.Expirations++;
                }

                break;

            default:
                if (!c.GateSampled) break;
                if (c.LoadPending || trigger)
                {
                    c.Output = true;
                    Load(c);
                    c.Phase = (c.Initial + 1) / 2;
                    c.Ce = c.Phase * 2;
                    break;
                }

                if (!c.Running) break;
                c.Phase--;
                c.Ce = Math.Max(0, c.Ce - 2);
                if (c.Phase <= 0)
                {
                    c.Output = !c.Output;
                    if (!c.Output) c.Expirations++;
                    c.NullCount = false;
                    var n = c.Initial;
                    c.Phase = Math.Max(1, c.Output ? (n + 1) / 2 : n / 2);
                    c.Ce = c.Phase * 2;
                }

                break;
        }

        if (c.Output != before) NotifyValueChanged();
    }

    /// <summary>The count register into the counting element.</summary>
    private static void Load(Counter c)
    {
        c.LoadPending = false;
        c.NullCount = false;
        c.Running = true;
        c.Ce = c.Initial % c.Modulus;
        if (c.Mode is 0 or 4) c.Armed = false;
    }

    // ---- the registers --------------------------------------------------------------

    private protected override int SelectedRegister(IDigitalContext context) =>
        (context.ReadInput(A0, Levels).IsHigh() ? 1 : 0) | (context.ReadInput(A1, Levels).IsHigh() ? 2 : 0);

    private protected override byte ReadRegister(int register)
    {
        if (register == 3) return 0xFF;
        var c = _counters[register];
        if (c.Status >= 0) return (byte)c.Status;

        var value = c.Latched >= 0 ? c.Latched : Encode(c, c.Ce);
        var high = c.Latched >= 0 ? c.LatchHighNext : c.ReadHighNext;
        return c.Rw switch
        {
            1 => (byte)value,
            2 => (byte)(value >> 8),
            _ => high ? (byte)(value >> 8) : (byte)value,
        };
    }

    private protected override void AfterRead(int register)
    {
        if (register == 3) return;
        var c = _counters[register];
        if (c.Status >= 0)
        {
            c.Status = -1;
            return;
        }

        if (c.Latched >= 0)
        {
            if (c.Rw == 3 && !c.LatchHighNext)
            {
                c.LatchHighNext = true;
                return;
            }

            c.Latched = -1;
            c.LatchHighNext = false;
            return;
        }

        if (c.Rw == 3) c.ReadHighNext = !c.ReadHighNext;
    }

    private protected override void WriteRegister(int register, byte value)
    {
        if (register == 3)
        {
            Control(value);
            return;
        }

        var c = _counters[register];
        if (!c.Programmed) return;

        switch (c.Rw)
        {
            case 1:
                c.Cr = value;
                break;
            case 2:
                c.Cr = value << 8;
                break;
            default:
                if (!c.WriteHighNext)
                {
                    c.Cr = (c.Cr & 0xFF00) | value;
                    c.WriteHighNext = true;
                    if (c.Mode == 0)
                    {
                        // The first byte of a mode 0 count stops the counter and takes OUT low.
                        c.Stopped = true;
                        c.Output = false;
                    }

                    return;
                }

                c.Cr = (c.Cr & 0x00FF) | (value << 8);
                c.WriteHighNext = false;
                break;
        }

        CountWritten(c);
    }

    /// <summary>A whole count has been written.</summary>
    private static void CountWritten(Counter c)
    {
        c.NullCount = true;
        c.Stopped = false;
        switch (c.Mode)
        {
            case 0:
                c.Output = false;
                c.LoadPending = true;
                break;

            case 4:
                c.LoadPending = true;
                break;

            case 1:
            case 5:
                c.Armed = true;
                break;

            default:
                // Modes 2 and 3: the first count starts at the next pulse; later ones at the end of the period.
                if (!c.Running) c.LoadPending = true;
                break;
        }
    }

    private void Control(byte value)
    {
        var select = value >> 6;
        if (select == 3)
        {
            if (HasReadBack) ReadBack(value);
            return;
        }

        var c = _counters[select];
        var rw = (value >> 4) & 3;
        if (rw == 0)
        {
            // Counter latch command: the count as it is, kept until read; a second one before then is ignored.
            if (c.Latched < 0 && c.Programmed)
            {
                c.Latched = Encode(c, c.Ce);
                c.LatchHighNext = false;
            }

            return;
        }

        var mode = (value >> 1) & 7;
        if (mode >= 6) mode -= 4;
        c.Programmed = true;
        c.Mode = mode;
        c.Bcd = (value & 1) != 0;
        c.Rw = rw;
        c.WriteHighNext = c.ReadHighNext = c.LatchHighNext = false;
        c.Latched = -1;
        c.Status = -1;
        c.NullCount = true;
        c.LoadPending = c.Running = c.Armed = c.Strobing = c.Stopped = false;
        c.Output = mode != 0;
        NotifyValueChanged();
    }

    /// <summary>The 8254's read-back: bit 5 low latches the counts, bit 4 low the status bytes, of the counters bits 3-1 pick.</summary>
    private void ReadBack(byte value)
    {
        for (var i = 0; i < 3; i++)
        {
            if ((value & (2 << i)) == 0) continue;
            var c = _counters[i];
            if ((value & 0x10) == 0 && c.Status < 0) c.Status = StatusOf(c);
            if ((value & 0x20) == 0 && c.Latched < 0)
            {
                c.Latched = Encode(c, c.Ce);
                c.LatchHighNext = false;
            }
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _counters = [new(), new(), new()];
    }

    private sealed record RunState(Counter[] Counters);

    public object CaptureRunState() => new RunState([.. _counters.Select(c => c.Copy())]);

    public void AdoptRunState(object state)
    {
        if (state is not RunState run) return;
        _counters = [.. run.Counters.Select(c => c.Copy())];
        NotifyValueChanged();
    }
}

/// <summary>
/// The Intel 8253 programmable interval timer: three 16-bit counters in six modes — see
/// <see cref="IntervalTimer"/>. Control words with bits 7-6 = 11 are ignored; there is no read-back.
/// <para>
/// Pinout (DIP-24): 1 = D7, 2 = D6, 3 = D5, 4 = D4, 5 = D3, 6 = D2, 7 = D1, 8 = D0, 9 = CLK0,
/// 10 = OUT0, 11 = GATE0, 12 = GND, 13 = OUT1, 14 = GATE1, 15 = CLK1, 16 = GATE2, 17 = OUT2,
/// 18 = CLK2, 19 = A0, 20 = A1, 21 = /CS, 22 = /RD, 23 = /WR, 24 = +5V.
/// </para>
/// </summary>
public sealed class Pit8253 : IntervalTimer
{
    public override string PartNumber => "8253";

    private protected override bool HasReadBack => false;
}

/// <summary>
/// The Intel 8254 programmable interval timer: the 8253's counters and pinout with the read-back
/// command (a control word with bits 7-6 = 11: bit 5 low latches counts, bit 4 low status bytes,
/// bits 3-1 pick counters 2, 1 and 0) and the status byte — OUT, NULL COUNT, RW1, RW0, M2-M0 and
/// BCD — read before the count. See <see cref="IntervalTimer"/>.
/// <para>
/// Pinout (DIP-24): as the 8253's.
/// </para>
/// </summary>
public sealed class Pit8254 : IntervalTimer
{
    public override string PartNumber => "8254";

    private protected override bool HasReadBack => true;
}
