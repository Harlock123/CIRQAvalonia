using System.ComponentModel;
using System.Text.Json.Nodes;
using Cirq.Components.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cirq.Mcp;

/// <summary>
/// Checkpoints: the circuit as it was at a moment, kept by name for this session, to compare with (diff), look at
/// (render_checkpoint) or go back to (restore_checkpoint). The AI Chat takes one before each reply's first change, for
/// its Undo this reply button and its timeline.
/// </summary>
public sealed partial class CircuitTools
{
    private readonly Dictionary<string, (string Json, string? Note, DateTime At)> _checkpoints = [];

    [McpServerTool(Name = "checkpoint")]
    [Description("Remembers the circuit as it is now under a name, for this session: diff compares the circuit with it, render_checkpoint draws it and restore_checkpoint goes back to it. Take one before a big change you might want to undo as a whole. Without a name, lists the checkpoints.")]
    public Task<string> Checkpoint(
        [Description("The checkpoint's name. A name used before is replaced.")] string? name = null,
        [Description("A note on what it's for. Optional.")] string? note = null,
        [Description("Accepted for other clients; checkpoints last as long as the session.")] bool persist = false,
        CancellationToken cancel = default) =>
        Read("took a checkpoint", s =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return new JsonObject
                {
                    ["checkpoints"] = new JsonArray(_checkpoints.Select(kv => (JsonNode)new JsonObject { ["name"] = kv.Key, ["note"] = kv.Value.Note, ["taken"] = kv.Value.At.ToString("HH:mm:ss") }).ToArray()),
                };
            _checkpoints[name.Trim()] = (CircuitSerializer.ToJson(s.Circuit), note, DateTime.Now);
            _ = persist;
            return new JsonObject { ["checkpoint"] = name.Trim(), ["parts"] = s.Circuit.Components.Count };
        }, cancel);

    [McpServerTool(Name = "diff", ReadOnly = true, Idempotent = true)]
    [Description("What has changed since a checkpoint: parts added, removed and changed (by designator), wires added and removed, and whether the probes or anything else (sheets, parameters, the title) changed.")]
    public Task<string> Diff(
        [Description("The checkpoint's name.")] string since,
        CancellationToken cancel = default) =>
        Read("compared with a checkpoint", s => Compare(CheckpointNamed(since), CircuitSerializer.ToJson(s.Circuit)), cancel);

    [McpServerTool(Name = "render_checkpoint", ReadOnly = true, Idempotent = true)]
    [Description("A picture of the schematic as it was at a checkpoint.")]
    public async Task<CallToolResult> RenderCheckpoint(
        [Description("The checkpoint's name.")] string name,
        [Description("About how wide the picture should be, in pixels, from 80 to 1600. Default 320.")] int width = 320,
        [Description("About how high, in pixels. Default 240.")] int height = 240,
        CancellationToken cancel = default)
    {
        if (width is < 80 or > 1600 || height is < 60 or > 1600) throw new McpException("width is from 80 to 1600 pixels and height from 60 to 1600.");
        var png = await Run("drew a checkpoint", _ =>
        {
            var circuit = CircuitSerializer.FromJson(CheckpointNamed(name)).Circuit;
            if (circuit.Components.Count == 0) throw new InvalidOperationException("The circuit was empty at that checkpoint: there's nothing to draw.");
            return host.Library.Picture(circuit, circuit.Sheets.FirstOrDefault(), Math.Clamp(width / 600.0, 0.5, 4));
        }, cancel).ConfigureAwait(false);
        return new CallToolResult { Content = [ImageContentBlock.FromBytes(png, "image/png")] };
    }

    [McpServerTool(Name = "restore_checkpoint", Destructive = true)]
    [Description("Puts the whole circuit back as it was at a checkpoint, as one undo step (so undo brings back what was there).")]
    public Task<string> RestoreCheckpoint(
        [Description("The checkpoint's name.")] string name,
        CancellationToken cancel = default) =>
        Read("went back to a checkpoint", s =>
        {
            var json = CheckpointNamed(name);
            var changes = Compare(json, CircuitSerializer.ToJson(s.Circuit));
            if (changes["changed"]?.GetValue<bool>() == false) return new JsonObject { ["restored"] = name, ["changed"] = false };
            using (s.Step($"AI: back to {name}")) s.Restore(json);
            return new JsonObject { ["restored"] = name, ["changed"] = true, ["undone"] = changes };
        }, cancel);

    private string CheckpointNamed(string name) =>
        _checkpoints.TryGetValue(name.Trim(), out var c) ? c.Json
        : throw new ArgumentException($"There's no checkpoint \"{name}\"" + (_checkpoints.Count > 0 ? $". The checkpoints: {string.Join(", ", _checkpoints.Keys)}." : ": checkpoint takes one."));

    /// <summary>How the circuit in <paramref name="now"/> differs from <paramref name="was"/> (both its file's JSON).</summary>
    internal static JsonObject Compare(string was, string now)
    {
        static JsonObject Parse(string json)
        {
            var o = JsonNode.Parse(json)!.AsObject();
            o.Remove("SavedUtc");
            return o;
        }
        static JsonArray List(JsonObject o, string key) => o[key] as JsonArray ?? [];
        var (a, b) = (Parse(was), Parse(now));
        static Dictionary<string, string> Parts(JsonObject o) =>
            List(o, "Components").OfType<JsonObject>().GroupBy(p => (string?)p["Name"] ?? "").ToDictionary(g => g.Key, g => g.First().ToJsonString());
        var (pa, pb) = (Parts(a), Parts(b));
        var wa = List(a, "Wires").Select(w => w!.ToJsonString()).ToList();
        var wb = List(b, "Wires").Select(w => w!.ToJsonString()).ToList();
        var probes = List(a, "Probes").ToJsonString() != List(b, "Probes").ToJsonString();
        var rest = new[] { a, b }.Select(o =>
        {
            var c = o.DeepClone().AsObject();
            foreach (var k in new[] { "Components", "Wires", "Probes" }) c.Remove(k);
            return c.ToJsonString();
        }).Distinct().Count() > 1;
        return new JsonObject
        {
            ["changed"] = a.ToJsonString() != b.ToJsonString(),
            ["parts"] = new JsonObject
            {
                ["added"] = new JsonArray(pb.Keys.Where(k => !pa.ContainsKey(k)).Select(k => (JsonNode)k).ToArray()),
                ["removed"] = new JsonArray(pa.Keys.Where(k => !pb.ContainsKey(k)).Select(k => (JsonNode)k).ToArray()),
                ["changed"] = new JsonArray(pb.Keys.Where(k => pa.TryGetValue(k, out var v) && v != pb[k]).Select(k => (JsonNode)k).ToArray()),
            },
            ["wires"] = new JsonObject { ["added"] = wb.Except(wa).Count(), ["removed"] = wa.Except(wb).Count() },
            ["probes_changed"] = probes,
            ["other_changed"] = rest,
        };
    }
}
