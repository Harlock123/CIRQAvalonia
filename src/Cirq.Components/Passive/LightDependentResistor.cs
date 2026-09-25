using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;
using Cirq.Core.Probing;

namespace Cirq.Components.Passive;

/// <summary>
/// A cadmium-sulphide light-dependent resistor: a resistance that falls as it is lit.
/// <para>
/// The response is a power law rather than anything linear — roughly
/// <c>R = R₁₀·(10/E)^γ</c>, with γ near 0.8 for CdS, which is why an LDR covers five decades of
/// resistance over the range between a dark room and daylight. That span is the reason these are
/// usually read with a comparator against a divider rather than measured directly.
/// </para>
/// <para>
/// Double-click it on the canvas to cover and uncover it, the same as operating a switch, so a
/// light-sensing circuit can be exercised while the simulation runs.
/// </para>
/// </summary>
public partial class LightDependentResistor : TwoTerminalComponent, IInteractiveComponent, ICurrentReporting, IPowerRated
{
    /// <summary>Watts it can dissipate. A cadmium sulphide cell is a small part in a small package.</summary>
    [ObservableProperty]
    public partial double PowerRating { get; set; } = 0.1;

    /// <summary>Illuminance used for "covered", in lux — a dark room, not a sealed box.</summary>
    public const double CoveredIlluminance = 0.1;

    /// <summary>Illuminance used for "uncovered", in lux — bright indoor lighting.</summary>
    public const double UncoveredIlluminance = 200.0;

    public LightDependentResistor(double illuminance = UncoveredIlluminance)
    {
        Illuminance = illuminance;
    }

    /// <summary>Light falling on the cell, in lux.</summary>
    [ObservableProperty]
    [Operable("Light", Minimum = 0.1, Maximum = 1e5, Unit = "lx", IsLogarithmic = true)]
    public partial double Illuminance { get; set; }

    /// <summary>Resistance at 10 lux, in ohms — the figure datasheets quote.</summary>
    [ObservableProperty]
    public partial double ResistanceAtTenLux { get; set; } = 10e3;

    /// <summary>
    /// Slope of the log-log response. Around 0.8 for a CdS cell; higher makes the part more
    /// sensitive to a change in light.
    /// </summary>
    [ObservableProperty]
    public partial double Gamma { get; set; } = 0.8;

    /// <summary>Resistance in complete darkness, in ohms, which the power law would never reach.</summary>
    [ObservableProperty]
    public partial double DarkResistance { get; set; } = 1e6;

    public override string ComponentType => "LDR";

    public override string DesignatorPrefix => "LDR";

    public override string ValueLabel => SiPrefix.Format(Resistance, "Ω");

    /// <summary>Present resistance, in ohms.</summary>
    public double Resistance
    {
        get
        {
            // Below a hundredth of a lux the power law runs away, and a real cell flattens out at
            // its dark resistance anyway.
            var lux = Math.Max(Illuminance, 1e-3);
            var value = ResistanceAtTenLux * Math.Pow(10.0 / lux, Math.Max(Gamma, 0.0));
            return Math.Clamp(value, 1.0, Math.Max(DarkResistance, 1.0));
        }
    }

    /// <summary>Current through the cell at the last solved point, in amps.</summary>
    public double Current { get; private set; }

    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state) =>
        CurrentIntoPin(terminal, Current);

    /// <summary>True while the cell is lit rather than covered.</summary>
    public bool IsLit => Illuminance > (CoveredIlluminance + UncoveredIlluminance) / 2.0;

    public string InteractionHint => IsLit ? "Cover the LDR" : "Uncover the LDR";

    public void Interact() => Illuminance = IsLit ? CoveredIlluminance : UncoveredIlluminance;

    public override void StampMatrix(MnaSystem system, SimulationState state) =>
        system.StampResistor(system.Node(A), system.Node(B), Resistance);

    public override void CommitTimeStep(MnaSystem system, SimulationState state) =>
        Current = VoltageAcross(system, A, B) / Math.Max(Resistance, 1e-9);

    public override void ResetState() => Current = 0;

    partial void OnIlluminanceChanged(double value) => NotifyValueChanged();

    partial void OnResistanceAtTenLuxChanged(double value) => NotifyValueChanged();

    partial void OnGammaChanged(double value) => NotifyValueChanged();
}
