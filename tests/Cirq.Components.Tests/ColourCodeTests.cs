using Cirq.Core.Units;

namespace Cirq.Components.Tests;

/// <summary>
/// The bands painted on a part, checked against the codes anybody with a drawer of resistors can
/// read off one.
/// </summary>
public class ColourCodeTests
{
    private static string Read(double value, double tolerance = 0.05) =>
        ColourCodes.For(value, tolerance)?.ToString() ?? "none";

    // ---- the codes everybody knows -----------------------------------------

    /// <summary>
    /// The ones worth knowing by heart: 1 kΩ is brown-black-red, 10 kΩ brown-black-orange, and
    /// 4k7 yellow-violet-red. At five percent the fourth band is gold.
    /// </summary>
    [Theory]
    [InlineData(1e3, "brown black red gold")]
    [InlineData(10e3, "brown black orange gold")]
    [InlineData(100e3, "brown black yellow gold")]
    [InlineData(4.7e3, "yellow violet red gold")]
    [InlineData(2.2e3, "red red red gold")]
    [InlineData(330.0, "orange orange brown gold")]
    [InlineData(1e6, "brown black green gold")]
    public void TheCodesAnybodyCanReadOffAResistor(double ohms, string expected)
    {
        Assert.Equal(expected, Read(ohms));
    }

    /// <summary>
    /// Below ten ohms the multiplier divides: 4R7 is yellow-violet-gold, and 0.47 Ω is
    /// yellow-violet-silver. That is the part of the code people forget.
    /// </summary>
    [Theory]
    [InlineData(4.7, "yellow violet gold gold")]
    [InlineData(0.47, "yellow violet silver gold")]
    [InlineData(10.0, "brown black black gold")]
    public void BelowTenOhmsTheMultiplierDivides(double ohms, string expected)
    {
        Assert.Equal(expected, Read(ohms));
    }

    /// <summary>Ten percent is silver, and twenty percent has no band at all.</summary>
    [Fact]
    public void TheToleranceBandIsTheLastOne()
    {
        Assert.Equal("brown black red silver", Read(1e3, 0.10));
        Assert.Equal("brown black red", Read(1e3, 0));
    }

    // ---- precision parts get five bands ------------------------------------

    /// <summary>
    /// A part at two percent or tighter carries three digits, because you cannot promise one
    /// percent on a value you only spelled to two figures. So a 1 % 1 kΩ is
    /// brown-black-black-brown-brown, not brown-black-red-brown.
    /// </summary>
    [Fact]
    public void APrecisionPartIsMarkedToThreeFigures()
    {
        Assert.Equal("brown black black brown brown", Read(1e3, 0.01));
        Assert.Equal("yellow violet black brown brown", Read(4.7e3, 0.01));
    }

    /// <summary>And five bands can say things four cannot, which is the whole point of them.</summary>
    [Fact]
    public void FiveBandsCanSayValuesFourCannot()
    {
        var loose = ColourCodes.For(1.21e3, 0.05);
        var tight = ColourCodes.For(1.21e3, 0.01);

        Assert.NotNull(loose);
        Assert.NotNull(tight);

        // Four bands round it to 1.2k and say so; five spell it exactly.
        Assert.False(loose.IsExact);
        Assert.Equal(1.2e3, loose.Represented, 1e-9);

        Assert.True(tight.IsExact);
        Assert.Equal(1.21e3, tight.Represented, 1e-9);
        Assert.Equal("brown red brown brown brown", tight.ToString());
    }

    [Theory]
    [InlineData(0.005, "green")]
    [InlineData(0.001, "violet")]
    [InlineData(0.0025, "blue")]
    [InlineData(0.02, "red")]
    public void EveryPrecisionToleranceHasItsOwnColour(double tolerance, string expected)
    {
        Assert.EndsWith(expected, Read(1e3, tolerance), StringComparison.Ordinal);
    }

    /// <summary>
    /// A tolerance between two bands is painted as the tighter one. The marking is a promise, and
    /// rounding it the loose way would turn it into a lie — a part sold at 3 % may not be marked
    /// as 5 %.
    /// </summary>
    [Fact]
    public void AToleranceBetweenTwoBandsIsPaintedAsTheTighterOne()
    {
        Assert.EndsWith("gold", Read(1e3, 0.03), StringComparison.Ordinal);
        Assert.EndsWith("brown", Read(1e3, 0.008), StringComparison.Ordinal);
    }

    // ---- values the code cannot say ----------------------------------------

    /// <summary>
    /// A value it cannot spell is still shown, at the nearest the bands can say, with a note. A
    /// resistor of 1234 Ω is a real thing somebody can type, and refusing to draw anything would
    /// be less use than drawing the 1.2 kΩ they would actually buy.
    /// </summary>
    [Fact]
    public void AValueTheBandsCannotSpellIsRoundedAndSaidToBe()
    {
        var code = ColourCodes.For(1234, 0.05);

        Assert.NotNull(code);
        Assert.False(code.IsExact);
        Assert.Equal(1.2e3, code.Represented, 1e-9);
        Assert.Contains("nearest", code.Note!);
    }

    /// <summary>
    /// Rounding up through a decade carries properly: 99.6 Ω to two figures is 100, which is three
    /// digits, so it becomes 10 × 10.
    /// </summary>
    [Fact]
    public void RoundingUpThroughADecadeCarries()
    {
        var code = ColourCodes.For(99.6, 0.05);

        Assert.NotNull(code);
        Assert.Equal(100.0, code.Represented, 1e-9);
        Assert.Equal("brown black brown gold", code.ToString());
    }

    /// <summary>
    /// Outside what the multiplier band can reach there is no code, and nothing is drawn rather
    /// than something invented.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-100.0)]
    [InlineData(0.001)]
    [InlineData(1e12)]
    [InlineData(double.NaN)]
    public void AValueWithNoCodeGetsNone(double ohms)
    {
        Assert.Null(ColourCodes.For(ohms, 0.05));
    }

    // ---- the bands describe themselves -------------------------------------

    /// <summary>
    /// Each band says what it means in its position, which is what makes the picture teach rather
    /// than decorate: the third band of a 1 kΩ is not "red", it is "× 100".
    /// </summary>
    [Fact]
    public void EachBandSaysWhatItMeansWhereItIs()
    {
        var code = ColourCodes.For(1e3, 0.05);

        Assert.NotNull(code);
        Assert.Equal(["1", "0", "× 100", "± 5 %"], code.Bands.Select(b => b.Meaning));
    }

    [Fact]
    public void TheMultiplierReadsAsAMultiplication()
    {
        Assert.Equal("× 1", ColourCodes.For(10, 0)!.Bands[^1].Meaning);
        Assert.Equal("÷ 10", ColourCodes.For(4.7, 0)!.Bands[^1].Meaning);
        Assert.Equal("× 1,000,000", ColourCodes.For(10e6, 0)!.Bands[^1].Meaning);
    }
}

/// <summary>
/// The three-digit code printed on a ceramic capacitor, which catches everybody once: 104 is not
/// 104 of anything, it is 10 followed by four zeros — in picofarads, however large the part is.
/// </summary>
public class PrintedCodeTests
{
    private static string Read(double farads, double tolerance = 0.10) =>
        PrintedCodes.ForCapacitance(farads, tolerance)?.Code ?? "none";

    [Theory]
    [InlineData(100e-9, "104K")]
    [InlineData(10e-9, "103K")]
    [InlineData(1e-9, "102K")]
    [InlineData(1e-6, "105K")]
    [InlineData(22e-12, "220K")]
    [InlineData(470e-12, "471K")]
    public void TheCodesPrintedOnRealCapacitors(double farads, string expected)
    {
        Assert.Equal(expected, Read(farads));
    }

    /// <summary>
    /// The trap, stated as a test: 100 nF is 104 and 1 µF is 105. Somebody reading the code as
    /// microfarads is out by a factor of a million.
    /// </summary>
    [Fact]
    public void TheCodeIsAlwaysInPicofaradsHoweverLargeThePartIs()
    {
        Assert.Equal(100e-9, PrintedCodes.ForCapacitance(100e-9, 0)!.Represented, 1e-18);
        Assert.Equal("104", PrintedCodes.ForCapacitance(100e-9, 0)!.Code);

        Assert.Equal("105", PrintedCodes.ForCapacitance(1e-6, 0)!.Code);
    }

    [Theory]
    [InlineData(0.05, "J")]
    [InlineData(0.10, "K")]
    [InlineData(0.20, "M")]
    [InlineData(0.01, "F")]
    public void TheToleranceLetterIsItsOwnAlphabet(double tolerance, string letter)
    {
        Assert.EndsWith(letter, Read(100e-9, tolerance), StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoToleranceThereIsNoLetter()
    {
        var code = PrintedCodes.ForCapacitance(100e-9, 0);

        Assert.NotNull(code);
        Assert.Equal("104", code.Code);
        Assert.Null(code.ToleranceLetter);
    }

    /// <summary>It reads itself out, which is what turns the picture into an explanation.</summary>
    [Fact]
    public void ItSaysHowToReadItself()
    {
        var code = PrintedCodes.ForCapacitance(100e-9, 0.10);

        Assert.NotNull(code);
        Assert.Equal("10", code.Digits);
        Assert.Equal("and 4 zeros", code.Multiplier);
        Assert.Equal("± 10 %", code.ToleranceText);
    }

    /// <summary>
    /// Under ten picofarads the value is printed rather than coded, because two digits and a
    /// count of zeros cannot say 4.7 pF.
    /// </summary>
    [Theory]
    [InlineData(4.7e-12)]
    [InlineData(0.0)]
    [InlineData(-1e-9)]
    [InlineData(1.0)]
    public void AValueWithNoCodeGetsNone(double farads)
    {
        Assert.Equal("none", Read(farads));
    }

    [Fact]
    public void AValueTheDigitsCannotSpellIsRoundedAndSaidToBe()
    {
        var code = PrintedCodes.ForCapacitance(123e-9, 0.10);

        Assert.NotNull(code);
        Assert.False(code.IsExact);
        Assert.Equal(120e-9, code.Represented, 1e-18);
        Assert.Contains("nearest", code.Note!);
    }
}
