using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// Base for the 4-bit binary full adders: A1–A4 plus B1–B4 plus a carry in C0 gives Σ1–Σ4 and a
/// carry out C4, so two of them chained C4 to C0 add eight bits.
/// <para>
/// Internally the real parts look ahead rather than ripple — the carry into each bit is worked out
/// from the inputs directly instead of waiting for the bit below — so all five outputs settle in
/// about the same time. That is how they are modelled: one sum, every output scheduled at the same
/// delay. The 7483 and 74283 are the same adder; the 74283 moved the supply pins to the corners
/// where every other 16-pin part has them.
/// </para>
/// </summary>
public abstract class FullAdderIc : DigitalIc
{
    private const int Width = 4;

    private readonly Terminal[] _a = new Terminal[Width];
    private readonly Terminal[] _b = new Terminal[Width];
    private readonly Terminal[] _sum = new Terminal[Width];

    /// <param name="pinout">
    /// Pin numbers in the order A1–A4, B1–B4, Σ1–Σ4, C0, C4, GND, VCC.
    /// </param>
    /// <param name="cmos">The 4008: CMOS levels and speed, VDD and VSS.</param>
    protected FullAdderIc(int[] pinout, bool cmos = false) : base(16)
    {
        PropagationDelay = cmos ? 400e-9 : 16e-9;
        if (cmos) Levels = LogicLevels.Cmos5V;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        for (var i = 0; i < Width; i++)
        {
            _a[i] = Pin(pinout[i], $"A{i + 1}", TerminalType.Input);
            _b[i] = Pin(pinout[Width + i], $"B{i + 1}", TerminalType.Input);
            _sum[i] = Pin(pinout[(2 * Width) + i], $"S{i + 1}", TerminalType.Output);
        }

        CarryIn = Pin(pinout[12], "C0", TerminalType.Input);
        CarryOut = Pin(pinout[13], "C4", TerminalType.Output);
        Gnd = Pin(pinout[14], cmos ? "VSS" : "GND", TerminalType.Ground);
        Vcc = Pin(pinout[15], cmos ? "VDD" : "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];

        ConfigurePins([.. _a, .. _b, CarryIn], [.. _sum, CarryOut]);
    }

    /// <summary>A1 to A4, A1 the least significant.</summary>
    public IReadOnlyList<Terminal> A => _a;

    /// <summary>B1 to B4.</summary>
    public IReadOnlyList<Terminal> B => _b;

    /// <summary>Σ1 to Σ4.</summary>
    public IReadOnlyList<Terminal> Sum => _sum;

    public Terminal CarryIn { get; }

    public Terminal CarryOut { get; }

    /// <summary>The five-bit result last worked out: the four sum bits and the carry above them.</summary>
    public int Result { get; private set; }

    public override string ValueLabel => $"{PartNumber} = {Result}";

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);

        int? Word(IReadOnlyList<Terminal> pins)
        {
            var value = 0;

            for (var i = 0; i < pins.Count; i++)
            {
                var state = context.ReadInput(pins[i], Levels);
                if (!state.IsDriven()) return null;
                if (state.IsHigh()) value |= 1 << i;
            }

            return value;
        }

        var a = Word(_a);
        var b = Word(_b);
        var carry = Word([CarryIn]);

        // An undriven input could be either, so it poisons every output from its own bit upwards —
        // and an adder's carry chain means that, conservatively, is all of them.
        if (a is null || b is null || carry is null)
        {
            for (var i = 0; i <= Width; i++) context.Schedule(this, i, LogicState.Unknown, delay);
            return;
        }

        Result = a.Value + b.Value + carry.Value;

        for (var i = 0; i < Width; i++)
            context.Schedule(this, i, LogicStateExtensions.FromBool((Result & (1 << i)) != 0), delay);

        context.Schedule(this, Width, LogicStateExtensions.FromBool(Result > 15), delay);

        NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        Result = 0;
    }
}

/// <summary>
/// 74283 4-bit binary full adder with fast carry, the pinout everything else uses.
/// <para>
/// Pinout: 1=Σ2, 2=B2, 3=A2, 4=Σ1, 5=A1, 6=B1, 7=C0, 8=GND, 9=C4, 10=Σ4, 11=B4, 12=A4, 13=Σ3, 14=A3,
/// 15=B3, 16=VCC.
/// </para>
/// </summary>
public sealed class Ic74283() : FullAdderIc([5, 3, 14, 12, 6, 2, 15, 11, 4, 1, 13, 10, 7, 9, 8, 16])
{
    public override string PartNumber => "74283";
}

/// <summary>
/// 7483 4-bit binary full adder — the original, with the one pinout in the family that puts VCC on
/// pin 5 and GND on pin 12. Wire it as if it were a 74283 and it is powered backwards.
/// <para>
/// Pinout: 1=A4, 2=Σ3, 3=A3, 4=B3, 5=VCC, 6=Σ2, 7=B2, 8=A2, 9=Σ1, 10=A1, 11=B1, 12=GND, 13=C0, 14=C4,
/// 15=Σ4, 16=B4.
/// </para>
/// </summary>
public sealed class Ic7483() : FullAdderIc([10, 8, 3, 1, 11, 7, 4, 16, 9, 6, 2, 15, 13, 14, 12, 5])
{
    public override string PartNumber => "7483";
}

/// <summary>
/// 4008 4-bit full adder, CMOS — the 74283's job on a pinout of its own, with the carry rippling
/// through four stages rather than looked ahead, so about 400 ns at 5 V for the whole sum.
/// <para>
/// Pinout: 1=A4, 2=B3, 3=A3, 4=B2, 5=A2, 6=B1, 7=A1, 8=VSS, 9=C0 (carry in), 10=S1, 11=S2, 12=S3,
/// 13=S4, 14=C4 (carry out), 15=B4, 16=VDD.
/// </para>
/// </summary>
public sealed class Ic4008() : FullAdderIc([7, 5, 3, 1, 6, 4, 2, 15, 10, 11, 12, 13, 9, 14, 8, 16], cmos: true)
{
    public override string PartNumber => "4008";
}
