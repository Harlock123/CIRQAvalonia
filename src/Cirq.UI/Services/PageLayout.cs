namespace Cirq.UI.Services;

/// <summary>A sheet of paper.</summary>
public enum PaperSize
{
    A4,
    Letter,
    Legal,
    A3,
}

/// <summary>Which way round the sheet goes.</summary>
public enum PageOrientation
{
    Portrait,
    Landscape,
}

/// <summary>
/// Where the content goes on a printed page.
/// <para>
/// This is the part that printing actually needs and exporting never did. A PDF written to embed
/// in something else is sized to the circuit — that is the right answer for a picture, and the
/// wrong one for paper, where the sheet is a fixed size and the drawing has to be placed on it.
/// Working it out as a value rather than inside the drawing code means the arithmetic can be
/// checked without producing a file and holding it up to the light.
/// </para>
/// </summary>
/// <param name="Paper">The sheet.</param>
/// <param name="Orientation">Which way round it goes.</param>
/// <param name="MarginPoints">Blank border on every side, in points.</param>
/// <param name="FitToPage">
/// True to scale the drawing so it fills the printable area. False prints at its natural size,
/// which is what you want when the drawing is going to be measured — and which may overflow.
/// </param>
/// <param name="IncludeHeader">
/// Adds a title block across the top: what the drawing is, which sheet this is, the revision, who
/// drew it, the date, and the page number. Paper leaves the screen and does not come back, and a
/// schematic with nothing on it saying what it is becomes a schematic of something nobody can
/// remember — or worse, one somebody builds from the wrong revision.
/// </param>
public sealed record PageSetup(
    PaperSize Paper = PaperSize.A4,
    PageOrientation Orientation = PageOrientation.Portrait,
    double MarginPoints = 36.0,
    bool FitToPage = true,
    bool IncludeHeader = true)
{
    /// <summary>
    /// Height of the title block, in points. Zero when there is none.
    /// <para>
    /// Two rows: a small caption naming each field, and the field itself under it. A block that
    /// printed the values alone would save eleven points and leave somebody guessing whether the
    /// middle one is the revision or the sheet.
    /// </para>
    /// </summary>
    public double HeaderPoints => IncludeHeader ? 42.0 : 0.0;

    /// <summary>The sheet's width in points, after the orientation is applied.</summary>
    public double WidthPoints =>
        Orientation == PageOrientation.Portrait ? ShortEdge : LongEdge;

    /// <summary>And its height.</summary>
    public double HeightPoints =>
        Orientation == PageOrientation.Portrait ? LongEdge : ShortEdge;

    /// <summary>
    /// The area the drawing may use: the sheet less the margins and the header. Never negative,
    /// however absurd the margin, so nothing downstream has to defend against a backwards
    /// rectangle.
    /// </summary>
    public (double X, double Y, double Width, double Height) ContentArea
    {
        get
        {
            var margin = Math.Max(MarginPoints, 0);

            var width = Math.Max(WidthPoints - (margin * 2), 1.0);
            var height = Math.Max(HeightPoints - (margin * 2) - HeaderPoints, 1.0);

            return (margin, margin + HeaderPoints, width, height);
        }
    }

    /// <summary>
    /// How much to scale content of the given size to sit on the page.
    /// <para>
    /// Fitting scales both ways and takes the smaller, so the proportions are kept — a schematic
    /// squashed to fill a page is not a schematic of the same circuit. It also refuses to scale
    /// <i>up</i>: a small circuit blown up to fill a sheet looks like a mistake, and the drawing
    /// was laid out at a size somebody chose.
    /// </para>
    /// </summary>
    public double ScaleFor(double contentWidth, double contentHeight)
    {
        if (contentWidth <= 0 || contentHeight <= 0) return 1.0;

        var (_, _, width, height) = ContentArea;

        var fit = Math.Min(width / contentWidth, height / contentHeight);

        return FitToPage ? Math.Min(fit, 1.0) : 1.0;
    }

    /// <summary>
    /// Where content of the given size lands once scaled: centred horizontally, and at the top
    /// vertically. Top rather than centred because a page of several sheets should start each in
    /// the same place, and a drawing floating in the middle of a sheet reads as an accident.
    /// </summary>
    public (double X, double Y, double Scale) Place(double contentWidth, double contentHeight)
    {
        var scale = ScaleFor(contentWidth, contentHeight);
        var (x, y, width, _) = ContentArea;

        return (x + Math.Max((width - (contentWidth * scale)) / 2, 0), y, scale);
    }

    /// <summary>True when content of this size will not fit, so the caller can say so.</summary>
    public bool Overflows(double contentWidth, double contentHeight)
    {
        var scale = ScaleFor(contentWidth, contentHeight);
        var (_, _, width, height) = ContentArea;

        return (contentWidth * scale) > width + 0.5 || (contentHeight * scale) > height + 0.5;
    }

    /// <summary>The sheet's shorter side in points, before the orientation is applied.</summary>
    private double ShortEdge => Paper switch
    {
        PaperSize.Letter or PaperSize.Legal => 612.0,
        PaperSize.A3 => 841.89,
        _ => 595.28,
    };

    private double LongEdge => Paper switch
    {
        PaperSize.Letter => 792.0,
        PaperSize.Legal => 1008.0,
        PaperSize.A3 => 1190.55,
        _ => 841.89,
    };

    /// <summary>What the paper is called, for a picker.</summary>
    public static string Describe(PaperSize paper) => paper switch
    {
        PaperSize.A4 => "A4  —  210 × 297 mm",
        PaperSize.Letter => "Letter  —  8.5 × 11 in",
        PaperSize.Legal => "Legal  —  8.5 × 14 in",
        _ => "A3  —  297 × 420 mm",
    };
}
