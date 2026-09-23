using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Cirq.UI.Services;

namespace Cirq.UI.Rendering;

/// <summary>
/// The panel that describes the part under the pointer: designator and type, what it is doing,
/// what it is set to, and anything it is complaining about.
/// <para>
/// Separate from the canvas because it is a piece of layout rather than a piece of input handling.
/// The canvas decides <i>when</i> there is something to describe; this decides what that looks
/// like, and can be drawn onto any context without a pointer ever having moved.
/// </para>
/// <para>
/// Drawn with the raw <see cref="DrawingContext"/> rather than through
/// <see cref="ISymbolCanvas"/>, because it has to measure text to size itself and because it
/// belongs to the editor rather than to the circuit — the same reason the grid is drawn that way.
/// </para>
/// </summary>
public static class HoverCard
{
    private const double Padding = 8;
    private const double Gap = 4;
    private const double TitleSize = 12.5;
    private const double BodySize = 11;

    /// <summary>How far from the pointer the card sits, so it never hides what it describes.</summary>
    private const double Offset = 18;

    /// <summary>A long violation wraps at this width rather than running off the window.</summary>
    private const double MinimumWrap = 220;

    public static void Draw(DrawingContext context, ComponentSummary summary, Point pointer, Size surface)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(summary);

        var title = Text(summary.Title, TitleSize, CanvasTheme.LabelBrush, FontWeight.SemiBold);

        var subtitle = summary.Subtitle.Length > 0
            ? Text(summary.Subtitle, BodySize, CanvasTheme.ValueBrush)
            : null;

        List<(FormattedText Label, FormattedText Value)> rows =
            [.. summary.Rows.Select(r => (
                Text(r.Label, BodySize, CanvasTheme.LabelBrush),
                Text(r.Value, BodySize, CanvasTheme.ValueBrush)))];

        List<FormattedText> warnings =
            [.. summary.Warnings.Select(w => Text(w, BodySize, CanvasTheme.ErrorBrush))];

        // What the part says about itself on its own body. Two lines under the picture: what the
        // bands read out to, and — when the marking cannot say exactly what was typed — why.
        var marking = summary.Marking;

        var reading = marking is null
            ? null
            : Text(marking.Readout, BodySize - 0.5, CanvasTheme.LabelBrush);

        // The caption only when it says something the subtitle does not. A banded part that reads
        // back exactly what it is set to would otherwise print the same value twice, one line
        // apart; when the bands cannot say it exactly, that difference is the whole point.
        var captionText = marking is null
            ? null
            : marking.Note is not null ? $"{marking.Caption} — {marking.Note}"
            : marking.Kind == Cirq.UI.Services.MarkingKind.Printed ? marking.Caption
            : null;

        var caption = captionText is null
            ? null
            : Text(captionText, BodySize - 0.5,
                marking!.Note is null ? CanvasTheme.ValueBrush : CanvasTheme.ErrorBrush);

        // Nothing wraps wider than the surface the card has to sit on. Place can move a card off
        // an edge, but it cannot shrink one, so a card wider than the canvas is simply clipped at
        // the control's edge — and the marking readout is long enough to cause that on a narrow
        // canvas, where before only a long violation could.
        var wrap = Math.Min(MinimumWrap, Math.Max(120, surface.Width - (Padding * 2) - Offset));

        // Two columns for the settings, so the values line up and can be read down.
        var labelWidth = rows.Count == 0 ? 0 : rows.Max(r => r.Label.Width);
        var valueWidth = rows.Count == 0 ? 0 : rows.Max(r => r.Value.Width);

        var width = Math.Max(title.Width, subtitle?.Width ?? 0);
        width = Math.Max(width, labelWidth + (Gap * 3) + valueWidth);

        if (marking is not null)
        {
            width = Math.Max(width, MarkingArt.Width(marking));

            // Wrapped rather than allowed to set the card's width. Five bands read out in words
            // is a long line, and the picture beside it is already the widest thing on the card.
            reading!.MaxTextWidth = wrap;
            width = Math.Max(width, reading.Width);

            if (caption is not null)
            {
                caption.MaxTextWidth = wrap;
                width = Math.Max(width, caption.Width);
            }
        }

        foreach (var warning in warnings)
        {
            warning.MaxTextWidth = Math.Max(width, wrap);
            width = Math.Max(width, warning.Width);
        }

        var height = title.Height;
        if (subtitle is not null) height += (Gap / 2) + subtitle.Height;
        if (rows.Count > 0) height += Gap + rows.Sum(r => Math.Max(r.Label.Height, r.Value.Height));
        if (warnings.Count > 0) height += Gap + warnings.Sum(w => w.Height + 1);

        if (marking is not null)
            height += Gap + MarkingArt.Height + 2 + reading!.Height + (caption?.Height ?? 0);

        var box = Place(new Size(width + (Padding * 2), height + (Padding * 2)), pointer, surface);

        context.DrawRectangle(
            CanvasTheme.SymbolFill,
            CanvasTheme.Pen(CanvasTheme.SymbolBrush, 1, 1),
            new RoundedRect(box, 4));

        var cursor = new Point(box.X + Padding, box.Y + Padding);

        context.DrawText(title, cursor);
        cursor = cursor.WithY(cursor.Y + title.Height);

        if (subtitle is not null)
        {
            cursor = cursor.WithY(cursor.Y + (Gap / 2));
            context.DrawText(subtitle, cursor);
            cursor = cursor.WithY(cursor.Y + subtitle.Height);
        }

        if (rows.Count > 0)
        {
            cursor = cursor.WithY(cursor.Y + Gap);

            var valueColumn = box.X + Padding + labelWidth + (Gap * 3);

            foreach (var (label, value) in rows)
            {
                context.DrawText(label, cursor);
                context.DrawText(value, new Point(valueColumn, cursor.Y));
                cursor = cursor.WithY(cursor.Y + Math.Max(label.Height, value.Height));
            }
        }

        if (warnings.Count > 0)
        {
            cursor = cursor.WithY(cursor.Y + Gap);

            foreach (var warning in warnings)
            {
                context.DrawText(warning, cursor);
                cursor = cursor.WithY(cursor.Y + warning.Height + 1);
            }
        }

        if (marking is not null)
        {
            cursor = cursor.WithY(cursor.Y + Gap);

            MarkingArt.Draw(new AvaloniaSymbolCanvas(context), marking, cursor);
            cursor = cursor.WithY(cursor.Y + MarkingArt.Height + 2);

            context.DrawText(reading!, cursor);
            cursor = cursor.WithY(cursor.Y + reading!.Height);

            if (caption is not null) context.DrawText(caption, cursor);
        }
    }

    /// <summary>
    /// Below and right of the pointer, folded back across it when that would hang off an edge. A
    /// card that runs off the window is a card nobody can read, and the corner of a schematic is
    /// exactly where the interesting parts tend to be.
    /// </summary>
    public static Rect Place(Size card, Point pointer, Size surface)
    {
        var x = pointer.X + Offset;
        var y = pointer.Y + Offset;

        if (x + card.Width > surface.Width) x = Math.Max(0, pointer.X - card.Width - Offset / 1.5);
        if (y + card.Height > surface.Height) y = Math.Max(0, pointer.Y - card.Height - Offset / 1.5);

        return new Rect(new Point(x, y), card);
    }

    private static FormattedText Text(string value, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(CanvasTheme.LabelTypeface.FontFamily, FontStyle.Normal, weight),
            size, brush);
}
