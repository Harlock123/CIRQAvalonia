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
                           [--http [ADDRESS:]PORT [--token TOKEN] [--cert FILE --key FILE]]

          --mcp          Serve the Model Context Protocol on stdin and stdout for an AI agent, with no window.
          --attach       Drive the CirqAvalonia that's open on this desktop (with AI › Allow AI Agents on),
                         instead of a circuit of the server's own.
          --root FOLDER  The working folder that open and save paths start from, and stay inside
                         (default: the current folder).
          --http [ADDRESS:]PORT
                         Serve over the network instead of stdio (MCP Streamable HTTP at /mcp), headless, until
                         stopped with Ctrl+C. ADDRESS is 0.0.0.0 (every network; the default), localhost or one of
                         this computer's addresses; the port is 8767 unless given. Every agent works on one circuit.
          --token TOKEN  The access token agents must send ("Authorization: Bearer TOKEN"), 16 characters or
                         more; else the CIRQAVALONIA_MCP_TOKEN environment variable; else a new one, printed.
          --cert FILE --key FILE
                         Serve HTTPS with this certificate and private key (PEM files).
        """;

    /// <summary>The network door's port unless another is given.</summary>
    public const int NetworkPort = 8767;

    /// <summary>The environment variable that can give the network door's token.</summary>
    public const string TokenVariable = "CIRQAVALONIA_MCP_TOKEN";

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
        string? http = null, token = null, cert = null, key = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--mcp": break;
                case "--attach": attach = true; break;
                case "--root" when i + 1 < args.Count: root = args[++i]; break;
                case "--http" when i + 1 < args.Count: http = args[++i]; break;
                case "--token" when i + 1 < args.Count: token = args[++i]; break;
                case "--cert" when i + 1 < args.Count: cert = args[++i]; break;
                case "--key" when i + 1 < args.Count: key = args[++i]; break;
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
        if (http != null)
        {
            if (attach)
            {
                await log.WriteLineAsync("--http serves a circuit of its own; to let other computers work in the open editor, turn its network door on in AI › Connect an Agent.").ConfigureAwait(false);
                return 2;
            }
            return await ServeHttpAsync(http, token, cert, key, root, library(), log, cancel).ConfigureAwait(false);
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

    /// <summary>Splits <c>[ADDRESS:]PORT</c> (an IPv6 address in brackets): the address is every network unless given.</summary>
    public static bool TryParseEndpoint(string text, int defaultPort, out string address, out int port)
    {
        address = "0.0.0.0";
        port = defaultPort;
        text = text.Trim();
        int colon = text.LastIndexOf(':');
        string portText = text;
        if (colon >= 0 && !(text.StartsWith('[') && text.EndsWith(']')))
        {
            address = text[..colon];
            portText = text[(colon + 1)..];
        }
        else if (!text.All(char.IsAsciiDigit))
        {
            address = text;
            return NetworkListener.ParseAddress(address) != null;
        }
        return int.TryParse(portText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535
            && NetworkListener.ParseAddress(address) != null;
    }

    /// <summary>The headless server over the network, one circuit for every agent, until <paramref name="cancel"/> (Ctrl+C).</summary>
    private static async Task<int> ServeHttpAsync(string endpoint, string? token, string? cert, string? key, string root, ICircuitLibrary library, TextWriter log, CancellationToken cancel)
    {
        if (!TryParseEndpoint(endpoint, NetworkPort, out var address, out var port))
        {
            await log.WriteLineAsync($"\"{endpoint}\" isn't [ADDRESS:]PORT (for example 8767 or 0.0.0.0:8767).").ConfigureAwait(false);
            return 2;
        }
        bool made = false;
        token ??= Environment.GetEnvironmentVariable(TokenVariable) is { Length: > 0 } fromEnvironment ? fromEnvironment : null;
        if (token == null)
        {
            token = NetworkListener.NewToken();
            made = true;
        }
        var settings = new NetworkSettings(address, port, token, cert, key);
        var host = new HeadlessHost(root, library);
        await using var listener = new NetworkListener(settings, CircuitServer.Name, o => CircuitServer.Configure(o, host));
        try
        {
            await listener.StartAsync(cancel).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            await log.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 1;
        }
        await log.WriteLineAsync($"CirqAvalonia MCP server (headless) on the network, working folder {root}. Agents connect to:").ConfigureAwait(false);
        foreach (var url in listener.Urls) await log.WriteLineAsync("  " + url).ConfigureAwait(false);
        await log.WriteLineAsync(made
            ? $"with the header \"Authorization: Bearer {token}\" (a new token: give --token or {TokenVariable} to keep one)."
            : "with the header \"Authorization: Bearer <your token>\".").ConfigureAwait(false);
        if (!settings.Secure) await log.WriteLineAsync("Plain HTTP: the token and the work cross the network unencrypted. Use --cert and --key on a network you don't trust.").ConfigureAwait(false);
        await log.WriteLineAsync("Ctrl+C stops it.").ConfigureAwait(false);
        try
        {
            await Task.Delay(Timeout.Infinite, cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        return 0;
    }
}
