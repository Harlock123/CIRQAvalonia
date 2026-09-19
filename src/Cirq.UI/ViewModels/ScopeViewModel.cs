using System.Collections.ObjectModel;
using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
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
}

/// <summary>
/// The oscilloscope panel's state: which probes are shown, the timebase and vertical scaling, and
/// the sample interval the engine should decimate its probe recording to.
/// </summary>
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

    /// <summary>Layout choices offered by the scope toolbar.</summary>
    public static IReadOnlyList<ScopeLayout> LayoutOptions { get; } =
        Enum.GetValues<ScopeLayout>();

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
