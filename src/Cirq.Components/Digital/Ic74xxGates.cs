using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// The quad two-input pinout shared by the 7400, 7408, 7432 and 7486: gate inputs on the pin pair
/// before each output, Vcc on 14 and GND on 7.
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

    protected QuadTwoInputGateIc(GateFunction function)
        : base(14, function, vccPin: 14, gndPin: 7, StandardPinout)
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

/// <summary>Hex inverter. Six independent NOT gates, Vcc on 14 and GND on 7.</summary>
public sealed class Ic7404 : MultiGateIc
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

    public Ic7404() : base(14, GateFunction.Not, vccPin: 14, gndPin: 7, Pinout) =>
        PropagationDelay = 10e-9;

    public override string PartNumber => "7404";

    /// <summary>The input and output pins of one of the six inverters, indexed from 0.</summary>
    public (Terminal A, Terminal Y) Inverter(int index) => (GateInputs(index)[0], GateOutput(index));
}

/// <summary>Triple 3-input NAND gate.</summary>
public sealed class Ic7410 : MultiGateIc
{
    private static readonly GateDefinition[] Pinout =
    [
        new([1, 2, 13], 12),
        new([3, 4, 5], 6),
        new([9, 10, 11], 8),
    ];

    public Ic7410() : base(14, GateFunction.Nand, vccPin: 14, gndPin: 7, Pinout) =>
        PropagationDelay = 11e-9;

    public override string PartNumber => "7410";

    public (Terminal A, Terminal B, Terminal C, Terminal Y) Gate(int index) =>
        (GateInputs(index)[0], GateInputs(index)[1], GateInputs(index)[2], GateOutput(index));
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
