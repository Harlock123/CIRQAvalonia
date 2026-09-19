using Cirq.Core.Digital;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

/// <summary>
/// A ULN2003 seven-channel Darlington sink driver.
/// <para>
/// The part that sits between logic and anything that needs real current. A 74xx output will not
/// drive a relay coil, a stepper winding or a filament, and wiring one to a transistor per channel
/// is seven transistors and fourteen resistors. This is all of that in one package.
/// </para>
/// <para>
/// Every channel <b>sinks</b>, which is the thing to get right: the load goes between the supply
/// and the output pin, not between the output and ground. An output that is off is not driving
/// low, it is disconnected, so nothing happens until the other end of the load is at a voltage.
/// Wiring a load from an output down to ground is the usual first mistake and does nothing at all.
/// </para>
/// <para>
/// Being a Darlington it does not saturate to nothing: about a volt is dropped across a conducting
/// output, which matters when the load is a 5 V relay running off a 5 V rail. The COM pin carries
/// the internal flyback diodes — tie it to the load's supply and inductive kick has somewhere to
/// go, which is the other half of why this part exists.
/// </para>
/// <para>
/// Pinout: 1-7 = inputs 1B to 7B, 8 = GND, 9 = COM, 10-16 = outputs 7C down to 1C.
/// </para>
/// </summary>
public sealed partial class Uln2003 : DigitalIc
{
    private const int Channels = 7;

    private readonly Terminal[] _inputs = new Terminal[Channels];
    private readonly Terminal[] _outputs = new Terminal[Channels];

    public Uln2003() : base(16)
    {
        PropagationDelay = 1e-6;        // a Darlington is slow; microseconds, not nanoseconds


        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        for (var i = 0; i < Channels; i++)
        {
            _inputs[i] = Pin(i + 1, $"{i + 1}B", TerminalType.Input);
            _outputs[i] = Pin(16 - i, $"{i + 1}C", TerminalType.Output);
        }

        Gnd = Pin(8, "GND", TerminalType.Ground);
        Common = Pin(9, "COM", TerminalType.Passive);

        // There is genuinely no Vcc pin on a ULN2003: the part is powered by whatever it is
        // sinking from. Pointing Vcc at the ground pin and asking for no minimum supply is how
        // that is said here — the package is never "unpowered", it just has nothing to sink.
        Vcc = Gnd;
        MinimumSupplyVoltage = 0;

        Terminals = [.. pins.Skip(1)];
        ConfigurePins(_inputs, _outputs);
    }

    /// <summary>The seven logic inputs, 1B through 7B.</summary>
    public IReadOnlyList<Terminal> Inputs => _inputs;

    /// <summary>The seven sinking outputs, 1C through 7C.</summary>
    public IReadOnlyList<Terminal> Outputs => _outputs;

    /// <summary>Pin 9: the common cathode of the internal flyback diodes.</summary>
    public Terminal Common { get; }

    /// <summary>Volts dropped across a conducting Darlington output.</summary>
    [ObservableProperty]
    public partial double SaturationVoltage { get; set; } = 0.9;

    /// <summary>
    /// Slope resistance of a conducting output, in ohms. A logic family's figure is tens of ohms,
    /// which would drop three volts at the tenth of an amp this part exists to switch.
    /// </summary>
    [ObservableProperty]
    public partial double DarlingtonResistance { get; set; } = 2.0;

    public override string PartNumber => "ULN2003";

    protected override double OutputImpedance => Math.Max(DarlingtonResistance, 1e-3);

    /// <summary>A conducting output holds about a volt above its ground pin, not zero.</summary>
    protected override double OutputVoltage(LogicState state) =>
        state == LogicState.Low ? SaturationVoltage : Levels.VoltageFor(state);

    /// <summary>Whether a given channel is currently pulling its output down.</summary>
    public bool IsSinking(int channel) => GetOutputState(channel) == LogicState.Low;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);

        for (var i = 0; i < Channels; i++)
        {
            var on = context.ReadInput(_inputs[i], Levels) == LogicState.High;

            // Released rather than driven high: an off channel is an open collector, and a load
            // wired to it sees nothing at all rather than being pulled anywhere.
            context.Schedule(this, i, on ? LogicState.Low : LogicState.HighImpedance, delay);
        }
    }
}
