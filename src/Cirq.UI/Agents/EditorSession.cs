using Cirq.Components.Serialization;
using Cirq.Core.Topology;
using Cirq.Mcp;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Agents;

/// <summary>
/// An editor window's document as an agent works on it: its circuit, its file, its undo history (an agent's change is
/// one step among the person's own, under Ctrl+Z), its simulation's transport and its scope. Used on the UI thread
/// only.
/// </summary>
public sealed class EditorSession(MainWindowViewModel editor) : ICircuitSession
{
    public MainWindowViewModel Editor => editor;

    public Circuit Circuit => editor.Circuit;

    public string? FilePath => editor.CurrentFilePath;

    public bool IsModified => editor.IsModified;

    public IDisposable Step(string label)
    {
        var before = CircuitSerializer.ToComparableJson(editor.Circuit);
        var gesture = editor.History.Gesture(editor.Circuit, label);
        return new End(() =>
        {
            gesture.Dispose();
            editor.AfterAgentEdit(CircuitSerializer.ToComparableJson(editor.Circuit) != before);
        });
    }

    public void Restore(string json) => editor.RestoreSnapshot(json);

    public void Replace(Circuit circuit, string? path) =>
        editor.AdoptCircuit(circuit, path, path is null ? $"AI: {circuit.Title}" : $"AI: opened {Path.GetFileName(path)}");

    public void Saved(string path) => editor.MarkSaved(path);

    public string? Undo() => editor.UndoStep();

    public string? Redo() => editor.RedoStep();

    public string Transport(TransportAction action)
    {
        switch (action)
        {
            case TransportAction.Run: editor.Simulation.Play(); break;
            case TransportAction.Pause: editor.Simulation.Pause(); break;
            default: editor.Simulation.ResetSimulation(); break;
        }
        return editor.Simulation.Status;
    }

    public (double Timebase, double VoltsPerDivision)? Scope(double? timebase, double? voltsPerDivision)
    {
        if (timebase is { } t) editor.Scope.TimebasePerDivision = t;
        if (voltsPerDivision is { } v) editor.Scope.VoltsPerDivision = v;
        return (editor.Scope.TimebasePerDivision, editor.Scope.VoltsPerDivision);
    }

    public void ShowRun(Action write)
    {
        // The editor's own run writes to the same probes from its worker; it stops first.
        editor.Simulation.Pause();
        write();
        editor.Redraw();
    }

    private sealed class End(Action end) : IDisposable
    {
        private Action? _end = end;

        public void Dispose()
        {
            _end?.Invoke();
            _end = null;
        }
    }
}
