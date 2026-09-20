using Avalonia.Media;
using Cirq.UI.Rendering;

namespace Cirq.UI.Tests;

/// <summary>
/// How a lit part is drawn. Both of these exist because the obvious rendering — paint the emitted
/// colour, fade it in proportion to the current — produces a symbol you cannot tell is on.
/// </summary>
public class EmissionTests
{
    [Fact]
    public void SomethingNotConductingIsNotDrawnAsLit()
    {
        Assert.Equal(0.0, CanvasTheme.Emission(0.0));
        Assert.Equal(0.0, CanvasTheme.Emission(0.001));
    }

    /// <summary>
    /// The point of the curve. An LED at a fifth of its rated current is plainly on to anyone
    /// looking at the board, so it has to be plainly on in the symbol too — a fifth of the way
    /// from the unlit colour to the lit one is a smudge.
    /// </summary>
    [Fact]
    public void ADimlyDrivenPartIsStillDrawnAsClearlyOn()
    {
        Assert.True(CanvasTheme.Emission(0.2) > 0.5,
            $"a fifth of rated current drew at {CanvasTheme.Emission(0.2):0.00}");
    }

    [Fact]
    public void BrighterStillReadsAsBrighter()
    {
        var levels = new[] { 0.05, 0.2, 0.5, 0.8, 1.0 }.Select(CanvasTheme.Emission).ToList();

        for (var i = 1; i < levels.Count; i++)
            Assert.True(levels[i] > levels[i - 1], "emission should rise with drive");

        Assert.Equal(1.0, levels[^1], 3);
    }

    /// <summary>
    /// A white LED on a white sheet is the case that forces the contrast rule: drawn faithfully it
    /// is the colour of the paper, so a lit one looks like an empty outline — which reads as off.
    /// </summary>
    [Fact]
    public void APaleColourIsDarkenedEnoughToSeeOnALightCanvas()
    {
        var white = Color.FromRgb(0xF2, 0xF6, 0xFF);
        var adjusted = CanvasTheme.VisibleAgainst(white, Colors.White);

        Assert.True(Luminance(adjusted) < Luminance(white) - 0.2,
            $"{adjusted} is no darker than the sheet it is drawn on");
    }

    [Fact]
    public void ADarkColourIsLightenedEnoughToSeeOnADarkCanvas()
    {
        var navy = Color.FromRgb(0x10, 0x14, 0x28);
        var adjusted = CanvasTheme.VisibleAgainst(navy, Color.FromRgb(0x14, 0x18, 0x20));

        Assert.True(Luminance(adjusted) > Luminance(navy) + 0.2,
            $"{adjusted} is no lighter than the sheet it is drawn on");
    }

    /// <summary>A colour that already stands out is left exactly as the part emits it.</summary>
    [Fact]
    public void AColourThatAlreadyStandsOutIsLeftAlone()
    {
        var red = Color.FromRgb(0xFF, 0x41, 0x36);

        Assert.Equal(red, CanvasTheme.VisibleAgainst(red, Colors.White));
        Assert.Equal(red, CanvasTheme.VisibleAgainst(red, Color.FromRgb(0x14, 0x18, 0x20)));
    }

    private static double Luminance(Color c) =>
        ((0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B)) / 255.0;
}
