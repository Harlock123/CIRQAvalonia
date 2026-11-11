using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cirq.Components.Protoboard;
using Cirq.UI.Controls;
using Cirq.UI.Services;
using Cirq.UI.Tests.Support;
using Cirq.UI.ViewModels;
using Cirq.UI.Views;

namespace Cirq.UI.Tests;

/// <summary>Every example built on breadboards, and the protoboard view in the window: F11, dragging, undo, the export.</summary>
[Collection(WindowCollection.Name)]
public class ProtoboardTests(WindowSession session) : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("cirq-board-").FullName;
    private readonly List<Window> _windows = [];

    public void Dispose()
    {
        session.Run(() =>
        {
            foreach (var window in _windows) window.Close();
        });
        try { Directory.Delete(_folder, true); }
        catch (IOException) { }
    }

    [Fact]
    public void Every_example_builds_on_the_board_and_the_board_matches_its_schematic()
    {
        var library = new Cirq.UI.Agents.EditorLibrary();
        var bad = new List<string>();
        var (wires, over) = (0, 0);
        foreach (var example in library.Examples)
        {
            var circuit = library.LoadExample(example.Name).Circuit;
            var layout = ProtoboardLayout.Arrange(circuit);
            if (!layout.Matches) bad.Add($"{example.Name}: {string.Join(" ", layout.Problems)}");
            else if (layout.Parts.Count + layout.Bench.Count == 0) bad.Add($"{example.Name}: nothing on the board");

            // The wires are routed round the parts: what's left over a body is a wire's tip going into a hole beside
            // one, a pitch or two on a board, never wires lying across chips.
            if (layout.CoveredLength > 8) bad.Add($"{example.Name}: {layout.CoveredLength:0.0} pitches of wire over parts");
            wires += layout.Wires.Count + layout.Bench.Sum(b => b.Leads.Count);
            over += layout.WiresOverParts;
        }

        Assert.True(bad.Count == 0, string.Join("\n", bad));
        Assert.True(over < wires / 10, $"{over} of {wires} wires pass over a part");
    }

    [Fact]
    public void F11_shows_the_board_a_lead_dragged_stays_where_it_is_put_and_Ctrl_Z_puts_it_back() => session.Run(() =>
    {
        var editor = new MainWindowViewModel { Autosave = new AutosaveStore(Path.Combine(_folder, "recovery.cirq")) };
        var window = new MainWindow { DataContext = editor, Width = 1400, Height = 900 };
        window.Show();
        _windows.Add(window);
        Dispatcher.UIThread.RunJobs();

        var board = window.FindControl<ProtoboardCanvas>("Board")!;
        var schematic = window.FindControl<CircuitCanvas>("Canvas")!;
        Assert.False(board.IsVisible);

        window.KeyPress(Key.F11, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(editor.IsProtoboardView);
        Assert.True(board.IsVisible);
        Assert.False(schematic.IsVisible);
        Assert.Contains("matches the schematic", editor.StatusMessage);

        window.Measure(window.ClientSize);
        window.Arrange(new Avalonia.Rect(window.ClientSize));
        Dispatcher.UIThread.RunJobs();

        // The opening circuit's resistor: drag its first lead three columns along.
        var layout = board.Layout!;
        var resistor = layout.Parts.First(p => p.Package.Look == BoardLook.Resistor);
        var origin = resistor.Holes[0];
        var target = origin with { Column = origin.Column - 3 };
        var start = board.TranslatePoint(board.ScreenOf(origin), window)!.Value;
        var end = board.TranslatePoint(board.ScreenOf(target), window)!.Value;
        window.MouseMove(start);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(new Avalonia.Point((start.X + end.X) / 2, start.Y));
        window.MouseMove(end);
        window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.False(editor.Circuit.Board.IsEmpty, editor.StatusMessage);
        var after = board.Layout!;
        Assert.Equal(target, after.Find(resistor.Part)!.Holes[0]);
        Assert.True(after.Matches);
        Assert.True(editor.IsModified);
        Assert.Contains("matches the schematic", editor.StatusMessage);

        // One undo step, and the layout has it again.
        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(editor.Circuit.Board.IsEmpty);
        Assert.Equal(origin, board.Layout!.Find(editor.Circuit.Components.First(c => c.Name == resistor.Part.Name))!.Holes[0]);

        // Back to the schematic.
        window.KeyPress(Key.F11, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(editor.IsProtoboardView);
        Assert.True(schematic.IsVisible);

        // The buttons at the canvas's corner: each shows its view, and pressing the one showing leaves it.
        var protoboardButton = window.FindControl<Button>("ProtoboardViewButton")!;
        var schematicButton = window.FindControl<Button>("SchematicViewButton")!;
        window.Click(protoboardButton);
        Assert.True(editor.IsProtoboardView);
        window.Click(protoboardButton);
        Assert.True(editor.IsProtoboardView);
        window.Click(schematicButton);
        Assert.False(editor.IsProtoboardView);
    });

    /// <summary>The board exported as a picture, and the guide's picture of it drawn the same way.</summary>
    [Fact]
    public void The_board_exports_as_a_picture_as_the_view_draws_it()
    {
        var library = new Cirq.UI.Agents.EditorLibrary();
        var circuit = library.LoadExample("LED Chaser").Circuit;
        var path = Path.Combine(_folder, "board.png");
        var exported = CircuitExporter.Export(circuit, null, path, new ExportOptions(ExportFormat.Png, ExportContent.Protoboard, RasterScale: 1.2));
        Assert.Equal([path], exported);
        using (var image = SkiaSharp.SKBitmap.Decode(path))
        {
            Assert.True(image.Width > 900 && image.Height > 200, $"{image.Width}×{image.Height}");
            // The board is white where it isn't drilled: the middle of the channel's top edge.
        }

        var svg = Path.Combine(_folder, "board.svg");
        CircuitExporter.Export(circuit, null, svg, new ExportOptions(ExportFormat.Svg, ExportContent.Protoboard));
        Assert.Contains("<svg", File.ReadAllText(svg));

        var empty = new Cirq.Core.Topology.Circuit();
        Assert.Throws<InvalidOperationException>(() => CircuitExporter.Export(empty, null, Path.Combine(_folder, "none.png"), new ExportOptions(ExportFormat.Png, ExportContent.Protoboard)));

        Directory.CreateDirectory(DocPlot.ImageDirectory);
        CircuitExporter.Export(circuit, null, Path.Combine(DocPlot.ImageDirectory, "64-protoboard.png"), new ExportOptions(ExportFormat.Png, ExportContent.Protoboard, RasterScale: 1.4));
    }
}
