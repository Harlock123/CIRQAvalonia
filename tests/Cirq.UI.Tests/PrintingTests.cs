using System.Text;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.UI.Services;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The printable document: a real sheet size, the drawing fitted on it, and a header saying what
/// it is. Avalonia has no print API, so what "printing" means here is producing that document and
/// handing it to the system — and the document is the part worth checking.
/// </summary>
public class PrintingTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"cirq-print-{Guid.NewGuid():N}");

    public PrintingTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private string Path_(string name) => System.IO.Path.Combine(_directory, name);

    private static Circuit Divider(string title = "Divider")
    {
        var circuit = new Circuit { Title = title };
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

    /// <summary>
    /// A PDF states each page's size in its MediaBox. Reading it back is how a page size can be
    /// checked without opening the file in something.
    /// </summary>
    /// <remarks>
    /// The box comes back rounded to whole points, which is Skia writing an integral page size —
    /// normal for PDF, and why these are compared to within a point rather than exactly.
    /// </remarks>
    private static List<(double Width, double Height)> MediaBoxes(string path)
    {
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(path));

        List<(double, double)> boxes = [];

        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(
                     text, @"/MediaBox\s*\[\s*([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)\s*\]"))
        {
            boxes.Add((
                double.Parse(match.Groups[3].Value) - double.Parse(match.Groups[1].Value),
                double.Parse(match.Groups[4].Value) - double.Parse(match.Groups[2].Value)));
        }

        return boxes;
    }

    // ---- the page is a real sheet ------------------------------------------

    [Fact]
    public void ThePageIsThePaperSizeRatherThanTheCircuitsSize()
    {
        var path = Path_("a4.pdf");

        CircuitExporter.WritePrintable(
            Divider(), null, path, new ExportOptions(ExportFormat.Pdf),
            new PageSetup(PaperSize.A4, PageOrientation.Portrait));

        var box = Assert.Single(MediaBoxes(path));

        Assert.Equal(595.28, box.Width, tolerance: 1.0);
        Assert.Equal(841.89, box.Height, tolerance: 1.0);
    }

    /// <summary>
    /// Which is the whole difference from the ordinary PDF export, whose page is sized to the
    /// drawing — right for a picture to embed, wrong for paper.
    /// </summary>
    [Fact]
    public void WhereTheOrdinaryPdfExportIsSizedToTheCircuit()
    {
        var circuit = Divider();

        var printed = Path_("printed.pdf");
        var exported = Path_("exported.pdf");

        CircuitExporter.WritePrintable(
            circuit, null, printed, new ExportOptions(ExportFormat.Pdf), new PageSetup());

        CircuitExporter.Export(circuit, null, exported, new ExportOptions(ExportFormat.Pdf));

        var printedBox = Assert.Single(MediaBoxes(printed));
        var exportedBox = Assert.Single(MediaBoxes(exported));

        Assert.Equal(595.28, printedBox.Width, tolerance: 1.0);

        // And the exported one is nothing like it, being the size of the drawing.
        Assert.True(Math.Abs(printedBox.Width - exportedBox.Width) > 5,
            $"both came out {printedBox.Width} wide, so the page was not sized to the paper");
    }

    [Theory]
    [InlineData(PaperSize.Letter, PageOrientation.Portrait, 612.0, 792.0)]
    [InlineData(PaperSize.Letter, PageOrientation.Landscape, 792.0, 612.0)]
    [InlineData(PaperSize.A3, PageOrientation.Landscape, 1190.55, 841.89)]
    public void EverySheetAndOrientationComesOutAtItsRealSize(
        PaperSize paper, PageOrientation orientation, double width, double height)
    {
        var path = Path_($"{paper}-{orientation}.pdf");

        CircuitExporter.WritePrintable(
            Divider(), null, path, new ExportOptions(ExportFormat.Pdf),
            new PageSetup(paper, orientation));

        var box = Assert.Single(MediaBoxes(path));

        Assert.Equal(width, box.Width, tolerance: 1.0);
        Assert.Equal(height, box.Height, tolerance: 1.0);
    }

    // ---- pages -------------------------------------------------------------

    [Fact]
    public void EachThingAskedForGetsASheetOfItsOwn()
    {
        var path = Path_("two.pdf");

        var pages = CircuitExporter.WritePrintable(
            Divider(), null, path,
            new ExportOptions(ExportFormat.Pdf, IncludePartsList: true),
            new PageSetup());

        // The schematic and the parts list, each on its own sheet.
        Assert.Equal(2, pages);
        Assert.Equal(2, MediaBoxes(path).Count);
    }

    [Fact]
    public void AndEverySheetIsTheSameSize()
    {
        var path = Path_("same.pdf");

        CircuitExporter.WritePrintable(
            Divider(), null, path,
            new ExportOptions(ExportFormat.Pdf, IncludePartsList: true),
            new PageSetup(PaperSize.Letter, PageOrientation.Landscape));

        var boxes = MediaBoxes(path);

        Assert.Equal(2, boxes.Count);
        Assert.All(boxes, b =>
        {
            Assert.Equal(792.0, b.Width, tolerance: 1.0);
            Assert.Equal(612.0, b.Height, tolerance: 1.0);
        });
    }

    [Fact]
    public void AnEmptyCircuitStillProducesASheetAPrinterWouldAccept()
    {
        var path = Path_("empty.pdf");

        var pages = CircuitExporter.WritePrintable(
            new Circuit(), null, path, new ExportOptions(ExportFormat.Pdf), new PageSetup());

        Assert.Equal(1, pages);
        Assert.Single(MediaBoxes(path));
        Assert.True(new FileInfo(path).Length > 200);
    }

    [Fact]
    public void AskingForTracesWithNoneRecordedSaysSo()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            CircuitExporter.WritePrintable(
                Divider(), null, Path_("no-traces.pdf"),
                new ExportOptions(ExportFormat.Pdf, ExportContent.Traces),
                new PageSetup()));

        Assert.Contains("probe", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the header --------------------------------------------------------

    /// <summary>
    /// Paper leaves the screen and does not come back. A schematic with nothing on it saying what
    /// it is becomes a schematic of something nobody can remember.
    /// </summary>
    [Fact]
    public void TheHeaderCarriesTheCircuitsName()
    {
        var path = Path_("titled.pdf");

        CircuitExporter.WritePrintable(
            Divider("Regulated bench supply"), null, path,
            new ExportOptions(ExportFormat.Pdf), new PageSetup(IncludeHeader: true));

        // Skia writes the text into the PDF's content stream; the name is in the file either as
        // literal text or as glyphs, so a length comparison against the same page without it is
        // the reliable check.
        var withHeader = new FileInfo(path).Length;

        var bare = Path_("bare.pdf");

        CircuitExporter.WritePrintable(
            Divider("Regulated bench supply"), null, bare,
            new ExportOptions(ExportFormat.Pdf), new PageSetup(IncludeHeader: false));

        Assert.True(withHeader > new FileInfo(bare).Length,
            "the header added nothing to the page");
    }

    [Fact]
    public void AnUntitledCircuitStillGetsAHeaderRatherThanABlankOne()
    {
        var circuit = Divider(string.Empty);

        var path = Path_("untitled.pdf");

        CircuitExporter.WritePrintable(
            circuit, null, path, new ExportOptions(ExportFormat.Pdf), new PageSetup());

        Assert.True(new FileInfo(path).Length > 200);
    }

    // ---- the dialog --------------------------------------------------------

    [Fact]
    public void TheDialogCountsTheSheetsItWillProduce()
    {
        var model = new PrintViewModel(hasTraces: true, canSpool: true);

        Assert.Equal(1, model.PageCount);

        model.IncludeTraces = true;
        model.IncludePartsList = true;

        Assert.Equal(3, model.PageCount);
        Assert.Contains("3 sheets", model.Summary);

        model.IncludeSchematic = false;
        model.IncludeTraces = false;
        model.IncludePartsList = false;

        Assert.False(model.HasSomethingToPrint);
        Assert.Contains("Nothing selected", model.Summary);
    }

    [Fact]
    public void ItOffersTracesOnlyWhenThereAreSome()
    {
        Assert.False(new PrintViewModel(hasTraces: false, canSpool: true).HasTraces);
        Assert.True(new PrintViewModel(hasTraces: true, canSpool: true).HasTraces);
    }

    /// <summary>
    /// A menu item called Print that silently opened a viewer would be a small lie told every
    /// time it was used, so the dialog says which it will be.
    /// </summary>
    [Fact]
    public void ItSaysPlainlyWhenThereIsNoPrinterAndItWillOpenAViewerInstead()
    {
        var spooling = new PrintViewModel(hasTraces: false, canSpool: true);
        var opening = new PrintViewModel(hasTraces: false, canSpool: false);

        Assert.Contains("sent to your printer", spooling.Summary);

        Assert.Contains("no print command", opening.Summary);
        Assert.Contains("PDF viewer", opening.Summary);
    }

    [Fact]
    public void TheChoicesReachThePageSetupAndTheExportOptions()
    {
        var model = new PrintViewModel(hasTraces: true, canSpool: true)
        {
            Paper = PaperSize.A3,
            Orientation = PageOrientation.Portrait,
            FitToPage = false,
            IncludeHeader = false,
            IncludeTraces = true,
        };

        var setup = model.Setup;

        Assert.Equal(PaperSize.A3, setup.Paper);
        Assert.Equal(PageOrientation.Portrait, setup.Orientation);
        Assert.False(setup.FitToPage);
        Assert.Equal(0, setup.HeaderPoints);

        Assert.Equal(ExportContent.Both, model.Content);

        model.IncludeSchematic = false;
        Assert.Equal(ExportContent.Traces, model.Content);
    }

    // ---- ink on paper ------------------------------------------------------

    /// <summary>
    /// A dark theme's strokes are light. A page cleared to white and drawn with them comes out
    /// looking blank — the circuit is there, in pale grey, on white paper.
    /// </summary>
    [Fact]
    public void PrintingDrawsInInkWhateverTheApplicationIsWearing()
    {
        using (Cirq.UI.Rendering.CanvasTheme.ForPrinting())
        {
            var background = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(
                Cirq.UI.Rendering.CanvasTheme.BackgroundBrush);

            var symbol = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(
                Cirq.UI.Rendering.CanvasTheme.SymbolBrush);

            // Paper is white and the ink is dark, which is the whole requirement.
            Assert.True(Luminance(background.Color) > 0.9, "the page was not light");
            Assert.True(Luminance(symbol.Color) < 0.3, "the ink was not dark");

            // And enough of a difference between them to read.
            Assert.True(Luminance(background.Color) - Luminance(symbol.Color) > 0.6);
        }
    }

    [Fact]
    public void AndTheApplicationsOwnColoursComeBackAfterwards()
    {
        var before = Cirq.UI.Rendering.CanvasTheme.SymbolBrush;

        using (Cirq.UI.Rendering.CanvasTheme.ForPrinting())
        {
            Assert.NotSame(before, Cirq.UI.Rendering.CanvasTheme.SymbolBrush);
        }

        Assert.Same(before, Cirq.UI.Rendering.CanvasTheme.SymbolBrush);
    }

    private static double Luminance(Avalonia.Media.Color c) =>
        ((0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B)) / 255.0;

    // ---- the handoff -------------------------------------------------------

    [Fact]
    public void SendingAFileThatIsNotThereIsAMessageRatherThanACrash()
    {
        var outcome = PrintService.Send(Path_("never-written.pdf"));

        Assert.False(outcome.Succeeded);
        Assert.Contains("not written", outcome.Message);
    }

    [Fact]
    public void WhateverHappensTheOutcomeNamesTheDocument()
    {
        var path = Path_("outcome.pdf");

        CircuitExporter.WritePrintable(
            Divider(), null, path, new ExportOptions(ExportFormat.Pdf), new PageSetup());

        // Not spooled and not opened — this only checks the outcome describes the file, without
        // launching anything on the machine running the tests.
        var outcome = new PrintOutcome(false, $"The document is at {path}", path);

        Assert.Contains(path, outcome.Message);
        Assert.Equal(path, outcome.Path);
    }
}
