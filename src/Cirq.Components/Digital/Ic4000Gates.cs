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

/// <summary>
/// The dual four-input pinout of the 4002 and 4012: gate one's output on pin 1 and its inputs on
/// 2-5, gate two mirrored on the other side with its output on 13, and pins 6 and 8 not connected.
/// </summary>
public abstract class DualFourInputCmosGateIc : MultiGateIc
{
    private static readonly GateDefinition[] Pinout =
    [
        new([2, 3, 4, 5], 1),
        new([9, 10, 11, 12], 13),
    ];

    protected DualFourInputCmosGateIc(GateFunction function)
        : base(14, function, vccPin: 14, gndPin: 7, Pinout, unconnectedPins: [6, 8])
    {
        Levels = LogicLevels.Cmos5V;
        PropagationDelay = 120e-9;
    }

    public (Terminal A, Terminal B, Terminal C, Terminal D, Terminal Y) Gate(int index) =>
        (GateInputs(index)[0], GateInputs(index)[1], GateInputs(index)[2], GateInputs(index)[3],
            GateOutput(index));
}

/// <summary>Dual 4-input NOR gate.</summary>
public sealed class Ic4002 : DualFourInputCmosGateIc
{
    public Ic4002() : base(GateFunction.Nor)
    {
    }

    public override string PartNumber => "4002";
}

/// <summary>Dual 4-input NAND gate — the CMOS counterpart of the 7420, on a different pinout.</summary>
public sealed class Ic4012 : DualFourInputCmosGateIc
{
    public Ic4012() : base(GateFunction.Nand)
    {
    }

    public override string PartNumber => "4012";
}

/// <summary>
/// The triple three-input pinout of the 4023 and 4025, which is nothing like the 7410's: gate one
/// takes 1, 2 and 8 into 9, gate two 3, 4 and 5 into 6, gate three 11, 12 and 13 into 10.
/// </summary>
public abstract class TripleThreeInputCmosGateIc : MultiGateIc
{
    private static readonly GateDefinition[] Pinout =
    [
        new([1, 2, 8], 9),
        new([3, 4, 5], 6),
        new([11, 12, 13], 10),
    ];

    protected TripleThreeInputCmosGateIc(GateFunction function)
        : base(14, function, vccPin: 14, gndPin: 7, Pinout)
    {
        Levels = LogicLevels.Cmos5V;
        PropagationDelay = 110e-9;
    }

    public (Terminal A, Terminal B, Terminal C, Terminal Y) Gate(int index) =>
        (GateInputs(index)[0], GateInputs(index)[1], GateInputs(index)[2], GateOutput(index));
}

/// <summary>Triple 3-input NAND gate.</summary>
public sealed class Ic4023 : TripleThreeInputCmosGateIc
{
    public Ic4023() : base(GateFunction.Nand)
    {
    }

    public override string PartNumber => "4023";
}

/// <summary>Triple 3-input NOR gate.</summary>
public sealed class Ic4025 : TripleThreeInputCmosGateIc
{
    public Ic4025() : base(GateFunction.Nor)
    {
    }

    public override string PartNumber => "4025";
}

/// <summary>
/// Base for the 4049 and 4050 hex buffers, and their odd package: VDD on pin <b>1</b> rather than 16,
/// VSS on 8, and pins 13 and 16 not connected. Wire one as if it were any other 16-pin CMOS part, with
/// the supply on 16, and it is not powered at all.
/// <para>
/// They exist to change levels. Their inputs have no protection diode to VDD, so they take inputs well
/// above their own supply — a 15 V signal into a part on 5 V comes out as 5 V logic — and the outputs
/// sink several milliamps, enough for two TTL loads. In this simulator no logic input is clamped to its
/// rail, so the inputs here simply read as high; it is the other CMOS parts that are generous, since a
/// real one would be pushing current through its protection diode.
/// </para>
/// <para>
/// Pinout: 1=VDD, 2=1Y, 3=1A, 4=2Y, 5=2A, 6=3Y, 7=3A, 8=VSS, 9=4A, 10=4Y, 11=5A, 12=5Y, 13=NC,
/// 14=6A, 15=6Y, 16=NC.
/// </para>
/// </summary>
public abstract class HexCmosBufferIc : MultiGateIc
{
    private static readonly GateDefinition[] Pinout =
    [
        new([3], 2),
        new([5], 4),
        new([7], 6),
        new([9], 10),
        new([11], 12),
        new([14], 15),
    ];

    protected HexCmosBufferIc(GateFunction function)
        : base(16, function, vccPin: 1, gndPin: 8, Pinout, unconnectedPins: [13, 16])
    {
        Levels = LogicLevels.Cmos5V;
        PropagationDelay = 60e-9;
    }

    /// <summary>The output stage is a big one: about 10 Ω rather than the family's 50, for the current it sinks.</summary>
    protected override double OutputImpedance => 10.0;

    /// <summary>The input and output pins of one of the six buffers, indexed from 0.</summary>
    public (Terminal A, Terminal Y) Inverter(int index) => (GateInputs(index)[0], GateOutput(index));
}

/// <summary>Hex inverting buffer / level converter. VDD is pin 1.</summary>
public sealed class Ic4049 : HexCmosBufferIc
{
    public Ic4049() : base(GateFunction.Not)
    {
    }

    public override string PartNumber => "4049";
}

/// <summary>Hex non-inverting buffer / level converter, the 4049 without the inversion. VDD is pin 1.</summary>
public sealed class Ic4050 : HexCmosBufferIc
{
    public Ic4050() : base(GateFunction.Buffer)
    {
    }

    public override string PartNumber => "4050";
}
