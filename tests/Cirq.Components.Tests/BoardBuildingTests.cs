using Cirq.Components.Digital;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Protoboard;
using Cirq.Components.Serialization;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>Building the circuit: hidden faults, stripboard and perfboard, and the build guide.</summary>
public class BoardBuildingTests
{
    /// <summary>A 9 V battery lighting a red LED through 1 kΩ.</summary>
    private static (Circuit Circuit, Resistor Resistor, Led Led) LedCircuit()
    {
        var circuit = new Circuit();
        var battery = circuit.Add(new Battery());
        var resistor = circuit.Add(new Resistor(1e3));
        var led = circuit.Add(new Led());
        var ground = circuit.Add(new Ground());
        circuit.Connect(battery.Terminals[0], resistor.Terminals[0]);
        circuit.Connect(resistor.Terminals[1], led.Terminals[0]);
        circuit.Connect(led.Terminals[1], ground.Terminals[0]);
        circuit.Connect(battery.Terminals[1], ground.Terminals[0]);
        return (circuit, resistor, led);
    }

    /// <summary>A 7400 with its inputs pulled up and an LED on an output: a chip, two-lead parts and a supply.</summary>
    private static Circuit GateCircuit()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5));
        var gate = circuit.Add(new Ic7400());
        var pull = circuit.Add(new Resistor(10e3));
        var limit = circuit.Add(new Resistor(330));
        var led = circuit.Add(new Led());
        var ground = circuit.Add(new Ground());
        circuit.Connect(supply.Terminals[1], ground.Terminals[0]);
        circuit.Connect(supply.Terminals[0], gate.TerminalByName("VCC"));
        circuit.Connect(gate.TerminalByName("GND"), ground.Terminals[0]);
        circuit.Connect(pull.Terminals[0], supply.Terminals[0]);
        circuit.Connect(pull.Terminals[1], gate.TerminalByName("1A"));
        circuit.Connect(gate.TerminalByName("1A"), gate.TerminalByName("1B"));
        circuit.Connect(gate.TerminalByName("1Y"), limit.Terminals[0]);
        circuit.Connect(limit.Terminals[1], led.Terminals[0]);
        circuit.Connect(led.Terminals[1], ground.Terminals[0]);
        return circuit;
    }

    private static double Glow(Circuit circuit, Led led)
    {
        var simulator = new CircuitSimulator(circuit, new Cirq.Core.Simulation.SimulationSettings { TimeStep = 1e-6 });
        simulator.Reset();
        simulator.SolveOperatingPoint();
        for (var i = 0; i < 20; i++) simulator.Step();
        return led.Brightness;
    }

    // ───────────────────────────── faults ─────────────────────────────

    [Fact]
    public void A_hidden_fault_changes_only_what_the_simulation_sees()
    {
        var (circuit, resistor, led) = LedCircuit();
        Assert.True(Glow(circuit, led) > 0.1);

        // A lead not making contact: the LED goes dark, while the drawing's netlist doesn't change.
        circuit.Faults.Add(new CircuitFault(FaultKind.Open, led.Terminals[0]));
        var simulated = circuit.BuildSimulationNetlist();
        Assert.Single(simulated.NetOf(led.Terminals[0]).Terminals);
        Assert.Contains(resistor.Terminals[1], circuit.BuildNetlist().NetOf(led.Terminals[0]).Terminals);
        Assert.Equal(0, Glow(circuit, led), 3);

        // The LED backwards: dark too, its anode now on ground.
        circuit.Faults.Clear();
        circuit.Faults.Add(new CircuitFault(FaultKind.Reversed, led.Terminals[0], led.Terminals[1]));
        Assert.True(circuit.BuildSimulationNetlist().NetOf(led.Terminals[0]).IsGround);
        Assert.Equal(0, Glow(circuit, led), 3);

        // Bridged across the LED: shorted out, dark again.
        circuit.Faults.Clear();
        circuit.Faults.Add(new CircuitFault(FaultKind.Bridged, led.Terminals[0], led.Terminals[1]));
        var bridged = circuit.BuildSimulationNetlist();
        Assert.Same(bridged.NetOf(led.Terminals[0]), bridged.NetOf(led.Terminals[1]));

        // Never saved: the file is the circuit as drawn.
        Assert.DoesNotContain("Fault", CircuitSerializer.ToJson(circuit), StringComparison.Ordinal);
        circuit.Faults.Clear();
        Assert.True(Glow(circuit, led) > 0.1);
    }

    [Fact]
    public void Fault_finding_hides_a_fault_the_circuit_has_gives_hints_and_knows_a_right_guess()
    {
        var circuit = GateCircuit();
        var candidates = FaultFinding.Candidates(circuit);
        Assert.Contains(candidates, f => f.Kind == FaultKind.Open);
        Assert.Contains(candidates, f => f.Kind == FaultKind.Bridged);
        Assert.Contains(candidates, f => f.Kind == FaultKind.Reversed);

        // Hidden at random, one at a time, always one the circuit still simulates with.
        for (var seed = 0; seed < 8; seed++)
        {
            var fault = FaultFinding.Hide(circuit, new Random(seed));
            Assert.NotNull(fault);
            Assert.Single(circuit.Faults);
            Assert.True(FaultFinding.Survives(circuit, fault));
        }

        // A particular one, and the clues it gives: each more telling.
        var led = circuit.Components.OfType<Led>().Single();
        FaultFinding.Hide(circuit, FaultKind.Reversed, led.Terminals[0]);
        Assert.Contains("backwards", FaultFinding.Hint(circuit, 1));
        Assert.Contains("net with", FaultFinding.Hint(circuit, 2));
        Assert.Contains(led.Name, FaultFinding.Hint(circuit, 3));
        Assert.True(FaultFinding.IsFaultAt(circuit, led));
        Assert.False(FaultFinding.IsFaultAt(circuit, circuit.Components.OfType<Ic7400>().Single()));

        // Not one it could have: a pin joined to nothing can't be the open one.
        var spare = circuit.Components.OfType<Ic7400>().Single().TerminalByName("4Y");
        Assert.Contains("isn't joined", Assert.Throws<ArgumentException>(() => FaultFinding.Hide(circuit, FaultKind.Open, spare)).Message);
    }

    // ───────────────────────────── stripboard and perfboard ─────────────────────────────

    [Theory]
    [InlineData(BoardKind.Stripboard)]
    [InlineData(BoardKind.Perfboard)]
    public void A_soldered_board_matches_the_schematic_and_says_where_everything_is(BoardKind kind)
    {
        var circuit = GateCircuit();
        circuit.Board.Kind = kind;
        var layout = ProtoboardLayout.Arrange(circuit);
        Assert.Equal(kind, layout.Kind);
        Assert.True(layout.Matches, string.Join(" ", layout.Problems));
        Assert.Equal(4, layout.Parts.Count);
        Assert.True(layout.Columns >= 10 && layout.Rows >= 8);

        // Every pin is in the board's own holes, and the chip's pin 1 is bottom left of it.
        var chip = layout.Parts.Single(p => p.Package.Form == BoardForm.Dip);
        Assert.All(layout.Parts.SelectMany(p => p.Holes), h => Assert.InRange(h.Column, 1, layout.Columns));
        Assert.Equal(chip.Holes[0].Column, chip.Holes[13].Column);
        Assert.True(chip.Holes[0].Row > chip.Holes[13].Row);
        Assert.Matches("^[A-Z]+[0-9]+$", layout.HoleName(chip.Holes[0]));

        if (kind == BoardKind.Stripboard)
        {
            // Every run of copper carries one net at most, and the chip's pins are each on a run of their own.
            foreach (var segment in layout.Segments)
            {
                var nets = layout.Parts.SelectMany(p => p.Package.Pins.Select((t, i) => (t, h: p.Holes[i])))
                    .Where(x => x.t is not null && x.h.Column == segment.Column && x.h.Row >= segment.From && x.h.Row <= segment.To)
                    .Select(x => circuit.BuildNetlist().NetOf(x.t!).Index).Distinct();
                Assert.True(nets.Count() <= 1, $"{SolderedLayout.Letters(segment.Column)}{segment.From}–{segment.To} carries two nets");
            }

            Assert.NotEmpty(layout.Cuts);
            Assert.NotSame(layout.SegmentAt(chip.Holes[0]), layout.SegmentAt(chip.Holes[13]));
        }
        else
        {
            Assert.Empty(layout.Cuts);
            Assert.All(layout.Wires, w => Assert.Same(layout.NetAt(w.From), w.Net));
        }

        Assert.Contains("matches the schematic", layout.Describe());
    }

    [Fact]
    public void What_it_is_built_on_is_saved_with_the_circuit()
    {
        var (circuit, _, _) = LedCircuit();
        Assert.DoesNotContain("Protoboard", CircuitSerializer.ToJson(circuit));
        circuit.Board.Kind = BoardKind.Stripboard;
        var json = CircuitSerializer.ToJson(circuit);
        Assert.Contains("\"Stripboard\"", json);
        Assert.Equal(BoardKind.Stripboard, CircuitSerializer.FromJson(json).Circuit.Board.Kind);
        circuit.Clear();
        Assert.Equal(BoardKind.Breadboard, circuit.Board.Kind);
    }

    // ───────────────────────────── the build guide ─────────────────────────────

    [Fact]
    public void The_build_guide_says_how_to_fit_each_part_and_ends_with_the_check()
    {
        var (circuit, resistor, led) = LedCircuit();
        var layout = ProtoboardLayout.Arrange(circuit);
        var steps = BuildGuide.Steps(layout);
        Assert.Equal(BuildStepKind.Board, steps[0].Kind);
        Assert.Equal(BuildStepKind.Check, steps[^1].Kind);
        Assert.Equal(Enumerable.Range(1, steps.Count), steps.Select(s => s.Number));
        Assert.Contains("matches the schematic", steps[^1].Text);

        var r = steps.Single(s => ReferenceEquals(s.Part?.Part, resistor));
        Assert.Contains("brown, black, red, gold", r.Text);
        Assert.Contains(layout.HoleName(r.Part!.Holes[0]), r.Text);
        var d = steps.Single(s => ReferenceEquals(s.Part?.Part, led));
        Assert.Contains("longer leg (anode)", d.Text);
        Assert.Contains("red", d.Text);
        Assert.Contains(steps, s => s.Kind == BuildStepKind.Bench && s.Text.Contains("BT1", StringComparison.Ordinal));

        // Stripboard: the copper is cut and linked while the board is bare, before any part goes in.
        var gates = GateCircuit();
        gates.Board.Kind = BoardKind.Stripboard;
        var strip = BuildGuide.Steps(ProtoboardLayout.Arrange(gates));
        var firstPart = strip.First(s => s.Kind == BuildStepKind.Part).Number;
        Assert.Contains(strip, s => s.Kind == BuildStepKind.Cut);
        Assert.All(strip.Where(s => s.Kind is BuildStepKind.Cut or BuildStepKind.Link), s => Assert.True(s.Number < firstPart));
        Assert.Contains("notch to the left", strip.First(s => s.Part?.Package.Form == BoardForm.Dip).Text);

        // Perfboard: the wiring on the back, net by net, after the parts.
        gates.Board.Kind = BoardKind.Perfboard;
        var perf = BuildGuide.Steps(ProtoboardLayout.Arrange(gates));
        Assert.Contains(perf, s => s.Kind == BuildStepKind.Wire && s.Text.StartsWith("On the back", StringComparison.Ordinal));
        Assert.Contains("1.", BuildGuide.Text(perf));
    }
}
