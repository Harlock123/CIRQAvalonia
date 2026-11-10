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
}
