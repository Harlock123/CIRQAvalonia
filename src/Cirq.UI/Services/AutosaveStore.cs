using Cirq.Components.Serialization;
using Cirq.Core.Topology;

namespace Cirq.UI.Services;

/// <summary>A recovered circuit and where it came from.</summary>
/// <param name="Json">The circuit as it was last written.</param>
/// <param name="OriginalPath">The file it belonged to, or null when it had never been saved.</param>
/// <param name="SavedUtc">When the snapshot was taken.</param>
public sealed record Autosave(string Json, string? OriginalPath, DateTimeOffset SavedUtc)
{
    /// <summary>What the recovery prompt calls it.</summary>
    public string Name => OriginalPath is null
        ? "an unsaved circuit"
        : Path.GetFileName(OriginalPath);

    /// <summary>How long ago it was written, for the prompt.</summary>
    public string Age
    {
        get
        {
            var elapsed = DateTimeOffset.UtcNow - SavedUtc;

            return elapsed switch
            {
                { TotalMinutes: < 2 } => "a moment ago",
                { TotalHours: < 1 } => $"{elapsed.TotalMinutes:0} minutes ago",
                { TotalDays: < 1 } => $"{elapsed.TotalHours:0} hours ago",
                _ => SavedUtc.LocalDateTime.ToString("d MMMM, HH:mm"),
            };
        }
    }
}

/// <summary>
/// Keeps a copy of the circuit somewhere safe while it is being worked on.
/// <para>
/// A crash, a power cut or a closed lid should cost the last few minutes rather than the
/// afternoon. The snapshot is written beside the application's other settings rather than next to
/// the file, so a recovered circuit never appears in the folder somebody is working in and never
/// gets picked up as a real file by mistake.
/// </para>
/// <para>
/// It matters more than it used to. A saved circuit now carries the SPICE cards for the imported
/// models it uses, so the file is the only copy of more than it once was — losing it loses parts
/// as well as a drawing.
/// </para>
/// <para>
/// Written the same way a real save is: to a temporary file, then moved into place. An autosave
/// interrupted halfway would otherwise leave a half-written snapshot where the previous good one
/// used to be, which is the one moment it is most needed.
/// </para>
/// </summary>
public sealed class AutosaveStore
{
    private readonly string _path;

    public AutosaveStore(string? path = null)
    {
        _path = path ?? DefaultPath();
    }

    public static string DefaultPath() => PathFor(0);

    /// <summary>
    /// Where the snapshot for one editor window goes.
    /// <para>
    /// One file each, because two windows sharing one would take turns overwriting each other's
    /// work and the survivor would be whichever happened to autosave last — which is the worst
    /// possible behaviour for the one feature whose entire job is not losing anything.
    /// </para>
    /// </summary>
    public static string PathFor(int window) =>
        Path.Combine(
            Folder(),
            window == 0 ? "recovery.cirq" : $"recovery-{window + 1}.cirq");

    private static string Folder() =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.Create),
            "CirqAvalonia");

    /// <summary>
    /// Every snapshot left behind by a previous run, whichever window wrote it, oldest first.
    /// <para>
    /// Scanned rather than remembered: how many windows were open when the lights went out is
    /// exactly the thing nobody wrote down.
    /// </para>
    /// </summary>
    public static IReadOnlyList<AutosaveStore> Abandoned()
    {
        try
        {
            var folder = Folder();

            if (!Directory.Exists(folder)) return [];

            return [.. Directory.EnumerateFiles(folder, "recovery*.cirq")
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => new AutosaveStore(p))
                .Where(s => s.Pending() is not null)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not being able to look is not a reason to refuse to start.
            return [];
        }
    }

    /// <summary>Where the snapshot lives, so it can be found or deleted.</summary>
    public string Location => _path;

    /// <summary>
    /// Writes a snapshot. Quiet about failure: not being able to autosave is not a reason to
    /// interrupt somebody, and the next attempt is a minute away.
    /// </summary>
    public bool Write(Circuit circuit, string? originalPath)
    {
        ArgumentNullException.ThrowIfNull(circuit);

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // The path it came from goes in the file rather than beside it, so there is one thing
            // to write and one thing to delete.
            var document = CircuitSerializer.ToDocument(circuit);
            document.Title = circuit.Title;

            var payload = new AutosavePayload
            {
                OriginalPath = originalPath,
                SavedUtc = DateTimeOffset.UtcNow,
                Circuit = System.Text.Json.JsonSerializer.Serialize(document, Options),
            };

            var temporary = _path + ".tmp";

            File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(payload, Options));
            File.Move(temporary, _path, overwrite: true);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// What is waiting to be recovered, or null when nothing is. A damaged snapshot counts as
    /// nothing rather than as an error: it is the one file where failing loudly helps least.
    /// </summary>
    public Autosave? Pending()
    {
        try
        {
            if (!File.Exists(_path)) return null;

            var payload = System.Text.Json.JsonSerializer.Deserialize<AutosavePayload>(
                File.ReadAllText(_path), Options);

            if (payload?.Circuit is not { Length: > 0 } json) return null;

            return new Autosave(json, payload.OriginalPath, payload.SavedUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Throws the snapshot away. Called when the circuit has been saved for real, and when a
    /// recovery has been offered and answered either way — a snapshot that outlived its question
    /// would be offered again on the next start, which is how a recovery prompt becomes noise.
    /// </summary>
    public void Discard()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to be done, and nothing worth saying.
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class AutosavePayload
    {
        public string? OriginalPath { get; set; }

        public DateTimeOffset SavedUtc { get; set; }

        public string Circuit { get; set; } = string.Empty;
    }
}
