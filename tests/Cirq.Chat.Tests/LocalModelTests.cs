using System.Text.Json.Nodes;
using Cirq.Mcp;
using static Cirq.Chat.Tests.FakeModelServer;

namespace Cirq.Chat.Tests;

/// <summary>
/// The chat on an OpenAI-compatible server (a local model through Ollama or LM Studio, one on another machine on the
/// network, or a cloud service), against a fake server scripting server-sent events; and Undo this reply on the
/// server's checkpoints. No real model server is ever called.
/// </summary>
public class LocalModelTests
{
    private static async Task<(Rig Rig, FakeModelServer Server)> Start(FakeModelServer server, string? key = null)
    {
        var rig = await Rig.Start();
        rig.Chat.Provider = ChatProvider.OpenAICompatible;
        rig.Chat.Server = server.Client(key);
        rig.Chat.ServerModel = "qwen3";
        return (rig, server);
    }

    [Fact]
    public async Task A_local_model_builds_a_circuit_with_no_key_and_gets_the_system_prompt_and_tools()
    {
        var (rig, server) = await Start(new FakeModelServer()
            .Reply(Say("I'll place a resistor."), Call("c1", "add_parts", new { parts = new object[] { new { kind = "Resistor", x = 0, y = 0 } } }))
            .Reply(Say("There's your resistor.")));
        await using var _ = rig;
        await rig.Send("place a resistor");

        Assert.Single(rig.Host.Session.Circuit.Components);
        var tool = Assert.Single(rig.Chat.Shown().OfType<ToolEntry>());
        Assert.Equal("add_parts Resistor → added 1", tool.Line);
        Assert.Equal("There's your resistor.", Assert.IsType<AssistantEntry>(rig.Chat.Shown()[^1]).Text);

        var request = server.Requests[0];
        Assert.Equal("qwen3", (string?)request["model"]);
        var system = (string?)request["messages"]![0]!["content"];
        Assert.StartsWith(CircuitServer.Instructions.TrimEnd()[..120], system);
        Assert.Contains("CirqAvalonia itself", system);
        // Core tools only, by default for a server's model: fewer for a small model to choose among.
        Assert.Equal(ChatTools.Core.Count, request["tools"]!.AsArray().Count);
        Assert.Null(server.Authorization[0]); // a local server is sent no key
    }

    [Fact]
    public async Task A_key_for_a_cloud_service_or_gateway_goes_only_in_its_header()
    {
        var (rig, server) = await Start(new FakeModelServer("https://api.example.com/v1").Reply(Say("Hello.")), Key);
        await using var _ = rig;
        await rig.Send("hi");
        Assert.Equal("Bearer " + Key, server.Authorization[0]);
        Assert.DoesNotContain(Key, server.Requests[0].ToJsonString());
    }

    [Fact]
    public async Task Undo_this_reply_puts_the_circuit_back_as_one_undo_step()
    {
        var (rig, _) = await Start(new FakeModelServer()
            .Reply(Call("c1", "add_parts", new { parts = new object[] { new { kind = "Resistor", x = 0, y = 0 }, new { kind = "Capacitor", x = 100, y = 0 } } }))
            .Reply(Say("Added an R and a C.")));
        await using var _ = rig;
        await rig.Send("add an R and a C");
        Assert.Equal(2, rig.Host.Session.Circuit.Components.Count);

        var undo = Assert.Single(rig.Chat.Entries.OfType<ReplyUndoEntry>());
        Assert.Equal("+2 parts", undo.Summary);
        Assert.True(await rig.Chat.UndoReplyAsync(undo));
        Assert.Empty(rig.Host.Session.Circuit.Components);
        // Ctrl+Z (the session's undo) brings the reply's parts back.
        Assert.NotNull(rig.Host.Session.Undo());
        Assert.Equal(2, rig.Host.Session.Circuit.Components.Count);
    }

    [Fact]
    public void A_circuit_diff_is_summed_up_in_a_line() =>
        Assert.Equal("+2 parts, −1 part, 1 changed, +3 wires, probes", ChatSession.Summarise(JsonNode.Parse("""
            { "changed": true, "parts": { "added": ["R1", "C1"], "removed": ["Q1"], "changed": ["U1"] },
              "wires": { "added": 3, "removed": 0 }, "probes_changed": true, "other_changed": false }
            """)!.AsObject()));
}
