using Cirq.Core.Primitives;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Core.Probing;

/// <summary>What quantity a probe records.</summary>
public enum ProbeKind
{
    /// <summary>Node voltage relative to ground.</summary>
    Voltage,
    /// <summary>Current through the probed component branch.</summary>
    Current,
    /// <summary>Digital logic level of the probed net, rendered as 0/1.</summary>
    Logic,
}

/// <summary>A measurement point attached to a terminal or net, feeding the oscilloscope panel.</summary>
public partial class SignalProbe : ObservableObject
{
    public SignalProbe() { }

    public SignalProbe(string label, Terminal terminal, Color color, int capacity = 10_000)
    {
        Label = label;
        TargetTerminal = terminal;
        TraceColor = color;
        HistoryBuffer = new CircularBuffer<DataPoint>(capacity);
    }

    public Guid Id { get; init; } = Guid.NewGuid();

    [ObservableProperty]
    public partial string Label { get; set; } = "Probe";

    [ObservableProperty]
    public partial Terminal? TargetTerminal { get; set; }

    [ObservableProperty]
    public partial ProbeKind Kind { get; set; } = ProbeKind.Voltage;

    [ObservableProperty]
    public partial Color TraceColor { get; set; } = Color.Yellow;

    /// <summary>Whether this trace is currently drawn on the scope.</summary>
    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    /// <summary>AC coupling subtracts the running DC average before display.</summary>
    [ObservableProperty]
    public partial bool AcCoupled { get; set; }

    /// <summary>Volts per division for this trace when the scope is in stacked mode.</summary>
    [ObservableProperty]
    public partial double VoltsPerDivision { get; set; } = 1.0;

    /// <summary>Vertical offset in divisions applied when rendering.</summary>
    [ObservableProperty]
    public partial double VerticalOffset { get; set; }

    public Guid? TargetNetId { get; set; }

    /// <summary>Resolved MNA node index, refreshed each time the simulation is compiled.</summary>
    public int NodeIndex { get; set; } = Netlist.GroundIndex;

    public CircularBuffer<DataPoint> HistoryBuffer { get; init; } = new(capacity: 10_000);

    /// <summary>Most recent sampled value, used for the numeric readout.</summary>
    [ObservableProperty]
    public partial double LastValue { get; set; }

    public void Record(double time, double value)
    {
        HistoryBuffer.Add(new DataPoint(time, value));
        LastValue = value;
    }

    public void ResetHistory()
    {
        HistoryBuffer.Clear();
        LastValue = 0;
    }
}
