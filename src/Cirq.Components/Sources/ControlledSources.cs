using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Sources;

/// <summary>
/// A voltage source whose output is a multiple of the voltage somewhere else — SPICE's <c>E</c>
/// element, and the thing every textbook draws as a diamond.
/// <para>
/// This is the primitive amplifiers are made of. An ideal op-amp is one of these with a gain of a
/// hundred thousand; the input stage of a real one is a transconductance into a capacitor; and
/// almost every macromodel a manufacturer publishes is a handful of these around a few passives.
/// Having it as a part means those models can be brought in rather than approximated.
/// </para>
/// <para>
/// It is ideal in both directions: the control pins draw no current at all, and the output holds
/// its voltage into any load. That is what makes it a modelling primitive rather than a component
/// — real behaviour is built by putting resistances around it.
/// </para>
/// </summary>
public partial class VoltageControlledVoltageSource : CircuitComponent
{
    public VoltageControlledVoltageSource(double gain = 10.0)
    {
        Gain = gain;

        OutputPositive = new Terminal("out+", "+", TerminalType.Output, new Point(40, -20));
        OutputNegative = new Terminal("out-", "−", TerminalType.Output, new Point(40, 20));
        ControlPositive = new Terminal("in+", "C+", TerminalType.Input, new Point(-40, -20));
        ControlNegative = new Terminal("in-", "C−", TerminalType.Input, new Point(-40, 20));

        Terminals = [OutputPositive, OutputNegative, ControlPositive, ControlNegative];
    }

    public Terminal OutputPositive { get; }

    public Terminal OutputNegative { get; }

    public Terminal ControlPositive { get; }

    public Terminal ControlNegative { get; }

    /// <summary>Volts out per volt in, which is dimensionless.</summary>
    [ObservableProperty]
    [Operable("Gain", Minimum = -1000, Maximum = 1000)]
    public partial double Gain { get; set; }

    public override string ComponentType => "VCVS";

    public override string DesignatorPrefix => "E";

    public override string ValueLabel => $"×{Gain:0.###}";

    public override int VoltageSourceCount => 1;

    public override void StampMatrix(MnaSystem system, SimulationState state) =>
        system.StampVcvs(
            system.Branch(this),
            system.Node(OutputPositive), system.Node(OutputNegative),
            system.Node(ControlPositive), system.Node(ControlNegative),
            Gain);

    partial void OnGainChanged(double value) => NotifyValueChanged();
}

/// <summary>
/// A current source whose output is the voltage somewhere else times a transconductance — SPICE's
/// <c>G</c> element.
/// <para>
/// The other half of the modelling kit. Where a <see cref="VoltageControlledVoltageSource"/> makes
/// a voltage amplifier, this makes a transconductance stage — which is what the input of nearly
/// every real op-amp actually is, and why an op-amp's gain falls with frequency: the current goes
/// into a capacitor.
/// </para>
/// </summary>
public partial class VoltageControlledCurrentSource : CircuitComponent
{
    public VoltageControlledCurrentSource(double transconductance = 1e-3)
    {
        Transconductance = transconductance;

        OutputPositive = new Terminal("out+", "+", TerminalType.Output, new Point(40, -20));
        OutputNegative = new Terminal("out-", "−", TerminalType.Output, new Point(40, 20));
        ControlPositive = new Terminal("in+", "C+", TerminalType.Input, new Point(-40, -20));
        ControlNegative = new Terminal("in-", "C−", TerminalType.Input, new Point(-40, 20));

        Terminals = [OutputPositive, OutputNegative, ControlPositive, ControlNegative];
    }

    public Terminal OutputPositive { get; }

    public Terminal OutputNegative { get; }

    public Terminal ControlPositive { get; }

    public Terminal ControlNegative { get; }

    /// <summary>Amps out per volt in, in siemens.</summary>
    [ObservableProperty]
    [Operable("Transconductance", Minimum = 0, Maximum = 1, Unit = "S")]
    public partial double Transconductance { get; set; }

    public override string ComponentType => "VCCS";

    public override string DesignatorPrefix => "G";

    public override string ValueLabel => SiPrefix.Format(Transconductance, "S");

    /// <summary>
    /// No branch: a transconductance is a conductance between two pairs of nodes and stamps
    /// straight into the matrix, which is why it costs nothing a voltage source costs.
    /// </summary>
    public override void StampMatrix(MnaSystem system, SimulationState state) =>
        system.StampVccs(
            system.Node(OutputPositive), system.Node(OutputNegative),
            system.Node(ControlPositive), system.Node(ControlNegative),
            Transconductance);

    partial void OnTransconductanceChanged(double value) => NotifyValueChanged();
}
