using System.Collections.Concurrent;
using static Cirq.Mcp.Tests.Calls;

namespace Cirq.Mcp.Tests;

/// <summary>A stand-in for the editor's UI thread: one thread of its own running the work it's given, in order.</summary>
internal sealed class TestUiThread : IUiThread, IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;

    public TestUiThread()
    {
        _thread = new Thread(() => { foreach (var job in _queue.GetConsumingEnumerable()) job(); }) { IsBackground = true, Name = "test UI" };
        _thread.Start();
    }

    public int ThreadId => _thread.ManagedThreadId;

    public Task<T> InvokeAsync<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception e) { done.SetException(e); }
        });
        return done.Task;
    }

    public void Dispose() => _queue.CompleteAdding();
}

/// <summary>A stand-in for the running editor: a circuit, whether the person is busy, and the status bar.</summary>
internal sealed class TestEditor(TestUiThread ui) : ILiveCircuit
{
    private readonly HeadlessSession _session = new();
    public volatile string? BusyWith;
    public readonly ConcurrentQueue<string> Reports = new();
    public int WrongThread;

    public ICircuitSession Session
    {
        get
        {
            if (Environment.CurrentManagedThreadId != ui.ThreadId) Interlocked.Increment(ref WrongThread);
            return _session;
        }
    }

    /// <summary>The circuit as the test sees it, read once the calls are done.</summary>
    public HeadlessSession Unchecked => _session;

    public string? Busy => BusyWith;

    public void Report(string text) => Reports.Enqueue(text);
}

/// <summary>
/// The live mode in one process: a <see cref="LiveListener"/> on a pipe serving a circuit on a test UI thread, and an
/// agent reaching it through the <see cref="LiveBridge"/>, as <c>CirqAvalonia --mcp --attach</c> would.
/// </summary>
public sealed class LiveTests : IDisposable
{
    private readonly TestUiThread _ui = new();
    private readonly TempFolder _folder = new();
    private readonly string _pipe = "cirq-test-" + Guid.NewGuid().ToString("N")[..12];

    private LiveListener Listen(TestEditor editor, TimeSpan? busyWait = null) =>
        new(_pipe, root => new LiveHost(_ui, editor, Bench.Library, root) { BusyWait = busyWait ?? TimeSpan.FromSeconds(5), BusyPoll = TimeSpan.FromMilliseconds(20) });

    [Fact]
    public async Task An_attached_agent_works_on_the_editors_circuit_on_its_UI_thread()
    {
        var editor = new TestEditor(_ui);
        await using var listener = Listen(editor);
        var connections = new ConcurrentQueue<int>();
        listener.ConnectionsChanged += connections.Enqueue;
        listener.Start();

        await using var bridge = new LiveBridge(_pipe, _folder.Path);
        await using var s = await InProcess.Start(bridge.Options());
        Assert.Contains("live app", s.Client.ServerInstructions);
        Assert.Equal(EndToEndTests.ToolNames, (await s.Client.ListToolsAsync(cancellationToken: CancellationToken.None)).Select(t => t.Name));

        await s.Client.Json("add_parts", new { parts = new object[] { new { kind = "Resistor" }, new { kind = "Ground", y = 100 } } });
        await s.Client.Json("connect", new { wires = new[] { new[] { "R1.B", "GND1.GND" } } });
        Assert.Contains("no part R7", await s.Client.Fails("get_part", new { name = "R7" }));

        Assert.Equal(2, editor.Unchecked.Circuit.Components.Count);
        Assert.Equal(2, editor.Unchecked.UndoCount);
        Assert.Equal(0, editor.WrongThread);
        Assert.Contains(editor.Reports, r => r.StartsWith("AI agent: Add 2 parts", StringComparison.Ordinal));
        Assert.Contains(editor.Reports, r => r.Contains("failed: There's no part R7"));
        Assert.Contains(1, connections);

        // Saving goes to the bridge's working folder.
        await s.Client.Json("save_circuit", new { path = "live.cirq" });
        Assert.True(File.Exists(_folder.File("live.cirq")));
    }

    [Fact]
    public async Task A_call_waits_for_the_person_then_gives_up_saying_why()
    {
        var editor = new TestEditor(_ui) { BusyWith = "a dialog box is open in CirqAvalonia" };
        await using var listener = Listen(editor, TimeSpan.FromMilliseconds(300));
        listener.Start();
        await using var bridge = new LiveBridge(_pipe, _folder.Path);
        await using var s = await InProcess.Start(bridge.Options());

        var message = await s.Client.Fails("add_parts", new { parts = new object[] { new { kind = "Resistor" } } });
        Assert.Contains("CirqAvalonia is busy: a dialog box is open", message);
        Assert.Empty(editor.Unchecked.Circuit.Components);

        // Once the person is done, the next call goes through.
        editor.BusyWith = null;
        await s.Client.Json("add_parts", new { parts = new object[] { new { kind = "Resistor" } } });
        Assert.Single(editor.Unchecked.Circuit.Components);
    }

    [Fact]
    public async Task With_no_editor_listening_the_bridge_says_how_to_let_agents_in()
    {
        await using var bridge = new LiveBridge(_pipe, _folder.Path) { ConnectTimeout = TimeSpan.FromMilliseconds(200) };
        await using var s = await InProcess.Start(bridge.Options());
        // The tools are listed before there's an editor, so an agent can be set up first.
        Assert.Equal(EndToEndTests.ToolNames.Length, (await s.Client.ListToolsAsync(cancellationToken: CancellationToken.None)).Count);
        Assert.Contains("Allow AI Agents", await s.Client.Fails("get_circuit"));

        // It connects as soon as one is.
        var editor = new TestEditor(_ui);
        await using var listener = Listen(editor);
        listener.Start();
        Assert.Equal("Untitled circuit", (string)(await s.Client.Json("get_circuit"))["title"]!);
    }

    [Fact]
    public async Task Only_one_editor_takes_agents_on_a_pipe()
    {
        await using var first = Listen(new TestEditor(_ui));
        first.Start();
        await using var second = Listen(new TestEditor(_ui));
        Assert.Contains("Another CirqAvalonia", Assert.Throws<IOException>(second.Start).Message);

        // Stopped, the pipe is free again at once.
        first.Stop();
        second.Start();
        Assert.True(LiveListener.Answers(_pipe));
    }

    [Fact]
    public void The_handshake_checks_the_protocol_and_the_working_folder()
    {
        Assert.Null(LiveProtocol.Check(LiveProtocol.Hello(_folder.Path).ToJsonString(), out var root));
        Assert.Equal(_folder.Path, root);
        Assert.Contains("only", LiveProtocol.Check("""{"protocol":"something-else","version":1}""", out _));
        Assert.Contains("version", LiveProtocol.Check("""{"protocol":"cirqavalonia-mcp","version":99}""", out _));
        Assert.Contains("doesn't exist", LiveProtocol.Check(LiveProtocol.Hello("/no/such/folder").ToJsonString(), out _));
        Assert.Contains("JSON", LiveProtocol.Check("hello", out _));
    }

    public void Dispose()
    {
        _ui.Dispose();
        _folder.Dispose();
    }
}
