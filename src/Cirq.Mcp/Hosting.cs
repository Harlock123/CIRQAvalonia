using System.Diagnostics;
using Cirq.Components.Serialization;
using Cirq.Core.Topology;

namespace Cirq.Mcp;

/// <summary>A part the palette offers: its group, its name there, what it is, and a way to make one.</summary>
public sealed record PartKind(string Category, string Name, string Description, Func<CircuitComponent> Create);

/// <summary>A ready-made circuit from the Examples menu.</summary>
public sealed record ExampleKind(string Category, string Name, string Description);

/// <summary>An example built: the circuit (with its probes), and the scope settings it is shown with.</summary>
public sealed record LoadedExample(Circuit Circuit, double Timebase, double VoltsPerDivision);

/// <summary>
/// What the server needs from the editor without depending on it: the palette, the examples and a picture of the
/// schematic. The editor (<c>Cirq.UI</c>) supplies the real ones; tests may supply their own.
/// </summary>
public interface ICircuitLibrary
{
    /// <summary>Every part the palette offers, in its order.</summary>
    IReadOnlyList<PartKind> Parts { get; }

    /// <summary>Every example circuit, in the Examples menu's order.</summary>
    IReadOnlyList<ExampleKind> Examples { get; }

    /// <summary>An example built into a circuit of its own (with its probes), ready to adopt.</summary>
    LoadedExample LoadExample(string name);

    /// <summary>The schematic drawn as a PNG, black on white as it prints; one sheet of it, or all of a one-page drawing.</summary>
    byte[] Picture(Circuit circuit, string? sheet, double scale);
}

/// <summary>What an agent may ask the editor's own simulation to do (the transport buttons).</summary>
public enum TransportAction { Run, Pause, Reset }

/// <summary>
/// The circuit an agent works on, and the document around it: its file, its undo history, and (in the app) the
/// editor's own simulation and scope. Used on the host's thread only (<see cref="ICircuitHost.RunAsync{T}"/>).
/// </summary>
public interface ICircuitSession
{
    Circuit Circuit { get; }

    /// <summary>The file the circuit was opened from or saved to, or null.</summary>
    string? FilePath { get; }

    /// <summary>Changed since it was opened or saved.</summary>
    bool IsModified { get; }

    /// <summary>
    /// Begins one undo step called <paramref name="label"/>: changes made before the scope ends are recorded as one,
    /// and the editor rebuilds its simulation and redraws when it ends.
    /// </summary>
    IDisposable Step(string label);

    /// <summary>Puts the circuit back as a snapshot has it (a failed step rolls back this way). Not an undo step.</summary>
    void Restore(string json);

    /// <summary>Adopts a new document (New, Open, an example): a fresh history, and not modified.</summary>
    void Replace(Circuit circuit, string? path);

    /// <summary>The circuit was written to <paramref name="path"/>, which becomes its file.</summary>
    void Saved(string path);

    /// <summary>Takes back the last step, returning its name, or null when there's nothing to undo.</summary>
    string? Undo();

    /// <summary>Puts back the step last undone, returning its name, or null.</summary>
    string? Redo();

    /// <summary>
    /// Runs, pauses or resets the editor's own simulation (the transport), returning its status line. The headless
    /// server has no transport and says so.
    /// </summary>
    string Transport(TransportAction action);

    /// <summary>Sets the scope's time and volts per division (null leaves one as it is); returns what they are.</summary>
    (double Timebase, double VoltsPerDivision)? Scope(double? timebase, double? voltsPerDivision);

    /// <summary>
    /// Writes a run's samples into the probes (<paramref name="write"/>), with the editor's own simulation paused so it
    /// isn't writing to them too, and has the scope show them.
    /// </summary>
    void ShowRun(Action write);
}

/// <summary>
/// Where the MCP tools' work runs. The handlers (<see cref="CircuitTools"/>) only touch the circuit through
/// <see cref="RunAsync{T}"/>, so the same handlers serve the headless server (<see cref="HeadlessHost"/>, a circuit of
/// its own) and the live editor (<see cref="LiveHost"/>, the open window's circuit, on the UI thread).
/// </summary>
public interface ICircuitHost
{
    /// <summary>The folder relative paths start from; open and save stay inside it.</summary>
    string Root { get; }

    /// <summary>The palette, the examples and the schematic picture.</summary>
    ICircuitLibrary Library { get; }

    /// <summary>
    /// Runs <paramref name="work"/> on the thread that owns the circuit, one call at a time, and returns what it returns
    /// (or throws what it throws). <paramref name="action"/> names it for the editor's status bar.
    /// </summary>
    Task<T> RunAsync<T>(string action, Func<ICircuitSession, T> work, CancellationToken cancel = default);
}

/// <summary>
/// The headless host: a circuit of its own with no window, as <c>CirqAvalonia --mcp</c> serves it. Calls run on
/// whichever thread the request came in on, one at a time.
/// </summary>
public sealed class HeadlessHost(string root, ICircuitLibrary library) : ICircuitHost
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Root { get; } = Path.GetFullPath(root);

    public ICircuitLibrary Library { get; } = library;

    public HeadlessSession Session { get; } = new();

    public async Task<T> RunAsync<T>(string action, Func<ICircuitSession, T> work, CancellationToken cancel = default)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            return work(Session);
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// A circuit with no editor around it: an undo history of whole-document snapshots, as the editor keeps, and no
/// transport or scope.
/// </summary>
public sealed class HeadlessSession : ICircuitSession
{
    private readonly List<(string Json, string Label)> _undo = [];
    private readonly List<(string Json, string Label)> _redo = [];
    private int _depth;

    public Circuit Circuit { get; } = new() { Title = "Untitled circuit" };

    public string? FilePath { get; private set; }

    public bool IsModified { get; private set; }

    /// <summary>How many steps there are to undo (for tests).</summary>
    public int UndoCount => _undo.Count;

    public IDisposable Step(string label)
    {
        var before = _depth == 0 ? (Json: CircuitSerializer.ToJson(Circuit), Comparable: CircuitSerializer.ToComparableJson(Circuit)) : default;
        _depth++;
        return new StepScope(() =>
        {
            if (--_depth > 0) return;
            if (CircuitSerializer.ToComparableJson(Circuit) == before.Comparable) return;
            _undo.Add((before.Json, label));
            if (_undo.Count > 100) _undo.RemoveAt(0);
            _redo.Clear();
            IsModified = true;
        });
    }

    public void Restore(string json) => Adopt(CircuitSerializer.FromJson(json).Circuit);

    public void Replace(Circuit circuit, string? path)
    {
        Adopt(circuit);
        FilePath = path;
        IsModified = false;
        _undo.Clear();
        _redo.Clear();
    }

    public void Saved(string path)
    {
        FilePath = path;
        IsModified = false;
    }

    public string? Undo() => Move(_undo, _redo);

    public string? Redo() => Move(_redo, _undo);

    private string? Move(List<(string Json, string Label)> from, List<(string Json, string Label)> to)
    {
        if (from.Count == 0) return null;
        var (json, label) = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add((CircuitSerializer.ToJson(Circuit), label));
        Restore(json);
        IsModified = true;
        return label;
    }

    public string Transport(TransportAction action) =>
        throw new NotSupportedException("The headless server has no running simulation to start or stop. Use run_transient, which runs the circuit and reports what the probes saw; the editor's Run button is only there when an agent is attached to the open app.");

    public (double Timebase, double VoltsPerDivision)? Scope(double? timebase, double? voltsPerDivision) => null;

    public void ShowRun(Action write) => write();

    /// <summary>Moves a circuit's contents into this one, as the editor does on open.</summary>
    private void Adopt(Circuit loaded) => Contents.Copy(loaded, Circuit);

    private sealed class StepScope(Action end) : IDisposable
    {
        private Action? _end = end;

        public void Dispose()
        {
            _end?.Invoke();
            _end = null;
        }
    }
}

/// <summary>Moving a whole document's contents from one <see cref="Circuit"/> into another.</summary>
public static class Contents
{
    /// <summary>
    /// Empties <paramref name="into"/> and moves everything <paramref name="from"/> holds into it: parts, wires,
    /// probes, sheets, parameters, requirements, the baseline and the conditions.
    /// </summary>
    public static void Copy(Circuit from, Circuit into)
    {
        into.Clear();
        into.Title = from.Title;
        foreach (var component in from.Components.ToList()) into.Components.Add(component);
        foreach (var wire in from.Wires.ToList()) into.Wires.Add(wire);
        foreach (var probe in from.Probes.ToList()) into.Probes.Add(probe);
        foreach (var sheet in from.Sheets) into.Sheets.Add(sheet);
        foreach (var parameter in from.Parameters) into.Parameters.Add(parameter);
        foreach (var spec in from.Specs) into.Specs.Add(spec);
        into.Baseline = from.Baseline;
        into.AmbientTemperatureCelsius = from.AmbientTemperatureCelsius;
        into.AdaptiveTimeStep = from.AdaptiveTimeStep;
        into.Revision = from.Revision;
        into.Author = from.Author;
    }
}

/// <summary>Runs work on a UI thread (the app's Avalonia dispatcher, or a test's stand-in).</summary>
public interface IUiThread
{
    Task<T> InvokeAsync<T>(Func<T> work);
}

/// <summary>The running editor as the live host sees it. Every member is used on the UI thread only.</summary>
public interface ILiveCircuit
{
    /// <summary>The window's document (an outside agent gets the window used last).</summary>
    ICircuitSession Session { get; }

    /// <summary>
    /// Why the editor can't take a call just now ("a dialog box is open", "the person is dragging a part"), or null
    /// when it can. The person's work in progress isn't interrupted: the call waits for it.
    /// </summary>
    string? Busy { get; }

    /// <summary>Tells the person what an agent just did (the status bar).</summary>
    void Report(string text);
}

/// <summary>
/// The live host: an agent's calls run against the circuit open in the editor, on its UI thread, so the canvas redraws
/// and Ctrl+Z undoes them like the person's own edits. While the person is in the middle of something (a dialog box
/// open, a drag under way) a call waits for up to <see cref="BusyWait"/>, then fails saying why.
/// </summary>
public sealed class LiveHost(IUiThread ui, ILiveCircuit editor, ICircuitLibrary library, string root) : ICircuitHost
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Root { get; } = Path.GetFullPath(root);

    public ICircuitLibrary Library { get; } = library;

    /// <summary>How long a call waits for the person to finish what they're doing.</summary>
    public TimeSpan BusyWait { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How often a waiting call looks again.</summary>
    public TimeSpan BusyPoll { get; init; } = TimeSpan.FromMilliseconds(200);

    public async Task<T> RunAsync<T>(string action, Func<ICircuitSession, T> work, CancellationToken cancel = default)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                var (ran, result, busy) = await ui.InvokeAsync(() =>
                {
                    if (editor.Busy is { } why) return (false, default(T), why);
                    try
                    {
                        var r = work(editor.Session);
                        editor.Report($"AI agent: {action}.");
                        return (true, r, (string?)null);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        editor.Report($"AI agent: {action} failed: {e.Message}");
                        throw;
                    }
                }).ConfigureAwait(false);
                if (ran) return result!;
                if (clock.Elapsed >= BusyWait)
                    throw new CircuitBusyException($"CirqAvalonia is busy: {busy}. The call waited {BusyWait.TotalSeconds:0} seconds; ask the person to finish, then try again.");
                await Task.Delay(BusyPoll, cancel).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>The editor stayed busy with the person's own work for too long to take a call.</summary>
public sealed class CircuitBusyException(string message) : Exception(message);
