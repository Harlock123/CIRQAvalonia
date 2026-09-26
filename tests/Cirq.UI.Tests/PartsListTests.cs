using System.Text;
using Cirq.Components.Digital;
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
/// A parts list is a bill of materials, so the grouping is the whole job: three 10k resistors are
/// one line and a 4k7 among them is another.
/// </summary>
[Collection(WindowCollection.Name)]
public class PartsListTests
{
    private static Circuit Circuit(params CircuitComponent[] components)
    {
        var circuit = new Circuit();
        foreach (var component in components) circuit.Add(component);
        return circuit;
    }

    [Fact]
    public void IdenticalPartsBecomeOneLineWithACount()
    {
        var circuit = Circuit(new Resistor(10e3), new Resistor(10e3), new Resistor(10e3));

        var row = Assert.Single(PartsList.For(circuit));

        Assert.Equal(3, row.Quantity);
        Assert.Equal("Resistor", row.Part);
        Assert.Equal("R1, R2, R3", row.Designators);
    }

    /// <summary>Same part, different value — two lines, because you have to buy both.</summary>
    [Fact]
    public void TheValueSeparatesOtherwiseIdenticalParts()
    {
        var circuit = Circuit(new Resistor(10e3), new Resistor(4.7e3), new Resistor(10e3));

        var rows = PartsList.For(circuit);

        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Single(r => r.Value.Contains("10k")).Quantity);
        Assert.Equal(1, rows.Single(r => r.Value.Contains("4.7k")).Quantity);
    }

    /// <summary>A ground symbol is a net label, not something you can buy.</summary>
    [Fact]
    public void GroundsAreNotParts()
    {
        var circuit = Circuit(new Resistor(1e3), new Ground(), new Ground());

        var row = Assert.Single(PartsList.For(circuit));

        Assert.Equal("Resistor", row.Part);
    }

    /// <summary>R10 comes after R2, which a plain string sort gets wrong.</summary>
    [Fact]
    public void DesignatorsSortByNumberNotByText()
    {
        var circuit = new Circuit();
        for (var i = 0; i < 12; i++) circuit.Add(new Resistor(1e3));

        var row = Assert.Single(PartsList.For(circuit));

        Assert.StartsWith("R1, R2, R3", row.Designators);
        Assert.EndsWith("R10, R11, R12", row.Designators);
    }

    /// <summary>Lines come out in designator order, the way a parts list is read.</summary>
    [Fact]
    public void TheLinesAreOrderedByDesignator()
    {
        var circuit = Circuit(
            new Ic7400(),
            new Resistor(1e3),
            new Capacitor(100e-9),
            new DcVoltageSource(5.0));

        var prefixes = PartsList.For(circuit).Select(r => r.Designators[0]).ToList();

        Assert.Equal(prefixes.OrderBy(c => c).ToList(), prefixes);
    }

    [Fact]
    public void AnEmptyCircuitHasAnEmptyList()
    {
        Assert.Empty(PartsList.For(new Circuit()));
    }

    // ---- in the export ---------------------------------------------------

    private static string TempPath(string extension) =>
        Path.Combine(Path.GetTempPath(), $"cirq-bom-{Guid.NewGuid():N}{extension}");

    private static Circuit Rig()
    {
        var circuit = new Circuit();

        circuit.Add(new DcVoltageSource(5.0) { X = 0, Y = 0 });
        circuit.Add(new Resistor(1e3) { X = 200, Y = 0 });
        circuit.Add(new Resistor(1e3) { X = 400, Y = 0 });
        circuit.Add(new Ground { X = 0, Y = 200 });

        return circuit;
    }

    /// <summary>The table goes below the drawing, so the page gets taller to hold it.</summary>
    [Fact]
    public void AskingForThePartsListMakesThePageTaller()
    {
        var without = TempPath(".png");
        var with = TempPath(".png");

        try
        {
            var circuit = Rig();

            CircuitExporter.Export(circuit, null, without,
                new ExportOptions(ExportFormat.Png, RasterScale: 1.0));
            CircuitExporter.Export(circuit, null, with,
                new ExportOptions(ExportFormat.Png, RasterScale: 1.0, IncludePartsList: true));

            static int Height(string path)
            {
                var b = File.ReadAllBytes(path);
                return (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
            }

            Assert.True(Height(with) > Height(without) + 80,
                $"{Height(with)} should be well over {Height(without)}");
        }
        finally
        {
            foreach (var p in new[] { without, with }) if (File.Exists(p)) File.Delete(p);
        }
    }

    /// <summary>And the table is really drawn, not merely allowed room for.</summary>
    [Fact]
    public void ThePartsListIsDrawnIntoTheFile()
    {
        var path = TempPath(".svg");

        try
        {
            CircuitExporter.Export(Rig(), null, path,
                new ExportOptions(ExportFormat.Svg, IncludePartsList: true));

            var svg = File.ReadAllText(path);

            Assert.Contains("Parts list", svg);
            Assert.Contains("Qty", svg);

            // Two resistors on one line, so the quantity is in there as well as the designators.
            Assert.Contains("R1, R2", svg);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>In the combined PDF it earns a page of its own.</summary>
    [Fact]
    public void ThePartsListGetsItsOwnPageInTheCombinedPdf()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cirq-bom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var path = Path.Combine(directory, "report.png");

            var written = CircuitExporter.Export(Rig(), null, path,
                new ExportOptions(ExportFormat.Png, ExportContent.Both, AsSingleFile: false,
                    RasterScale: 1.0, IncludePartsList: true));

            var combined = Assert.Single(written, f => f.EndsWith(".pdf", StringComparison.Ordinal));
            var pdf = File.ReadAllText(combined, Encoding.Latin1);

            // Schematic and parts list. There is no scope in this rig, so the traces page is
            // absent and the parts list is the second page.
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(pdf, @"/Type\s*/Page[^s]").Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Left alone, an export has no table in it.</summary>
    [Fact]
    public void ThePartsListIsOffUnlessAskedFor()
    {
        var path = TempPath(".svg");

        try
        {
            CircuitExporter.Export(Rig(), null, path, new ExportOptions(ExportFormat.Svg));

            Assert.DoesNotContain("Parts list", File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
