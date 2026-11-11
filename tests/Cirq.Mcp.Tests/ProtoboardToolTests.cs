using Cirq.Components.Protoboard;
using static Cirq.Mcp.Tests.Calls;

namespace Cirq.Mcp.Tests;

/// <summary>render_protoboard and arrange_protoboard: the circuit built on breadboards, as an agent sees and moves it.</summary>
public class ProtoboardToolTests
{
    [Fact]
    public async Task The_board_is_drawn_with_where_everything_is_and_a_part_moves_by_hand_in_one_undo_step()
    {
        using var b = new Bench();
        await b.BuildRc();

        var picture = await b.Tools.RenderProtoboard(scale: 8);
        var png = Png(picture);
        Assert.True(png.Length > 1000);
        var text = Text(picture);
        Assert.Contains("1 board, 2 parts on the board, 1 on the bench", text);
        Assert.Contains("R1 (10kΩ): A=", text);
        Assert.Contains("FG1 on the bench: OUT→", text);
        Assert.Contains("The board matches the schematic.", text);

        // Move the resistor's B lead along, then the capacitor as a whole; the circuit itself doesn't change.
        var before = Cirq.Components.Serialization.CircuitSerializer.ToJson(b.Circuit);
        var layout = ProtoboardLayout.Arrange(b.Circuit);
        var r1 = layout.Find(b.Circuit.Components[1])!;
        var target = r1.Holes[1] with { Column = r1.Holes[1].Column + 2 };
        var moved = await Parse(b.Tools.ArrangeProtoboard([new() { Part = "R1", To = Breadboard.Name(target), Pin = "B" }]));
        Assert.True((bool)moved["matches"]!);
        Assert.Contains($"B={Breadboard.Name(target)}", (string)moved["board"]!);
        Assert.Contains("(placed by hand)", (string)moved["board"]!);
        Assert.Equal(b.Circuit.Wires.Count, Cirq.Components.Serialization.CircuitSerializer.FromJson(before).Circuit.Wires.Count);

        // One undo step puts it back the way the layout had it.
        await b.Tools.Undo();
        Assert.True(b.Circuit.Board.IsEmpty);

        // A hole that isn't one, a part on the bench, a move that would short two nets: refused, saying why, nothing changed.
        Assert.Contains("isn't a hole", await Error(b.Tools.ArrangeProtoboard([new() { Part = "R1", To = "z9" }])));
        Assert.Contains("on the bench", await Error(b.Tools.ArrangeProtoboard([new() { Part = "FG1", To = "a20" }])));
        // (Undo rebuilt the parts, so the board is looked at afresh.)
        var c1 = ProtoboardLayout.Arrange(b.Circuit).Find(b.Circuit.Components[2])!;
        Assert.Contains("Couldn't move R1", await Error(b.Tools.ArrangeProtoboard([new() { Part = "R1", To = Breadboard.Name(c1.Holes[1]), Pin = "A" }])));
        Assert.True(b.Circuit.Board.IsEmpty);
        Assert.Contains("nothing to do", await Error(b.Tools.ArrangeProtoboard()));

        // Reset lays it out afresh.
        await b.Tools.ArrangeProtoboard([new() { Part = "R1", To = Breadboard.Name(target), Pin = "B" }]);
        Assert.False(b.Circuit.Board.IsEmpty);
        var reset = await Parse(b.Tools.ArrangeProtoboard(reset: true));
        Assert.True(b.Circuit.Board.IsEmpty);
        Assert.DoesNotContain("by hand", (string)reset["board"]!);
    }

    [Fact]
    public async Task The_board_type_the_build_guide_and_a_fault_to_find_are_there_for_an_agent()
    {
        using var b = new Bench();
        await b.BuildRc();

        // The build guide, numbered, ending with the check.
        var guide = await Parse(b.Tools.BuildGuide());
        Assert.Equal("breadboard", (string)guide["board"]!);
        var steps = guide["steps"]!.AsArray().Select(x => (string)x!).ToList();
        Assert.StartsWith("1. ", steps[0]);
        Assert.Contains(steps, x => x.Contains("R1, 10kΩ (brown, black, orange, gold)", StringComparison.Ordinal));
        Assert.Contains("matches the schematic", steps[^1]);

        // Onto stripboard, an undo step; a part moved on it by its strip letter and hole number.
        var strip = await Parse(b.Tools.ArrangeProtoboard(board: "stripboard"));
        Assert.True((bool)strip["matches"]!);
        Assert.Contains("A stripboard", (string)strip["board"]!);
        var movedOnStrip = await Parse(b.Tools.ArrangeProtoboard([new() { Part = "R1", To = "T20", Pin = "A" }]));
        Assert.True((bool)movedOnStrip["matches"]!);
        Assert.Contains("A=T20", (string)movedOnStrip["board"]!);
        Assert.Contains("isn't a hole", await Error(b.Tools.ArrangeProtoboard([new() { Part = "R1", To = "12" }])));
        await b.Tools.Undo();
        Assert.Equal(Cirq.Core.Topology.BoardKind.Stripboard, b.Circuit.Board.Kind);
        Assert.Contains("breadboard, stripboard, perfboard or pcb", await Error(b.Tools.ArrangeProtoboard(board: "veroboard")));
        Assert.Contains(steps.Count > 0 ? "Cut" : "", string.Join(" ", (await Parse(b.Tools.BuildGuide()))["steps"]!.AsArray().Select(x => (string)x!)));
        await b.Tools.Undo();
        Assert.Equal(Cirq.Core.Topology.BoardKind.Breadboard, b.Circuit.Board.Kind);

        // A fault: hidden without saying where, hinted at, guessed wrong, then right.
        var hidden = await Parse(b.Tools.Fault("hide"));
        Assert.True((bool)hidden["hidden"]!);
        Assert.DoesNotContain("R1", (string)hidden["note"]!);
        Assert.True((bool)(await Parse(b.Tools.Fault("status")))["hidden"]!);
        Assert.StartsWith("Hint:", (string)(await Parse(b.Tools.Fault("hint", level: 3)))["hint"]!);
        var at = b.Circuit.Faults[0].Parts.First();
        var other = b.Circuit.Components.First(c => c.Name != at.Name && Cirq.Components.Protoboard.BoardPackages.IsBuilt(c));
        Assert.False((bool)(await Parse(b.Tools.Fault("guess", part: other.Name)))["right"]!);
        var found = await Parse(b.Tools.Fault("guess", part: at.Name));
        Assert.True((bool)found["right"]!);
        Assert.Empty(b.Circuit.Faults);

        // One chosen, and one that can't be.
        Assert.Contains("isn't making contact", (string)(await Parse(b.Tools.Fault("hide", pin: "C1.A")))["note"]!);
        Assert.Contains("backwards", await Error(b.Tools.Fault("hide", pin: "R1.A", kind: "reversed")));
        Assert.Contains("C1", (string)(await Parse(b.Tools.Fault("reveal")))["fault"]!);
        Assert.Contains("hide, hint, measure, guess", await Error(b.Tools.Fault("find")));

        // On a PCB, routed and matching; and measure reads the faulty circuit, as a meter would.
        var pcb = await Parse(b.Tools.ArrangeProtoboard(board: "pcb"));
        Assert.True((bool)pcb["matches"]!);
        Assert.Contains("Have the printed circuit board made", string.Join(" ", (await Parse(b.Tools.BuildGuide()))["steps"]!.AsArray().Select(x => (string)x!)));
        await Parse(b.Tools.Fault("hide", pin: "C1.A"));
        var reading = await Parse(b.Tools.Fault("measure", pin: "R1.B"));
        Assert.Equal("R1.B", (string)reading["pin"]!);
        Assert.NotNull(reading["volts"]);
        Assert.Contains("say which pin", await Error(b.Tools.Fault("measure")), StringComparison.OrdinalIgnoreCase);
    }
}
