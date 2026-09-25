namespace Cirq.UI.Services;

/// <summary>
/// The file-picking and confirmation surface the view model needs, kept behind an interface so the
/// open/save logic can be tested without a window on screen.
/// </summary>
public interface ICircuitFileDialogs
{
    /// <summary>Asks for a circuit to open; null when the user cancels.</summary>
    Task<string?> PickOpenPathAsync();

    /// <summary>
    /// A file of numbers to play into a waveform source — a CSV off a scope, a WAV. Null when the
    /// picker was cancelled.
    /// </summary>
    Task<string?> PickWaveformPathAsync();

    /// <summary>Asks where to save; null when the user cancels.</summary>
    Task<string?> PickSavePathAsync(string suggestedFileName);

    /// <summary>Asks whether to discard unsaved work. False cancels the operation.</summary>
    Task<bool> ConfirmDiscardChangesAsync(string circuitTitle);

    /// <summary>Reports a problem the user needs to know about, such as an unreadable file.</summary>
    Task ReportAsync(string title, string message);

    /// <summary>
    /// Asks whether to bring back a circuit that was being worked on when the application last
    /// stopped. False throws it away.
    /// </summary>
    /// <param name="name">What the circuit was called, or "an unsaved circuit".</param>
    /// <param name="age">How long ago the snapshot was taken, in words.</param>
    Task<bool> ConfirmRecoveryAsync(string name, string age);

    /// <summary>
    /// Asks what to export and where to put it; null when the user cancels either question.
    /// </summary>
    /// <param name="suggestedFileName">The circuit's name, without an extension.</param>
    /// <param name="hasTraces">
    /// False when there is nothing on the scope, which greys out the options that would produce
    /// an empty half.
    /// </param>
    Task<ExportRequest?> PickExportAsync(string suggestedFileName, bool hasTraces);
}
