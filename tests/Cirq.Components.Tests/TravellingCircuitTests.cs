using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Serialization;
using Cirq.Components.Spice;
using Cirq.Core.Topology;

namespace Cirq.Components.Tests;

/// <summary>
/// A circuit that opens complete on a machine that has never seen it.
/// <para>
/// A part built in to this library is saved by name and found again by name, which works anywhere
/// because the name means the same thing in every copy of the application. An <b>imported</b> part
/// does not: its definition lives in a file in somebody's application data, and a circuit sent to
/// a colleague arrived naming a model that was nowhere to be found. It opened with a warning and
/// a default diode — which is to say it opened as a different circuit, giving different answers,
/// with nothing on the screen to say so.
/// </para>
/// <para>
/// So a saved circuit carries the cards for the imported models it uses. Nothing else: not
/// built-ins, which are identical everywhere, and not the rest of somebody's library, which is
/// theirs and not the file's business.
/// </para>
/// </summary>
public class TravellingCircuitTests
{
    private const string Card = ".model TRAVELLER D(Is=4.7n N=1.63 Rs=0.31 Bv=42)";

    /// <summary>Registers a model, and takes it away again however the test ends.</summary>
    private static IDisposable Imported(string card)
    {
        var parsed = SpiceModelReader.Parse(card);
        var model = Assert.Single(parsed.Cards);

        SpiceModelImport.Register(model);

        return new Removal(model.Name, model.Kind);
    }

    private sealed record Removal(string Name, SpiceDeviceKind Kind) : IDisposable
    {
        public void Dispose() => SpiceModelImport.Unregister(Name, Kind);
    }

    private static Circuit WithImportedDiode(string modelName)
    {
        var circuit = new Circuit { Title = "Travelling" };
        var diode = circuit.Add(new Diode
        {
            Model = DiodeModel.Library.Single(m =>
                string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase)),
        });
        var resistor = circuit.Add(new Resistor(1e3));

        circuit.Connect(diode.Cathode, resistor.A);

        return circuit;
    }

    // ---- what a file carries ----------------------------------------------

    /// <summary>
    /// The whole point: the machine forgets the model entirely — as a colleague's machine never
    /// knew it — and the file still opens as the circuit that was saved.
    /// </summary>
    [Fact]
    public void ACircuitOpensCompleteOnAMachineThatNeverImportedItsModels()
    {
        string json;

        using (Imported(Card))
        {
            json = CircuitSerializer.ToJson(WithImportedDiode("TRAVELLER"));
        }

        // Gone, as it would be anywhere else.
        Assert.DoesNotContain(DiodeModel.Library, m => m.Name == "TRAVELLER");

        var result = CircuitSerializer.FromJson(json);

        try
        {
            Assert.True(result.IsClean, string.Join("; ", result.Warnings));

            var diode = result.Circuit.Components.OfType<Diode>().Single();

            Assert.Equal("TRAVELLER", diode.Model.Name);
            Assert.Equal(4.7e-9, diode.Model.SaturationCurrent, 15);
            Assert.Equal(1.63, diode.Model.EmissionCoefficient, 9);
            Assert.Equal(42.0, diode.Model.BreakdownVoltage, 9);
        }
        finally
        {
            SpiceModelImport.Unregister("TRAVELLER", SpiceDeviceKind.Diode);
        }
    }

    /// <summary>
    /// Without the card the same file is a different circuit, and this is what it used to be. Kept
    /// as a test because it is the behaviour the feature exists to replace, and because it is
    /// still what happens to a file written before the feature existed.
    /// </summary>
    [Fact]
    public void WithoutTheCardTheSameFileFallsBackAndSaysSo()
    {
        string json;

        using (Imported(Card))
        {
            json = CircuitSerializer.ToJson(WithImportedDiode("TRAVELLER"));
        }

        var stripped = System.Text.RegularExpressions.Regex.Replace(
            json, "\"Models\"\\s*:\\s*\\[.*?\\]", "\"Models\": null",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        var result = CircuitSerializer.FromJson(stripped);

        Assert.False(result.IsClean);
        Assert.Contains(result.Warnings, w => w.Contains("TRAVELLER"));
        Assert.NotEqual("TRAVELLER", result.Circuit.Components.OfType<Diode>().Single().Model.Name);
    }

    /// <summary>
    /// An ordinary circuit's file does not change at all. Every built-in model means the same
    /// thing in every copy of the application, so copying its definition into the file would be
    /// noise — and noise in a format people read and diff.
    /// </summary>
    [Fact]
    public void ACircuitOfBuiltInPartsCarriesNoModelsAtAll()
    {
        var circuit = new Circuit();
        circuit.Add(new Diode());
        circuit.Add(new Resistor(1e3));
        circuit.Add(new BipolarTransistor());

        Assert.Null(CircuitSerializer.ToDocument(circuit).Models);
        Assert.DoesNotContain("\"Models\"", CircuitSerializer.ToJson(circuit));
    }

    /// <summary>
    /// Only what is used. A file is not an export of everything on the machine that wrote it —
    /// somebody's library is theirs, and a circuit with one imported diode in it should not carry
    /// the forty models they happened to have imported that week.
    /// </summary>
    [Fact]
    public void OnlyTheModelsTheCircuitActuallyUsesAreCarried()
    {
        using (Imported(Card))
        using (Imported(".model BYSTANDER D(Is=1n N=1.1)"))
        {
            var models = CircuitSerializer.ToDocument(WithImportedDiode("TRAVELLER")).Models;

            Assert.Equal("TRAVELLER", Assert.Single(models!).Name);
        }
    }

    /// <summary>A model inside a block travels as readily as one on the sheet.</summary>
    [Fact]
    public void AModelInsideABlockTravelsToo()
    {
        string json;

        using (Imported(Card))
        {
            var circuit = WithImportedDiode("TRAVELLER");

            Assert.NotNull(Hierarchy.Grouping.Group(circuit, [.. circuit.Components], "Travelling"));

            json = CircuitSerializer.ToJson(circuit);
        }

        var result = CircuitSerializer.FromJson(json);

        try
        {
            var diode = result.Circuit.Components
                .OfType<Hierarchy.Subcircuit>().Single()
                .Descendants().OfType<Diode>().Single();

            Assert.Equal("TRAVELLER", diode.Model.Name);
            Assert.Equal(4.7e-9, diode.Model.SaturationCurrent, 15);
        }
        finally
        {
            SpiceModelImport.Unregister("TRAVELLER", SpiceDeviceKind.Diode);
        }
    }

    // ---- what a file must not do ------------------------------------------

    /// <summary>
    /// Opening a file does not rewrite somebody's parts. If their model of that name and the
    /// file's disagree, theirs is the one every other circuit on the machine was built against,
    /// and swapping it would change answers in circuits nobody had open.
    /// </summary>
    [Fact]
    public void AModelAlreadyHereWinsAndTheDisagreementIsReported()
    {
        string json;

        using (Imported(Card))
        {
            json = CircuitSerializer.ToJson(WithImportedDiode("TRAVELLER"));
        }

        // The same name, a different part — which is exactly the dangerous case.
        using (Imported(".model TRAVELLER D(Is=9.9n N=1.05)"))
        {
            var result = CircuitSerializer.FromJson(json);

            Assert.Contains(result.Warnings, w =>
                w.Contains("TRAVELLER") && w.Contains("already here"));

            var diode = result.Circuit.Components.OfType<Diode>().Single();

            Assert.Equal("TRAVELLER", diode.Model.Name);
            Assert.Equal(9.9e-9, diode.Model.SaturationCurrent, 15);
        }
    }

    /// <summary>
    /// And when they agree there is nothing to report. Two people who imported the same card off
    /// the same datasheet should be able to swap circuits without a dialog every time.
    /// </summary>
    [Fact]
    public void AModelAlreadyHereAndIdenticalIsNotWorthMentioning()
    {
        string json;

        using (Imported(Card))
        {
            json = CircuitSerializer.ToJson(WithImportedDiode("TRAVELLER"));

            var result = CircuitSerializer.FromJson(json);

            Assert.True(result.IsClean, string.Join("; ", result.Warnings));
        }
    }

    /// <summary>
    /// A built-in counts as already here, so a file naming one never gets to redefine it — which
    /// is the same protection, for the name anybody is likeliest to collide with.
    /// </summary>
    [Fact]
    public void AFileCannotRedefineABuiltIn()
    {
        string json;

        using (Imported(".model 1N4148 D(Is=1e-6 N=2.5)"))
        {
            var circuit = new Circuit();
            circuit.Add(new Diode
            {
                Model = DiodeModel.Library.Single(m => m.Name == "1N4148"),
            });

            json = CircuitSerializer.ToJson(circuit);
        }

        var result = CircuitSerializer.FromJson(json);
        var diode = result.Circuit.Components.OfType<Diode>().Single();

        Assert.Equal(DiodeModel.D1N4148.SaturationCurrent, diode.Model.SaturationCurrent, 15);
        Assert.Contains(result.Warnings, w => w.Contains("1N4148"));
    }

    // ---- the card itself ---------------------------------------------------

    /// <summary>
    /// What is written is regenerated, not the text that came in — so a card that arrived over
    /// five continuation lines goes out as one, and two people who pasted the same model in
    /// different layouts get identical files.
    /// </summary>
    [Fact]
    public void ACardWrittenBackOutReadsAsTheSameCard()
    {
        var original = Assert.Single(SpiceModelReader.Parse(
            """
            * Somebody's datasheet header
            .model WRAPPED NPN(Is=1e-15 Bf=180
            + Br=3 Vaf=95        ; the Early voltage
            + Nf=1.02)
            """).Cards);

        var again = Assert.Single(SpiceModelReader.Parse(original.ToCard()).Cards);

        Assert.Equal(original.Name, again.Name);
        Assert.Equal(original.Kind, again.Kind);
        Assert.Equal(original.Parameters.Count, again.Parameters.Count);

        foreach (var (key, value) in original.Parameters)
            Assert.Equal(value, again.Parameters[key], 15);

        // And is the same text the second time round, which is what makes a file diffable.
        Assert.Equal(original.ToCard(), again.ToCard());
    }

    /// <summary>Every kind of device travels, not only diodes.</summary>
    [Theory]
    [InlineData(".model TRAVELNPN NPN(Is=2e-15 Bf=220 Vaf=88)")]
    [InlineData(".model TRAVELPNP PNP(Is=3e-15 Bf=160)")]
    [InlineData(".model TRAVELNMOS NMOS(Vto=2.8 Kp=18 Lambda=0.02)")]
    [InlineData(".model TRAVELPMOS PMOS(Vto=-3.1 Kp=9)")]
    public void EveryKindOfImportedDeviceTravels(string card)
    {
        var parsed = Assert.Single(SpiceModelReader.Parse(card).Cards);

        using (Imported(card))
        {
            var written = parsed.ToCard();

            Assert.Equal(written, SpiceModelImport.CardFor(parsed.Name)!.ToCard());
        }

        Assert.Null(SpiceModelImport.CardFor(parsed.Name));
    }

    /// <summary>
    /// A model can be taken out through its own library rather than through the importer, and
    /// when it is, the card behind it has to go too. A card left behind for a name that has gone
    /// back to meaning the built-in would have a file carry a definition of a part it is not
    /// using — which is worse than carrying nothing, because it would be believed.
    /// </summary>
    [Fact]
    public void ACardWhoseModelHasGoneIsNotCarried()
    {
        using (Imported(".model STALE D(Is=5n N=1.3)"))
        {
            Assert.NotNull(SpiceModelImport.CardFor("STALE"));

            DiodeModel.Unregister("STALE");

            Assert.Null(SpiceModelImport.CardFor("STALE"));
        }
    }

    /// <summary>
    /// The same, for the case that actually happens: an import shadowing a built-in is removed,
    /// the name goes back to meaning the shipped part, and a circuit using it must be saved as
    /// using the shipped part.
    /// </summary>
    [Fact]
    public void RemovingAnImportThatShadowedABuiltInStopsCarryingIt()
    {
        using (Imported(".model 1N4148 D(Is=1e-7 N=2.2)"))
        {
            Assert.NotNull(SpiceModelImport.CardFor("1N4148"));
        }

        Assert.Null(SpiceModelImport.CardFor("1N4148"));

        var circuit = new Circuit();
        circuit.Add(new Diode(DiodeModel.Library.Single(m => m.Name == "1N4148")));

        Assert.Null(CircuitSerializer.ToDocument(circuit).Models);
    }
}
