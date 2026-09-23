using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Units;
using Cirq.UI.Services;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// What the hover card shows beside the numbers: the part's value as it is written on the part.
/// <para>
/// The arithmetic of the codes themselves is tested against the standards in
/// <c>ColourCodeTests</c>; what is checked here is which parts get a marking at all, and that the
/// marking a part gets is the one that part really carries.
/// </para>
/// </summary>
public class ComponentMarkingTests
{
    [Fact]
    public void AResistorIsBanded()
    {
        var marking = ComponentMarkings.For(new Resistor(10e3) { Tolerance = 0.05 });

        Assert.NotNull(marking);
        Assert.Equal(MarkingKind.Bands, marking.Kind);
        Assert.Equal(
            [BandColour.Brown, BandColour.Black, BandColour.Orange, BandColour.Gold],
            marking.Bands.Select(b => b.Colour));

        // Exact, so there is nothing to apologise for.
        Assert.Null(marking.Note);
        Assert.Equal("10kΩ", marking.Caption);
    }

    /// <summary>
    /// An inductor carries the same four bands, but they are read in <b>microhenries</b> — the
    /// same trap as a ceramic's picofarads, and for the same reason: the multiplier band cannot
    /// reach 10⁻⁴ at all, so a part marked in henries would have no code to paint.
    /// </summary>
    [Fact]
    public void AndSoIsAnInductorButInMicrohenries()
    {
        var marking = ComponentMarkings.For(new Inductor(100e-6) { Tolerance = 0.1 });

        Assert.NotNull(marking);
        Assert.Equal(MarkingKind.Bands, marking.Kind);

        // 100 µH: brown-black-brown, and silver for the ten percent.
        Assert.Equal(
            [BandColour.Brown, BandColour.Black, BandColour.Brown, BandColour.Silver],
            marking.Bands.Select(b => b.Colour));

        // Read back in henries, whatever the bands are counted in.
        Assert.Equal(SiPrefix.Format(100e-6, "H"), marking.Caption);
        Assert.Null(marking.Note);
    }

    /// <summary>
    /// A ceramic has its value printed rather than painted, and the caption says the thing that
    /// catches everybody out — the digits are picofarads whatever the part is.
    /// </summary>
    [Fact]
    public void ACeramicIsPrinted()
    {
        var marking = ComponentMarkings.For(new Capacitor(100e-9) { Tolerance = 0.2 });

        Assert.NotNull(marking);
        Assert.Equal(MarkingKind.Printed, marking.Kind);
        Assert.Empty(marking.Bands);
        Assert.Equal("104M", marking.Code?.Code);
        Assert.Contains("in pF", marking.Caption);
        Assert.Contains("100nF", marking.Caption);
    }

    /// <summary>
    /// An electrolytic derives from <see cref="Capacitor"/> but is not marked like one: its value
    /// is printed on the can in plain microfarads. Giving it a ceramic's three-digit code would be
    /// inventing a marking the part does not carry, which is worse than showing nothing.
    /// </summary>
    [Fact]
    public void AnElectrolyticIsNotGivenACeramicsCode()
    {
        Assert.Null(ComponentMarkings.For(new ElectrolyticCapacitor(100e-6)));
    }

    /// <summary>Parts that carry a part number rather than a code get no picture.</summary>
    [Fact]
    public void AndNeitherIsAPartWithNoCodeAtAll()
    {
        Assert.Null(ComponentMarkings.For(new DcVoltageSource(5.0)));
        Assert.Null(ComponentMarkings.For(new Ground()));
    }

    /// <summary>
    /// A tolerance tight enough to need three significant figures gets the five-band form, because
    /// you cannot promise one percent on a value you only spelled to two figures.
    /// </summary>
    [Fact]
    public void ATightToleranceGetsTheFiveBandForm()
    {
        var marking = ComponentMarkings.For(new Resistor(1.21e3) { Tolerance = 0.01 });

        Assert.NotNull(marking);
        Assert.Equal(5, marking.Bands.Count);
        Assert.True(marking.HasTolerance);
    }

    /// <summary>
    /// A value the bands cannot spell says so, and says what they do spell. Silently drawing the
    /// nearest bands would have the card assert something about the part that is not true.
    /// </summary>
    [Fact]
    public void AValueTheBandsCannotSpellSaysWhatTheyDoSpell()
    {
        var marking = ComponentMarkings.For(new Resistor(1234) { Tolerance = 0.05 });

        Assert.NotNull(marking);
        Assert.NotNull(marking.Note);
        Assert.Equal("1.2kΩ", marking.Caption);
    }

    /// <summary>
    /// A part with no tolerance at all has no tolerance band, so there is no wider gap and nothing
    /// to say which end to read from — which is exactly how an unmarked part behaves.
    /// </summary>
    [Fact]
    public void NoToleranceMeansNoToleranceBand()
    {
        var marking = ComponentMarkings.For(new Resistor(10e3) { Tolerance = 0 });

        Assert.NotNull(marking);
        Assert.False(marking.HasTolerance);
        Assert.DoesNotContain(marking.Bands, b => b.Colour == BandColour.Gold);
    }

    /// <summary>The card carries the marking, which is how it reaches the drawing at all.</summary>
    [Fact]
    public void TheSummaryCarriesIt()
    {
        var summary = ComponentSummary.For(new Resistor(4.7e3) { Name = "R7" });

        Assert.NotNull(summary.Marking);
        Assert.Equal(
            [BandColour.Yellow, BandColour.Violet, BandColour.Red, BandColour.Gold],
            summary.Marking.Bands.Select(b => b.Colour));

        Assert.Null(ComponentSummary.For(new Ground()).Marking);
    }

    /// <summary>
    /// Every part in the palette either has a marking that can be drawn or has none. A marking
    /// that threw while being worked out would take the canvas down with it, since it is built
    /// during a render.
    /// </summary>
    [Fact]
    public void EveryPaletteEntryEitherHasAMarkingOrHasNone()
    {
        foreach (var item in ComponentCatalog.AllItems)
        {
            var component = item.Create();

            if (ComponentMarkings.For(component) is not { } marking) continue;

            Assert.False(string.IsNullOrWhiteSpace(marking.Caption));

            if (marking.Kind == MarkingKind.Bands)
            {
                Assert.NotEmpty(marking.Bands);
                Assert.Null(marking.Code);
            }
            else
            {
                Assert.NotNull(marking.Code);
            }
        }
    }
}
