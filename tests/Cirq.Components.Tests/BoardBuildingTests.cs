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
    // ───────────────────────────── soldered boards by hand ─────────────────────────────

    [Theory]
    [InlineData(BoardKind.Stripboard)]
    [InlineData(BoardKind.Perfboard)]
    public void A_part_on_a_soldered_board_moves_by_hand_and_the_board_still_matches(BoardKind kind)
    {
        var circuit = GateCircuit();
        circuit.Board.Kind = kind;
        var layout = ProtoboardLayout.Arrange(circuit);
        var chip = layout.Parts.Single(p => p.Package.Form == BoardForm.Dip);

        // The chip moved down and right, into clear board (it grows to hold it): its shape kept, the rest laid out round it, the copper worked out again.
        var grab = chip.Holes[0];
        var to = new BoardHole(1, grab.Row + 15, grab.Column + 5);
        var edit = ProtoboardEdits.MovePart(circuit, layout, chip.Part, grab, to);
        Assert.True(edit.Succeeded, edit.Problem);
        Assert.Equal(kind, edit.Arrangement!.Kind);
        circuit.Board.CopyFrom(edit.Arrangement);
        var moved = ProtoboardLayout.Arrange(circuit);
        var now = moved.Find(chip.Part)!;
        Assert.True(now.ByHand);
        Assert.Equal(to, now.Holes[0]);
        Assert.Equal(chip.Holes.Select(h => (h.Row + 15, h.Column + 5)), now.Holes.Select(h => (h.Row, h.Column)));
        Assert.True(moved.Matches, string.Join(" ", moved.Problems));
        Assert.Empty(moved.Refused);

        // What was put where by hand survives a save, named as the board names its holes.
        var json = CircuitSerializer.ToJson(circuit);
        Assert.Contains($"\"{SolderedLayout.Name(to)}\"", json);
        var reopened = CircuitSerializer.FromJson(json);
        Assert.Empty(reopened.Warnings);
        var again = ProtoboardLayout.Arrange(reopened.Circuit);
        Assert.Equal(to, again.Parts.Single(p => p.Part.Id == chip.Part.Id).Holes[0]);

        // A resistor's lead moved somewhere free; and a part dropped on the chip's pins is refused, with the reason.
        var resistor = moved.Parts.First(p => p.Package.Form == BoardForm.TwoLead);
        var free = Enumerable.Range(1, 40).Select(r => new BoardHole(1, r, 1)).First(h => moved.PartAt(h) is null && !moved.Wires.Any(w => w.From == h || w.To == h)
            && ProtoboardEdits.MoveLead(circuit, moved, resistor.Part, resistor.Package.Pins[1]!, h).Succeeded);
        Assert.True(ProtoboardEdits.MoveLead(circuit, moved, resistor.Part, resistor.Package.Pins[1]!, free).Layout!.Matches);
        var onChip = ProtoboardEdits.MovePart(circuit, moved, resistor.Part, resistor.Holes[0], now.Holes[2]);
        Assert.False(onChip.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(onChip.Problem));

        // Links and back wiring follow the parts: they aren't moved on their own.
        if (moved.Wires.Count > 0) Assert.False(ProtoboardEdits.MoveWireEnd(circuit, moved, moved.Wires[0], true, new BoardHole(1, 1, 1)).Succeeded);

        // Released, it is laid out automatically again.
        var released = ProtoboardEdits.Release(circuit, moved, chip.Part);
        Assert.False(released.Layout!.Find(chip.Part)!.ByHand);
        Assert.True(released.Layout.Matches);
    }

    [Fact]
    public void Soldered_hole_names_read_back()
    {
        Assert.Equal(new BoardHole(1, 12, 3), SolderedLayout.Parse("C12"));
        Assert.Equal(new BoardHole(1, 4, 27), SolderedLayout.Parse("aa4"));
        Assert.Equal("AA4", SolderedLayout.Name(SolderedLayout.Parse("AA4")!.Value));
        Assert.Null(SolderedLayout.Parse("12"));
        Assert.Null(SolderedLayout.Parse("C0"));
    }

    // ───────────────────────────── the meter's other ranges, and lessons ─────────────────────────────

    [Fact]
    public void The_ohmmeter_reads_resistance_continuity_and_open_with_the_power_off()
    {
        var circuit = new Circuit();
        var battery = circuit.Add(new Battery());
        var r1 = circuit.Add(new Resistor(4.7e3));
        var r2 = circuit.Add(new Resistor(10e3));
        var ground = circuit.Add(new Ground());
        circuit.Connect(battery.Terminals[0], r1.Terminals[0]);
        circuit.Connect(r1.Terminals[1], r2.Terminals[0]);
        circuit.Connect(r2.Terminals[1], ground.Terminals[0]);
        circuit.Connect(battery.Terminals[1], ground.Terminals[0]);

        // Across R1 alone: the battery is off the bench, so R2 is no parallel path.
        Assert.Equal(4.7e3, Ohmmeter.Between(circuit, r1.Terminals[0], r1.Terminals[1])!.Value, 4.7e3 * 1e-3);
        Assert.Equal(14.7e3, Ohmmeter.Between(circuit, r1.Terminals[0], r2.Terminals[1])!.Value, 14.7e3 * 1e-3);
        Assert.Equal(0, Ohmmeter.Between(circuit, r2.Terminals[1], battery.Terminals[1]));
        Assert.Equal("4.70k", Ohmmeter.Display(4.7e3));

        // A pin that isn't making contact reads open.
        FaultFinding.Hide(circuit, FaultKind.Open, r1.Terminals[1]);
        Assert.True(double.IsPositiveInfinity(Ohmmeter.Between(circuit, r1.Terminals[0], r2.Terminals[1])!.Value));
        Assert.Equal("OL", Ohmmeter.Display(double.PositiveInfinity));
    }

    [Fact]
    public void Fault_finding_measures_the_faulty_circuit_and_scores_the_find()
    {
        var (circuit, resistor, led) = LedCircuit();
        var before = FaultFinding.Measure(circuit, led.Terminals[0])!.Value;
        Assert.InRange(before, 1.5, 2.5);
        FaultFinding.Hide(circuit, FaultKind.Open, led.Terminals[0]);

        // With the LED's anode open no current flows, so the resistor's far end sits at the battery's voltage.
        Assert.InRange(FaultFinding.Measure(circuit, resistor.Terminals[1])!.Value, 8.5, 9.5);

        Assert.Equal(100, FaultFinding.Score(1, 0, TimeSpan.FromSeconds(20)));
        Assert.Equal(100 - 15 - 10 - 6, FaultFinding.Score(2, 1, TimeSpan.FromMinutes(3)));
        Assert.Equal(0, FaultFinding.Score(20, 5, TimeSpan.FromHours(1)));
    }
    // ───────────────────────────── printed circuit board ─────────────────────────────

    [Theory]
    [InlineData(BoardKind.Pcb, true)]
    [InlineData(BoardKind.Pcb, false)]
    [InlineData(BoardKind.DoubleSidedPcb, true)]
    [InlineData(BoardKind.DoubleSidedPcb, false)]
    public void A_pcb_is_routed_with_traces_that_never_touch_another_net(BoardKind kind, bool pour)
    {
        var circuit = GateCircuit();
        circuit.Board.Kind = kind;
        circuit.Board.GroundPour = pour;
        var layout = ProtoboardLayout.Arrange(circuit);
        Assert.Equal(kind, layout.Kind);
        Assert.Equal(pour, layout.Pour.Count > 0);
        Assert.True(layout.Matches, string.Join(" ", layout.Problems));
        Assert.Empty(layout.Cuts);
        Assert.Contains(layout.Wires, w => w.IsTrace);

        // Every trace starts on a pad of its own net, and the cells of different nets' traces (at half the pitch) never meet,
        // nor do they meet another net's pad.
        var netlist = circuit.BuildNetlist();
        var pads = layout.Parts.SelectMany(p => p.Package.Pins.Select((t, i) => (t, h: p.Holes[i]))).Where(x => x.t is not null)
            .ToDictionary(x => ((int)(2 * SolderedLayout.Position(x.h).X), (int)(2 * SolderedLayout.Position(x.h).Y)), x => netlist.NetOf(x.t!));
        var owner = new Dictionary<(int, int, int), Net>();
        foreach (var trace in layout.Wires.Where(w => w.IsTrace))
        {
            Assert.Same(trace.Net, layout.NetAt(trace.From));
            for (var i = 1; i < trace.Route.Count; i++)
            {
                var (a, b) = (trace.Route[i - 1], trace.Route[i]);
                var steps = (int)Math.Round(Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y)) * 2);
                for (var k = 0; k <= steps; k++)
                {
                    var cell = ((int)Math.Round(2 * (a.X + ((b.X - a.X) * k / Math.Max(1, steps)))), (int)Math.Round(2 * (a.Y + ((b.Y - a.Y) * k / Math.Max(1, steps)))));
                    if (pads.TryGetValue(cell, out var padNet)) Assert.Equal(trace.Net.Index, padNet.Index);
                    if (owner.TryGetValue((trace.Layer, cell.Item1, cell.Item2), out var other)) Assert.Same(trace.Net, other);
                    owner[(trace.Layer, cell.Item1, cell.Item2)] = trace.Net;
                    Assert.InRange(trace.Layer, 0, layout.Layers - 1);
                }
            }
        }

        var steps2 = BuildGuide.Steps(layout);
        Assert.StartsWith("Have the printed circuit board made", steps2[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_board_routes_round_a_crowded_chip_and_jumps_what_it_cannot()
    {
        // Every one of a 7400's inputs tied to the pin across the chip from it: traces must find their way between pins.
        var circuit = GateCircuit();
        var gate = circuit.Components.OfType<Ic7400>().Single();
        circuit.Connect(gate.TerminalByName("2A"), gate.TerminalByName("4B"));
        circuit.Connect(gate.TerminalByName("2B"), gate.TerminalByName("3A"));
        circuit.Connect(gate.TerminalByName("2Y"), gate.TerminalByName("3B"));
        circuit.Connect(gate.TerminalByName("3Y"), gate.TerminalByName("4A"));
        circuit.Board.Kind = BoardKind.Pcb;
        var layout = ProtoboardLayout.Arrange(circuit);
        Assert.True(layout.Matches, string.Join(" ", layout.Problems));
        Assert.All(layout.Wires.Where(w => !w.IsTrace), w => Assert.Equal(2, w.Route.Count));
    }

    [Fact]
    public void Gerber_files_hold_the_copper_mask_silkscreen_outline_and_drill()
    {
        var circuit = GateCircuit();
        circuit.Board.Kind = BoardKind.Pcb;
        circuit.Board.GroundPour = false;   // its regions are lines too; the pour has a test of its own
        var layout = ProtoboardLayout.Arrange(circuit);
        var files = PcbGerber.Files(layout, "gate board");
        Assert.Equal(["gate_board-B_Cu.gbl", "gate_board-B_Mask.gbs", "gate_board-Edge_Cuts.gm1", "gate_board-F_SilkS.gto", "gate_board.drl"], files.Keys.Order(StringComparer.Ordinal));

        var pins = layout.Parts.SelectMany(p => p.Holes).Distinct().Count();
        var copper = files["gate_board-B_Cu.gbl"];
        Assert.StartsWith("%TF.GenerationSoftware,CirqAvalonia*%", copper, StringComparison.Ordinal);
        Assert.Contains("%FSLAX46Y46*%", copper);
        Assert.Contains("%MOMM*%", copper);
        Assert.Contains("%TF.FileFunction,Copper,L1,Bot*%", copper);
        Assert.Equal(pins, copper.Split('\n').Count(l => l.EndsWith("D03*", StringComparison.Ordinal)));
        Assert.Equal(layout.Wires.Count(w => w.IsTrace), copper.Split('\n').Count(l => l.EndsWith("D02*", StringComparison.Ordinal)));
        Assert.EndsWith("M02*\n", copper, StringComparison.Ordinal);
        Assert.Contains("%TF.FilePolarity,Negative*%", files["gate_board-B_Mask.gbs"]);
        Assert.Contains("%TF.FileFunction,Profile,NP*%", files["gate_board-Edge_Cuts.gm1"]);
        Assert.True(files["gate_board-F_SilkS.gto"].Split('\n').Count(l => l.EndsWith("D01*", StringComparison.Ordinal)) > 20);

        // Every coordinate lies on the board.
        var (w, h) = (SolderedLayout.Width(layout.Columns) * 2.54e6, SolderedLayout.Height(layout.Rows) * 2.54e6);
        foreach (var file in files.Where(f => f.Key.EndsWith(".gbl", StringComparison.Ordinal) || f.Key.EndsWith(".gto", StringComparison.Ordinal)))
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(file.Value, "X(-?[0-9]+)Y(-?[0-9]+)"))
            {
                Assert.InRange(long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 0, (long)w);
                Assert.InRange(long.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), 0, (long)h);
            }

        var drill = files["gate_board.drl"];
        Assert.StartsWith("M48", drill, StringComparison.Ordinal);
        Assert.Contains("METRIC", drill);
        Assert.Equal(pins, drill.Split('\n').Count(l => l.StartsWith('X')));

        using var zip = new MemoryStream();
        PcbGerber.WriteZip(layout, zip, "gate board");
        zip.Position = 0;
        using var archive = new System.IO.Compression.ZipArchive(zip);
        Assert.Equal(5, archive.Entries.Count);

        circuit.Board.Kind = BoardKind.Perfboard;
        Assert.Throws<ArgumentException>(() => PcbGerber.Files(ProtoboardLayout.Arrange(circuit), "x"));
    }
    // ───────────────────────────── double-sided, the pour, the rules, the bill ─────────────────────────────

    /// <summary>Every input of a 7400 tied to a pin across the chip: crossings a single side can't make.</summary>
    private static Circuit CrowdedGate()
    {
        var circuit = GateCircuit();
        var gate = circuit.Components.OfType<Ic7400>().Single();
        circuit.Connect(gate.TerminalByName("2A"), gate.TerminalByName("4B"));
        circuit.Connect(gate.TerminalByName("2B"), gate.TerminalByName("3A"));
        circuit.Connect(gate.TerminalByName("2Y"), gate.TerminalByName("3B"));
        circuit.Connect(gate.TerminalByName("3Y"), gate.TerminalByName("4A"));
        return circuit;
    }

    [Fact]
    public void A_double_sided_board_crosses_with_vias_where_a_single_side_needs_jumpers()
    {
        var circuit = CrowdedGate();
        circuit.Board.GroundPour = false;
        circuit.Board.Kind = BoardKind.Pcb;
        var single = ProtoboardLayout.Arrange(circuit);
        circuit.Board.Kind = BoardKind.DoubleSidedPcb;
        var both = ProtoboardLayout.Arrange(circuit);
        Assert.True(both.Matches, string.Join(" ", both.Problems));
        Assert.Equal(2, both.Layers);
        Assert.Empty(single.Vias);
        Assert.True(both.Wires.Count(w => !w.IsTrace) <= single.Wires.Count(w => !w.IsTrace));

        // A via is where a trace changes side: a trace ends there on each.
        foreach (var via in both.Vias)
            Assert.Equal(2, both.Wires.Where(w => w.IsTrace && (w.From == via || w.To == via)).Select(w => w.Layer).Distinct().Count());
        if (both.Vias.Count > 0) Assert.Contains(both.Wires, w => w.IsTrace && w.Layer == 1);
    }

    [Fact]
    public void The_ground_pour_keeps_clear_of_other_nets_and_joins_grounds_pads()
    {
        var circuit = GateCircuit();
        circuit.Board.Kind = BoardKind.Pcb;
        circuit.Board.GroundPour = false;
        var bare = ProtoboardLayout.Arrange(circuit);
        circuit.Board.GroundPour = true;
        var poured = ProtoboardLayout.Arrange(circuit);
        Assert.True(poured.Matches, string.Join(" ", poured.Problems));
        Assert.NotEmpty(poured.Pour);

        // Ground needs no more traces than without the pour (usually none: the pour joins its pads).
        bool Ground(BoardWire w) => w.Net.IsGround && w.IsTrace;
        Assert.True(poured.Wires.Count(Ground) <= bare.Wires.Count(Ground));

        // No pour within the clearance of another net's pad, anywhere.
        var pitch = PcbRouter.Pitch;
        foreach (var part in poured.Parts)
            for (var i = 0; i < part.Holes.Count; i++)
            {
                if (poured.NetAt(part.Holes[i]) is { IsGround: true }) continue;
                var (x, y) = SolderedLayout.Position(part.Holes[i]);
                var keep = (PcbRouter.PadDiameter / 2 + GroundPour.Clearance - 0.01) / pitch;
                Assert.DoesNotContain(poured.Pour, a => Math.Max(Math.Abs(Math.Clamp(x, a.Left, a.Right) - x), 0) is var dx
                    && Math.Abs(Math.Clamp(y, a.Top, a.Bottom) - y) is var dy && Math.Sqrt((dx * dx) + (dy * dy)) < keep);
            }

        // The pour goes out in the Gerbers as regions, and the setting is saved.
        Assert.Contains("G36*", PcbGerber.Files(poured, "gate").Single(f => f.Key.EndsWith(".gbl", StringComparison.Ordinal)).Value);
        circuit.Board.GroundPour = false;
        var json = CircuitSerializer.ToJson(circuit);
        Assert.Contains("\"GroundPour\": false", json);
        Assert.False(CircuitSerializer.FromJson(json).Circuit.Board.GroundPour);
    }

    [Fact]
    public void A_double_sided_boards_gerbers_have_both_copper_layers_and_drill_the_vias()
    {
        var circuit = CrowdedGate();
        circuit.Board.Kind = BoardKind.DoubleSidedPcb;
        var layout = ProtoboardLayout.Arrange(circuit);
        var files = PcbGerber.Files(layout, "two");
        Assert.Contains("two-F_Cu.gtl", files.Keys);
        Assert.Contains("two-F_Mask.gts", files.Keys);
        Assert.Contains("%TF.FileFunction,Copper,L1,Top*%", files["two-F_Cu.gtl"]);
        Assert.Contains("%TF.FileFunction,Copper,L2,Bot*%", files["two-B_Cu.gbl"]);
        if (layout.Vias.Count > 0)
        {
            Assert.Contains("T2C0.500", files["two.drl"]);
            Assert.Equal(layout.Vias.Count, files["two.drl"].Split("T2\n")[1].Split('\n').Count(l => l.StartsWith('X')));
        }

        var json = CircuitSerializer.ToJson(circuit);
        Assert.Equal(BoardKind.DoubleSidedPcb, CircuitSerializer.FromJson(json).Circuit.Board.Kind);
    }

    [Fact]
    public void The_design_rule_check_holds_the_board_to_a_board_houses_limits()
    {
        var circuit = GateCircuit();
        circuit.Board.Kind = BoardKind.Pcb;
        var layout = ProtoboardLayout.Arrange(circuit);

        // A board house's standard rules: it passes.
        Assert.Empty(DesignRules.Check(layout, PcbRules.BoardHouse));
        Assert.Contains("passes", DesignRules.Summary([], PcbRules.BoardHouse));

        // Gaps a board this dense doesn't leave: neighbouring pads of a chip are 0.94 mm apart edge to edge.
        var etched = DesignRules.Check(layout, PcbRules.HomeEtching with { MinClearance = 1.0 });
        Assert.Contains(etched, i => i.Rule == "Clearance");
        Assert.All(etched, i => Assert.InRange(i.X, 0, SolderedLayout.Width(layout.Columns)));

        // A board too big for the budget size.
        var tight = PcbRules.Budget with { MaxWidth = 10, MaxHeight = 10 };
        Assert.Contains(DesignRules.Check(layout, tight), i => i.Rule == "Board size");

        // Rules finer than the parts allow: the holes and the rings.
        var fine = new PcbRules("Fine", 0.5, 0.1, 1.0, 0.5);
        var found = DesignRules.Check(layout, fine);
        Assert.Contains(found, i => i.Rule == "Trace width");
        Assert.Contains(found, i => i.Rule == "Drill");
        Assert.Contains(found, i => i.Rule == "Annular ring");
        Assert.Equal(0, DesignRules.SegmentDistance((0, 0), (2, 2), (0, 2), (2, 0)));
        Assert.Equal(1, DesignRules.SegmentDistance((0, 0), (2, 0), (0, 1), (2, 1)), 9);
    }

    [Fact]
    public void The_bill_of_materials_groups_the_parts_and_lists_the_bench_apart()
    {
        var circuit = GateCircuit();
        var extra = circuit.Add(new Resistor(10e3));
        var lines = BillOfMaterials.Lines(circuit);
        var tenK = lines.Single(l => l.Value == "10kΩ");
        Assert.Equal(2, tenK.Quantity);
        Assert.Equal(["R1", extra.Name], tenK.Designators);
        Assert.Contains(lines, l => l.Part == "7400" && l.Package == "DIP-14");
        Assert.Equal("axial resistor", tenK.Package);
        Assert.DoesNotContain(lines, l => l.Designators.Any(d => d.StartsWith("GND", StringComparison.Ordinal)));

        var csv = BillOfMaterials.Csv(circuit);
        Assert.StartsWith("Designators,Quantity,Part,Value,Package\r\n", csv, StringComparison.Ordinal);
        Assert.Contains("On the bench", csv);
        Assert.Contains($"R1 {extra.Name},2,", csv);
    }
}
