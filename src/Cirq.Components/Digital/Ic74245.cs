using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// 74HC245 octal bus transceiver: eight bidirectional buffers with one direction pin and one
/// output enable.
/// <para>
/// This is the part that makes a <b>shared bus</b> possible, and it is the only kind of output in
/// this library that can genuinely let go of a wire. An open-collector pin — a 7447 segment, an
/// LM339, a PCF8574 port — pulls down or releases, and a pull-up decides what released means. A
/// tri-state output is different: released is <i>nothing at all</i>, and what the wire does next
/// is somebody else's business entirely. That is how eight devices share eight wires and take
/// turns.
/// </para>
/// <para>
/// It also makes the matching mistake possible, which is why it is worth having. Enable two of
/// these onto the same bus pointing opposite ways and they fight: one holds a wire high through a
/// few tens of ohms while the other holds it low through a few tens of ohms, the pair of them
/// somewhere in the middle reading as neither, and both getting hot. Nothing warns you, the logic
/// downstream reads rubbish, and on real hardware the chips eventually fail. Here it is visible —
/// put a probe on a bus wire and watch it sit at half a supply.
/// </para>
/// <para>
/// Pinout: 1=DIR, 2-9=A1-A8, 10=GND, 11-18=B8-B1, 19=/OE, 20=VCC. With <c>DIR</c> high the A side
/// is read and the B side driven; low is the other way. <c>/OE</c> is active low, so a floating
/// enable pin leaves the part switched on and driving — tie it somewhere.
/// </para>
/// </summary>
public sealed class Ic74245 : DigitalIc
{
    private const int Width = 8;

    private readonly Terminal[] _a = new Terminal[Width];
    private readonly Terminal[] _b = new Terminal[Width];

    public Ic74245() : base(20)
    {
        PropagationDelay = 8e-9;

        var pins = new Terminal[21];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 20));

        Direction = Pin(1, "DIR", TerminalType.Input);

        for (var i = 0; i < Width; i++)
        {
            _a[i] = Pin(2 + i, $"A{i + 1}", TerminalType.Bidirectional);
            _b[i] = Pin(18 - i, $"B{i + 1}", TerminalType.Bidirectional);
        }

        Gnd = Pin(10, "GND", TerminalType.Ground);
        OutputEnable = Pin(19, "OE", TerminalType.Input);
        Vcc = Pin(20, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];

        // Every data pin is in both lists, which is what bidirectional means here: whichever side
        // is not being driven is released, and reading it then gives whatever the bus is doing.
        ConfigurePins([Direction, OutputEnable, .. _a, .. _b], [.. _a, .. _b]);
    }

    /// <summary>High sends A to B; low sends B to A.</summary>
    public Terminal Direction { get; }

    /// <summary>Active low. High releases all sixteen data pins.</summary>
    public Terminal OutputEnable { get; }

    /// <summary>The A-side pins, A1 through A8.</summary>
    public IReadOnlyList<Terminal> A => _a;

    /// <summary>The B-side pins, B1 through B8.</summary>
    public IReadOnlyList<Terminal> B => _b;

    public override string PartNumber => "74HC245";

    public override string ValueLabel => !IsEnabled
        ? "released"
        : IsAToB ? "A → B" : "B → A";

    /// <summary>True while the part is driving one side, rather than letting go of both.</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>Which way it is pointing, when it is enabled at all.</summary>
    public bool IsAToB { get; private set; }

    /// <summary>The byte crossing the part, as the driven side is being held.</summary>
    public int Value { get; private set; }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);

        // Active low, and a floating pin reads low on this family — so an unwired enable leaves
        // the part driving, which is the behaviour rather than a convenience.
        IsEnabled = !context.ReadInput(OutputEnable, Levels).IsHigh();
        IsAToB = context.ReadInput(Direction, Levels).IsHigh();

        if (!IsEnabled)
        {
            for (var i = 0; i < Width * 2; i++)
                context.Schedule(this, i, LogicState.HighImpedance, delay);

            Value = 0;
            return;
        }

        var source = IsAToB ? _a : _b;
        var value = 0;

        for (var i = 0; i < Width; i++)
        {
            var bit = context.ReadInput(source[i], Levels).IsHigh();
            if (bit) value |= 1 << i;

            // Index i is the A pin and i + Width is the B pin, matching how they were configured.
            var driven = IsAToB ? i + Width : i;
            var released = IsAToB ? i : i + Width;

            context.Schedule(this, driven, bit ? LogicState.High : LogicState.Low, delay);
            context.Schedule(this, released, LogicState.HighImpedance, delay);
        }

        Value = value;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        IsEnabled = false;
        IsAToB = false;
        Value = 0;
    }
}
