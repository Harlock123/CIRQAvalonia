using Cirq.Mcp;

namespace Cirq.Chat;

/// <summary>The Claude models the chat offers, by their API ids.</summary>
public static class ChatModels
{
    public const string Opus = "claude-opus-5-5";
    public const string Sonnet = "claude-sonnet-5-5";
    public const string Haiku = "claude-haiku-4-5-20251001";

    /// <summary>The model a new chat starts with.</summary>
    public const string Default = Opus;

    /// <summary>The choices, most capable first, with a name to show.</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> All =
    [
        (Opus, "Claude Opus 5.5"),
        (Sonnet, "Claude Sonnet 5.5"),
        (Haiku, "Claude Haiku 4.5"),
    ];

    public static string Label(string id) => All.FirstOrDefault(m => m.Id == id).Label ?? id;
}

/// <summary>What Claude is told: the MCP server's own instructions, and a few lines about working inside the app.</summary>
public static class ChatPrompt
{
    public const string InApp = """
        You are working inside CirqAvalonia itself, in its AI Chat panel, with the person watching. Your tool calls change the circuit open in the window as you make them, the canvas redraws as you go, a run's traces appear in the scope, and the person's Ctrl+Z undoes your steps one at a time. Talk to them briefly and plainly: say what you're about to build, build it, check it (render_schematic for the drawing; operating_point, run_transient or ac_sweep for what it does), then say what you made and what the numbers show. Keep it to a few sentences; the tool calls show in the panel on their own. When the circuit does something worth watching live (an oscillator, a counter, LEDs), you can start the editor's own simulation with transport run and set the scope with set_scope. If the person is busy in the app (a dialog open, a drag under way), calls wait for them. Files you open or save are in the working folder.
        """;

    /// <summary>The system prompt: the same instructions every MCP agent gets, then <see cref="InApp"/>.</summary>
    public static string System => CircuitServer.Instructions.TrimEnd() + "\n\n" + InApp.TrimEnd();
}
