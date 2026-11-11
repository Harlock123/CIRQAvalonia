using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;

namespace Cirq.Components.Processors;

/// <summary>
/// The MOS 6532 RIOT — RAM, I/O and timer — of the Atari 2600 and the KIM-1, on a 6502's bus: 128 bytes of RAM, two
/// 8-bit ports with their direction registers, an interval timer and an edge detector on PA7, in a DIP-40.
/// <para>
/// It's selected while CS1 is high and /CS2 low, and takes a write (or does a read's side effects) as Φ2 falls, like the
/// 6522. /RS low picks the RAM (A0-A6); /RS high the registers, by A0-A4: with A2 low, ORA, DDRA, ORB and DDRB (A1, A0);
/// with A2 high, a write with A4 high sets the timer, A1 and A0 choosing it to count every 1, 8, 64 or 1024 cycles (A3
/// enabling its interrupt), and a write with A4 low sets PA7's edge (A0: 1 positive) and enables its interrupt (A1); a
/// read with A0 low reads the timer, A0 high the flags (bit 7 the timer, bit 6 PA7), which clears PA7's. The timer
/// counts down at its rate to zero, sets its flag, then carries on down once a cycle so a program can see how late it
/// is; a read or write of the timer clears the flag. /IRQ (open drain) is low while an enabled flag is set. Port A's pins
/// are pulled up; port B's are three-state as inputs. Pinout: 1 VSS, 2-7 A5-A0, 8-15 PA0-PA7, 16-19 PB7-PB4, 20 VCC,
/// 21-24 PB3-PB0, 25 /IRQ, 26-33 D7-D0, 34 /RES, 35 R/W, 36 /RS, 37 /CS2, 38 CS1, 39 Φ2, 40 A6.
/// </para>
/// </summary>
public sealed partial class Riot6532 : DigitalIc, IRunState
{
    public const double PullUpResistance = 10e3;

    private readonly Terminal[] _a = new Terminal[7];
    private readonly Terminal[] _pa = new Terminal[8];
    private readonly Terminal[] _pb = new Terminal[8];
    private readonly Terminal[] _d = new Terminal[8];
    private readonly byte[] _ram = new byte[128];
    private byte _ora, _ddra, _orb, _ddrb;
    private int _timer = 0xFF, _prescale = 1024, _countdown = 1024;
    private bool _timerFlag, _timerIrq, _pa7Flag, _pa7Irq, _pa7Positive, _lastPa7, _expired;
    private LogicState _lastPhi2;

    public Riot6532() : base(40)
    {
        PropagationDelay = 50e-9;
        var pins = new Terminal[41];
        Terminal Pin(int n, string name, TerminalType type) => pins[n] = new Terminal($"p{n}", name, type, DipPackage.PinOffset(n, 40));
        Gnd = Pin(1, "VSS", TerminalType.Ground);
        for (var i = 0; i < 6; i++) _a[5 - i] = Pin(2 + i, $"A{5 - i}", TerminalType.Input);
        for (var i = 0; i < 8; i++) _pa[i] = Pin(8 + i, $"PA{i}", TerminalType.Bidirectional);
        for (var i = 0; i < 4; i++) _pb[7 - i] = Pin(16 + i, $"PB{7 - i}", TerminalType.Bidirectional);
        Vcc = Pin(20, "VCC", TerminalType.Power);
        for (var i = 0; i < 4; i++) _pb[3 - i] = Pin(21 + i, $"PB{3 - i}", TerminalType.Bidirectional);
        Irq = Pin(25, "IRQ", TerminalType.Output);
        for (var i = 0; i < 8; i++) _d[7 - i] = Pin(26 + i, $"D{7 - i}", TerminalType.Bidirectional);
        Reset = Pin(34, "RES", TerminalType.Input);
        ReadWrite = Pin(35, "R/W", TerminalType.Input);
        RamSelect = Pin(36, "RS", TerminalType.Input);
        ChipSelect2 = Pin(37, "CS2", TerminalType.Input);
        ChipSelect1 = Pin(38, "CS1", TerminalType.Input);
        Clock = Pin(39, "PHI2", TerminalType.Input);
        _a[6] = Pin(40, "A6", TerminalType.Input);
        Terminals = [.. pins.Skip(1)];
        ConfigurePins([Clock, Reset, ReadWrite, RamSelect, ChipSelect1, ChipSelect2, .. _a, .. _d, .. _pa, .. _pb], [.. _d, .. _pa, .. _pb, Irq]);
    }

    public Terminal Irq { get; }
    public Terminal Reset { get; }
    public Terminal ReadWrite { get; }
    public Terminal RamSelect { get; }
    public Terminal ChipSelect1 { get; }
    public Terminal ChipSelect2 { get; }
    public Terminal Clock { get; }

    public IReadOnlyList<Terminal> Address => _a;
    public IReadOnlyList<Terminal> Data => _d;
    public IReadOnlyList<Terminal> PortA => _pa;
    public IReadOnlyList<Terminal> PortB => _pb;

    public IReadOnlyList<byte> Ram => _ram;

    public int Timer => _timer;

    public bool TimerFlag => _timerFlag;

    public override string PartNumber => "6532";

    public override bool LabelsPins => true;

    public override string PinLabel(Terminal pin) => pin.Name switch
    {
        "IRQ" or "RES" or "RS" or "CS2" => "/" + pin.Name,
        "PHI2" => "Φ2",
        _ => pin.Name,
    };

    public override string ValueLabel => $"6532 timer {_timer:X2}";

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);
        var vcc = system.Node(Vcc);
        for (var i = 0; i < 8; i++)
            if ((_ddra & (1 << i)) == 0) system.StampResistor(system.Node(_pa[i]), vcc, PullUpResistance);
    }

    private void ResetRegisters()
    {
        (_ora, _ddra, _orb, _ddrb) = (0, 0, 0, 0);
        (_timerIrq, _pa7Irq, _pa7Flag, _timerFlag, _pa7Positive, _expired) = (false, false, false, false, false, false);
    }

    private static byte Pins(IDigitalContext context, Terminal[] port, LogicLevels levels)
    {
        var value = 0;
        for (var i = 0; i < 8; i++)
            if (context.ReadInput(port[i], levels).IsHigh()) value |= 1 << i;
        return (byte)value;
    }

    private void Tick()
    {
        if (_expired)
        {
            _timer = (_timer - 1) & 0xFF;
            return;
        }

        if (--_countdown > 0) return;
        _countdown = _prescale;
        if (_timer == 0)
        {
            _timerFlag = true;
            _expired = true;
            _timer = 0xFF;
        }
        else _timer--;
    }

    private byte Read(int address, bool ram, IDigitalContext context)
    {
        if (ram) return _ram[address & 0x7F];
        if ((address & 4) == 0)
            return (address & 3) switch
            {
                0 => (byte)((_ora & _ddra) | (Pins(context, _pa, Levels) & ~_ddra)),
                1 => _ddra,
                2 => (byte)((_orb & _ddrb) | (Pins(context, _pb, Levels) & ~_ddrb)),
                _ => _ddrb,
            };
        return (address & 1) == 0 ? (byte)_timer : (byte)((_timerFlag ? 0x80 : 0) | (_pa7Flag ? 0x40 : 0));
    }

    private void AfterRead(int address, bool ram)
    {
        if (ram || (address & 4) == 0) return;
        if ((address & 1) == 0)
        {
            _timerIrq = (address & 8) != 0;
            if (_timerFlag) _timerFlag = false;
        }
        else _pa7Flag = false;
    }

    private void Write(int address, bool ram, byte value)
    {
        if (ram)
        {
            _ram[address & 0x7F] = value;
            return;
        }

        if ((address & 4) == 0)
        {
            switch (address & 3)
            {
                case 0: _ora = value; break;
                case 1: _ddra = value; break;
                case 2: _orb = value; break;
                default: _ddrb = value; break;
            }

            return;
        }

        if ((address & 0x10) != 0)
        {
            _prescale = (address & 3) switch { 0 => 1, 1 => 8, 2 => 64, _ => 1024 };
            _countdown = _prescale;
            _timer = value;
            _timerIrq = (address & 8) != 0;
            (_timerFlag, _expired) = (false, false);
        }
        else
        {
            _pa7Positive = (address & 1) != 0;
            _pa7Irq = (address & 2) != 0;
        }
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var phi2 = context.ReadInput(Clock, Levels);
        var previous = _lastPhi2;
        _lastPhi2 = phi2;
        if (context.ReadInput(Reset, Levels).IsLow()) ResetRegisters();

        var pa7 = context.ReadInput(_pa[7], Levels).IsHigh();
        if (pa7 != _lastPa7 && pa7 == _pa7Positive) _pa7Flag = true;
        _lastPa7 = pa7;

        var selected = context.ReadInput(ChipSelect1, Levels).IsHigh() && context.ReadInput(ChipSelect2, Levels).IsLow();
        var reading = context.ReadInput(ReadWrite, Levels).IsHigh();
        var ram = context.ReadInput(RamSelect, Levels).IsLow();
        var address = 0;
        for (var i = 0; i < 7; i++)
            if (context.ReadInput(_a[i], Levels).IsHigh()) address |= 1 << i;

        if (!context.IsInitializing && previous.IsHigh() && phi2.IsLow())
        {
            if (selected)
            {
                if (reading) AfterRead(address, ram);
                else Write(address, ram, Pins(context, _d, Levels));
            }

            Tick();
            NotifyValueChanged();
        }

        var driveData = selected && reading && phi2.IsHigh();
        var value = driveData ? Read(address, ram, context) : (byte)0;
        var delay = DelayFor(context);
        for (var i = 0; i < 8; i++)
        {
            Request(context, i, driveData ? (((value >> i) & 1) != 0 ? LogicState.High : LogicState.Low) : LogicState.HighImpedance, delay);
            Request(context, 8 + i, (_ddra & (1 << i)) == 0 ? LogicState.HighImpedance : ((_ora >> i) & 1) != 0 ? LogicState.High : LogicState.Low, delay);
            Request(context, 16 + i, (_ddrb & (1 << i)) == 0 ? LogicState.HighImpedance : ((_orb >> i) & 1) != 0 ? LogicState.High : LogicState.Low, delay);
        }

        var irq = (_timerFlag && _timerIrq) || (_pa7Flag && _pa7Irq);
        Request(context, 24, irq ? LogicState.Low : LogicState.HighImpedance, delay);
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        ResetRegisters();
        Array.Clear(_ram);
        (_timer, _prescale, _countdown, _lastPa7, _lastPhi2) = (0xFF, 1024, 1024, false, LogicState.Unknown);
    }

    public object CaptureRunState() => ((byte[])_ram.Clone(), _ora, _ddra, _orb, _ddrb, _timer);

    public void AdoptRunState(object state)
    {
        if (state is not (byte[] ram, byte ora, byte ddra, byte orb, byte ddrb, int timer)) return;
        Array.Copy(ram, _ram, 128);
        (_ora, _ddra, _orb, _ddrb, _timer) = (ora, ddra, orb, ddrb, timer);
    }
}
