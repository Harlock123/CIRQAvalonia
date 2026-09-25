using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;
using Cirq.UI.Services;

namespace Cirq.UI.Tests;

/// <summary>
/// What every net is sitting at and what every part is doing.
/// <para>
/// Checked against arithmetic anybody can do on the back of the schematic rather than against
/// recorded output: ten volts across two equal resistors puts five volts in the middle, and if this
/// ever says something else then one of the two is wrong and it is not the arithmetic.
/// </para>
/// </summary>
public class LiveValuesTests
{
    /// <summary>
    /// How close to the arithmetic a reading has to come.
    /// <para>
    /// Not exact, and it cannot be: the solver shunts every node to ground through a tiny
    /// conductance so that a floating one still has an equation, which puts the midpoint of a
    /// divider a couple of parts in a billion below half the rail. Asserting exact equality here
    /// would be asserting that gmin is not there, and it is there on purpose.
    /// </para>
    /// </summary>
    private const double Tolerance = 1e-6;

    /// <summary>
    /// A divider off a ten volt rail: <c>V1(+) → R1 → mid → R2 → ground</c>, with the supply's
    /// negative returned to the same ground.
    /// </summary>
    private static (CircuitSimulator Sim, Circuit Circuit, Resistor R1, Resistor R2) Divider(
        double upper = 1e3, double lower = 1e3)
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(10.0) { Name = "V1" });
        var r1 = circuit.Add(new Resistor(upper) { Name = "R1", X = 100, Y = 40 });
        var r2 = circuit.Add(new Resistor(lower) { Name = "R2", X = 100, Y = 140 });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Positive, TargetTerminal = r1.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = r1.B, TargetTerminal = r2.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = r2.B, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Negative, TargetTerminal = ground.Pin });

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        return (sim, circuit, r1, r2);
    }

    /// <summary>Two equal resistors across ten volts: ten at the top, five in the middle.</summary>
    [Fact]
    public void EveryNetReadsItsOwnVoltage()
    {
        var (sim, circuit, _, _) = Divider();

        var live = LiveValues.For(circuit, sim);

        var volts = live.Nets.Select(n => n.Volts).OrderBy(v => v).ToList();

        Assert.Equal(2, volts.Count);
        Assert.Equal(5.0, volts[0], Tolerance);
        Assert.Equal(10.0, volts[1], Tolerance);
    }

    /// <summary>
    /// Ground is left out. It is zero everywhere by definition, and writing that against every
    /// return is a row of noughts where the informative figures could have gone.
    /// </summary>
    [Fact]
    public void GroundIsNotAnnotated()
    {
        var (sim, circuit, _, _) = Divider();

        var live = LiveValues.For(circuit, sim);

        Assert.DoesNotContain(live.Nets, n => n.Volts == 0.0);
        Assert.DoesNotContain(live.Nets, n => n.Name == "GND");
    }

    /// <summary>
    /// Five volts across a kilohm is five milliamps, flowing in at the end the supply is on. The
    /// sign is the half worth testing: a magnitude that is wrong looks wrong, and a direction that
    /// is wrong looks like a working feature describing a circuit that runs backwards.
    /// </summary>
    [Fact]
    public void CurrentThroughAPartIsPositiveIntoItsFirstPin()
    {
        var (sim, circuit, r1, _) = Divider();

        var reading = LiveValues.For(circuit, sim).For(r1);

        Assert.NotNull(reading);
        Assert.Equal(5e-3, reading.Amps!.Value, Tolerance);
        Assert.Equal(5.0, reading.Volts!.Value, Tolerance);
    }

    /// <summary>And the other way round on the part whose first pin is the far end of the loop.</summary>
    [Fact]
    public void TheSignFollowsThePinRatherThanTheCircuit()
    {
        var (sim, circuit, r1, r2) = Divider();

        var live = LiveValues.For(circuit, sim);

        // R1's A is its supply end and R2's A is its own supply end, so both read positive: this is
        // a statement about each part, not about a direction round the loop.
        Assert.True(live.For(r1)!.Amps > 0);
        Assert.True(live.For(r2)!.Amps > 0);

        // Reversing one part's wiring reverses only that part's reading.
        var reversed = new Circuit();
        var supply = reversed.Add(new DcVoltageSource(10.0) { Name = "V1" });
        var r = reversed.Add(new Resistor(1e3) { Name = "R1" });
        var ground = reversed.Add(new Ground { Name = "GND1" });

        reversed.Wires.Add(new WireSegment { SourceTerminal = supply.Positive, TargetTerminal = r.B });
        reversed.Wires.Add(new WireSegment { SourceTerminal = r.A, TargetTerminal = ground.Pin });
        reversed.Wires.Add(new WireSegment { SourceTerminal = supply.Negative, TargetTerminal = ground.Pin });

        var sim2 = new CircuitSimulator(reversed);
        sim2.Reset();
        sim2.SolveOperatingPoint();

        Assert.Equal(-0.01, LiveValues.For(reversed, sim2).For(r)!.Amps!.Value, Tolerance);
    }

    /// <summary>
    /// Five volts at five milliamps is twenty-five milliwatts, which is also what a quarter-watt
    /// part is comfortably inside and a good deal less than the figure that gets written on the
    /// drawing.
    /// </summary>
    [Fact]
    public void PowerIsVoltsTimesAmps()
    {
        var (sim, circuit, r1, _) = Divider();

        var reading = LiveValues.For(circuit, sim).For(r1);

        Assert.Equal(25e-3, reading!.Watts!.Value, Tolerance);
    }

    /// <summary>
    /// Ten volts across a hundred ohms is a watt, which is over the line at which the figure is
    /// worth putting on the schematic. Twenty-five milliwatts is not.
    /// </summary>
    [Fact]
    public void OnlyDissipationWorthWorryingAboutReachesTheDrawing()
    {
        var (hot, hotCircuit, r, _) = Divider(100.0, 1e-3);
        var hotText = LiveValues.For(hotCircuit, hot).For(r)!.Text;

        Assert.Contains("W", hotText);

        var (cool, coolCircuit, cold, _) = Divider();
        var coolText = LiveValues.For(coolCircuit, cool).For(cold)!.Text;

        Assert.DoesNotContain("W", coolText);
        Assert.Contains("5mA", coolText);
    }

    /// <summary>
    /// A transistor has three pins, so it has no single current through it and no single voltage
    /// across it — but it does have a dissipation, and that is the number worth having anyway.
    /// </summary>
    [Fact]
    public void APartWithThreePinsGetsWattsButNoCurrent()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(10.0) { Name = "V1" });
        var bias = circuit.Add(new DcVoltageSource(0.75) { Name = "V2" });
        var load = circuit.Add(new Resistor(1e3) { Name = "RL" });
        var q = circuit.Add(new BipolarTransistor { Name = "Q1", ThermalResistance = 200.0 });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Positive, TargetTerminal = load.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = load.B, TargetTerminal = q.Collector });
        circuit.Wires.Add(new WireSegment { SourceTerminal = bias.Positive, TargetTerminal = q.Base });
        circuit.Wires.Add(new WireSegment { SourceTerminal = q.Emitter, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Negative, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = bias.Negative, TargetTerminal = ground.Pin });

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        var reading = LiveValues.For(circuit, sim).For(q);

        Assert.NotNull(reading);
        Assert.Null(reading.Amps);
        Assert.Null(reading.Volts);
        Assert.NotNull(reading.Watts);
        Assert.True(reading.Watts > 0, $"a conducting transistor dissipates something, not {reading.Watts}");
    }

    /// <summary>
    /// A part that models its own heating reports where its die actually is, which is above the air
    /// around it by however many watts times however many degrees per watt.
    /// </summary>
    [Fact]
    public void ASelfHeatingPartReportsItsDieRatherThanTheRoom()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(10.0) { Name = "V1" });
        var bias = circuit.Add(new DcVoltageSource(0.8) { Name = "V2" });
        var load = circuit.Add(new Resistor(100.0) { Name = "RL" });
        var q = circuit.Add(new BipolarTransistor { Name = "Q1", ThermalResistance = 250.0 });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Positive, TargetTerminal = load.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = load.B, TargetTerminal = q.Collector });
        circuit.Wires.Add(new WireSegment { SourceTerminal = bias.Positive, TargetTerminal = q.Base });
        circuit.Wires.Add(new WireSegment { SourceTerminal = q.Emitter, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Negative, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = bias.Negative, TargetTerminal = ground.Pin });

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        var reading = LiveValues.For(circuit, sim).For(q);

        Assert.NotNull(reading);
        Assert.NotNull(reading.Celsius);
        Assert.True(reading.Celsius > circuit.AmbientTemperatureCelsius,
            $"a die dissipating {reading.Watts} W through 250 °C/W sits above the room, not at {reading.Celsius} °C");
    }

    /// <summary>
    /// A part with no thermal resistance given has no die temperature to report. The alternative —
    /// repeating the ambient back — reads as a measurement and is not one.
    /// </summary>
    [Fact]
    public void APartWithNoThermalModelReportsNoTemperature()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(5.0) { Name = "V1" });
        var d = circuit.Add(new Diode { Name = "D1" });
        var limit = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Positive, TargetTerminal = limit.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = limit.B, TargetTerminal = d.Anode });
        circuit.Wires.Add(new WireSegment { SourceTerminal = d.Cathode, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Negative, TargetTerminal = ground.Pin });

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        var reading = LiveValues.For(circuit, sim).For(d);

        Assert.NotNull(reading);
        Assert.Null(reading.Celsius);
    }

    /// <summary>
    /// A net's figure goes at its topmost point, and the leftmost of those when two are level. It
    /// has to be the same place every frame: a label that moves from one end of a net to the other
    /// between redraws cannot be read at all.
    /// </summary>
    [Fact]
    public void ANetsFigureSitsAtItsTopmostTerminal()
    {
        var (sim, circuit, r1, r2) = Divider();

        var live = LiveValues.For(circuit, sim);

        var mid = live.Nets.Single(n => Math.Abs(n.Volts - 5.0) < Tolerance);
        var highest = Math.Min(r1.B.AbsolutePosition.Y, r2.A.AbsolutePosition.Y);

        Assert.Equal(highest, mid.At.Y, Tolerance);

        // And twice in a row gives the same answer.
        Assert.Equal(mid.At.X, LiveValues.For(circuit, sim).Nets
            .Single(n => Math.Abs(n.Volts - 5.0) < Tolerance).At.X, Tolerance);
    }

    /// <summary>
    /// A part dropped on the sheet and not yet wired to anything has terminals no netlist has ever
    /// seen. Asking for their node indices throws, so the reading has to check first — this is the
    /// state a circuit is in for as long as it takes to draw the second wire.
    /// </summary>
    [Fact]
    public void AnUnwiredPartIsSkippedRatherThanThrowing()
    {
        var (sim, circuit, _, _) = Divider();

        circuit.Add(new Resistor(4.7e3) { Name = "R9", X = 400, Y = 400 });

        // Deliberately not recompiled: this is exactly the window between dropping a part and the
        // netlist being rebuilt, and the canvas draws several frames inside it.
        var live = LiveValues.For(circuit, sim);

        Assert.DoesNotContain(live.Parts.Keys, p => p.Name == "R9");
    }

    /// <summary>
    /// An export carries the figures when the editor is showing them. A picture of the circuit that
    /// quietly dropped half of what was on screen would be a picture of a different schematic.
    /// </summary>
    [Fact]
    public void AnExportCarriesTheFiguresTheEditorIsShowing()
    {
        var (sim, circuit, _, _) = Divider();

        var live = LiveValues.For(circuit, sim);

        var path = Path.Combine(Path.GetTempPath(), $"cirq-live-{Guid.NewGuid():N}.svg");
        var plain = Path.Combine(Path.GetTempPath(), $"cirq-plain-{Guid.NewGuid():N}.svg");

        try
        {
            CircuitExporter.Export(circuit, null, path,
                new ExportOptions(ExportFormat.Svg, ExportContent.Schematic, Live: live));

            CircuitExporter.Export(circuit, null, plain,
                new ExportOptions(ExportFormat.Svg, ExportContent.Schematic));

            var annotated = File.ReadAllText(path);
            var bare = File.ReadAllText(plain);

            // The figures are drawn as text, so they are in the file as text.
            Assert.Contains("5mA", annotated, StringComparison.Ordinal);
            Assert.DoesNotContain("5mA", bare, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(plain);
        }
    }

    /// <summary>The figures are written the way a meter reads them, not in scientific notation.</summary>
    [Fact]
    public void FiguresAreWrittenInEngineeringUnits()
    {
        var (sim, circuit, r1, _) = Divider();

        var live = LiveValues.For(circuit, sim);

        Assert.Equal("5mA", live.For(r1)!.Text);
        Assert.Equal("5V", live.Nets.Single(n => Math.Abs(n.Volts - 5.0) < Tolerance).Text);
    }

    /// <summary>
    /// The solver's own residue is written as a plain zero rather than dressed up in an SI prefix.
    /// <para>
    /// A node tied to ground comes back a few hundred picovolts off it, and <c>450pV</c> on a
    /// schematic reads as a measurement of something — at a resolution no instrument has, about a
    /// node that is simply grounded. What is above the floor is left exactly as it is, because a
    /// few nanoamps through a part that should be passing none is a real thing to have noticed.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(4.5e-10, "0V")]
    [InlineData(1.8e-9, "0V")]
    [InlineData(5.0, "5V")]
    [InlineData(-2.4e-3, "-2.4mV")]
    public void ResidueIsWrittenAsZero(double volts, string expected) =>
        Assert.Equal(expected, LiveValues.Figure(volts, "V", LiveValues.VoltFloor));

    /// <summary>Currents get a floor of their own, set low enough to leave real leakage alone.</summary>
    [Fact]
    public void RealLeakageSurvivesTheFloor()
    {
        Assert.Equal("0A", LiveValues.Figure(1e-15, "A", LiveValues.AmpFloor));
        Assert.Equal("4.52nA", LiveValues.Figure(4.52e-9, "A", LiveValues.AmpFloor));
    }

    /// <summary>
    /// The hover card leads with what the part is doing and follows with what it is set to. A part
    /// hovered in a stopped circuit shows only the settings, which is all there is to show.
    /// </summary>
    [Fact]
    public void TheHoverCardLeadsWithTheMeasurement()
    {
        var (sim, circuit, r1, _) = Divider();

        var reading = LiveValues.For(circuit, sim).For(r1);

        var withReadings = ComponentSummary.For(r1, reading);
        var without = ComponentSummary.For(r1);

        Assert.Equal("Through", withReadings.Rows[0].Label);
        Assert.Equal("5mA", withReadings.Rows[0].Value);
        Assert.Contains(withReadings.Rows, r => r.Label == "Across" && r.Value == "5V");

        Assert.DoesNotContain(without.Rows, r => r.Label is "Through" or "Across" or "Dissipating");
    }

    /// <summary>
    /// Readings push settings into the "+n more" line rather than making the card taller. A card
    /// taller than the part it describes stops being a glance, which is the whole of its point.
    /// </summary>
    [Fact]
    public void ReadingsDoNotMakeTheCardGrow()
    {
        var (sim, circuit, _, _) = Divider();

        var generator = circuit.Add(new FunctionGenerator { Name = "FG1" });

        var card = ComponentSummary.For(
            generator, new PartReading(generator, 1.0, 2.0, 3.0, 40.0));

        // Four readings and then whatever fits, plus at most the one overflow line.
        Assert.True(card.Rows.Count <= ComponentSummary.MaximumRows + 1,
            $"{card.Rows.Count} rows is a panel, not a card");
        Assert.Equal("Die", card.Rows[3].Label);
    }
}
