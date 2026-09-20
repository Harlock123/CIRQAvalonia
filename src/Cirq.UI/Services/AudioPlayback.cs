using System.ComponentModel;
using System.Diagnostics;

namespace Cirq.UI.Services;

/// <summary>
/// Hands a recorded file to whatever the desktop plays audio with.
/// <para>
/// This is the whole of the application's relationship with the sound hardware, and deliberately
/// so. Playing audio from inside the process would mean a native audio library shipped for every
/// runtime identifier, and — the harder problem — a simulation that produced samples at a fixed
/// rate in real time, which a variable-step transient solver cannot do. Writing a file and asking
/// the desktop to open it costs nothing, works everywhere, and gives a file that can be kept,
/// compared against another run, or looked at in something built for audio.
/// </para>
/// </summary>
public static class AudioPlayback
{
    /// <summary>
    /// Opens the file in the system's default handler. Returns what to tell the person, which is
    /// either where it went or why it did not go anywhere.
    /// </summary>
    public static string Play(string path)
    {
        if (!File.Exists(path)) return $"There is no file at {path} yet — run the circuit first.";

        try
        {
            // Each desktop has exactly one of these and none of them share a name.
            var (command, arguments) = OperatingSystem.IsWindows()
                ? (path, string.Empty)
                : OperatingSystem.IsMacOS()
                    ? ("afplay", Quote(path))
                    : ("xdg-open", Quote(path));

            Process.Start(new ProcessStartInfo(command, arguments)
            {
                // Windows has no opener command: the shell resolves the association itself, which
                // is what UseShellExecute asks it to do.
                UseShellExecute = OperatingSystem.IsWindows(),
            });

            return $"Playing {Path.GetFileName(path)}";
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException
                                      or PlatformNotSupportedException or IOException)
        {
            // No handler installed is an ordinary state on a bare Linux box, not a crash.
            return $"Could not play {Path.GetFileName(path)}: {e.Message}. The file is there to open.";
        }
    }

    private static string Quote(string path) => $"\"{path}\"";
}
