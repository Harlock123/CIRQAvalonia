using System.Text.Json;
using System.Text.Json.Nodes;
using Cirq.Components.Passive;
using Cirq.Mcp;
using static Cirq.Chat.Tests.FakeClaude;

namespace Cirq.Chat.Tests;

public class ChatTests
{
    private static CancellationToken Cancel => CancellationToken.None;

    private static readonly object Resistor = new { parts = new object[] { new { kind = "Resistor", settings = new { Resistance = "10k" } } } };

    [Fact]
    public async Task Claude_places_a_part_looks_at_the_drawing_and_says_so()
    {
        var api = new FakeClaude()
            .Reply(Say("I'll place a resistor."), Call("toolu_1", "add_parts", Resistor))
            .Reply(new Thinking("sig-abc"), Call("toolu_2", "render_schematic", new { scale = 1 }))
            .Reply(Say("There's your 10 kΩ resistor."));
        await using var rig = await Rig.Start(api);
        await rig.Send("place a 10k resistor");

        // The tool ran on the circuit: one undo step, one resistor.
        var circuit = rig.Host.Session.Circuit;
        Assert.Equal(10e3, Assert.IsType<Resistor>(Assert.Single(circuit.Components)).Resistance);
        Assert.Equal(1, rig.Host.Session.UndoCount);
        Assert.Equal(0, api.Left);
        Assert.False(rig.Chat.Working);

        // What the panel shows: the person, Claude, the two tool calls, Claude's answer.
        var entries = rig.Chat.Entries;
        Assert.Equal("place a 10k resistor", Assert.IsType<UserEntry>(entries[0]).Text);
        Assert.Equal("I'll place a resistor.", Assert.IsType<AssistantEntry>(entries[1]).Text);
        var placed = Assert.IsType<ToolEntry>(entries[2]);
        Assert.Equal("add_parts Resistor → added 1", placed.Line);
        Assert.False(placed.IsError);
        var look = entries.OfType<ToolEntry>().Single(e => e.Name == "render_schematic");
        var image = Assert.Single(look.Images);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(0x89, image.Data[0]);
        var last = Assert.IsType<AssistantEntry>(entries[^1]);
        Assert.Equal("There's your 10 kΩ resistor.", last.Text);
        Assert.False(last.Streaming);

        // The picture went back to Claude as an image block in the tool_result, with its caption.
        var third = api.Requests[2];
        var result = Json.Results(third).Single(r => (string?)r["tool_use_id"] == "toolu_2");
        var content = result["content"]!.AsArray();
        var img = content.OfType<JsonObject>().Single(b => (string?)b["type"] == "image");
        Assert.Equal("image/png", (string?)img["source"]!["media_type"]);
        Assert.Equal(image.Data, Convert.FromBase64String((string)img["source"]!["data"]!));
        Assert.Contains("1 parts", (string?)content.OfType<JsonObject>().Single(b => (string?)b["type"] == "text")["text"]);
        Assert.Null(result["is_error"]);

        // The thinking block came back unchanged, signature and all.
        var assistant = third["messages"]!.AsArray()[3]!["content"]!.AsArray();
        Assert.Equal("sig-abc", (string?)assistant.OfType<JsonObject>().Single(b => (string?)b["type"] == "thinking")["signature"]);
        Assert.Equal(["user", "assistant", "user", "assistant", "user"], third["messages"]!.AsArray().Select(m => (string)m!["role"]!));
    }

    [Fact]
    public async Task Requests_cache_the_system_prompt_and_tools_and_name_the_model()
    {
        var api = new FakeClaude().Reply(Say("Hello.")).Reply(Say("Again."));
        await using var rig = await Rig.Start(api);
        await rig.Send("hi");
        rig.Chat.Model = ChatModels.Haiku;
        await rig.Send("hi again");

        var r = api.Requests[0];
        Assert.Equal(ChatModels.Opus, (string?)r["model"]);
        Assert.Equal("claude-haiku-4-5-20251001", (string?)api.Requests[1]["model"]);
        Assert.True((bool?)r["stream"]);
        // The system prompt is the server's instructions plus the in-app lines, with a cache breakpoint.
        var system = Assert.Single(r["system"]!.AsArray())!;
        Assert.StartsWith(CircuitServer.Instructions.TrimEnd()[..200], (string?)system["text"]);
        Assert.Contains("AI Chat panel", (string?)system["text"]);
        Assert.Equal("ephemeral", (string?)system["cache_control"]!["type"]);
        // Only the last tool carries one (it caches all the tools before it), and the conversation is cached as a whole.
        var tools = r["tools"]!.AsArray();
        Assert.Equal("ephemeral", (string?)tools[^1]!["cache_control"]!["type"]);
        Assert.All(tools.Take(tools.Count - 1), t => Assert.Null(t!["cache_control"]));
        Assert.Equal("ephemeral", (string?)r["cache_control"]!["type"]);
        Assert.Equal(3, Breakpoints(r));
        // The conversation is kept: the second request has the first exchange in it.
        Assert.Equal(3, api.Requests[1]["messages"]!.AsArray().Count);
        // The key goes in its header, and nowhere in the body.
        Assert.DoesNotContain(Key, api.Requests[0].ToJsonString());
        var h = api.Headers[0].Headers;
        Assert.Contains(Key, (h.TryGetValues("x-api-key", out var k) ? k : []).Concat(h.Authorization is { } a ? [a.ToString()] : []).Single());
    }

    private static int Breakpoints(JsonNode? node) => node switch
    {
        JsonObject o => o.Sum(p => (p.Key == "cache_control" ? 1 : 0) + Breakpoints(p.Value)),
        JsonArray a => a.Sum(Breakpoints),
        _ => 0,
    };

    [Fact]
    public async Task Claude_gets_exactly_the_MCP_servers_tools()
    {
        var api = new FakeClaude().Reply(Say("ok"));
        await using var rig = await Rig.Start(api);
        await rig.Send("hi");
        var sent = api.Requests[0]["tools"]!.AsArray().Select(t => t!.AsObject()).ToList();
        var server = CircuitServer.Definitions();
        Assert.Equal(server.Select(t => t.Name), sent.Select(t => (string?)t["name"]));
        Assert.Equal(31, sent.Count);
        foreach (var (mcp, tool) in server.Zip(sent))
        {
            Assert.Equal(mcp.Description, (string?)tool["description"]);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(mcp.InputSchema.GetRawText()), tool["input_schema"]), $"{mcp.Name}'s schema changed on the way.");
        }
    }

    [Fact]
    public async Task A_tool_error_goes_back_as_an_is_error_result_and_Claude_carries_on()
    {
        var api = new FakeClaude()
            .Reply(Call("toolu_1", "add_parts", new { parts = new object[] { new { kind = "Flux Capacitor" } } }))
            .Reply(Call("toolu_2", "add_parts", new { parts = Array.Empty<object>(), nonsense = 3 }))
            .Reply(Say("There's no flux capacitor in the palette."));
        await using var rig = await Rig.Start(api);
        await rig.Send("a flux capacitor");
        var failed = rig.Chat.Entries.OfType<ToolEntry>().ToList();
        Assert.All(failed, f => Assert.True(f.IsError));
        Assert.Contains("✗", failed[0].Line);
        var result = Json.Results(api.Requests[1]).Single();
        Assert.True((bool?)result["is_error"]);
        Assert.Contains("Flux Capacitor", result.ToJsonString());
        Assert.Contains("no argument", Json.Results(api.Requests[2]).Last().ToJsonString());
        // A failed call changes nothing.
        Assert.Empty(rig.Host.Session.Circuit.Components);
        Assert.Equal("There's no flux capacitor in the palette.", ((AssistantEntry)rig.Chat.Entries[^1]).Text);
    }

    [Theory]
    [InlineData(401, "authentication_error", "401")]
    [InlineData(429, "rate_limit_error", "429")]
    [InlineData(529, "overloaded_error", "overloaded")]
    [InlineData(500, "api_error", "500")]
    public async Task API_errors_show_as_a_line_with_Retry_and_Retry_works(int status, string type, string says)
    {
        var api = new FakeClaude().Error(status, type, "Something went wrong.").Reply(Say("Here I am."));
        await using var rig = await Rig.Start(api);
        await rig.Send("hello");
        var error = Assert.IsType<NoticeEntry>(rig.Chat.Entries[^1]);
        Assert.Equal(NoticeKind.Error, error.Kind);
        Assert.True(error.CanRetry);
        Assert.Contains(says, error.Text);
        Assert.DoesNotContain(Key, error.Text);
        Assert.DoesNotContain(Key[^8..], error.Text);
        Assert.False(rig.Chat.Working);

        await rig.Chat.RetryAsync(Cancel);
        Assert.False(error.CanRetry);
        Assert.Equal("Here I am.", ((AssistantEntry)rig.Chat.Entries[^1]).Text);
        Assert.Equal(api.Requests[0]["messages"]!.ToJsonString(), api.Requests[1]["messages"]!.ToJsonString());
    }

    [Fact]
    public async Task A_network_failure_is_explained_not_thrown()
    {
        await using var rig = await Rig.Start();
        rig.Chat.Client = new Anthropic.AnthropicClient { ApiKey = Key, HttpClient = new HttpClient(new Unreachable()), MaxRetries = 0 };
        await rig.Send("hello");
        var error = Assert.IsType<NoticeEntry>(rig.Chat.Entries[^1]);
        Assert.Contains("Couldn't reach the Anthropic API", error.Text);
        Assert.True(error.CanRetry);
    }

    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            throw new HttpRequestException("No route to host.");
    }

    [Fact]
    public async Task The_step_cap_stops_a_runaway_request_and_says_so()
    {
        var api = new FakeClaude();
        for (var i = 0; i < 3; i++) api.Reply(Call($"toolu_{i}a", "add_parts", Resistor), Call($"toolu_{i}b", "get_circuit", new { }));
        await using var rig = await Rig.Start(api);
        rig.Chat.MaxToolCalls = 5;
        await rig.Send("resistors forever");
        Assert.Equal(5, rig.Chat.Entries.OfType<ToolEntry>().Count());
        var notice = Assert.IsType<NoticeEntry>(rig.Chat.Entries[^1]);
        Assert.Contains("5 tool calls", notice.Text);
        Assert.Equal(0, api.Left);
        // Every tool_use still has its tool_result (the sixth refused), so "continue" can carry on.
        var turns = rig.Chat.Turns;
        Assert.True(turns[^1].User);
        Assert.Contains("limit of 5 tool calls", turns[^1].Blocks.Select(b => b.Json.GetRawText()).Last());
        api.Reply(Say("Done."));
        await rig.Send("continue");
        var last = api.Requests[^1]["messages"]!.AsArray();
        Assert.Equal("continue", (string?)last[^1]!["content"]!.AsArray()[^1]!["text"]);
        Assert.Equal("assistant", (string?)last[^2]!["role"]);
    }

    [Fact]
    public async Task Auto_continue_carries_on_past_the_limit_by_itself_then_stops()
    {
        var api = new FakeClaude();
        for (var i = 0; i < 4; i++) api.Reply(Call($"toolu_{i}a", "add_parts", Resistor), Call($"toolu_{i}b", "get_circuit", new { }));
        api.Reply(Say("All done."));
        await using var rig = await Rig.Start(api);
        rig.Chat.MaxToolCalls = 3;
        rig.Chat.AutoContinues = 2;
        await rig.Send("eight calls");
        var tools = rig.Chat.Entries.OfType<ToolEntry>().ToList();
        Assert.Equal(8, tools.Count);
        Assert.All(tools, t => Assert.DoesNotContain("limit of", t.Result));
        var notices = rig.Chat.Entries.OfType<NoticeEntry>().ToList();
        Assert.Equal(2, notices.Count);
        Assert.Contains("carrying on by itself (2 of 2", notices[1].Text);
        Assert.Equal("All done.", ((AssistantEntry)rig.Chat.Entries[^1]).Text);
        Assert.Equal(4, rig.Host.Session.Circuit.Components.Count);

        // Past its last automatic continue, it stops as before.
        var more = new FakeClaude();
        for (var i = 0; i < 3; i++) more.Reply(Call($"toolu_{i}a", "add_parts", Resistor), Call($"toolu_{i}b", "get_circuit", new { }));
        await using var rig2 = await Rig.Start(more);
        rig2.Chat.MaxToolCalls = 2;
        rig2.Chat.AutoContinues = 1;
        await rig2.Send("resistors forever");
        Assert.Equal(4, rig2.Chat.Entries.OfType<ToolEntry>().Count(t => !t.IsError));
        Assert.Contains("Stopped after 4 tool calls", Assert.IsType<NoticeEntry>(rig2.Chat.Entries[^1]).Text);
    }

    [Fact]
    public async Task Stop_ends_a_reply_that_is_still_coming()
    {
        var api = new FakeClaude().Hang().Reply(Say("Back again."));
        await using var rig = await Rig.Start(api);
        var send = rig.Send("hello");
        for (var i = 0; i < 500 && api.Requests.Count == 0; i++) await Task.Delay(10, Cancel);
        Assert.True(rig.Chat.Working);
        rig.Chat.Stop();
        await send.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
        Assert.False(rig.Chat.Working);
        Assert.Equal("Stopped.", Assert.IsType<NoticeEntry>(rig.Chat.Entries[^1]).Text);
        await rig.Send("are you there?");
        var only = Assert.Single(api.Requests[^1]["messages"]!.AsArray())!;
        Assert.Equal(2, only["content"]!.AsArray().Count);
    }

    [Fact]
    public async Task Stop_gives_up_a_tool_call_waiting_for_a_busy_person()
    {
        // The live host as the editor has it, with a dialog open: the call waits (as for outside agents).
        var person = new BusyPerson();
        var host = new LiveHost(person, person, Rig.Library, Path.GetTempPath()) { BusyWait = TimeSpan.FromMinutes(1), BusyPoll = TimeSpan.FromMilliseconds(20) };
        await using var bridge = await McpToolBridge.StartAsync(host, Cancel);
        var api = new FakeClaude().Reply(Call("toolu_1", "add_parts", Resistor)).Reply(Say("ok"));
        var chat = new ChatSession(bridge) { Client = api.Client() };
        var send = chat.SendAsync("a resistor", Cancel);
        for (var i = 0; i < 500 && person.Asked < 2; i++) await Task.Delay(10, Cancel);
        Assert.True(chat.Working);
        Assert.True(Assert.Single(chat.Entries.OfType<ToolEntry>()).Running);
        chat.Stop();
        await send.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
        var tool = Assert.Single(chat.Entries.OfType<ToolEntry>());
        Assert.True(tool.IsError);
        Assert.Contains("Stopped", tool.Result);
        Assert.Empty(person.Session.Circuit.Components);
        Assert.True(chat.Turns[^1].User);
        Assert.Contains("toolu_1", chat.Turns[^1].Blocks[0].Json.GetRawText());
    }

    [Fact]
    public async Task New_chat_while_Claude_works_leaves_nothing_of_the_old_request()
    {
        var person = new BusyPerson();
        var host = new LiveHost(person, person, Rig.Library, Path.GetTempPath()) { BusyWait = TimeSpan.FromMinutes(1), BusyPoll = TimeSpan.FromMilliseconds(20) };
        await using var bridge = await McpToolBridge.StartAsync(host, Cancel);
        var api = new FakeClaude().Reply(Call("toolu_1", "add_parts", Resistor)).Reply(Say("Fresh start."));
        var chat = new ChatSession(bridge) { Client = api.Client() };
        var send = chat.SendAsync("a resistor", Cancel);
        for (var i = 0; i < 500 && person.Asked < 2; i++) await Task.Delay(10, Cancel);
        chat.NewChat();
        await send.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
        Assert.Empty(chat.Entries);
        Assert.Empty(chat.Turns);
        await chat.SendAsync("hello", Cancel);
        Assert.Single(api.Requests[^1]["messages"]!.AsArray());
        Assert.Equal("Fresh start.", ((AssistantEntry)chat.Entries[^1]).Text);
    }

    private sealed class BusyPerson : ILiveCircuit, IUiThread
    {
        public int Asked;
        public ICircuitSession Session { get; } = new HeadlessSession();
        public string? Busy { get { Interlocked.Increment(ref Asked); return "a dialog box is open in CirqAvalonia"; } }
        public void Report(string text) { }
        public Task<T> InvokeAsync<T>(Func<T> work) => Task.FromResult(work());
    }

    [Fact]
    public async Task Without_a_key_the_chat_explains_how_to_set_one()
    {
        var api = new FakeClaude();
        await using var rig = await Rig.Start(api);
        rig.Chat.Client = null;
        await rig.Send("hello");
        var notice = Assert.IsType<NoticeEntry>(rig.Chat.Entries[^1]);
        Assert.Contains(ApiKeyStore.EnvironmentVariable, notice.Text);
        Assert.Contains("CirqAvalonia", notice.Text);
        Assert.Contains("Key…", notice.Text);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task New_chat_forgets_the_conversation()
    {
        var api = new FakeClaude().Reply(Say("One.")).Reply(Say("Two."));
        await using var rig = await Rig.Start(api);
        var cleared = 0;
        rig.Chat.Cleared += () => cleared++;
        await rig.Send("first");
        rig.Chat.NewChat();
        Assert.Empty(rig.Chat.Entries);
        Assert.Equal(1, cleared);
        await rig.Send("second");
        Assert.Single(api.Requests[1]["messages"]!.AsArray());
    }

    [Fact]
    public void Tool_lines_are_compact()
    {
        static Dictionary<string, JsonElement> In(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

        var run = new ToolEntry("run_transient", In("""{"duration":"5m","plot":true}"""));
        Assert.Equal("run_transient 5m plot=true → …", run.Line);
        run.Finish(new ToolOutcome(false, """{"duration":"5ms","time_step":"500ns","probes":[{"probe":"Out"}]}""", [new ToolImage([1, 2], "image/png")]));
        Assert.Equal("run_transient 5m plot=true → duration 5ms, time_step 500ns", run.Line);

        var parts = new ToolEntry("add_parts", In("""{"parts":[{"kind":"Resistor"},{"kind":"Capacitor"},{"kind":"Ground"}]}"""));
        parts.Finish(new ToolOutcome(false, """{"added":[{},{},{}]}""", []));
        Assert.Equal("add_parts Resistor, Capacitor, Ground → added 3", parts.Line);

        var wires = new ToolEntry("connect", In("""{"wires":[["R1.A","C1.A"],["C1.B","GND1.GND"]]}"""));
        wires.Finish(new ToolOutcome(false, """{"wires_added":2,"nets":[]}""", []));
        Assert.Equal("connect 2 wires → wires_added 2", wires.Line);

        var failed = new ToolEntry("get_part", In("""{"name":"R9"}"""));
        failed.Finish(ToolOutcome.Error("There's no part R9."));
        Assert.Equal("get_part R9 ✗ There's no part R9.", failed.Line);
    }
}
