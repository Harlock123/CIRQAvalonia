using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// 74373 octal transparent latch with tri-state outputs.
/// <para>
/// The word is <b>transparent</b>, and it is the difference between this and the flip-flops
/// elsewhere in the palette. A 7474 or a 4013 samples its input on an <i>edge</i> and ignores it
/// the rest of the time. This one, while its latch enable is high, is a piece of wire: the outputs
/// follow the inputs continuously. It only remembers when <c>LE</c> goes low, and what it
/// remembers is whatever the inputs happened to be at that instant.
/// </para>
/// <para>
/// People confuse the two constantly, and the way to tell them apart is to watch what happens
/// while the enable is held high. A latch passes everything through, glitches included. A
/// flip-flop passes nothing until the next edge. Which one you want depends on whether you are
/// holding a value or sampling one.
/// </para>
/// <para>
/// Its classic use is the one that makes a <b>multiplexed bus</b> possible. Address and data share
/// the same eight pins on parts like the 8051 and 8085: the processor puts the low address byte
/// out first with a strobe, this grabs it, and then the same wires carry data while the latch
/// holds the address steady for the memory to see. That is why every board built around those
/// processors has one of these next to the CPU.
/// </para>
/// <para>
/// Pinout: 1=/OE, 2=Q0, 3=D0, 4=D1, 5=Q1, 6=Q2, 7=D2, 8=D3, 9=Q3, 10=GND,
/// 11=LE, 12=Q4, 13=D4, 14=D5, 15=Q5, 16=Q6, 17=D6, 18=D7, 19=Q7, 20=VCC.
/// </para>
/// </summary>
public sealed class Ic74373 : DigitalIc
{
    private const int Width = 8;

    private readonly Terminal[] _d = new Terminal[Width];
    private readonly Terminal[] _q = new Terminal[Width];

    private int _held;

    public Ic74373() : base(20)
    {
        PropagationDelay = 12e-9;

        var pins = new Terminal[21];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 20));

        OutputEnable = Pin(1, "OE", TerminalType.Input);

        _q[0] = Pin(2, "Q0", TerminalType.Output);
        _d[0] = Pin(3, "D0", TerminalType.Input);
        _d[1] = Pin(4, "D1", TerminalType.Input);
        _q[1] = Pin(5, "Q1", TerminalType.Output);
        _q[2] = Pin(6, "Q2", TerminalType.Output);
        _d[2] = Pin(7, "D2", TerminalType.Input);
        _d[3] = Pin(8, "D3", TerminalType.Input);
        _q[3] = Pin(9, "Q3", TerminalType.Output);

        Gnd = Pin(10, "GND", TerminalType.Ground);
        LatchEnable = Pin(11, "LE", TerminalType.Input);

        _q[4] = Pin(12, "Q4", TerminalType.Output);
        _d[4] = Pin(13, "D4", TerminalType.Input);
        _d[5] = Pin(14, "D5", TerminalType.Input);
        _q[5] = Pin(15, "Q5", TerminalType.Output);
        _q[6] = Pin(16, "Q6", TerminalType.Output);
        _d[6] = Pin(17, "D6", TerminalType.Input);
        _d[7] = Pin(18, "D7", TerminalType.Input);
        _q[7] = Pin(19, "Q7", TerminalType.Output);

        Vcc = Pin(20, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([OutputEnable, LatchEnable, .. _d], _q);
    }

    /// <summary>Active low. High releases the outputs without disturbing what is held.</summary>
    public Terminal OutputEnable { get; }

    /// <summary>High makes it transparent; the falling edge is what captures.</summary>
    public Terminal LatchEnable { get; }

    /// <summary>The eight data inputs, D0 through D7.</summary>
    public IReadOnlyList<Terminal> Data => _d;

    /// <summary>The eight outputs, Q0 through Q7.</summary>
    public IReadOnlyList<Terminal> Outputs => _q;

    public override string PartNumber => "74373";

    public override string ValueLabel => IsTransparent ? $"0x{_held:X2} (open)" : $"0x{_held:X2}";

    /// <summary>The byte it is holding, or passing through while transparent.</summary>
    public int Value => _held;

    /// <summary>True while the latch is open and the outputs are simply following the inputs.</summary>
    public bool IsTransparent { get; private set; }

    /// <summary>True while the outputs are released and the byte is held but not shown.</summary>
    public bool IsReleased { get; private set; }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);

        IsTransparent = context.ReadInput(LatchEnable, Levels).IsHigh();
        IsReleased = context.ReadInput(OutputEnable, Levels).IsHigh();

        // Transparent means transparent: while the enable is high this is a wire, and the held
        // value simply tracks whatever the inputs are doing. It stops tracking when LE goes low,
        // which is the capture — there is no edge detection here because there is no edge to
        // detect, only a moment when the following stops.
        if (IsTransparent)
        {
            var value = 0;

            for (var i = 0; i < Width; i++)
                if (context.ReadInput(_d[i], Levels).IsHigh()) value |= 1 << i;

            _held = value;
        }

        for (var i = 0; i < Width; i++)
        {
            var bit = (_held & (1 << i)) != 0;

            context.Schedule(this, i,
                IsReleased ? LogicState.HighImpedance : bit ? LogicState.High : LogicState.Low, delay);
        }

        NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        _held = 0;
        IsTransparent = false;
        IsReleased = false;
    }
}
