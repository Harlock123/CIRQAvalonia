using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// Base for the 8-to-1 multiplexers with complementary outputs, the 74151 and the 74251 — one pinout,
/// one difference. With its strobe high the 74151 forces Y low and W high; the 74251's pin 7 is an
/// output enable instead, and high releases both outputs, so several can be wired onto one bus line
/// and the one enabled is the one heard.
/// <para>
/// Pinout: 1=D3, 2=D2, 3=D1, 4=D0, 5=Y, 6=W, 7=E (or OE), 8=GND,
/// 9=A0, 10=A1, 11=A2, 12=D7, 13=D6, 14=D5, 15=D4, 16=VCC.
/// </para>
/// </summary>
public abstract class EightToOneMultiplexerIc : DigitalIc
{
    private readonly Terminal[] _data = new Terminal[8];

    protected EightToOneMultiplexerIc(bool triState) : base(16)
    {
        IsTriState = triState;
        PropagationDelay = 22e-9;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        var output = triState ? TerminalType.Bidirectional : TerminalType.Output;

        _data[3] = Pin(1, "D3", TerminalType.Input);
        _data[2] = Pin(2, "D2", TerminalType.Input);
        _data[1] = Pin(3, "D1", TerminalType.Input);
        _data[0] = Pin(4, "D0", TerminalType.Input);
        Output = Pin(5, "Y", output);
        InvertedOutput = Pin(6, "W", output);
        Strobe = Pin(7, triState ? "OE" : "E", TerminalType.Input);
        Gnd = Pin(8, "GND", TerminalType.Ground);
        SelectA = Pin(9, "A0", TerminalType.Input);
        SelectB = Pin(10, "A1", TerminalType.Input);
        SelectC = Pin(11, "A2", TerminalType.Input);
        _data[7] = Pin(12, "D7", TerminalType.Input);
        _data[6] = Pin(13, "D6", TerminalType.Input);
        _data[5] = Pin(14, "D5", TerminalType.Input);
        _data[4] = Pin(15, "D4", TerminalType.Input);
        Vcc = Pin(16, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([.. _data, SelectA, SelectB, SelectC, Strobe], [Output, InvertedOutput]);
    }

    /// <summary>The eight data inputs, D0 through D7.</summary>
    public IReadOnlyList<Terminal> Data => _data;

    public Terminal SelectA { get; }
    public Terminal SelectB { get; }
    public Terminal SelectC { get; }

    /// <summary>
    /// Active low. High forces the 74151's outputs to their inactive state, and releases the 74251's.
    /// </summary>
    public Terminal Strobe { get; }

    public Terminal Output { get; }

    public Terminal InvertedOutput { get; }

    /// <summary>True for the 74251, whose outputs let go rather than being forced.</summary>
    public bool IsTriState { get; }

    /// <summary>The data input currently routed to the output, or -1 while strobed off.</summary>
    public int SelectedInput { get; private set; } = -1;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);

        if (!context.ReadInput(Strobe, Levels).IsLow())
        {
            SelectedInput = -1;
            context.Schedule(this, 0, IsTriState ? LogicState.HighImpedance : LogicState.Low, delay);
            context.Schedule(this, 1, IsTriState ? LogicState.HighImpedance : LogicState.High, delay);
            return;
        }

        var address = 0;
        if (context.ReadInput(SelectA, Levels).IsHigh()) address |= 1;
        if (context.ReadInput(SelectB, Levels).IsHigh()) address |= 2;
        if (context.ReadInput(SelectC, Levels).IsHigh()) address |= 4;

        SelectedInput = address;

        var selected = context.ReadInput(_data[address], Levels);
        context.Schedule(this, 0, selected, delay);
        context.Schedule(this, 1, selected.Invert(), delay);
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        SelectedInput = -1;
    }
}

/// <summary>8-to-1 multiplexer with complementary outputs.</summary>
public sealed class Ic74151() : EightToOneMultiplexerIc(triState: false)
{
    public override string PartNumber => "74151";
}

/// <summary>8-to-1 multiplexer with complementary tri-state outputs, released while OE is high.</summary>
public sealed class Ic74251() : EightToOneMultiplexerIc(triState: true)
{
    public override string PartNumber => "74251";
}

/// <summary>
/// 74153 dual 4-to-1 multiplexer: two independent selectors sharing their two select lines, each with
/// its own active-low strobe that forces its output low when high. Two of them pick one of four
/// two-bit words; the select lines are B (pin 2) and A (pin 14), A the low bit.
/// <para>
/// Pinout: 1=1G, 2=B, 3=1C3, 4=1C2, 5=1C1, 6=1C0, 7=1Y, 8=GND, 9=2Y, 10=2C0, 11=2C1, 12=2C2, 13=2C3,
/// 14=A, 15=2G, 16=VCC.
/// </para>
/// </summary>
public sealed class Ic74153 : DigitalIc
{
    private readonly Terminal[][] _c = [new Terminal[4], new Terminal[4]];
    private readonly Terminal[] _strobe = new Terminal[2];
    private readonly Terminal[] _y = new Terminal[2];

    public Ic74153() : base(16)
    {
        PropagationDelay = 18e-9;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        _strobe[0] = Pin(1, "1G", TerminalType.Input);
        SelectB = Pin(2, "B", TerminalType.Input);
        for (var i = 0; i < 4; i++) _c[0][i] = Pin(6 - i, $"1C{i}", TerminalType.Input);
        _y[0] = Pin(7, "1Y", TerminalType.Output);
        Gnd = Pin(8, "GND", TerminalType.Ground);
        _y[1] = Pin(9, "2Y", TerminalType.Output);
        for (var i = 0; i < 4; i++) _c[1][i] = Pin(10 + i, $"2C{i}", TerminalType.Input);
        SelectA = Pin(14, "A", TerminalType.Input);
        _strobe[1] = Pin(15, "2G", TerminalType.Input);
        Vcc = Pin(16, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([SelectA, SelectB, .. _strobe, .. _c[0], .. _c[1]], _y);
    }

    public override string PartNumber => "74153";

    /// <summary>The low select line.</summary>
    public Terminal SelectA { get; }

    /// <summary>The high select line.</summary>
    public Terminal SelectB { get; }

    /// <summary>One half's active-low strobe.</summary>
    public Terminal Strobe(int half) => _strobe[half];

    /// <summary>One half's data inputs, C0 to C3.</summary>
    public IReadOnlyList<Terminal> Data(int half) => _c[half];

    /// <summary>One half's output.</summary>
    public Terminal Output(int half) => _y[half];

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        var select = (context.ReadInput(SelectA, Levels).IsHigh() ? 1 : 0)
                     | (context.ReadInput(SelectB, Levels).IsHigh() ? 2 : 0);

        for (var half = 0; half < 2; half++)
        {
            var on = context.ReadInput(_strobe[half], Levels).IsLow();
            Request(context, half, on ? context.ReadInput(_c[half][select], Levels) : LogicState.Low, delay);
        }
    }
}

/// <summary>
/// Base for the quad 2-to-1 multiplexers on one pinout: the 74157 (true outputs), the 74158 (inverted)
/// and the 74257 (true, tri-state). SELECT low passes each A input, high each B; the strobe on pin 15
/// is active low, and high forces the 157's outputs low, the 158's high, and releases the 257's — which
/// is how two 257s, or a 257 and anything else tri-state, share four bus lines.
/// <para>
/// Pinout: 1=SEL, 2=1A, 3=1B, 4=1Y, 5=2A, 6=2B, 7=2Y, 8=GND, 9=3Y, 10=3B, 11=3A, 12=4Y, 13=4B, 14=4A,
/// 15=G (OE on the 74257), 16=VCC.
/// </para>
/// </summary>
public abstract class QuadTwoToOneMultiplexerIc : DigitalIc
{
    private readonly Terminal[] _a = new Terminal[4];
    private readonly Terminal[] _b = new Terminal[4];
    private readonly Terminal[] _y = new Terminal[4];

    protected QuadTwoToOneMultiplexerIc(bool inverting, bool triState) : base(16)
    {
        IsInverting = inverting;
        IsTriState = triState;
        PropagationDelay = 14e-9;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        var output = triState ? TerminalType.Bidirectional : TerminalType.Output;

        // (A, B, Y) per section.
        int[][] pinout = [[2, 3, 4], [5, 6, 7], [11, 10, 9], [14, 13, 12]];

        for (var i = 0; i < 4; i++)
        {
            _a[i] = Pin(pinout[i][0], $"{i + 1}A", TerminalType.Input);
            _b[i] = Pin(pinout[i][1], $"{i + 1}B", TerminalType.Input);
            _y[i] = Pin(pinout[i][2], $"{i + 1}Y", output);
        }

        Select = Pin(1, "SEL", TerminalType.Input);
        Strobe = Pin(15, triState ? "OE" : "G", TerminalType.Input);
        Gnd = Pin(8, "GND", TerminalType.Ground);
        Vcc = Pin(16, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([Select, Strobe, .. _a, .. _b], _y);
    }

    /// <summary>Low passes the A inputs, high the B inputs.</summary>
    public Terminal Select { get; }

    /// <summary>Active low.</summary>
    public Terminal Strobe { get; }

    public IReadOnlyList<Terminal> A => _a;

    public IReadOnlyList<Terminal> B => _b;

    public IReadOnlyList<Terminal> Outputs => _y;

    /// <summary>True for the 74158.</summary>
    public bool IsInverting { get; }

    /// <summary>True for the 74257.</summary>
    public bool IsTriState { get; }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        var on = context.ReadInput(Strobe, Levels).IsLow();
        var useB = context.ReadInput(Select, Levels).IsHigh();

        for (var i = 0; i < 4; i++)
        {
            LogicState state;

            if (!on)
            {
                state = IsTriState ? LogicState.HighImpedance : LogicState.Low;
            }
            else
            {
                state = context.ReadInput(useB ? _b[i] : _a[i], Levels);
                if (!state.IsDriven()) state = LogicState.Unknown;
            }

            if (IsInverting) state = state.Invert();
            Request(context, i, state, delay);
        }
    }
}

/// <summary>Quad 2-to-1 multiplexer.</summary>
public sealed class Ic74157() : QuadTwoToOneMultiplexerIc(inverting: false, triState: false)
{
    public override string PartNumber => "74157";
}

/// <summary>Quad 2-to-1 multiplexer with inverted outputs.</summary>
public sealed class Ic74158() : QuadTwoToOneMultiplexerIc(inverting: true, triState: false)
{
    public override string PartNumber => "74158";
}

/// <summary>Quad 2-to-1 multiplexer with tri-state outputs, released while OE is high.</summary>
public sealed class Ic74257() : QuadTwoToOneMultiplexerIc(inverting: false, triState: true)
{
    public override string PartNumber => "74257";
}

/// <summary>
/// Base for the four-line decoders that select one output by a binary code: the 7442 and 4028
/// (BCD to one of ten) and the 74154 (one of sixteen, with two enables). The TTL parts' outputs are
/// active low, the 4028's active high; a code with no output — 10 to 15 on the BCD parts — selects
/// none.
/// </summary>
public abstract class LineDecoderIc : DigitalIc
{
    private readonly Terminal[] _address = new Terminal[4];
    private readonly Terminal[] _outputs;
    private readonly Terminal[] _enables;
    private readonly bool _activeLow;

    /// <param name="addressPins">A, B, C, D: the code's bits, least significant first.</param>
    /// <param name="outputPins">The pin each output is on, output 0 first.</param>
    /// <param name="enablePins">Active-low enables, all of which must be low; none on the BCD parts.</param>
    protected LineDecoderIc(int pinCount, int[] addressPins, int[] outputPins, int[] enablePins,
        int vccPin, int gndPin, bool cmos)
        : base(pinCount)
    {
        _activeLow = !cmos;
        Levels = cmos ? LogicLevels.Cmos5V : LogicLevels.Ttl;
        PropagationDelay = cmos ? 200e-9 : 20e-9;

        var pins = new Terminal[pinCount + 1];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, pinCount));

        for (var i = 0; i < 4; i++) _address[i] = Pin(addressPins[i], ((char)('A' + i)).ToString(), TerminalType.Input);
        _outputs = [.. outputPins.Select((p, i) => Pin(p, cmos ? $"Q{i}" : $"Y{i}", TerminalType.Output))];
        _enables = [.. enablePins.Select((p, i) => Pin(p, $"G{i + 1}", TerminalType.Input))];
        Gnd = Pin(gndPin, cmos ? "VSS" : "GND", TerminalType.Ground);
        Vcc = Pin(vccPin, cmos ? "VDD" : "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([.. _address, .. _enables], _outputs);
    }

    /// <summary>A to D, A the least significant.</summary>
    public IReadOnlyList<Terminal> Address => _address;

    /// <summary>The outputs, 0 upwards.</summary>
    public IReadOnlyList<Terminal> Outputs => _outputs;

    /// <summary>The active-low enables (the 74154's G1 and G2); empty on the BCD decoders.</summary>
    public IReadOnlyList<Terminal> Enables => _enables;

    /// <summary>True for the TTL parts, whose selected output goes low.</summary>
    public bool IsActiveLow => _activeLow;

    /// <summary>The output selected, or -1 when none is (disabled, or a code past the last output).</summary>
    public int SelectedOutput { get; private set; } = -1;

    public override string ValueLabel => SelectedOutput < 0 ? "none" : $"{(_activeLow ? 'Y' : 'Q')}{SelectedOutput}";

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        var enabled = _enables.All(e => context.ReadInput(e, Levels).IsLow());

        var code = 0;
        for (var i = 0; i < 4; i++)
            if (context.ReadInput(_address[i], Levels).IsHigh()) code |= 1 << i;

        var before = SelectedOutput;
        SelectedOutput = enabled && code < _outputs.Length ? code : -1;

        for (var i = 0; i < _outputs.Length; i++)
            Request(context, i, LogicStateExtensions.FromBool((i == SelectedOutput) ^ _activeLow), delay);

        if (before != SelectedOutput) NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        SelectedOutput = -1;
    }
}

/// <summary>
/// 7442 BCD to decimal decoder, active-low outputs; 10 to 15 leave all ten high. Pinout: 1-7=Y0-Y6,
/// 8=GND, 9-11=Y7-Y9, 12=D, 13=C, 14=B, 15=A, 16=VCC.
/// </summary>
public sealed class Ic7442() : LineDecoderIc(16, [15, 14, 13, 12], [1, 2, 3, 4, 5, 6, 7, 9, 10, 11], [],
    vccPin: 16, gndPin: 8, cmos: false)
{
    public override string PartNumber => "7442";
}

/// <summary>
/// 74154 4-to-16 line decoder, active-low outputs, enabled while G1 and G2 are both low — a 24-pin
/// part. Pinout: 1-11=Y0-Y10, 12=GND, 13-17=Y11-Y15, 18=G1, 19=G2, 20=D, 21=C, 22=B, 23=A, 24=VCC.
/// </summary>
public sealed class Ic74154() : LineDecoderIc(24, [23, 22, 21, 20],
    [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 14, 15, 16, 17], [18, 19], vccPin: 24, gndPin: 12, cmos: false)
{
    public override string PartNumber => "74154";
}

/// <summary>
/// 4028 BCD to decimal decoder, CMOS, active-high outputs: one of ten high, none for 10 to 15. Pinout:
/// 1=Q4, 2=Q2, 3=Q0, 4=Q7, 5=Q9, 6=Q5, 7=Q6, 8=VSS, 9=Q8, 10=A, 11=D, 12=C, 13=B, 14=Q1, 15=Q3, 16=VDD.
/// </summary>
public sealed class Ic4028() : LineDecoderIc(16, [10, 13, 12, 11], [3, 14, 2, 15, 1, 6, 7, 4, 9, 5], [],
    vccPin: 16, gndPin: 8, cmos: true)
{
    public override string PartNumber => "4028";
}
