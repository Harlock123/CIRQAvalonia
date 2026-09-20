using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Passive;

/// <summary>
/// A common-mode choke: two windings on one core, wound so that the signal passes and the noise
/// does not.
/// <para>
/// Both conductors of a pair go through it, and the winding sense is the whole trick. Current
/// going out along one and back along the other — the <b>differential</b> current, which is the
/// signal — produces two equal and opposite fluxes that cancel in the core. The choke is not
/// there as far as that current is concerned: a few microhenries of leakage and nothing else.
/// </para>
/// <para>
/// Current going the <i>same</i> way along both — the <b>common-mode</b> current, which is noise
/// riding on the pair and returning through the ground, the chassis, or the air — produces fluxes
/// that add. That current sees the full inductance of both windings, and it is stopped.
/// </para>
/// <para>
/// So it is not a filter in the usual sense. It does not select by frequency; it selects by
/// <b>which way the current is going</b>, and it is the only part in the palette that does. That
/// is why one sits at the entry of every mains inlet and across every USB and Ethernet pair: it
/// can be fitted in series with a signal without attenuating the signal at all, which no ordinary
/// inductor can manage.
/// </para>
/// <para>
/// Sweep one in <b>Frequency Response</b> and the two impedances are the picture: the
/// differential-mode trace barely moves and the common-mode trace climbs with frequency. That
/// difference is the entire component.
/// </para>
/// </summary>
public partial class CommonModeChoke : CircuitComponent
{
    private readonly double[] _previousCurrent = new double[2];
    private readonly double[] _previousVoltage = new double[2];

    public CommonModeChoke(double inductance = 1e-3, double coupling = 0.995)
    {
        Inductance = inductance;
        Coupling = coupling;

        A1 = new Terminal("a1", "1", TerminalType.Passive, new Point(-40, -20));
        B1 = new Terminal("b1", "2", TerminalType.Passive, new Point(40, -20));
        A2 = new Terminal("a2", "3", TerminalType.Passive, new Point(-40, 20));
        B2 = new Terminal("b2", "4", TerminalType.Passive, new Point(40, 20));

        Terminals = [A1, B1, A2, B2];
    }

    /// <summary>One conductor in.</summary>
    public Terminal A1 { get; }

    /// <summary>And out.</summary>
    public Terminal B1 { get; }

    /// <summary>The other conductor in.</summary>
    public Terminal A2 { get; }

    /// <summary>And out.</summary>
    public Terminal B2 { get; }

    /// <summary>Inductance of each winding, in henries — the figure a choke is sold by.</summary>
    [ObservableProperty]
    public partial double Inductance { get; set; }

    /// <summary>
    /// How tightly the two windings share their core, from zero to one. It is never quite one, and
    /// what is left over is the leakage inductance — which is the part the signal <i>does</i> see,
    /// and therefore the thing that decides how fast a pair can still run through one.
    /// </summary>
    [ObservableProperty]
    public partial double Coupling { get; set; }

    /// <summary>Resistance of each winding, in ohms.</summary>
    [ObservableProperty]
    public partial double WindingResistance { get; set; } = 0.05;

    public override string ComponentType => "Common-Mode Choke";

    public override string DesignatorPrefix => "L";

    public override string ValueLabel => $"{SiPrefix.Format(Inductance, "H")} CM";

    /// <summary>One branch per winding.</summary>
    public override int VoltageSourceCount => 2;

    /// <summary>
    /// What common-mode current sees: both windings' flux adding, so very nearly twice the
    /// inductance of one.
    /// </summary>
    public double CommonModeInductance => Math.Max(Inductance, 1e-18) * (1.0 + Math.Clamp(Coupling, 0, 1));

    /// <summary>
    /// What the signal sees: the fluxes cancelling, leaving only what the coupling failed to
    /// share. This is the leakage inductance, and on a good choke it is a thousandth of the other
    /// number.
    /// </summary>
    public double DifferentialInductance => Math.Max(Inductance, 1e-18) * (1.0 - Math.Clamp(Coupling, 0, 1));

    /// <summary>Impedance it presents to common-mode current at a frequency, in ohms.</summary>
    public double CommonModeImpedanceAt(double hertz) =>
        Reactance(CommonModeInductance, hertz);

    /// <summary>And to the signal, which is the number that has to stay small.</summary>
    public double DifferentialImpedanceAt(double hertz) =>
        Reactance(DifferentialInductance, hertz);

    private double Reactance(double henries, double hertz)
    {
        var x = 2.0 * Math.PI * Math.Max(hertz, 0.0) * henries;

        return Math.Sqrt((x * x) + (WindingResistance * WindingResistance));
    }

    /// <summary>Current in one winding at the last accepted point, indexed 0 and 1.</summary>
    public double WindingCurrent(int index) => _previousCurrent[index];

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        (int Plus, int Minus)[] pins =
        [
            (system.Node(A1), system.Node(B1)),
            (system.Node(A2), system.Node(B2)),
        ];

        for (var i = 0; i < 2; i++)
        {
            var branch = system.Branch(this, i);

            system.Add(pins[i].Plus, branch, 1.0);
            system.Add(pins[i].Minus, branch, -1.0);

            system.Add(branch, pins[i].Plus, 1.0);
            system.Add(branch, pins[i].Minus, -1.0);
            system.Add(branch, branch, -Math.Max(WindingResistance, 1e-9));
        }

        // At DC both windings are their own resistance and the core has no say at all — which is
        // also why a common-mode choke can sit in a supply rail without dropping anything.
        if (!state.IsTransient) return;

        var trapezoidal = state.EffectiveIntegration == IntegrationMethod.Trapezoidal;
        var scale = (trapezoidal ? 2.0 : 1.0) / state.TimeStep;

        var self = Math.Max(Inductance, 1e-18) * scale;
        var mutual = self * Math.Clamp(Coupling, 0, 1);

        for (var i = 0; i < 2; i++)
        {
            var branch = system.Branch(this, i);
            var other = system.Branch(this, 1 - i);

            // The mutual term is stamped with the same sign as the self term, which is what makes
            // this a common-mode choke rather than a transformer: currents in the same direction
            // add, currents in opposite directions cancel.
            system.Add(branch, branch, -self);
            system.StampBranchCoupling(branch, other, -mutual);

            var history = (-self * _previousCurrent[i]) - (mutual * _previousCurrent[1 - i]);

            if (trapezoidal)
                history -= _previousVoltage[i] - (Math.Max(WindingResistance, 1e-9) * _previousCurrent[i]);

            system.AddRhs(branch, history);
        }
    }

    /// <summary>
    /// Both windings and the coupling between them. The DC stamp has already put each winding's
    /// resistance on its own diagonal, so this adds the reactance and the mutual term — with the
    /// mutual positive, which is the whole of what distinguishes the part.
    /// </summary>
    public override void StampAc(AcSystem system, SimulationState state)
    {
        var self = Math.Max(Inductance, 1e-18);

        system.AddInductance(system.Branch(this, 0), self);
        system.AddInductance(system.Branch(this, 1), self);
        system.AddMutualInductance(
            system.Branch(this, 0), system.Branch(this, 1), self * Math.Clamp(Coupling, 0, 1));
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        (Terminal Plus, Terminal Minus)[] pins = [(A1, B1), (A2, B2)];

        for (var i = 0; i < 2; i++)
        {
            _previousCurrent[i] = system.BranchCurrent(this, i);
            _previousVoltage[i] = system.NodeVoltage(pins[i].Plus) - system.NodeVoltage(pins[i].Minus);
        }
    }

    public override void ResetState()
    {
        Array.Clear(_previousCurrent);
        Array.Clear(_previousVoltage);
    }

    partial void OnInductanceChanged(double value) => NotifyValueChanged();
}
