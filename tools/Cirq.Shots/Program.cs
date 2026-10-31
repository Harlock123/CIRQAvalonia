using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Anthropic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Cirq.Mcp;
using Cirq.UI;
using Cirq.UI.Services;
using Cirq.UI.ViewModels;
using Cirq.UI.Views;

// Draws the guide's screenshots of the AI Chat: the real editor, in a headless window rendered by Skia, with Claude's
// side of the conversation replayed from a script. The tool calls are real (they run through the app's own MCP
// server against the window's circuit) and so are the numbers Claude quotes at the end: they are read from the
// results. Usage: dotnet run --project tools/Cirq.Shots -- <folder>
var folder = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine("docs", "images"));
Directory.CreateDirectory(folder);
var scratch = Directory.CreateTempSubdirectory("cirq-shots-").FullName;

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

var app = (App)Application.Current!;
app.SettingsStore = new JsonSettingsStore(Path.Combine(scratch, "settings.json"));
ThemeManager.Apply(AppTheme.Dark, app);

var editor = new MainWindowViewModel { Autosave = new AutosaveStore(Path.Combine(scratch, "recovery.cirq")) };
var window = new MainWindow { DataContext = editor, Width = 1680, Height = 1000, ChatRoot = scratch };
window.Show();
Pump();

var claude = new Replay();
editor.IsChatOpen = true;
Pump();
var chat = window.Chat!;
chat.ClientFactory = _ => new AnthropicClient { ApiKey = "sk-ant-replay-0000", HttpClient = new HttpClient(claude), MaxRetries = 0 };
chat.Keys.Override = "sk-ant-replay-0000";
chat.RefreshIntro();

// ── A 555 astable, built, checked and run ──
var astable = Calls(Recipes.Astable555());
claude.Say("I'll build a classic 555 astable for about 1 kHz: RA = 1 kΩ, RB ≈ 6.7 kΩ and a 100 nF timing capacitor, with an LED and its resistor on the output so you can see it working.")
      .Then(astable.Where(c => c.Tool is "new_circuit" or "add_parts").ToArray())
      .Then(astable.Where(c => c.Tool == "connect").ToArray())
      .Then(astable.Where(c => c.Tool is "add_probe").ToArray())
      .Then(astable.Where(c => c.Tool is "check_circuit" or "render_schematic").ToArray())
      .Then(astable.Where(c => c.Tool == "run_transient").ToArray())
      .Then(("set_scope", new JsonObject { ["timebase"] = "500u", ["volts_per_division"] = 2 }))
      .Finally(results =>
      {
          var run = JsonNode.Parse(results.Last(r => r.Contains("\"probes\"")))!;
          var output = run["probes"]!.AsArray().First(p => (string)p!["probe"]! == "Output")!;
          var capacitor = run["probes"]!.AsArray().First(p => (string)p!["probe"]! == "Capacitor")!;
          return $"It's running at {output["frequency"]} with a {output["duty_cycle"]} duty cycle, close to the 1 kHz asked for. " +
                 $"The timing capacitor ramps between {capacitor["min"]} and {capacitor["max"]}, a third and two thirds of the 9 V supply, as a 555 should, " +
                 "and the duty cycle is over half because C charges through RA and RB but discharges through RB alone. Press F5 to watch it run live.";
      });
Ask("Build a 555 astable at about 1 kHz with an LED on its output, run it and show me the waveforms.");
editor.ZoomToFitCommand.Execute(null);
Shot("35-ai-chat.png");

// ── An RC low-pass and its frequency response ──
var filter = Calls(Recipes.RcLowPass("1k"));
claude.Say("An RC low-pass with its corner at 1 kHz: 10 kΩ in series and 15.92 nF to ground, since f = 1 / 2πRC. I'll build it, then sweep it.")
      .Then(filter.Where(c => c.Tool is "new_circuit" or "add_parts").ToArray())
      .Then(filter.Where(c => c.Tool == "connect").ToArray())
      .Then(filter.Where(c => c.Tool == "add_probe").ToArray())
      .Then(filter.Where(c => c.Tool == "run_transient").ToArray())
      .Then(("set_scope", new JsonObject { ["timebase"] = "1m", ["volts_per_division"] = 1 }))
      .Then(filter.Where(c => c.Tool == "ac_sweep").ToArray())
      .Finally(results =>
      {
          var sweep = JsonNode.Parse(results.Last(r => r.Contains("corner_minus_3db")))!;
          var output = sweep["probes"]!.AsArray().First(p => (string)p!["probe"]! == "Output")!;
          return $"The −3 dB corner comes out at {output["corner_minus_3db"]}, as designed. In time, the 333 Hz square comes out with its corners rounded off; in frequency, above the corner the output falls 20 dB a decade " +
                 $"({output["gain_db_at_stop"]} dB by {sweep["to"]}) and the phase heads for −90°.";
      });
chat.Session.NewChat();
Ask("Now design an RC low-pass filter at 1 kHz and show me its frequency response.");
editor.ZoomToFitCommand.Execute(null);
// The sweep's line opened, to show the Bode plot Claude looked at.
var line = chat.GetLogicalDescendants().OfType<Button>().Last(b => b.Content is TextBlock t && (t.Text ?? "").Contains("ac_sweep"));
line.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
Pump();
Shot("36-ai-chat-bode.png");

window.Close();
Pump();
Directory.Delete(scratch, true);
return;

void Ask(string text)
{
    chat.Input.Text = text;
    chat.Send();
    Pump(chat.Running);
}

void Pump(Task? task = null)
{
    for (var i = 0; i < 60000 && (task is null ? i < 20 : !task.IsCompleted); i++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(2);
    }
    if (task is { IsCompleted: false }) throw new TimeoutException("The chat didn't finish.");
    task?.GetAwaiter().GetResult();
}

void Shot(string name)
{
    for (var i = 0; i < 40; i++) Pump();
    var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was drawn.");
    frame.Save(Path.Combine(folder, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    Console.WriteLine($"Wrote {Path.Combine(folder, name)}");
}

// A recipe's calls: each "→ tool {arguments}" line.
static List<(string Tool, JsonObject Args)> Calls(string recipe)
{
    var calls = Regex.Matches(recipe, @"^\s*→ (\w+) (\{.*\})$", RegexOptions.Multiline)
        .Select(m => (Tool: m.Groups[1].Value, Args: (JsonObject)JsonNode.Parse(m.Groups[2].Value)!)).ToList();
    // The window has a circuit open already; a new one replaces it.
    foreach (var call in calls.Where(c => c.Tool == "new_circuit")) call.Args["discard_changes"] = true;
    return calls;
}

/// <summary>
/// Claude's side of a conversation, replayed: an opening sentence with the first calls, then a turn of calls per
/// request, then a closing sentence worked out from the tool results it was sent. Answers as the Messages API
/// streams, so the chat shows it exactly as it would a real reply.
/// </summary>
internal sealed class Replay : HttpMessageHandler
{
    private readonly Queue<Func<List<string>, (string? Text, (string Tool, JsonObject Args)[] Calls)>> _turns = new();
    private string? _opening;
    private int _id;

    public Replay Say(string text)
    {
        _opening = text;
        return this;
    }

    public Replay Then(params (string Tool, JsonObject Args)[] calls)
    {
        var text = _opening;
        _opening = null;
        _turns.Enqueue(_ => (text, calls));
        return this;
    }

    public Replay Finally(Func<List<string>, string> text)
    {
        _turns.Enqueue(results => (text(results), []));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancel))!;
        var results = body["messages"]!.AsArray()
            .SelectMany(m => m!["content"] is JsonArray a ? a.OfType<JsonObject>() : [])
            .Where(b => (string?)b["type"] == "tool_result")
            .Select(b => b["content"] is JsonArray c ? string.Join("\n", c.OfType<JsonObject>().Where(x => (string?)x["type"] == "text").Select(x => (string?)x["text"])) : "")
            .ToList();
        var (text, calls) = _turns.Dequeue()(results);
        var sse = new StringBuilder();
        void Event(string type, JsonObject data)
        {
            data["type"] = type;
            sse.Append("event: ").Append(type).Append("\ndata: ").Append(data.ToJsonString()).Append("\n\n");
        }
        Event("message_start", new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["id"] = $"msg_{++_id}", ["type"] = "message", ["role"] = "assistant", ["model"] = "claude-opus-5-5",
                ["content"] = new JsonArray(), ["stop_reason"] = null, ["stop_sequence"] = null,
                ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 },
            },
        });
        var index = 0;
        if (text != null)
        {
            Event("content_block_start", new JsonObject { ["index"] = index, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" } });
            Event("content_block_delta", new JsonObject { ["index"] = index, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text } });
            Event("content_block_stop", new JsonObject { ["index"] = index++ });
        }
        foreach (var (tool, args) in calls)
        {
            Event("content_block_start", new JsonObject { ["index"] = index, ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = $"toolu_{_id}_{index}", ["name"] = tool, ["input"] = new JsonObject() } });
            Event("content_block_delta", new JsonObject { ["index"] = index, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = args.ToJsonString() } });
            Event("content_block_stop", new JsonObject { ["index"] = index++ });
        }
        Event("message_delta", new JsonObject
        {
            ["delta"] = new JsonObject { ["stop_reason"] = calls.Length > 0 ? "tool_use" : "end_turn", ["stop_sequence"] = null },
            ["usage"] = new JsonObject { ["output_tokens"] = 1 },
        });
        Event("message_stop", new JsonObject());
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream") };
    }
}
