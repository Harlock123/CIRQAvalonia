using System.Net;
using System.Net.Http.Headers;
using ModelContextProtocol.Client;

namespace Cirq.Mcp.Tests;

/// <summary>
/// The network door, headless: the circuit server over Streamable HTTP with a token (the SDK's own client on
/// localhost, port 0), the same tools as stdio, requests without the token or from another site's page refused,
/// bad settings said plainly, the token's owner-only file, and the command line's address and port.
/// </summary>
public sealed class NetworkTests : IAsyncLifetime
{
    private const string Token = "cirq-test-token-0123456789";
    private readonly Bench _bench = new();
    private NetworkListener _listener = null!;

    public async Task InitializeAsync()
    {
        _listener = new NetworkListener(new NetworkSettings("localhost", 0, Token), CircuitServer.Name, o => CircuitServer.Configure(o, _bench.Host));
        await _listener.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _listener.DisposeAsync();
        _bench.Dispose();
    }

    private string Url => $"http://127.0.0.1:{_listener.Port}{NetworkListener.Path}";

    private async Task<McpClient> Client(string token = Token, string? protocol = "2025-11-25") =>
        await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(Url),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
        }), protocol == null ? null : new McpClientOptions { ProtocolVersion = protocol }, null, CancellationToken.None);

    [Fact]
    public async Task An_agent_on_the_network_gets_every_tool_and_builds_the_circuit()
    {
        await using var client = await Client();
        Assert.Equal(CircuitServer.Name, client.ServerInfo?.Name);
        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
        Assert.Equal(CircuitServer.Definitions().Select(t => t.Name).Order(), tools.Select(t => t.Name).Order());
        await client.Json("add_parts", new { parts = new object[] { new { kind = "Resistor" }, new { kind = "Ground", y = 100 } } });
        Assert.Equal(2, _bench.Circuit.Components.Count);
        Assert.Equal(1, _listener.Connections);
    }

    [Fact]
    public async Task A_client_on_the_newest_protocol_is_served_too()
    {
        await using var client = await Client(protocol: null);
        await client.Json("add_parts", new { parts = new object[] { new { kind = "Resistor" } } });
        Assert.Single(_bench.Circuit.Components);
    }

    [Fact]
    public async Task Without_the_right_token_or_from_another_sites_page_nothing_gets_in()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => Client("not-the-right-token-at-all"));
        using var http = new HttpClient();
        HttpRequestMessage Post(string? token, string? origin = null)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, Url)
            {
                Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""", System.Text.Encoding.UTF8, "application/json"),
            };
            r.Headers.Accept.ParseAdd("application/json");
            r.Headers.Accept.ParseAdd("text/event-stream");
            if (token != null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (origin != null) r.Headers.Add("Origin", origin);
            return r;
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Post(null), CancellationToken.None)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Post(Token + "x"), CancellationToken.None)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Post(Token, "https://example.com"), CancellationToken.None)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Post(Token), CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task Bad_settings_and_a_port_in_use_are_said_plainly()
    {
        Assert.Contains("16 characters", new NetworkSettings("0.0.0.0", 8767, "short").Problem());
        Assert.Contains("isn't an address", new NetworkSettings("nowhere at all", 8767, Token).Problem());
        await using var second = new NetworkListener(new NetworkSettings("localhost", _listener.Port, Token), CircuitServer.Name, o => CircuitServer.Configure(o, _bench.Host));
        var e = await Assert.ThrowsAsync<IOException>(() => second.StartAsync(CancellationToken.None));
        Assert.Contains("already in use", e.Message);
    }

    [Fact]
    public void The_token_file_is_owner_only_and_the_command_line_reads_address_and_port()
    {
        var store = NetworkToken.In(_bench.Folder.Path);
        var token = store.Get();
        Assert.Equal(token, store.Get());
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.Path));
        Assert.NotEqual(token, store.Renew());

        Assert.True(McpCommandLine.TryParseEndpoint("9000", McpCommandLine.NetworkPort, out var a, out var p));
        Assert.Equal(("0.0.0.0", 9000), (a, p));
        Assert.True(McpCommandLine.TryParseEndpoint("localhost", McpCommandLine.NetworkPort, out a, out p));
        Assert.Equal(("localhost", 8767), (a, p));
        Assert.False(McpCommandLine.TryParseEndpoint("localhost:0", McpCommandLine.NetworkPort, out _, out _));
        Assert.Contains("--http", McpCommandLine.Usage);
    }
}
