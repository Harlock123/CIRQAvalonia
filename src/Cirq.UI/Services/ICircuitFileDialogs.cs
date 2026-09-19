namespace Cirq.UI.Services;

/// <summary>
/// The file-picking and confirmation surface the view model needs, kept behind an interface so the
/// open/save logic can be tested without a window on screen.
/// </summary>
public interface ICircuitFileDialogs
{
    /// <summary>Asks for a circuit to open; null when the user cancels.</summary>
    Task<string?> PickOpenPathAsync();

    /// <summary>Asks where to save; null when the user cancels.</summary>
    Task<string?> PickSavePathAsync(string suggestedFileName);

    /// <summary>Asks whether to discard unsaved work. False cancels the operation.</summary>
    Task<bool> ConfirmDiscardChangesAsync(string circuitTitle);

    /// <summary>Reports a problem the user needs to know about, such as an unreadable file.</summary>
    Task ReportAsync(string title, string message);
}
