using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// 8-bit serial-in, parallel-out shift register.
/// <para>
/// Pinout: 1=A, 2=B, 3=QA, 4=QB, 5=QC, 6=QD, 7=GND,
/// 8=CLK, 9=CLR, 10=QE, 11=QF, 12=QG, 13=QH, 14=VCC.
/// </para>
/// <para>
/// The two serial inputs are ANDed together, so one of them doubles as a gate on the incoming
/// data; tie it high if you only want the other. Clear is asynchronous and active low. Data
/// advances on the rising clock edge.
/// </para>
/// </summary>
public sealed class Ic74164 : DigitalIc
{
    private readonly Terminal[] _outputs = new Terminal[8];
    private LogicState _lastClock = LogicState.Unknown;
    private int _shiftRegister;

    public Ic74164() : base(14)
    {
        PropagationDelay = 18e-9;

        var pins = new Terminal[15];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 14));

        SerialA = Pin(1, "A", TerminalType.Input);
        SerialB = Pin(2, "B", TerminalType.Input);
        _outputs[0] = Pin(3, "QA", TerminalType.Output);
        _outputs[1] = Pin(4, "QB", TerminalType.Output);
        _outputs[2] = Pin(5, "QC", TerminalType.Output);
        _outputs[3] = Pin(6, "QD", TerminalType.Output);
        Gnd = Pin(7, "GND", TerminalType.Ground);
        Clock = Pin(8, "CLK", TerminalType.Input);
        Clear = Pin(9, "CLR", TerminalType.Input);
        _outputs[4] = Pin(10, "QE", TerminalType.Output);
        _outputs[5] = Pin(11, "QF", TerminalType.Output);
        _outputs[6] = Pin(12, "QG", TerminalType.Output);
        _outputs[7] = Pin(13, "QH", TerminalType.Output);
        Vcc = Pin(14, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([SerialA, SerialB, Clock, Clear], _outputs);
    }

    public Terminal SerialA { get; }
    public Terminal SerialB { get; }
    public Terminal Clock { get; }

    /// <summary>Asynchronous, active low.</summary>
    public Terminal Clear { get; }

    public override string PartNumber => "74164";

    /// <summary>The eight parallel outputs, QA through QH.</summary>
    public IReadOnlyList<Terminal> Outputs => _outputs;

    /// <summary>Register contents, with QA as bit 0.</summary>
    public int Value => _shiftRegister & 0xFF;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var clock = context.ReadInput(Clock, Levels);

        if (context.ReadInput(Clear, Levels).IsLow())
        {
            _shiftRegister = 0;
        }
        else if (_lastClock.IsLow() && clock.IsHigh())
        {
            var incoming =
                context.ReadInput(SerialA, Levels).IsHigh() &&
                context.ReadInput(SerialB, Levels).IsHigh();

            _shiftRegister = ((_shiftRegister << 1) | (incoming ? 1 : 0)) & 0xFF;
        }

        _lastClock = clock;

        var delay = DelayFor(context);
        for (var i = 0; i < 8; i++)
            context.Schedule(this, i, LogicStateExtensions.FromBool((_shiftRegister & (1 << i)) != 0), delay);
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _shiftRegister = 0;
        _lastClock = LogicState.Unknown;
    }
}

/// <summary>
/// 8-bit parallel-in, serial-out shift register.
/// <para>
/// Pinout: 1=SH/LD, 2=CLK, 3=E, 4=F, 5=G, 6=H, 7=QH', 8=GND,
/// 9=QH, 10=SER, 11=A, 12=B, 13=C, 14=D, 15=CLK INH, 16=VCC.
/// </para>
/// <para>
/// Taking SH/LD low loads the eight parallel inputs asynchronously; releasing it shifts towards QH
/// on each rising clock edge. The clock-inhibit pin gates the clock, which is what lets one of
/// these be read out under the control of a slower device.
/// </para>
/// </summary>
public sealed class Ic74165 : DigitalIc
{
    private readonly Terminal[] _parallel = new Terminal[8];
    private LogicState _lastClock = LogicState.Unknown;
    private int _shiftRegister;

    public Ic74165() : base(16)
    {
        PropagationDelay = 20e-9;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        ShiftLoad = Pin(1, "SH/LD", TerminalType.Input);
        Clock = Pin(2, "CLK", TerminalType.Input);
        _parallel[4] = Pin(3, "E", TerminalType.Input);
        _parallel[5] = Pin(4, "F", TerminalType.Input);
        _parallel[6] = Pin(5, "G", TerminalType.Input);
        _parallel[7] = Pin(6, "H", TerminalType.Input);
        InvertedOutput = Pin(7, "QH'", TerminalType.Output);
        Gnd = Pin(8, "GND", TerminalType.Ground);
        Output = Pin(9, "QH", TerminalType.Output);
        SerialInput = Pin(10, "SER", TerminalType.Input);
        _parallel[0] = Pin(11, "A", TerminalType.Input);
        _parallel[1] = Pin(12, "B", TerminalType.Input);
        _parallel[2] = Pin(13, "C", TerminalType.Input);
        _parallel[3] = Pin(14, "D", TerminalType.Input);
        ClockInhibit = Pin(15, "CLK INH", TerminalType.Input);
        Vcc = Pin(16, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([.. _parallel, ShiftLoad, Clock, ClockInhibit, SerialInput], [Output, InvertedOutput]);
    }

    /// <summary>The eight parallel inputs, A through H. A is the first bit shifted out.</summary>
    public IReadOnlyList<Terminal> ParallelInputs => _parallel;

    /// <summary>Active low: loads the parallel inputs.</summary>
    public Terminal ShiftLoad { get; }

    public Terminal Clock { get; }

    /// <summary>Active high: blocks the clock.</summary>
    public Terminal ClockInhibit { get; }

    public Terminal SerialInput { get; }

    public Terminal Output { get; }

    public Terminal InvertedOutput { get; }

    public override string PartNumber => "74165";

    /// <summary>Register contents, with A as bit 0.</summary>
    public int Value => _shiftRegister & 0xFF;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var clock = context.ReadInput(Clock, Levels);
        var inhibited = context.ReadInput(ClockInhibit, Levels).IsHigh();

        if (context.ReadInput(ShiftLoad, Levels).IsLow())
        {
            // Asynchronous parallel load, which beats the clock entirely.
            _shiftRegister = 0;
            for (var i = 0; i < 8; i++)
                if (context.ReadInput(_parallel[i], Levels).IsHigh())
                    _shiftRegister |= 1 << i;
        }
        else if (!inhibited && _lastClock.IsLow() && clock.IsHigh())
        {
            var incoming = context.ReadInput(SerialInput, Levels).IsHigh();
            // Shift towards H, with the serial input entering at A.
            _shiftRegister = ((_shiftRegister >> 1) | (incoming ? 0x80 : 0)) & 0xFF;
        }

        _lastClock = clock;

        var q = LogicStateExtensions.FromBool((_shiftRegister & 0x80) != 0);
        var delay = DelayFor(context);
        context.Schedule(this, 0, q, delay);
        context.Schedule(this, 1, q.Invert(), delay);
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _shiftRegister = 0;
        _lastClock = LogicState.Unknown;
    }
}
