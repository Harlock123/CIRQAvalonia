using System.Text.Json;
using Cirq.Components.Serialization;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>
/// A fault-finding lesson in a file of its own (.cirqlesson), for a teacher to hand out: the circuit, the fault hidden in
/// it and the symptom to start from. The fault travels as the pin it's at, not inside the circuit, so the circuit part
/// of the file opens clean.
/// </summary>
public sealed record FaultLessonFile(int Format, string Name, string Symptom, string Kind, string Pin, string? Second, string Circuit)
{
    public const string Extension = ".cirqlesson";
}

public sealed partial class MainWindowViewModel
{
    private static readonly JsonSerializerOptions LessonJson = new() { WriteIndented = true };

    /// <summary>Writes the circuit, its hidden fault and <paramref name="symptom"/> as a lesson, returning what the status bar says.</summary>
    public string SaveFaultLesson(string path, string name, string symptom)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (Circuit.Faults.Count == 0) throw new InvalidOperationException("Hide a fault first (Simulate › Fault Finding › Hide a Fault): that's what a lesson is.");
        if (string.IsNullOrWhiteSpace(symptom)) throw new InvalidOperationException("A lesson needs a symptom to start from: what the person will see go wrong.");
        var fault = Circuit.Faults[0];
        string Pin(Terminal t) => $"{t.Owner!.Name}.{t.Name}";
        var file = new FaultLessonFile(1, string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name.Trim(), symptom.Trim(),
            fault.Kind.ToString(), Pin(fault.First), fault.Second is { } second ? Pin(second) : null, CircuitSerializer.ToJson(Circuit));
        File.WriteAllText(path, JsonSerializer.Serialize(file, LessonJson));
        return $"Lesson \"{file.Name}\" saved to {Path.GetFileName(path)}: {fault.Describe()} The person opening it starts from: {file.Symptom}";
    }

    /// <summary>Opens a lesson file: its circuit, its fault hidden, its symptom in the status bar and the clock started.</summary>
    public void OpenFaultLesson(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        FaultLessonFile file;
        try
        {
            file = JsonSerializer.Deserialize<FaultLessonFile>(File.ReadAllText(path)) ?? throw new InvalidDataException("The file is empty.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} isn't a lesson file: {e.Message}", e);
        }

        if (file.Format != 1) throw new InvalidDataException($"{Path.GetFileName(path)} is a lesson from a newer version (format {file.Format}).");
        if (!Enum.TryParse<FaultKind>(file.Kind, out var kind)) throw new InvalidDataException($"The lesson's fault is a \"{file.Kind}\", which this version doesn't know.");
        var loaded = CircuitSerializer.FromJson(file.Circuit);
        Simulation.Pause();
        ReplaceCircuitWith(loaded.Circuit);
        CurrentFilePath = null;
        IsModified = false;
        History.Reset(Circuit);
        Simulation.InvalidateTopology();
        RequestZoomToFit?.Invoke(this, EventArgs.Empty);
        BeginLesson(new FaultLesson(file.Name, "", file.Symptom, kind, file.Pin, file.Second));
    }

    /// <summary>Writes the circuit as a page to share (see <see cref="Services.SharePage"/>), returning what the status bar says.</summary>
    public string ExportSharePage(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (Circuit.Components.Count == 0) throw new InvalidOperationException("The circuit is empty: there's nothing to share yet.");
        var title = Circuit.Title.Length > 0 ? Circuit.Title : Path.GetFileNameWithoutExtension(path);
        Services.SharePage.Write(path, title, Circuit);
        return $"Share page written to {Path.GetFileName(path)}: the schematic, the board, the parts, the steps and the circuit file in one page, to attach or put on a site";
    }

    [RelayCommand]
    private async Task ExportSharePageAsync()
    {
        if (FileDialogs is null) return;
        var title = Circuit.Title.Length > 0 ? Circuit.Title : "circuit";
        var path = await FileDialogs.PickSavePathAsync(title + ".html");
        if (path is null) return;
        if (!path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) path = Path.ChangeExtension(path, ".html");
        try
        {
            StatusMessage = ExportSharePage(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await FileDialogs.ReportAsync("Share page", $"Could not write the share page: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SaveFaultLessonAsync()
    {
        if (FileDialogs is null) return;
        if (Circuit.Faults.Count == 0)
        {
            StatusMessage = "Hide a fault first (Simulate › Fault Finding › Hide a Fault, or Claude's fault tool at a pin you choose): that's what a lesson is.";
            return;
        }

        var symptom = await FileDialogs.PromptAsync("Save as a lesson", "What will the person see go wrong? This is where the lesson starts from, so say what's wrong, not why.",
            "It should … It doesn't.");
        if (symptom is null) return;
        var title = Circuit.Title.Length > 0 ? Circuit.Title : "lesson";
        var path = await FileDialogs.PickSavePathAsync(title + FaultLessonFile.Extension);
        if (path is null) return;
        if (!path.EndsWith(FaultLessonFile.Extension, StringComparison.OrdinalIgnoreCase)) path += FaultLessonFile.Extension;
        try
        {
            StatusMessage = SaveFaultLesson(path, title, symptom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await FileDialogs.ReportAsync("Save as a lesson", ex.Message);
        }
    }

    [RelayCommand]
    private async Task OpenFaultLessonAsync()
    {
        if (FileDialogs is null) return;
        if (!await MayDiscardAsync()) return;
        var path = await FileDialogs.PickOpenPathAsync();
        if (path is null) return;
        try
        {
            OpenFaultLesson(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or CircuitFormatException or ArgumentException)
        {
            await FileDialogs.ReportAsync("Open a lesson", ex.Message);
        }
    }
}
