using Cirq.Components.Nonlinear;
using Cirq.Components.Spice;

namespace Cirq.Components.Tests;

/// <summary>
/// Reading SPICE model cards, which is what turns the parts library from "what is built in" into
/// "anything with a datasheet".
/// </summary>
public class SpiceImportTests
{
    // ---- the numbers -------------------------------------------------------

    [Theory]
    [InlineData("1", 1.0)]
    [InlineData("2.5", 2.5)]
    [InlineData("-3", -3.0)]
    [InlineData("2.52n", 2.52e-9)]
    [InlineData("1E-9", 1e-9)]
    [InlineData("1.5e3", 1500.0)]
    [InlineData("4k7", 4000.0)]
    [InlineData("10k", 1e4)]
    [InlineData("2.2u", 2.2e-6)]
    [InlineData("100p", 1e-10)]
    [InlineData("3T", 3e12)]
    public void SpiceNumbersParseWithTheirSuffixes(string text, double expected)
    {
        Assert.Equal(expected, SpiceValue.Parse(text)!.Value, Math.Abs(expected) * 1e-9);
    }

    /// <summary>
    /// The trap that has been catching people since the seventies, and the reason this is parsed
    /// rather than handed to double.Parse.
    /// </summary>
    [Fact]
    public void MeansMilliAndMegMeansMega()
    {
        Assert.Equal(1e-3, SpiceValue.Parse("1M")!.Value, 12);
        Assert.Equal(1e-3, SpiceValue.Parse("1m")!.Value, 12);

        Assert.Equal(1e6, SpiceValue.Parse("1MEG")!.Value, 6);
        Assert.Equal(1e6, SpiceValue.Parse("1meg")!.Value, 6);
        Assert.Equal(1e6, SpiceValue.Parse("1Meg")!.Value, 6);
    }

    [Fact]
    public void AnythingAfterTheSuffixIsDecoration()
    {
        Assert.Equal(1e3, SpiceValue.Parse("1kOhm")!.Value, 9);
        Assert.Equal(2.2e-6, SpiceValue.Parse("2.2uF")!.Value, 12);
        Assert.Equal(1e6, SpiceValue.Parse("1MegHz")!.Value, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData(null)]
    public void SomethingThatIsNotANumberIsNotOne(string? text)
    {
        Assert.Null(SpiceValue.Parse(text));
    }

    // ---- the cards ---------------------------------------------------------

    [Fact]
    public void ADiodeCardIsReadIntoADiodeModel()
    {
        var result = SpiceModelReader.Parse(".model 1N4148 D(Is=2.52n Rs=0.568 N=1.752 Bv=75 Ibv=5u)");

        Assert.Empty(result.Problems);

        var card = Assert.Single(result.Cards);

        Assert.Equal("1N4148", card.Name);
        Assert.Equal(SpiceDeviceKind.Diode, card.Kind);

        var model = SpiceModelImport.ToDiode(card);

        Assert.Equal(2.52e-9, model.SaturationCurrent, 15);
        Assert.Equal(1.752, model.EmissionCoefficient, 6);
        Assert.Equal(0.568, model.SeriesResistance, 6);
        Assert.Equal(75.0, model.BreakdownVoltage, 6);
        Assert.Equal(5e-6, model.BreakdownCurrent, 12);
    }

    [Fact]
    public void ABipolarCardCarriesItsPolarityAndItsGain()
    {
        var result = SpiceModelReader.Parse(
            ".model 2N3906 PNP(Is=1.41f Bf=180 Br=4.977 Vaf=18.7 Nf=1.0)");

        var model = SpiceModelImport.ToBipolar(Assert.Single(result.Cards));

        Assert.Equal(BjtPolarity.Pnp, model.Polarity);
        Assert.Equal(1.41e-15, model.SaturationCurrent, 15);
        Assert.Equal(180, model.ForwardBeta, 6);
        Assert.Equal(18.7, model.EarlyVoltage, 6);
    }

    /// <summary>
    /// A P-channel card quotes a negative threshold; this library works in the channel's own
    /// convention, where the overdrive is positive either way.
    /// </summary>
    [Fact]
    public void AMosfetCardsNegativeThresholdIsBroughtIntoTheChannelsOwnConvention()
    {
        var result = SpiceModelReader.Parse(".model IRF9540 PMOS(Vto=-3.7 Kp=0.8 Lambda=0.01)");

        var model = SpiceModelImport.ToMosfet(Assert.Single(result.Cards));

        Assert.Equal(MosfetChannel.PChannel, model.Channel);
        Assert.Equal(3.7, model.ThresholdVoltage, 6);
        Assert.Equal(0.8, model.TransconductanceParameter, 6);
        Assert.Equal(0.01, model.ChannelLengthModulation, 6);
    }

    [Fact]
    public void ACardWrappedOverSeveralLinesIsOneCard()
    {
        var result = SpiceModelReader.Parse("""
            * A datasheet model, as they are usually published
            .model BC547B NPN(Is=7.049f Xti=3 Eg=1.11 Vaf=62.79
            + Bf=374.6 Ne=1.259 Ise=7.049f Ikf=0.08157
            + Nk=0.5 Xtb=1.5 Br=1.001)
            """);

        Assert.Empty(result.Problems);

        var card = Assert.Single(result.Cards);

        Assert.Equal("BC547B", card.Name);
        Assert.Equal(374.6, card.Value(0, "BF"), 3);
        Assert.Equal(62.79, card.Value(0, "VAF"), 3);
    }

    [Fact]
    public void CommentsAndBlankLinesAreIgnored()
    {
        var result = SpiceModelReader.Parse("""
            * a heading

            .model D1 D(Is=1n)   ; the one we want
            ; a whole comment line
            """);

        var card = Assert.Single(result.Cards);
        Assert.Equal("D1", card.Name);
        Assert.Equal(1e-9, card.Value(0, "IS"), 15);
    }

    [Fact]
    public void SeveralCardsInOneBlockAllComeBack()
    {
        var result = SpiceModelReader.Parse("""
            .model DX D(Is=1n)
            .model QX NPN(Bf=100)
            .model MX NMOS(Vto=2 Kp=0.05)
            """);

        Assert.Equal(3, result.Cards.Count);
        Assert.Empty(result.Problems);
    }

    // ---- what it will not do -----------------------------------------------

    /// <summary>
    /// A card for a device this simulator does not have is reported rather than silently dropped.
    /// </summary>
    [Fact]
    public void ADeviceThereIsNoModelForIsNamedRatherThanIgnored()
    {
        var result = SpiceModelReader.Parse(".model J1 NJF(Vto=-2 Beta=1m)");

        Assert.Empty(result.Cards);

        var problem = Assert.Single(result.Problems);

        Assert.Contains("J1", problem);
        Assert.Contains("NJF", problem);
    }

    [Fact]
    public void TextWithNoCardsInItSaysWhatOneLooksLike()
    {
        var result = SpiceModelReader.Parse("R1 1 2 10k\nV1 1 0 DC 5");

        Assert.Empty(result.Cards);
        Assert.Contains(".model", Assert.Single(result.Problems));
    }

    /// <summary>
    /// Parameters this simulator has nowhere to put are reported. Quietly dropping them would
    /// leave somebody believing their part was modelled more closely than it is.
    /// </summary>
    [Fact]
    public void ParametersThatCannotBeUsedAreNamedInTheReport()
    {
        var result = SpiceModelReader.Parse(
            ".model D1 D(Is=1n N=1.8 Cjo=4p Tt=11n Rs=0.5)");

        var imported = SpiceModelImport.Register(Assert.Single(result.Cards));

        try
        {
            Assert.Contains("IS", imported.Used, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("RS", imported.Used, StringComparer.OrdinalIgnoreCase);

            Assert.Contains("CJO", imported.Ignored, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("TT", imported.Ignored, StringComparer.OrdinalIgnoreCase);

            Assert.Contains("ignored", imported.Summary);
        }
        finally
        {
            DiodeModel.Unregister("D1");
        }
    }

    // ---- registration ------------------------------------------------------

    [Fact]
    public void AnImportedModelJoinsTheLibraryAndCanBeTakenOutAgain()
    {
        var before = DiodeModel.Library.Count;

        var result = SpiceModelReader.Parse(".model MYDIODE D(Is=3.3n N=1.9 Rs=0.25)");
        SpiceModelImport.Register(Assert.Single(result.Cards));

        try
        {
            Assert.Equal(before + 1, DiodeModel.Library.Count);

            var model = DiodeModel.Library.Single(m => m.Name == "MYDIODE");

            Assert.Equal(3.3e-9, model.SaturationCurrent, 15);
        }
        finally
        {
            Assert.True(DiodeModel.Unregister("MYDIODE"));
        }

        Assert.Equal(before, DiodeModel.Library.Count);
    }

    [Fact]
    public void ImportingTheSameNameTwiceReplacesRatherThanDuplicates()
    {
        try
        {
            SpiceModelImport.Register(SpiceModelReader.Parse(".model DUP D(Is=1n)").Cards[0]);
            SpiceModelImport.Register(SpiceModelReader.Parse(".model DUP D(Is=5n)").Cards[0]);

            var model = Assert.Single(DiodeModel.Library, m => m.Name == "DUP");

            Assert.Equal(5e-9, model.SaturationCurrent, 15);
        }
        finally
        {
            DiodeModel.Unregister("DUP");
        }
    }

    /// <summary>The real test: an imported part actually simulates.</summary>
    [Fact]
    public void AnImportedDiodeSolvesLikeTheOneItWasCopiedFrom()
    {
        var result = SpiceModelReader.Parse(".model COPY4148 D(Is=2.52n Rs=0.568 N=1.752 Bv=75)");
        SpiceModelImport.Register(result.Cards[0]);

        try
        {
            double Drop(DiodeModel model)
            {
                var circuit = new Cirq.Core.Topology.Circuit();
                var supply = circuit.Add(new Cirq.Components.Sources.DcVoltageSource(5.0));
                var series = circuit.Add(new Cirq.Components.Passive.Resistor(4.3e3));
                var diode = circuit.Add(new Diode(model));
                var ground = circuit.Add(new Cirq.Components.Sources.Ground());

                circuit.Connect(supply.Negative, ground.Pin);
                circuit.Connect(supply.Positive, series.A);
                circuit.Connect(series.B, diode.Anode);
                circuit.Connect(diode.Cathode, ground.Pin);

                var sim = new Cirq.Engine.Simulation.CircuitSimulator(circuit);
                sim.Reset();
                sim.SolveOperatingPoint();

                return sim.NodeVoltage(diode.Anode);
            }

            var imported = DiodeModel.Library.Single(m => m.Name == "COPY4148");

            // The card was copied from the built-in 1N4148's own parameters, so it has to land in
            // the same place.
            Assert.Equal(Drop(DiodeModel.D1N4148), Drop(imported), 3);
        }
        finally
        {
            DiodeModel.Unregister("COPY4148");
        }
    }
}
