using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// The quad two-input pinout shared by the 7400, 7403, 7408, 7432 and 7486: gate inputs on the pin
/// pair before each output, Vcc on 14 and GND on 7.
/// </summary>
public abstract class QuadTwoInputGateIc : MultiGateIc
{
    /// <summary>Pin assignments per gate: (A, B, Y).</summary>
    private static readonly GateDefinition[] StandardPinout =
    [
        new([1, 2], 3),
        new([4, 5], 6),
        new([10, 9], 8),
        new([13, 12], 11),
    ];

    protected QuadTwoInputGateIc(GateFunction function, bool openCollector = false)
        : base(14, function, vccPin: 14, gndPin: 7, StandardPinout, openCollector: openCollector)
    {
    }

    /// <summary>The A, B and Y pins of one of the four gates, indexed from 0.</summary>
    public (Terminal A, Terminal B, Terminal Y) Gate(int index) =>
        (GateInputs(index)[0], GateInputs(index)[1], GateOutput(index));
}

/// <summary>Quad 2-input NAND gate.</summary>
public sealed class Ic7400 : QuadTwoInputGateIc
{
    public Ic7400() : base(GateFunction.Nand) => PropagationDelay = 11e-9;

    public override string PartNumber => "7400";
}

/// <summary>
/// Quad 2-input NAND gate with open-collector outputs: the 7400's pinout and function, but an output
/// that would be high is released instead. Give each output a pull-up — or tie several outputs to
/// one pull-up and the wire is low whenever any gate's inputs are both high, a wired-AND of the
/// NANDs with no gate to build it from.
/// </summary>
public sealed class Ic7403 : QuadTwoInputGateIc
{
    public Ic7403() : base(GateFunction.Nand, openCollector: true) => PropagationDelay = 22e-9;

    public override string PartNumber => "7403";
}

/// <summary>Quad 2-input AND gate.</summary>
public sealed class Ic7408 : QuadTwoInputGateIc
{
    public Ic7408() : base(GateFunction.And) => PropagationDelay = 12e-9;

    public override string PartNumber => "7408";
}

/// <summary>Quad 2-input OR gate.</summary>
public sealed class Ic7432 : QuadTwoInputGateIc
{
    public Ic7432() : base(GateFunction.Or) => PropagationDelay = 12e-9;

    public override string PartNumber => "7432";
}

/// <summary>Quad 2-input exclusive-OR gate.</summary>
public sealed class Ic7486 : QuadTwoInputGateIc
{
    public Ic7486() : base(GateFunction.Xor) => PropagationDelay = 14e-9;

    public override string PartNumber => "7486";
}

/// <summary>
/// Quad 2-input NOR gate. Unlike the rest of the quad family the outputs come first on each gate,
/// so pin 1 is an output rather than an input.
/// </summary>
public sealed class Ic7402 : MultiGateIc
{
    private static readonly GateDefinition[] Pinout =
    [
        new([2, 3], 1),
        new([5, 6], 4),
        new([8, 9], 10),
        new([11, 12], 13),
    ];

    public Ic7402() : base(14, GateFunction.Nor, vccPin: 14, gndPin: 7, Pinout) =>
        PropagationDelay = 11e-9;

    public override string PartNumber => "7402";

    public (Terminal A, Terminal B, Terminal Y) Gate(int index) =>
        (GateInputs(index)[0], GateInputs(index)[1], GateOutput(index));
}

/// <summary>
/// The hex pinout of the 7404 and its open-collector relatives the 7406 and 7407: six one-input
/// gates, inputs 1, 3, 5, 9, 11, 13 and outputs on the pin after each, Vcc on 14 and GND on 7.
/// </summary>
public abstract class HexGateIc : MultiGateIc
{
    private static readonly GateDefinition[] Pinout =
    [
        new([1], 2),
        new([3], 4),
        new([5], 6),
        new([13], 12),
        new([11], 10),
        new([9], 8),
    ];

    protected HexGateIc(GateFunction function, bool openCollector = false)
        : base(14, function, vccPin: 14, gndPin: 7, Pinout, openCollector: openCollector)
    {
    }

    /// <summary>The input and output pins of one of the six gates, indexed from 0.</summary>
    public (Terminal A, Terminal Y) Inverter(int index) => (GateInputs(index)[0], GateOutput(index));
}

/// <summary>Hex inverter. Six independent NOT gates, Vcc on 14 and GND on 7.</summary>
public sealed class Ic7404 : HexGateIc
{
    public Ic7404() : base(GateFunction.Not) => PropagationDelay = 10e-9;

    public override string PartNumber => "7404";
}

/// <summary>
/// Hex inverter with high-voltage open-collector outputs: the 7404's pinout, each output pulling down
/// or letting go. The output transistors stand off 30 V, so the pull-up can go to a relay coil's or a
/// lamp's own supply rather than to 5 V — that, and the 40 mA they sink, is what the part is for.
/// Nothing here limits the voltage on a released output; keeping under the 30 V is yours.
/// </summary>
public sealed class Ic7406 : HexGateIc
{
    public Ic7406() : base(GateFunction.Not, openCollector: true) => PropagationDelay = 15e-9;

    public override string PartNumber => "7406";
}

/// <summary>
/// Hex buffer with high-voltage open-collector outputs: the 7406 without the inversion. A high in
/// lets go of the output (pulled up to anything up to 30 V), a low in pulls it down — a level
/// shifter from 5 V logic to a higher rail, six times over.
/// </summary>
public sealed class Ic7407 : HexGateIc
{
    public Ic7407() : base(GateFunction.Buffer, openCollector: true) => PropagationDelay = 14e-9;

    public override string PartNumber => "7407";
}

/// <summary>
/// The triple three-input pinout of the 7410, 7411 and 7427: gate one on 1, 2 and 13 driving 12,
/// gate two on 3, 4 and 5 driving 6, gate three on 9, 10 and 11 driving 8.
/// </summary>
public abstract class TripleThreeInputGateIc : MultiGateIc
{
    private static readonly GateDefinition[] Pinout =
    [
        new([1, 2, 13], 12),
        new([3, 4, 5], 6),
        new([9, 10, 11], 8),
    ];

    protected TripleThreeInputGateIc(GateFunction function)
        : base(14, function, vccPin: 14, gndPin: 7, Pinout)
    {
    }

    public (Terminal A, Terminal B, Terminal C, Terminal Y) Gate(int index) =>
        (GateInputs(index)[0], GateInputs(index)[1], GateInputs(index)[2], GateOutput(index));
}

/// <summary>Triple 3-input NAND gate.</summary>
public sealed class Ic7410 : TripleThreeInputGateIc
{
    public Ic7410() : base(GateFunction.Nand) => PropagationDelay = 11e-9;

    public override string PartNumber => "7410";
}

/// <summary>Triple 3-input AND gate, on the 7410's pinout.</summary>
public sealed class Ic7411 : TripleThreeInputGateIc
{
    public Ic7411() : base(GateFunction.And) => PropagationDelay = 12e-9;

    public override string PartNumber => "7411";
}

/// <summary>
/// Triple 3-input NOR gate, on the 7410's pinout — unlike the two-input NOR, the 7402, which moves its
/// outputs to the other end of each gate.
/// </summary>
public sealed class Ic7427 : TripleThreeInputGateIc
{
    public Ic7427() : base(GateFunction.Nor) => PropagationDelay = 10e-9;

    public override string PartNumber => "7427";
}

/// <summary>Dual 4-input NAND gate. Pins 3 and 11 are not connected on this part.</summary>
public sealed class Ic7420 : MultiGateIc
{
    private static readonly GateDefinition[] Pinout =
    [
        new([1, 2, 4, 5], 6),
        new([9, 10, 12, 13], 8),
    ];

    public Ic7420()
        : base(14, GateFunction.Nand, vccPin: 14, gndPin: 7, Pinout, unconnectedPins: [3, 11]) =>
        PropagationDelay = 12e-9;

    public override string PartNumber => "7420";

    public (Terminal A, Terminal B, Terminal C, Terminal D, Terminal Y) Gate(int index) =>
        (GateInputs(index)[0], GateInputs(index)[1], GateInputs(index)[2], GateInputs(index)[3],
            GateOutput(index));
}

/// <summary>
/// 8-input NAND gate: one gate filling the package. Inputs on 1-6, 11 and 12, the output on 8, and
/// 9, 10 and 13 not connected. The output is low only when all eight are high, which makes it the
/// part for spotting one value on a byte — feed it the bits that should be 1 straight and the ones
/// that should be 0 through inverters.
/// </summary>
public sealed class Ic7430 : MultiGateIc
{
    private static readonly GateDefinition[] Pinout = [new([1, 2, 3, 4, 5, 6, 11, 12], 8)];

    public Ic7430()
        : base(14, GateFunction.Nand, vccPin: 14, gndPin: 7, Pinout, unconnectedPins: [9, 10, 13]) =>
        PropagationDelay = 12e-9;

    public override string PartNumber => "7430";

    /// <summary>The eight inputs, A to H.</summary>
    public IReadOnlyList<Terminal> Inputs => GateInputs(0);

    public Terminal Output => GateOutput(0);
}

/// <summary>
/// Quad 2-input NAND Schmitt trigger: the 7400's pinout and function with hysteresis on every input —
/// the TTL cousin of the 4093. The thresholds are TTL ones, about 1.7 V rising and 0.9 V falling on a
/// 5 V rail, rather than the CMOS part's fractions of the supply.
/// </summary>
public sealed class Ic74132 : SchmittGateIc
{
    private static readonly GateDefinition[] Pinout =
    [
        new([1, 2], 3),
        new([4, 5], 6),
        new([10, 9], 8),
        new([13, 12], 11),
    ];

    public Ic74132() : base(14, GateFunction.Nand, vccPin: 14, gndPin: 7, Pinout)
    {
        PropagationDelay = 15e-9;
        Levels = LogicLevels.Ttl;
        UpperThresholdFraction = 0.34;
        LowerThresholdFraction = 0.18;
    }

    public override string PartNumber => "74132";

    /// <summary>The A, B and Y pins of one of the four gates, indexed from 0.</summary>
    public (Terminal A, Terminal B, Terminal Y) Gate(int index) =>
        (GateInputs(index)[0], GateInputs(index)[1], GateOutput(index));
}

/// <summary>
/// Base for the 74125 and 74126 quad tri-state buffers: four independent buffers, each with its own
/// enable, on the same pinout — the 125 enabled by a low, the 126 by a high. Released, an output is
/// nothing at all, which is what lets four sources take turns on one wire; enabled two at once and
/// disagreeing, they fight, and the outputs are bus pins so that fight is left to the running circuit
/// rather than the rule check.
/// <para>
/// Pinout: 1=1OE, 2=1A, 3=1Y, 4=2OE, 5=2A, 6=2Y, 7=GND, 8=3Y, 9=3A, 10=3OE, 11=4Y, 12=4A, 13=4OE,
/// 14=VCC.
/// </para>
/// </summary>
public abstract class QuadTriStateBufferIc : DigitalIc
{
    private readonly Terminal[] _a = new Terminal[4];
    private readonly Terminal[] _enable = new Terminal[4];
    private readonly Terminal[] _y = new Terminal[4];

    protected QuadTriStateBufferIc(bool enableActiveLow) : base(14)
    {
        EnableActiveLow = enableActiveLow;
        PropagationDelay = 12e-9;

        var pins = new Terminal[15];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 14));

        // (OE, A, Y) per buffer.
        int[][] pinout = [[1, 2, 3], [4, 5, 6], [10, 9, 8], [13, 12, 11]];

        for (var i = 0; i < 4; i++)
        {
            _enable[i] = Pin(pinout[i][0], $"{i + 1}OE", TerminalType.Input);
            _a[i] = Pin(pinout[i][1], $"{i + 1}A", TerminalType.Input);
            _y[i] = Pin(pinout[i][2], $"{i + 1}Y", TerminalType.Bidirectional);
        }

        Gnd = Pin(7, "GND", TerminalType.Ground);
        Vcc = Pin(14, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([.. _enable, .. _a], _y);
    }

    /// <summary>True for the 74125, whose buffers are enabled by a low.</summary>
    public bool EnableActiveLow { get; }

    /// <summary>The enable, input and output of one buffer, indexed from 0.</summary>
    public (Terminal Enable, Terminal A, Terminal Y) Buffer(int index) => (_enable[index], _a[index], _y[index]);

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);

        for (var i = 0; i < 4; i++)
        {
            var enable = context.ReadInput(_enable[i], Levels);
            var on = EnableActiveLow ? enable.IsLow() : enable.IsHigh();
            var a = context.ReadInput(_a[i], Levels);

            Request(context, i,
                !enable.IsDriven() ? LogicState.Unknown
                : !on ? LogicState.HighImpedance
                : a.IsDriven() ? a : LogicState.Unknown,
                delay);
        }
    }
}

/// <summary>Quad tri-state buffer, each enabled by a low on its OE pin.</summary>
public sealed class Ic74125() : QuadTriStateBufferIc(enableActiveLow: true)
{
    public override string PartNumber => "74125";
}

/// <summary>Quad tri-state buffer, each enabled by a high on its OE pin — the 74125 with the enables the other way up.</summary>
public sealed class Ic74126() : QuadTriStateBufferIc(enableActiveLow: false)
{
    public override string PartNumber => "74126";
}
