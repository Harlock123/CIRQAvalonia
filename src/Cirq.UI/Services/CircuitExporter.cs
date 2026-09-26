using System.Globalization;
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

    /// <summary>
    /// A SPICE deck. Text rather than a picture, and the one export another tool can act on
    /// rather than only look at.
    /// </summary>
    Netlist,

    /// <summary>
    /// A KiCad netlist — the step between a simulation that works and a board.
    /// <para>
    /// The SPICE deck says what the circuit does; this says what it is. Every part with its
    /// designator, value and footprint, and every net with the pins on it, in the file Pcbnew
    /// reads — so the board is wired from the netlist that was simulated rather than from one
    /// somebody typed again.
    /// </para>
    /// </summary>
    KiCad,

    /// <summary>The recorded traces as comma-separated values, for a spreadsheet or a script.</summary>
    Csv,

    /// <summary>
    /// The parts list as comma-separated values — a bill of materials.
    /// <para>
    /// The circuit already knows every part in it and what each is set to, and the printed sheet
    /// has shown that list for a while. This is the same thing in the form somebody ordering the
    /// parts actually wants: a file a spreadsheet opens, not a page to read off.
    /// </para>
    /// </summary>
    Bom,
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
/// <param name="Live">
/// Measured figures to write onto the schematic — each net's voltage, each part's current. Null
/// leaves them off, which is the default.
/// <para>
/// Unlike the interactive markers, these are not a hint to whoever is driving the editor: a
/// schematic with its operating point marked on it is a thing somebody prints and takes to a
/// bench, and having to read the voltages off a screen beside it is exactly what the printout was
/// meant to avoid. So the caller passes them when the editor is showing them.
/// </para>
/// </param>
public sealed record ExportOptions(
    ExportFormat Format,
    ExportContent Content = ExportContent.Schematic,
    bool AsSingleFile = true,
    double RasterScale = 2.0,
    bool TransparentBackground = false,
    bool ShowInteractiveMarkers = false,
    bool IncludePartsList = false,
    LiveSnapshot? Live = null,
    string? Sheet = null,
    bool AllSheets = false);

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
    /// <summary>
    /// Writes one of the text formats and returns the path.
    /// <para>
    /// A netlist describes the circuit and a CSV describes the traces, so each ignores the
    /// content setting: there is no such thing as a netlist of an oscilloscope.
    /// </para>
    /// </summary>
    private static string WriteText(Circuit circuit, string path, ExportOptions options)
    {
        if (options.Format == ExportFormat.Netlist)
        {
            var result = Cirq.Components.Spice.SpiceNetlistWriter.Write(circuit);

            File.WriteAllText(path, result.Netlist);
            return path;
        }

        if (options.Format == ExportFormat.KiCad)
        {
            var board = Cirq.Components.Eda.KiCadNetlist.Write(circuit, Path.GetFileName(path));

            if (board.IsEmpty)
                throw new InvalidOperationException("There is nothing on the schematic to build.");

            File.WriteAllText(path, board.Netlist);
            return path;
        }

        if (options.Format == ExportFormat.Bom)
        {
            var rows = PartsList.For(circuit);

            if (rows.Count == 0)
                throw new InvalidOperationException("There is nothing on the schematic to list.");

            var bom = new System.Text.StringBuilder();

            // The footprint is a column rather than a separate export: a bill of materials is what
            // somebody orders and builds from, and "which package" is half of what they need.
            bom.AppendLine("Quantity,Designators,Part,Value,Footprint");

            foreach (var row in rows)
            {
                bom.Append(row.Quantity.ToString(CultureInfo.InvariantCulture))
                   .Append(',').Append(Quote(row.Designators))
                   .Append(',').Append(Quote(row.Part))
                   .Append(',').Append(Quote(row.Value))
                   .Append(',').Append(Quote(row.Footprint))
                   .AppendLine();
            }

            File.WriteAllText(path, bom.ToString());
            return path;
        }

        if (circuit.Probes.Count == 0)
            throw new InvalidOperationException(
                "There are no traces to export. Attach a probe and run first.");

        var samples = circuit.Probes
            .Where(p => p.IsVisible)
            .SelectMany(p => p.HistoryBuffer.ToArray())
            .ToList();

        if (samples.Count == 0)
            throw new InvalidOperationException(
                "The probes have recorded nothing yet. Run the circuit first.");

        // Everything recorded, rather than the window on screen: a file is not a screen, and
        // somebody exporting data wants the run rather than the part of it currently in view.
        File.WriteAllText(
            path,
            Cirq.Core.Probing.TraceCsv.Write(
                circuit.Probes, samples.Min(s => s.Time), samples.Max(s => s.Time)));

        return path;
    }

    /// <summary>
    /// Writes a PDF laid out for paper: a fixed sheet size, the drawing fitted inside the margins,
    /// and a header saying what it is.
    /// <para>
    /// Separate from the ordinary PDF export, which sizes the page to the circuit — right for a
    /// picture to embed, wrong for paper, where the sheet is a given and the drawing has to be
    /// placed on it. Each part gets a page of its own rather than being crammed onto one: a
    /// schematic and an oscilloscope trace squeezed onto the top and bottom half of a sheet are
    /// two things too small to read.
    /// </para>
    /// </summary>
    /// <returns>How many pages were written.</returns>
    public static int WritePrintable(
        Circuit circuit, IScopeSource? scope, string path, ExportOptions options, PageSetup page)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var wantsSchematic = options.Content != ExportContent.Traces;
        var wantsTraces = options.Content is ExportContent.Traces or ExportContent.Both;
        var hasTraces = wantsTraces && scope is { HasTraces: true };

        if (options.Content == ExportContent.Traces && !hasTraces)
            throw new InvalidOperationException("There are no traces to print. Attach a probe and run first.");

        var rows = options.IncludePartsList ? PartsList.For(circuit) : [];

        // Ink on paper for the whole document, whatever the application is wearing. Without this
        // a dark theme prints its own light strokes onto the white page below and comes out
        // blank — see CanvasTheme.ForPrinting.
        using var ink = CanvasTheme.ForPrinting();

        using var stream = File.Create(path);
        using var document = SKDocument.CreatePdf(stream);

        // A split drawing prints as a page each. Printing only the page somebody happened to be
        // looking at would be a printed document that is not the document.
        var sheets = options.AllSheets && circuit.Sheets.Count > 1
            ? circuit.Sheets.ToList()
            : [options.Sheet ?? string.Empty];

        var pages = 0;
        var total = (wantsSchematic ? sheets.Count : 0) + (hasTraces ? 1 : 0) + (rows.Count > 0 ? 1 : 0);

        if (total == 0) total = 1;

        void Sheet(string caption, double contentWidth, double contentHeight, Action<SKCanvas> draw)
        {
            var canvas = document.BeginPage((float)page.WidthPoints, (float)page.HeightPoints);

            // Paper is white, whatever the application's theme is. A dark schematic printed as it
            // appears on screen empties a cartridge and comes out worse.
            canvas.Clear(SKColors.White);

            DrawPageHeader(canvas, circuit, page, caption, ++pages, total);

            var (x, y, scale) = page.Place(contentWidth, contentHeight);

            var depth = canvas.Save();

            canvas.Translate((float)x, (float)y);
            canvas.Scale((float)scale);

            draw(canvas);

            canvas.RestoreToCount(depth);
            document.EndPage();
        }

        if (wantsSchematic)
        {
            foreach (var sheet in sheets)
            {
                var drawn = options with
                {
                    // Always opaque on paper, whatever the export dialog last said.
                    TransparentBackground = false,
                    Sheet = sheet.Length == 0 ? options.Sheet : sheet,
                };

                var bounds = SchematicBounds(circuit, drawn);
                var caption = sheets.Count > 1 ? $"Schematic — {sheet}" : "Schematic";

                Sheet(caption, bounds.Width, bounds.Height,
                    canvas => DrawSchematic(canvas, circuit, bounds, drawn));
            }
        }

        if (hasTraces && scope is not null)
        {
            var (_, _, width, height) = page.ContentArea;

            // The scope fills the width and takes its usual proportion of it, or the page's
            // height if that is less.
            var traceWidth = width;
            var traceHeight = Math.Min(width * TraceAspect, height);

            Sheet("Traces", traceWidth, traceHeight,
                canvas => DrawScope(canvas, scope,
                    new SKRect(0, 0, (float)traceWidth, (float)traceHeight)));
        }

        if (rows.Count > 0)
        {
            var table = MeasurePartsList(rows);

            Sheet("Parts list", table.Width, table.Height,
                canvas => DrawPartsList(canvas, rows, table.Width));
        }

        // A circuit with nothing in it still has to produce a file a printer will accept.
        if (pages == 0) Sheet("Schematic", 200, 150, _ => { });

        return pages;
    }

    /// <summary>
    /// The line across the top of a printed page: what the circuit is, when it was printed, and
    /// which sheet this is.
    /// <para>
    /// Paper leaves the screen and does not come back. A schematic with nothing on it saying what
    /// it is becomes a schematic of something nobody can remember, and a two-page printout with
    /// no page numbers becomes two loose sheets.
    /// </para>
    /// </summary>
    private static void DrawPageHeader(
        SKCanvas canvas, Circuit circuit, PageSetup page, string caption, int number, int total)
    {
        if (!page.IncludeHeader) return;

        var margin = (float)Math.Max(page.MarginPoints, 0);
        var right = (float)page.WidthPoints - margin;
        var height = (float)page.HeaderPoints - 8f;

        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var faint = new SKPaint { Color = new SKColor(0x88, 0x88, 0x88), IsAntialias = true };

        using var rules = new SKPaint
        {
            Color = new SKColor(0x66, 0x66, 0x66),
            StrokeWidth = 0.7f,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
        };

        using var label = new SKFont(SKTypeface.Default, 6.5f);
        using var value = new SKFont(SKTypeface.Default, 10f);

        var name = string.IsNullOrWhiteSpace(circuit.Title) ? "Untitled circuit" : circuit.Title;

        // The fields a title block carries, in the order every drawing office puts them: what it
        // is, which sheet of it this is, what revision, who drew it, when, and where in the set.
        (string Label, string Text, float Weight)[] cells =
        [
            ("TITLE", name, 3.0f),
            ("SHEET", caption, 2.0f),
            ("REV", Given(circuit.Revision), 0.7f),
            ("DRAWN", Given(circuit.Author), 1.2f),
            ("DATE", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 1.1f),
            ("PAGE", total > 1 ? $"{number} of {total}" : "1 of 1", 0.9f),
        ];

        var total_weight = cells.Sum(c => c.Weight);
        var box = new SKRect(margin, margin, right, margin + height);

        canvas.DrawRect(box, rules);

        var x = margin;

        foreach (var (field, text, weight) in cells)
        {
            var width = (right - margin) * (weight / total_weight);

            if (x > margin + 0.5f) canvas.DrawLine(x, box.Top, x, box.Bottom, rules);

            canvas.DrawText(field, x + 4f, box.Top + 10f, SKTextAlign.Left, label, faint);

            // Clipped to its own cell, so a long title stops at the divider rather than running
            // across the next field and being read as part of it.
            var depth = canvas.Save();

            canvas.ClipRect(new SKRect(x, box.Top, x + width - 2f, box.Bottom));
            canvas.DrawText(text, x + 4f, box.Bottom - 7f, SKTextAlign.Left, value, ink);
            canvas.RestoreToCount(depth);

            x += width;
        }
    }

    /// <summary>A field nobody filled in, drawn as a dash rather than as nothing.</summary>
    private static string Given(string text) =>
        string.IsNullOrWhiteSpace(text) ? "—" : text.Trim();

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
        ExportFormat.Bom => ".csv",
        ExportFormat.Png => ".png",
        ExportFormat.Jpeg => ".jpg",
        ExportFormat.Bmp => ".bmp",
        ExportFormat.Svg => ".svg",
        ExportFormat.Netlist => ".cir",
        ExportFormat.KiCad => ".net",
        ExportFormat.Csv => ".csv",
        _ => ".pdf",
    };

    /// <summary>
    /// True for the formats that are text rather than a drawing. They do not go through the Skia
    /// canvas the picture formats share, and they ignore everything about layout.
    /// </summary>
    public static bool IsText(ExportFormat format) =>
        format is ExportFormat.Netlist or ExportFormat.KiCad or ExportFormat.Csv or ExportFormat.Bom;

    /// <summary>True for the formats that have a resolution rather than being drawn as shapes.</summary>
    /// <summary>
    /// A CSV field, quoted when it has to be. A designator list is "R1, R2, R3" — commas and all
    /// — so getting this wrong would put the parts in the wrong columns of every row that has
    /// more than one of something.
    /// </summary>
    private static string Quote(string value) =>
        value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;

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

        // The text formats are not drawings and share none of the layout below. A netlist or a
        // parts list is about the circuit rather than about a page of it, so sheets do not come
        // into it: there is one netlist however the drawing is cut up.
        if (IsText(options.Format)) return [WriteText(circuit, path, options)];

        if (options.AllSheets && circuit.Sheets.Count > 1) return EverySheet(circuit, scope, path, options);

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

    /// <summary>
    /// Every page of a split drawing.
    /// <para>
    /// One PDF with a page each, because that is what a PDF is for and a reader can turn the pages;
    /// one file each for the picture formats, named after the sheet, because a PNG has no pages and
    /// stacking them into one image would put every page on top of the first — which is the same
    /// reason a single-sheet export shows only the page you are on.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> EverySheet(
        Circuit circuit, IScopeSource? scope, string path, ExportOptions options)
    {
        var wantsTraces = options.Content is ExportContent.Traces or ExportContent.Both;
        var hasTraces = wantsTraces && scope is { HasTraces: true };

        if (options.Format == ExportFormat.Pdf)
        {
            WriteSheetedPdf(circuit, hasTraces ? scope : null, path, options);
            return [path];
        }

        var directory = Path.GetDirectoryName(path) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Extension(options.Format);

        List<string> written = [];

        foreach (var sheet in circuit.Sheets)
        {
            var name = string.Concat(sheet.Split(Path.GetInvalidFileNameChars()));
            var each = Path.Combine(directory, $"{stem}-{name}{extension}");

            // The traces belong to the circuit rather than to a page of it, so they go on the
            // first sheet's file and are not repeated on every one.
            var first = ReferenceEquals(sheet, circuit.Sheets[0]) || sheet == circuit.Sheets[0];

            WriteSheet(
                circuit, first && hasTraces ? scope : null, each, options with { Sheet = sheet },
                includeSchematic: options.Content != ExportContent.Traces,
                includeTraces: first && hasTraces);

            written.Add(each);
        }

        return written;
    }

    /// <summary>One PDF, one page per sheet, with the traces after them.</summary>
    private static void WriteSheetedPdf(
        Circuit circuit, IScopeSource? scope, string path, ExportOptions options)
    {
        using var stream = File.Create(path);
        using var document = SKDocument.CreatePdf(stream);

        if (options.Content != ExportContent.Traces)
        {
            foreach (var sheet in circuit.Sheets)
            {
                var page = options with { Sheet = sheet };
                var bounds = SchematicBounds(circuit, page);

                var canvas = document.BeginPage((float)bounds.Width, (float)bounds.Height);

                PaintBackground(canvas, bounds.Width, bounds.Height, options);
                DrawSchematic(canvas, circuit, bounds, page);
                document.EndPage();
            }
        }

        if (scope is { HasTraces: true })
        {
            var height = TraceWidth * TraceAspect;
            var canvas = document.BeginPage((float)TraceWidth, (float)height);

            PaintBackground(canvas, TraceWidth, height, options);
            DrawScope(canvas, scope, new SKRect(0, 0, (float)TraceWidth, (float)height));
            document.EndPage();
        }
    }

    // ---- sheets ----------------------------------------------------------

    /// <summary>One sheet holding the schematic, the traces, or both stacked.</summary>
    private static void WriteSheet(
        Circuit circuit, IScopeSource? scope, string path, ExportOptions options,
        bool includeSchematic, bool includeTraces)
    {
        var schematic = includeSchematic ? SchematicBounds(circuit, options) : default;

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
        var schematic = SchematicBounds(circuit, options);

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
    /// The extent of what is being exported, with a margin round it — which is what "export the
    /// circuit" means regardless of where the view happens to be scrolled. On a document split
    /// into sheets it is the one sheet: drawn together they would overlap, since every page starts
    /// its coordinates in the same corner.
    /// </summary>
    private static Rect SchematicBounds(Circuit circuit, ExportOptions options)
    {
        var bounds = CircuitRenderer.BoundsOf(circuit, options.Sheet);

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
            // Selection is an editing state, not part of the drawing: a picture of the circuit
            // must not depend on what happened to be highlighted when it was taken.
            new CircuitRenderOptions(
                1.0, null, options.ShowInteractiveMarkers, ShowSelection: false, Live: options.Live,
                Sheet: options.Sheet));
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
