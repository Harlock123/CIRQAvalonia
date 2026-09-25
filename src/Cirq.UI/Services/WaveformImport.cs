using System.Globalization;
using Cirq.Components.Sources;
using Cirq.Core.Audio;

namespace Cirq.UI.Services;

/// <summary>What came out of a file, and what had to be done to it to fit.</summary>
/// <param name="Points">The table, ready to go into a <see cref="WaveformSource"/>.</param>
/// <param name="Count">How many points that is.</param>
/// <param name="Seconds">How long the waveform runs for.</param>
/// <param name="Note">
/// What was done that the reader should know about — a resampling, a column chosen, a normalise —
/// or empty when the file went in as it stood.
/// </param>
public sealed record ImportedWaveform(string Points, int Count, double Seconds, string Note);

/// <summary>
/// Turns a file of numbers into a waveform a circuit can be fed.
/// <para>
/// Two formats, because they are the two that exist in practice. A <b>CSV</b> is what a bench
/// scope, a data logger, a spreadsheet and this application's own trace export all produce. A
/// <b>WAV</b> is what audio is, and "does my filter actually clean this up" is a question about a
/// real recording rather than about a sine.
/// </para>
/// <para>
/// Both are copied into the circuit rather than referenced from it. A circuit that pointed at a
/// file on disk would open differently — or not at all — on another machine or next month, and a
/// stimulus that silently changes is worse than one that has to be re-imported.
/// </para>
/// </summary>
public static class WaveformImport
{
    /// <summary>
    /// How many points are kept, at most.
    /// <para>
    /// A second of CD audio is forty-four thousand samples and a saved circuit is a text file
    /// somebody may want to read. More than this and the document is mostly waveform; it is also
    /// more resolution than a transient at any sane time step will actually visit. Longer files are
    /// resampled rather than truncated, so what you get is the whole waveform at lower resolution
    /// rather than the first fraction of it at full — which is the difference between a quieter
    /// version of the sound and the first syllable of it.
    /// </para>
    /// </summary>
    public const int MaximumPoints = 8192;

    /// <summary>True when this is a file type that can be imported at all.</summary>
    public static bool CanRead(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".csv" or ".txt" or ".wav" or ".wave";

    /// <summary>
    /// Reads a file into a table of points.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="volts">
    /// What a full-scale sample becomes, for a WAV. Audio is stored as a fraction of full scale
    /// with no units at all, so something has to say what it is worth in volts.
    /// </param>
    public static ImportedWaveform Read(string path, double volts = 1.0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Path.GetExtension(path).ToLowerInvariant() is ".wav" or ".wave"
            ? FromWave(path, volts)
            : FromText(path);
    }

    private static ImportedWaveform FromWave(string path, double volts)
    {
        var (samples, rate) = WaveFile.Read(path);

        if (samples.Length == 0 || rate <= 0)
            throw new InvalidDataException("That file has no audio in it.");

        var interval = 1.0 / rate;
        var seconds = samples.Length * interval;

        var (kept, keptInterval, resampled) = Fit(samples, interval);

        var scaled = kept.Select(s => s * volts).ToArray();

        var note = resampled
            ? $"{samples.Length:N0} samples at {rate:N0} Hz, resampled to {kept.Count:N0} points — " +
              $"the whole {seconds:0.###} s at lower resolution rather than the start of it at full."
            : $"{kept.Count:N0} samples at {rate:N0} Hz.";

        return new ImportedWaveform(
            WaveformSource.Tabulate(scaled, keptInterval), kept.Count, seconds, note);
    }

    /// <summary>
    /// Reads a table of numbers: one column of values, or two of time and value.
    /// <para>
    /// Two columns are taken as time and value, which is what this application's own trace export
    /// writes and what every scope writes. One column is taken as values at a uniform interval,
    /// because a column of numbers out of a spreadsheet usually has no time in it at all.
    /// </para>
    /// <para>
    /// A header row is skipped by failing to parse rather than by being recognised, which handles
    /// every spelling of one without a list of them.
    /// </para>
    /// </summary>
    private static ImportedWaveform FromText(string path)
    {
        List<double> times = [];
        List<double> values = [];
        var columns = 0;

        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;

            var fields = line.Split([',', ';', '\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0) continue;

            if (fields.Length >= 2 && Number(fields[0], out var first) && Number(fields[1], out var second))
            {
                times.Add(first);
                values.Add(second);
                columns = 2;
                continue;
            }

            if (Number(fields[0], out var only))
            {
                values.Add(only);
                columns = Math.Max(columns, 1);
            }
        }

        if (values.Count < 2)
            throw new InvalidDataException(
                "That file has no pairs of numbers in it. A waveform needs at least two points — " +
                "either one column of values, or two of time and value.");

        if (columns == 2)
        {
            var ordered = times.Zip(values).OrderBy(p => p.First).ToList();
            var seconds = ordered[^1].First - ordered[0].First;

            var thinned = Thin(ordered);

            var text = string.Join('\n', thinned.Select(p =>
                $"{p.First.ToString("G7", CultureInfo.InvariantCulture)} " +
                $"{p.Second.ToString("G7", CultureInfo.InvariantCulture)}"));

            return new ImportedWaveform(
                text, thinned.Count, seconds,
                thinned.Count < ordered.Count
                    ? $"{ordered.Count:N0} rows thinned to {thinned.Count:N0} points."
                    : $"{thinned.Count:N0} points over {seconds:0.###} s.");
        }

        // One column: a uniform millisecond apart, which is a stated assumption rather than a
        // guess dressed up as a measurement — the note says so, and the time scale adjusts it.
        const double interval = 1e-3;

        var (kept, keptInterval, _) = Fit(values, interval);

        return new ImportedWaveform(
            WaveformSource.Tabulate(kept, keptInterval),
            kept.Count,
            kept.Count * keptInterval,
            $"{values.Count:N0} values in one column, taken as 1 ms apart. " +
            "Set the source's Time Scale if they were not.");
    }

    /// <summary>
    /// Thins a run of samples to at most <see cref="MaximumPoints"/>, keeping the length.
    /// </summary>
    private static (IReadOnlyList<double> Kept, double Interval, bool Resampled) Fit(
        IReadOnlyList<double> samples, double interval)
    {
        if (samples.Count <= MaximumPoints) return (samples, interval, false);

        var stride = (double)samples.Count / MaximumPoints;

        List<double> kept = new(MaximumPoints);

        // Averaged over each stride rather than sampled at it. Picking one sample in every six
        // aliases: a waveform with anything above the new Nyquist rate in it comes back as a
        // different, lower-frequency waveform, which looks perfectly plausible and is not what was
        // in the file. Averaging is a crude anti-alias filter, and crude is enough here.
        for (var i = 0; i < MaximumPoints; i++)
        {
            var from = (int)(i * stride);
            var to = Math.Min((int)((i + 1) * stride), samples.Count);

            if (to <= from) to = Math.Min(from + 1, samples.Count);
            if (from >= samples.Count) break;

            var sum = 0.0;
            for (var j = from; j < to; j++) sum += samples[j];

            kept.Add(sum / (to - from));
        }

        return (kept, interval * stride, true);
    }

    /// <summary>The same thinning for points that carry their own times.</summary>
    private static List<(double First, double Second)> Thin(List<(double First, double Second)> points) =>
        points.Count <= MaximumPoints
            ? points
            : [.. Enumerable.Range(0, MaximumPoints)
                .Select(i => points[(int)((long)i * points.Count / MaximumPoints)])];

    private static bool Number(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
