using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Cirq.Chat.Tests;
using Cirq.Components.Passive;
using Cirq.Mcp;
using Cirq.UI.Controls;
using Cirq.UI.Services;
using Cirq.UI.Tests.Support;
using Cirq.UI.ViewModels;
using Cirq.UI.Views;
using static Cirq.Chat.Tests.FakeClaude;

namespace Cirq.UI.Tests;

/// <summary>
/// AI in the editor window: the AI Chat panel (F12) against a fake Anthropic API, Claude's tool calls running in the
/// window's circuit through the app's own MCP server, and outside agents let in through AI › Allow AI Agents.
/// </summary>
[Collection(WindowCollection.Name)]
public class AgentWindowTests(WindowSession session) : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("cirq-agents-").FullName;
    private readonly List<Window> _windows = [];

    private App App => (App)Avalonia.Application.Current!;

    private (MainWindow Window, MainWindowViewModel Editor) Open()
    {
        // Settings go to a folder of the test's own: the real settings and key file are never touched.
        App.SettingsStore = new JsonSettingsStore(Path.Combine(_folder, "settings.json"));
        // And its autosave too, so no snapshot of the person's own is offered back as the window opens.
        var editor = new MainWindowViewModel { Autosave = new AutosaveStore(Path.Combine(_folder, $"recovery-{_windows.Count}.cirq")) };
        var window = new MainWindow { DataContext = editor, Width = 1500, Height = 900, ChatRoot = _folder };
        window.Show();
        _windows.Add(window);
        Dispatcher.UIThread.RunJobs();
        return (window, editor);
    }

    private static void Press(Window window, Avalonia.Input.Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Runs the UI thread's work until a task (one that needs the UI thread to finish) is done.</summary>
    private static T Finish<T>(Task<T> task)
    {
        Finish((Task)task);
        return task.Result;
    }

    private static void Finish(Task task)
    {
        for (var i = 0; i < 30000 && !task.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        Assert.True(task.IsCompleted, "It didn't finish.");
        task.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    private static List<string> Texts(Control panel) =>
        panel.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0).ToList();

    [Fact]
    public void F12_opens_the_chat_where_Claude_builds_in_the_window_and_Ctrl_Z_undoes_it() => session.Run(() =>
    {
        var api = new FakeClaude()
            .Reply(Say("I'll add a load resistor."), Call("toolu_1", "add_parts", new { parts = new object[] { new { kind = "Resistor", x = 200, y = 0, settings = new { Resistance = "4k7" } } } }))
            .Reply(Call("toolu_2", "render_schematic", new { scale = 1 }))
            .Reply(Say("Done: R2 is 4.7 kΩ."));
        var (window, editor) = Open();
        var parts = editor.Circuit.Components.Count;
        Assert.Null(window.Chat);

        Press(window, Avalonia.Input.Key.F12);
        Assert.True(editor.IsChatOpen);
        var chat = Assert.IsType<ChatPanel>(window.Chat);
        // Beside the properties panel, in the column the window keeps for it.
        Assert.Same(window.FindControl<Border>("ChatHost"), chat.Parent);
        Assert.True(chat.Bounds.Width > 300, $"the chat is {chat.Bounds.Width} wide");
        chat.ClientFactory = _ => api.Client();
        chat.Keys.Override = FakeClaude.Key;

        chat.Input.Text = "add a 4k7 load";
        chat.Send();
        Assert.Equal("", chat.Input.Text);
        Finish(chat.Running);

        // The resistor is in the window's circuit, one undo step among the person's own.
        Assert.Equal(parts + 1, editor.Circuit.Components.Count);
        var added = Assert.IsType<Resistor>(editor.Circuit.Components[^1]);
        Assert.Equal(4700, added.Resistance, 6);
        Assert.Equal("AI: Add Resistor", editor.History.UndoLabel);
        Assert.True(editor.IsModified);
        var texts = Texts(chat);
        Assert.Contains("I'll add a load resistor.", texts);
        Assert.Contains(texts, t => t.StartsWith("▸ add_parts Resistor → added 1"));
        Assert.Contains("Done: R2 is 4.7 kΩ.", texts);
        Assert.Single(chat.GetLogicalDescendants().OfType<Image>());
        Assert.Equal("Claude has finished.", editor.StatusMessage);

        // The person's Ctrl+Z takes Claude's step back.
        window.FindControl<CircuitCanvas>("Canvas")!.Focus();
        Press(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
        Assert.Equal(parts, editor.Circuit.Components.Count);

        // F12 hides it again.
        Press(window, Avalonia.Input.Key.F12);
        Assert.False(editor.IsChatOpen);
        Assert.False(window.FindControl<Border>("ChatHost")!.IsVisible);
    });

    [Fact]
    public void Auto_continue_and_the_model_are_remembered() => session.Run(() =>
    {
        var (window, editor) = Open();
        editor.IsChatOpen = true;
        var chat = window.Chat!;
        Assert.True(chat.AutoContinue.IsChecked);
        Assert.Equal(ChatPanel.AutoContinueTimes, chat.Session.AutoContinues);
        chat.AutoContinue.IsChecked = false;
        Assert.Equal(0, chat.Session.AutoContinues);
        Assert.False(App.Settings.ChatAutoContinue);
        Assert.False(new JsonSettingsStore(Path.Combine(_folder, "settings.json")).Load().ChatAutoContinue);
        chat.AutoContinue.IsChecked = true;
        Assert.True(App.Settings.ChatAutoContinue);
    });

    [Fact]
    public void An_agents_change_that_fails_leaves_the_window_as_it_was_and_a_run_fills_the_scope() => session.Run(() =>
    {
        var (_, editor) = Open();
        var tools = new CircuitTools(App.Agents.ChatHost(editor, _folder));
        var steps = editor.History.CanUndo;

        // The opening circuit is the RC low-pass example; a good setting and a bad one: neither sticks.
        var failed = tools.SetPart("R1", new() { ["Resistance"] = System.Text.Json.JsonSerializer.SerializeToElement("1k"), ["Tolerance"] = System.Text.Json.JsonSerializer.SerializeToElement("lots") });
        for (var i = 0; i < 5000 && !failed.IsCompleted; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        Assert.IsType<ModelContextProtocol.McpException>(failed.Exception?.InnerException);
        Assert.Equal(10e3, editor.Circuit.Components.OfType<Resistor>().Single().Resistance);
        Assert.Equal(steps, editor.History.CanUndo);

        // A run's samples go into the window's probes for the scope, with its own simulation paused.
        editor.Simulation.Play();
        Finish(tools.RunTransient(System.Text.Json.JsonSerializer.SerializeToElement("2m"), plot: false));
        Assert.False(editor.Simulation.IsRunning);
        Assert.All(editor.Scope.Probes, p => Assert.InRange(p.HistoryBuffer[^1].Time, 1.9e-3, 2.1e-3));
        Assert.StartsWith("Claude: ran the circuit", editor.StatusMessage);
    });

    [Fact]
    public void Allowing_agents_opens_the_door_remembers_it_and_ticks_every_window() => session.Run(() =>
    {
        var (_, first) = Open();
        var (second, other) = Open();
        App.AgentPipe = "cirq-ui-test-" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            first.AllowAgents = true;
            Assert.True(App.Agents.Running);
            Assert.True(other.AllowAgents);
            Assert.True(new JsonSettingsStore(Path.Combine(_folder, "settings.json")).Load().AllowAgents);
            Assert.StartsWith("AI agents may connect", first.StatusMessage);

            // An outside agent works in the window used last.
            App.Agents.Activated(other);
            other.Circuit.Title = "The other one";
            var bridge = new LiveBridge(App.AgentPipe, _folder);
            var call = bridge.CallAsync(new ModelContextProtocol.Protocol.CallToolRequestParams { Name = "get_circuit" }, CancellationToken.None).AsTask();
            var result = Finish(call);
            Assert.NotEqual(true, result.IsError);
            Assert.Contains("The other one", ((ModelContextProtocol.Protocol.TextContentBlock)result.Content[0]).Text);
            Finish(bridge.DisposeAsync().AsTask());

            other.AllowAgents = false;
            Assert.False(App.Agents.Running);
            Assert.False(first.AllowAgents);
            Assert.False(App.Settings.AllowAgents);
        }
        finally
        {
            App.SetAllowAgents(false);
            App.AgentPipe = LiveProtocol.PipeName;
        }
    });

    public void Dispose()
    {
        session.Run(() =>
        {
            foreach (var window in _windows) window.Close();
            Dispatcher.UIThread.RunJobs();

            // A whole editor drew its canvas here, caching the theme's brushes, which belong to this thread. Tests
            // that export from another thread would trip over them; they resolve afresh instead.
            Cirq.UI.Rendering.CanvasTheme.Invalidate();
        });
        try { Directory.Delete(_folder, true); }
        catch (IOException) { }
    }
}
