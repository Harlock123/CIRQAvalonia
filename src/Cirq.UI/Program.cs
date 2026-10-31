using Avalonia;
using Cirq.Mcp;
using Cirq.UI.Agents;

namespace Cirq.UI;

internal static class Program
{
    /// <summary>
    /// Starts the editor. With <c>--mcp</c> it opens no window at all: it serves the Model Context Protocol on stdio for
    /// an AI agent instead (see <see cref="McpCommandLine"/>), on a circuit of its own or, with <c>--attach</c>, on the
    /// circuit open in a running editor.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (McpCommandLine.Wanted(args))
            return McpCommandLine.RunAsync(args, () => new EditorLibrary(), Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error)
                .GetAwaiter().GetResult();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
