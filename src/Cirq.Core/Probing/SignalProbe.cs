using Cirq.Core.Primitives;
using Cirq.Core.Units;
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

    /// <summary>
    /// The voltage between the probed terminal and a second one, rather than against ground.
    /// <para>
    /// Several things in this library are <i>defined</i> as a difference and cannot honestly be
    /// shown any other way. A CAN or RS-485 pair carries its bit as the difference between two
    /// wires, both of which are doing something uninteresting against ground. A shunt's whole job
    /// is the millivolts across it while both of its ends sit near a rail. A bridge sensor outputs
    /// a few millivolts riding on half the supply. Probing one end and imagining the subtraction
    /// is what a person has had to do until now.
    /// </para>
    /// </summary>
    Differential,

    /// <summary>
    /// Instantaneous power: the voltage across the probe's two points times the current through
    /// the probed terminal.
    /// <para>
    /// Power is a first-class quantity in every real design — what a regulator is burning, what a
    /// resistor has to be rated for, where a solar panel delivers the most — and it is one
    /// multiplication away from two things the scope already has. Having to do that multiplication
    /// by eye is why nobody does it.
    /// </para>
    /// </summary>
    Power,
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

    /// <summary>The kinds offered in the scope's trace list.</summary>
    public static IReadOnlyList<ProbeKind> KindOptions { get; } = Enum.GetValues<ProbeKind>();

    /// <summary>
    /// The second point a differential or power probe measures against. Null means ground, which
    /// is what an ordinary voltage probe measures against and a sensible default for the rest.
    /// </summary>
    [ObservableProperty]
    public partial Terminal? ReferenceTerminal { get; set; }

    /// <summary>Node index of <see cref="ReferenceTerminal"/>, resolved with the rest.</summary>
    public int ReferenceNodeIndex { get; set; } = Netlist.GroundIndex;

    /// <summary>True for the kinds that measure between two points rather than against ground.</summary>
    public bool IsDerived => Kind is ProbeKind.Differential or ProbeKind.Power;

    /// <summary>What the reference is called, for the trace row. "ground" when there is none.</summary>
    public string ReferenceLabel => ReferenceTerminal?.ToString() ?? "ground";

    /// <summary>Unit symbol for this probe's quantity — a logic trace has none.</summary>
    public string Unit => Kind switch
    {
        ProbeKind.Voltage or ProbeKind.Differential => "V",
        ProbeKind.Current => "A",
        ProbeKind.Power => "W",
        _ => "",
    };

    /// <summary>
    /// The numeric readout, in engineering notation. A current probe reading 0.00303 A is a
    /// measurement nobody takes: it is 3.03 mA, and the prefix is most of the information.
    /// </summary>
    public string Reading => Kind == ProbeKind.Logic
        ? LastValue switch { >= 0.75 => "1", <= 0.25 => "0", _ => "x" }
        : SiPrefix.Format(LastValue, Unit);

    /// <summary>
    /// What this trace is doing over whatever the scope last measured, or <c>None</c> before it
    /// has measured anything. Filled in by the scope rather than computed here, because the
    /// answer depends on the window on screen and the probe does not know what that is.
    /// </summary>
    public TraceMeasurements Measurements { get; private set; } = TraceMeasurements.None;

    /// <summary>Records a fresh set of measurements and tells the panel to re-read the summary.</summary>
    public void SetMeasurements(TraceMeasurements measurements)
    {
        Measurements = measurements;
        OnPropertyChanged(nameof(Measurements));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>
    /// A line of the measurements, sized to the trace list beside the plot. Peak to peak and
    /// frequency first, because between them they answer "how big" and "how fast", which is what
    /// somebody glancing at a trace wants; the rest is behind the tooltip.
    /// </summary>
    public string Summary
    {
        get
        {
            var m = Measurements;
            if (!m.IsValid || Kind == ProbeKind.Logic) return string.Empty;

            List<string> parts = [$"{SiPrefix.Format(m.PeakToPeak, Unit)}pp"];

            if (m.Frequency is { } hertz) parts.Add(SiPrefix.Format(hertz, "Hz"));
            else parts.Add($"{SiPrefix.Format(m.Mean, Unit)} avg");

            return string.Join("  ", parts);
        }
    }

    /// <summary>Everything measured, for the tooltip on the trace row.</summary>
    public string SummaryDetail
    {
        get
        {
            var m = Measurements;
            if (!m.IsValid) return "Nothing measured yet.";

            List<string> lines =
            [
                $"Peak to peak  {SiPrefix.Format(m.PeakToPeak, Unit)}",
                $"Minimum       {SiPrefix.Format(m.Minimum, Unit)}",
                $"Maximum       {SiPrefix.Format(m.Maximum, Unit)}",
                $"Mean          {SiPrefix.Format(m.Mean, Unit)}",
                $"RMS           {SiPrefix.Format(m.Rms, Unit)}",
            ];

            if (m.Frequency is { } hertz)
            {
                lines.Add($"Frequency     {SiPrefix.Format(hertz, "Hz")}");
                lines.Add($"Period        {SiPrefix.Format(m.Period!.Value, "s")}");
            }

            if (m.DutyCycle is { } duty) lines.Add($"Duty cycle    {duty * 100:0.#} %");
            if (m.RiseTime is { } rise) lines.Add($"Rise (10-90)  {SiPrefix.Format(rise, "s")}");

            return string.Join(Environment.NewLine, lines);
        }
    }

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

    partial void OnLastValueChanged(double value) => OnPropertyChanged(nameof(Reading));

    /// <summary>
    /// Changing what a probe measures invalidates everything it has already recorded — amps and
    /// volts do not belong on the same trace — so the history goes rather than being rescaled.
    /// </summary>
    partial void OnKindChanged(ProbeKind value)
    {
        ResetHistory();
        OnPropertyChanged(nameof(Unit));
        OnPropertyChanged(nameof(Reading));
        OnPropertyChanged(nameof(IsDerived));
    }

    partial void OnReferenceTerminalChanged(Terminal? value)
    {
        // The measurement is now against something else, so the old samples are of a different
        // quantity. Same reasoning as changing the kind.
        ResetHistory();
        OnPropertyChanged(nameof(ReferenceLabel));
    }

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
