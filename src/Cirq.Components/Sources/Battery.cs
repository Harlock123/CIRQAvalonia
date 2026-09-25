using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Sources;

/// <summary>A stocked cell or pack, with the numbers off its datasheet.</summary>
/// <param name="Name">What it is called.</param>
/// <param name="Voltage">Nominal terminal voltage when fresh, in volts.</param>
/// <param name="InternalResistance">Series resistance in ohms — what makes it sag.</param>
/// <param name="CapacityMilliampHours">Charge it holds, in mAh.</param>
/// <param name="CutoffFraction">Fraction of nominal at which it is considered flat.</param>
public sealed record BatteryModel(
    string Name,
    double Voltage,
    double InternalResistance,
    double CapacityMilliampHours,
    double CutoffFraction = 0.7)
{
    public static readonly BatteryModel AlkalineAA = new("AA alkaline", 1.5, 0.25, 2500);

    public static readonly BatteryModel Alkaline9V = new("9 V alkaline (PP3)", 9.0, 2.0, 550);

    public static readonly BatteryModel Lithium18650 = new("18650 Li-ion", 3.7, 0.05, 3000, 0.76);

    public static readonly BatteryModel CoinCell2032 = new("CR2032 coin cell", 3.0, 10.0, 220);

    public static readonly BatteryModel LeadAcid12V = new("12 V sealed lead-acid", 12.0, 0.02, 7000, 0.85);

    public static readonly IReadOnlyList<BatteryModel> Library =
        [AlkalineAA, Alkaline9V, Lithium18650, CoinCell2032, LeadAcid12V];

    public override string ToString() => Name;
}

/// <summary>
/// A battery: a voltage source that sags under load and eventually runs out.
/// <para>
/// Every other source here is ideal and holds its voltage into a dead short. A real cell does not,
/// and the difference is most of why a circuit that works on the bench supply misbehaves on
/// batteries. The internal resistance is the whole story: a coin cell is ten ohms, so asking it
/// for the twenty milliamps an LED wants drops it two hundred millivolts and it browns out the
/// thing it is powering. A lead-acid cell is twenty milliohms and will weld a screwdriver.
/// </para>
/// <para>
/// Charge is counted out as it is drawn, so the state of charge falls in proportion to the current
/// taken and for how long — which means a stalled motor or a relay left energised visibly empties
/// it. The terminal voltage follows a simple curve: nearly flat through most of the discharge,
/// then a knee. That is the shape of a real discharge, if not any particular chemistry's exact
/// numbers.
/// </para>
/// </summary>
public partial class Battery : TwoTerminalComponent, ICurrentReporting
{
    public Battery(BatteryModel? model = null) : base("+", "-")
    {
        Model = model ?? BatteryModel.Alkaline9V;
        StateOfCharge = 1.0;
    }

    [ObservableProperty]
    public partial BatteryModel Model { get; set; }

    /// <summary>How full it is, 1 down to 0.</summary>
    [ObservableProperty]
    public partial double StateOfCharge { get; set; }

    /// <summary>
    /// Whether charge is counted out as it is drawn. Off, the cell keeps its sag but never runs
    /// down, which is what you want when the point of the circuit is something else.
    /// </summary>
    [ObservableProperty]
    public partial bool Discharges { get; set; } = true;

    public Terminal Positive => A;

    public Terminal Negative => B;

    public override string ComponentType => "Battery";

    public override string DesignatorPrefix => "BT";

    public override string ValueLabel => IsFlat
        ? "flat"
        : $"{SiPrefix.Format(OpenCircuitVoltage, "V")} · {StateOfCharge * 100:0}%";

    public override int VoltageSourceCount => 1;

    /// <summary>Current out of the + pin at the last solved point, in amps.</summary>
    public double OutputCurrent { get; private set; }

    /// <summary>Charge taken out of it so far, in milliamp-hours.</summary>
    public double DeliveredMilliampHours { get; private set; }

    /// <summary>
    /// Terminal voltage with no load. Flat for most of the discharge and then a knee, which is
    /// the shape that matters: a battery gives little warning before it stops.
    /// </summary>
    public double OpenCircuitVoltage
    {
        get
        {
            var soc = Math.Clamp(StateOfCharge, 0.0, 1.0);
            var floor = Math.Clamp(Model.CutoffFraction, 0.1, 0.99);

            // Flat across the top and then a cliff: at half charge this has given up a couple of
            // percent, and at two percent charge it has given up most of what it had. A power of
            // soc itself sags steadily from the start, which is not what a cell does.
            var curve = 1.0 - Math.Pow(1.0 - soc, 4.0);

            return Model.Voltage * (floor + ((1.0 - floor) * curve));
        }
    }

    /// <summary>True once it can no longer hold a useful voltage.</summary>
    public bool IsFlat => StateOfCharge <= 0.0;

    public IReadOnlyList<string> Violations
    {
        get
        {
            if (IsFlat) return ["flat — it has delivered its whole rated capacity"];

            // A cell asked for far more than its resistance allows is being abused, and on a real
            // bench it gets hot rather than obliging.
            var shortCircuit = Model.Voltage / Math.Max(Model.InternalResistance, 1e-6);
            if (Math.Abs(OutputCurrent) > shortCircuit * 0.5)
            {
                return [$"delivering {SiPrefix.Format(Math.Abs(OutputCurrent), "A")} against a " +
                        $"{SiPrefix.Format(shortCircuit, "A")} short-circuit current — this cell " +
                        "would be getting hot"];
            }

            return [];
        }
    }

    public override void StampMatrix(MnaSystem system, SimulationState state) =>
        system.StampTheveninSource(
            system.Branch(this), system.Node(A), system.Node(B),
            OpenCircuitVoltage, Math.Max(Model.InternalResistance, 1e-6));

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        OutputCurrent = -system.BranchCurrent(this);

        if (!Discharges || !state.IsTransient || IsFlat) return;

        // Coulomb counting: amps for seconds is charge, and the capacity is the budget. Only
        // current drawn out counts; pushing current back in is charging, which is also honest.
        var deltaMilliampHours = OutputCurrent * 1e3 * state.TimeStep / 3600.0;

        DeliveredMilliampHours += deltaMilliampHours;

        var capacity = Math.Max(Model.CapacityMilliampHours, 1e-6);
        StateOfCharge = Math.Clamp(StateOfCharge - (deltaMilliampHours / capacity), 0.0, 1.0);
    }

    /// <summary>
    /// Current into the cell through the probed pin, which is the opposite of what it is putting
    /// out. <see cref="OutputCurrent"/> is what the cell delivers — positive when charge is leaving
    /// the + pin — and <see cref="ICurrentReporting"/> asks for the current going the other way,
    /// into the part, so that clamping either end of anything reads the way a meter does.
    /// <para>
    /// This used to report <see cref="OutputCurrent"/> itself, with the sign of a delivering cell
    /// backwards against every other part in the library. A current probe on a battery read the
    /// wrong way round, the dots on its wires ran the wrong way, and nothing was obviously broken:
    /// the magnitude was right, and a battery is the one part where "current out" is such a natural
    /// thing to say that the reading looked correct.
    /// </para>
    /// </summary>
    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state) =>
        CurrentIntoPin(terminal, -OutputCurrent);

    public override void ResetState()
    {
        OutputCurrent = 0;
        DeliveredMilliampHours = 0;
        StateOfCharge = 1.0;
    }

    partial void OnModelChanged(BatteryModel value) => NotifyValueChanged();

    partial void OnStateOfChargeChanged(double value) => NotifyValueChanged();
}
