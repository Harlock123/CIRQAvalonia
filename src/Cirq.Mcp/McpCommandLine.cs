using ModelContextProtocol.Server;

namespace Cirq.Mcp;

/// <summary>
/// <c>CirqAvalonia --mcp</c>: the MCP server on stdio, with no window. Headless, it works on a circuit of its own; with
/// <c>--attach</c> it is a bridge to the running editor's open circuit instead (see <see cref="LiveBridge"/>). Nothing
/// but the MCP stream goes to stdout; notes go to stderr.
/// </summary>
public static class McpCommandLine
{
    public const string Usage = """
        CirqAvalonia --mcp [--attach] [--root FOLDER]

          --mcp          Serve the Model Context Protocol on stdin and stdout for an AI agent, with no window.
          --attach       Drive the CirqAvalonia that's open on this desktop (with AI › Allow AI Agents on),
                         instead of a circuit of the server's own.
          --root FOLDER  The working folder that open and save paths start from, and stay inside
                         (default: the current folder).
        """;

    /// <summary>Whether the command line asks for the MCP server.</summary>
    public static bool Wanted(IReadOnlyList<string> args) => args.Contains("--mcp", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Runs the server until the client closes the stream. <paramref name="library"/> gives the palette, the examples
    /// and the schematic picture. Returns the process's exit code.
    /// </summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, Func<ICircuitLibrary> library, Stream input, Stream output, TextWriter log, CancellationToken cancel = default)
    {
        var attach = false;
        var root = Environment.CurrentDirectory;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--mcp": break;
                case "--attach": attach = true; break;
                case "--root" when i + 1 < args.Count: root = args[++i]; break;
                case "--help" or "-h":
                    await log.WriteLineAsync(Usage).ConfigureAwait(false);
                    return 0;
                default:
                    await log.WriteLineAsync($"Unknown option {args[i]}.\n\n{Usage}").ConfigureAwait(false);
                    return 2;
            }
        }
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            await log.WriteLineAsync($"The working folder {root} doesn't exist.").ConfigureAwait(false);
            return 2;
        }
        var transport = new StreamServerTransport(input, output, CircuitServer.Name);
        if (attach)
        {
            await log.WriteLineAsync($"CirqAvalonia MCP bridge to the open editor (pipe {LiveProtocol.PipeName}), working folder {root}.").ConfigureAwait(false);
            await using var bridge = new LiveBridge(LiveProtocol.PipeName, root);
            await using var server = McpServer.Create(transport, bridge.Options());
            await server.RunAsync(cancel).ConfigureAwait(false);
        }
        else
        {
            await log.WriteLineAsync($"CirqAvalonia MCP server (headless), working folder {root}.").ConfigureAwait(false);
            await CircuitServer.RunAsync(transport, new HeadlessHost(root, library()), cancel).ConfigureAwait(false);
        }
        return 0;
    }
}
