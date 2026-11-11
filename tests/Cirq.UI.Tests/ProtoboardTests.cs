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

        // And on stripboard, perfboard and a PCB, laid out automatically: every one matching its schematic too.
        foreach (var kind in new[] { Cirq.Core.Topology.BoardKind.Stripboard, Cirq.Core.Topology.BoardKind.Perfboard, Cirq.Core.Topology.BoardKind.Pcb })
            foreach (var example in library.Examples)
            {
                var circuit = library.LoadExample(example.Name).Circuit;
                circuit.Board.Kind = kind;
                var layout = ProtoboardLayout.Arrange(circuit);
                if (!layout.Matches) bad.Add($"{example.Name} on {kind}: {string.Join(" ", layout.Problems)}");
                if (BuildGuide.Steps(layout) is not [{ Kind: BuildStepKind.Board }, .., { Kind: BuildStepKind.Check }]) bad.Add($"{example.Name} on {kind}: no build guide");
            }

        Assert.True(bad.Count == 0, string.Join("\n", bad));
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

    [Fact]
    public void Stripboard_the_build_guide_the_meter_and_fault_finding_work_in_the_window() => session.Run(() =>
    {
        var editor = new MainWindowViewModel { Autosave = new AutosaveStore(Path.Combine(_folder, "recovery-2.cirq")) };
        var window = new MainWindow { DataContext = editor, Width = 1400, Height = 900 };
        window.Show();
        _windows.Add(window);
        Dispatcher.UIThread.RunJobs();
        var board = window.FindControl<ProtoboardCanvas>("Board")!;

        // Onto stripboard with its button: an undo step, the board laid out again on strips.
        editor.ShowProtoboardCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        window.Click(window.FindControl<Button>("StripboardButton")!);
        Assert.True(editor.IsStripboard);
        Assert.True(editor.IsModified);
        Assert.Equal(Cirq.Core.Topology.BoardKind.Stripboard, board.Layout!.Kind);
        Assert.True(board.Layout.Matches);
        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(editor.IsBreadboard);
        window.Click(window.FindControl<Button>("StripboardButton")!);

        // The build guide: step by step, the board showing only what the steps so far fit.
        window.Click(window.FindControl<Button>("BuildGuideButton")!);
        Dispatcher.UIThread.RunJobs();
        Assert.True(editor.IsBuildGuideOpen);
        Assert.Equal(0, board.BuildStep);
        Assert.Equal(board.BuildSteps.Count, editor.BuildSteps.Count);
        Assert.StartsWith("Step 1 of", editor.BuildGuideTitle);
        Assert.Contains("stripboard", editor.BuildGuideText);
        window.Click(window.FindControl<Button>("BuildNextButton")!);
        Assert.Equal(1, editor.BuildGuideStep);
        Assert.Equal(1, board.BuildStep);
        Assert.Equal(board.BuildSteps[1].Text, window.FindControl<TextBlock>("BuildGuideText")!.Text);
        editor.LastBuildStepCommand.Execute(null);
        Assert.Contains("matches the schematic", editor.BuildGuideText);
        window.Click(window.FindControl<Button>("BuildNextButton")!);
        Assert.Equal(board.BuildSteps.Count - 1, editor.BuildGuideStep);
        editor.CloseBuildGuideCommand.Execute(null);
        Assert.Equal(-1, board.BuildStep);

        // The meter: its black probe on ground, the red one dragged onto the supply, reading it once the circuit runs.
        window.Click(window.FindControl<Button>("MeterButton")!);
        Assert.True(board.MeterShown);
        window.Measure(window.ClientSize);
        window.Arrange(new Avalonia.Rect(window.ClientSize));
        board.RequestFit();
        Dispatcher.UIThread.RunJobs();
        var layout = board.Layout!;
        var generator = layout.Bench.Single().Leads.Single(l => !l.Net.IsGround);
        editor.Simulation.Rebuild();
        board.InvalidateVisual();
        Dispatcher.UIThread.RunJobs();
        var meterTip = MeterTip(board, window);
        var target = board.TranslatePoint(board.ScreenOf(generator.To), window)!.Value;
        window.MouseMove(meterTip);
        window.MouseDown(meterTip, MouseButton.Left);
        window.MouseMove(target);
        window.MouseUp(target, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.StartsWith("Meter: ", editor.StatusMessage);
        Assert.Contains(" V, ", editor.StatusMessage);

        // Fault finding: hidden, a wrong guess and a hint, then found.
        editor.HideFaultCommand.Execute(null);
        Assert.True(editor.HasHiddenFault);
        var fault = editor.Circuit.Faults[0];
        var innocent = editor.Circuit.Components.First(c => !fault.Parts.Contains(c));
        editor.SelectedComponent = innocent;
        editor.GuessFaultCommand.Execute(null);
        Assert.StartsWith($"Not {innocent.Name}", editor.StatusMessage);
        editor.FaultHintCommand.Execute(null);
        Assert.StartsWith("Hint:", editor.StatusMessage);
        editor.SelectedComponent = fault.First.Owner;
        editor.GuessFaultCommand.Execute(null);
        Assert.StartsWith("Found it in 2 guesses and 1 hint", editor.StatusMessage);
        Assert.False(editor.HasHiddenFault);
    });

    /// <summary>Where the red probe's tip is parked on the meter, on the window, read back from the meter as drawn.</summary>
    private static Avalonia.Point MeterTip(ProtoboardCanvas board, Window window)
    {
        var layout = board.Layout!;
        var (_, _, _, bottom) = layout.Extent;
        var (x, y) = (2.0 + 8.0, bottom + 2.0 - 2.0);
        var screen = board.ScreenOfPitches(x, y);
        return board.TranslatePoint(screen, window)!.Value;
    }

    [Fact]
    public void The_build_sheet_has_the_picture_the_parts_and_every_step()
    {
        var library = new Cirq.UI.Agents.EditorLibrary();
        var circuit = library.LoadExample("555 Astable").Circuit;
        var layout = ProtoboardLayout.Arrange(circuit);
        var steps = BuildGuide.Steps(layout);
        var html = BuildSheet.Html("555 Astable", circuit, layout, steps);
        Assert.Contains("<img alt=\"The finished board\" src=\"data:image/png;base64,", html);
        Assert.Contains("NE555", html);
        foreach (var step in steps) Assert.Contains(System.Net.WebUtility.HtmlEncode(step.Text), html);
        Assert.Contains("The board matches the schematic.", html);
    }

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
        circuit.Board.Kind = Cirq.Core.Topology.BoardKind.Stripboard;
        CircuitExporter.Export(circuit, null, Path.Combine(DocPlot.ImageDirectory, "65-stripboard.png"), new ExportOptions(ExportFormat.Png, ExportContent.Protoboard, RasterScale: 1.4));
    }
}
