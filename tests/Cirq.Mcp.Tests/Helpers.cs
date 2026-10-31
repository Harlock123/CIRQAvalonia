using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cirq.UI.Agents;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cirq.Mcp.Tests;

/// <summary>A folder of its own for a test, deleted afterwards.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cirq-mcp-" + Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); }
        catch (IOException) { }
    }
}

/// <summary>One end of an in-process byte stream pair: what one side writes, the other reads.</summary>
internal sealed class Wire : IDisposable
{
    private readonly AnonymousPipeServerStream _write = new(PipeDirection.Out);
    private readonly AnonymousPipeClientStream _read;

    public Wire() => _read = new AnonymousPipeClientStream(PipeDirection.In, _write.ClientSafePipeHandle);

    public Stream Writer => _write;
    public Stream Reader => _read;

    public void Dispose()
    {
        _write.Dispose();
        _read.Dispose();
    }
}

/// <summary>An MCP client talking to a server in the same process, over a pair of in-memory pipes.</summary>
internal sealed class InProcess : IAsyncDisposable
{
    private readonly Wire _up = new(), _down = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _server = Task.CompletedTask;

    public McpClient Client { get; private set; } = null!;

    public static Task<InProcess> Start(ICircuitHost host) => Start(CircuitServer.Options(host));

    public static async Task<InProcess> Start(McpServerOptions options)
    {
        var s = new InProcess();
        var transport = new StreamServerTransport(s._up.Reader, s._down.Writer, "test");
        var server = McpServer.Create(transport, options);
        s._server = Task.Run(async () =>
        {
            await using (server) await server.RunAsync(s._stop.Token);
        });
        s.Client = await McpClient.CreateAsync(new StreamClientTransport(s._up.Writer, s._down.Reader), null, null, CancellationToken.None);
        return s;
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _stop.CancelAsync();
        try { await _server.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException or IOException) { }
        _up.Dispose();
        _down.Dispose();
    }
}

internal static class Calls
{
    /// <summary>Calls a tool and returns its JSON answer, failing the test on an error result.</summary>
    public static async Task<JsonObject> Json(this McpClient client, string tool, object? args = null)
    {
        var result = await client.Call(tool, args);
        Assert.True(result.IsError != true, $"{tool} failed: {Text(result)}");
        return (JsonObject)JsonNode.Parse(Text(result))!;
    }

    public static async Task<CallToolResult> Call(this McpClient client, string tool, object? args = null)
    {
        var dict = args == null ? null : JsonSerializer.SerializeToElement(args).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        return await client.CallToolAsync(tool, dict, cancellationToken: CancellationToken.None);
    }

    /// <summary>Calls a tool that should fail, and returns its message.</summary>
    public static async Task<string> Fails(this McpClient client, string tool, object? args = null)
    {
        var result = await client.Call(tool, args);
        Assert.True(result.IsError == true, $"{tool} should have failed but said: {Text(result)}");
        return Text(result);
    }

    public static string Text(CallToolResult r) => string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text));

    /// <summary>The PNG a result carries.</summary>
    public static byte[] Png(CallToolResult r)
    {
        var png = Assert.Single(r.Content.OfType<ImageContentBlock>()).DecodedData.ToArray();
        Assert.True(png.Length > 1000 && png[0] == 0x89 && png[1] == 'P', "not a PNG");
        return png;
    }

    /// <summary>A handler's JSON answer.</summary>
    public static async Task<JsonObject> Parse(Task<string> call) => (JsonObject)JsonNode.Parse(await call)!;

    /// <summary>The message of a handler call that should fail as an MCP tool error.</summary>
    public static async Task<string> Error<T>(Task<T> call) => (await Assert.ThrowsAsync<McpException>(async () => await call)).Message;

    /// <summary>JSON arguments as a handler takes them.</summary>
    public static Dictionary<string, JsonElement> Args(object o) =>
        JsonSerializer.SerializeToElement(o).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    public static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);
}

/// <summary>A headless host with an empty circuit and the editor's own library, and the handlers bound to it.</summary>
internal sealed class Bench : IDisposable
{
    /// <summary>One library for every test: building the palette's list makes each part once.</summary>
    public static readonly EditorLibrary Library = new();

    public TempFolder Folder { get; } = new();
    public HeadlessHost Host { get; }
    public CircuitTools Tools { get; }
    public HeadlessSession Session => Host.Session;
    public Cirq.Core.Topology.Circuit Circuit => Host.Session.Circuit;

    public Bench()
    {
        Host = new HeadlessHost(Folder.Path, Library);
        Tools = new CircuitTools(Host);
    }

    /// <summary>An RC low-pass (10 kΩ, 100 nF, so a 159 Hz corner) driven by a 0-4 V square at 500 Hz, probed in and out.</summary>
    public async Task BuildRc()
    {
        await Tools.AddParts(
        [
            new() { Kind = "Function Generator", X = -160, Y = 40, Rotation = 90, Settings = Calls.Args(new { Shape = "Square", Frequency = "500", AmplitudePeakToPeak = 4, DcOffset = 2 }) },
            new() { Kind = "Resistor", X = -40, Y = -20, Settings = Calls.Args(new { Resistance = "10k" }) },
            new() { Kind = "Capacitor", X = 60, Y = 40, Rotation = 90, Settings = Calls.Args(new { Capacitance = "100n" }) },
            new() { Kind = "Ground", X = -160, Y = 140 },
            new() { Kind = "Ground", X = 60, Y = 140 },
        ]);
        await Tools.Connect([["FG1.OUT", "R1.A"], ["R1.B", "C1.A"], ["C1.B", "GND2.GND"], ["FG1.GND", "GND1.GND"]]);
        await Tools.AddProbe("R1.A", "Input");
        await Tools.AddProbe("C1.A", "Output");
    }

    public void Dispose() => Folder.Dispose();
}
