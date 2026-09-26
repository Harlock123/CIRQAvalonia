using System.Collections.ObjectModel;
using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>How the scope arranges its traces.</summary>
public enum ScopeLayout
{
    /// <summary>Every trace shares one set of axes.</summary>
    Unified,
    /// <summary>Traces are offset vertically so they do not overlap.</summary>
    Stacked,
    /// <summary>Each trace gets its own sub-plot.</summary>
    Tiled,

    /// <summary>
    /// One trace against another instead of against time.
    /// <para>
    /// Time is not always the interesting axis. Plotting a device's current against the voltage
    /// across it draws its I-V curve while the circuit runs; plotting an output against its input
    /// draws the transfer characteristic, and if the input sweeps up and back again a comparator's
    /// hysteresis comes out as the loop it actually is — which a DC sweep cannot show, because a
    /// sweep only goes one way. Two sine waves against each other give the Lissajous figure that
    /// is how phase was measured before anything had a phase meter.
    /// </para>
    /// </summary>
    Xy,
}

/// <summary>
/// The oscilloscope panel's state: which probes are shown, the timebase and vertical scaling, and
/// the sample interval the engine should decimate its probe recording to.
/// </summary>
/// <summary>
/// A trace as it was at some moment, kept to compare against.
/// </summary>
/// <param name="Label">The probe it came from.</param>
/// <param name="Color">The probe's colour, so a reference is recognisably the same signal.</param>
/// <param name="Samples">What was recorded at the moment it was taken.</param>
/// <param name="TakenUtc">When, so several references can be told apart.</param>
public sealed record ReferenceTrace(
    string Label,
    Cirq.Core.Primitives.Color Color,
    IReadOnlyList<Cirq.Core.Primitives.DataPoint> Samples,
    DateTimeOffset TakenUtc)
{
    /// <summary>What the legend calls it.</summary>
    public string LegendText => $"{Label} (ref)";

    /// <summary>What the list of references calls it.</summary>
    public string Summary => $"{Label} · {Samples.Count} points at {TakenUtc.LocalDateTime:HH:mm:ss}";
}

/// <summary>A trace worked out from the others rather than recorded.</summary>
/// <param name="Label">What to call it.</param>
/// <param name="Expression">The arithmetic, over the other traces' labels.</param>
/// <param name="Color">What to draw it in.</param>
public sealed record ComputedTrace(
    string Label, string Expression, Cirq.Core.Primitives.Color Color)
{
    public string LegendText => $"{Label} = {Expression}";
}

public sealed partial class ScopeViewModel : ObservableObject
{
    /// <summary>Horizontal divisions on the scope face, matching a real bench instrument.</summary>
    public const int HorizontalDivisions = 10;

    public const int VerticalDivisions = 8;

    private readonly Circuit _circuit;
    private int _paletteIndex;

    public ScopeViewModel(Circuit circuit)
    {
        _circuit = circuit;
        Probes = circuit.Probes;
    }

    public ObservableCollection<SignalProbe> Probes { get; }

    /// <summary>
    /// Traces captured earlier, drawn behind the live ones to compare against.
    /// <para>
    /// "Is that better than what I had" is the question after every edit, and until now the only
    /// way to answer it was to remember what the last one looked like. Two pictures side by side
    /// tell you much less than two curves on the same axes, because what you are looking for is
    /// the difference between them.
    /// </para>
    /// </summary>
    public ObservableCollection<ReferenceTrace> References { get; } = [];

    /// <summary>
    /// Traces worked out from the recorded ones: a ratio, a difference, a power, an efficiency.
    /// <para>
    /// The scope already has probe kinds for a difference and a power, because those were common
    /// enough to be worth their own. But every such kind is a guess at what somebody will want and
    /// the list has no end, so this covers the rest at the cost of one feature rather than a dozen.
    /// </para>
    /// </summary>
    public ObservableCollection<ComputedTrace> Computed { get; } = [];

    public bool HasComputed => Computed.Count > 0;

    /// <summary>What is typed in the expression box.</summary>
    [ObservableProperty]
    public partial string ExpressionText { get; set; } = string.Empty;

    /// <summary>What to call the result, or blank to use the expression itself.</summary>
    [ObservableProperty]
    public partial string ExpressionLabel { get; set; } = string.Empty;

    /// <summary>What is wrong with what has been typed, or empty when nothing is.</summary>
    [ObservableProperty]
    public partial string ExpressionProblem { get; private set; } = string.Empty;

    public bool HasExpressionProblem => ExpressionProblem.Length > 0;

    partial void OnExpressionTextChanged(string value)
    {
        ExpressionProblem = value.Trim().Length == 0
            ? string.Empty
            : TraceExpression.Validate(value, Probes.Select(p => p.Label)) ?? string.Empty;

        OnPropertyChanged(nameof(HasExpressionProblem));
    }

    /// <summary>
    /// Adds the expression as a trace. Refused rather than added broken: a computed trace that
    /// cannot be worked out would be an empty line on the plot with nothing to say why.
    /// </summary>
    [RelayCommand]
    private void AddComputed()
    {
        var expression = ExpressionText.Trim();

        if (expression.Length == 0)
        {
            ExpressionProblem = "Type an expression — Out / In, or {DC out} - {AC in}.";
            OnPropertyChanged(nameof(HasExpressionProblem));
            return;
        }

        if (TraceExpression.Validate(expression, Probes.Select(p => p.Label)) is { } problem)
        {
            ExpressionProblem = problem;
            OnPropertyChanged(nameof(HasExpressionProblem));
            return;
        }

        var label = ExpressionLabel.Trim();
        if (label.Length == 0) label = expression;

        Computed.Add(new ComputedTrace(label, expression, NextComputedColour()));

        ExpressionText = string.Empty;
        ExpressionLabel = string.Empty;
        ExpressionProblem = string.Empty;

        OnPropertyChanged(nameof(HasComputed));
        OnPropertyChanged(nameof(HasExpressionProblem));
    }

    [RelayCommand]
    private void RemoveComputed(ComputedTrace? trace)
    {
        if (trace is null || !Computed.Remove(trace)) return;

        OnPropertyChanged(nameof(HasComputed));
    }

    /// <summary>
    /// Works out one computed trace against what the probes have recorded. Empty when it cannot
    /// be — a trace it needs may have been removed since it was added.
    /// </summary>
    public IReadOnlyList<Cirq.Core.Primitives.DataPoint> Samples(ComputedTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);

        var traces = Probes.ToDictionary(
            p => p.Label,
            p => (IReadOnlyList<Cirq.Core.Primitives.DataPoint>)p.HistoryBuffer.ToArray(),
            StringComparer.OrdinalIgnoreCase);

        try
        {
            return TraceExpression.Evaluate(trace.Expression, traces);
        }
        catch (ExpressionException)
        {
            return [];
        }
    }

    /// <summary>
    /// Colours that are not any probe's, so a computed trace never looks like a recorded one.
    /// </summary>
    private Cirq.Core.Primitives.Color NextComputedColour()
    {
        Cirq.Core.Primitives.Color[] palette =
        [
            new(0xFF, 0xFF, 0xFF, 0xFF),
            new(0xFF, 0xB8, 0x92, 0xFF),
            new(0xFF, 0x7F, 0xFF, 0xD4),
            new(0xFF, 0xFF, 0xC4, 0x7F),
        ];

        return palette[Computed.Count % palette.Length];
    }

    public bool HasReferences => References.Count > 0;

    /// <summary>What the toolbar button says about them.</summary>
    public string ReferenceSummary => References.Count switch
    {
        0 => "No reference",
        1 => "1 reference",
        var n => $"{n} references",
    };

    /// <summary>
    /// Takes a copy of every visible trace as it stands. Copied rather than referenced: the
    /// probes keep recording, and a reference that moved with them would not be one.
    /// </summary>
    [RelayCommand]
    private void CaptureReference()
    {
        var taken = DateTimeOffset.UtcNow;
        var added = 0;

        foreach (var probe in Probes.Where(p => p.IsVisible))
        {
            var samples = probe.HistoryBuffer.ToArray();
            if (samples.Length < 2) continue;

            References.Add(new ReferenceTrace(probe.Label, probe.TraceColor, samples, taken));
            added++;
        }

        if (added > 0) ReferencesChanged();
    }

    /// <summary>Throws the captured traces away.</summary>
    [RelayCommand]
    private void ClearReferences()
    {
        if (References.Count == 0) return;

        References.Clear();
        ReferencesChanged();
    }

    private void ReferencesChanged()
    {
        OnPropertyChanged(nameof(HasReferences));
        OnPropertyChanged(nameof(ReferenceSummary));
    }

    /// <summary>Layout choices offered by the scope toolbar.</summary>
    public static IReadOnlyList<ScopeLayout> LayoutOptions { get; } =
        Enum.GetValues<ScopeLayout>();

    /// <summary>
    /// Which trace goes on the horizontal axis in XY mode. Null means the first visible one, which
    /// is what somebody switching to XY with two traces up almost always wants.
    /// </summary>
    [ObservableProperty]
    public partial SignalProbe? XyHorizontal { get; set; }

    /// <summary>True while the scope is plotting one trace against another rather than against time.</summary>
    public bool IsXy => Layout == ScopeLayout.Xy;

    partial void OnLayoutChanged(ScopeLayout value) => OnPropertyChanged(nameof(IsXy));

    /// <summary>
    /// The pairs to draw in XY mode: the trace on the horizontal axis, and each other visible
    /// trace against it.
    /// </summary>
    public (SignalProbe Horizontal, IReadOnlyList<SignalProbe> Vertical) XyPairs()
    {
        var visible = Probes.Where(p => p.IsVisible).ToList();

        if (visible.Count == 0) return (new SignalProbe(), []);

        var horizontal = XyHorizontal is { } chosen && visible.Contains(chosen)
            ? chosen
            : visible[0];

        return (horizontal, [.. visible.Where(p => !ReferenceEquals(p, horizontal))]);
    }

    /// <summary>
    /// Resamples one trace against another over a window, pairing them by time rather than by
    /// position in their buffers.
    /// <para>
    /// By time, because the two buffers need not line up: a probe added later is shorter, and a
    /// probe whose kind was changed has been cleared. Interpolating the vertical trace at each of
    /// the horizontal one's sample times is right whatever the two have been through.
    /// </para>
    /// </summary>
    public static (double[] X, double[] Y) XySeries(
        SignalProbe horizontal, SignalProbe vertical, double from, double to)
    {
        var xs = horizontal.HistoryBuffer.ToArray();
        var ys = vertical.HistoryBuffer.ToArray();

        if (xs.Length < 2 || ys.Length < 2) return ([], []);

        List<double> x = [];
        List<double> y = [];

        foreach (var sample in xs)
        {
            if (sample.Time < from) continue;
            if (sample.Time > to) break;

            if (ValueAt(ys, sample.Time) is not { } paired) continue;

            x.Add(sample.Value);
            y.Add(paired);
        }

        // Fewer than two paired points is not a curve, and ScottPlot would rather not be handed
        // a single one.
        if (x.Count < 2) return ([], []);

        return ([.. x], [.. y]);
    }

    /// <summary>Seconds per horizontal division.</summary>
    [ObservableProperty]
    public partial double TimebasePerDivision { get; set; } = 1e-4;

    /// <summary>Volts per vertical division.</summary>
    [ObservableProperty]
    public partial double VoltsPerDivision { get; set; } = 2.0;

    [ObservableProperty]
    public partial ScopeLayout Layout { get; set; } = ScopeLayout.Unified;

    /// <summary>When true the view scrolls to keep the newest sample at the right edge.</summary>
    [ObservableProperty]
    public partial bool AutoScroll { get; set; } = true;

    /// <summary>Applies AC coupling to every trace at once.</summary>
    [ObservableProperty]
    public partial bool AcCoupleAll { get; set; }

    [ObservableProperty]
    public partial SignalProbe? SelectedProbe { get; set; }

    // ---- triggering ------------------------------------------------------

    /// <summary>
    /// When the capture starts.
    /// <para>
    /// Off follows the newest samples, which is what this scope has always done and is right for
    /// watching a circuit settle. Anything repeating wants Auto or Normal, which line the same
    /// feature of the waveform up in the same place every repaint so it stands still; anything that
    /// happens once wants Single.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial TriggerMode TriggerMode { get; set; } = TriggerMode.Off;

    /// <summary>
    /// The trace the edge is looked for on. Null takes the first visible one, so turning triggering
    /// on does something sensible before anything has been chosen.
    /// </summary>
    [ObservableProperty]
    public partial SignalProbe? TriggerProbe { get; set; }

    /// <summary>The value the trigger signal has to cross, in its own units.</summary>
    [ObservableProperty]
    public partial double TriggerLevel { get; set; }

    [ObservableProperty]
    public partial TriggerSlope TriggerSlope { get; set; } = TriggerSlope.Rising;

    /// <summary>
    /// Where along the screen the trigger sits, as a fraction of the window. A fifth of the way in
    /// by default, so there is a little of what led up to the edge as well as what followed it —
    /// which is most of why a scope has a trigger at all rather than a start button.
    /// </summary>
    [ObservableProperty]
    public partial double TriggerPosition { get; set; } = 0.2;

    public static IReadOnlyList<TriggerMode> TriggerModes { get; } = Enum.GetValues<TriggerMode>();

    public static IReadOnlyList<TriggerSlope> TriggerSlopes { get; } = Enum.GetValues<TriggerSlope>();

    /// <summary>The instant the displayed capture is lined up on, or null when nothing has been found.</summary>
    [ObservableProperty]
    public partial double? TriggeredAt { get; private set; }

    /// <summary>True once a single-shot capture has happened, which is what holds it on screen.</summary>
    [ObservableProperty]
    public partial bool HasCaptured { get; private set; }

    /// <summary>Raised when a single-shot trigger fires, so the run can be stopped on it.</summary>
    public event EventHandler? SingleShotCaptured;

    private double _armedAt;
    private double _heldStart;

    /// <summary>
    /// Waits for the next edge. Single-shot only: the other modes are always looking.
    /// </summary>
    [RelayCommand]
    public void Arm()
    {
        _armedAt = Probes.Count == 0 ? 0 : Probes.Max(p => p.HistoryBuffer.Count == 0
            ? 0
            : p.HistoryBuffer[p.HistoryBuffer.Count - 1].Time);

        HasCaptured = false;
        TriggeredAt = null;

        OnPropertyChanged(nameof(TriggerStatus));
    }

    /// <summary>What the trigger is doing, in the words a scope's front panel uses.</summary>
    public string TriggerStatus => TriggerMode switch
    {
        TriggerMode.Off => "Following the newest samples",
        TriggerMode.Single when HasCaptured => $"Captured at {SiPrefix.Format(TriggeredAt ?? 0, "s", 4)}",
        TriggerMode.Single => "Armed — waiting for an edge",
        _ when TriggeredAt is { } at => $"Triggered at {SiPrefix.Format(at, "s", 4)}",
        TriggerMode.Normal => "Waiting for an edge",
        _ => "No edge yet — free running",
    };

    /// <summary>
    /// Where the visible window starts, given the newest sample in the circuit.
    /// <para>
    /// The one place the trigger is actually applied. An edge is only worth lining up on once
    /// everything that follows it on screen has been recorded — otherwise the picture grows to the
    /// right as the samples arrive, which is the sliding a trigger is there to stop — so the search
    /// is limited to edges at least the post-trigger part of the window old.
    /// </para>
    /// </summary>
    public double WindowStart(double latest)
    {
        var window = WindowSeconds;
        var free = AutoScroll ? Math.Max(0, latest - window) : 0;

        if (TriggerMode == TriggerMode.Off)
        {
            TriggeredAt = null;
            return free;
        }

        var probe = TriggerProbe ?? Probes.FirstOrDefault(p => p.IsVisible);

        if (probe is null) return free;

        // Held: a single-shot capture is a photograph, and it does not move afterwards.
        if (TriggerMode == TriggerMode.Single && HasCaptured)
            return TriggeredAt is { } held ? Start(held, window) : _heldStart;

        var post = window * (1.0 - TriggerPosition);

        var found = ScopeTrigger.Find(
            probe.HistoryBuffer,
            TriggerLevel,
            TriggerSlope,
            noLaterThan: latest - post,
            after: TriggerMode == TriggerMode.Single ? _armedAt : null,
            // Two percent of the screen. Noise on a slow edge crosses the level many times, and
            // without a band to fall back past, the display jumps between those crossings.
            hysteresis: VoltageSpan * 0.02);

        if (found is not { } edge)
        {
            // Normal holds its last capture rather than showing something that did not trigger;
            // Auto gives up and free-runs, which is what makes it the mode to leave a scope in.
            TriggeredAt = null;
            OnPropertyChanged(nameof(TriggerStatus));

            return TriggerMode == TriggerMode.Auto ? free : _heldStart;
        }

        TriggeredAt = edge;
        _heldStart = Start(edge, window);

        if (TriggerMode == TriggerMode.Single && !HasCaptured)
        {
            HasCaptured = true;
            SingleShotCaptured?.Invoke(this, EventArgs.Empty);
        }

        OnPropertyChanged(nameof(TriggerStatus));

        return _heldStart;
    }

    private double Start(double edge, double window) => Math.Max(0, edge - (window * TriggerPosition));

    partial void OnTriggerModeChanged(TriggerMode value)
    {
        TriggeredAt = null;
        HasCaptured = false;

        if (value == TriggerMode.Single) Arm();

        OnPropertyChanged(nameof(IsTriggering));
        OnPropertyChanged(nameof(IsSingleShot));
        OnPropertyChanged(nameof(TriggerStatus));
    }

    /// <summary>True whenever an edge is being looked for, which shows the rest of the controls.</summary>
    public bool IsTriggering => TriggerMode != TriggerMode.Off;

    /// <summary>True in single-shot, which is the only mode with something to arm.</summary>
    public bool IsSingleShot => TriggerMode == TriggerMode.Single;

    /// <summary>Total time span shown across the scope face, in seconds.</summary>
    public double WindowSeconds => TimebasePerDivision * HorizontalDivisions;

    /// <summary>Full vertical span, in volts.</summary>
    public double VoltageSpan => VoltsPerDivision * VerticalDivisions;

    /// <summary>
    /// Extra vertical range, as a fraction of the nominal span, so a trace at full scale is not
    /// clipped. Without it a signal whose peak lands exactly on a range boundary — a 4 Vpp square
    /// wave on a 1 V/div setting, say — rides along the axis frame, which is drawn after the data
    /// and shaves the top off the waveform.
    /// </summary>
    public const double TraceHeadroom = 0.04;

    /// <summary>The vertical range actually plotted: the nominal span plus a little headroom.</summary>
    public double DisplayVoltageSpan => VoltageSpan * (1.0 + TraceHeadroom);

    /// <summary>Half the plotted range, which is what the axis limits are set to.</summary>
    public double DisplayVoltageHalfSpan => DisplayVoltageSpan / 2.0;

    /// <summary>
    /// When set, the vertical range and position follow the traces, so changing a source amplitude
    /// keeps the waveform on screen instead of quietly running off the top. Turned off the moment
    /// the vertical controls are used by hand, because that is the user taking over the knob.
    /// </summary>
    [ObservableProperty]
    public partial bool AutoScaleVertical { get; set; } = true;

    /// <summary>Voltage at the middle of the display, the equivalent of a scope's position knob.</summary>
    [ObservableProperty]
    public partial double VerticalCenter { get; set; }

    /// <summary>Lowest voltage currently on screen.</summary>
    public double DisplayMinimum => VerticalCenter - DisplayVoltageHalfSpan;

    /// <summary>Highest voltage currently on screen.</summary>
    public double DisplayMaximum => VerticalCenter + DisplayVoltageHalfSpan;

    /// <summary>
    /// Refits the vertical range around the observed data, returning true when anything moved.
    /// <para>
    /// Deliberately sticky: the range is only changed when the trace no longer fits, or when it has
    /// shrunk to use less than a third of the screen. Refitting on every frame would make the axis
    /// hunt between two neighbouring settings whenever a signal sat near a boundary.
    /// </para>
    /// </summary>
    public bool AutoScaleTo(double minimum, double maximum)
    {
        if (!AutoScaleVertical) return false;
        if (double.IsNaN(minimum) || double.IsNaN(maximum) || minimum > maximum) return false;

        var halfRange = Math.Max((maximum - minimum) / 2.0, 1e-9);
        var half = DisplayVoltageHalfSpan;

        var fitsWithRoomToSpare =
            minimum > DisplayMinimum + half * 0.02 &&
            maximum < DisplayMaximum - half * 0.02 &&
            halfRange > half * 0.33;

        if (fitsWithRoomToSpare) return false;

        // Leave about 10% of the span free so the trace never touches the frame.
        VoltsPerDivision = NextNiceStep(halfRange * 2.2 / VerticalDivisions);
        VerticalCenter = RoundToward((minimum + maximum) / 2.0, VoltsPerDivision / 4.0);
        return true;
    }

    /// <summary>Smallest value from the 1-2-5 sequence that is at least <paramref name="value"/>.</summary>
    public static double NextNiceStep(double value)
    {
        value = Math.Max(value, 1e-9);
        var exponent = Math.Floor(Math.Log10(value));
        var magnitude = Math.Pow(10, exponent);

        foreach (var step in new[] { 1.0, 2.0, 5.0 })
            if (step * magnitude >= value * 0.999999)
                return step * magnitude;

        return 10.0 * magnitude;
    }

    /// <summary>Snaps the centre to a fraction of a division so it does not drift by tiny amounts.</summary>
    private static double RoundToward(double value, double step) =>
        step <= 0 ? value : Math.Round(value / step) * step;

    /// <summary>
    /// Simulated seconds between recorded probe samples. Chosen so one screen's worth of trace
    /// fills roughly the buffer, which keeps a nanosecond-step circuit from overrunning history in
    /// microseconds of simulated time.
    /// </summary>
    public double SuggestedSampleInterval => WindowSeconds / 4000.0;

    /// <summary>Raised when a setting changes that the engine needs to know about.</summary>
    public event EventHandler? SamplingChanged;

    // ---- measurements and cursors ----------------------------------------

    /// <summary>
    /// True while the trace list is showing what each trace is doing rather than only its present
    /// value. Off by default: the rows are compact, and a number under every trace is noise until
    /// it is the number you want.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowMeasurements { get; set; }

    /// <summary>
    /// True while the two time cursors are on the plot. They are how you measure something the
    /// automatic readouts do not cover — the gap between two unrelated edges, the width of one
    /// pulse in a burst, how long a relay took to pick up.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowCursors { get; set; }

    /// <summary>Where the first cursor sits, in seconds.</summary>
    [ObservableProperty]
    public partial double CursorA { get; set; }

    /// <summary>Where the second cursor sits, in seconds.</summary>
    [ObservableProperty]
    public partial double CursorB { get; set; }

    /// <summary>True once the cursors have been placed, rather than sitting on top of each other.</summary>
    private bool _cursorsPlaced;

    /// <summary>What the cursors say, written out for the strip under the plot.</summary>
    [ObservableProperty]
    public partial string CursorReadout { get; private set; } = string.Empty;

    /// <summary>
    /// Re-measures every trace over the window on screen, and re-reads the cursors.
    /// <para>
    /// Called from the panel's redraw timer rather than from the engine: a measurement is of what
    /// is being displayed, and the display is what has the window. Doing it on the solver's
    /// schedule would measure a hundred thousand times a second to update a label twenty-five
    /// times a second.
    /// </para>
    /// </summary>
    public void Measure(double start, double window)
    {
        var end = start + window;

        foreach (var probe in Probes)
            probe.SetMeasurements(
                probe.IsVisible
                    ? TraceMeasurements.Of(probe.HistoryBuffer.ToArray(), start, end)
                    : TraceMeasurements.None);

        if (!ShowCursors)
        {
            CursorReadout = string.Empty;
            _cursorsPlaced = false;
            return;
        }

        // First time on, put them a third and two thirds across, which is where a person would.
        if (!_cursorsPlaced)
        {
            CursorA = start + (window / 3.0);
            CursorB = start + (window * 2.0 / 3.0);
            _cursorsPlaced = true;
        }

        CursorReadout = ReadCursors();
    }

    /// <summary>
    /// The cursor strip: the gap between them, what that is as a frequency, and what each visible
    /// trace was doing at each one.
    /// <para>
    /// The reciprocal is there because it is what the cursors are most often used for. Straddle
    /// one cycle of anything and the answer to "what frequency is that" is 1/Δt, and doing that
    /// division by hand at the screen is the tedious part.
    /// </para>
    /// </summary>
    private string ReadCursors()
    {
        var delta = CursorB - CursorA;

        List<string> parts =
        [
            $"A {SiPrefix.Format(CursorA, "s")}",
            $"B {SiPrefix.Format(CursorB, "s")}",
            $"Δt {SiPrefix.Format(Math.Abs(delta), "s")}",
        ];

        if (Math.Abs(delta) > 1e-15)
            parts.Add($"1/Δt {SiPrefix.Format(1.0 / Math.Abs(delta), "Hz")}");

        foreach (var probe in Probes.Where(p => p.IsVisible))
        {
            var samples = probe.HistoryBuffer.ToArray();

            if (ValueAt(samples, CursorA) is not { } a) continue;
            if (ValueAt(samples, CursorB) is not { } b) continue;

            parts.Add($"{probe.Label} Δ {SiPrefix.Format(b - a, probe.Unit)}");
        }

        return string.Join("   ·   ", parts);
    }

    /// <summary>
    /// What a trace was at one instant, interpolated between the samples either side. A cursor
    /// lands between samples far more often than on one, and snapping to the nearest would make
    /// the reading jump about as the trace scrolls underneath it.
    /// </summary>
    public static double? ValueAt(IReadOnlyList<Cirq.Core.Primitives.DataPoint> samples, double time)
    {
        if (samples.Count == 0) return null;
        if (time < samples[0].Time || time > samples[^1].Time) return null;

        for (var i = 1; i < samples.Count; i++)
        {
            if (samples[i].Time < time) continue;

            var previous = samples[i - 1];
            var span = samples[i].Time - previous.Time;

            return span <= 0
                ? samples[i].Value
                : previous.Value + ((time - previous.Time) / span * (samples[i].Value - previous.Value));
        }

        return samples[^1].Value;
    }

    /// <summary>Puts the cursors back where they would have started, for the Reset button.</summary>
    [RelayCommand]
    public void ResetCursors() => _cursorsPlaced = false;

    public SignalProbe AddProbe(Terminal terminal, string? label = null)
    {
        var existing = Probes.FirstOrDefault(p => terminal.Equals(p.TargetTerminal));
        if (existing is not null)
        {
            SelectedProbe = existing;
            return existing;
        }

        var colour = Color.ProbePalette[_paletteIndex++ % Color.ProbePalette.Count];
        var probe = new SignalProbe(label ?? terminal.ToString(), terminal, colour);
        Probes.Add(probe);
        SelectedProbe = probe;
        return probe;
    }

    [RelayCommand]
    public void RemoveProbe(SignalProbe? probe)
    {
        if (probe is null) return;
        Probes.Remove(probe);
        if (ReferenceEquals(SelectedProbe, probe)) SelectedProbe = Probes.FirstOrDefault();
    }

    [RelayCommand]
    public void ClearProbes()
    {
        Probes.Clear();
        SelectedProbe = null;
    }

    [RelayCommand]
    public void ClearTraces()
    {
        foreach (var probe in Probes) probe.ResetHistory();
    }

    [RelayCommand]
    private void ZoomTimeIn() => TimebasePerDivision = NextTimebase(TimebasePerDivision, -1);

    [RelayCommand]
    private void ZoomTimeOut() => TimebasePerDivision = NextTimebase(TimebasePerDivision, 1);

    [RelayCommand]
    private void ZoomVoltsIn()
    {
        AutoScaleVertical = false;
        VoltsPerDivision = NextTimebase(VoltsPerDivision, -1);
    }

    [RelayCommand]
    private void ZoomVoltsOut()
    {
        AutoScaleVertical = false;
        VoltsPerDivision = NextTimebase(VoltsPerDivision, 1);
    }

    /// <summary>Steps through the classic 1-2-5 sequence used by bench instrument dials.</summary>
    private static double NextTimebase(double current, int direction)
    {
        var exponent = Math.Floor(Math.Log10(current));
        var mantissa = current / Math.Pow(10, exponent);

        double[] steps = [1, 2, 5];
        var index = mantissa < 1.5 ? 0 : mantissa < 3.5 ? 1 : 2;

        index += direction;
        if (index > 2)
        {
            index = 0;
            exponent++;
        }
        else if (index < 0)
        {
            index = 2;
            exponent--;
        }

        return Math.Clamp(steps[index] * Math.Pow(10, exponent), 1e-12, 100);
    }

    partial void OnTimebasePerDivisionChanged(double value)
    {
        OnPropertyChanged(nameof(WindowSeconds));
        OnPropertyChanged(nameof(SuggestedSampleInterval));
        OnPropertyChanged(nameof(TimebaseLabel));
        SamplingChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnVoltsPerDivisionChanged(double value)
    {
        OnPropertyChanged(nameof(VoltageSpan));
        OnPropertyChanged(nameof(DisplayVoltageSpan));
        OnPropertyChanged(nameof(DisplayVoltageHalfSpan));
        OnPropertyChanged(nameof(DisplayMinimum));
        OnPropertyChanged(nameof(DisplayMaximum));
        OnPropertyChanged(nameof(VoltsLabel));
    }

    partial void OnVerticalCenterChanged(double value)
    {
        OnPropertyChanged(nameof(DisplayMinimum));
        OnPropertyChanged(nameof(DisplayMaximum));
        OnPropertyChanged(nameof(VoltsLabel));
    }

    partial void OnAcCoupleAllChanged(bool value)
    {
        foreach (var probe in Probes) probe.AcCoupled = value;
    }

    public string TimebaseLabel => $"{Cirq.Core.Units.SiPrefix.Format(TimebasePerDivision, "s")}/div";

    public string VoltsLabel => $"{Cirq.Core.Units.SiPrefix.Format(VoltsPerDivision, "V")}/div";
}
