using static Cirq.Chat.Tests.FakeClaude;

namespace Cirq.Chat.Tests;

/// <summary>What the chat uses: tokens as the API reports them, their estimated cost, the turn's line, the totals and the budget.</summary>
public class UsageTests
{
    private static readonly object Resistor = new { parts = new object[] { new { kind = "Resistor", settings = new { Resistance = "10k" } } } };

    /// <summary>Each fake reply: 100 fresh, 1000 written to the cache, 5000 read from it, 20 out; at Opus 5.5's prices $0.0068.</summary>
    private static readonly (int, int, int, int) Tokens = (100, 1000, 5000, 20);
    private const double PerRequest = (100 * 4 + 20 * 20 + 1000 * 5 + 5000 * 0.2) / 1e6;

    [Fact]
    public async Task A_turns_requests_are_added_up_live_shown_after_it_and_kept_in_the_totals()
    {
        var api = new FakeClaude { Tokens = Tokens }
            .Reply(Say("A resistor."), Call("toolu_1", "add_parts", Resistor))
            .Reply(Call("toolu_2", "render_schematic", new { scale = 1 }))
            .Reply(Say("There it is."));
        await using var rig = await Rig.Start(api);
        var chat = rig.Chat;
        chat.Ledger = new UsageLedger();
        int changes = 0;
        bool sawLive = false;
        chat.UsageChanged += () => { changes++; if (chat.Working && chat.TurnTokens.Output > 0) sawLive = true; };
        await rig.Send("place a resistor and show me");

        // Three requests, each counted as the API reported it, while it ran and after.
        Assert.True(sawLive);
        Assert.True(changes >= 6, $"{changes}");
        var used = chat.ChatTokens;
        Assert.Equal(new TokenUsage(300, 60, 3000, 15000, 3), used);
        Assert.Equal(3 * PerRequest, chat.ChatCost, 9);
        // The line after the turn: its requests, the picture that went to Claude with the third, the estimate.
        var line = Assert.IsType<UsageEntry>(chat.Entries[^1]);
        Assert.Equal(3, line.Requests.Count);
        Assert.Equal(1, line.Pictures);
        Assert.Equal([0, 0, 1], line.Requests.Select(r => r.Pictures));
        Assert.Equal("This turn: 3 requests · 18.3k in (15.0k from cache) · 60 out · 1 picture · ≈ $0.020", line.Line);
        Assert.Contains("Request 3:", line.Details);
        Assert.Contains("1.0k written to the cache", line.Details);
        Assert.Contains("Anthropic Console", line.Details);
        // Today's total has it.
        var today = chat.Ledger.Day(DateOnly.FromDateTime(DateTime.Now));
        Assert.Equal(used, today.Tokens);
        Assert.Equal(3 * PerRequest, today.Cost, 9);

        // Another message adds to the chat; prices of the person's own change the estimate; New Chat starts from nothing.
        api.Reply(Say("You're welcome."));
        await rig.Send("thanks");
        Assert.Equal(4 * PerRequest, chat.ChatCost, 9);
        Assert.Equal(2, chat.Entries.OfType<UsageEntry>().Count());
        chat.OwnPrices = [new ModelPrice(chat.Model, 8, 40, 10, 0.4)];
        Assert.Equal(2 * PerRequest, chat.Price!.Cost(new TokenUsage(100, 20, 1000, 5000)), 9);
        chat.NewChat();
        Assert.True(chat.ChatTokens.IsEmpty);
        Assert.Equal(0, chat.ChatCost);
        Assert.Equal(4 * PerRequest, chat.Ledger.Day(DateOnly.FromDateTime(DateTime.Now)).Cost, 9);
    }

    [Fact]
    public async Task The_budget_stops_a_chat_between_requests_and_another_message_carries_it_on()
    {
        var api = new FakeClaude { Tokens = Tokens };
        for (int i = 0; i < 4; i++) api.Reply(Call($"toolu_{i}", "get_circuit", new { }));
        api.Reply(Say("Done."));
        await using var rig = await Rig.Start(api);
        // About one and a half requests' worth: the second request goes past it, so a third isn't sent.
        rig.Chat.Budget = 0.01;
        await rig.Send("look around");
        Assert.Equal(2, api.Requests.Count);
        var notice = rig.Chat.Entries.OfType<NoticeEntry>().Last();
        Assert.Contains("budget", notice.Text);
        Assert.Contains("$0.014", notice.Text);
        Assert.Contains("Send another message", notice.Text);
        Assert.IsType<UsageEntry>(rig.Chat.Entries[^1]);
        // The last tool call's result waits in the person's turn, so the conversation can go on.
        Assert.True(rig.Chat.Turns[^1].User);
        // Carrying on allows as much again: two more requests, then it stops again.
        await rig.Send("carry on");
        Assert.Equal(4, api.Requests.Count);
        Assert.Equal(2, rig.Chat.Entries.OfType<NoticeEntry>().Count(n => n.Text.Contains("budget")));
        // No budget: it goes on to the end.
        rig.Chat.Budget = null;
        await rig.Send("finish");
        Assert.Equal(0, api.Left);
        Assert.Equal("Done.", rig.Chat.Entries.OfType<AssistantEntry>().Last().Text);
    }

    [Fact]
    public async Task A_request_that_fails_counts_nothing_and_adds_no_line()
    {
        var api = new FakeClaude().Error(529, "overloaded_error", "Overloaded");
        await using var rig = await Rig.Start(api);
        await rig.Send("hello");
        Assert.True(rig.Chat.ChatTokens.IsEmpty);
        Assert.DoesNotContain(rig.Chat.Entries, e => e is UsageEntry);
    }

    [Fact]
    public void The_ledger_keeps_days_in_its_file_and_prices_and_counts_read_plainly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Cirq.Chat.Tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "chat-usage.json");
            var ledger = new UsageLedger(path);
            var day1 = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
            ledger.Add(day1, new TokenUsage(1000, 100, 0, 0, 1), 0.5);
            ledger.Add(day1.AddDays(1), new TokenUsage(2000, 200, 0, 0, 1), 1.25);
            ledger.Add(day1.AddMonths(1), new TokenUsage(5, 5, 0, 0, 1), null);
            var back = new UsageLedger(path);
            Assert.Equal(1.75, back.Month(2026, 11).Cost, 9);
            Assert.Equal(new TokenUsage(3000, 300, 0, 0, 2), back.Month(2026, 11).Tokens);
            Assert.Equal(0, back.Month(2026, 12).Cost);
            Assert.Equal(10, back.Month(2026, 12).Tokens.AllInput + back.Month(2026, 12).Tokens.Output);
            back.Reset();
            Assert.True(new UsageLedger(path).Month(2026, 11).Tokens.IsEmpty);
            // A broken file starts afresh.
            File.WriteAllText(path, "{ not json");
            Assert.True(new UsageLedger(path).Month(2026, 11).Tokens.IsEmpty);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        Assert.Equal(("950", "12.4k", "1.20M"), (TokenUsage.Short(950), TokenUsage.Short(12_400), TokenUsage.Short(1_200_000)));
        Assert.Equal(("$0.042", "$1.37", "$128"), (ChatPricing.Dollars(0.042), ChatPricing.Dollars(1.37), ChatPricing.Dollars(128.4)));
        Assert.Equal(4, ChatPricing.Find(ChatModels.Opus)!.Input);
        Assert.Null(ChatPricing.Find("some-other-model"));
        Assert.Contains("input", Assert.Throws<ArgumentException>(() => new ModelPrice("m", -1, 0, 0, 0).Checked()).Message);
    }
}
