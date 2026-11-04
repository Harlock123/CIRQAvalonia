using System.Globalization;
using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;

namespace Cirq.Components.Processors;

/// <summary>
/// The MOS 6526 Complex Interface Adapter — two of them in every Commodore 64, one scanning the
/// keyboard and joysticks, the other on the serial bus and the user port: two 8-bit ports, two
/// 16-bit timers, a time-of-day clock, a serial shift register and an interrupt controller.
/// <para>
/// <b>The bus.</b> Selected while /CS is low; RS0-RS3 pick one of sixteen registers. A read drives
/// the data lines while PHI2 is high and takes effect — clearing what a read clears — as PHI2 falls;
/// a write takes the data bus as PHI2 falls. Everything else counts on that same edge, once a cycle.
/// </para>
/// <list type="bullet">
/// <item><b>0-3, the ports.</b> PRA, PRB, DDRA, DDRB. A one in a DDR bit makes the pin an output
/// driving the PR bit; a zero lets it go. A read of PRA or PRB gives the <i>pins</i>, outputs
/// included, which is how the C64 reads its keyboard. Every port pin has a passive pull-up, as the
/// datasheet says, so an input with nothing on it reads as a one — and making a pin an output
/// only while it should be low turns the port into open-drain lines. A read or a write of PRB
/// takes /PC low for the cycle after it.</item>
/// <item><b>4-7, the timers.</b> TA and TB, each a 16-bit down counter with a latch: writing the
/// low byte sets the latch, writing the high byte sets it too and, with the timer stopped, loads the
/// counter — and in one-shot mode starts it. Reads give the counter. A timer counts to zero, then
/// underflows on its next count: it sets its interrupt flag, reloads from the latch, and in one-shot
/// mode stops. So a running timer underflows every latch + 1 counts, and a timer started, loaded
/// or force-loaded by a write counts from the cycle after it.</item>
/// <item><b>8-B, the time-of-day clock.</b> Tenths, seconds, minutes and hours in BCD, hours 1-12
/// with bit 7 for PM, counted from the 50 or 60 Hz on the TOD pin (CRA bit 7 says which). Reading
/// the hours freezes what the four registers read until the tenths are read, so a time read hours
/// first never tears; writing the hours stops the clock until the tenths are written, so a time
/// written hours first starts on the tenth. With CRB bit 7 set, writes set the alarm instead, and
/// the time reaching the alarm sets the alarm flag. After 11:59:59.9 comes 12:00:00.0 with AM and PM
/// swapped, and after 12:59:59.9, 1:00:00.0.</item>
/// <item><b>C, the serial data register.</b> CRA bit 6 set is output: a byte written goes out on SP
/// most significant bit first, a bit for every two timer A underflows, with CNT the clock — SP
/// changes as CNT falls and is good as it rises — and a byte written while one is going out
/// follows straight after it. Clear, it is input: SP is shifted in on every rising edge of CNT,
/// and after eight the byte is in the register. Either way the eighth bit sets the SP flag.</item>
/// <item><b>D, the interrupt control register.</b> Reading gives the flags — timer A, timer B,
/// alarm, serial, /FLAG — with bit 7 set if any of them is enabled, and clears them all. Writing
/// with bit 7 set enables the bits given; with it clear, disables them. /FLAG's falling edge sets
/// its flag (it is the C64's cassette read and serial SRQ). /IRQ is open drain and needs a
/// pull-up.</item>
/// <item><b>E and F, the control registers.</b> CRA: bit 0 start, 1 timer A's underflow onto PB6,
/// 2 toggle rather than one-cycle pulse there, 3 one-shot, 4 force load (a strobe, it reads as
/// zero), 5 count CNT's rising edges rather than PHI2, 6 serial output, 7 50 Hz on TOD. CRB: bits
/// 0-4 the same for timer B and PB7; bits 5-6 count PHI2, CNT edges, timer A underflows, or timer A
/// underflows while CNT is high (a 32-bit timer, or a gated one); bit 7 writes the alarm.</item>
/// </list>
/// <para>
/// With PBON set the timer drives its PB pin whatever DDRB says: a one-cycle high pulse at each
/// underflow, or a level that toggles at each underflow and goes high when the timer is started.
/// /RES clears every register but the timer latches, which go to all ones, so the ports come up as
/// inputs and read as $FF through the pull-ups.
/// </para>
/// <para>
/// <b>The 6526 and the 6526A/8521.</b> This is the original 6526: /IRQ goes low the cycle after an
/// interrupt flag is set. The later 6526A (the 8521 in the C64C) pulls it a cycle sooner, which is
/// what timing-exact C64 software has to know. Not modelled: the timers' pipeline (on the silicon a
/// start, a load or a CNT edge takes a cycle or two to reach the counter, and an ICR read in the
/// very cycle a timer B flag would be set loses it on the 6526), the TOD's extra 12-hour quirks
/// when written, and timing finer than a whole cycle. SP, CNT and /FLAG are pulled up here as the
/// ports are, so they can be left open.
/// </para>
/// <para>
/// Pinout: 1=VSS, 2-9=PA0-PA7, 10-17=PB0-PB7, 18=/PC, 19=TOD, 20=VCC, 21=/IRQ, 22=R/W, 23=/CS,
/// 24=/FLAG, 25=PHI2, 26-33=D7-D0, 34=/RES, 35-38=RS3-RS0, 39=SP, 40=CNT.
/// </para>
/// </summary>
public sealed class Cia6526 : DigitalIc, IRunState
{
    /// <summary>Resistance of each pin's passive pull-up to VCC, in ohms: a representative few kilohms.</summary>
    public const double PullUpResistance = 4.7e3;

    public const byte FlagTimerA = 0x01, FlagTimerB = 0x02, FlagAlarm = 0x04, FlagSerial = 0x08, FlagFlag = 0x10;

    private readonly Terminal[] _pa = new Terminal[8];
    private readonly Terminal[] _pb = new Terminal[8];
    private readonly Terminal[] _data = new Terminal[8];
    private readonly Terminal[] _rs = new Terminal[4];

    // Output indices: PA0-7 = 0-7, PB0-7 = 8-15, D0-7 = 16-23, /PC = 24, SP = 25, CNT = 26, /IRQ = 27.
    private const int DataIndex = 16, PcIndex = 24, SpIndex = 25, CntIndex = 26, IrqIndex = 27;

    /// <summary>Everything that changes as it runs, in one place so a run can hand it back whole.</summary>
    private sealed class Registers
    {
        public byte Pra, Prb, Ddra, Ddrb, Cra, Crb, Flags, Mask;
        public ushort TaLatch = 0xFFFF, Ta = 0xFFFF, TbLatch = 0xFFFF, Tb = 0xFFFF;

        // PB6 and PB7 as the timers drive them: the toggle levels, and the one-cycle pulses.
        public bool Pb6Toggle, Pb7Toggle, TaPulse, TbPulse;

        // Time of day, its alarm, the read latch and the divider counting TOD pin edges.
        public byte TodTenths, TodSeconds, TodMinutes, TodHours, AlarmTenths, AlarmSeconds, AlarmMinutes, AlarmHours;
        public byte[] TodLatch = new byte[4];
        public bool TodLatched, TodStopped;
        public int TodDivider;

        // The serial port: the register, the shift register, the bits shifted and the output side.
        public byte Sdr, Shift;
        public int Bits;
        public bool Sending, Queued, CntOut = true, SpOut = true;

        public bool PcLow, IrqLine, AssertedLastCycle;

        // A timer loaded or started by this cycle's write does not count until the next one.
        public bool ALoaded, BLoaded;

        public Registers Clone()
        {
            var copy = (Registers)MemberwiseClone();
            copy.TodLatch = (byte[])TodLatch.Clone();
            return copy;
        }
    }

    private Registers _r = new();

    private LogicState _lastPhi2 = LogicState.Unknown;
    private bool _lastFlag = true, _lastCnt = true, _lastTod;
    private int _cntRises;

    public Cia6526() : base(40)
    {
        PropagationDelay = 40e-9;

        var pins = new Terminal[41];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 40));

        Gnd = Pin(1, "VSS", TerminalType.Ground);
        for (var i = 0; i < 8; i++) _pa[i] = Pin(2 + i, $"PA{i}", TerminalType.Bidirectional);
        for (var i = 0; i < 8; i++) _pb[i] = Pin(10 + i, $"PB{i}", TerminalType.Bidirectional);
        PortControl = Pin(18, "PC", TerminalType.Output);
        TimeOfDayInput = Pin(19, "TOD", TerminalType.Input);
        Vcc = Pin(20, "VCC", TerminalType.Power);
        Irq = Pin(21, "IRQ", TerminalType.Bidirectional);
        ReadWrite = Pin(22, "RW", TerminalType.Input);
        ChipSelect = Pin(23, "CS", TerminalType.Input);
        Flag = Pin(24, "FLAG", TerminalType.Input);
        Clock = Pin(25, "PHI2", TerminalType.Input);
        for (var i = 0; i < 8; i++) _data[i] = Pin(33 - i, $"D{i}", TerminalType.Bidirectional);
        Reset = Pin(34, "RES", TerminalType.Input);
        for (var i = 0; i < 4; i++) _rs[i] = Pin(38 - i, $"RS{i}", TerminalType.Input);
        SerialPort = Pin(39, "SP", TerminalType.Bidirectional);
        Count = Pin(40, "CNT", TerminalType.Bidirectional);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins(
            [Clock, ReadWrite, ChipSelect, Reset, .. _rs, TimeOfDayInput, Flag, SerialPort, Count, .. _pa, .. _pb, .. _data],
            [.. _pa, .. _pb, .. _data, PortControl, SerialPort, Count, Irq]);
    }

    public override string PartNumber => "6526";

    public override string ComponentType => "6526 CIA";

    public override bool LabelsPins => true;

    public IReadOnlyList<Terminal> PortA => _pa;

    public IReadOnlyList<Terminal> PortB => _pb;

    public IReadOnlyList<Terminal> Data => _data;

    /// <summary>RS0-RS3, the register select inputs.</summary>
    public IReadOnlyList<Terminal> RegisterSelect => _rs;

    public Terminal Clock { get; }

    public Terminal ReadWrite { get; }

    /// <summary>/CS, active low.</summary>
    public Terminal ChipSelect { get; }

    /// <summary>/RES, active low.</summary>
    public Terminal Reset { get; }

    /// <summary>/IRQ: open drain, low while an enabled interrupt flag is set.</summary>
    public Terminal Irq { get; }

    /// <summary>/PC: low for the cycle after a read or write of port B.</summary>
    public Terminal PortControl { get; }

    /// <summary>/FLAG: a falling edge sets the FLAG interrupt flag.</summary>
    public Terminal Flag { get; }

    /// <summary>TOD: the 50 or 60 Hz the time-of-day clock counts.</summary>
    public Terminal TimeOfDayInput { get; }

    /// <summary>SP: the serial data, out or in.</summary>
    public Terminal SerialPort { get; }

    /// <summary>CNT: the serial clock, and a count input for the timers.</summary>
    public Terminal Count { get; }

    public override string ValueLabel => $"TA {_r.Ta:X4} TB {_r.Tb:X4}";

    public override string PinLabel(Terminal pin) => pin.Name switch
    {
        "PC" or "IRQ" or "CS" or "FLAG" or "RES" => "/" + pin.Name,
        "RW" => "R/W",
        _ => pin.Name,
    };

    // ---- what a test or an agent wants to see -----------------------------------

    public byte OutputA => _r.Pra;

    public byte OutputB => _r.Prb;

    public byte DirectionA => _r.Ddra;

    public byte DirectionB => _r.Ddrb;

    public ushort TimerA => _r.Ta;

    public ushort TimerALatch => _r.TaLatch;

    public ushort TimerB => _r.Tb;

    public ushort TimerBLatch => _r.TbLatch;

    public byte ControlA => _r.Cra;

    public byte ControlB => _r.Crb;

    /// <summary>The interrupt flags as a read of ICR would give them, without clearing them.</summary>
    public byte InterruptFlags => (byte)(_r.Flags | ((_r.Flags & _r.Mask) != 0 ? 0x80 : 0));

    public byte InterruptMask => _r.Mask;

    public byte SerialData => _r.Sdr;

    /// <summary>True while a write of the hours has the clock stopped, waiting for the tenths.</summary>
    public bool IsTimeOfDayStopped => _r.TodStopped;

    /// <summary>The time-of-day registers as they are counting: tenths, seconds, minutes and hours (BCD, bit 7 PM).</summary>
    public (byte Tenths, byte Seconds, byte Minutes, byte Hours) TimeOfDay => (_r.TodTenths, _r.TodSeconds, _r.TodMinutes, _r.TodHours);

    /// <summary>The alarm, in the same form.</summary>
    public (byte Tenths, byte Seconds, byte Minutes, byte Hours) Alarm => (_r.AlarmTenths, _r.AlarmSeconds, _r.AlarmMinutes, _r.AlarmHours);

    /// <summary>The time of day as a clock shows it: <c>12:00:05.3 PM</c>.</summary>
    public string TimeOfDayText => FormatTime(_r.TodHours, _r.TodMinutes, _r.TodSeconds, _r.TodTenths);

    /// <summary>A BCD time as text.</summary>
    public static string FormatTime(byte hours, byte minutes, byte seconds, byte tenths) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{hours & 0x1F:X2}:{minutes:X2}:{seconds:X2}.{tenths & 0x0F:X} {((hours & 0x80) != 0 ? "PM" : "AM")}");

    // ---- the logic ------------------------------------------------------------------

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);

        // The passive pull-ups: every port pin, and SP, CNT and /FLAG.
        var vcc = system.Node(Vcc);
        foreach (var pin in _pa.Concat(_pb).Append(SerialPort).Append(Count).Append(Flag))
            system.StampResistor(system.Node(pin), vcc, PullUpResistance);
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var phi2 = context.ReadInput(Clock, Levels);
        var previous = _lastPhi2;
        _lastPhi2 = phi2;

        if (context.ReadInput(Reset, Levels).IsLow())
        {
            ResetRegisters();
            DriveOutputs(context, driveData: false, 0);
            return;
        }

        var selected = context.ReadInput(ChipSelect, Levels).IsLow();
        var reading = context.ReadInput(ReadWrite, Levels).IsHigh();
        var register = 0;
        for (var i = 0; i < 4; i++)
            if (context.ReadInput(_rs[i], Levels).IsHigh()) register |= 1 << i;

        Edges(context);

        if (!context.IsInitializing && previous.IsHigh() && phi2.IsLow())
        {
            // The end of a cycle. What lasted one cycle is over; then the access takes effect, and
            // the timers count.
            _r.PcLow = _r.TaPulse = _r.TbPulse = false;
            _r.ALoaded = _r.BLoaded = false;

            if (selected)
            {
                if (reading) AfterRead(register);
                else Write(register, ReadBus(context));
            }

            Tick(context);
        }

        var driveData = selected && reading && phi2.IsHigh();
        DriveOutputs(context, driveData, driveData ? Read(register, context) : (byte)0);
    }

    private byte ReadBus(IDigitalContext context) => Pins(context, _data);

    private byte Pins(IDigitalContext context, Terminal[] pins)
    {
        var value = 0;
        for (var i = 0; i < 8; i++)
            if (!context.ReadInput(pins[i], Levels).IsLow()) value |= 1 << i;

        return (byte)value;
    }

    /// <summary>What a read of a register returns. No side effects: those happen as the cycle ends.</summary>
    private byte Read(int register, IDigitalContext context) => register switch
    {
        0 => Pins(context, _pa),
        1 => Pins(context, _pb),
        2 => _r.Ddra,
        3 => _r.Ddrb,
        4 => (byte)_r.Ta,
        5 => (byte)(_r.Ta >> 8),
        6 => (byte)_r.Tb,
        7 => (byte)(_r.Tb >> 8),
        8 => _r.TodLatched ? _r.TodLatch[0] : _r.TodTenths,
        9 => _r.TodLatched ? _r.TodLatch[1] : _r.TodSeconds,
        10 => _r.TodLatched ? _r.TodLatch[2] : _r.TodMinutes,
        11 => _r.TodLatched ? _r.TodLatch[3] : _r.TodHours,
        12 => _r.Sdr,
        13 => InterruptFlags,
        14 => _r.Cra,
        _ => _r.Crb,
    };

    /// <summary>What a read does as it ends: the ICR clears, the TOD latches and unlatches, /PC pulses.</summary>
    private void AfterRead(int register)
    {
        switch (register)
        {
            case 1:
                _r.PcLow = true;
                break;
            case 8:
                _r.TodLatched = false;
                break;
            case 11:
                if (!_r.TodLatched)
                {
                    _r.TodLatch = [_r.TodTenths, _r.TodSeconds, _r.TodMinutes, _r.TodHours];
                    _r.TodLatched = true;
                }

                break;
            case 13:
                _r.Flags = 0;
                break;
        }
    }

    private void Write(int register, byte value)
    {
        var r = _r;
        var alarm = (r.Crb & 0x80) != 0;

        switch (register)
        {
            case 0:
                r.Pra = value;
                break;
            case 1:
                r.Prb = value;
                r.PcLow = true;
                break;
            case 2:
                r.Ddra = value;
                break;
            case 3:
                r.Ddrb = value;
                break;
            case 4:
                r.TaLatch = (ushort)((r.TaLatch & 0xFF00) | value);
                break;
            case 5:
                r.TaLatch = (ushort)((r.TaLatch & 0x00FF) | (value << 8));
                if ((r.Cra & 0x01) == 0)
                {
                    r.Ta = r.TaLatch;
                    r.ALoaded = true;
                    if ((r.Cra & 0x08) != 0) StartA(r.Cra | 0x01);
                }

                break;
            case 6:
                r.TbLatch = (ushort)((r.TbLatch & 0xFF00) | value);
                break;
            case 7:
                r.TbLatch = (ushort)((r.TbLatch & 0x00FF) | (value << 8));
                if ((r.Crb & 0x01) == 0)
                {
                    r.Tb = r.TbLatch;
                    r.BLoaded = true;
                    if ((r.Crb & 0x08) != 0) StartB(r.Crb | 0x01);
                }

                break;
            case 8:
                if (alarm) r.AlarmTenths = (byte)(value & 0x0F);
                else
                {
                    r.TodTenths = (byte)(value & 0x0F);
                    r.TodStopped = false;
                    r.TodDivider = 0;
                }

                break;
            case 9:
                if (alarm) r.AlarmSeconds = (byte)(value & 0x7F);
                else r.TodSeconds = (byte)(value & 0x7F);
                break;
            case 10:
                if (alarm) r.AlarmMinutes = (byte)(value & 0x7F);
                else r.TodMinutes = (byte)(value & 0x7F);
                break;
            case 11:
                if (alarm) r.AlarmHours = (byte)(value & 0x9F);
                else
                {
                    r.TodHours = (byte)(value & 0x9F);
                    r.TodStopped = true;
                }

                break;
            case 12:
                r.Sdr = value;
                if ((r.Cra & 0x40) != 0)
                {
                    // Output: start on the next underflow, or follow the byte going out now.
                    if (r.Sending) r.Queued = true;
                    else
                    {
                        r.Shift = value;
                        r.Bits = 0;
                        r.Sending = true;
                    }
                }

                break;
            case 13:
                if ((value & 0x80) != 0) r.Mask |= (byte)(value & 0x1F);
                else r.Mask &= (byte)~(value & 0x1F);
                break;
            case 14:
                if (((value ^ r.Cra) & 0x40) != 0) StopSerial();
                if ((value & 0x10) != 0) r.Ta = r.TaLatch;
                StartA(value);
                r.ALoaded = true;
                break;
            default:
                if ((value & 0x10) != 0) r.Tb = r.TbLatch;
                StartB(value);
                r.BLoaded = true;
                break;
        }

        NotifyValueChanged();
    }

    /// <summary>Takes a new CRA, without its force-load strobe; starting the timer sets PB6's toggle.</summary>
    private void StartA(int value)
    {
        if ((value & 0x01) != 0 && (_r.Cra & 0x01) == 0) _r.Pb6Toggle = true;
        _r.Cra = (byte)(value & ~0x10);
    }

    private void StartB(int value)
    {
        if ((value & 0x01) != 0 && (_r.Crb & 0x01) == 0) _r.Pb7Toggle = true;
        _r.Crb = (byte)(value & ~0x10);
    }

    private void StopSerial()
    {
        _r.Sending = _r.Queued = false;
        _r.Bits = 0;
        _r.CntOut = _r.SpOut = true;
    }

    /// <summary>The edges that are not on PHI2: /FLAG falling, CNT rising, and the TOD pin rising.</summary>
    private void Edges(IDigitalContext context)
    {
        var flag = !context.ReadInput(Flag, Levels).IsLow();
        var cnt = !context.ReadInput(Count, Levels).IsLow();
        var tod = context.ReadInput(TimeOfDayInput, Levels).IsHigh();

        if (!context.IsInitializing)
        {
            if (_lastFlag && !flag) _r.Flags |= FlagFlag;

            // CNT counts for the timers as the cycle ends; in serial input it shifts SP in at once.
            if (!_lastCnt && cnt && (_r.Cra & 0x40) == 0)
            {
                _cntRises++;
                _r.Shift = (byte)((_r.Shift << 1) | (context.ReadInput(SerialPort, Levels).IsLow() ? 0 : 1));
                if (++_r.Bits == 8)
                {
                    _r.Sdr = _r.Shift;
                    _r.Bits = 0;
                    _r.Flags |= FlagSerial;
                }
            }
            else if (!_lastCnt && cnt)
            {
                _cntRises++;
            }

            if (!_lastTod && tod && !_r.TodStopped && ++_r.TodDivider >= ((_r.Cra & 0x80) != 0 ? 5 : 6))
            {
                _r.TodDivider = 0;
                TickTimeOfDay();
            }
        }

        _lastFlag = flag;
        _lastCnt = cnt;
        _lastTod = tod;
    }

    /// <summary>One cycle of the timers and the interrupt line, at the end of the cycle.</summary>
    private void Tick(IDigitalContext context)
    {
        var r = _r;
        var cntRises = _cntRises;
        _cntRises = 0;
        var cntHigh = !context.ReadInput(Count, Levels).IsLow();

        var aCounts = (r.Cra & 0x01) == 0 || r.ALoaded ? 0 : (r.Cra & 0x20) == 0 ? 1 : cntRises;
        var aUnderflows = 0;
        for (var i = 0; i < aCounts; i++)
        {
            if (r.Ta == 0)
            {
                aUnderflows++;
                r.Ta = r.TaLatch;
                r.Flags |= FlagTimerA;
                r.TaPulse = true;
                r.Pb6Toggle = !r.Pb6Toggle;
                ShiftOut();
                if ((r.Cra & 0x08) != 0)
                {
                    r.Cra &= unchecked((byte)~0x01);
                    break;
                }
            }
            else
            {
                r.Ta--;
            }
        }

        var bCounts = (r.Crb & 0x01) == 0 || r.BLoaded ? 0 : ((r.Crb >> 5) & 3) switch
        {
            0 => 1,
            1 => cntRises,
            2 => aUnderflows,
            _ => cntHigh ? aUnderflows : 0,
        };

        for (var i = 0; i < bCounts; i++)
        {
            if (r.Tb == 0)
            {
                r.Tb = r.TbLatch;
                r.Flags |= FlagTimerB;
                r.TbPulse = true;
                r.Pb7Toggle = !r.Pb7Toggle;
                if ((r.Crb & 0x08) != 0)
                {
                    r.Crb &= unchecked((byte)~0x01);
                    break;
                }
            }
            else
            {
                r.Tb--;
            }
        }

        // The 6526 pulls /IRQ the cycle after the flag: an interrupt asserted for a whole cycle.
        var asserted = (r.Flags & r.Mask) != 0;
        r.IrqLine = asserted && r.AssertedLastCycle;
        r.AssertedLastCycle = asserted;
    }

    /// <summary>A timer A underflow, as the serial port sees it in output mode: half a bit.</summary>
    private void ShiftOut()
    {
        var r = _r;
        if ((r.Cra & 0x40) == 0 || !r.Sending) return;

        r.CntOut = !r.CntOut;
        if (!r.CntOut)
        {
            // CNT falls: the next bit onto SP, most significant first.
            r.SpOut = (r.Shift & 0x80) != 0;
            return;
        }

        // CNT rises: the bit is taken.
        r.Shift <<= 1;
        if (++r.Bits < 8) return;

        r.Bits = 0;
        r.Flags |= FlagSerial;
        if (r.Queued)
        {
            r.Shift = r.Sdr;
            r.Queued = false;
        }
        else
        {
            r.Sending = false;
        }
    }

    /// <summary>A tenth of a second on the time-of-day clock, and the alarm.</summary>
    private void TickTimeOfDay()
    {
        var r = _r;
        if (++r.TodTenths >= 10)
        {
            r.TodTenths = 0;
            r.TodSeconds = Bcd(r.TodSeconds);
            if (r.TodSeconds == 0x60)
            {
                r.TodSeconds = 0;
                r.TodMinutes = Bcd(r.TodMinutes);
                if (r.TodMinutes == 0x60)
                {
                    r.TodMinutes = 0;
                    var pm = r.TodHours & 0x80;
                    var hours = r.TodHours & 0x1F;

                    // 11 to 12 swaps AM and PM; 12 goes round to 1.
                    if (hours == 0x11) r.TodHours = (byte)(0x12 | (pm ^ 0x80));
                    else if (hours == 0x12) r.TodHours = (byte)(0x01 | pm);
                    else r.TodHours = (byte)(Bcd((byte)hours) | pm);
                }
            }
        }

        if (r.TodTenths == r.AlarmTenths && r.TodSeconds == r.AlarmSeconds &&
            r.TodMinutes == r.AlarmMinutes && r.TodHours == r.AlarmHours)
            r.Flags |= FlagAlarm;

        NotifyValueChanged();
    }

    /// <summary>One more, in BCD.</summary>
    private static byte Bcd(byte value) =>
        (value & 0x0F) >= 9 ? (byte)((value & 0xF0) + 0x10) : (byte)(value + 1);

    private void DriveOutputs(IDigitalContext context, bool driveData, byte value)
    {
        var r = _r;
        var delay = DelayFor(context);

        for (var i = 0; i < 8; i++)
        {
            var a = (r.Ddra & (1 << i)) != 0;
            Request(context, i, a ? Level((r.Pra >> i) & 1) : LogicState.HighImpedance, delay);

            var b = (r.Ddrb & (1 << i)) != 0;
            var bit = (r.Prb >> i) & 1;
            if (i == 6 && (r.Cra & 0x02) != 0)
            {
                b = true;
                bit = ((r.Cra & 0x04) != 0 ? r.Pb6Toggle : r.TaPulse) ? 1 : 0;
            }
            else if (i == 7 && (r.Crb & 0x02) != 0)
            {
                b = true;
                bit = ((r.Crb & 0x04) != 0 ? r.Pb7Toggle : r.TbPulse) ? 1 : 0;
            }

            // An output high is the pull-up's job as well as the driver's; an output low is a pin pulled down.
            Request(context, 8 + i, b ? Level(bit) : LogicState.HighImpedance, delay);
            Request(context, DataIndex + i, driveData ? Level((value >> i) & 1) : LogicState.HighImpedance, delay);
        }

        var serialOut = (r.Cra & 0x40) != 0;
        Request(context, PcIndex, r.PcLow ? LogicState.Low : LogicState.High, delay);
        Request(context, SpIndex, serialOut ? Level(r.SpOut ? 1 : 0) : LogicState.HighImpedance, delay);
        Request(context, CntIndex, serialOut ? Level(r.CntOut ? 1 : 0) : LogicState.HighImpedance, delay);
        Request(context, IrqIndex, r.IrqLine ? LogicState.Low : LogicState.HighImpedance, delay);
    }

    private static LogicState Level(int bit) => bit != 0 ? LogicState.High : LogicState.Low;

    /// <summary>What /RES does: every register cleared, the timer latches and counters to all ones.</summary>
    private void ResetRegisters() => _r = new Registers();

    public override void ResetLogic()
    {
        base.ResetLogic();
        ResetRegisters();
        _lastPhi2 = LogicState.Unknown;
        _lastFlag = _lastCnt = true;
        _lastTod = false;
        _cntRises = 0;
    }

    public object CaptureRunState() => _r.Clone();

    public void AdoptRunState(object state)
    {
        if (state is not Registers registers) return;
        _r = registers.Clone();
        NotifyValueChanged();
    }
}
