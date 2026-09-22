using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.UI.Services;

namespace Cirq.UI.Tests;

/// <summary>
/// The two text exports, through the same entry point the picture formats use — so they are found
/// where somebody already looks rather than on a menu of their own.
/// </summary>
public class TextExportTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"cirq-export-{Guid.NewGuid():N}");

    public TextExportTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private string Path_(string name) => Path.Combine(_directory, name);

    private static Circuit Divider()
    {
        var circuit = new Circuit { Title = "Divider" };
        var supply = circuit.Add(new DcVoltageSource(10.0));
        var top = circuit.Add(new Resistor(1e3));
        var bottom = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);

        return circuit;
    }

    // ---- netlist -----------------------------------------------------------

    [Fact]
    public void ANetlistExportWritesADeckWithTheRightExtension()
    {
        var path = Path_("divider.cir");

        var written = CircuitExporter.Export(
            Divider(), null, path, new ExportOptions(ExportFormat.Netlist));

        Assert.Equal(path, Assert.Single(written));
        Assert.True(File.Exists(path));

        var deck = File.ReadAllText(path);

        Assert.Contains("* Divider", deck);
        Assert.Contains(".end", deck);
        Assert.Contains("V1 ", deck);
        Assert.Equal(".cir", CircuitExporter.Extension(ExportFormat.Netlist));
    }

    /// <summary>
    /// A netlist describes the circuit, so it needs no scope and ignores the content setting —
    /// there is no such thing as a netlist of an oscilloscope.
    /// </summary>
    [Fact]
    public void ANetlistNeedsNoTracesAndIgnoresWhatTheContentSettingSays()
    {
        var path = Path_("traces-requested.cir");

        CircuitExporter.Export(
            Divider(), null, path,
            new ExportOptions(ExportFormat.Netlist, ExportContent.Traces));

        Assert.Contains("R1 ", File.ReadAllText(path));
    }

    // ---- CSV ---------------------------------------------------------------

    private static Circuit WithRecordedTraces()
    {
        var circuit = Divider();
        var resistor = circuit.Components.OfType<Resistor>().First();

        var probe = new SignalProbe("Mid", resistor.B, Color.ProbePalette[0]);

        for (var t = 0.0; t <= 1e-3; t += 1e-5) probe.Record(t, 5.0 + Math.Sin(t * 1e4));

        circuit.Probes.Add(probe);
        return circuit;
    }

    [Fact]
    public void ACsvExportWritesTheRecordedTraces()
    {
        var path = Path_("traces.csv");

        CircuitExporter.Export(
            WithRecordedTraces(), null, path, new ExportOptions(ExportFormat.Csv));

        var lines = File.ReadAllLines(path);

        Assert.StartsWith("time_s,Mid (V)", lines[0]);
        Assert.True(lines.Length > 50);

        // Readable as numbers, which is the whole point of the format.
        var fields = lines[1].Split(',');

        Assert.Equal(2, fields.Length);
        Assert.True(double.TryParse(fields[0], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _));
    }

    /// <summary>
    /// A file is not a screen: somebody exporting data wants the run rather than the part of it
    /// currently in view.
    /// </summary>
    [Fact]
    public void ItWritesEverythingRecordedRatherThanTheWindowOnScreen()
    {
        var circuit = Divider();
        var resistor = circuit.Components.OfType<Resistor>().First();
        var probe = new SignalProbe("Long", resistor.B, Color.ProbePalette[0]);

        // Ten seconds of it, where a scope window is milliseconds.
        for (var t = 0.0; t <= 10.0; t += 1e-2) probe.Record(t, t);

        circuit.Probes.Add(probe);

        var path = Path_("whole-run.csv");
        CircuitExporter.Export(circuit, null, path, new ExportOptions(ExportFormat.Csv));

        var lines = File.ReadAllLines(path);
        var last = lines[^1].Split(',');

        Assert.Equal(10.0, double.Parse(last[0], System.Globalization.CultureInfo.InvariantCulture), 2);
    }

    [Fact]
    public void ACsvExportWithNoProbesSaysWhatToDo()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            CircuitExporter.Export(
                Divider(), null, Path_("nothing.csv"), new ExportOptions(ExportFormat.Csv)));

        Assert.Contains("probe", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ACsvExportWithProbesThatHaveRecordedNothingSaysThatInstead()
    {
        var circuit = Divider();
        var resistor = circuit.Components.OfType<Resistor>().First();

        circuit.Probes.Add(new SignalProbe("Quiet", resistor.B, Color.ProbePalette[0]));

        var error = Assert.Throws<InvalidOperationException>(() =>
            CircuitExporter.Export(
                circuit, null, Path_("quiet.csv"), new ExportOptions(ExportFormat.Csv)));

        Assert.Contains("Run the circuit", error.Message);
    }

    // ---- fitting the existing export ---------------------------------------

    [Fact]
    public void BothAreTextAndNeitherIsRaster()
    {
        foreach (var format in new[] { ExportFormat.Netlist, ExportFormat.Csv })
        {
            Assert.True(CircuitExporter.IsText(format));
            Assert.False(CircuitExporter.IsRaster(format));
        }

        foreach (var format in new[]
                 {
                     ExportFormat.Png, ExportFormat.Jpeg, ExportFormat.Bmp,
                     ExportFormat.Svg, ExportFormat.Pdf,
                 })
        {
            Assert.False(CircuitExporter.IsText(format));
        }
    }

    [Fact]
    public void EveryFormatStillHasAnExtensionOfItsOwn()
    {
        var extensions = Enum.GetValues<ExportFormat>()
            .ToDictionary(f => f, CircuitExporter.Extension);

        Assert.All(extensions.Values, e => Assert.StartsWith(".", e));

        // Not all distinct any more, and they should not be: a bill of materials is a CSV file
        // like the traces are, and giving it another extension to keep a test happy would make a
        // spreadsheet refuse to open it.
        //
        // What actually matters is that nothing falls through to the default. PDF is the only
        // format entitled to .pdf, so a new one arriving without a case of its own shows up here
        // rather than quietly writing a .pdf full of text.
        foreach (var (format, extension) in extensions)
        {
            if (format == ExportFormat.Pdf) continue;

            Assert.NotEqual(".pdf", extension);
        }

        Assert.Equal(".csv", extensions[ExportFormat.Csv]);
        Assert.Equal(".csv", extensions[ExportFormat.Bom]);
    }

    /// <summary>
    /// A text export writes one file and nothing else — no companion PDF, whatever the combined
    /// layout setting says.
    /// </summary>
    [Fact]
    public void ATextExportWritesExactlyOneFile()
    {
        var path = Path_("single.cir");

        var written = CircuitExporter.Export(
            Divider(), null, path,
            new ExportOptions(ExportFormat.Netlist, ExportContent.Both, AsSingleFile: false));

        Assert.Single(written);
        Assert.Single(Directory.GetFiles(_directory));
    }
}
