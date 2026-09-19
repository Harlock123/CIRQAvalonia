using Avalonia;
using Cirq.UI.Services;

namespace Cirq.UI.Rendering;

/// <summary>
/// Draws a parts list as a table, on whatever canvas it is handed.
/// <para>
/// It measures before it draws, so the columns are as wide as their contents rather than as wide
/// as someone guessed: a row of a dozen designators pushes the table out instead of running into
/// the column beside it.
/// </para>
/// </summary>
public static class PartsListRenderer
{
    private const double TitleSize = 15.0;
    private const double HeaderSize = 11.0;
    private const double RowSize = 11.0;

    private const double RowHeight = 19.0;
    private const double ColumnGap = 22.0;

    /// <summary>Space above the title and below the last row.</summary>
    private const double Padding = 18.0;

    private static readonly string[] Headings = ["Qty", "Ref", "Part", "Value"];

    /// <summary>How much room the table needs, measured against the canvas that will draw it.</summary>
    public static Size Measure(ISymbolCanvas canvas, IReadOnlyList<PartsListRow> rows)
    {
        var widths = ColumnWidths(canvas, rows);
        var width = widths.Sum() + (ColumnGap * (widths.Length - 1));

        // Title, a blank, the heading row, its rule, then the rows.
        var height = Padding + RowHeight + (RowHeight * (rows.Count + 1)) + Padding;

        return new Size(Math.Max(width, 220), height);
    }

    /// <summary>
    /// Draws the table with its top-left corner at the origin. <paramref name="width"/> is the
    /// room available, which the table is centred in.
    /// </summary>
    public static void Draw(
        ISymbolCanvas canvas, IReadOnlyList<PartsListRow> rows, double width, double zoom = 1.0)
    {
        var widths = ColumnWidths(canvas, rows);
        var tableWidth = widths.Sum() + (ColumnGap * (widths.Length - 1));
        var left = Math.Max(0, (width - tableWidth) / 2);

        var y = Padding;

        canvas.DrawText("Parts list", new Point(left, y), TitleSize, CanvasTheme.LabelBrush, SymbolTextAlign.Left);
        y += RowHeight + 6;

        // Column headings, then a rule under them.
        DrawRow(canvas, Headings, widths, left, y, HeaderSize, CanvasTheme.LabelBrush);
        y += RowHeight * 0.55;

        var rule = CanvasTheme.Pen(CanvasTheme.LabelBrush, 0.8, zoom);
        canvas.DrawLine(rule, new Point(left, y), new Point(left + tableWidth, y));
        y += RowHeight * 0.45;

        foreach (var row in rows)
        {
            string[] cells =
            [
                row.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                row.Designators,
                row.Part,
                row.Value,
            ];

            // The value is drawn in the same colour the schematic gives values, so the two read
            // as the same information rather than as two unrelated tables.
            DrawRow(canvas, cells, widths, left, y, RowSize, CanvasTheme.SymbolBrush, valueColumn: 3);
            y += RowHeight;
        }

        if (rows.Count == 0)
        {
            canvas.DrawText("Nothing placed yet", new Point(left, y), RowSize,
                CanvasTheme.LabelBrush, SymbolTextAlign.Left);
        }
    }

    private static void DrawRow(
        ISymbolCanvas canvas, IReadOnlyList<string> cells, double[] widths,
        double left, double y, double size, Avalonia.Media.IBrush brush, int valueColumn = -1)
    {
        var x = left;

        for (var i = 0; i < cells.Count; i++)
        {
            var cellBrush = i == valueColumn ? CanvasTheme.ValueBrush : brush;

            // The quantity is a number, so it is right-aligned in its column the way a number
            // should be; everything else reads left to right.
            if (i == 0)
            {
                canvas.DrawText(cells[i], new Point(x + widths[i], y), size, cellBrush, SymbolTextAlign.Right);
            }
            else
            {
                canvas.DrawText(cells[i], new Point(x, y), size, cellBrush, SymbolTextAlign.Left);
            }

            x += widths[i] + ColumnGap;
        }
    }

    /// <summary>Each column as wide as the widest thing in it, heading included.</summary>
    private static double[] ColumnWidths(ISymbolCanvas canvas, IReadOnlyList<PartsListRow> rows)
    {
        var widths = new double[Headings.Length];

        for (var i = 0; i < Headings.Length; i++)
            widths[i] = canvas.MeasureText(Headings[i], HeaderSize);

        foreach (var row in rows)
        {
            widths[0] = Math.Max(widths[0], canvas.MeasureText(
                row.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture), RowSize));
            widths[1] = Math.Max(widths[1], canvas.MeasureText(row.Designators, RowSize));
            widths[2] = Math.Max(widths[2], canvas.MeasureText(row.Part, RowSize));
            widths[3] = Math.Max(widths[3], canvas.MeasureText(row.Value, RowSize));
        }

        return widths;
    }
}
