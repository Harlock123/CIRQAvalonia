using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Ics;

/// <summary>
/// Shared behaviour for the amplifiers whose gain is set inside the package rather than by
/// feedback you wire yourself: the output follows the input difference by a fixed factor, about a
/// reference, and stops at the rails.
/// <para>
/// The output is stamped as a Thevenin source re-evaluated each Newton iteration, which is what
/// makes the clipping honest rather than a post-hoc clamp on a linear answer. These parts are not
/// wired with feedback round themselves, so the iteration settles immediately.
/// </para>
/// </summary>
public abstract partial class FixedGainAmplifier : CircuitComponent
{
    protected FixedGainAmplifier()
    {
        InPlus = new Terminal("in+", "IN+", TerminalType.Input, new Point(-50, -20));
        InMinus = new Terminal("in-", "IN-", TerminalType.Input, new Point(-50, 20));
        Output = new Terminal("out", "OUT", TerminalType.Output, new Point(50, 0));
        PositiveSupply = new Terminal("v+", "V+", TerminalType.Power, new Point(0, -45));
        NegativeSupply = new Terminal("v-", "V-", TerminalType.Ground, new Point(0, 45));

        Terminals = [InPlus, InMinus, Output, PositiveSupply, NegativeSupply];
    }

    public Terminal InPlus { get; }
    public Terminal InMinus { get; }
    public Terminal Output { get; }
    public Terminal PositiveSupply { get; }
    public Terminal NegativeSupply { get; }

    /// <summary>Output impedance in ohms.</summary>
    [ObservableProperty]
    public partial double OutputResistance { get; set; } = 50.0;

    /// <summary>How close the output can get to either rail, in volts.</summary>
    [ObservableProperty]
    public partial double SwingHeadroom { get; set; } = 1.0;

    /// <summary>Resistance each input presents, which is what loads the source driving it.</summary>
    [ObservableProperty]
    public partial double InputResistance { get; set; } = 50e3;

    public override string DesignatorPrefix => "U";

    public override bool IsNonlinear => true;

    public override int VoltageSourceCount => 1;

    /// <summary>Output voltage at the last solved point.</summary>
    public double OutputVoltage { get; private set; }

    /// <summary>True while the output has run into a rail and stopped following the input.</summary>
    public bool IsClipping { get; private set; }

    /// <summary>The factor the input difference is multiplied by.</summary>
    protected abstract double Gain { get; }

    /// <summary>
    /// What the output sits at with no input. Half the supply for a single-supply audio amp, the
    /// reference pin for an instrumentation amp.
    /// </summary>
    protected abstract double QuiescentOutput(MnaSystem system);

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var plus = system.Node(InPlus);
        var minus = system.Node(InMinus);
        var reference = system.Node(NegativeSupply);

        // The inputs are a load like any other, and a high-impedance source notices.
        system.StampResistor(plus, reference, Math.Max(InputResistance, 1.0));
        system.StampResistor(minus, reference, Math.Max(InputResistance, 1.0));

        var differential = system.IterationVoltageAcross(plus, minus);
        var target = QuiescentOutput(system) + (Gain * differential);

        var top = system.IterationVoltage(system.Node(PositiveSupply)) - SwingHeadroom;
        var bottom = system.IterationVoltage(reference) + SwingHeadroom;

        // Rails first, in case the supply is wired the wrong way round or not at all.
        if (bottom > top) (top, bottom) = ((top + bottom) / 2, (top + bottom) / 2);

        var clamped = Math.Clamp(target, bottom, top);

        system.StampTheveninSource(
            system.Branch(this), system.Node(Output), reference,
            clamped, Math.Max(OutputResistance, 1e-3));
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        OutputVoltage = system.NodeVoltage(Output) - system.NodeVoltage(NegativeSupply);

        var differential = system.NodeVoltage(InPlus) - system.NodeVoltage(InMinus);
        var wanted = QuiescentOutput(system) + (Gain * differential);
        var top = system.NodeVoltage(PositiveSupply) - system.NodeVoltage(NegativeSupply) - SwingHeadroom;

        IsClipping = wanted > top || wanted < SwingHeadroom;
    }

    public override void ResetState()
    {
        OutputVoltage = 0;
        IsClipping = false;
    }
}

/// <summary>
/// An LM386 audio power amplifier — the chip behind almost every small speaker project.
/// <para>
/// It runs from a single supply, needs no feedback network, and drives a few hundred milliwatts
/// into eight ohms. The gain is twenty as it comes, which is the whole point: two pins, a
/// capacitor between them, and it becomes two hundred, and there is nothing else to get wrong.
/// </para>
/// <para>
/// Its output idles at half the supply so it can swing both ways, which is why the speaker is
/// coupled through a capacitor rather than wired straight to it — connect it directly and half the
/// rail sits across the voice coil continuously. That is the mistake this part invites and the one
/// the power reading on the speaker will show you.
/// </para>
/// </summary>
public sealed partial class Lm386 : FixedGainAmplifier
{
    public Lm386()
    {
        OutputResistance = 0.5;         // it is a power amp; it can actually drive something
        SwingHeadroom = 1.0;
        InputResistance = 50e3;
    }

    /// <summary>
    /// Voltage gain. Twenty with nothing across the gain pins, two hundred with a capacitor
    /// across them, and anything between with a resistor in series.
    /// </summary>
    [ObservableProperty]
    public partial double VoltageGain { get; set; } = 20.0;

    public override string ComponentType => "LM386";

    public override string ValueLabel => $"×{VoltageGain:0}";

    protected override double Gain => VoltageGain;

    /// <summary>Half the supply, so the output has somewhere to go in both directions.</summary>
    protected override double QuiescentOutput(MnaSystem system) =>
        (system.IterationVoltage(system.Node(PositiveSupply))
         - system.IterationVoltage(system.Node(NegativeSupply))) / 2.0;

    partial void OnVoltageGainChanged(double value) => NotifyValueChanged();
}

/// <summary>
/// An INA126 instrumentation amplifier: the part for reading a small difference sitting on top of
/// a large common voltage.
/// <para>
/// A thermocouple, a strain gauge or a current-shunt gives you millivolts between two pins that
/// may both be several volts above ground. An op-amp difference stage can do it, but its accuracy
/// depends on four resistors matching, and yours will not. This has them trimmed on the die, which
/// is what you are paying for — the common-mode rejection.
/// </para>
/// <para>
/// The gain is set by one external resistor on the real part, <c>G = 5 + 80kΩ/R_G</c>. Here it is
/// a property with that formula available, rather than a resistor you wire between two pins: the
/// gain is a number you choose, and choosing it by soldering is a detail of the package rather
/// than of the circuit.
/// </para>
/// <para>
/// The reference pin is not decoration. On a single supply the output cannot go below ground, so a
/// difference that swings both ways needs the reference lifted off it — tie REF to ground and half
/// the measurement is lost at the rail.
/// </para>
/// </summary>
public sealed partial class Ina126 : FixedGainAmplifier
{
    public Ina126()
    {
        OutputResistance = 100.0;
        SwingHeadroom = 0.9;
        InputResistance = 1e9;          // it is an instrumentation amp; it must not load the source

        Reference = new Terminal("ref", "REF", TerminalType.Input, new Point(50, 30));
        Terminals = [InPlus, InMinus, Reference, Output, PositiveSupply, NegativeSupply];
    }

    /// <summary>What the output is measured against. Not optional on a single supply.</summary>
    public Terminal Reference { get; }

    /// <summary>Differential gain, 5 to about 10,000 on the real part.</summary>
    [ObservableProperty]
    public partial double DifferentialGain { get; set; } = 100.0;

    public override string ComponentType => "INA126";

    public override string ValueLabel => $"G={DifferentialGain:0}";

    /// <summary>The gain-setting resistor that would give the present gain, in ohms.</summary>
    public double GainSettingResistance => 80e3 / Math.Max(DifferentialGain - 5.0, 1e-6);

    /// <summary>The gain a given external resistor would set, per the datasheet.</summary>
    public static double GainForResistance(double ohms) => 5.0 + (80e3 / Math.Max(ohms, 1e-6));

    protected override double Gain => Math.Max(DifferentialGain, 1.0);

    protected override double QuiescentOutput(MnaSystem system) =>
        system.IterationVoltageAcross(system.Node(Reference), system.Node(NegativeSupply));

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);

        // The reference pin is an input that draws next to nothing, but it must not float.
        system.StampResistor(system.Node(Reference), system.Node(NegativeSupply), 1e9);
    }

    partial void OnDifferentialGainChanged(double value) => NotifyValueChanged();
}
