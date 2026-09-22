using System.Globalization;
using System.Text;

namespace Cirq.Core.Probing;

/// <summary>
/// Writes recorded traces out as CSV, so they can go somewhere else.
/// <para>
/// Everything the scope shows is already a measurement; this is how it leaves. A spreadsheet to
/// fit a curve, a script to compare two runs, a report that wants the numbers rather than a
/// picture of them — none of that was possible with five image formats.
/// </para>
/// <para>
/// The awkward part is that the probes do <b>not</b> share a time axis. Each records when the
/// solver happened to accept a time point, a probe added later starts later, and one whose kind
/// was changed was cleared. So the rows are built on a <b>union</b> of every probe's sample times,
/// with each column interpolated where it has no sample of its own — which is the same thing the
/// XY plot does, and for the same reason. A blank would be honest too, but a column full of gaps
/// is painful in every tool that reads CSV.
/// </para>
/// </summary>
public static class TraceCsv
{
    /// <summary>
    /// Writes every visible probe over a time window.
    /// </summary>
    /// <param name="probes">The probes. Hidden ones are left out, as they are on the plot.</param>
    /// <param name="from">Start of the window, in seconds.</param>
    /// <param name="to">End of it.</param>
    /// <param name="maximumRows">
    /// A ceiling on the rows written, decimating evenly if the window holds more. A million points
    /// is a file most spreadsheets will not open, and a curve does not need them.
    /// </param>
    public static string Write(
        IReadOnlyList<SignalProbe> probes, double from, double to, int maximumRows = 20_000)
    {
        ArgumentNullException.ThrowIfNull(probes);

        var visible = probes.Where(p => p.IsVisible).ToList();
        var series = visible.Select(p => p.HistoryBuffer.ToArray()).ToList();

        var times = SampleTimes(series, from, to, maximumRows);

        var csv = new StringBuilder();

        // A header naming each column with its unit, because a column of numbers whose unit is
        // somewhere else is a column of numbers somebody will misread.
        csv.Append("time_s");

        for (var i = 0; i < visible.Count; i++)
        {
            var unit = visible[i].Unit;

            csv.Append(',').Append(Escape(
                unit.Length > 0 ? $"{visible[i].Label} ({unit})" : visible[i].Label));
        }

        csv.AppendLine();

        foreach (var time in times)
        {
            csv.Append(Number(time));

            for (var i = 0; i < visible.Count; i++)
            {
                csv.Append(',');

                if (ValueAt(series[i], time) is { } value) csv.Append(Number(value));
            }

            csv.AppendLine();
        }

        return csv.ToString();
    }

    /// <summary>How many rows <see cref="Write"/> would produce, without building the file.</summary>
    public static int RowCount(
        IReadOnlyList<SignalProbe> probes, double from, double to, int maximumRows = 20_000)
    {
        var series = probes.Where(p => p.IsVisible).Select(p => p.HistoryBuffer.ToArray()).ToList();

        return SampleTimes(series, from, to, maximumRows).Count;
    }

    /// <summary>
    /// The union of every probe's sample times inside the window, in order and without duplicates,
    /// decimated evenly if there are more than asked for.
    /// </summary>
    private static List<double> SampleTimes(
        List<Primitives.DataPoint[]> series, double from, double to, int maximumRows)
    {
        SortedSet<double> union = [];

        // The window's ends are compared with a tolerance, because they are floating-point
        // quantities that arrived by different routes: the end comes from a timebase, and a
        // sample's time from an accumulation of solver steps. A run of ten steps of 1e-4 lands at
        // 0.0010000000000000002, which is outside a window ending at 1e-3 by one part in 1e16 —
        // and dropping the last sample of every export over a rounding error is not a trade worth
        // making.
        var slack = Math.Max(Math.Abs(to - from), 1.0) * 1e-9;

        foreach (var samples in series)
            foreach (var sample in samples)
            {
                if (sample.Time < from - slack) continue;
                if (sample.Time > to + slack) break;

                union.Add(sample.Time);
            }

        var times = union.ToList();

        if (maximumRows <= 0 || times.Count <= maximumRows) return times;

        // Evenly spaced through the list rather than truncated: a file cut short at the row limit
        // would silently be a file of the first fraction of the run.
        List<double> decimated = [];

        for (var i = 0; i < maximumRows; i++)
            decimated.Add(times[(int)((long)i * (times.Count - 1) / (maximumRows - 1))]);

        return [.. decimated.Distinct()];
    }

    /// <summary>
    /// One trace's value at an instant, interpolated between the samples either side. Null outside
    /// its own recorded span, which leaves the cell empty rather than inventing a reading for a
    /// probe that was not attached yet.
    /// </summary>
    private static double? ValueAt(Primitives.DataPoint[] samples, double time)
    {
        if (samples.Length == 0) return null;

        // The same tolerance the window uses, and for the same reason: a time in the union can be
        // a rounding error past this trace's own last sample, and reading that as "no data" would
        // leave a blank in the final row of the very trace whose sample put the row there.
        // Genuinely outside — a probe attached halfway through the run — is still blank.
        var slack = Math.Max(samples[^1].Time - samples[0].Time, 1.0) * 1e-9;

        if (time < samples[0].Time - slack || time > samples[^1].Time + slack) return null;

        if (time <= samples[0].Time) return samples[0].Value;
        if (time >= samples[^1].Time) return samples[^1].Value;

        // Binary search for the sample at or before the time; the buffers are in order and a
        // linear scan per cell would be quadratic across a whole file.
        var low = 0;
        var high = samples.Length - 1;

        while (low < high)
        {
            var mid = (low + high + 1) / 2;

            if (samples[mid].Time <= time) low = mid;
            else high = mid - 1;
        }

        if (low >= samples.Length - 1) return samples[^1].Value;

        var a = samples[low];
        var b = samples[low + 1];
        var span = b.Time - a.Time;

        return span <= 0
            ? b.Value
            : a.Value + ((time - a.Time) / span * (b.Value - a.Value));
    }

    /// <summary>
    /// A number a spreadsheet and a script will both read the same way: invariant culture, so the
    /// decimal separator does not depend on where the file was written, and enough digits that a
    /// microvolt on a five volt rail survives.
    /// </summary>
    private static string Number(double value) =>
        value.ToString("G9", CultureInfo.InvariantCulture);

    /// <summary>A field quoted if it needs to be, which a probe's label might.</summary>
    private static string Escape(string field)
    {
        if (!field.Contains(',') && !field.Contains('"') && !field.Contains('\n')) return field;

        return $"\"{field.Replace("\"", "\"\"")}\"";
    }
}
