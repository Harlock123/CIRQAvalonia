using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Nonlinear;

/// <summary>
/// Several LEDs in one package: each a junction of its own between two of the package's pins, with its own colour and
/// brightness, as the bridge rectifier's four diodes are stamped (a junction and its series resistance through an
/// internal node).
/// </summary>
public abstract partial class MultiLed : CircuitComponent, ICurrentRated
{
    private readonly double[] _previous;
    private readonly double[] _current;
    private bool _limited;

    /// <summary>One LED of the package: which pins it's between, its model and the colour it shines.</summary>
    protected sealed record Junction(int Anode, int Cathode, DiodeModel Model, Color Colour);

    protected abstract IReadOnlyList<Junction> Junctions { get; }

    protected MultiLed(int count)
    {
        _previous = new double[count];
        _current = new double[count];
        Brightness = new double[count];
    }

    /// <summary>Each LED's brightness from 0 to 1, in the order of <see cref="Colours"/>.</summary>
    public double[] Brightness { get; }

    /// <summary>The colour each LED shines.</summary>
    public IReadOnlyList<Color> Colours => [.. Junctions.Select(j => j.Colour)];

    /// <summary>The current each LED is taking, in amps.</summary>
    public IReadOnlyList<double> Currents => _current;

    /// <summary>The mix the eye sees: each colour weighted by how bright it is.</summary>
    public (double R, double G, double B, double Brightness) Mix()
    {
        var (r, g, b, total) = (0.0, 0.0, 0.0, 0.0);
        for (var i = 0; i < Brightness.Length; i++)
        {
            var c = Colours[i];
            r += c.R / 255.0 * Brightness[i];
            g += c.G / 255.0 * Brightness[i];
            b += c.B / 255.0 * Brightness[i];
            total = Math.Max(total, Brightness[i]);
        }

        var scale = Math.Max(1, Math.Max(r, Math.Max(g, b)));
        return (r / scale, g / scale, b / scale, total);
    }

    /// <summary>Current at which each LED is fully lit, in amps.</summary>
    [ObservableProperty]
    public partial double RatedCurrent { get; set; } = 20e-3;

    public override string DesignatorPrefix => "LED";

    public override bool IsNonlinear => true;

    public override int InternalNodeCount => _previous.Length;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        _limited = false;
        var junctions = Junctions;
        for (var i = 0; i < junctions.Count; i++)
        {
            var j = junctions[i];
            var vt = state.ThermalVoltage * j.Model.EmissionCoefficient;
            var critical = Cirq.Components.Nonlinear.Junction.CriticalVoltage(j.Model.SaturationCurrent, vt);
            var anode = system.Node(Terminals[j.Anode]);
            var cathode = system.Node(Terminals[j.Cathode]);
            var bulk = system.InternalNode(this, i);
            var raw = system.IterationVoltageAcross(anode, bulk);
            var vd = Diode.LimitJunctionVoltage(raw, _previous[i], vt, critical);
            if (Math.Abs(vd - raw) > 1e-12) _limited = true;
            _previous[i] = vd;
            var (current, conductance) = Cirq.Components.Nonlinear.Junction.Evaluate(vd, j.Model.SaturationCurrent, vt);
            system.StampNorton(anode, bulk, conductance, current - (conductance * vd));
            system.StampConductance(bulk, cathode, 1.0 / Math.Max(j.Model.SeriesResistance, 1e-4));
        }
    }

    public override bool HasConverged(MnaSystem system, SimulationState state) => !_limited;

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var junctions = Junctions;
        for (var i = 0; i < junctions.Count; i++)
        {
            var j = junctions[i];
            var vt = state.ThermalVoltage * j.Model.EmissionCoefficient;
            var vd = system.NodeVoltage(Terminals[j.Anode]) - system.NodeVoltage(system.InternalNode(this, i));
            _previous[i] = vd;
            (_current[i], _) = Cirq.Components.Nonlinear.Junction.Evaluate(vd, j.Model.SaturationCurrent, vt);
            Brightness[i] = Math.Sqrt(Math.Clamp(_current[i] / Math.Max(RatedCurrent, 1e-9), 0, 1));
        }
    }

    public override void ResetState()
    {
        Array.Clear(_previous);
        Array.Clear(_current);
        Array.Clear(Brightness);
        _limited = false;
    }
}

/// <summary>
/// An RGB LED, common cathode: red, green and blue in one 5 mm package, each with its own anode (R, G, B) and the
/// cathode (K) they share; or common anode (<see cref="CommonAnode"/>), the three cathodes on their own and one anode.
/// Mix them with a resistor and a PWM pin each.
/// </summary>
public sealed partial class RgbLed : MultiLed
{
    private IReadOnlyList<Junction> _junctions = [];

    public RgbLed() : base(3)
    {
        Terminals =
        [
            new Terminal("r", "R", TerminalType.Passive, new Point(-30, -20)),
            new Terminal("g", "G", TerminalType.Passive, new Point(-30, 0)),
            new Terminal("b", "B", TerminalType.Passive, new Point(-30, 20)),
            new Terminal("k", "K", TerminalType.Passive, new Point(30, 0)),
        ];
        Build();
    }

    /// <summary>Common anode: the shared pin is the anode, and R, G and B are the three cathodes.</summary>
    [ObservableProperty]
    public partial bool CommonAnode { get; set; }

    partial void OnCommonAnodeChanged(bool value) => Build();

    private void Build()
    {
        var models = new[] { (DiodeModel.LedRed, "#FF4136"), (DiodeModel.LedGreen, "#2ECC40"), (DiodeModel.LedBlue, "#3D9BFF") };
        _junctions = [.. models.Select((m, i) => CommonAnode
            ? new Junction(3, i, m.Item1, Color.FromHex(m.Item2))
            : new Junction(i, 3, m.Item1, Color.FromHex(m.Item2)))];
        NotifyValueChanged();
    }

    protected override IReadOnlyList<Junction> Junctions => _junctions;

    public override string ComponentType => "RGB LED";

    public override string ValueLabel => CommonAnode ? "RGB, common anode" : "RGB, common cathode";
}

/// <summary>
/// A bicolour LED with two leads: red and green back to back, so current one way lights red and the other way green,
/// and an AC or a fast swap shows yellow.
/// </summary>
public sealed partial class BicolourLed : MultiLed
{
    private static readonly IReadOnlyList<Junction> Pair =
    [
        new(0, 1, DiodeModel.LedRed, Color.FromHex("#FF4136")),
        new(1, 0, DiodeModel.LedGreen, Color.FromHex("#2ECC40")),
    ];

    public BicolourLed() : base(2)
    {
        Terminals =
        [
            new Terminal("a", "A", TerminalType.Passive, new Point(-30, 0)),
            new Terminal("b", "B", TerminalType.Passive, new Point(30, 0)),
        ];
    }

    protected override IReadOnlyList<Junction> Junctions => Pair;

    public override string ComponentType => "Bicolour LED";

    public override string ValueLabel => "red A→B, green B→A";
}
