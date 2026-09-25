using System.Globalization;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Sources;

/// <summary>What happens when a stored waveform runs out.</summary>
public enum WaveformEnding
{
    /// <summary>Stay at the last value, which is what a step or a load change does.</summary>
    Hold,

    /// <summary>Start again from the beginning, which is what a repeating stimulus does.</summary>
    Repeat,

    /// <summary>Fall to zero, for a burst with silence after it.</summary>
    Zero,
}

/// <summary>
/// A voltage source that plays back a list of points rather than a shape from a menu.
/// <para>
/// The five shapes a function generator offers answer "what does this circuit do to a sine", and
/// a great many real questions are not that. A supply brownout, a startup ramp, a load step, a
/// measured waveform off a real bench, a few seconds of music through a filter: none of them is a
/// sine, a square, a triangle, a sawtooth or a constant, and until now none of them could be put
/// into a circuit here at all.
/// </para>
/// <para>
/// This is SPICE's PWL source, with the points kept as text so they are part of the document. A
/// source that referenced a file on disk would be a circuit that opens differently — or not at all
/// — on another machine, or on the same machine next month; a self-contained one always plays the
/// waveform it was saved with. Importing from a file therefore copies the samples in rather than
/// remembering where they came from.
/// </para>
/// <para>
/// Every corner is a solver breakpoint, which is the part that makes it accurate rather than
/// approximately accurate: without it the transient loop steps over a two-microsecond glitch
/// because nothing told it there was one, and the circuit is fed a waveform subtly unlike the one
/// on the screen.
/// </para>
/// </summary>
public partial class WaveformSource : TwoTerminalComponent, IBreakpointSource, IAcExcitation
{
    private double[] _times = [];
    private double[] _values = [];
    private string _compiledFrom = string.Empty;

    public WaveformSource() : base("OUT", "GND")
    {
    }

    /// <summary>
    /// The points, as <c>time value</c> pairs separated by semicolons or newlines. Times are in
    /// seconds and values in volts, and either may carry an SI prefix — <c>1m 4.5</c> is four and a
    /// half volts at a millisecond.
    /// <code>
    /// 0 0; 1m 0; 1.01m 5; 5m 5; 5.01m 0
    /// </code>
    /// <para>
    /// Out-of-order times are sorted rather than rejected: a table pasted from a spreadsheet that
    /// happens to be sorted by voltage is a mistake worth recovering from rather than an error
    /// worth stopping for.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial string Points { get; set; } = "0 0; 1m 0; 1.01m 5; 5m 5; 5.01m 0";

    /// <summary>What to do once the last point has been passed.</summary>
    [ObservableProperty]
    public partial WaveformEnding Ending { get; set; } = WaveformEnding.Hold;

    /// <summary>Multiplies every value. One leaves the table as it is.</summary>
    [ObservableProperty]
    [Operable("Scale", Minimum = -10, Maximum = 10)]
    public partial double Scale { get; set; } = 1.0;

    /// <summary>Added to every value after scaling, in volts.</summary>
    [ObservableProperty]
    [Operable("Offset", Minimum = -25, Maximum = 25, Unit = "V")]
    public partial double DcOffset { get; set; }

    /// <summary>
    /// Stretches the time axis. Two plays it at half speed; a half plays it twice as fast. This is
    /// what makes a waveform captured at one sample rate usable against a circuit at another.
    /// </summary>
    [ObservableProperty]
    [Operable("Time scale", Minimum = 0.01, Maximum = 100, IsLogarithmic = true)]
    public partial double TimeScale { get; set; } = 1.0;

    /// <summary>How long to wait before the first point, in seconds.</summary>
    [ObservableProperty]
    public partial double StartDelay { get; set; }

    /// <summary>Output impedance in ohms; zero is an ideal source.</summary>
    [ObservableProperty]
    public partial double OutputResistance { get; set; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    /// <summary>How hard it drives a frequency sweep, in volts.</summary>
    [ObservableProperty]
    public partial double AcMagnitude { get; set; } = 1.0;

    /// <summary>Phase of that excitation, in degrees.</summary>
    [ObservableProperty]
    public partial double AcPhaseDegrees { get; set; }

    public Terminal Output => A;

    public Terminal Return => B;

    public override string ComponentType => "Waveform Source";

    public override string DesignatorPrefix => "AWG";

    public override int VoltageSourceCount => 1;

    public override string ValueLabel
    {
        get
        {
            Compile();

            if (_times.Length == 0) return "empty";

            return $"{_times.Length} points · {SiPrefix.Format(Duration, "s")}";
        }
    }

    /// <summary>How long the stored waveform runs for, before any time scaling.</summary>
    public double Duration
    {
        get
        {
            Compile();

            return _times.Length == 0 ? 0 : _times[^1] - _times[0];
        }
    }

    /// <summary>How many points it is playing back.</summary>
    public int PointCount
    {
        get
        {
            Compile();

            return _times.Length;
        }
    }

    /// <summary>
    /// The output at a moment, interpolated between the two points either side of it.
    /// <para>
    /// Linear, and deliberately not smoother. A spline through measured samples invents overshoot
    /// that was never in the measurement, and the place it invents it is the sharp edge — which is
    /// the part of the waveform somebody imported a real capture to look at.
    /// </para>
    /// </summary>
    public double ValueAt(double time)
    {
        Compile();

        if (!IsEnabled || _times.Length == 0) return DcOffset;

        var scale = Math.Max(TimeScale, 1e-12);
        var t = (time - StartDelay) / scale;

        if (t <= _times[0]) return Shaped(_values[0]);

        var last = _times[^1];

        if (t > last)
        {
            switch (Ending)
            {
                case WaveformEnding.Hold: return Shaped(_values[^1]);
                case WaveformEnding.Zero: return DcOffset;

                default:
                    var span = last - _times[0];
                    if (span <= 0) return Shaped(_values[^1]);
                    t = _times[0] + ((t - _times[0]) % span);
                    break;
            }
        }

        var index = Array.BinarySearch(_times, t);
        if (index >= 0) return Shaped(_values[index]);

        var next = ~index;
        if (next <= 0) return Shaped(_values[0]);
        if (next >= _times.Length) return Shaped(_values[^1]);

        var step = _times[next] - _times[next - 1];
        var fraction = step <= 0 ? 0 : (t - _times[next - 1]) / step;

        return Shaped(_values[next - 1] + (fraction * (_values[next] - _values[next - 1])));
    }

    private double Shaped(double value) => (value * Scale) + DcOffset;

    public override void StampMatrix(MnaSystem system, SimulationState state) =>
        system.StampTheveninSource(
            system.Branch(this), system.Node(A), system.Node(B), ValueAt(state.Time), OutputResistance);

    /// <summary>
    /// A sweep asks what the circuit does to a small sine at each frequency, so the stored waveform
    /// means nothing here and all this contributes is how large that sine is.
    /// </summary>
    public override void StampAc(AcSystem system, SimulationState state) =>
        system.AddRhs(system.Branch(this), AcSystem.Phasor(AcMagnitude, AcPhaseDegrees));

    /// <summary>
    /// The next corner in the table, so the solver lands exactly on it.
    /// <para>
    /// This is what separates a waveform that is played from one that is approximated. Between
    /// breakpoints the transient loop is free to take a step as large as the local error allows,
    /// and a source that never declares a corner invites it to step straight over a short pulse —
    /// producing a smooth, plausible, entirely wrong answer.
    /// </para>
    /// </summary>
    public double? NextBreakpointAfter(double time)
    {
        Compile();

        if (!IsEnabled || _times.Length == 0) return null;

        var scale = Math.Max(TimeScale, 1e-12);
        var t = (time - StartDelay) / scale;
        var span = _times[^1] - _times[0];

        // How many whole passes have already been played, as a shift to add back to whatever the
        // table says. Folding the time into the first pass means one search serves both cases.
        var offset = 0.0;

        if (t >= _times[^1])
        {
            if (Ending != WaveformEnding.Repeat || span <= 0) return null;

            offset = Math.Floor((t - _times[0]) / span) * span;
            t -= offset;
        }

        foreach (var point in _times)
            if (point > t + 1e-15)
                return ((point + offset) * scale) + StartDelay;

        // Past the last corner of this pass, so the next one is the start of the next pass.
        return Ending == WaveformEnding.Repeat && span > 0
            ? ((_times[0] + offset + span) * scale) + StartDelay
            : null;
    }

    partial void OnPointsChanged(string value) => NotifyValueChanged();

    partial void OnScaleChanged(double value) => NotifyValueChanged();

    partial void OnTimeScaleChanged(double value) => NotifyValueChanged();

    partial void OnEndingChanged(WaveformEnding value) => NotifyValueChanged();

    // ---- the table ---------------------------------------------------------

    /// <summary>
    /// Parses <see cref="Points"/> into two arrays, once per change to the text.
    /// <para>
    /// Cached because this is read inside the Newton loop — several times per time point, of which
    /// there may be millions — and re-parsing a few thousand points there would dominate the
    /// solve. The guard is the text itself rather than a dirty flag, so a property set from
    /// anywhere, including the file loader and the undo stack, invalidates it.
    /// </para>
    /// </summary>
    private void Compile()
    {
        if (_compiledFrom == Points) return;

        _compiledFrom = Points;

        List<(double Time, double Value)> points = [];

        foreach (var entry in (Points ?? string.Empty).Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = entry.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2) continue;

            if (!Number(fields[0], out var time) || !Number(fields[1], out var value)) continue;

            points.Add((time, value));
        }

        // Sorted rather than trusted, because the interpolation binary-searches and a table out of
        // order would read as a waveform that jumps backwards in time. A stable sort keeps two
        // points at the same instant in the order they were written, which is how a table says
        // "step from here to there" without inventing a slope.
        var ordered = points.OrderBy(p => p.Time).ToList();

        _times = [.. ordered.Select(p => p.Time)];
        _values = [.. ordered.Select(p => p.Value)];
    }

    /// <summary>
    /// A field as a number, accepting an SI prefix. <c>1m</c> is a millisecond where a bare
    /// <c>0.001</c> would do, and a table written the way anybody would say it out loud is a table
    /// with fewer zeros to miscount.
    /// </summary>
    private static bool Number(string text, out double value) =>
        SiPrefix.TryParse(text, out value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Writes a list of samples out as a table, for whatever has just read a file.
    /// <para>
    /// The rounding is not cosmetic. A circuit file holding several thousand points at seventeen
    /// significant figures each is a circuit file that is mostly noise from the last bit of a
    /// double, and none of those digits survives being interpolated between anyway.
    /// </para>
    /// </summary>
    public static string Tabulate(IReadOnlyList<double> samples, double sampleInterval)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var text = new System.Text.StringBuilder(samples.Count * 18);

        for (var i = 0; i < samples.Count; i++)
        {
            if (i > 0) text.Append('\n');

            text.Append((i * sampleInterval).ToString("G7", CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(samples[i].ToString("G7", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }
}
