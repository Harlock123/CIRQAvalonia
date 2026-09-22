using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cirq.UI.Services;

/// <summary>What happened when a document was handed to the platform.</summary>
/// <param name="Succeeded">Whether anything took it.</param>
/// <param name="Message">What to tell the user, either way.</param>
/// <param name="Path">The file that was produced, which exists whether or not it printed.</param>
public sealed record PrintOutcome(bool Succeeded, string Message, string Path);

/// <summary>
/// Hands a printable document to whatever the operating system uses for printing.
/// <para>
/// <b>Avalonia has no printing API</b>, on any platform, as of 12.1. So printing here means
/// producing a proper page-sized PDF — which is most of the work, and the part that was actually
/// missing — and then asking the system to take it.
/// </para>
/// <para>
/// Where a real print spooler exists the document goes straight to it. Where one does not, it
/// opens in whatever handles PDFs, and the print dialog is one keystroke away in a viewer the
/// person already knows. Both are said plainly in the result rather than the second being
/// disguised as the first: a menu item called Print that silently opened a viewer would be a
/// small lie told every time it was used.
/// </para>
/// </summary>
public static class PrintService
{
    /// <summary>
    /// Sends a file to the platform. Never throws: a printer that is not there is a message, not
    /// a crash, and the document has already been written either way.
    /// </summary>
    public static PrintOutcome Send(string path, bool spoolDirectly = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
            return new PrintOutcome(false, "The document was not written.", path);

        try
        {
            if (spoolDirectly && SpoolCommand() is { } spool && Run(spool.File, [.. spool.Arguments, path]))
            {
                return new PrintOutcome(
                    true, $"Sent to the printer. The document is also at {path}", path);
            }

            if (Open(path))
            {
                return new PrintOutcome(
                    true,
                    "Opened in your PDF viewer — print it from there. " +
                    $"The document is at {path}",
                    path);
            }

            return new PrintOutcome(
                false,
                $"Nothing on this system offered to open a PDF. The document is at {path}",
                path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException
                                      or InvalidOperationException or UnauthorizedAccessException)
        {
            return new PrintOutcome(false, $"Could not print: {ex.Message}. The document is at {path}", path);
        }
    }

    /// <summary>
    /// True when this system has a spooler that can be handed a file directly, which decides
    /// whether the dialog offers to print or only to open.
    /// </summary>
    public static bool CanSpoolDirectly() => SpoolCommand() is not null;

    /// <summary>
    /// The command that takes a file to a printer, or null when there is none.
    /// <para>
    /// <c>lp</c> is CUPS and is on macOS always and on most Linux installs. Windows has no
    /// equivalent that can be relied on — the <c>print</c> shell verb depends on whatever is
    /// registered for PDFs and quietly does nothing when that handler does not implement it — so
    /// there it always opens the viewer, which at least is honest about what it did.
    /// </para>
    /// </summary>
    private static (string File, string[] Arguments)? SpoolCommand()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;

        foreach (var command in new[] { "lp", "lpr" })
            if (Which(command) is { } found)
                return (found, []);

        return null;
    }

    /// <summary>Opens a file with whatever the desktop has registered for it.</summary>
    private static bool Open(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // UseShellExecute is what makes this the file's registered handler rather than an
            // attempt to execute the PDF.
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return process is not null;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return Run("open", [path]);

        foreach (var command in new[] { "xdg-open", "gio" })
        {
            if (Which(command) is not { } found) continue;

            var arguments = command == "gio" ? new[] { "open", path } : [path];

            if (Run(found, arguments)) return true;
        }

        return false;
    }

    private static bool Run(string file, string[] arguments)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false };

        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = Process.Start(info);

        return process is not null;
    }

    /// <summary>Where a command lives, or null when it is not on the path.</summary>
    private static string? Which(string command)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];

        foreach (var directory in paths)
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;

            var candidate = Path.Combine(directory, command);

            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
