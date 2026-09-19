using Cirq.Core.Simulation;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Passive;

/// <summary>
/// A polarised aluminium electrolytic: the large smoothing capacitor in a power supply.
/// <para>
/// Two things distinguish it from an ideal capacitor and both matter in the circuit it is usually
/// found in. It has real series resistance, which is what actually sets the ripple voltage across
/// a smoothing capacitor — ripple current through the ESR, not just the charge-discharge slope.
/// And it is polarised: connected backwards, or run over its voltage rating, a real one vents.
/// Those are reported rather than silently simulated, in the same way a Raspberry Pi pin reports
/// being driven above 3.3V.
/// </para>
/// </summary>
public partial class ElectrolyticCapacitor : Capacitor
{
    public ElectrolyticCapacitor(double capacitance = 100e-6)
        : base(capacitance)
    {
        // Polarity is shown by the symbol — a filled bar against a curved plate and a "+" — which
        // is how an electrolytic is drawn on a schematic, rather than by naming the pins.
    }

    /// <summary>
    /// Equivalent series resistance in ohms. Stamped between the capacitance and the negative
    /// terminal through an internal node, so the ripple it develops is solved rather than assumed.
    /// </summary>
    [ObservableProperty]
    public partial double EquivalentSeriesResistance { get; set; } = 0.15;

    /// <summary>Working voltage printed on the can, in volts.</summary>
    [ObservableProperty]
    public partial double VoltageRating { get; set; } = 25.0;

    public override string ComponentType => "Electrolytic Capacitor";

    public override string ValueLabel => $"{SiPrefix.Format(Capacitance, "F")} {VoltageRating:0}V";

    /// <summary>The capacitance sits against an internal node; the ESR bridges it to the terminal.</summary>
    public override int InternalNodeCount => 1;

    /// <summary>
    /// Reverse voltage that counts as abuse rather than noise, in volts. Datasheets allow around
    /// a volt of transient reverse bias.
    /// </summary>
    private const double ReverseThreshold = 0.5;

    /// <summary>
    /// How long the part has to be held backwards before it is called out, in seconds. What
    /// damages an electrolytic is sustained reverse bias — gas generation and heating — not an
    /// instant of it, in the same way a regulator's die is damaged by sustained power rather
    /// than by a microsecond of inrush. Without this, the startup transient of a supply coming up
    /// around it was enough to raise a warning about a correctly connected capacitor.
    /// </summary>
    private const double ReverseTolerance = 1e-3;

    /// <summary>Most negative voltage seen across the part, for the polarity check.</summary>
    public double MostReverseVoltage { get; private set; }

    /// <summary>Cumulative time spent meaningfully reverse-biased, in seconds.</summary>
    public double ReverseBiasSeconds { get; private set; }

    /// <summary>Largest magnitude of voltage seen across the part.</summary>
    public double PeakVoltage { get; private set; }

    /// <summary>True once the part has been held backwards for longer than it would tolerate.</summary>
    public bool IsReverseBiased => ReverseBiasSeconds > ReverseTolerance;

    /// <summary>True once the working voltage has been exceeded.</summary>
    public bool IsOverVoltage => PeakVoltage > VoltageRating;

    /// <summary>What is wrong with how this part is being used, if anything.</summary>
    public IReadOnlyList<string> Violations
    {
        get
        {
            List<string> found = [];

            if (IsReverseBiased)
            {
                found.Add(
                    $"reverse-biased to {MostReverseVoltage:0.0} V for {ReverseBiasSeconds * 1e3:0.#} ms " +
                    "— an electrolytic is polarised and vents when connected backwards");
            }

            if (IsOverVoltage)
                found.Add($"{PeakVoltage:0.0} V across a {VoltageRating:0} V part");

            return found;
        }
    }

    protected override (int Positive, int Negative) CapacitanceNodes(MnaSystem system) =>
        (system.Node(A), system.InternalNode(this));

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);

        // A floor on the resistance: a perfect capacitor would otherwise short the internal node
        // to the terminal with an infinite conductance and take the matrix with it.
        system.StampConductance(
            system.InternalNode(this),
            system.Node(B),
            1.0 / Math.Max(EquivalentSeriesResistance, 1e-4));
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        base.CommitTimeStep(system, state);

        // Terminal to terminal, which is what the part is rated on — not the capacitance alone.
        var across = system.NodeVoltage(A) - system.NodeVoltage(B);
        PeakVoltage = Math.Max(PeakVoltage, Math.Abs(across));

        if (across < -ReverseThreshold)
        {
            MostReverseVoltage = Math.Min(MostReverseVoltage, across);
            if (state.TimeStep > 0) ReverseBiasSeconds += state.TimeStep;
        }
    }

    public override void ResetState()
    {
        base.ResetState();
        MostReverseVoltage = 0;
        ReverseBiasSeconds = 0;
        PeakVoltage = 0;
    }

    partial void OnVoltageRatingChanged(double value) => NotifyValueChanged();
}
