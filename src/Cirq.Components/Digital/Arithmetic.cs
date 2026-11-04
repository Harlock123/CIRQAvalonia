using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// 7485 four-bit magnitude comparator: A3-A0 against B3-B0, with A&gt;B, A=B and A&lt;B outputs — and
/// three cascade inputs of the same names, which decide the answer when the four bits here are equal.
/// Chain them least significant first, each one's outputs into the next one's cascade inputs, and the
/// last one compares the whole word. The first in the chain has its A=B input tied high and the other
/// two low.
/// <para>
/// With A and B equal the outputs follow the cascade inputs, including the datasheet's two odd rows:
/// A&gt;B and A&lt;B in both high give all three outputs low, and all three in low give A&gt;B and A&lt;B
/// out both high. A=B in high wins over the other two.
/// </para>
/// <para>
/// Pinout: 1=B3, 2=A&lt;B in, 3=A=B in, 4=A&gt;B in, 5=A&gt;B out, 6=A=B out, 7=A&lt;B out, 8=GND, 9=B0,
/// 10=A0, 11=B1, 12=A1, 13=A2, 14=B2, 15=A3, 16=VCC.
/// </para>
/// </summary>
public sealed class Ic7485 : DigitalIc
{
    private readonly Terminal[] _a = new Terminal[4];
    private readonly Terminal[] _b = new Terminal[4];

    public Ic7485() : base(16)
    {
        PropagationDelay = 23e-9;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        _b[3] = Pin(1, "B3", TerminalType.Input);
        LessIn = Pin(2, "A<Bi", TerminalType.Input);
        EqualIn = Pin(3, "A=Bi", TerminalType.Input);
        GreaterIn = Pin(4, "A>Bi", TerminalType.Input);
        Greater = Pin(5, "A>B", TerminalType.Output);
        Equal = Pin(6, "A=B", TerminalType.Output);
        Less = Pin(7, "A<B", TerminalType.Output);
        Gnd = Pin(8, "GND", TerminalType.Ground);
        _b[0] = Pin(9, "B0", TerminalType.Input);
        _a[0] = Pin(10, "A0", TerminalType.Input);
        _b[1] = Pin(11, "B1", TerminalType.Input);
        _a[1] = Pin(12, "A1", TerminalType.Input);
        _a[2] = Pin(13, "A2", TerminalType.Input);
        _b[2] = Pin(14, "B2", TerminalType.Input);
        _a[3] = Pin(15, "A3", TerminalType.Input);
        Vcc = Pin(16, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([.. _a, .. _b, GreaterIn, EqualIn, LessIn], [Greater, Equal, Less]);
    }

    public override string PartNumber => "7485";

    /// <summary>A0 to A3.</summary>
    public IReadOnlyList<Terminal> A => _a;

    /// <summary>B0 to B3.</summary>
    public IReadOnlyList<Terminal> B => _b;

    public Terminal GreaterIn { get; }
    public Terminal EqualIn { get; }
    public Terminal LessIn { get; }
    public Terminal Greater { get; }
    public Terminal Equal { get; }
    public Terminal Less { get; }

    /// <summary>What it last decided: "A>B", "A=B", "A<B", or the cascade's odd rows.</summary>
    public string Verdict { get; private set; } = "";

    public override string ValueLabel => Verdict;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        bool High(Terminal pin) => context.ReadInput(pin, Levels).IsHigh();

        int Word(Terminal[] pins) =>
            (High(pins[0]) ? 1 : 0) | (High(pins[1]) ? 2 : 0) | (High(pins[2]) ? 4 : 0) | (High(pins[3]) ? 8 : 0);

        var a = Word(_a);
        var b = Word(_b);

        bool greater, equal, less;

        if (a != b)
        {
            (greater, equal, less) = (a > b, false, a < b);
        }
        else if (High(EqualIn))
        {
            (greater, equal, less) = (false, true, false);
        }
        else
        {
            // Equal here, and the cascade decides — the two rows where both are given are the datasheet's.
            var gt = High(GreaterIn);
            var lt = High(LessIn);
            (greater, equal, less) = (gt, lt) switch
            {
                (true, false) => (true, false, false),
                (false, true) => (false, false, true),
                (true, true) => (false, false, false),
                _ => (true, false, true),
            };
        }

        Request(context, 0, LogicStateExtensions.FromBool(greater), delay);
        Request(context, 1, LogicStateExtensions.FromBool(equal), delay);
        Request(context, 2, LogicStateExtensions.FromBool(less), delay);

        var verdict = (greater, equal, less) switch
        {
            (true, false, false) => "A>B",
            (false, true, false) => "A=B",
            (false, false, true) => "A<B",
            _ => $"{(greater ? 1 : 0)}{(equal ? 1 : 0)}{(less ? 1 : 0)}",
        };

        if (verdict != Verdict)
        {
            Verdict = verdict;
            NotifyValueChanged();
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        Verdict = "";
    }
}

/// <summary>
/// 74181 four-bit arithmetic logic unit: sixteen logic functions of A and B with M high, sixteen
/// arithmetic ones with M low, chosen by S3-S0, with a carry in and out and the look-ahead outputs a
/// 74182 takes. The table here is the datasheet's <b>active-high data</b> one: a 1 is a high pin.
/// <para>
/// In that convention the carries are active <b>low</b>: Cn high means no carry in, and Cn+4 goes low
/// when there is a carry out. So A plus B (S = 1001, M low) with Cn high is A + B, and with Cn low it
/// is A + B + 1; A minus B minus 1 (S = 0110) with Cn low is A − B, with Cn+4 low when A ≥ B.
/// </para>
/// <para>
/// Every function is the one adder underneath. Each bit forms two terms from A, B and S — call them
/// X = A + B·S0 + B̄·S1 and Y = A·B̄·S2 + A·B·S3 — and the arithmetic result is X plus Y plus the carry.
/// The logic result is the same with every internal carry forced, which is ¬(X ⊕ Y). That is why the
/// list of arithmetic functions reads so strangely (A plus A·B̄, (A + B̄) plus A·B): they are whatever
/// that sum gives for each S.
/// </para>
/// <para>
/// G and P are the look-ahead outputs, as the silicon has them: in the active-low data convention G
/// is low when the four bits generate a carry and P low when they propagate one. Wired straight to a
/// 74182, in either convention, the carries come out right. A=B is open collector and lets go when all
/// four F outputs are high — which, subtracting with no carry in, is when A equals B.
/// </para>
/// <para>
/// Pinout: 1=B0, 2=A0, 3=S3, 4=S2, 5=S1, 6=S0, 7=Cn, 8=M, 9=F0, 10=F1, 11=F2, 12=GND, 13=F3, 14=A=B,
/// 15=P, 16=Cn+4, 17=G, 18=B3, 19=A3, 20=B2, 21=A2, 22=B1, 23=A1, 24=VCC.
/// </para>
/// </summary>
public sealed class Ic74181 : DigitalIc
{
    private readonly Terminal[] _a = new Terminal[4];
    private readonly Terminal[] _b = new Terminal[4];
    private readonly Terminal[] _s = new Terminal[4];
    private readonly Terminal[] _f = new Terminal[4];

    public Ic74181() : base(24)
    {
        PropagationDelay = 24e-9;

        var pins = new Terminal[25];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 24));

        _b[0] = Pin(1, "B0", TerminalType.Input);
        _a[0] = Pin(2, "A0", TerminalType.Input);
        _s[3] = Pin(3, "S3", TerminalType.Input);
        _s[2] = Pin(4, "S2", TerminalType.Input);
        _s[1] = Pin(5, "S1", TerminalType.Input);
        _s[0] = Pin(6, "S0", TerminalType.Input);
        CarryIn = Pin(7, "Cn", TerminalType.Input);
        Mode = Pin(8, "M", TerminalType.Input);
        _f[0] = Pin(9, "F0", TerminalType.Output);
        _f[1] = Pin(10, "F1", TerminalType.Output);
        _f[2] = Pin(11, "F2", TerminalType.Output);
        Gnd = Pin(12, "GND", TerminalType.Ground);
        _f[3] = Pin(13, "F3", TerminalType.Output);
        Equal = Pin(14, "A=B", TerminalType.Bidirectional);
        Propagate = Pin(15, "P", TerminalType.Output);
        CarryOut = Pin(16, "Cn+4", TerminalType.Output);
        Generate = Pin(17, "G", TerminalType.Output);
        _b[3] = Pin(18, "B3", TerminalType.Input);
        _a[3] = Pin(19, "A3", TerminalType.Input);
        _b[2] = Pin(20, "B2", TerminalType.Input);
        _a[2] = Pin(21, "A2", TerminalType.Input);
        _b[1] = Pin(22, "B1", TerminalType.Input);
        _a[1] = Pin(23, "A1", TerminalType.Input);
        Vcc = Pin(24, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([.. _a, .. _b, .. _s, CarryIn, Mode], [.. _f, CarryOut, Generate, Propagate, Equal]);
    }

    public override string PartNumber => "74181";

    /// <summary>Twenty-four legs with seven kinds of input: the symbol names them.</summary>
    public override bool LabelsPins => true;

    /// <summary>A0 to A3.</summary>
    public IReadOnlyList<Terminal> A => _a;

    /// <summary>B0 to B3.</summary>
    public IReadOnlyList<Terminal> B => _b;

    /// <summary>S0 to S3, the function select.</summary>
    public IReadOnlyList<Terminal> Select => _s;

    /// <summary>F0 to F3.</summary>
    public IReadOnlyList<Terminal> F => _f;

    /// <summary>Carry in, active low in the active-high data convention.</summary>
    public Terminal CarryIn { get; }

    /// <summary>High for the logic functions, low for the arithmetic ones.</summary>
    public Terminal Mode { get; }

    /// <summary>Carry out, Cn+4: active low in the active-high data convention.</summary>
    public Terminal CarryOut { get; }

    /// <summary>The look-ahead generate output, for a 74182.</summary>
    public Terminal Generate { get; }

    /// <summary>The look-ahead propagate output, for a 74182.</summary>
    public Terminal Propagate { get; }

    /// <summary>Open collector: released while F is 1111.</summary>
    public Terminal Equal { get; }

    /// <summary>The four F bits last worked out.</summary>
    public int Result { get; private set; }

    public override string ValueLabel => $"F = {Result}";

    /// <summary>
    /// The datasheet's sum, as a function of everything on the inputs: F, and the carry, G and P pin
    /// levels. Exposed so the tests and the guide can check the table against it.
    /// </summary>
    public static (int F, bool CarryOutPin, bool GeneratePin, bool PropagatePin) Evaluate(
        int a, int b, int select, bool logic, bool carryInPin)
    {
        bool S(int i) => (select & (1 << i)) != 0;

        var x = new bool[4];
        var y = new bool[4];

        for (var i = 0; i < 4; i++)
        {
            var ai = (a & (1 << i)) != 0;
            var bi = (b & (1 << i)) != 0;
            x[i] = ai || (bi && S(0)) || (!bi && S(1));
            y[i] = (ai && !bi && S(2)) || (ai && bi && S(3));
        }

        // The carry chain: Y generates, X propagates (and Y implies X), the carry in active low.
        var carry = !carryInPin;
        var f = 0;

        for (var i = 0; i < 4; i++)
        {
            var bit = x[i] ^ y[i] ^ (logic || carry);
            if (bit) f |= 1 << i;
            carry = y[i] || (x[i] && carry);
        }

        // As the silicon has them, which is the active-low data convention's G and P: G low when the
        // group would carry out even with no carry in there, P low when it would carry one through.
        // In this (active-high) convention that makes G high while a carry in would carry out, and P
        // high while some bit generates.
        var carriesIfGiven = y[3] || (x[3] && (y[2] || (x[2] && (y[1] || (x[1] && x[0])))));
        var someGenerate = y[0] || y[1] || y[2] || y[3];

        return (f, !carry, carriesIfGiven, someGenerate);
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        bool High(Terminal pin) => context.ReadInput(pin, Levels).IsHigh();

        int Word(Terminal[] pins) =>
            (High(pins[0]) ? 1 : 0) | (High(pins[1]) ? 2 : 0) | (High(pins[2]) ? 4 : 0) | (High(pins[3]) ? 8 : 0);

        var (f, carryOut, generate, propagate) =
            Evaluate(Word(_a), Word(_b), Word(_s), High(Mode), High(CarryIn));

        for (var i = 0; i < 4; i++)
            Request(context, i, LogicStateExtensions.FromBool((f & (1 << i)) != 0), delay);

        Request(context, 4, LogicStateExtensions.FromBool(carryOut), delay);
        Request(context, 5, LogicStateExtensions.FromBool(generate), delay);
        Request(context, 6, LogicStateExtensions.FromBool(propagate), delay);
        Request(context, 7, f == 0x0F ? LogicState.HighImpedance : LogicState.Low, delay);

        if (f != Result)
        {
            Result = f;
            NotifyValueChanged();
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        Result = 0;
    }
}

/// <summary>
/// 74182 look-ahead carry generator: four 74181s' G and P in, the carries into the second, third and
/// fourth of them out, and a G and P for the whole sixteen bits — so a 16-bit sum settles in about
/// three gate delays rather than rippling through four ALUs.
/// <para>
/// The datasheet's equations, with G0-G3 and P0-P3 active low on their pins and the carries active
/// high: Cn+x = G0 + P0·Cn, Cn+y = G1 + P1·G0 + P1·P0·Cn, Cn+z = G2 + P2·G1 + P2·P1·G0 + P2·P1·P0·Cn,
/// and G and P out the four-group generate and propagate, active low. They are self-dual, which is
/// what lets the same part serve 74181s in either data convention wired straight to it.
/// </para>
/// <para>
/// Pinout: 1=G1, 2=P1, 3=G0, 4=P0, 5=G3, 6=P3, 7=P, 8=GND, 9=Cn+z, 10=G, 11=Cn+y, 12=Cn+x, 13=Cn,
/// 14=G2, 15=P2, 16=VCC.
/// </para>
/// </summary>
public sealed class Ic74182 : DigitalIc
{
    private readonly Terminal[] _g = new Terminal[4];
    private readonly Terminal[] _p = new Terminal[4];

    public Ic74182() : base(16)
    {
        PropagationDelay = 10e-9;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        _g[1] = Pin(1, "G1", TerminalType.Input);
        _p[1] = Pin(2, "P1", TerminalType.Input);
        _g[0] = Pin(3, "G0", TerminalType.Input);
        _p[0] = Pin(4, "P0", TerminalType.Input);
        _g[3] = Pin(5, "G3", TerminalType.Input);
        _p[3] = Pin(6, "P3", TerminalType.Input);
        PropagateOut = Pin(7, "P", TerminalType.Output);
        Gnd = Pin(8, "GND", TerminalType.Ground);
        CarryZ = Pin(9, "Cn+z", TerminalType.Output);
        GenerateOut = Pin(10, "G", TerminalType.Output);
        CarryY = Pin(11, "Cn+y", TerminalType.Output);
        CarryX = Pin(12, "Cn+x", TerminalType.Output);
        CarryIn = Pin(13, "Cn", TerminalType.Input);
        _g[2] = Pin(14, "G2", TerminalType.Input);
        _p[2] = Pin(15, "P2", TerminalType.Input);
        Vcc = Pin(16, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([.. _g, .. _p, CarryIn], [CarryX, CarryY, CarryZ, GenerateOut, PropagateOut]);
    }

    public override string PartNumber => "74182";

    /// <summary>G0 to G3, active low.</summary>
    public IReadOnlyList<Terminal> GenerateIn => _g;

    /// <summary>P0 to P3, active low.</summary>
    public IReadOnlyList<Terminal> PropagateIn => _p;

    public Terminal CarryIn { get; }
    public Terminal CarryX { get; }
    public Terminal CarryY { get; }
    public Terminal CarryZ { get; }
    public Terminal GenerateOut { get; }
    public Terminal PropagateOut { get; }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        var g = _g.Select(pin => context.ReadInput(pin, Levels).IsLow()).ToArray();
        var p = _p.Select(pin => context.ReadInput(pin, Levels).IsLow()).ToArray();
        var c = context.ReadInput(CarryIn, Levels).IsHigh();

        var x = g[0] || (p[0] && c);
        var y = g[1] || (p[1] && x);
        var z = g[2] || (p[2] && y);
        var generate = g[3] || (p[3] && (g[2] || (p[2] && (g[1] || (p[1] && g[0])))));
        var propagate = p[0] && p[1] && p[2] && p[3];

        Request(context, 0, LogicStateExtensions.FromBool(x), delay);
        Request(context, 1, LogicStateExtensions.FromBool(y), delay);
        Request(context, 2, LogicStateExtensions.FromBool(z), delay);
        Request(context, 3, LogicStateExtensions.FromBool(!generate), delay);
        Request(context, 4, LogicStateExtensions.FromBool(!propagate), delay);
    }
}

/// <summary>
/// 74280 nine-bit odd/even parity generator and checker: ΣEVEN high when an even number of the nine
/// inputs A-I are high, ΣODD when an odd number are. Eight data bits and a received parity bit in,
/// and ΣEVEN or ΣODD says whether the word arrived whole.
/// <para>
/// Pinout: 1=G, 2=H, 3=NC, 4=I, 5=ΣEVEN, 6=ΣODD, 7=GND, 8=A, 9=B, 10=C, 11=D, 12=E, 13=F, 14=VCC.
/// </para>
/// </summary>
public sealed class Ic74280 : DigitalIc
{
    private readonly Terminal[] _inputs = new Terminal[9];

    public Ic74280() : base(14)
    {
        PropagationDelay = 33e-9;

        var pins = new Terminal[15];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 14));

        _inputs[6] = Pin(1, "G", TerminalType.Input);
        _inputs[7] = Pin(2, "H", TerminalType.Input);
        Pin(3, "NC", TerminalType.Passive);
        _inputs[8] = Pin(4, "I", TerminalType.Input);
        Even = Pin(5, "EVEN", TerminalType.Output);
        Odd = Pin(6, "ODD", TerminalType.Output);
        Gnd = Pin(7, "GND", TerminalType.Ground);
        for (var i = 0; i < 6; i++) _inputs[i] = Pin(8 + i, ((char)('A' + i)).ToString(), TerminalType.Input);
        Vcc = Pin(14, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins(_inputs, [Even, Odd]);
    }

    public override string PartNumber => "74280";

    /// <summary>A to I.</summary>
    public IReadOnlyList<Terminal> Inputs => _inputs;

    public Terminal Even { get; }

    public Terminal Odd { get; }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        var ones = _inputs.Count(pin => context.ReadInput(pin, Levels).IsHigh());

        Request(context, 0, LogicStateExtensions.FromBool(ones % 2 == 0), delay);
        Request(context, 1, LogicStateExtensions.FromBool(ones % 2 == 1), delay);
    }
}
