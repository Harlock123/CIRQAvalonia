using Cirq.Components.Digital;
using Cirq.Components.Hierarchy;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Components.Spice;
using Cirq.Core.Topology;

namespace Cirq.Components.Tests;

/// <summary>
/// Writing a circuit out as a SPICE deck — the other half of importing one, and what keeps a
/// circuit drawn here from being trapped here.
/// </summary>
public class SpiceNetlistTests
{
    private static (Circuit, Resistor Top, Resistor Bottom) Divider()
    {
        var circuit = new Circuit { Title = "Divider" };
        var supply = circuit.Add(new DcVoltageSource(10.0));
        var top = circuit.Add(new Resistor(1e3));
        var bottom = circuit.Add(new Resistor(3e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);

        return (circuit, top, bottom);
    }

    private static IReadOnlyList<string> Elements(string deck) =>
        [.. deck.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('*') && !l.StartsWith('.'))];

    // ---- the elements ------------------------------------------------------

    [Fact]
    public void EveryPassiveAndSourceBecomesAnElementLine()
    {
        var (circuit, _, _) = Divider();
        circuit.Add(new Capacitor(1e-6));
        circuit.Add(new Inductor(10e-3));
        circuit.Add(new DcCurrentSource(1e-3));

        var result = SpiceNetlistWriter.Write(circuit);

        Assert.Empty(result.Skipped);
        Assert.False(result.IsEmpty);

        var elements = Elements(result.Netlist);

        Assert.Contains(elements, l => l.StartsWith("R1 "));
        Assert.Contains(elements, l => l.StartsWith("R2 "));
        Assert.Contains(elements, l => l.StartsWith("C1 "));
        Assert.Contains(elements, l => l.StartsWith("L1 "));
        Assert.Contains(elements, l => l.StartsWith("V1 "));
        Assert.Contains(elements, l => l.StartsWith("I1 "));
    }

    /// <summary>
    /// SPICE puts the element type in the first character, so a designator keeps its number and
    /// loses its letter — R1 would otherwise come out as RR1.
    /// </summary>
    [Fact]
    public void ADesignatorDoesNotGetItsPrefixTwice()
    {
        var (circuit, _, _) = Divider();

        var elements = Elements(SpiceNetlistWriter.Write(circuit).Netlist);

        Assert.DoesNotContain(elements, l => l.StartsWith("RR"));
        Assert.DoesNotContain(elements, l => l.StartsWith("VV"));
    }

    [Fact]
    public void GroundIsNodeZeroBecauseThatIsTheOneNameSpiceInsistsOn()
    {
        var (circuit, _, _) = Divider();

        var supply = Elements(SpiceNetlistWriter.Write(circuit).Netlist)
            .Single(l => l.StartsWith("V1 "));

        // "V1 <positive> 0 DC ..." — the return is ground.
        Assert.Equal("0", supply.Split(' ')[2]);
    }

    [Fact]
    public void ANamedNetKeepsItsNameSoTheDeckIsReadable()
    {
        var (circuit, top, _) = Divider();

        circuit.Connect(circuit.Add(new NetLabel("VCC")).Pin, top.A);

        var deck = SpiceNetlistWriter.Write(circuit).Netlist;

        Assert.Contains("VCC", deck);
        Assert.Contains(Elements(deck), l => l.StartsWith("R1 VCC "));
    }

    /// <summary>
    /// A current source's current flows from its first node through the source to its second,
    /// which is the opposite of the convention here — so the nodes are swapped on the way out.
    /// </summary>
    [Fact]
    public void ACurrentSourcesNodesAreSwappedForSpicesConvention()
    {
        var circuit = new Circuit();
        var drive = circuit.Add(new DcCurrentSource(1e-3));
        var load = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(drive.Positive, ground.Pin);
        circuit.Connect(drive.Negative, load.A);
        circuit.Connect(load.B, ground.Pin);

        var line = Elements(SpiceNetlistWriter.Write(circuit).Netlist).Single(l => l.StartsWith("I1 "));

        // Positive is on ground here, so it is SPICE's *second* node.
        var fields = line.Split(' ');

        Assert.NotEqual("0", fields[1]);
        Assert.Equal("0", fields[2]);
    }

    /// <summary>
    /// A generator is a voltage source with a waveform on it, which is exactly what SPICE has.
    /// Leaving it out exported analog circuits with nothing driving them.
    /// </summary>
    [Fact]
    public void AFunctionGeneratorBecomesASourceWithItsWaveform()
    {
        var circuit = new Circuit();
        var generator = circuit.Add(new FunctionGenerator(Waveform.Sine, 1e3, 4.0) { DcOffset = 2.5 });
        var load = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(generator.Return, ground.Pin);
        circuit.Connect(generator.Output, load.A);
        circuit.Connect(load.B, ground.Pin);

        var result = SpiceNetlistWriter.Write(circuit);

        Assert.Empty(result.Skipped);

        var line = Elements(result.Netlist).Single(l => l.Contains("SIN("));

        // Offset, amplitude as a half of peak-to-peak, and the frequency.
        Assert.Contains("SIN(2.5e+00 2e+00 1e+03", line);

        // Plus a DC and an AC magnitude, so .op and .ac both have something to work with.
        Assert.Contains("DC 2.5e+00 AC 2e+00", line);
    }

    /// <summary>
    /// SPICE has no triangle element, but PULSE takes explicit rise and fall times — and a
    /// triangle is a pulse that spends all of its period rising and falling.
    /// </summary>
    [Theory]
    [InlineData(Waveform.Square)]
    [InlineData(Waveform.Triangle)]
    [InlineData(Waveform.Sawtooth)]
    public void TheOtherShapesBecomeAPulseWithTheRightTimings(Waveform shape)
    {
        var circuit = new Circuit();
        var generator = circuit.Add(new FunctionGenerator(shape, 1e3, 2.0));
        circuit.Connect(generator.Return, circuit.Add(new Ground()).Pin);

        var line = Elements(SpiceNetlistWriter.Write(circuit).Netlist).Single(l => l.StartsWith('V'));

        Assert.Contains("PULSE(", line);

        // The period is the last field, and it is one over the frequency whatever the shape.
        var fields = line[(line.IndexOf("PULSE(", StringComparison.Ordinal) + 6)..]
            .TrimEnd(')')
            .Split(' ');

        Assert.Equal(1e-3, SpiceValue.Parse(fields[^1])!.Value, 9);

        // And it swings either side of the offset: -1 to +1 on a 2 Vpp generator at zero offset.
        Assert.Equal(-1.0, SpiceValue.Parse(fields[0])!.Value, 6);
        Assert.Equal(1.0, SpiceValue.Parse(fields[1])!.Value, 6);
    }

    [Fact]
    public void ADisabledGeneratorIsJustItsOffset()
    {
        var circuit = new Circuit();
        var generator = circuit.Add(
            new FunctionGenerator(Waveform.Sine, 1e3, 4.0) { DcOffset = 1.5, IsEnabled = false });

        circuit.Connect(generator.Return, circuit.Add(new Ground()).Pin);

        var line = Elements(SpiceNetlistWriter.Write(circuit).Netlist).Single(l => l.StartsWith('V'));

        Assert.DoesNotContain("SIN", line);
        Assert.Contains("DC 1.5e+00", line);
    }

    // ---- element names -----------------------------------------------------

    /// <summary>
    /// A designator's prefix is not always one letter. LED1 stripped of one character leaves ED1,
    /// which prefixed with D gave DED1.
    /// </summary>
    [Fact]
    public void AMultiLetterPrefixIsStrippedProperly()
    {
        var circuit = new Circuit();
        var led = circuit.Add(new Led());
        circuit.Connect(led.Cathode, circuit.Add(new Ground()).Pin);

        var line = Assert.Single(Elements(SpiceNetlistWriter.Write(circuit).Netlist),
            l => l.StartsWith('D'));

        Assert.StartsWith("D1 ", line);
        Assert.DoesNotContain("DED", line);
    }

    /// <summary>
    /// And a part already called V1 keeps V1, even when a generator that would also want it sorts
    /// earlier in the deck.
    /// </summary>
    [Fact]
    public void APartEntitledToItsOwnNameKeepsIt()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(15.0));
        var generator = circuit.Add(new FunctionGenerator(Waveform.Sine, 1e3, 1.0));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(generator.Return, ground.Pin);

        var elements = Elements(SpiceNetlistWriter.Write(circuit).Netlist);

        Assert.Equal("V1", Assert.Single(elements, l => l.StartsWith("V1 ")).Split(' ')[0]);

        // The generator gets a name of its own, and the two are different.
        var names = elements.Select(l => l.Split(' ')[0]).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("VFG1", names);
    }

    [Fact]
    public void EveryElementNameInADeckIsUnique()
    {
        var circuit = new Circuit();
        var ground = circuit.Add(new Ground());

        // Deliberately awkward: parts whose designators differ only by prefix.
        circuit.Connect(circuit.Add(new Diode()).Cathode, ground.Pin);
        circuit.Connect(circuit.Add(new Led()).Cathode, ground.Pin);
        circuit.Connect(circuit.Add(new DcVoltageSource(5)).Negative, ground.Pin);
        circuit.Connect(circuit.Add(new FunctionGenerator(Waveform.Sine, 1e3, 1)).Return, ground.Pin);

        for (var i = 0; i < 3; i++)
            circuit.Connect(circuit.Add(new Resistor(1e3)).B, ground.Pin);

        var names = Elements(SpiceNetlistWriter.Write(circuit).Netlist)
            .Select(l => l.Split(' ')[0])
            .ToList();

        Assert.Equal(7, names.Count);
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ---- models ------------------------------------------------------------

    [Fact]
    public void ADeviceGetsAModelCardSoTheDeckStandsOnItsOwn()
    {
        var circuit = new Circuit();
        var diode = circuit.Add(new Diode());
        var transistor = circuit.Add(new BipolarTransistor());
        var fet = circuit.Add(new Mosfet(MosfetModel.Irf9540));
        var ground = circuit.Add(new Ground());

        circuit.Connect(diode.Cathode, ground.Pin);
        circuit.Connect(transistor.Emitter, ground.Pin);
        circuit.Connect(fet.Source, ground.Pin);

        var deck = SpiceNetlistWriter.Write(circuit).Netlist;

        Assert.Contains(".model 1N4148 D(", deck);
        Assert.Contains(".model 2N3904 NPN(", deck);
        Assert.Contains(".model IRF9540 PMOS(", deck);
    }

    [Fact]
    public void AModelIsWrittenOnceHoweverManyPartsUseIt()
    {
        var circuit = new Circuit();
        var ground = circuit.Add(new Ground());

        for (var i = 0; i < 4; i++)
            circuit.Connect(circuit.Add(new Diode()).Cathode, ground.Pin);

        var deck = SpiceNetlistWriter.Write(circuit).Netlist;

        Assert.Equal(1, deck.Split(".model 1N4148").Length - 1);
        Assert.Equal(4, Elements(deck).Count(l => l.StartsWith('D')));
    }

    /// <summary>
    /// The strongest check available: the deck's own model cards read back through the importer to
    /// the models they were written from. Two independent pieces of code agreeing is worth more
    /// than either agreeing with itself.
    /// </summary>
    [Fact]
    public void TheModelCardsItWritesAreOnesItCanReadBack()
    {
        var circuit = new Circuit();
        var diode = circuit.Add(new Diode(DiodeModel.D1N4001));
        var transistor = circuit.Add(new BipolarTransistor(BjtModel.Bc547));
        var fet = circuit.Add(new Mosfet(MosfetModel.IrlZ44N));
        var ground = circuit.Add(new Ground());

        circuit.Connect(diode.Cathode, ground.Pin);
        circuit.Connect(transistor.Emitter, ground.Pin);
        circuit.Connect(fet.Source, ground.Pin);

        var deck = SpiceNetlistWriter.Write(circuit).Netlist;

        var parsed = SpiceModelReader.Parse(deck);

        Assert.Empty(parsed.Problems);
        Assert.Equal(3, parsed.Cards.Count);

        var read = SpiceModelImport.ToDiode(parsed.Cards.Single(c => c.Name == "1N4001"));

        Assert.Equal(DiodeModel.D1N4001.SaturationCurrent, read.SaturationCurrent, 15);
        Assert.Equal(DiodeModel.D1N4001.EmissionCoefficient, read.EmissionCoefficient, 6);
        Assert.Equal(DiodeModel.D1N4001.SeriesResistance, read.SeriesResistance, 6);
        Assert.Equal(DiodeModel.D1N4001.BreakdownVoltage, read.BreakdownVoltage, 6);

        var bjt = SpiceModelImport.ToBipolar(parsed.Cards.Single(c => c.Name == "BC547"));

        Assert.Equal(BjtModel.Bc547.ForwardBeta, bjt.ForwardBeta, 6);
        Assert.Equal(BjtModel.Bc547.EarlyVoltage, bjt.EarlyVoltage, 6);
        Assert.Equal(BjtPolarity.Npn, bjt.Polarity);

        var mos = SpiceModelImport.ToMosfet(parsed.Cards.Single(c => c.Name == "IRLZ44N"));

        Assert.Equal(MosfetModel.IrlZ44N.ThresholdVoltage, mos.ThresholdVoltage, 6);
        Assert.Equal(MosfetModel.IrlZ44N.TransconductanceParameter, mos.TransconductanceParameter, 6);
    }

    /// <summary>
    /// And a P-channel's threshold goes back out negative, which is how a card states it — the
    /// library works in the channel's own convention internally.
    /// </summary>
    [Fact]
    public void APChannelThresholdIsWrittenSigned()
    {
        var circuit = new Circuit();
        var fet = circuit.Add(new Mosfet(MosfetModel.Irf9540));
        circuit.Connect(fet.Source, circuit.Add(new Ground()).Pin);

        var deck = SpiceNetlistWriter.Write(circuit).Netlist;

        Assert.Contains("Vto=-3.5", deck);

        var card = SpiceModelReader.Parse(deck).Cards.Single();

        // Back through the importer it becomes positive again, as the library holds it.
        Assert.Equal(3.5, SpiceModelImport.ToMosfet(card).ThresholdVoltage, 6);
    }

    // ---- what it cannot carry ----------------------------------------------

    /// <summary>
    /// A netlist that quietly omits half a circuit is worse than one that says what it could not
    /// carry, so the parts with no SPICE equivalent are named in the deck as comments.
    /// </summary>
    [Fact]
    public void APartWithNoSpiceEquivalentIsNamedInTheDeckRatherThanDroppedSilently()
    {
        var (circuit, _, _) = Divider();
        var gate = circuit.Add(new Ic7400());

        var result = SpiceNetlistWriter.Write(circuit);

        Assert.Contains(gate.Name, Assert.Single(result.Skipped));
        Assert.Contains("7400", result.Skipped[0]);

        // In the deck, as a comment, with a sentence saying the circuit is incomplete there.
        Assert.Contains($"*   {gate.Name}", result.Netlist);
        Assert.Contains("no SPICE equivalent", result.Netlist);

        // And not as an element line.
        Assert.DoesNotContain(Elements(result.Netlist), l => l.Contains("7400"));
    }

    [Fact]
    public void GroundsLabelsAndAnnotationsAreNotReportedAsMissingBecauseTheyAreNotParts()
    {
        var (circuit, top, _) = Divider();

        circuit.Connect(circuit.Add(new NetLabel("RAIL")).Pin, top.A);
        circuit.Add(new Cirq.Components.Annotations.SchematicNote("A note"));
        circuit.Add(new Cirq.Components.Annotations.SchematicBox("A section"));

        Assert.Empty(SpiceNetlistWriter.Write(circuit).Skipped);
    }

    // ---- structure ---------------------------------------------------------

    [Fact]
    public void ABlocksContentsAppearAsOrdinaryPartsBecauseThatIsWhatTheyAre()
    {
        var (circuit, top, bottom) = Divider();

        Grouping.Group(circuit, [top, bottom], "Divider");

        var result = SpiceNetlistWriter.Write(circuit);

        // Both resistors are in the deck, and the block itself is neither an element nor missing.
        Assert.Contains("R1", result.Written);
        Assert.Contains("R2", result.Written);
        Assert.Empty(result.Skipped);
        Assert.DoesNotContain(Elements(result.Netlist), l => l.StartsWith('X'));
    }

    [Fact]
    public void TheDeckCarriesTheCircuitsTemperatureAndEndsProperly()
    {
        var (circuit, _, _) = Divider();
        circuit.AmbientTemperatureCelsius = 85;

        var deck = SpiceNetlistWriter.Write(circuit).Netlist;

        Assert.Contains(".temp 85", deck);
        Assert.Contains(".op", deck);
        Assert.EndsWith(".end" + Environment.NewLine, deck);

        // And the first line is a title, which SPICE always treats as a comment.
        Assert.StartsWith("* Divider", deck);
    }

    /// <summary>
    /// Values are written in exponential form rather than with a SPICE suffix on purpose: 1e-3
    /// cannot be misread, where 1m is milli to SPICE and mega to about half the people reading it.
    /// </summary>
    [Fact]
    public void ValuesAreWrittenUnambiguouslyRatherThanWithASuffix()
    {
        var circuit = new Circuit();
        var resistor = circuit.Add(new Resistor(1e6));
        circuit.Connect(resistor.B, circuit.Add(new Ground()).Pin);

        var line = Elements(SpiceNetlistWriter.Write(circuit).Netlist).Single(l => l.StartsWith("R1 "));

        Assert.Contains("e+", line);
        Assert.DoesNotContain("1M", line);

        // And it reads back as the megohm it is, not as a milliohm.
        Assert.Equal(1e6, SpiceValue.Parse(line.Split(' ')[^1])!.Value, 3);
    }

    [Fact]
    public void AnEmptyCircuitGivesADeckThatSaysSoRatherThanThrowing()
    {
        var result = SpiceNetlistWriter.Write(new Circuit());

        Assert.True(result.IsEmpty);
        Assert.Contains(".end", result.Netlist);
    }
}
