using System.Text;
using System.Text.Json.Nodes;
using Cirq.UI.Agents;

namespace Cirq.Mcp.Tests;

/// <summary><c>CirqAvalonia --mcp</c>: the options, and a session over its standard streams.</summary>
public class CommandLineTests
{
    private static Task<int> Run(string[] args, StringWriter log, Stream? input = null, Stream? output = null) =>
        McpCommandLine.RunAsync(args, () => Bench.Library, input ?? new MemoryStream(), output ?? new MemoryStream(), log);

    [Fact]
    public async Task It_explains_its_options_and_refuses_bad_ones()
    {
        Assert.True(McpCommandLine.Wanted(["--MCP"]));
        Assert.False(McpCommandLine.Wanted(["circuit.cirq"]));

        var log = new StringWriter();
        Assert.Equal(0, await Run(["--mcp", "--help"], log));
        Assert.Contains("--attach", log.ToString());

        log = new StringWriter();
        Assert.Equal(2, await Run(["--mcp", "--frobnicate"], log));
        Assert.Contains("Unknown option --frobnicate", log.ToString());

        log = new StringWriter();
        Assert.Equal(2, await Run(["--mcp", "--root", "/no/such/folder/at/all"], log));
        Assert.Contains("doesn't exist", log.ToString());
    }

    [Fact]
    public async Task Headless_it_serves_a_circuit_of_its_own_on_standard_streams_and_writes_nothing_else_there()
    {
        using var folder = new TempFolder();
        var requests = string.Join("\n",
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"add_parts","arguments":{"parts":[{"kind":"Resistor"}]}}}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"save_circuit","arguments":{"path":"one.cirq"}}}""") + "\n";
        using var stdin = new Wire();
        using var stdout = new Wire();
        var log = new StringWriter();
        var server = Run(["--mcp", "--root", folder.Path], log, stdin.Reader, stdout.Writer);
        await stdin.Writer.WriteAsync(Encoding.UTF8.GetBytes(requests));
        await stdin.Writer.FlushAsync();

        // Every line on stdout is a JSON-RPC message; the notes go to the log.
        var answers = new List<JsonObject>();
        using (var reader = new StreamReader(stdout.Reader, leaveOpen: true))
            while (!answers.Any(a => (int?)a["id"] == 3) && await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) is { } line)
                answers.Add(JsonNode.Parse(line)!.AsObject());
        stdin.Writer.Close();
        Assert.Equal(0, await server.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.All(answers, a => Assert.Equal("2.0", (string?)a["jsonrpc"]));
        Assert.Contains(answers, a => (int?)a["id"] == 3 && a["result"]!["isError"] is null);
        Assert.Contains("headless", log.ToString());
        Assert.True(File.Exists(folder.File("one.cirq")));
    }

    [Fact]
    public void The_app_hands_the_server_its_own_library()
    {
        var library = new EditorLibrary();
        Assert.Equal(Cirq.UI.ViewModels.ComponentCatalog.Categories.Sum(c => c.Items.Count), library.Parts.Count);
        Assert.Equal(Cirq.UI.ViewModels.Examples.All.Count, library.Examples.Count);
        var example = library.LoadExample("RC Low-Pass");
        Assert.Equal(2, example.Circuit.Probes.Count);
        Assert.Equal(200e-6, example.Timebase, 9);
    }
}
