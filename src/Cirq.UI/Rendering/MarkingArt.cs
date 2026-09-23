using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Cirq.Core.Units;
using Cirq.UI.Services;

namespace Cirq.UI.Rendering;

/// <summary>
/// Draws the value as it is written on the part — the bands on a resistor, the code on a ceramic.
/// <para>
/// Real colours rather than theme ones, and that is the point: a band is only useful if it is the
/// colour the band actually is. Everything else on the card follows the theme; this does not,
/// because "the third band is red" has to mean red.
/// </para>
/// <para>
/// Drawn against <see cref="ISymbolCanvas"/> rather than straight onto Avalonia's context, so the
/// same code draws the card on screen and the guide's illustration of the codes into a file. A
/// second drawing of the bands, made to be looked at in the documentation, would be free to drift
/// away from the one people actually see.
/// </para>
/// </summary>
public static class MarkingArt
{
    /// <summary>How tall the picture is, whichever kind it is.</summary>
    public const double Height = 30;

    private const double BodyHeight = 22;
    private const double BandWidth = 6;
    private const double BandGap = 5;
    private const double LeadLength = 9;

    /// <summary>
    /// The gap that separates the tolerance band from the value bands on a real part. It is what
    /// tells you which end to read from, and without it a five-band resistor is ambiguous — the
    /// code reads differently backwards.
    /// </summary>
    private const double ToleranceGap = 7;

    /// <summary>
    /// The colours as they are on a part. Chosen to be told apart on a dark card as well as a
    /// light one — a real "black" band on a dark background would be invisible, so it is drawn as
    /// the near-black it looks like against a beige body.
    /// <para>
    /// Immutable brushes, because this is read from whichever thread is drawing — the canvas, an
    /// export, or the test that generates the guide's picture — and a mutable one belongs to the
    /// thread that made it.
    /// </para>
    /// </summary>
    private static readonly Dictionary<BandColour, IBrush> Palette = new()
    {
        [BandColour.Black] = Ink(0x20, 0x20, 0x20),
        [BandColour.Brown] = Ink(0x8B, 0x4A, 0x1E),
        [BandColour.Red] = Ink(0xD3, 0x2F, 0x2F),
        [BandColour.Orange] = Ink(0xF5, 0x7C, 0x00),
        [BandColour.Yellow] = Ink(0xF5, 0xD3, 0x00),
        [BandColour.Green] = Ink(0x2E, 0x9E, 0x48),
        [BandColour.Blue] = Ink(0x25, 0x62, 0xC8),
        [BandColour.Violet] = Ink(0x8E, 0x44, 0xC4),
        [BandColour.Grey] = Ink(0x9E, 0x9E, 0x9E),
        [BandColour.White] = Ink(0xF2, 0xF2, 0xF2),
        // Darker than gold looks in the abstract, and deliberately: a light gold sits so close to
        // the beige body that the tolerance band disappears into it, which is the one band a
        // reader most needs to find — it is what says which end to read from.
        [BandColour.Gold] = Ink(0xB0, 0x7D, 0x0A),
        [BandColour.Silver] = Ink(0xC0, 0xC4, 0xC8),
    };

    /// <summary>The body a resistor's bands sit on: the beige every one of them is.</summary>
    private static readonly IBrush Body = Ink(0xD8, 0xC2, 0x9A);

    /// <summary>A ceramic disc, which is the other colour everybody recognises.</summary>
    private static readonly IBrush Ceramic = Ink(0x3F, 0x6B, 0x3F);

    private static readonly IBrush Lead = Ink(0xA8, 0xAE, 0xB4);

    private static readonly IPen LeadPen = new ImmutablePen(Lead.ToImmutable(), 2);

    /// <summary>Everything that is not a band is a missing one, and magenta says so loudly.</summary>
    private static readonly IBrush Missing = Ink(0xFF, 0x00, 0xFF);

    /// <summary>How wide the picture will be, so the card can size itself before drawing.</summary>
    public static double Width(ComponentMarking marking)
    {
        ArgumentNullException.ThrowIfNull(marking);

        return marking.Kind == MarkingKind.Bands
            ? (LeadLength * 2) + BodyWidth(marking.Bands.Count)
            : (LeadLength * 2) + PrintedWidth;
    }

    /// <summary>Draws it with its left edge at <paramref name="at"/>.</summary>
    public static void Draw(ISymbolCanvas canvas, ComponentMarking marking, Point at)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(marking);

        if (marking.Kind == MarkingKind.Bands) DrawBanded(canvas, marking, at);
        else DrawPrinted(canvas, marking, at);
    }

    private const double PrintedWidth = 46;

    private static double BodyWidth(int bands) =>
        Math.Max(PrintedWidth, (bands * BandWidth) + ((bands + 1) * BandGap) + ToleranceGap);

    private static void DrawBanded(ISymbolCanvas canvas, ComponentMarking marking, Point at)
    {
        var width = BodyWidth(marking.Bands.Count);
        var top = at.Y + ((Height - BodyHeight) / 2);
        var body = new Rect(at.X + LeadLength, top, width, BodyHeight);

        Leads(canvas, at, width, top);

        canvas.DrawRectangle(Body, null, new RoundedRect(body, 5));

        // Bands from the left, as they are read — the tolerance band is the one with the gap
        // before it on a real part, and the spacing here keeps that.
        var x = body.X + BandGap;

        for (var i = 0; i < marking.Bands.Count; i++)
        {
            var band = marking.Bands[i];

            // The wider gap goes before the last band when there is a tolerance one, which is how
            // a part says which end to start reading from.
            if (i == marking.Bands.Count - 1 && marking.HasTolerance) x += ToleranceGap;

            var brush = Palette.TryGetValue(band.Colour, out var colour) ? colour : Missing;

            canvas.DrawRectangle(brush, null, new Rect(x, top, BandWidth, BodyHeight));

            x += BandWidth + BandGap;
        }
    }

    private static void DrawPrinted(ISymbolCanvas canvas, ComponentMarking marking, Point at)
    {
        var top = at.Y + ((Height - BodyHeight) / 2);
        var body = new Rect(at.X + LeadLength, top, PrintedWidth, BodyHeight);

        Leads(canvas, at, PrintedWidth, top);

        canvas.DrawRectangle(Ceramic, null, new RoundedRect(body, BodyHeight / 2));

        if (marking.Code is not { } code) return;

        canvas.DrawText(
            code.Code, body.Center, 11, Brushes.White, SymbolTextAlign.Centre);
    }

    /// <summary>
    /// A lead either side, so the picture reads as a component rather than as a swatch. Both of a
    /// disc ceramic's leads really leave the bottom, but drawn sideways here so the picture sits
    /// in a line of text rather than needing a column of its own.
    /// </summary>
    private static void Leads(ISymbolCanvas canvas, Point at, double width, double top)
    {
        var middle = top + (BodyHeight / 2);

        canvas.DrawLine(LeadPen, new Point(at.X, middle), new Point(at.X + LeadLength, middle));
        canvas.DrawLine(
            LeadPen,
            new Point(at.X + LeadLength + width, middle),
            new Point(at.X + LeadLength + width + LeadLength, middle));
    }

    private static ImmutableSolidColorBrush Ink(byte r, byte g, byte b) =>
        new(Color.FromRgb(r, g, b));
}
