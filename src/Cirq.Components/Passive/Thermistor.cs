using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;
using Cirq.Core.Probing;

namespace Cirq.Components.Passive;

/// <summary>Which way a thermistor's resistance moves with temperature.</summary>
public enum ThermistorKind
{
    /// <summary>Negative temperature coefficient: resistance falls as it warms. The common sort.</summary>
    Ntc,

    /// <summary>Positive temperature coefficient: resistance rises as it warms.</summary>
    Ptc,
}

/// <summary>
/// A thermistor: a resistance that depends on temperature.
/// <para>
/// An NTC follows the Beta equation, <c>R = R₂₅·exp(B·(1/T − 1/T₂₅))</c> with the temperatures in
/// kelvin. That is the real curve and it is steeply non-linear — a 10 kΩ B3950 part reads 33 kΩ at
/// freezing and 2.5 kΩ at 60 °C. Linearising it is the usual reason a home-made thermometer reads
/// correctly at one temperature and nowhere else.
/// </para>
/// <para>
/// A PTC is specified differently, by a temperature coefficient per kelvin rather than a Beta, so
/// it is modelled that way rather than by pretending one equation covers both.
/// </para>
/// <para>
/// Double-click it on the canvas to swing it between cold and warm, the same as operating a
/// switch, so a temperature-sensing circuit can be exercised while the simulation runs.
/// </para>
/// </summary>
public partial class Thermistor : TwoTerminalComponent, IInteractiveComponent, ICurrentReporting, IPowerRated
{
    /// <summary>
    /// Watts it can dissipate before it is measuring its own heating rather than the room.
    /// <para>
    /// The figure on a thermistor is not really a rating so much as a warning: pass enough current
    /// through it and the reading it gives you is of a part warmed by the current you are measuring
    /// it with, which is the one failure mode a temperature sensor has that nothing else does.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial double PowerRating { get; set; } = 0.05;

    /// <summary>Temperature used for "cold", in degrees Celsius.</summary>
    public const double ColdTemperature = 0.0;

    /// <summary>Temperature used for "warm", in degrees Celsius.</summary>
    public const double WarmTemperature = 60.0;

    private const double Kelvin = 273.15;
    private const double ReferenceKelvin = 25.0 + Kelvin;

    public Thermistor(ThermistorKind kind = ThermistorKind.Ntc)
    {
        Kind = kind;
        if (kind is ThermistorKind.Ptc) ResistanceAt25 = 1e3;
    }

    [ObservableProperty]
    public partial ThermistorKind Kind { get; set; }

    /// <summary>Temperature of the bead, in degrees Celsius.</summary>
    [ObservableProperty]
    [Operable("Temperature", Minimum = -40, Maximum = 150, Unit = "°C")]
    public partial double Temperature { get; set; } = 25.0;

    /// <summary>Resistance at 25 °C, in ohms — the figure a part is sold by.</summary>
    [ObservableProperty]
    public partial double ResistanceAt25 { get; set; } = 10e3;

    /// <summary>
    /// Beta coefficient in kelvin, for an NTC. 3950 K is the common value for a 10 kΩ bead.
    /// </summary>
    [ObservableProperty]
    public partial double Beta { get; set; } = 3950.0;

    /// <summary>Fractional resistance change per kelvin, for a PTC. Around 0.7%/K for a silistor.</summary>
    [ObservableProperty]
    public partial double TemperatureCoefficient { get; set; } = 0.007;

    public override string ComponentType => Kind is ThermistorKind.Ntc ? "NTC Thermistor" : "PTC Thermistor";

    public override string DesignatorPrefix => "TH";

    public override string ValueLabel => $"{SiPrefix.Format(Resistance, "Ω")} @ {Temperature:0}°C";

    /// <summary>Present resistance, in ohms.</summary>
    public double Resistance
    {
        get
        {
            var kelvin = Math.Max(Temperature + Kelvin, 1.0);

            var value = Kind is ThermistorKind.Ntc
                // The Beta equation, which is what an NTC datasheet actually gives you.
                ? ResistanceAt25 * Math.Exp(Beta * ((1.0 / kelvin) - (1.0 / ReferenceKelvin)))
                // A PTC is specified by a coefficient per kelvin instead.
                : ResistanceAt25 * Math.Exp(TemperatureCoefficient * (kelvin - ReferenceKelvin));

            return Math.Clamp(value, 1e-3, 1e12);
        }
    }

    /// <summary>Current through the bead at the last solved point, in amps.</summary>
    public double Current { get; private set; }

    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state) =>
        CurrentIntoPin(terminal, Current);

    /// <summary>True while the bead is nearer the warm end of its toggle than the cold.</summary>
    public bool IsWarm => Temperature > (ColdTemperature + WarmTemperature) / 2.0;

    public string InteractionHint => IsWarm ? "Cool the thermistor" : "Warm the thermistor";

    public void Interact() => Temperature = IsWarm ? ColdTemperature : WarmTemperature;

    public override void StampMatrix(MnaSystem system, SimulationState state) =>
        system.StampResistor(system.Node(A), system.Node(B), Resistance);

    public override void CommitTimeStep(MnaSystem system, SimulationState state) =>
        Current = VoltageAcross(system, A, B) / Math.Max(Resistance, 1e-9);

    public override void ResetState() => Current = 0;

    partial void OnTemperatureChanged(double value) => NotifyValueChanged();

    partial void OnResistanceAt25Changed(double value) => NotifyValueChanged();

    partial void OnBetaChanged(double value) => NotifyValueChanged();

    partial void OnKindChanged(ThermistorKind value) => NotifyValueChanged();
}
