using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Nonlinear;

/// <summary>
/// A full-wave bridge: four rectifier diodes in one package, turning an AC input into a pulsating
/// DC output at twice the input frequency.
/// <para>
/// Modelled as four real junctions rather than as an idealised block, because the thing people
/// come to a bridge to understand is where the voltage went: both halves of the cycle pass through
/// two diodes in series, so the output peak sits about 1.4 V below the input peak. An ideal bridge
/// would hide exactly the detail worth seeing.
/// </para>
/// <para>
/// Each junction carries its own ohmic node, the same as <see cref="Diode"/>, so the exponential is
/// linearised about the true junction voltage rather than the terminal voltage.
/// </para>
/// </summary>
public partial class BridgeRectifier : CircuitComponent
{
    /// <summary>The four diodes, named for where they sit in the bridge.</summary>
    private static readonly (string Name, int Anode, int Cathode)[] Arms =
    [
        ("AC1+", 0, 2),   // AC1 -> OUT+
        ("AC2+", 1, 2),   // AC2 -> OUT+
        ("-AC1", 3, 0),   // OUT- -> AC1
        ("-AC2", 3, 1),   // OUT- -> AC2
    ];

    private readonly double[] _previousJunctionVoltage = new double[4];
    private readonly double[] _current = new double[4];
    private bool _limitedThisIteration;

    public BridgeRectifier(DiodeModel? model = null)
    {
        Model = model ?? DiodeModel.D1N4001;

        Ac1 = new Terminal("ac1", "~1", TerminalType.Passive, new Point(-40, 0));
        Ac2 = new Terminal("ac2", "~2", TerminalType.Passive, new Point(40, 0));
        Positive = new Terminal("pos", "+", TerminalType.Passive, new Point(0, -40));
        Negative = new Terminal("neg", "-", TerminalType.Passive, new Point(0, 40));

        Terminals = [Ac1, Ac2, Positive, Negative];
    }

    /// <summary>First AC input.</summary>
    public Terminal Ac1 { get; }

    /// <summary>Second AC input.</summary>
    public Terminal Ac2 { get; }

    /// <summary>Positive DC output.</summary>
    public Terminal Positive { get; }

    /// <summary>Negative DC output.</summary>
    public Terminal Negative { get; }

    /// <summary>The rectifier diode used for all four arms.</summary>
    [ObservableProperty]
    public partial DiodeModel Model { get; set; }

    public override string ComponentType => "Bridge Rectifier";

    public override string DesignatorPrefix => "BR";

    public override string ValueLabel => Model.Name;

    public override bool IsNonlinear => true;

    /// <summary>One ohmic node per arm.</summary>
    public override int InternalNodeCount => 4;

    /// <summary>Current through each arm at the converged solution, in amps.</summary>
    public IReadOnlyList<double> ArmCurrents => _current;

    /// <summary>Total current leaving the positive output, in amps.</summary>
    public double OutputCurrent => _current[0] + _current[1];

    /// <summary>Drop from the AC input pair to the DC output pair, in volts.</summary>
    public double ForwardDrop { get; private set; }

    private int NodeOf(MnaSystem system, int index) => index switch
    {
        0 => system.Node(Ac1),
        1 => system.Node(Ac2),
        2 => system.Node(Positive),
        _ => system.Node(Negative),
    };

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        _limitedThisIteration = false;

        var vt = state.ThermalVoltage * Model.EmissionCoefficient;
        var critical = Junction.CriticalVoltage(Model.SaturationCurrent, vt);

        for (var arm = 0; arm < Arms.Length; arm++)
        {
            var anode = NodeOf(system, Arms[arm].Anode);
            var cathode = NodeOf(system, Arms[arm].Cathode);
            var bulk = system.InternalNode(this, arm);

            var raw = system.IterationVoltageAcross(anode, bulk);
            var vd = Diode.LimitJunctionVoltage(raw, _previousJunctionVoltage[arm], vt, critical);

            // Any arm still being limited means the solve has not settled.
            if (Math.Abs(vd - raw) > 1e-12) _limitedThisIteration = true;
            _previousJunctionVoltage[arm] = vd;

            var (current, conductance) = Junction.Evaluate(vd, Model.SaturationCurrent, vt);

            system.StampNorton(anode, bulk, conductance, current - conductance * vd);
            system.StampConductance(bulk, cathode, 1.0 / Math.Max(Model.SeriesResistance, 1e-4));
        }
    }

    public override bool HasConverged(MnaSystem system, SimulationState state) => !_limitedThisIteration;

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var vt = state.ThermalVoltage * Model.EmissionCoefficient;

        for (var arm = 0; arm < Arms.Length; arm++)
        {
            var anode = NodeOf(system, Arms[arm].Anode);
            var bulk = system.InternalNode(this, arm);
            var vd = system.NodeVoltage(anode) - system.NodeVoltage(bulk);

            _previousJunctionVoltage[arm] = vd;
            (_current[arm], _) = Junction.Evaluate(vd, Model.SaturationCurrent, vt);
        }

        // How much of the input the bridge is eating: the peak-to-peak span the AC pair presents,
        // less what actually arrives across the output pair.
        var ac = Math.Abs(system.NodeVoltage(Ac1) - system.NodeVoltage(Ac2));
        var dc = system.NodeVoltage(Positive) - system.NodeVoltage(Negative);
        ForwardDrop = ac - dc;
    }

    public override void ResetState()
    {
        Array.Clear(_previousJunctionVoltage);
        Array.Clear(_current);
        _limitedThisIteration = false;
        ForwardDrop = 0;
    }

    partial void OnModelChanged(DiodeModel value) => NotifyValueChanged();
}
