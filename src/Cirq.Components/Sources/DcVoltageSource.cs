using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Sources;

/// <summary>Independent DC voltage source with optional series (output) resistance.</summary>
public partial class DcVoltageSource : TwoTerminalComponent
{
    public DcVoltageSource(double voltage = 5.0) : base("+", "-")
    {
        Voltage = voltage;
    }

    /// <summary>Source voltage in volts, measured from the + pin to the - pin.</summary>
    [ObservableProperty]
    [Operable("Voltage", Minimum = 0, Maximum = 50, Unit = "V")]
    public partial double Voltage { get; set; }

    /// <summary>
    /// How hard this source drives a frequency sweep, in volts. It has nothing to do with the DC
    /// value: to a small signal a supply is a short circuit, so a source that is not meant to be
    /// the stimulus leaves this at zero and simply holds its node still.
    /// </summary>
    [ObservableProperty]
    public partial double AcMagnitude { get; set; }

    /// <summary>Phase of that excitation, in degrees. Only means anything against another source.</summary>
    [ObservableProperty]
    public partial double AcPhaseDegrees { get; set; }

    /// <summary>Internal series resistance in ohms; zero models an ideal source.</summary>
    [ObservableProperty]
    public partial double SeriesResistance { get; set; }

    public Terminal Positive => A;

    public Terminal Negative => B;

    public override string ComponentType => "DC Voltage Source";

    public override string DesignatorPrefix => "V";

    public override string ValueLabel => SiPrefix.Format(Voltage, "V");

    public override int VoltageSourceCount => 1;

    /// <summary>Current delivered by the source at the last solved point (positive = out of the + pin).</summary>
    public double OutputCurrent { get; private set; }

    public override void StampMatrix(MnaSystem system, SimulationState state) =>
        system.StampTheveninSource(system.Branch(this), system.Node(A), system.Node(B), Voltage, SeriesResistance);

    /// <summary>
    /// The DC stamp has already written the branch row and the series resistance; the only thing
    /// left is what the source is asked to inject, which is its AC magnitude and not its voltage.
    /// </summary>
    public override void StampAc(AcSystem system, SimulationState state) =>
        system.AddRhs(system.Branch(this), AcSystem.Phasor(AcMagnitude, AcPhaseDegrees));

    public override void CommitTimeStep(MnaSystem system, SimulationState state) =>
        OutputCurrent = -system.BranchCurrent(this);

    partial void OnVoltageChanged(double value) => NotifyValueChanged();
}
