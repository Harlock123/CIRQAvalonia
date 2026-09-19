using Cirq.UI.ViewModels;
using System.Text;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.UI.Rendering;
using Cirq.UI.Services;
using SkiaSharp;

namespace Cirq.UI.Tests;

/// <summary>
/// The exporter writes real files, so these check the bytes rather than that a method ran: the
/// magic numbers, the dimensions in the headers, and that the drawing actually reached the page.
/// </summary>
public class ExportTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "cirq-export-" + Guid.NewGuid().ToString("N"));

    public ExportTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }

        GC.SuppressFinalize(this);
    }

    private string Path_(string name) => Path.Combine(_directory, name);

    /// <summary>A small circuit with a wire and a caption, so there is something to find in the file.</summary>
    private static Circuit Rig()
    {
        var circuit = new Circuit { Title = "Export rig" };

        var source = new DcVoltageSource(5.0) { X = 0, Y = 0 };
        var resistor = new Resistor(1e3) { X = 200, Y = 0 };
        var ground = new Ground { X = 0, Y = 200 };

        circuit.Add(source);
        circuit.Add(resistor);
        circuit.Add(ground);

        circuit.Connect(source.Positive, resistor.A);
        circuit.Connect(source.Negative, ground.Pin);

        return circuit;
    }

    /// <summary>Stands in for the scope panel, and records that it was asked to draw.</summary>
    private sealed class FakeScope(bool hasTraces = true) : IScopeSource
    {
        public bool HasTraces { get; } = hasTraces;

        public SKRect LastArea { get; private set; }

        public int RenderCount { get; private set; }

        public void Render(SKCanvas canvas, SKRect area)
        {
            LastArea = area;
            RenderCount++;

            using var paint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
            canvas.DrawRect(area, paint);
        }
    }

    // ---- raster ----------------------------------------------------------

    private static (int Width, int Height) PngSize(string path)
    {
        var bytes = File.ReadAllBytes(path);

        Assert.True(bytes.Length > 24);
        Assert.Equal<byte[]>([0x89, (byte)'P', (byte)'N', (byte)'G'], bytes[..4]);

        // IHDR width and height are big-endian at offsets 16 and 20.
        static int BigEndian(byte[] b, int at) =>
            (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];

        return (BigEndian(bytes, 16), BigEndian(bytes, 20));
    }

    [Fact]
    public void APngComesOutAtTheSizeOfTheCircuit()
    {
        var circuit = Rig();
        var path = Path_("schematic.png");

        CircuitExporter.Export(circuit, null, path, new ExportOptions(ExportFormat.Png, RasterScale: 2.0));

        var bounds = CircuitRenderer.BoundsOf(circuit);
        var (width, height) = PngSize(path);

        // The margin is added all round, and the whole thing is then scaled.
        Assert.InRange(width, (bounds.Width + 40) * 2, (bounds.Width + 70) * 2);
        Assert.InRange(height, (bounds.Height + 40) * 2, (bounds.Height + 70) * 2);
    }

    /// <summary>The scale is a resolution, so it multiplies the pixels and nothing else.</summary>
    [Fact]
    public void TheRasterScaleMultipliesThePixels()
    {
        var circuit = Rig();

        var single = Path_("one.png");
        var triple = Path_("three.png");

        CircuitExporter.Export(circuit, null, single, new ExportOptions(ExportFormat.Png, RasterScale: 1.0));
        CircuitExporter.Export(circuit, null, triple, new ExportOptions(ExportFormat.Png, RasterScale: 3.0));

        var (smallWidth, smallHeight) = PngSize(single);
        var (bigWidth, bigHeight) = PngSize(triple);

        Assert.InRange(bigWidth, (smallWidth * 3) - 3, (smallWidth * 3) + 3);
        Assert.InRange(bigHeight, (smallHeight * 3) - 3, (smallHeight * 3) + 3);
    }

    /// <summary>
    /// Skia ships without a BMP encoder, so this one is written by hand — which makes its header
    /// worth checking rather than assuming.
    /// </summary>
    [Fact]
    public void ABmpHasAValidHeaderAndAsManyPixelsAsItClaims()
    {
        var circuit = Rig();
        var path = Path_("schematic.bmp");

        CircuitExporter.Export(circuit, null, path, new ExportOptions(ExportFormat.Bmp, RasterScale: 1.0));

        var bytes = File.ReadAllBytes(path);

        Assert.Equal((byte)'B', bytes[0]);
        Assert.Equal((byte)'M', bytes[1]);

        var declaredSize = BitConverter.ToInt32(bytes, 2);
        var offset = BitConverter.ToInt32(bytes, 10);
        var width = BitConverter.ToInt32(bytes, 18);
        var height = BitConverter.ToInt32(bytes, 22);
        var bpp = BitConverter.ToInt16(bytes, 28);

        Assert.Equal(bytes.Length, declaredSize);
        Assert.Equal(54, offset);
        Assert.Equal(32, bpp);
        Assert.True(width > 0 && height > 0);
        Assert.Equal(54 + (width * height * 4), bytes.Length);
    }

    [Fact]
    public void AJpegIsWritten()
    {
        var circuit = Rig();
        var path = Path_("schematic.jpg");

        CircuitExporter.Export(circuit, null, path, new ExportOptions(ExportFormat.Jpeg));

        var bytes = File.ReadAllBytes(path);

        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
        Assert.True(bytes.Length > 1000);
    }

    // ---- vector ----------------------------------------------------------

    /// <summary>
    /// The point of SVG is that the schematic is shapes rather than pixels, so the file has to
    /// contain drawing elements — not just a valid wrapper round an embedded image.
    /// </summary>
    [Fact]
    public void AnSvgIsMadeOfShapes()
    {
        var circuit = Rig();
        var path = Path_("schematic.svg");

        CircuitExporter.Export(circuit, null, path, new ExportOptions(ExportFormat.Svg));

        var svg = File.ReadAllText(path);

        Assert.Contains("<svg", svg);
        Assert.Contains("</svg>", svg);

        // Lines and paths are what a schematic is made of. Skia writes them as one or the other
        // depending on the call, so either satisfies this.
        Assert.True(
            svg.Contains("<path") || svg.Contains("<line") || svg.Contains("<polyline"),
            "the SVG contained no drawing elements");

        Assert.DoesNotContain("<image", svg);
    }

    [Fact]
    public void AnSvgCarriesTheCircuitsSize()
    {
        var circuit = Rig();
        var path = Path_("sized.svg");

        CircuitExporter.Export(circuit, null, path, new ExportOptions(ExportFormat.Svg));

        var svg = File.ReadAllText(path);
        var bounds = CircuitRenderer.BoundsOf(circuit);

        var width = ReadLength(svg, "width");
        var height = ReadLength(svg, "height");

        Assert.InRange(width, bounds.Width + 40, bounds.Width + 70);
        Assert.InRange(height, bounds.Height + 40, bounds.Height + 70);
    }

    private static double ReadLength(string svg, string attribute)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            svg, attribute + "=\"(?<value>[0-9.]+)");

        Assert.True(match.Success, $"no {attribute} on the svg element");
        return double.Parse(match.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void APdfIsAPdf()
    {
        var circuit = Rig();
        var path = Path_("schematic.pdf");

        CircuitExporter.Export(circuit, null, path, new ExportOptions(ExportFormat.Pdf));

        var bytes = File.ReadAllBytes(path);
        var head = Encoding.ASCII.GetString(bytes, 0, 5);
        var tail = Encoding.ASCII.GetString(bytes[^32..]);

        Assert.Equal("%PDF-", head);
        Assert.Contains("%%EOF", tail);
    }

    // ---- framing ---------------------------------------------------------

    /// <summary>
    /// The export is framed on the circuit, not on the window — so moving a part further out
    /// makes the file bigger even though nothing about the view changed.
    /// </summary>
    [Fact]
    public void TheFrameFollowsTheCircuitRatherThanTheView()
    {
        var circuit = Rig();
        var near = Path_("near.png");
        var far = Path_("far.png");

        CircuitExporter.Export(circuit, null, near, new ExportOptions(ExportFormat.Png, RasterScale: 1.0));

        circuit.Components[1].X = 1200;

        CircuitExporter.Export(circuit, null, far, new ExportOptions(ExportFormat.Png, RasterScale: 1.0));

        var (nearWidth, _) = PngSize(near);
        var (farWidth, _) = PngSize(far);

        Assert.True(farWidth > nearWidth + 900, $"{farWidth} should be about a thousand wider than {nearWidth}");
    }

    /// <summary>An empty circuit still has to produce a file a viewer will open.</summary>
    [Fact]
    public void AnEmptyCircuitStillExports()
    {
        var path = Path_("empty.png");

        CircuitExporter.Export(new Circuit(), null, path, new ExportOptions(ExportFormat.Png));

        var (width, height) = PngSize(path);

        Assert.True(width > 0);
        Assert.True(height > 0);
    }

    // ---- traces ----------------------------------------------------------

    [Fact]
    public void TracesAreDrawnIntoTheAreaTheyAreGiven()
    {
        var scope = new FakeScope();
        var path = Path_("traces.png");

        CircuitExporter.Export(Rig(), scope, path,
            new ExportOptions(ExportFormat.Png, ExportContent.Traces, RasterScale: 1.0));

        Assert.Equal(1, scope.RenderCount);
        Assert.True(scope.LastArea.Width > 0);
        Assert.True(scope.LastArea.Height > 0);
    }

    /// <summary>Exporting traces that do not exist is a mistake worth saying out loud.</summary>
    [Fact]
    public void ExportingTracesWithoutAnyIsRefused()
    {
        var path = Path_("nothing.png");

        var error = Assert.Throws<InvalidOperationException>(() =>
            CircuitExporter.Export(Rig(), new FakeScope(hasTraces: false), path,
                new ExportOptions(ExportFormat.Png, ExportContent.Traces)));

        Assert.Contains("no traces", error.Message);
        Assert.False(File.Exists(path));
    }

    /// <summary>Both on one sheet: the schematic above, the traces below, in one file.</summary>
    [Fact]
    public void BothOnOneSheetIsTallerThanEitherAlone()
    {
        var circuit = Rig();
        var alone = Path_("alone.png");
        var together = Path_("together.png");

        CircuitExporter.Export(circuit, null, alone,
            new ExportOptions(ExportFormat.Png, ExportContent.Schematic, RasterScale: 1.0));

        CircuitExporter.Export(circuit, new FakeScope(), together,
            new ExportOptions(ExportFormat.Png, ExportContent.Both, AsSingleFile: true, RasterScale: 1.0));

        var (_, aloneHeight) = PngSize(alone);
        var (_, bothHeight) = PngSize(together);

        Assert.True(bothHeight > aloneHeight + 300, $"{bothHeight} should be well over {aloneHeight}");
    }

    /// <summary>
    /// Separate files, and the combined PDF alongside them — three files from one export, with
    /// the PDF holding both pages.
    /// </summary>
    [Fact]
    public void SeparateFilesComeWithACombinedPdf()
    {
        var path = Path_("report.png");

        var written = CircuitExporter.Export(Rig(), new FakeScope(), path,
            new ExportOptions(ExportFormat.Png, ExportContent.Both, AsSingleFile: false, RasterScale: 1.0));

        Assert.Equal(3, written.Count);
        Assert.All(written, f => Assert.True(File.Exists(f), $"{f} was not written"));

        Assert.Contains(written, f => f.EndsWith("report-schematic.png", StringComparison.Ordinal));
        Assert.Contains(written, f => f.EndsWith("report-traces.png", StringComparison.Ordinal));

        var combined = Assert.Single(written, f => f.EndsWith("report.pdf", StringComparison.Ordinal));
        var pdf = File.ReadAllText(combined, Encoding.Latin1);

        Assert.StartsWith("%PDF-", pdf);

        // Two pages, because the traces got one of their own.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(pdf, @"/Type\s*/Page[^s]").Count);
    }

    [Fact]
    public void EveryFormatWritesSomethingWorthOpening()
    {
        var circuit = Rig();

        foreach (var format in Enum.GetValues<ExportFormat>())
        {
            var path = Path_("all" + CircuitExporter.Extension(format));

            CircuitExporter.Export(circuit, null, path, new ExportOptions(format, RasterScale: 1.0));

            Assert.True(File.Exists(path), $"{format} wrote no file");
            Assert.True(new FileInfo(path).Length > 200, $"{format} wrote only {new FileInfo(path).Length} bytes");
        }
    }
}

/// <summary>The menu command around the exporter: what it asks, and what it does with the answer.</summary>
public class ExportCommandTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var path in _files.Where(File.Exists)) File.Delete(path);
        GC.SuppressFinalize(this);
    }

    private string TempPath(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cirq-cmd-{Guid.NewGuid():N}{extension}");
        _files.Add(path);
        return path;
    }

    private sealed class Scope(bool hasTraces) : IScopeSource
    {
        public bool HasTraces { get; } = hasTraces;

        public void Render(SKCanvas canvas, SKRect area)
        {
        }
    }

    [Fact]
    public async Task CancellingTheDialogWritesNothing()
    {
        var vm = new MainWindowViewModel();
        var dialogs = new FakeFileDialogs { ExportRequest = null };
        vm.FileDialogs = dialogs;

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.ExportPrompts);
        Assert.Empty(dialogs.Reports);
    }

    [Fact]
    public async Task ExportingWritesTheFileAndSaysSo()
    {
        var vm = new MainWindowViewModel();
        var path = TempPath(".svg");

        vm.FileDialogs = new FakeFileDialogs
        {
            ExportRequest = new ExportRequest(path, new ExportOptions(ExportFormat.Svg)),
        };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.True(File.Exists(path));
        Assert.Contains("Exported", vm.StatusMessage);
    }

    /// <summary>
    /// The dialog is told whether there is anything on the scope, so it can grey out the options
    /// that would otherwise produce a blank half.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheDialogIsToldWhetherThereAreTraces(bool hasTraces)
    {
        var vm = new MainWindowViewModel { ScopeSource = new Scope(hasTraces) };
        var dialogs = new FakeFileDialogs();
        vm.FileDialogs = dialogs;

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal(hasTraces, dialogs.LastExportOfferedTraces);
    }

    /// <summary>With no scope attached at all, traces are simply not on offer.</summary>
    [Fact]
    public async Task NoScopeMeansNoTraces()
    {
        var vm = new MainWindowViewModel();
        var dialogs = new FakeFileDialogs();
        vm.FileDialogs = dialogs;

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.False(dialogs.LastExportOfferedTraces);
    }

    /// <summary>A failed export is reported rather than thrown at the user.</summary>
    [Fact]
    public async Task AFailedExportIsReported()
    {
        var vm = new MainWindowViewModel();
        var dialogs = new FakeFileDialogs
        {
            // Traces were asked for, and there are none.
            ExportRequest = new ExportRequest(
                TempPath(".png"), new ExportOptions(ExportFormat.Png, ExportContent.Traces)),
        };
        vm.FileDialogs = dialogs;

        await vm.ExportCommand.ExecuteAsync(null);

        var report = Assert.Single(dialogs.Reports);
        Assert.Equal("Export failed", report.Title);
        Assert.Contains("no traces", report.Message);
    }
}

/// <summary>
/// A plotting library renders as though it owns the surface. ScottPlot clears the canvas before
/// it draws, which on a bitmap wiped the schematic above it — so the exporter has to confine a
/// scope to the area it was given rather than trusting it.
/// </summary>
public class ScopeContainmentTests
{
    private sealed class ClearingScope : IScopeSource
    {
        public bool HasTraces => true;

        public void Render(SKCanvas canvas, SKRect area)
        {
            // What ScottPlot does on the way in.
            canvas.Clear(SKColors.Transparent);

            using var paint = new SKPaint { Color = SKColors.Navy, Style = SKPaintStyle.Fill };
            canvas.DrawRect(area, paint);
        }
    }

    [Fact]
    public void AScopeThatClearsTheCanvasCannotWipeTheSchematic()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cirq-clip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var path = Path.Combine(directory, "both.png");

            var circuit = new Circuit();
            circuit.Add(new Resistor(1e3) { X = 0, Y = 0 });
            circuit.Add(new Resistor(2e3) { X = 300, Y = 0 });

            CircuitExporter.Export(circuit, new ClearingScope(), path,
                new ExportOptions(ExportFormat.Png, ExportContent.Both, AsSingleFile: true, RasterScale: 1.0));

            using var bitmap = SKBitmap.Decode(path);

            // The top-left corner belongs to the schematic half, and the background was painted
            // there before the scope ever drew. If the clear escaped, it is transparent.
            var corner = bitmap.GetPixel(4, 4);

            Assert.True(corner.Alpha > 0,
                "the scope's clear escaped its area and wiped the schematic's background");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
