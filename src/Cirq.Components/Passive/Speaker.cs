using Cirq.Core.Simulation;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Passive;

/// <summary>
/// A moving-coil loudspeaker, electrically a voice coil: a few ohms of wire with some inductance.
/// <para>
/// The impedance on the box is a nominal figure, not a resistance — it is the DC resistance plus
/// whatever the coil's inductance adds at frequency, which is why an "8 ohm" speaker measures
/// about six with a meter and rather more than eight at the top of its range. Both parts are here,
/// so an amplifier driving one sees a load that gets harder with frequency rather than a fixed
/// resistor.
/// </para>
/// <para>
/// What it reports is power, because that is what a speaker is rated in and what decides whether
/// it survives. The rating is thermal — a coil is a heater with a magnet near it — so it is the
/// average that counts, not the peaks, and the average is what is tracked.
/// </para>
/// </summary>
public partial class Speaker : Inductor
{
    /// <summary>Time constant the power average is taken over — a coil's thermal memory.</summary>
    private const double ThermalTimeConstant = 0.25;

    public Speaker(double impedance = 8.0)
    {
        NominalImpedance = impedance;
        Inductance = 500e-6;
        SeriesResistance = impedance * 0.8;     // DC resistance runs below the nominal figure
    }

    /// <summary>The figure on the box, in ohms.</summary>
    [ObservableProperty]
    public partial double NominalImpedance { get; set; }

    /// <summary>Continuous power it can take before the coil cooks, in watts.</summary>
    [ObservableProperty]
    public partial double PowerRating { get; set; } = 0.5;

    public override string ComponentType => "Speaker";

    public override string DesignatorPrefix => "LS";

    public override string ValueLabel => IsSounding
        ? $"{SiPrefix.Format(AveragePower, "W")}"
        : $"{SiPrefix.Format(NominalImpedance, "Ω")}";

    /// <summary>Power being dissipated in the coil right now, in watts.</summary>
    public double InstantaneousPower { get; private set; }

    /// <summary>Power averaged over the coil's thermal time constant, in watts.</summary>
    public double AveragePower { get; private set; }

    /// <summary>True while enough is going through it to hear.</summary>
    public bool IsSounding => AveragePower > PowerRating * 1e-3;

    public IReadOnlyList<string> Violations => AveragePower > PowerRating
        ? [$"taking {SiPrefix.Format(AveragePower, "W")} against a " +
           $"{SiPrefix.Format(PowerRating, "W")} rating — the coil is a heater and this one is " +
           "being asked to run hot"]
        : [];

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        base.CommitTimeStep(system, state);

        InstantaneousPower = Current * Current * Math.Max(SeriesResistance, 1e-9);

        if (!state.IsTransient)
        {
            AveragePower = InstantaneousPower;
            return;
        }

        // A first-order average rather than the instantaneous value: a speaker fed a sine wave is
        // at zero watts twice a cycle, and the coil does not care.
        var alpha = Math.Clamp(state.TimeStep / ThermalTimeConstant, 0.0, 1.0);
        AveragePower += (InstantaneousPower - AveragePower) * alpha;
    }

    public override void ResetState()
    {
        base.ResetState();
        InstantaneousPower = 0;
        AveragePower = 0;
    }

    partial void OnNominalImpedanceChanged(double value) => NotifyValueChanged();
}
