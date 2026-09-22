using Cirq.UI.Services;

namespace Cirq.UI.Tests;

/// <summary>
/// Where content goes on a printed page. This is the arithmetic printing needs and exporting
/// never did, and it is worth checking as a value rather than by producing a PDF and holding it
/// up to the light.
/// </summary>
public class PageLayoutTests
{
    /// <summary>A point is a seventy-second of an inch, and every figure here is in points.</summary>
    [Theory]
    [InlineData(PaperSize.A4, 595.28, 841.89)]
    [InlineData(PaperSize.Letter, 612.0, 792.0)]
    [InlineData(PaperSize.Legal, 612.0, 1008.0)]
    [InlineData(PaperSize.A3, 841.89, 1190.55)]
    public void EachPaperSizeIsItsRealSize(PaperSize paper, double width, double height)
    {
        var page = new PageSetup(paper);

        Assert.Equal(width, page.WidthPoints, 2);
        Assert.Equal(height, page.HeightPoints, 2);

        // A4's long edge is 297 mm, which at 72 points to the inch is 841.89.
        Assert.Equal(height / 72.0 * 25.4, page.HeightPoints / 72.0 * 25.4, 2);
    }

    [Fact]
    public void LandscapeSwapsTheEdges()
    {
        var portrait = new PageSetup(PaperSize.A4);
        var landscape = portrait with { Orientation = PageOrientation.Landscape };

        Assert.Equal(portrait.WidthPoints, landscape.HeightPoints, 6);
        Assert.Equal(portrait.HeightPoints, landscape.WidthPoints, 6);
        Assert.True(landscape.WidthPoints > landscape.HeightPoints);
    }

    // ---- the printable area ------------------------------------------------

    [Fact]
    public void TheContentAreaIsTheSheetLessItsMarginsAndHeader()
    {
        var page = new PageSetup(PaperSize.A4, MarginPoints: 36, IncludeHeader: true);

        var (x, y, width, height) = page.ContentArea;

        Assert.Equal(36, x, 6);
        Assert.Equal(36 + page.HeaderPoints, y, 6);
        Assert.Equal(595.28 - 72, width, 2);
        Assert.Equal(841.89 - 72 - page.HeaderPoints, height, 2);
    }

    [Fact]
    public void WithoutAHeaderThatBandGoesBackToTheDrawing()
    {
        var withHeader = new PageSetup(IncludeHeader: true);
        var without = withHeader with { IncludeHeader = false };

        Assert.Equal(0, without.HeaderPoints);
        Assert.Equal(
            withHeader.ContentArea.Height + withHeader.HeaderPoints,
            without.ContentArea.Height,
            6);
    }

    /// <summary>
    /// A margin wider than the paper is absurd but should not produce a backwards rectangle that
    /// everything downstream has to defend against.
    /// </summary>
    [Fact]
    public void AnAbsurdMarginStillLeavesAValidArea()
    {
        var page = new PageSetup(PaperSize.A4, MarginPoints: 5000);

        var (_, _, width, height) = page.ContentArea;

        Assert.True(width > 0);
        Assert.True(height > 0);

        // And a negative one is treated as none.
        var negative = new PageSetup(PaperSize.A4, MarginPoints: -50).ContentArea;

        Assert.Equal(0, negative.X, 6);
    }

    // ---- fitting -----------------------------------------------------------

    [Fact]
    public void SomethingTooBigIsScaledDownToFit()
    {
        var page = new PageSetup(PaperSize.A4);
        var (_, _, width, height) = page.ContentArea;

        var scale = page.ScaleFor(width * 4, height * 4);

        Assert.Equal(0.25, scale, 3);
        Assert.False(page.Overflows(width * 4, height * 4));
    }

    /// <summary>
    /// Both ways, taking the smaller — a schematic squashed to fill a page is not a schematic of
    /// the same circuit.
    /// </summary>
    [Fact]
    public void TheProportionsAreKept()
    {
        var page = new PageSetup(PaperSize.A4);
        var (_, _, width, height) = page.ContentArea;

        // Very wide and short: the width is what limits it.
        var scale = page.ScaleFor(width * 10, height / 10);

        Assert.Equal(0.1, scale, 3);

        var (_, _, placed) = page.Place(width * 10, height / 10);

        Assert.Equal(scale, placed, 6);
    }

    /// <summary>
    /// And it refuses to scale up. A small circuit blown up to fill a sheet looks like a mistake,
    /// and the drawing was laid out at a size somebody chose.
    /// </summary>
    [Fact]
    public void SomethingSmallIsLeftAlone()
    {
        var page = new PageSetup(PaperSize.A4);

        Assert.Equal(1.0, page.ScaleFor(50, 50), 6);
    }

    [Fact]
    public void WithoutFitToPageNothingIsScaledAndItMayOverflow()
    {
        var page = new PageSetup(PaperSize.A4, FitToPage: false);
        var (_, _, width, height) = page.ContentArea;

        Assert.Equal(1.0, page.ScaleFor(width * 4, height * 4), 6);
        Assert.True(page.Overflows(width * 4, height * 4));

        // Something that fits does not report an overflow.
        Assert.False(page.Overflows(width / 2, height / 2));
    }

    // ---- placement ---------------------------------------------------------

    [Fact]
    public void ContentIsCentredAcrossThePageAndStartsAtTheTop()
    {
        var page = new PageSetup(PaperSize.A4);
        var (areaX, areaY, width, _) = page.ContentArea;

        var (x, y, _) = page.Place(width / 2, 100);

        // Half the leftover either side.
        Assert.Equal(areaX + (width / 4), x, 3);

        // Top rather than centred: a drawing floating in the middle reads as an accident.
        Assert.Equal(areaY, y, 6);
    }

    [Fact]
    public void ContentAsWideAsThePageStartsAtTheMargin()
    {
        var page = new PageSetup(PaperSize.A4);
        var (areaX, _, width, height) = page.ContentArea;

        var (x, _, _) = page.Place(width, height);

        Assert.Equal(areaX, x, 6);
    }

    /// <summary>Whatever is asked for, the placed content lands inside the printable area.</summary>
    [Theory]
    [InlineData(100, 100)]
    [InlineData(5000, 80)]
    [InlineData(80, 5000)]
    [InlineData(3000, 3000)]
    public void PlacedContentAlwaysLandsOnThePaper(double contentWidth, double contentHeight)
    {
        var page = new PageSetup(PaperSize.A4);
        var (areaX, areaY, areaWidth, areaHeight) = page.ContentArea;

        var (x, y, scale) = page.Place(contentWidth, contentHeight);

        Assert.True(x >= areaX - 0.5, $"placed at {x}, left of the area at {areaX}");
        Assert.True(y >= areaY - 0.5);

        Assert.True(x + (contentWidth * scale) <= areaX + areaWidth + 0.5,
            "the content ran off the right of the page");
        Assert.True(y + (contentHeight * scale) <= areaY + areaHeight + 0.5,
            "the content ran off the bottom of the page");
    }

    [Fact]
    public void NothingToPlaceIsNotADivisionByZero()
    {
        var page = new PageSetup();

        Assert.Equal(1.0, page.ScaleFor(0, 0), 6);
        Assert.Equal(1.0, page.ScaleFor(100, 0), 6);
    }

    [Fact]
    public void EveryPaperSizeHasADescriptionWithItsRealDimensions()
    {
        Assert.Contains("210 × 297", PageSetup.Describe(PaperSize.A4));
        Assert.Contains("8.5 × 11", PageSetup.Describe(PaperSize.Letter));

        foreach (var paper in Enum.GetValues<PaperSize>())
            Assert.NotEmpty(PageSetup.Describe(paper));
    }
}
