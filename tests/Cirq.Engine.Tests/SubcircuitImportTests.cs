using Cirq.Components.Hierarchy;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Components.Spice;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// Reading SPICE <c>.subckt</c> definitions, which is what turns the parts this library chose to
/// model into the parts a manufacturer actually sells.
/// <para>
/// A <c>.model</c> card describes one device. Everything more interesting than a transistor — an
/// op-amp, a regulator, a reference — is published as a subcircuit: a pin list and a little
/// netlist. What comes in is a <b>block</b>, the same thing grouping a selection produces, which
/// is why it works at all: a block already flattens before the solve and already travels inside a
/// saved file.
/// </para>
/// </summary>
public class SubcircuitImportTests
{
    private const string Divider = """
        * A two-resistor divider, the simplest thing with pins.
        .subckt DIVIDER in out gnd
        R1 in out 10k
        R2 out gnd 10k
        .ends DIVIDER
        """;

    private static SpiceSubcircuit One(string text)
    {
        var parsed = SpiceSubcircuitReader.Parse(text);

        Assert.Empty(parsed.Problems);
        return Assert.Single(parsed.Subcircuits);
    }

    // ---- reading -----------------------------------------------------------

    [Fact]
    public void ThePinsComeBackInTheOrderTheHeaderListsThem()
    {
        var definition = One(Divider);

        Assert.Equal("DIVIDER", definition.Name);
        Assert.Equal(["in", "out", "gnd"], definition.Pins);
        Assert.Equal(2, definition.Elements.Count);
    }

    [Fact]
    public void ElementValuesAreReadWithTheirSuffixes()
    {
        var definition = One(Divider);

        Assert.All(definition.Elements, e => Assert.Equal(10e3, e.Value!.Value, 6));
        Assert.Equal(['R', 'R'], definition.Elements.Select(e => e.Letter));
    }

    /// <summary>Continuation lines and comments work as they do everywhere else in SPICE.</summary>
    [Fact]
    public void ContinuationsAndCommentsAreHandled()
    {
        var definition = One("""
            .subckt WRAPPED a b
            + c
            R1 a b 1k   ; the only part
            * a comment line
            R2 b c 2k
            .ends
            """);

        Assert.Equal(["a", "b", "c"], definition.Pins);
        Assert.Equal(2, definition.Elements.Count);
    }

    /// <summary>A model defined inside a subcircuit belongs to it, which is how vendors ship them.</summary>
    [Fact]
    public void AModelInsideBelongsToTheSubcircuit()
    {
        var definition = One("""
            .subckt CLAMP in out
            D1 in out DLOCAL
            .model DLOCAL D(Is=3n N=1.6)
            .ends
            """);

        var card = Assert.Single(definition.Models);

        Assert.Equal("DLOCAL", card.Name);
        Assert.Equal("DLOCAL", Assert.Single(definition.Elements).Model);
    }

    // ---- building ----------------------------------------------------------

    [Fact]
    public void ABlockComesOutWithItsPartsAndItsPins()
    {
        var built = SpiceSubcircuitImport.Build(One(Divider));

        Assert.True(built.Succeeded, string.Join("; ", built.Problems));
        Assert.Equal(2, built.Parts);
        Assert.Equal(3, built.Pins);
        Assert.Equal("DIVIDER", built.Block!.BlockName);

        // The pins are in the header's order, because a SPICE subcircuit's pins have no names —
        // only positions — and getting them out of order silently builds a different part.
        Assert.Equal(["in", "out", "gnd"], built.Block.Ports.Select(p => p.Outer.Name));
    }

    /// <summary>
    /// And it solves. The block is placed in a circuit, driven, and asked for the answer the
    /// netlist says it should give — which is the only test that really matters.
    /// </summary>
    [Fact]
    public void TheImportedBlockSolvesAsTheNetlistSaysItShould()
    {
        var built = SpiceSubcircuitImport.Build(One(Divider));

        Assert.True(built.Succeeded, string.Join("; ", built.Problems));

        var circuit = new Circuit();
        var block = built.Block!;

        circuit.Components.Add(block);

        var supply = circuit.Add(new DcVoltageSource(10.0));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, block.Ports[0].Outer);   // in
        circuit.Connect(block.Ports[2].Outer, ground.Pin);        // gnd

        var probe = new SignalProbe("Out", block.Ports[1].Outer, default);
        circuit.Probes.Add(probe);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.ResolveProbes();

        // Two equal resistors across ten volts: five.
        Assert.Equal(5.0, sim.NodeVoltage(block.Ports[1].Outer), 1e-6);
    }

    /// <summary>
    /// A subcircuit built from controlled sources solves too — which is the case that matters,
    /// because that is what every op-amp macromodel is made of.
    /// </summary>
    [Fact]
    public void AMacromodelOfControlledSourcesSolves()
    {
        // A transconductance into a resistor, then a unity buffer: the skeleton of every op-amp
        // model there is. Gain is gm × R = 1 mS × 100 kΩ = 100.
        var definition = One("""
            .subckt SIMPLEAMP inp inn out gnd
            G1 gnd mid inp inn 1m
            R1 mid gnd 100k
            E1 out gnd mid gnd 1
            .ends
            """);

        var built = SpiceSubcircuitImport.Build(definition);

        Assert.True(built.Succeeded, string.Join("; ", built.Problems));
        Assert.Equal(3, built.Parts);

        var circuit = new Circuit();
        var block = built.Block!;

        circuit.Components.Add(block);

        var drive = circuit.Add(new DcVoltageSource(0.01));
        var ground = circuit.Add(new Ground());

        circuit.Connect(drive.Negative, ground.Pin);
        circuit.Connect(drive.Positive, block.Ports[0].Outer);   // inp
        circuit.Connect(block.Ports[1].Outer, ground.Pin);       // inn
        circuit.Connect(block.Ports[3].Outer, ground.Pin);       // gnd

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        // 10 mV in, a gain of a hundred: one volt out.
        Assert.Equal(1.0, sim.NodeVoltage(block.Ports[2].Outer), 1e-6);
    }

    /// <summary>A transistor inside one works, with its model carried alongside it.</summary>
    [Fact]
    public void ATransistorInsideOneIsBuiltFromItsOwnModel()
    {
        var definition = One("""
            .subckt AMPSTAGE base collector emitter
            Q1 collector base emitter QLOCAL
            .model QLOCAL NPN(Is=1e-14 Bf=250 Vaf=80)
            .ends
            """);

        var built = SpiceSubcircuitImport.Build(definition);

        try
        {
            Assert.True(built.Succeeded, string.Join("; ", built.Problems));

            var transistor = Assert.Single(built.Block!.InnerComponents.OfType<BipolarTransistor>());

            Assert.Equal("QLOCAL", transistor.Model.Name);
            Assert.Equal(250, transistor.Model.ForwardBeta, 1e-9);
        }
        finally
        {
            SpiceModelImport.Unregister("QLOCAL", SpiceDeviceKind.Npn);
        }
    }

    // ---- refusing rather than approximating --------------------------------

    /// <summary>
    /// An element this library has no part for stops the import and is named. A block with a piece
    /// left out is not the part it claims to be, and importing it silently would be worse than
    /// refusing — the circuit would solve, and give the wrong answer.
    /// </summary>
    [Fact]
    public void AnElementWithNoPartStopsTheImportAndIsNamed()
    {
        var parsed = SpiceSubcircuitReader.Parse("""
            .subckt WITHSWITCH a b c
            R1 a b 1k
            S1 b c a c SWMOD
            .ends
            """);

        Assert.Contains(parsed.Problems, p => p.Contains("S1") && p.Contains("switch"));
    }

    [Fact]
    public void ANestedSubcircuitIsNamedRatherThanIgnored()
    {
        var parsed = SpiceSubcircuitReader.Parse("""
            .subckt OUTER a b
            X1 a b INNER
            .ends
            """);

        Assert.Contains(parsed.Problems, p => p.Contains("X1") && p.Contains("nested"));
    }

    [Fact]
    public void AParameterisedSubcircuitSaysItsParametersWereNotHonoured()
    {
        var parsed = SpiceSubcircuitReader.Parse("""
            .subckt GAINBLOCK in out PARAMS: gain=10
            R1 in out 1k
            .ends
            """);

        Assert.Contains(parsed.Problems, p => p.Contains("parameters"));
        Assert.Equal(["in", "out"], parsed.Subcircuits[0].Pins);
    }

    [Fact]
    public void AMissingEndsIsReported()
    {
        var parsed = SpiceSubcircuitReader.Parse("""
            .subckt UNFINISHED a b
            R1 a b 1k
            """);

        Assert.Contains(parsed.Problems, p => p.Contains(".ends"));
    }

    [Fact]
    public void APinJoinedToNothingIsReported()
    {
        var built = SpiceSubcircuitImport.Build(One("""
            .subckt LONELY a b spare
            R1 a b 1k
            .ends
            """));

        Assert.False(built.Succeeded);
        Assert.Contains(built.Problems, p => p.Contains("spare"));
    }

    [Fact]
    public void TextWithNoSubcircuitInItSaysSo()
    {
        var parsed = SpiceSubcircuitReader.Parse(".model 1N4148 D(Is=2.52n)");

        Assert.Empty(parsed.Subcircuits);
        Assert.Contains(parsed.Problems, p => p.Contains("No .subckt"));
    }

    /// <summary>Several in one paste, which is how a vendor ships a family.</summary>
    [Fact]
    public void SeveralInOnePasteAreAllRead()
    {
        var parsed = SpiceSubcircuitReader.Parse("""
            .subckt ONE a b
            R1 a b 1k
            .ends
            .subckt TWO a b
            C1 a b 100n
            .ends
            """);

        Assert.Empty(parsed.Problems);
        Assert.Equal(["ONE", "TWO"], parsed.Subcircuits.Select(s => s.Name));
    }
}
