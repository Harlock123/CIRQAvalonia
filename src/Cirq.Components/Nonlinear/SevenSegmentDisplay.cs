using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Nonlinear;

/// <summary>
/// Seven-segment LED display with a decimal point. Each of the eight segments is a real diode with
/// its own series resistance and its own brightness, so the display loads the driver the way a
/// physical one does: drive it too hard and the segments are bright, forget the current-limiting
/// resistors and the current is absurd.
/// <para>
/// Every segment gets an internal ohmic node, which is what keeps its exponential linearised about
/// the true junction voltage rather than the pin voltage.
/// </para>
/// </summary>
public partial class SevenSegmentDisplay : CircuitComponent
{
    /// <summary>Segment order throughout: a, b, c, d, e, f, g, dp.</summary>
    public const int SegmentCount = 8;

    private static readonly string[] SegmentNames = ["a", "b", "c", "d", "e", "f", "g", "dp"];

    private readonly Terminal[] _segments = new Terminal[SegmentCount];
    private readonly double[] _junctionVoltages = new double[SegmentCount];
    private readonly double[] _currents = new double[SegmentCount];
    private readonly double[] _brightness = new double[SegmentCount];
    private bool _limitedThisIteration;

    public SevenSegmentDisplay(bool commonAnode = true)
    {
        IsCommonAnode = commonAnode;

        // Segments a-d down the left edge, e-dp down the right, common at the bottom.
        for (var i = 0; i < SegmentCount; i++)
        {
            var left = i < 4;
            var x = left ? -60.0 : 60.0;
            var y = -45.0 + (i % 4) * 30.0;
            _segments[i] = new Terminal($"s{i}", SegmentNames[i], TerminalType.Input, new Point(x, y));
        }

        Common = new Terminal("com", "COM", TerminalType.Power, new Point(0, 95));
        Terminals = [.. _segments, Common];
    }

    public Terminal Common { get; }

    /// <summary>The eight segment pins, in the order a, b, c, d, e, f, g, dp.</summary>
    public IReadOnlyList<Terminal> Segments => _segments;

    /// <summary>Segment pin by letter; "dp" for the decimal point.</summary>
    public Terminal Segment(string name) =>
        _segments[Array.IndexOf(SegmentNames, name) is var i and >= 0
            ? i
            : throw new KeyNotFoundException($"'{name}' is not a segment.")];

    /// <summary>
    /// True for a common-anode display, which is driven by pulling segment pins low — the
    /// arrangement the 7447 expects.
    /// </summary>
    [ObservableProperty]
    public partial bool IsCommonAnode { get; set; }

    [ObservableProperty]
    public partial DiodeModel Model { get; set; } = DiodeModel.LedRed;

    [ObservableProperty]
    public partial Color EmittedColor { get; set; } = Color.FromHex("#FF4136");

    /// <summary>Series resistance built into each segment, in ohms.</summary>
    [ObservableProperty]
    public partial double SegmentResistance { get; set; } = 30.0;

    /// <summary>Current at which a segment is considered fully lit, in amps.</summary>
    [ObservableProperty]
    public partial double RatedCurrent { get; set; } = 15e-3;

    public override string ComponentType => "7-Segment Display";

    public override string DesignatorPrefix => "DS";

    public override string ValueLabel => IsCommonAnode ? "common anode" : "common cathode";

    public override bool IsNonlinear => true;

    /// <summary>One ohmic node per segment.</summary>
    public override int InternalNodeCount => SegmentCount;

    /// <summary>Normalised brightness of each segment, in the order a..g, dp.</summary>
    public IReadOnlyList<double> SegmentBrightness => _brightness;

    /// <summary>Forward current through each segment, in amps.</summary>
    public IReadOnlyList<double> SegmentCurrents => _currents;

    /// <summary>
    /// The digit currently being displayed, or null when the lit segments do not spell one. Handy
    /// for asserting in tests and for the tooltip.
    /// </summary>
    public int? DisplayedDigit
    {
        get
        {
            var pattern = 0;
            for (var i = 0; i < 7; i++)
                if (_brightness[i] > 0.15)
                    pattern |= 1 << (6 - i);

            for (var digit = 0; digit <= 9; digit++)
                if (Digital.Ic7447.PatternFor(digit) == pattern)
                    return digit;

            return null;
        }
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        _limitedThisIteration = false;

        var common = system.Node(Common);
        var vt = state.ThermalVoltage * Model.EmissionCoefficient;
        var vCritical = Junction.CriticalVoltage(Model.SaturationCurrent, vt);
        var seriesConductance = 1.0 / Math.Max(SegmentResistance, 1e-4);

        for (var i = 0; i < SegmentCount; i++)
        {
            var pin = system.Node(_segments[i]);
            var bulk = system.InternalNode(this, i);

            // A common-anode display is driven from the common pin through each LED to the
            // segment pin; a common-cathode one is the other way round.
            var anode = IsCommonAnode ? common : pin;
            var cathode = IsCommonAnode ? pin : common;

            var raw = system.IterationVoltageAcross(anode, bulk);
            var vj = Junction.Limit(raw, _junctionVoltages[i], vt, vCritical);
            if (Math.Abs(vj - raw) > 1e-12) _limitedThisIteration = true;
            _junctionVoltages[i] = vj;

            var (current, conductance) = Junction.Evaluate(vj, Model.SaturationCurrent, vt);

            system.StampNorton(anode, bulk, conductance + 1e-12, current - conductance * vj);
            system.StampConductance(bulk, cathode, seriesConductance);
        }
    }

    public override bool HasConverged(MnaSystem system, SimulationState state) => !_limitedThisIteration;

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var common = system.NodeVoltage(Common);
        var vt = state.ThermalVoltage * Model.EmissionCoefficient;

        for (var i = 0; i < SegmentCount; i++)
        {
            var pin = system.NodeVoltage(_segments[i]);
            var bulk = system.NodeVoltage(system.InternalNode(this, i));
            var anode = IsCommonAnode ? common : pin;

            var vj = anode - bulk;
            _junctionVoltages[i] = vj;

            var (current, _) = Junction.Evaluate(vj, Model.SaturationCurrent, vt);
            _currents[i] = current;

            var ratio = Math.Clamp(current / Math.Max(RatedCurrent, 1e-9), 0, 1);
            // Perceived brightness tracks roughly the square root of drive current.
            _brightness[i] = Math.Sqrt(ratio);
        }
    }

    public override void ResetState()
    {
        Array.Clear(_junctionVoltages);
        Array.Clear(_currents);
        Array.Clear(_brightness);
        _limitedThisIteration = false;
    }

    partial void OnIsCommonAnodeChanged(bool value) => NotifyValueChanged();

    partial void OnModelChanged(DiodeModel value) => NotifyValueChanged();
}
