using Avalonia.Threading;
using Cirq.Mcp;
using Cirq.UI.ViewModels;
using Cirq.UI.Views;

namespace Cirq.UI.Agents;

/// <summary>
/// The application's door for AI agents, and the live host they and the AI Chat work through.
/// <para>
/// While agents are allowed (AI › Allow AI Agents), a <see cref="LiveListener"/> takes connections from
/// <c>CirqAvalonia --mcp --attach</c> bridges, and every call runs on the UI thread against the window used last, so
/// the canvas redraws as the agent works and Ctrl+Z undoes its steps. Each window's AI Chat gets a host of its own
/// (<see cref="ChatHost"/>) on that window, whether or not outside agents are allowed. Calls wait while a dialog is
/// open or the person is dragging on the canvas.
/// </para>
/// </summary>
public sealed class LiveAgents : ILiveCircuit, IUiThread, IAsyncDisposable
{
    private LiveListener? _listener;
    private MainWindowViewModel? _active;
    private readonly Dictionary<MainWindowViewModel, Avalonia.Controls.Window> _windows = [];

    /// <summary>The palette, examples and schematic picture the tools use.</summary>
    public EditorLibrary Library { get; } = new();

    public bool Running => _listener != null;

    /// <summary>How long an agent's call waits for the person to finish with a dialog or a drag.</summary>
    public TimeSpan BusyWait { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Raised (on the UI thread) when an outside agent connects or goes, with how many are connected.</summary>
    public event Action<int>? ConnectionsChanged;

    /// <summary>The window an outside agent works in: the one used last.</summary>
    public MainWindowViewModel? Active => _active;

    /// <summary>A window opened: the window that shows an editor, so its dialogs can be told apart from others'.</summary>
    public void Opened(MainWindowViewModel editor, Avalonia.Controls.Window window) => _windows[editor] = window;

    /// <summary>Every open editor window's document.</summary>
    public IEnumerable<MainWindowViewModel> Editors => _windows.Keys;

    /// <summary>A window came to the front.</summary>
    public void Activated(MainWindowViewModel window) => _active = window;

    /// <summary>A window closed; if it was the one agents were using, they have none until another is used.</summary>
    public void Closed(MainWindowViewModel window)
    {
        _windows.Remove(window);
        if (ReferenceEquals(_active, window)) _active = null;
    }

    ICircuitSession ILiveCircuit.Session =>
        new EditorSession(_active ?? throw new InvalidOperationException("No CirqAvalonia window is open to work in. Ask the person to click in one."));

    public string? Busy => BusyIn(_active);

    /// <summary>Why an editor window can't take an agent's call just now, or null.</summary>
    public string? BusyIn(MainWindowViewModel? editor) =>
        editor is null ? null
        : _windows.TryGetValue(editor, out var window) && Dialog.IsOpenOver(window) ? "a dialog box is open in CirqAvalonia"
        : editor.IsInGesture ? "the person is dragging on the canvas"
        : null;

    public void Report(string text)
    {
        if (_active is not null) _active.StatusMessage = text;
    }

    public Task<T> InvokeAsync<T>(Func<T> work) => Dispatcher.UIThread.InvokeAsync(work).GetTask();

    /// <summary>
    /// The host a window's AI Chat runs its in-process MCP server on: the same live host outside agents get (UI thread,
    /// waiting while the person is busy, each call one undo step), bound to that window. Its status lines say Claude is
    /// working.
    /// </summary>
    public LiveHost ChatHost(MainWindowViewModel window, string root) =>
        new(this, new ChatWindow(this, window), Library, root) { BusyWait = BusyWait };

    private sealed class ChatWindow(LiveAgents agents, MainWindowViewModel window) : ILiveCircuit
    {
        private readonly EditorSession _session = new(window);

        public ICircuitSession Session => _session;

        public string? Busy => agents.BusyIn(window);

        public void Report(string text) =>
            window.StatusMessage = text.StartsWith("AI agent: ", StringComparison.Ordinal) ? "Claude: " + text["AI agent: ".Length..] : text;
    }

    /// <summary>Opens the door on a pipe; returns why it couldn't (another CirqAvalonia has it), or null.</summary>
    public string? Start(string pipeName)
    {
        if (_listener != null) return null;
        var listener = new LiveListener(pipeName, root => new LiveHost(this, this, Library, root) { BusyWait = BusyWait });
        listener.ConnectionsChanged += n => Dispatcher.UIThread.Post(() => ConnectionsChanged?.Invoke(n));
        try
        {
            listener.Start();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ = listener.DisposeAsync();
            return e.Message;
        }
        _listener = listener;
        return null;
    }

    public async Task StopAsync()
    {
        var listener = _listener;
        _listener = null;
        if (listener == null) return;
        // The pipe closes now, so the door can be opened again straight away; the sessions wind down after.
        listener.Stop();
        await listener.DisposeAsync();
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
