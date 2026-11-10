using Cirq.Components.Digital;
using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Processors;
using Cirq.Components.Protoboard;
using Cirq.Components.Serialization;
using Cirq.Components.Sources;
using Cirq.Core.Topology;

namespace Cirq.Components.Tests;

/// <summary>The protoboard: the board's holes, how each part is built on it, the automatic layout, moves by hand and the file.</summary>
public class ProtoboardTests
{
    /// <summary>A 9 V battery lighting an LED through a 1 kΩ resistor: about the smallest thing anyone builds on a breadboard.</summary>
    private static (Circuit Circuit, Battery Battery, Resistor Resistor, Led Led) LedCircuit()
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
        return (circuit, battery, resistor, led);
    }

    [Fact]
    public void Holes_are_named_as_the_board_prints_them_and_read_back()
    {
        Assert.Equal("e12", Breadboard.Name(new BoardHole(1, Breadboard.E, 12)));
        Assert.Equal("2:f1", Breadboard.Name(new BoardHole(2, Breadboard.F, 1)));
        Assert.Equal("T+5", Breadboard.Name(new BoardHole(1, Breadboard.TopPlus, 5)));
        foreach (var name in new[] { "a1", "j63", "T+3", "T-61", "B-30", "B+45", "3:c7" })
            Assert.Equal(name, Breadboard.Name(Breadboard.Parse(name)!.Value));
        Assert.Equal(new BoardHole(1, Breadboard.BottomMinus, 30), Breadboard.Parse("b−30"));
        // Not holes: past the end, a rail where it has no hole (between two groups of five), nonsense.
        Assert.Null(Breadboard.Parse("a64"));
        Assert.Null(Breadboard.Parse("T+8"));
        Assert.Null(Breadboard.Parse("k3"));
        Assert.Null(Breadboard.Parse("hello"));
        Assert.Equal(50, Breadboard.RailColumns.Count);

        // Five holes to a strip either side of the channel, a rail the whole length.
        var strip = Breadboard.StripOf(new BoardHole(1, Breadboard.A + 2, 9));
        Assert.Equal(5, Breadboard.HolesOf(strip).Count());
        Assert.Equal(strip, Breadboard.StripOf(new BoardHole(1, Breadboard.E, 9)));
        Assert.NotEqual(strip, Breadboard.StripOf(new BoardHole(1, Breadboard.F, 9)));
        Assert.Equal(50, Breadboard.HolesOf(Breadboard.StripOf(new BoardHole(1, Breadboard.TopMinus, 3))).Count());
    }

    [Fact]
    public void Each_part_is_built_from_the_package_it_is_bought_in()
    {
        var opamp = BoardPackages.For(new OpAmp741());
        Assert.Equal(BoardForm.Dip, opamp.Form);
        Assert.Equal(8, opamp.Pins.Count);
        Assert.Equal("in-", opamp.Pins[1]!.Id);
        Assert.Equal("v+", opamp.Pins[6]!.Id);
        Assert.Null(opamp.Pins[0]);

        var nand = BoardPackages.For(new Ic7400());
        Assert.Equal((BoardForm.Dip, 14, 3), (nand.Form, nand.Pins.Count, nand.Span));
        Assert.Equal("GND", nand.Pins[6]!.Name);

        // A 6502 is a wide 40-pin DIP whose pin 5 isn't connected; the 68000 is the 0.9 inch 64-pin.
        var cpu = BoardPackages.For(new Cpu6502());
        Assert.Equal((40, 6), (cpu.Pins.Count, cpu.Span));
        Assert.Null(cpu.Pins[4]);
        Assert.Equal(9, BoardPackages.For(new Cpu68000()).Span);

        var transistor = BoardPackages.For(new BipolarTransistor());
        Assert.Equal(BoardForm.Inline, transistor.Form);
        Assert.Equal(BoardLook.To92, transistor.Look);
        Assert.Equal(["e", "b", "c"], transistor.Pins.Select(p => p!.Id));

        Assert.Equal(BoardForm.TwoLead, BoardPackages.For(new Resistor()).Form);
        Assert.Equal(BoardLook.Led, BoardPackages.For(new Led()).Look);
        Assert.Equal(BoardForm.Bench, BoardPackages.For(new DcVoltageSource()).Form);
        Assert.Equal(BoardForm.Bench, BoardPackages.For(new FunctionGenerator()).Form);
        Assert.Equal(BoardForm.None, BoardPackages.For(new Ground()).Form);
        Assert.Equal(BoardForm.None, BoardPackages.For(new NetLabel()).Form);
    }

    [Fact]
    public void A_circuit_is_laid_out_on_the_board_with_the_supply_and_ground_on_the_rails_and_matches_its_schematic()
    {
        var (circuit, battery, resistor, led) = LedCircuit();
        var layout = ProtoboardLayout.Arrange(circuit);

        Assert.True(layout.Matches, string.Join(" ", layout.Problems));
        Assert.Equal(1, layout.Boards);
        Assert.Equal(2, layout.Parts.Count);
        var bench = Assert.Single(layout.Bench);
        Assert.Same(battery, bench.Part);
        Assert.Equal(2, bench.Leads.Count);

        // Ground on both − rails, the battery's + on the + rails, and the rails say so.
        Assert.True(layout.Rails[Breadboard.TopMinus].IsGround);
        Assert.Same(layout.Rails[Breadboard.TopMinus], layout.Rails[Breadboard.BottomMinus]);
        Assert.Contains(battery.Terminals[0], layout.Rails[Breadboard.TopPlus].Terminals);
        Assert.Equal("+9V", layout.Caption(layout.Rails[Breadboard.TopPlus]));
        Assert.Equal(WireRole.Ground, bench.Leads.Single(l => l.Terminal == battery.Terminals[1]).Role);

        // The resistor and the LED meet in one strip, and the LED's cathode is on ground.
        var r = layout.Find(resistor)!;
        var d = layout.Find(led)!;
        Assert.Contains(resistor.Terminals[1], layout.NetAt(r.Holes[1])!.Terminals);
        Assert.Same(layout.NetAt(r.Holes[1]), layout.NetAt(d.Holes[0]));
        Assert.True(layout.NetAt(d.Holes[1])!.IsGround);
        Assert.Contains("matches the schematic", layout.Describe());
    }

    [Fact]
    public void A_moved_part_stays_where_it_was_put_the_rest_stays_still_and_a_move_that_would_short_two_nets_is_refused()
    {
        var (circuit, _, resistor, led) = LedCircuit();
        var layout = ProtoboardLayout.Arrange(circuit);
        var r = layout.Find(resistor)!;
        var d = layout.Find(led)!;

        // Along four columns: the resistor goes, the LED stays where it was, and it all still matches.
        var to = r.Holes[0] with { Column = r.Holes[0].Column + 20 };
        var moved = ProtoboardEdits.MovePart(circuit, layout, resistor, r.Holes[0], to);
        Assert.True(moved.Succeeded, moved.Problem);
        circuit.Board.CopyFrom(moved.Arrangement!);
        var after = ProtoboardLayout.Arrange(circuit);
        Assert.True(after.Matches, string.Join(" ", after.Problems));
        Assert.Equal(to, after.Find(resistor)!.Holes[0]);
        Assert.True(after.Find(resistor)!.ByHand);
        Assert.Equal(d.Holes, after.Find(led)!.Holes);

        // The LED's anode into a strip carrying ground: refused, saying why.
        var groundHole = after.Strips.First(s => s.Value.IsGround && !s.Key.IsRail).Key;
        var free = Breadboard.HolesOf(groundHole).First(h => after.PartAt(h) is null && after.Wires.All(w => w.From != h && w.To != h));
        var shorted = ProtoboardEdits.MoveLead(circuit, after, led, led.Terminals[0], free);
        Assert.False(shorted.Succeeded);
        Assert.Matches("join|short", shorted.Problem!);

        // Back to the layout, and everything placed afresh.
        var released = ProtoboardEdits.Release(circuit, after, resistor);
        Assert.False(released.Arrangement!.Parts.ContainsKey(resistor.Id));
        circuit.Board.Clear();
        Assert.True(circuit.Board.IsEmpty);
    }

    [Fact]
    public void A_wire_end_moves_along_its_strip_and_nowhere_else()
    {
        var (circuit, _, _, _) = LedCircuit();
        var layout = ProtoboardLayout.Arrange(circuit);
        var wire = layout.Wires.FirstOrDefault();
        if (wire is null)
        {
            // Give it one: the LED moved well away from the resistor needs a jumper back.
            var led = layout.Find(circuit.Components.OfType<Led>().Single())!;
            var edit = ProtoboardEdits.MovePart(circuit, layout, led.Part, led.Holes[0], led.Holes[0] with { Column = led.Holes[0].Column + 25 });
            Assert.True(edit.Succeeded, edit.Problem);
            circuit.Board.CopyFrom(edit.Arrangement!);
            layout = ProtoboardLayout.Arrange(circuit);
            wire = layout.Wires.First();
        }

        var strip = Breadboard.StripOf(wire.From);
        var spare = Breadboard.HolesOf(strip).FirstOrDefault(h => h != wire.From && layout.PartAt(h) is null && layout.Wires.All(w => w.From != h && w.To != h)
                                                                && layout.Bench.All(b => b.Leads.All(l => l.To != h)));
        var along = ProtoboardEdits.MoveWireEnd(circuit, layout, wire, fromEnd: true, spare);
        Assert.True(along.Succeeded, along.Problem);
        circuit.Board.CopyFrom(along.Arrangement!);
        var after = ProtoboardLayout.Arrange(circuit);
        Assert.True(after.Matches);
        Assert.Contains(after.Wires, w => w.ByHand && (w.From == spare || w.To == spare));

        var elsewhere = ProtoboardEdits.MoveWireEnd(circuit, after, after.Wires.First(w => w.ByHand), fromEnd: true, new BoardHole(1, Breadboard.A, 60));
        Assert.False(elsewhere.Succeeded);
        Assert.Contains("own strip", elsewhere.Problem);
    }

    [Fact]
    public void The_board_made_by_hand_is_saved_with_the_circuit_and_a_board_left_alone_adds_nothing_to_the_file()
    {
        var (circuit, _, resistor, _) = LedCircuit();
        Assert.DoesNotContain("Protoboard", CircuitSerializer.ToJson(circuit));

        var layout = ProtoboardLayout.Arrange(circuit);
        var r = layout.Find(resistor)!;
        var edit = ProtoboardEdits.MovePart(circuit, layout, resistor, r.Holes[0], r.Holes[0] with { Column = r.Holes[0].Column + 12 });
        circuit.Board.CopyFrom(edit.Arrangement!);
        var json = CircuitSerializer.ToJson(circuit);
        Assert.Contains("\"Protoboard\"", json);

        var back = CircuitSerializer.FromJson(json);
        Assert.True(back.IsClean, string.Join(" ", back.Warnings));
        var reread = ProtoboardLayout.Arrange(back.Circuit);
        var original = ProtoboardLayout.Arrange(circuit);
        Assert.Equal(
            original.Parts.Select(p => (p.Part.Name, string.Join(",", p.Holes.Select(Breadboard.Name)))).OrderBy(p => p.Name),
            reread.Parts.Select(p => (p.Part.Name, string.Join(",", p.Holes.Select(Breadboard.Name)))).OrderBy(p => p.Name));
        Assert.Equal(original.Wires.Count, reread.Wires.Count);

        // A hole the board doesn't have is a warning, and that part is laid out afresh.
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var record = node["Protoboard"]!["Parts"]!.AsArray().First(p => p!["Pins"] is not null)!;
        var pins = record["Pins"]!.AsObject();
        pins[pins.First().Key] = "z99";
        var broken = CircuitSerializer.FromJson(node.ToJsonString());
        Assert.Contains(broken.Warnings, w => w.Contains("protoboard", StringComparison.Ordinal));
        Assert.Equal(circuit.Board.Parts.Count - 1, broken.Circuit.Board.Parts.Count);
        Assert.False(broken.Circuit.Board.Parts.ContainsKey(Guid.Parse(record["Id"]!.GetValue<string>())));
        Assert.True(ProtoboardLayout.Arrange(broken.Circuit).Matches);
    }

    [Fact]
    public void A_chip_straddles_the_channel_pin_one_bottom_left_and_its_pins_are_joined_as_the_schematic_says()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5));
        var gate = circuit.Add(new Ic7400());
        var pull = circuit.Add(new Resistor(10e3));
        var ground = circuit.Add(new Ground());
        circuit.Connect(supply.Terminals[1], ground.Terminals[0]);
        circuit.Connect(supply.Terminals[0], gate.TerminalByName("VCC"));
        circuit.Connect(gate.TerminalByName("GND"), ground.Terminals[0]);
        circuit.Connect(gate.TerminalByName("1A"), gate.TerminalByName("1B"));
        circuit.Connect(pull.Terminals[0], supply.Terminals[0]);
        circuit.Connect(pull.Terminals[1], gate.TerminalByName("1A"));

        var layout = ProtoboardLayout.Arrange(circuit);
        Assert.True(layout.Matches, string.Join(" ", layout.Problems));
        var chip = layout.Find(gate)!;
        Assert.Equal(Breadboard.F, chip.Holes[0].Row);
        Assert.Equal(Breadboard.E, chip.Holes[13].Row);
        Assert.Equal(chip.Holes[0].Column, chip.Holes[13].Column);
        Assert.Equal(chip.Holes[0].Column + 6, chip.Holes[6].Column);

        // VCC (14) and 1A/1B (1, 2) reach their nets; 1A and 1B are joined by a wire between their strips.
        Assert.Same(layout.Rails[Breadboard.TopPlus], layout.NetAt(chip.Holes[13]));
        Assert.Same(layout.NetAt(chip.Holes[0]), layout.NetAt(chip.Holes[1]));
        Assert.NotEqual(Breadboard.StripOf(chip.Holes[0]), Breadboard.StripOf(chip.Holes[1]));
    }
}
