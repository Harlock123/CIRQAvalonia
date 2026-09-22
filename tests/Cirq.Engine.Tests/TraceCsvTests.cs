using System.Globalization;
using Cirq.Core.Primitives;
using Cirq.Core.Probing;

namespace Cirq.Engine.Tests;

/// <summary>
/// Writing recorded traces out as CSV. The awkward part is that probes do not share a time axis,
/// so most of these are about what happens where one has no sample of its own.
/// </summary>
public class TraceCsvTests
{
    private static SignalProbe Probe(
        string label, ProbeKind kind, Func<double, double> signal,
        double from = 0, double to = 1e-3, double step = 1e-5, int capacity = 10_000)
    {
        var probe = new SignalProbe
        {
            Label = label,
            Kind = kind,
            HistoryBuffer = new CircularBuffer<DataPoint>(capacity),
        };

        for (var t = from; t <= to + step / 2; t += step) probe.Record(t, signal(t));

        return probe;
    }

    private static (string[] Header, string[][] Rows) Parse(string csv)
    {
        var lines = csv.Split('\n').Where(l => l.Trim().Length > 0).ToArray();

        return (lines[0].Split(','), [.. lines.Skip(1).Select(l => l.Split(','))]);
    }

    private static double Field(string[] row, int column) =>
        double.Parse(row[column], CultureInfo.InvariantCulture);

    // ---- the basics --------------------------------------------------------

    [Fact]
    public void TheHeaderNamesEachColumnWithItsUnit()
    {
        var csv = TraceCsv.Write(
            [Probe("Out", ProbeKind.Voltage, t => t),
             Probe("Supply", ProbeKind.Current, t => t),
             Probe("Burn", ProbeKind.Power, t => t),
             Probe("Clock", ProbeKind.Logic, t => 0)],
            0, 1e-3);

        var (header, _) = Parse(csv);

        Assert.Equal("time_s", header[0]);
        Assert.Equal("Out (V)", header[1]);
        Assert.Equal("Supply (A)", header[2]);
        Assert.Equal("Burn (W)", header[3]);

        // A logic trace has no unit, so it is named without empty brackets.
        Assert.Equal("Clock", header[4]);
    }

    /// <summary>
    /// Including the last one. Its time arrived by accumulating solver steps and can land a
    /// fraction of a part per quadrillion past a window end that came from a timebase, which is
    /// no reason to drop it.
    /// </summary>
    [Fact]
    public void EveryRecordedPointBecomesARow()
    {
        var probe = Probe("V", ProbeKind.Voltage, t => t * 1000, step: 1e-4);

        var (_, rows) = Parse(TraceCsv.Write([probe], 0, 1e-3));

        Assert.Equal(probe.HistoryBuffer.Count, rows.Length);

        // And the values are the ones recorded, read back through an ordinary parse.
        Assert.Equal(0.0, Field(rows[0], 1), 9);
        Assert.Equal(1.0, Field(rows[^1], 1), 6);
    }

    [Fact]
    public void AHiddenTraceIsLeftOutAsItIsOnThePlot()
    {
        var shown = Probe("Shown", ProbeKind.Voltage, t => 1);
        var hidden = Probe("Hidden", ProbeKind.Voltage, t => 2);

        hidden.IsVisible = false;

        var (header, rows) = Parse(TraceCsv.Write([shown, hidden], 0, 1e-3));

        Assert.Equal(2, header.Length);
        Assert.DoesNotContain("Hidden", header);
        Assert.All(rows, r => Assert.Equal(2, r.Length));
    }

    [Fact]
    public void OnlyTheWindowAskedForIsWritten()
    {
        var probe = Probe("V", ProbeKind.Voltage, t => t, to: 10e-3, step: 1e-4);

        var (_, rows) = Parse(TraceCsv.Write([probe], 4e-3, 6e-3));

        Assert.All(rows, r => Assert.InRange(Field(r, 0), 4e-3 - 1e-9, 6e-3 + 1e-9));
        Assert.True(rows.Length > 10);
    }

    // ---- the axes not lining up --------------------------------------------

    /// <summary>
    /// Probes record when the solver accepted a time point, so two of them need not share a
    /// single instant. The rows are the union, with each column interpolated where it has no
    /// sample of its own.
    /// </summary>
    [Fact]
    public void TwoTracesOnDifferentTimeAxesAreBothFilledIn()
    {
        var coarse = Probe("Coarse", ProbeKind.Voltage, t => t * 1000, step: 1e-4);
        var fine = Probe("Fine", ProbeKind.Voltage, t => t * 2000, step: 1e-5);

        var (header, rows) = Parse(TraceCsv.Write([coarse, fine], 0, 1e-3));

        Assert.Equal(3, header.Length);

        // Every row has both columns filled, and each is its own straight line — so the
        // interpolation put the right value in, not the nearest one.
        Assert.All(rows, row =>
        {
            Assert.Equal(3, row.Length);

            var t = Field(row, 0);

            Assert.Equal(t * 1000, Field(row, 1), 1e-6);
            Assert.Equal(t * 2000, Field(row, 2), 1e-6);
        });
    }

    /// <summary>
    /// And a probe attached later has no reading before it existed. The cell is left empty rather
    /// than carrying a number nobody measured.
    /// </summary>
    [Fact]
    public void ATraceIsBlankOutsideItsOwnRecordedSpanRatherThanInvented()
    {
        var early = Probe("Early", ProbeKind.Voltage, t => 1, from: 0, to: 1e-3);
        var late = Probe("Late", ProbeKind.Voltage, t => 2, from: 5e-4, to: 1e-3);

        var (_, rows) = Parse(TraceCsv.Write([early, late], 0, 1e-3));

        var beforeItExisted = rows.Where(r => Field(r, 0) < 4.9e-4).ToList();
        var afterwards = rows.Where(r => Field(r, 0) > 5.1e-4).ToList();

        Assert.NotEmpty(beforeItExisted);
        Assert.NotEmpty(afterwards);

        Assert.All(beforeItExisted, r => Assert.Equal(string.Empty, r[2].Trim()));
        Assert.All(afterwards, r => Assert.Equal(2.0, Field(r, 2), 6));
    }

    // ---- size --------------------------------------------------------------

    /// <summary>
    /// A million rows is a file most spreadsheets will not open, and a curve does not need them.
    /// </summary>
    [Fact]
    public void AVeryLongRunIsDecimatedToTheRowLimit()
    {
        // Given room in the buffer on purpose: with the ordinary ten thousand the ring would
        // decimate the run before the writer saw it, and this would be testing the buffer.
        var probe = Probe("V", ProbeKind.Voltage, t => t, to: 1.0, step: 1e-5, capacity: 200_000);

        Assert.True(probe.HistoryBuffer.Count > 50_000);

        var (_, rows) = Parse(TraceCsv.Write([probe], 0, 1.0, maximumRows: 500));

        Assert.InRange(rows.Length, 400, 500);
        Assert.Equal(rows.Length, TraceCsv.RowCount([probe], 0, 1.0, maximumRows: 500));
    }

    /// <summary>
    /// Evenly, not by truncation — a file cut short at the limit would silently be a file of the
    /// first fraction of the run.
    /// </summary>
    [Fact]
    public void AndTheDecimationSpansTheWholeRunRatherThanTruncatingIt()
    {
        var probe = Probe("V", ProbeKind.Voltage, t => t, to: 1.0, step: 1e-5, capacity: 200_000);

        var (_, rows) = Parse(TraceCsv.Write([probe], 0, 1.0, maximumRows: 200));

        Assert.Equal(0.0, Field(rows[0], 0), 6);
        Assert.Equal(1.0, Field(rows[^1], 0), 3);
    }

    // ---- being readable elsewhere ------------------------------------------

    /// <summary>
    /// Invariant culture, so the decimal separator does not depend on where the file was written.
    /// A file full of commas as decimal points inside a comma-separated file is unreadable.
    /// </summary>
    [Fact]
    public void NumbersAreWrittenWithADotWhateverTheMachinesCulture()
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var csv = TraceCsv.Write([Probe("V", ProbeKind.Voltage, t => 1.5)], 0, 1e-3);

            Assert.Contains("1.5", csv);
            Assert.DoesNotContain("1,5", csv);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ALabelWithACommaInItIsQuoted()
    {
        var csv = TraceCsv.Write(
            [Probe("Shunt (1 ohm, so 1 V per amp)", ProbeKind.Voltage, t => 1)], 0, 1e-3);

        var header = csv.Split('\n')[0];

        Assert.Contains("\"Shunt (1 ohm, so 1 V per amp) (V)\"", header);

        // And the row still has exactly two fields, so the quoting worked.
        var row = csv.Split('\n')[1];

        Assert.Equal(2, row.Split(',').Length);
    }

    [Fact]
    public void EnoughDigitsSurviveThatASmallSignalOnABigRailIsStillThere()
    {
        // Fifty microvolts of ripple on five volts: the interesting part is the ninth digit.
        var probe = Probe("Rail", ProbeKind.Voltage, t => 5.0 + (50e-6 * Math.Sin(t * 1e5)));

        var (_, rows) = Parse(TraceCsv.Write([probe], 0, 1e-3));

        var values = rows.Select(r => Field(r, 1)).ToList();

        Assert.True(values.Max() - values.Min() > 1e-5,
            "the ripple was rounded away by the number format");
    }

    // ---- nothing to write --------------------------------------------------

    [Fact]
    public void NoProbesGivesAHeaderAndNoRowsRatherThanThrowing()
    {
        var csv = TraceCsv.Write([], 0, 1e-3);

        Assert.StartsWith("time_s", csv);
        Assert.Empty(Parse(csv).Rows);
    }

    [Fact]
    public void AProbeWithNothingRecordedGivesAColumnAndNoRows()
    {
        var csv = TraceCsv.Write([new SignalProbe { Label = "Quiet" }], 0, 1e-3);

        var (header, rows) = Parse(csv);

        Assert.Contains("Quiet (V)", header);
        Assert.Empty(rows);
    }
}
