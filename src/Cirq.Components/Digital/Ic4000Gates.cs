using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// The quad two-input pinout shared by the 4001, 4011, 4070, 4071 and 4081.
/// <para>
/// It is <b>not</b> the 74xx quad pinout, and that is the trap. A 7400 puts gate two on pins 4, 5
/// and 6 and gate three on 9, 10 and 8; a 4011 puts gate two on 5, 6 and 4 and gate three on 8, 9
/// and 10. Only gates one and four land in the same places. Dropping a 4011 into a board laid out
/// for a 7400 gets you two working gates and two that do nothing sensible, which is a long
/// afternoon if you are not expecting it.
/// </para>
/// <para>
/// The CMOS family is at least consistent with itself: unlike TTL, where the 7402 moves its
/// outputs to pins 1, 4, 10 and 13, the CMOS NOR has exactly the same pinout as the CMOS NAND.
/// </para>
/// </summary>
public abstract class QuadTwoInputCmosGateIc : MultiGateIc
{
    /// <summary>Pin assignments per gate: (A, B, Y).</summary>
    private static readonly GateDefinition[] StandardPinout =
    [
        new([1, 2], 3),
        new([5, 6], 4),
        new([8, 9], 10),
        new([12, 13], 11),
    ];

    protected QuadTwoInputCmosGateIc(GateFunction function)
        : base(14, function, vccPin: 14, gndPin: 7, StandardPinout)
    {
        Levels = LogicLevels.Cmos5V;

        // CMOS at 5 V is an order of magnitude slower than the TTL parts it sits beside. At 15 V
        // it would be about three times quicker; the delay is not modelled as supply-dependent.
        PropagationDelay = 90e-9;
    }

    /// <summary>The A, B and Y pins of one of the four gates, indexed from 0.</summary>
    public (Terminal A, Terminal B, Terminal Y) Gate(int index) =>
        (GateInputs(index)[0], GateInputs(index)[1], GateOutput(index));
}

/// <summary>Quad 2-input NOR gate — the CMOS counterpart of the 7402.</summary>
public sealed class Ic4001 : QuadTwoInputCmosGateIc
{
    public Ic4001() : base(GateFunction.Nor)
    {
    }

    public override string PartNumber => "4001";
}

/// <summary>Quad 2-input NAND gate — the CMOS counterpart of the 7400, and the workhorse of the family.</summary>
public sealed class Ic4011 : QuadTwoInputCmosGateIc
{
    public Ic4011() : base(GateFunction.Nand)
    {
    }

    public override string PartNumber => "4011";
}

/// <summary>Quad 2-input exclusive-OR gate.</summary>
public sealed class Ic4070 : QuadTwoInputCmosGateIc
{
    public Ic4070() : base(GateFunction.Xor)
    {
    }

    public override string PartNumber => "4070";
}

/// <summary>Quad 2-input OR gate.</summary>
public sealed class Ic4071 : QuadTwoInputCmosGateIc
{
    public Ic4071() : base(GateFunction.Or)
    {
    }

    public override string PartNumber => "4071";
}

/// <summary>Quad 2-input AND gate.</summary>
public sealed class Ic4081 : QuadTwoInputCmosGateIc
{
    public Ic4081() : base(GateFunction.And)
    {
    }

    public override string PartNumber => "4081";
}

/// <summary>
/// A 4069 hex inverter. Six independent NOT gates on the 7404's pinout — one of the few places
/// where a CMOS part and its TTL equivalent do agree.
/// </summary>
public sealed class Ic4069 : MultiGateIc
{
    private static readonly GateDefinition[] Pinout =
    [
        new([1], 2),
        new([3], 4),
        new([5], 6),
        new([9], 8),
        new([11], 10),
        new([13], 12),
    ];

    public Ic4069() : base(14, GateFunction.Not, vccPin: 14, gndPin: 7, Pinout)
    {
        Levels = LogicLevels.Cmos5V;
        PropagationDelay = 60e-9;
    }

    public override string PartNumber => "4069";

    /// <summary>The input and output pins of one of the six inverters, indexed from 0.</summary>
    public (Terminal A, Terminal Y) Inverter(int index) => (GateInputs(index)[0], GateOutput(index));
}

/// <summary>
/// A 4093 quad 2-input NAND Schmitt trigger: the 4011's pinout and function, with hysteresis on
/// every input.
/// <para>
/// This is the part that makes an oscillator out of almost nothing. Tie the two inputs of one gate
/// together, put a resistor from its output back to that input and a capacitor from the input to
/// ground, and it runs: the capacitor charges until it crosses the upper threshold, the output
/// flips and it discharges until it crosses the lower one. Four gates, four oscillators, or one
/// oscillator and three gates to debounce the switches feeding it.
/// </para>
/// </summary>
public sealed class Ic4093 : SchmittGateIc
{
    private static readonly GateDefinition[] Pinout =
    [
        new([1, 2], 3),
        new([5, 6], 4),
        new([8, 9], 10),
        new([12, 13], 11),
    ];

    public Ic4093()
        : base(14, GateFunction.Nand, vccPin: 14, gndPin: 7, Pinout) =>
        PropagationDelay = 120e-9;

    public override string PartNumber => "4093";

    /// <summary>The A, B and Y pins of one of the four gates, indexed from 0.</summary>
    public (Terminal A, Terminal B, Terminal Y) Gate(int index) =>
        (GateInputs(index)[0], GateInputs(index)[1], GateOutput(index));
}
