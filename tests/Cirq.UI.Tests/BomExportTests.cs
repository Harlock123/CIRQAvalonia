using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.UI.Services;

using Cirq.UI.Tests.Support;

namespace Cirq.UI.Tests;

// Rendering tests share the window session's collection so they cannot run while another test is
// changing the application's theme. The canvas colours are resolved through a static cache that a
// theme change invalidates, so a render in one collection and an Apply in another is a race — and
// it was one: four different colour-sensitive tests failed once each under load, each passing on
// its own. See WindowSession.

/// <summary>
/// The parts list as a file rather than a page. The circuit already knows every part in it and
/// what each is set to; this is that list in the form somebody ordering the parts actually wants.
/// </summary>
[Collection(WindowCollection.Name)]
public class BomExportTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"cirq-bom-{Guid.NewGuid():N}.csv");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static Circuit Rig()
    {
        var circuit = new Circuit();

        // Three of one value and one of another, which is exactly the grouping a BOM is for.
        foreach (var value in new[] { 10e3, 10e3, 10e3, 4.7e3 })
            circuit.Add(new Resistor(value));

        circuit.Add(new Capacitor(100e-9));
        circuit.Add(new DcVoltageSource(5.0));

        return circuit;
    }

    [Fact]
    public void ItWritesAHeaderAndOneLinePerPartAndValue()
    {
        CircuitExporter.Export(Rig(), null, _path, new ExportOptions(ExportFormat.Bom));

        var lines = File.ReadAllLines(_path);

        Assert.Equal("Quantity,Designators,Part,Value", lines[0]);

        // Three 10k resistors are one line; the 4k7 is another.
        Assert.Contains(lines, l => l.StartsWith("3,", StringComparison.Ordinal) && l.Contains("10"));
        Assert.Contains(lines, l => l.StartsWith("1,", StringComparison.Ordinal) && l.Contains("4.7"));
    }

    /// <summary>
    /// A designator list is "R1, R2, R3" — commas and all — so it has to be quoted or every row
    /// with more than one part in it would put its columns in the wrong places.
    /// </summary>
    [Fact]
    public void ADesignatorListIsQuotedSoTheColumnsSurvive()
    {
        CircuitExporter.Export(Rig(), null, _path, new ExportOptions(ExportFormat.Bom));

        var grouped = File.ReadAllLines(_path).Single(l => l.StartsWith("3,", StringComparison.Ordinal));

        Assert.Contains('"', grouped);

        // Four fields, whatever is inside the quotes.
        var fields = System.Text.RegularExpressions.Regex.Matches(grouped, @"(""[^""]*""|[^,]*)")
            .Select(m => m.Value)
            .Where(v => v.Length > 0)
            .ToList();

        Assert.Equal(4, fields.Count);
    }

    /// <summary>It needs no traces, because a parts list is not a measurement.</summary>
    [Fact]
    public void ItNeedsNoTraces()
    {
        var circuit = Rig();

        Assert.Empty(circuit.Probes);

        CircuitExporter.Export(circuit, null, _path, new ExportOptions(ExportFormat.Bom));

        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void AnEmptySchematicSaysThereIsNothingToList()
    {
        var problem = Assert.Throws<InvalidOperationException>(() =>
            CircuitExporter.Export(new Circuit(), null, _path, new ExportOptions(ExportFormat.Bom)));

        Assert.Contains("nothing", problem.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ItCountsAsATextFormatSoTheDialogDropsThePictureSettings()
    {
        Assert.True(CircuitExporter.IsText(ExportFormat.Bom));
        Assert.False(CircuitExporter.IsRaster(ExportFormat.Bom));
    }
}
