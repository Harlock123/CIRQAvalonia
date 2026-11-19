using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Cirq.Mcp;
using Cirq.UI.Agents;
using Cirq.UI.Services;
using Cirq.UI.ViewModels;
using Cirq.UI.Views;

namespace Cirq.UI;

public partial class App : Application
{
    /// <summary>The persisted preferences, shared with the settings dialog.</summary>
    public AppSettings Settings { get; private set; } = new();

    /// <summary>Where those preferences are read from and written to.</summary>
    public ISettingsStore SettingsStore { get; internal set; } = new JsonSettingsStore();

    /// <summary>
    /// The door for AI agents outside the application, and the live host every window's AI Chat works through.
    /// </summary>
    public LiveAgents Agents { get; } = new();

    private Cirq.Chat.UsageLedger? _chatUsage;

    /// <summary>
    /// The AI Chat's use day by day, in chat-usage.json beside the settings: one for the app, so every window's chat
    /// adds to the same totals.
    /// </summary>
    public Cirq.Chat.UsageLedger ChatUsage
    {
        get
        {
            var path = Path.Combine(Path.GetDirectoryName(SettingsStore.Location) ?? Path.GetTempPath(), "chat-usage.json");
            if (_chatUsage?.Path != path) _chatUsage = new Cirq.Chat.UsageLedger(path);
            return _chatUsage;
        }
    }

    /// <summary>The pipe outside agents connect on (tests use one of their own).</summary>
    public string AgentPipe { get; set; } = LiveProtocol.PipeName;

    /// <summary>
    /// Lets AI agents outside the application in, or shuts them out (AI › Allow AI Agents), remembers it, and ticks the
    /// menu item in every window. Returns a line for the status bar.
    /// </summary>
    public string SetAllowAgents(bool allow)
    {
        if (Settings.AllowAgents != allow)
        {
            Settings.AllowAgents = allow;
            SettingsStore.Save(Settings);
        }
        string status;
        if (allow)
        {
            status = Agents.Start(AgentPipe) is { } problem
                ? $"AI agents can't connect: {problem}"
                : "AI agents may connect (CirqAvalonia --mcp --attach). They work in the window you used last.";
        }
        else
        {
            _ = Agents.StopAsync();
            status = "AI agents are shut out.";
        }
        foreach (var editor in Agents.Editors.ToList()) editor.AllowAgents = allow;
        return status;
    }

    private string? _tokenInMemory;

    /// <summary>The network door's token file, beside the settings (null for a settings store that isn't a file's, as tests' fakes).</summary>
    private NetworkToken? TokenStore => SettingsStore is JsonSettingsStore store && Path.GetDirectoryName(store.Location) is { Length: > 0 } folder ? Cirq.Mcp.NetworkToken.In(folder) : null;

    /// <summary>The network door's access token (made the first time it's asked for; never in settings.json).</summary>
    public string NetworkToken => TokenStore?.Get() ?? (_tokenInMemory ??= NetworkListener.NewToken());

    /// <summary>Makes a new token (agents given the old one can no longer connect), reopens an open door with it, and returns it.</summary>
    public string RenewNetworkToken()
    {
        var token = TokenStore?.Renew() ?? (_tokenInMemory = NetworkListener.NewToken());
        _ = SyncNetworkAsync();
        return token;
    }

    /// <summary>Why the network door didn't open the last time it tried, or null.</summary>
    public string? NetworkProblem { get; private set; }

    private Task<string>? _networkSync;
    private bool _syncNetworkAgain;

    /// <summary>
    /// Opens, closes or reopens the network door to match the settings (and the token), and returns a line for the
    /// status bar. One at a time; a change meanwhile runs again after, and the task ends when the door is as the
    /// settings now say.
    /// </summary>
    public Task<string> SyncNetworkAsync()
    {
        if (_networkSync is { IsCompleted: false })
        {
            _syncNetworkAgain = true;
            return _networkSync;
        }
        return _networkSync = RunNetworkSync();
    }

    private async Task<string> RunNetworkSync()
    {
        string status = "";
        do
        {
            _syncNetworkAgain = false;
            var s = Settings;
            var want = s.AllowNetworkAgents
                ? new NetworkSettings(s.NetworkAgentsAddress, s.NetworkAgentsPort, NetworkToken, NullIfEmpty(s.NetworkAgentsCertificate), NullIfEmpty(s.NetworkAgentsKey))
                : null;
            string root = NullIfEmpty(s.NetworkAgentsFolder) ?? DocumentsFolder;
            status = "";
            if (Agents.NetworkRunning && (want == null || want != Agents.NetworkSettings || root != Agents.NetworkRoot))
            {
                await Agents.StopNetworkAsync();
                if (want == null) status = "AI agents on other computers are shut out.";
            }
            NetworkProblem = null;
            if (want != null && !Agents.NetworkRunning)
            {
                NetworkProblem = await Agents.StartNetworkAsync(want, root);
                status = NetworkProblem is { } problem
                    ? $"AI agents on other computers can't connect: {problem}"
                    : $"AI agents on other computers may connect (MCP): {Agents.NetworkUrls.FirstOrDefault()}, with the access token (AI › Connect an Agent).";
            }
        }
        while (_syncNetworkAgain);
        return status;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string DocumentsFolder =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) is { Length: > 0 } docs && Directory.Exists(docs)
            ? docs : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Preferences first, so the window is built with the right theme already applied
            // rather than flashing the default one on the way in.
            SettingsStore = new JsonSettingsStore();
            Settings = SettingsStore.Load();
            ThemeManager.Apply(Settings.Theme, this);

            // While the user is on "System", follow the desktop if it changes underneath us.
            if (PlatformSettings is not null)
            {
                PlatformSettings.ColorValuesChanged += (_, _) =>
                {
                    if (Settings.Theme == AppTheme.System) ThemeManager.NotifyChanged();
                };
            }

            var viewModel = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.ShutdownRequested += (_, _) => viewModel.Dispose();

            Agents.ConnectionsChanged += n =>
            {
                if (Agents.Active is { } window)
                    window.StatusMessage = n > 0 ? $"AI agents connected: {n}. Their changes show here, and Ctrl+Z undoes them." : "The AI agent disconnected.";
            };
            if (Settings.AllowAgents) SetAllowAgents(true);
            if (Settings.AllowNetworkAgents)
                _ = SyncNetworkAsync().ContinueWith(t => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (t.Result.Length > 0) viewModel.StatusMessage = t.Result; }), TaskScheduler.Default);
            // The pipe closes as this is called; the sessions wind down on their own.
            desktop.Exit += (_, _) =>
            {
                _ = Agents.StopAsync();
                _ = Agents.StopNetworkAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
