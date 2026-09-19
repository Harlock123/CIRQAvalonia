using Avalonia;
using Avalonia.Media;
using Cirq.Core.Topology;
using Cirq.UI.Rendering;
using SkiaSharp;

namespace Cirq.UI.Services;

/// <summary>A file an export can be written as.</summary>
public enum ExportFormat
{
    Png,
    Jpeg,
    Bmp,
    Svg,
    Pdf,
}

/// <summary>What goes in the export.</summary>
public enum ExportContent
{
    /// <summary>The schematic alone.</summary>
    Schematic,

    /// <summary>The oscilloscope alone.</summary>
    Traces,

    /// <summary>Both — as two files, or as one, depending on <see cref="ExportOptions.AsSingleFile"/>.</summary>
    Both,
}

/// <param name="Format">The file format to write.</param>
/// <param name="Content">Schematic, traces, or both.</param>
/// <param name="AsSingleFile">
/// When both are exported: true puts them in one file, schematic above traces. False writes one
/// file each, and adds a two-page PDF alongside them.
/// </param>
/// <param name="RasterScale">
/// Pixels per schematic unit for the bitmap formats. Two gives a crisp image at ordinary sizes
/// without producing something enormous. Ignored by SVG and PDF, which have no resolution.
/// </param>
/// <param name="TransparentBackground">Leaves the page unpainted, for dropping onto another background.</param>
/// <param name="ShowInteractiveMarkers">
/// Whether the rings that mark double-clickable parts are drawn. Off by default in an export:
/// they are a hint to whoever is driving the editor, not part of the circuit.
/// </param>
/// <param name="IncludePartsList">
/// Adds a table of what the circuit is made of — quantity, designators, part and value, grouped
/// the way a bill of materials is. It goes below everything else on a single sheet, and gets a
/// page of its own in the combined PDF.
/// </param>
public sealed record ExportOptions(
    ExportFormat Format,
    ExportContent Content = ExportContent.Schematic,
    bool AsSingleFile = true,
    double RasterScale = 2.0,
    bool TransparentBackground = false,
    bool ShowInteractiveMarkers = false,
    bool IncludePartsList = false);

/// <summary>An export the user has asked for: what to write, and where.</summary>
public sealed record ExportRequest(string Path, ExportOptions Options);

/// <summary>Something that can draw the current oscilloscope onto a canvas.</summary>
/// <remarks>
/// The plot belongs to the scope panel rather than to a view model, and the exporter has no
/// business reaching into a control. This is the whole of what it needs.
/// </remarks>
public interface IScopeSource
{
    /// <summary>False when there is nothing to export — no probes, or no samples yet.</summary>
    bool HasTraces { get; }

    /// <summary>Draws the scope, filling <paramref name="area"/>.</summary>
    void Render(SKCanvas canvas, SKRect area);
}

/// <summary>
/// Writes the schematic and the traces out as files.
/// <para>
/// Every format goes through the same Skia canvas, so a PDF is drawn by exactly the code that
/// draws the screen — see <see cref="CircuitRenderer"/>. That is what keeps an export honest: it
/// cannot fall behind the editor, because it is not a second implementation of it.
/// </para>
/// </summary>
public static class CircuitExporter
{
    /// <summary>Margin left around the schematic, in schematic units.</summary>
    private const double Margin = 28.0;

    /// <summary>Height of the scope panel in an export, relative to its width.</summary>
    private const double TraceAspect = 0.45;

    /// <summary>Width used for a traces-only export, and the floor for a combined one.</summary>
    private const double TraceWidth = 1000.0;

    /// <summary>Gap between the schematic and the traces on a combined sheet.</summary>
    private const double Gutter = 32.0;

    public static string Extension(ExportFormat format) => format switch
    {
        ExportFormat.Png => ".png",
        ExportFormat.Jpeg => ".jpg",
        ExportFormat.Bmp => ".bmp",
        ExportFormat.Svg => ".svg",
        _ => ".pdf",
    };

    /// <summary>True for the formats that have a resolution rather than being drawn as shapes.</summary>
    public static bool IsRaster(ExportFormat format) =>
        format is ExportFormat.Png or ExportFormat.Jpeg or ExportFormat.Bmp;

    /// <summary>
    /// Writes the export, and returns every file it produced.
    /// <para>
    /// <paramref name="path"/> names the main file; the extra files of a multi-file export are
    /// named after it.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Export(
        Circuit circuit, IScopeSource? scope, string path, ExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var wantsTraces = options.Content is ExportContent.Traces or ExportContent.Both;
        var hasTraces = wantsTraces && scope is { HasTraces: true };

        if (options.Content == ExportContent.Traces && !hasTraces)
            throw new InvalidOperationException("There are no traces to export. Attach a probe and run first.");

        var single = options.Content != ExportContent.Both || options.AsSingleFile;

        if (single)
        {
            WriteSheet(circuit, hasTraces ? scope : null, path, options,
                includeSchematic: options.Content != ExportContent.Traces,
                includeTraces: hasTraces);

            return [path];
        }

        // Both, as separate files — plus the two-page PDF that puts them together.
        var directory = Path.GetDirectoryName(path) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Extension(options.Format);

        var schematicPath = Path.Combine(directory, $"{stem}-schematic{extension}");
        var tracesPath = Path.Combine(directory, $"{stem}-traces{extension}");
        var combinedPath = Path.Combine(directory, $"{stem}.pdf");

        List<string> written = [];

        WriteSheet(circuit, null, schematicPath, options, includeSchematic: true, includeTraces: false);
        written.Add(schematicPath);

        if (hasTraces)
        {
            WriteSheet(circuit, scope, tracesPath, options, includeSchematic: false, includeTraces: true);
            written.Add(tracesPath);
        }

        WriteCombinedPdf(circuit, hasTraces ? scope : null, combinedPath, options);
        written.Add(combinedPath);

        return written;
    }

    // ---- sheets ----------------------------------------------------------

    /// <summary>One sheet holding the schematic, the traces, or both stacked.</summary>
    private static void WriteSheet(
        Circuit circuit, IScopeSource? scope, string path, ExportOptions options,
        bool includeSchematic, bool includeTraces)
    {
        var schematic = includeSchematic ? SchematicBounds(circuit) : default;

        var rows = options.IncludePartsList ? PartsList.For(circuit) : [];
        var table = options.IncludePartsList ? MeasurePartsList(rows) : default;

        var width = includeSchematic ? schematic.Width : TraceWidth;
        if (includeTraces) width = Math.Max(width, TraceWidth);
        if (options.IncludePartsList) width = Math.Max(width, table.Width + (Margin * 2));

        var traceHeight = includeTraces ? width * TraceAspect : 0;

        var height = (includeSchematic ? schematic.Height : 0)
                     + (includeSchematic && includeTraces ? Gutter : 0)
                     + traceHeight
                     + (options.IncludePartsList ? Gutter + table.Height : 0);

        Write(path, width, height, options, canvas =>
        {
            var y = 0.0;

            if (includeSchematic)
            {
                // Centred, because the traces or the table can be the wider of the three.
                using (Translate(canvas, (float)((width - schematic.Width) / 2), 0))
                {
                    DrawSchematic(canvas, circuit, schematic, options);
                }

                y += schematic.Height;
            }

            if (includeTraces && scope is not null)
            {
                if (includeSchematic) y += Gutter;

                DrawScope(canvas, scope, new SKRect(0, (float)y, (float)width, (float)(y + traceHeight)));
                y += traceHeight;
            }

            if (options.IncludePartsList)
            {
                if (y > 0) y += Gutter;

                using (Translate(canvas, 0, (float)y))
                {
                    DrawPartsList(canvas, rows, width);
                }
            }
        });
    }

    /// <summary>The schematic and the traces as two pages of one document.</summary>
    private static void WriteCombinedPdf(
        Circuit circuit, IScopeSource? scope, string path, ExportOptions options)
    {
        var schematic = SchematicBounds(circuit);

        using var stream = File.Create(path);
        using var document = SKDocument.CreatePdf(stream);

        var page = document.BeginPage((float)schematic.Width, (float)schematic.Height);
        PaintBackground(page, schematic.Width, schematic.Height, options);
        DrawSchematic(page, circuit, schematic, options);
        document.EndPage();

        if (scope is not null)
        {
            var height = TraceWidth * TraceAspect;
            var second = document.BeginPage((float)TraceWidth, (float)height);
            PaintBackground(second, TraceWidth, height, options);
            DrawScope(second, scope, new SKRect(0, 0, (float)TraceWidth, (float)height));
            document.EndPage();
        }

        if (options.IncludePartsList)
        {
            var rows = PartsList.For(circuit);
            var table = MeasurePartsList(rows);

            var width = Math.Max(schematic.Width, table.Width + (Margin * 2));
            var third = document.BeginPage((float)width, (float)table.Height);

            PaintBackground(third, width, table.Height, options);
            DrawPartsList(third, rows, width);
            document.EndPage();
        }

        document.Close();
    }

    // ---- drawing ---------------------------------------------------------

    /// <summary>
    /// The whole circuit's extent with a margin round it, which is what "export the circuit"
    /// means regardless of where the view happens to be scrolled.
    /// </summary>
    private static Rect SchematicBounds(Circuit circuit)
    {
        var bounds = CircuitRenderer.BoundsOf(circuit);

        // An empty circuit still has to produce a valid file rather than a zero-sized one.
        if (bounds.Width <= 0 || bounds.Height <= 0)
            bounds = new Rect(bounds.X, bounds.Y, Math.Max(bounds.Width, 200), Math.Max(bounds.Height, 150));

        return bounds.Inflate(Margin);
    }

    /// <summary>
    /// Hands the scope its patch of the page, and holds it to it.
    /// <para>
    /// The clip is not tidiness. A plotting library renders as though it owns the surface, and
    /// ScottPlot begins by clearing it — which on a bitmap wiped the schematic that had just been
    /// drawn above, leaving a blank half and the traces alone at the bottom. It went unnoticed in
    /// SVG, where a clear cannot retroactively remove elements already written out, which is
    /// exactly the sort of difference between backends worth pinning down rather than living
    /// with. Clipping confines the clear to the area the scope was given.
    /// </para>
    /// </summary>
    private static void DrawScope(SKCanvas canvas, IScopeSource scope, SKRect area)
    {
        var depth = canvas.Save();

        canvas.ClipRect(area);
        scope.Render(canvas, area);

        canvas.RestoreToCount(depth);
    }

    private static void DrawPartsList(SKCanvas canvas, IReadOnlyList<PartsListRow> rows, double width)
    {
        using var symbols = new SkiaSymbolCanvas(canvas);
        PartsListRenderer.Draw(symbols, rows, width);
    }

    /// <summary>
    /// How big the table will be, before there is a page to draw it on. Measured with the same
    /// font the export will use, so the page comes out wide enough for it rather than wide enough
    /// for a guess.
    /// </summary>
    private static Size MeasurePartsList(IReadOnlyList<PartsListRow> rows)
    {
        using var bitmap = new SKBitmap(1, 1);
        using var canvas = new SKCanvas(bitmap);
        using var symbols = new SkiaSymbolCanvas(canvas);

        return PartsListRenderer.Measure(symbols, rows);
    }

    private static void DrawSchematic(SKCanvas canvas, Circuit circuit, Rect bounds, ExportOptions options)
    {
        using var symbols = new SkiaSymbolCanvas(canvas);
        using var _ = Translate(canvas, (float)-bounds.X, (float)-bounds.Y);

        // Zoom stays at one: the drawing comes out at its natural size and the canvas scale, set
        // once for the whole page, decides how many pixels that is. Passing the raster scale here
        // instead would thin every stroke by exactly the amount the scale then thickened it.
        CircuitRenderer.Draw(symbols, circuit,
            new CircuitRenderOptions(1.0, null, options.ShowInteractiveMarkers));
    }

    // ---- files -----------------------------------------------------------

    private static void Write(
        string path, double width, double height, ExportOptions options, Action<SKCanvas> draw)
    {
        var scale = IsRaster(options.Format) ? options.RasterScale : 1.0;

        switch (options.Format)
        {
            case ExportFormat.Svg:
            {
                using var stream = File.Create(path);
                var page = new SKRect(0, 0, (float)width, (float)height);

                // The document is only complete once the canvas is disposed, so it has to close
                // before the stream does.
                using (var canvas = SKSvgCanvas.Create(page, stream))
                {
                    PaintBackground(canvas, width, height, options);
                    draw(canvas);
                }

                break;
            }

            case ExportFormat.Pdf:
            {
                using var stream = File.Create(path);
                using var document = SKDocument.CreatePdf(stream);

                var canvas = document.BeginPage((float)width, (float)height);
                PaintBackground(canvas, width, height, options);
                draw(canvas);

                document.EndPage();
                document.Close();
                break;
            }

            default:
            {
                var pixelWidth = Math.Max(1, (int)Math.Ceiling(width * scale));
                var pixelHeight = Math.Max(1, (int)Math.Ceiling(height * scale));

                using var bitmap = new SKBitmap(pixelWidth, pixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var canvas = new SKCanvas(bitmap);

                canvas.Clear(SKColors.Transparent);
                canvas.Scale((float)scale);
                PaintBackground(canvas, width, height, options);
                draw(canvas);
                canvas.Flush();

                WriteBitmap(bitmap, path, options.Format);
                break;
            }
        }
    }

    private static void WriteBitmap(SKBitmap bitmap, string path, ExportFormat format)
    {
        // Skia is built without a BMP encoder on every platform we ship, so Encode returns null
        // for it rather than failing loudly. It is a simple enough format to write directly, and
        // doing so means BMP behaves like the others instead of being the one that mysteriously
        // produces an empty file.
        if (format == ExportFormat.Bmp)
        {
            WriteBmp(bitmap, path);
            return;
        }

        var encoding = format == ExportFormat.Jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png;

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(encoding, 92)
                         ?? throw new InvalidOperationException($"Skia could not encode a {format} image.");
        using var stream = File.Create(path);

        data.SaveTo(stream);
    }

    /// <summary>A 32-bit uncompressed BMP, written bottom-up as the oldest readers expect.</summary>
    private static void WriteBmp(SKBitmap bitmap, string path)
    {
        const int fileHeader = 14;
        const int infoHeader = 40;

        var width = bitmap.Width;
        var height = bitmap.Height;
        var stride = width * 4;
        var pixelBytes = stride * height;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(fileHeader + infoHeader + pixelBytes);
        writer.Write(0);                                  // reserved
        writer.Write(fileHeader + infoHeader);            // offset to the pixels

        writer.Write(infoHeader);
        writer.Write(width);
        writer.Write(height);
        writer.Write((short)1);                           // planes
        writer.Write((short)32);                          // bits per pixel
        writer.Write(0);                                  // BI_RGB, uncompressed
        writer.Write(pixelBytes);
        writer.Write(2835);                               // 72 DPI, in pixels per metre
        writer.Write(2835);
        writer.Write(0);                                  // palette entries
        writer.Write(0);                                  // important colours

        var pixels = bitmap.Bytes;

        // BGRA already, which is what BMP wants — but bottom row first.
        for (var y = height - 1; y >= 0; y--)
            writer.Write(pixels, y * stride, stride);
    }

    private static void PaintBackground(SKCanvas canvas, double width, double height, ExportOptions options)
    {
        if (options.TransparentBackground) return;

        var colour = CanvasTheme.BackgroundBrush is ISolidColorBrush solid
            ? SkiaSymbolCanvas.ToSkia(solid.Color)
            : SKColors.White;

        using var paint = new SKPaint { Color = colour, Style = SKPaintStyle.Fill };
        canvas.DrawRect(new SKRect(0, 0, (float)width, (float)height), paint);
    }

    private static IDisposable Translate(SKCanvas canvas, float dx, float dy)
    {
        var depth = canvas.Save();
        canvas.Translate(dx, dy);
        return new Restore(canvas, depth);
    }

    private sealed class Restore(SKCanvas canvas, int depth) : IDisposable
    {
        public void Dispose() => canvas.RestoreToCount(depth);
    }
}
