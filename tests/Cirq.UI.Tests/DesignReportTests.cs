using Cirq.Components.Explaining;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Core.Verification;
using Cirq.UI.Services;

namespace Cirq.UI.Tests;

/// <summary>
/// One page with the drawing, what it is, the requirements and the parts.
/// <para>
/// Every piece of this existed already; what is checked here is that the assembly says the right
/// things and escapes what it is given — a report is the one artefact that leaves the application
/// and is read somewhere else, so a designator with an ampersand in it must not break the page.
/// </para>
/// </summary>
public class DesignReportTests
{
    private static Circuit Divider()
    {
        var circuit = new Circuit { Title = "Divider" };

        var supply = circuit.Add(new DcVoltageSource(10.0) { Name = "V1" });
        var top = circuit.Add(new Resistor(6.8e3) { Name = "R1" });
        var bottom = circuit.Add(new Resistor(3.3e3) { Name = "R2" });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);

        return circuit;
    }

    private static ReportContent Content(
        Circuit circuit,
        IReadOnlyList<SpecResult>? specs = null,
        string? svg = null,
        string notes = "") =>
        new(circuit.Title, notes, svg, specs ?? [], CircuitExplainer.Explain(circuit));

    [Fact]
    public void ItIsAWholeHtmlDocument()
    {
        var page = DesignReport.Build(Divider(), Content(Divider()));

        Assert.StartsWith("<!DOCTYPE html>", page);
        Assert.Contains("</html>", page);

        // Self-contained: nothing fetched from anywhere, so it still renders when it is filed.
        Assert.DoesNotContain("<script", page);
        Assert.DoesNotContain("http://", page);
        Assert.DoesNotContain("https://", page);
    }

    [Fact]
    public void ItCarriesThePartsList()
    {
        var page = DesignReport.Build(Divider(), Content(Divider()));

        Assert.Contains("Parts", page);

        // One row per part and value, which is what a bill of materials is — two resistors of
        // different values are two lines, not one line reading "R1, R2".
        Assert.Contains("R1", page);
        Assert.Contains("6.8k", page);
        Assert.Contains("R2", page);
        Assert.Contains("3.3k", page);
    }

    [Fact]
    public void ItCarriesWhatTheCircuitIs()
    {
        var circuit = Divider();

        var page = DesignReport.Build(circuit, Content(circuit));

        Assert.Contains("What it is", page);
        Assert.Contains("divider", page);
        Assert.Contains("3.267V", page);
    }

    /// <summary>
    /// The verdict goes at the top, where somebody who reads nothing else will still see it. A
    /// report whose verdict is on page four is a report whose verdict nobody knows.
    /// </summary>
    [Fact]
    public void TheVerdictComesBeforeAnythingElse()
    {
        var circuit = Divider();

        var spec = new DesignSpec
        {
            Name = "Tap",
            Trace = "Out",
            Quantity = SpecQuantity.Mean,
            Comparison = SpecComparison.Within,
            Limit = 3.3,
            Tolerance = 0.1,
        };

        var met = new SpecResult(spec, 3.27, true, "met");

        var page = DesignReport.Build(circuit, Content(circuit, [met]));

        Assert.True(page.IndexOf("All 1 requirements met.", StringComparison.Ordinal)
                    < page.IndexOf("Parts", StringComparison.Ordinal));

        Assert.Contains("verdict good", page);
    }

    [Fact]
    public void AFailedRequirementIsMarkedAsOne()
    {
        var circuit = Divider();

        var spec = new DesignSpec { Name = "Ripple", Trace = "Rail", Limit = 0.05 };
        var broken = new SpecResult(spec, 0.2, false, "not met");

        var page = DesignReport.Build(circuit, Content(circuit, [broken]));

        Assert.Contains("verdict bad", page);
        Assert.Contains("not met", page);
        Assert.Contains("1 not met", page);
    }

    /// <summary>Not measurable is kept apart from not met, as it is everywhere else.</summary>
    [Fact]
    public void NotMeasurableIsNotAFailure()
    {
        var circuit = Divider();

        var spec = new DesignSpec { Name = "Frequency", Trace = "Out" };
        var unknown = new SpecResult(spec, null, null, "nothing to measure");

        var page = DesignReport.Build(circuit, Content(circuit, [unknown]));

        Assert.Contains("verdict unsure", page);
        Assert.Contains("not measurable", page);
        Assert.DoesNotContain("not met", page);
    }

    /// <summary>
    /// A report leaves the application and is read somewhere else, so anything that came from a
    /// person has to be escaped. A circuit called "R&amp;D" must not break the page.
    /// </summary>
    [Fact]
    public void EverythingFromAPersonIsEscaped()
    {
        var circuit = Divider();

        circuit.Title = "R&D <build 2>";
        circuit.Components.First(c => c.Name == "R1").Name = "R&1";

        var page = DesignReport.Build(circuit, Content(circuit, notes: "watch <this> & that"));

        Assert.Contains("R&amp;D &lt;build 2&gt;", page);
        Assert.Contains("watch &lt;this&gt; &amp; that", page);
        Assert.DoesNotContain("<build 2>", page);
    }

    /// <summary>The drawing goes in as SVG, and only the drawing.</summary>
    [Fact]
    public void TheSchematicGoesInAsSvg()
    {
        var circuit = Divider();

        var page = DesignReport.Build(circuit, Content(circuit, svg: "<svg><rect/></svg>"));

        Assert.Contains("<svg><rect/></svg>", page);
        Assert.Contains("The circuit", page);
    }

    /// <summary>And a report without one is still a report.</summary>
    [Fact]
    public void ItSurvivesHavingNoDrawing()
    {
        var page = DesignReport.Build(Divider(), Content(Divider(), svg: null));

        Assert.DoesNotContain("The circuit</h2>", page);
        Assert.Contains("Parts", page);
    }

    /// <summary>
    /// The conditions are always there. A number without them is not a measurement, and a report
    /// that leaves them out is one nobody can repeat.
    /// </summary>
    [Fact]
    public void ItAlwaysSaysWhatItWasRunAt()
    {
        var circuit = Divider();

        circuit.AmbientTemperatureCelsius = 85.0;

        var page = DesignReport.Build(circuit, Content(circuit));

        Assert.Contains("Conditions", page);
        Assert.Contains("85 °C", page);
    }

    /// <summary>
    /// The real path end to end: the schematic through the ordinary exporter, trimmed to the
    /// drawing and put in the page. An SVG file's own declaration has no business inside a
    /// document that already has one.
    /// </summary>
    [Fact]
    public void TheExportersOwnSvgGoesInCleanly()
    {
        var circuit = Divider();
        var temporary = Path.Combine(Path.GetTempPath(), $"cirq-svg-{Guid.NewGuid():N}.svg");

        try
        {
            CircuitExporter.Export(
                circuit, null, temporary,
                new ExportOptions(ExportFormat.Svg, ExportContent.Schematic));

            var file = File.ReadAllText(temporary);
            var drawing = file[file.IndexOf("<svg", StringComparison.OrdinalIgnoreCase)..];

            var page = DesignReport.Build(circuit, Content(circuit, svg: drawing));

            Assert.Contains("<svg", page);
            Assert.DoesNotContain("<?xml", page);

            // And the designators really are in it, as text somebody can select.
            Assert.Contains("R1", page);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    [Fact]
    public void ItWritesAFile()
    {
        var circuit = Divider();
        var path = Path.Combine(Path.GetTempPath(), $"cirq-report-test-{Guid.NewGuid():N}.html");

        try
        {
            DesignReport.Write(circuit, Content(circuit), path);

            Assert.True(File.Exists(path));
            Assert.Contains("<!DOCTYPE html>", File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
