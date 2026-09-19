using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Passive;

/// <summary>
/// A transformer with a tap at the middle of its secondary.
/// <para>
/// This is how mains supplies were built for fifty years, and it is still how a lot of gear is
/// wired. With the tap taken as the zero volt line, the two halves swing in opposite directions —
/// so two diodes give you <b>full-wave rectification</b> with one diode drop in the path instead
/// of the two a bridge costs you. At five volts that difference is most of a volt, which is why
/// the arrangement outlived the bridge in low-voltage supplies.
/// </para>
/// <para>
/// It is also how a split supply is made: rectify both halves separately against the tap and you
/// have a positive and a negative rail from one winding, which is what every op-amp circuit with
/// plus and minus fifteen volts on it is fed from.
/// </para>
/// <para>
/// The secondary is modelled as what it physically is — two windings in series sharing a core,
/// each with a quarter of the total inductance, and all three windings coupled to each other. The
/// tap is therefore a real connection to the middle rather than an ideal half-voltage point, so
/// loading one half unevenly affects the other the way it does in practice.
/// </para>
/// </summary>
public partial class CentreTappedTransformer : CircuitComponent
{
    private const int Windings = 3;

    private readonly double[] _previousCurrent = new double[Windings];
    private readonly double[] _previousVoltage = new double[Windings];

    public CentreTappedTransformer(
        double primaryInductance = 1e-3, double secondaryInductance = 1e-3, double coupling = 0.99)
    {
        PrimaryInductance = primaryInductance;
        SecondaryInductance = secondaryInductance;
        Coupling = coupling;

        P1 = new Terminal("p1", "P1", TerminalType.Passive, new Point(-30, -24));
        P2 = new Terminal("p2", "P2", TerminalType.Passive, new Point(-30, 24));
        S1 = new Terminal("s1", "S1", TerminalType.Passive, new Point(30, -32));
        CentreTap = new Terminal("ct", "CT", TerminalType.Passive, new Point(30, 0));
        S2 = new Terminal("s2", "S2", TerminalType.Passive, new Point(30, 32));

        Terminals = [P1, S1, P2, CentreTap, S2];
    }

    public Terminal P1 { get; }
    public Terminal P2 { get; }

    /// <summary>One end of the secondary.</summary>
    public Terminal S1 { get; }

    /// <summary>The middle of the secondary, and usually the zero volt line.</summary>
    public Terminal CentreTap { get; }

    /// <summary>The other end of the secondary.</summary>
    public Terminal S2 { get; }

    [ObservableProperty]
    public partial double PrimaryInductance { get; set; }

    /// <summary>Inductance of the whole secondary, end to end, in henries.</summary>
    [ObservableProperty]
    public partial double SecondaryInductance { get; set; }

    /// <summary>Coupling factor between the windings.</summary>
    [ObservableProperty]
    public partial double Coupling { get; set; }

    /// <summary>
    /// Resistance of the primary winding, in ohms.
    /// <para>
    /// Not zero by default, because a winding is a long piece of thin wire and because an ideal
    /// one is a dead short at DC — put an ideal source across that and there is no solution to
    /// find, only a voltage-source loop. It is also what limits the inrush when a supply is first
    /// switched on, which is a real effect rather than a numerical convenience.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial double PrimaryResistance { get; set; } = 1.0;

    /// <summary>Resistance of the whole secondary, in ohms. Each half carries its share.</summary>
    [ObservableProperty]
    public partial double SecondaryResistance { get; set; } = 0.5;

    public override string ComponentType => "Transformer (CT)";

    public override string DesignatorPrefix => "T";

    /// <summary>One branch per winding: the primary and the two secondary halves.</summary>
    public override int VoltageSourceCount => Windings;

    public override string ValueLabel =>
        $"{SiPrefix.Format(PrimaryInductance, "H")}:{SiPrefix.Format(SecondaryInductance, "H")} CT";

    /// <summary>
    /// Inductance of one half of the secondary. Two series-aiding halves on one core come to four
    /// times a single half's inductance, not twice — the mutual term between them counts as well
    /// — so each half is a quarter of the whole.
    /// </summary>
    public double HalfInductance => Math.Max(SecondaryInductance, 1e-18) / 4.0;

    /// <summary>Turns ratio from the primary to the whole secondary.</summary>
    public double TurnsRatio =>
        Math.Sqrt(Math.Max(SecondaryInductance, 1e-18) / Math.Max(PrimaryInductance, 1e-18));

    public double PrimaryCurrent => _previousCurrent[0];

    /// <summary>Current in one half of the secondary, indexed 0 for the S1 side.</summary>
    public double HalfCurrent(int index) => _previousCurrent[index + 1];

    private double[] Inductances =>
        [Math.Max(PrimaryInductance, 1e-18), HalfInductance, HalfInductance];

    private double[] Resistances =>
    [
        Math.Max(PrimaryResistance, 1e-9),
        Math.Max(SecondaryResistance, 1e-9) / 2.0,
        Math.Max(SecondaryResistance, 1e-9) / 2.0,
    ];

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var ct = system.Node(CentreTap);

        // Each winding between its own pair of nodes: the primary, then S1 to the tap, then the
        // tap to S2. Wiring them this way round is what makes the two halves swing oppositely
        // about the tap.
        (int Plus, int Minus)[] pins =
        [
            (system.Node(P1), system.Node(P2)),
            (system.Node(S1), ct),
            (ct, system.Node(S2)),
        ];

        for (var i = 0; i < Windings; i++)
        {
            var branch = system.Branch(this, i);

            system.Add(pins[i].Plus, branch, 1.0);
            system.Add(pins[i].Minus, branch, -1.0);
        }

        if (!state.IsTransient && state.Settings.UseInitialConditions)
        {
            for (var i = 0; i < Windings; i++) system.Add(system.Branch(this, i), system.Branch(this, i), 1.0);
            return;
        }

        var r = Resistances;

        for (var i = 0; i < Windings; i++)
        {
            var branch = system.Branch(this, i);

            system.Add(branch, pins[i].Plus, 1.0);
            system.Add(branch, pins[i].Minus, -1.0);
            system.Add(branch, branch, -r[i]);
        }

        // At DC a winding is its own resistance and nothing more, the inductance having no say.
        if (!state.IsTransient) return;

        var trapezoidal = state.EffectiveIntegration == IntegrationMethod.Trapezoidal;
        var scale = (trapezoidal ? 2.0 : 1.0) / state.TimeStep;

        var l = Inductances;
        var k = Math.Clamp(Coupling, 0, 1);

        for (var i = 0; i < Windings; i++)
        {
            var branch = system.Branch(this, i);
            var history = 0.0;

            for (var j = 0; j < Windings; j++)
            {
                // The mutual term between every pair, M = k.sqrt(Li.Lj), with the self term as
                // the case where the two are the same winding.
                var coefficient = (i == j ? l[i] : k * Math.Sqrt(l[i] * l[j])) * scale;

                if (i == j) system.Add(branch, branch, -coefficient);
                else system.StampBranchCoupling(branch, system.Branch(this, j), -coefficient);

                history -= coefficient * _previousCurrent[j];
            }

            if (trapezoidal) history -= _previousVoltage[i] - (r[i] * _previousCurrent[i]);
            system.AddRhs(branch, history);
        }
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var ct = system.NodeVoltage(CentreTap);

        _previousCurrent[0] = system.BranchCurrent(this, 0);
        _previousCurrent[1] = system.BranchCurrent(this, 1);
        _previousCurrent[2] = system.BranchCurrent(this, 2);

        _previousVoltage[0] = system.NodeVoltage(P1) - system.NodeVoltage(P2);
        _previousVoltage[1] = system.NodeVoltage(S1) - ct;
        _previousVoltage[2] = ct - system.NodeVoltage(S2);
    }

    public override void ResetState()
    {
        Array.Clear(_previousCurrent);
        Array.Clear(_previousVoltage);
    }

    partial void OnPrimaryInductanceChanged(double value) => NotifyValueChanged();

    partial void OnSecondaryInductanceChanged(double value) => NotifyValueChanged();
}
