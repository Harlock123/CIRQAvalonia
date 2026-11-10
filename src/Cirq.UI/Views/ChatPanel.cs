using System.Globalization;
using Anthropic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Cirq.Chat;
using Cirq.UI.Services;

namespace Cirq.UI.Views;

/// <summary>
/// The AI Chat panel on the right of the window (F12, or AI › AI Chat): the conversation with Claude, each tool call
/// as a compact line that opens to its input and result, small pictures of the schematics and plots Claude looked at,
/// and below them the input box (Enter sends, Shift+Enter starts a new line), Send (Stop while Claude works), the
/// model and Auto-continue. Under those, a line says what this chat has used: tokens as the API reports them and an
/// estimate in dollars, live while Claude works, with today's and this month's totals in its tooltip; each turn ends
/// with a small line of its own, and Cost… sets the prices and a budget. The conversation itself is <see cref="ChatSession"/>'s; this is only its controls.
/// </summary>
public sealed class ChatPanel : DockPanel
{
    private readonly ApiKeyStore _keys;
    private readonly Action<string> _status;
    private readonly AppSettings? _settings;
    private readonly Action? _saveSettings;
    private readonly StackPanel _messages = new() { Spacing = 6, Margin = new Thickness(8) };
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _intro = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.9 };
    private readonly ComboBox _model;
    private readonly Button _send;
    private (string Key, IAnthropicClient Client)? _client;
    private readonly StackPanel _cost = new() { Spacing = 6 };
    private UsageLedger? _ledger;

    /// <summary>What this chat has used so far: the line under the input box.</summary>
    public TextBlock UsageText { get; } = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    /// <summary>Cost…'s budget: the dollars a chat may use before it stops and asks; empty for no limit.</summary>
    public TextBox BudgetBox { get; } = new() { FontSize = 12, Width = 80, PlaceholderText = "none" };

    /// <summary>Cost…'s prices, a box for each model: four numbers of dollars a million tokens.</summary>
    public Dictionary<string, TextBox> PriceBoxes { get; } = [];

    /// <summary>Today's and this month's totals, as Cost… shows them.</summary>
    public TextBlock Totals { get; } = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };

    /// <summary>Where the totals are kept (the app's one ledger), or null to keep none.</summary>
    public UsageLedger? Ledger
    {
        get => _ledger;
        set
        {
            if (_ledger != null) _ledger.Changed -= ShowUsageSoon;
            _ledger = value;
            Session.Ledger = value;
            if (value != null) value.Changed += ShowUsageSoon;
            ShowUsage();
        }
    }

    /// <summary>How many times a request carries on by itself, with Auto-continue ticked: 600 tool calls in all.</summary>
    public const int AutoContinueTimes = 9;

    /// <summary>Auto-continue: carry on after the tool-call limit without waiting for "continue".</summary>
    public CheckBox AutoContinue { get; } = new() { Content = "Auto-continue", FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    public ChatSession Session { get; }

    /// <summary>Where the key comes from (tests set one for the run only).</summary>
    public ApiKeyStore Keys => _keys;

    /// <summary>The input box.</summary>
    public TextBox Input { get; } = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, MaxHeight = 160, FontSize = 13,
        PlaceholderText = "Ask Claude to build or test a circuit… (Enter sends)",
    };

    /// <summary>Makes the API client for a key (tests give one on a fake API).</summary>
    public Func<string, IAnthropicClient> ClientFactory { get; set; } = key => new AnthropicClient { ApiKey = key };

    /// <summary>The request under way, if any (tests wait for it).</summary>
    public Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>Raised by the panel's close button.</summary>
    public event Action? CloseRequested;

    public ChatPanel(IToolBridge tools, ApiKeyStore keys, Action<string> status, AppSettings? settings = null, Action? saveSettings = null)
    {
        _keys = keys;
        _status = status;
        _settings = settings;
        _saveSettings = saveSettings;
        Session = new ChatSession(tools);
        Themed(this, BackgroundProperty, "PanelBackground");
        Themed(_intro, TextBlock.ForegroundProperty, "TextSecondary");

        var title = new TextBlock { Text = "AI CHAT", FontSize = 11, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0) };
        Themed(title, TextBlock.ForegroundProperty, "TextMuted");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, 4, 0) };
        buttons.Children.Add(Small("New Chat", NewChat, "Forget this conversation and start again"));
        buttons.Children.Add(Small("Key…", () => _ = EditKey(), "Your Anthropic API key"));
        buttons.Children.Add(Small("Cost…", () => _ = ShowCost(), "What the chat has used: a budget, the prices it's estimated at, and today's and this month's totals"));
        buttons.Children.Add(Small("✕", () => CloseRequested?.Invoke(), "Hide the chat (F12)"));
        var header = new DockPanel { Height = 34 };
        SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(title);
        SetDock(header, Dock.Top);
        Children.Add(header);

        _model = new ComboBox { ItemsSource = ChatModels.All.Select(m => m.Label).ToList(), Width = 160, FontSize = 12 };
        var chosen = ChatModels.All.ToList().FindIndex(m => m.Id == settings?.ChatModel);
        _model.SelectedIndex = chosen < 0 ? 0 : chosen;
        Session.Model = ChatModels.All[_model.SelectedIndex].Id;
        _model.SelectionChanged += (_, _) =>
        {
            if (_model.SelectedIndex < 0) return;
            Session.Model = ChatModels.All[_model.SelectedIndex].Id;
            if (_settings is null) return;
            _settings.ChatModel = Session.Model;
            _saveSettings?.Invoke();
        };
        ToolTip.SetTip(_model, "The Claude model. Opus is the most capable; Sonnet and Haiku are quicker and cost less.");
        _send = new Button { Content = "Send", Width = 72, HorizontalContentAlignment = HorizontalAlignment.Center };
        _send.Click += (_, _) => SendOrStop();
        ToolTip.SetTip(_send, "Send (Enter); Stop while Claude is working");
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        SetDock(_send, Dock.Right);
        row.Children.Add(_send);
        SetDock(_model, Dock.Left);
        row.Children.Add(_model);
        row.Children.Add(AutoContinue);
        Themed(UsageText, TextBlock.ForegroundProperty, "TextSecondary");
        var bottom = new StackPanel { Margin = new Thickness(8), Children = { Input, row, UsageText } };
        SetDock(bottom, Dock.Bottom);
        Children.Add(bottom);

        _scroll = new ScrollViewer { Content = _messages, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var well = new Border { BorderThickness = new Thickness(0, 1), Child = _scroll };
        Themed(well, Border.BackgroundProperty, "AppBackground");
        Themed(well, Border.BorderBrushProperty, "PanelBorder");
        Children.Add(well);
        _messages.Children.Add(_intro);

        // Enter sends; Shift+Enter is a new line (the box handles that itself).
        Input.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                e.Handled = true;
                if (!Session.Working) Send();
            }
        }, RoutingStrategies.Tunnel);

        Session.EntryAdded += entry => { _messages.Children.Add(View(entry)); ScrollToEnd(); };
        Session.Cleared += () => { _messages.Children.Clear(); _messages.Children.Add(_intro); };
        Session.UsageChanged += ShowUsage;
        Session.WorkingChanged += () =>
        {
            _send.Content = Session.Working ? "Stop" : "Send";
            _status(Session.Working ? "Claude is working…" : "Claude has finished.");
            ShowUsage();
        };
        AutoContinue.IsCheckedChanged += (_, _) =>
        {
            var on = AutoContinue.IsChecked == true;
            Session.AutoContinues = on ? AutoContinueTimes : 0;
            if (_settings is null || _settings.ChatAutoContinue == on) return;
            _settings.ChatAutoContinue = on;
            _saveSettings?.Invoke();
        };
        AutoContinue.IsChecked = settings?.ChatAutoContinue ?? true;
        Session.AutoContinues = AutoContinue.IsChecked == true ? AutoContinueTimes : 0;
        ToolTip.SetTip(AutoContinue, $"When Claude has made {Session.MaxToolCalls} tool calls for one request, carry on by itself instead of waiting for you to say \"continue\" (up to {Session.MaxToolCalls * (AutoContinueTimes + 1)} calls). Stop ends it at any time.");
        BuildCost();
        LoadCost();
        RefreshIntro();
        ShowUsage();
    }

    // ───────────────────────────── what it uses ─────────────────────────────

    /// <summary>Cost…'s contents: the budget, each model's prices, today's and this month's totals, and Reset.</summary>
    private void BuildCost()
    {
        TextBlock Words(string text, double size = 12)
        {
            var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Themed(t, TextBlock.ForegroundProperty, "TextPrimary");
            return t;
        }
        Themed(Totals, TextBlock.ForegroundProperty, "TextPrimary");
        _cost.Children.Add(Words("The chat shows what it uses as it goes: the tokens the API reports, and an estimate in dollars from the prices below. The Anthropic Console has the bill. Each tool call sends the conversation again, so a turn is often many requests; the prompt cache makes most of each one cheap."));
        ToolTip.SetTip(BudgetBox, "When a chat has used about this many dollars, it stops between requests and says so; sending another message lets it carry on for as much again. Empty for no limit.");
        BudgetBox.LostFocus += (_, _) => ReadBudget();
        _cost.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { Words("Stop a chat at about $"), BudgetBox } });
        _cost.Children.Add(Words($"Prices, $ a million tokens: input, output, cache write, cache read (Anthropic's as of {ChatPricing.AsOf}; change them if they change, or empty a box for the built-in ones)", 11));
        foreach (var (id, label) in ChatModels.All)
        {
            var box = new TextBox { FontSize = 12, Width = 190 };
            ToolTip.SetTip(box, $"{label}'s prices in dollars a million tokens: input, output, cache write, cache read. Empty puts back the built-in ones.");
            box.LostFocus += (_, _) => ReadPrice(id, box);
            PriceBoxes[id] = box;
            var name = Words(label);
            name.Width = 140;
            _cost.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { name, box } });
        }
        _cost.Children.Add(Totals);
        var reset = new Button { Content = "Reset Totals", HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) => { _ledger?.Reset(); _status("The chat's usage totals are reset."); };
        ToolTip.SetTip(reset, "Forget today's and this month's totals");
        _cost.Children.Add(reset);
    }

    /// <summary>The Cost… dialog.</summary>
    private async Task ShowCost()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        LoadCost();
        ShowUsage();
        var close = new Button { Name = "CloseButton", Content = "Close", Width = 90, HorizontalContentAlignment = HorizontalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        var holder = new Border { Child = _cost };
        var window = new Window
        {
            Title = "AI Chat Cost", Width = 520, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border { Padding = new Thickness(16), Child = new StackPanel { Spacing = 12, Children = { holder, close } } },
        };
        Themed(window, BackgroundProperty, "PanelBackground");
        close.Click += (_, _) => window.Close();
        // The boxes keep what was typed in them when the window closes with one still focused.
        window.Closing += (_, _) => { ReadBudget(); foreach (var (id, box) in PriceBoxes) ReadPrice(id, box); };
        await window.ShowModal(owner);
        holder.Child = null;
    }

    private static string PriceText(ModelPrice p) => string.Join(" ", new[] { p.Input, p.Output, p.CacheWrite, p.CacheRead }.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)));

    /// <summary>The person's own prices from the settings, as the session uses them (a broken one is left out).</summary>
    private List<ModelPrice> OwnPrices() => (_settings?.ChatPrices ?? [])
        .Where(kv => kv.Value is { Length: 4 })
        .Select(kv => new ModelPrice(kv.Key, kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]))
        .Where(p => { try { p.Checked(); return true; } catch (ArgumentException) { return false; } })
        .ToList();

    /// <summary>Takes a model's prices from its box: four numbers, or empty for the built-in ones.</summary>
    public void ReadPrice(string model, TextBox box)
    {
        var text = box.Text?.Trim() ?? "";
        var own = _settings?.ChatPrices ?? [];
        var before = own.TryGetValue(model, out var was) ? was : null;
        if (text.Length == 0) own.Remove(model);
        else
        {
            try
            {
                var v = text.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => double.Parse(x.TrimStart('$'), NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
                if (v.Length != 4) throw new FormatException();
                new ModelPrice(model, v[0], v[1], v[2], v[3]).Checked();
                // The built-in prices typed back in aren't kept as the person's own.
                if (ChatPricing.Defaults.FirstOrDefault(d => d.Model == model) is { } d && PriceText(d) == PriceText(new ModelPrice(model, v[0], v[1], v[2], v[3]))) own.Remove(model);
                else own[model] = v;
            }
            catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
            {
                _status($"{ChatModels.Label(model)}'s prices are four numbers of dollars a million tokens: input, output, cache write, cache read.");
            }
        }
        var after = own.TryGetValue(model, out var now) ? now : null;
        if (!(before == null && after == null) && (before == null || after == null || !before.SequenceEqual(after))) _saveSettings?.Invoke();
        Session.OwnPrices = OwnPrices();
        box.Text = PriceText(ChatPricing.Find(model, Session.OwnPrices)!);
        ShowUsage();
    }

    /// <summary>Takes the budget from its box: dollars above 0, or empty for no limit.</summary>
    public void ReadBudget()
    {
        var text = BudgetBox.Text?.Trim().TrimStart('$') ?? "";
        double? budget = Session.Budget;
        if (text.Length == 0) budget = null;
        else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double b) && b > 0 && b < 1e6) budget = b;
        else _status("The budget is a number of dollars above 0, or empty for no limit.");
        Session.Budget = budget;
        if (_settings != null && _settings.ChatBudget != budget)
        {
            _settings.ChatBudget = budget;
            _saveSettings?.Invoke();
        }
        BudgetBox.Text = budget is { } x ? x.ToString("0.##", CultureInfo.InvariantCulture) : "";
    }

    /// <summary>Takes up the prices and budget from the settings.</summary>
    private void LoadCost()
    {
        Session.OwnPrices = OwnPrices();
        if (_settings != null) Session.Budget = _settings.ChatBudget;
        BudgetBox.Text = Session.Budget is { } x ? x.ToString("0.##", CultureInfo.InvariantCulture) : "";
        foreach (var (id, box) in PriceBoxes) box.Text = PriceText(ChatPricing.Find(id, Session.OwnPrices)!);
    }

    private void ShowUsageSoon() => Dispatcher.UIThread.Post(ShowUsage);

    /// <summary>The usage line and the totals, as they are now.</summary>
    public void ShowUsage()
    {
        var chat = Session.ChatTokens;
        if (chat.IsEmpty && chat.Requests == 0) UsageText.Text = "This chat: nothing used yet.";
        else
        {
            var line = $"This chat: {chat.Describe()} · ≈ {ChatPricing.Dollars(Session.ChatCost)}";
            if (Session.Working) line += $" · this turn ≈ {ChatPricing.Dollars(Session.TurnCost)}";
            UsageText.Text = line;
        }
        var now = Session.Clock().LocalDateTime;
        string Total(UsageDay d) => d.Tokens.IsEmpty ? "nothing" : $"{d.Tokens.Describe()} · ≈ {ChatPricing.Dollars(d.Cost)}";
        Totals.Text = _ledger is { } l
            ? $"Today: {Total(l.Day(DateOnly.FromDateTime(now)))}\nThis month: {Total(l.Month(now.Year, now.Month))}"
            : "Totals aren't kept in this window.";
        ToolTip.SetTip(UsageText, $"{Totals.Text}\n\nEstimated from the tokens the API reports and the prices in Cost…; the Anthropic Console has the bill. Each tool call sends the conversation again, so a turn is often many requests; the prompt cache makes most of each one cheap.");
    }

    /// <summary>Says what the chat can do, and how to give it a key if it has none.</summary>
    public void RefreshIntro()
    {
        const string what = "Chat with Claude to build and test circuits in this window: \"build an RC low-pass filter at 1 kHz and show its response\", \"make a 555 blink an LED at 2 Hz\".";
        _intro.Text = _keys.Get() == null
            ? $"{what}\n\nThere's no Anthropic API key yet. Set the {ApiKeyStore.EnvironmentVariable} environment variable before starting CirqAvalonia, or press Key… above to enter one. Your own Anthropic API account is billed for what the chat uses."
            : $"{what} You watch it work; Ctrl+Z undoes its steps. Your Anthropic API account is billed for what it uses.";
    }

    /// <summary>The intro's words (for tests).</summary>
    public string IntroText => _intro.Text ?? "";

    /// <summary>Puts the cursor in the input box.</summary>
    public void FocusInput() => Dispatcher.UIThread.Post(() => Input.Focus(), DispatcherPriority.Background);

    private void SendOrStop()
    {
        if (Session.Working) Session.Stop();
        else Send();
    }

    /// <summary>Sends what's in the input box.</summary>
    public void Send()
    {
        var text = Input.Text?.Trim() ?? "";
        if (text.Length == 0 || Session.Working) return;
        Input.Text = "";
        UseKey();
        Running = Guarded(Session.SendAsync(text));
    }

    private void Retry()
    {
        if (Session.Working) return;
        UseKey();
        Running = Guarded(Session.RetryAsync());
    }

    /// <summary>A request's failure shows as a line in the chat; it never takes the app down.</summary>
    private async Task Guarded(Task request)
    {
        try { await request; }
        catch (Exception e)
        {
            _messages.Children.Add(View(new NoticeEntry(ChatErrors.Describe(e), NoticeKind.Error)));
        }
    }

    /// <summary>The client for the current key (made again only when the key changes), or none.</summary>
    private void UseKey()
    {
        var key = _keys.Get();
        if (key == null) { Session.Client = null; _client = null; return; }
        if (_client is not { } c || c.Key != key) _client = (key, ClientFactory(key));
        Session.Client = _client.Value.Client;
    }

    private void NewChat()
    {
        Session.NewChat();
        RefreshIntro();
        _status("New chat.");
    }

    public void Stop() => Session.Stop();

    private void ScrollToEnd() => Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);

    // ───────────────────────────── the entries ─────────────────────────────

    private Control View(ChatEntry entry) => entry switch
    {
        UserEntry u => Bubble(Words(u.Text, "TextPrimary"), "GroupHeaderOpen", new Thickness(40, 0, 0, 0)),
        AssistantEntry a => AssistantView(a),
        ToolEntry t => ToolView(t),
        NoticeEntry n => NoticeView(n),
        UsageEntry u => UsageView(u),
        _ => new TextBlock(),
    };

    private static SelectableTextBlock Words(string text, string colour)
    {
        var block = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        Themed(block, SelectableTextBlock.ForegroundProperty, colour);
        return block;
    }

    private static Border Bubble(Control child, string background, Thickness margin)
    {
        var bubble = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 6), Margin = margin, Child = child };
        Themed(bubble, Border.BackgroundProperty, background);
        return bubble;
    }

    private Control AssistantView(AssistantEntry a)
    {
        var text = Words(a.Text, "TextPrimary");
        a.Changed += () => { text.Text = a.Text; ScrollToEnd(); };
        return Bubble(text, "PanelBackground", new Thickness(0, 0, 20, 0));
    }

    /// <summary>A tool call: "▸ add_parts Resistor, Capacitor → added 2", which opens to its input, result and pictures.</summary>
    private Control ToolView(ToolEntry t)
    {
        var line = new TextBlock { FontFamily = new FontFamily("monospace"), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        var toggle = new Button { Content = line, Background = Brushes.Transparent, Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        var thumbs = new WrapPanel { Orientation = Orientation.Horizontal };
        var details = new StackPanel { Spacing = 4, IsVisible = false, Margin = new Thickness(14, 2, 0, 4) };
        var box = new StackPanel { Children = { toggle, thumbs, details } };
        void Refresh()
        {
            line.Text = (details.IsVisible ? "▾ " : "▸ ") + t.Line;
            Themed(line, TextBlock.ForegroundProperty, t.IsError ? "TextError" : t.Running ? "TextSecondary" : "SymbolValue");
            ToolTip.SetTip(toggle, t.Running ? "Running…" : "Show the input and result");
            thumbs.Children.Clear();
            foreach (var image in t.Images) if (Picture(image, 160) is { } p) thumbs.Children.Add(p);
            details.Children.Clear();
            if (details.IsVisible)
            {
                details.Children.Add(Detail("Input", t.Input));
                if (!t.Running) details.Children.Add(Detail(t.IsError ? "Error" : "Result", t.Result.Length > 0 ? t.Result : "(no text)"));
                foreach (var image in t.Images) if (Picture(image, 340) is { } p) details.Children.Add(p);
            }
        }
        toggle.Click += (_, _) => { details.IsVisible = !details.IsVisible; Refresh(); };
        t.Changed += Refresh;
        Refresh();
        return box;
    }

    private static Control Detail(string label, string text)
    {
        var heading = new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeight.Bold };
        Themed(heading, TextBlock.ForegroundProperty, "TextSecondary");
        var body = new SelectableTextBlock { Text = text.Length > 4000 ? text[..4000] + "\n…" : text, FontFamily = new FontFamily("monospace"), FontSize = 11, TextWrapping = TextWrapping.Wrap };
        Themed(body, SelectableTextBlock.ForegroundProperty, "TextValue");
        return new StackPanel { Children = { heading, body } };
    }

    /// <summary>Each picture decoded once, however often its line is redrawn.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ToolImage, Bitmap> Decoded = [];

    private static Control? Picture(ToolImage image, double width)
    {
        try
        {
            var bitmap = Decoded.GetValue(image, i => new Bitmap(new MemoryStream(i.Data)));
            var frame = new Border
            {
                BorderThickness = new Thickness(1), Margin = new Thickness(14, 2, 4, 2), HorizontalAlignment = HorizontalAlignment.Left,
                Child = new Image { Source = bitmap, Width = width, Stretch = Stretch.Uniform },
            };
            Themed(frame, Border.BorderBrushProperty, "PanelBorder");
            return frame;
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>A turn's use, a small line after it; its tooltip lists each request.</summary>
    private static Control UsageView(UsageEntry u)
    {
        var text = new TextBlock { Text = u.Line, FontSize = 10, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Right };
        Themed(text, TextBlock.ForegroundProperty, "TextMuted");
        ToolTip.SetTip(text, u.Details);
        return text;
    }

    private Control NoticeView(NoticeEntry n)
    {
        var text = new TextBlock { Text = n.Text, TextWrapping = TextWrapping.Wrap, FontSize = 12, FontStyle = n.Kind == NoticeKind.Info ? FontStyle.Italic : FontStyle.Normal };
        Themed(text, TextBlock.ForegroundProperty, n.Kind == NoticeKind.Error ? "TextError" : "TextSecondary");
        var panel = new StackPanel { Spacing = 4, Children = { text } };
        if (n.CanRetry)
        {
            var retry = new Button { Content = "Retry", HorizontalAlignment = HorizontalAlignment.Left };
            retry.Click += (_, _) => Retry();
            ToolTip.SetTip(retry, "Send the conversation again");
            panel.Children.Add(retry);
            n.Changed += () => retry.IsVisible = n.CanRetry;
        }
        return panel;
    }

    // ───────────────────────────── the key ─────────────────────────────

    /// <summary>The key dialog: enter a key, or remove the saved one. It says where the key is kept.</summary>
    private async Task EditKey()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var box = new TextBox { PasswordChar = '•', PlaceholderText = "sk-ant-…", FontSize = 13 };
        var where = _keys.Path is { } p
            ? $"It's kept in {p}, a file of its own that only your user account can read, and is sent only to the Anthropic API."
            : "It's kept only while the app is running.";
        var now = _keys.Source switch
        {
            KeySource.Environment => $"The {ApiKeyStore.EnvironmentVariable} environment variable is set ({ApiKeyStore.Mask(_keys.Get())}), and is used before a key entered here.",
            KeySource.File => $"A key is saved: {ApiKeyStore.Mask(_keys.Get())}.",
            _ => "No key is set yet.",
        };
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"Paste your Anthropic API key (from console.anthropic.com). {where} Your own API account is billed for what the chat uses.\n\n{now}",
        };
        Themed(text, TextBlock.ForegroundProperty, "TextPrimary");
        var save = new Button { Content = "Save", Width = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var remove = new Button { Content = "Remove Saved Key", IsEnabled = _keys.Path != null && _keys.Source == KeySource.File };
        var cancel = new Button { Name = "CloseButton", Content = "Cancel", Width = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { remove, save, cancel } };
        var window = new Window
        {
            Title = "Anthropic API Key", Width = 480, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border { Padding = new Thickness(16), Child = new StackPanel { Spacing = 12, Children = { text, box, buttons } } },
        };
        Themed(window, BackgroundProperty, "PanelBackground");
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(box.Text)) return;
            try
            {
                _keys.Save(box.Text);
                _status("The API key is saved.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _status("The API key couldn't be saved: the settings folder can't be written.");
            }
            box.Text = "";
            window.Close();
        };
        remove.Click += (_, _) => { _keys.Remove(); _status("The saved API key is removed."); window.Close(); };
        await window.ShowModal(owner);
        RefreshIntro();
    }

    private static Button Small(string text, Action click, string tip)
    {
        var button = new Button { Content = text, Classes = { "linkish" }, Padding = new Thickness(6, 2), VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => click();
        ToolTip.SetTip(button, tip);
        return button;
    }

    /// <summary>Takes a property from the theme, following it when the theme changes.</summary>
    private static void Themed(AvaloniaObject target, AvaloniaProperty property, string key)
    {
        if (target is StyledElement element) element.Bind(property, element.GetResourceObservable(key));
    }
}
