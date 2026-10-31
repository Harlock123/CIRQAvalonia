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
            // The pipe closes as this is called; the sessions wind down on their own.
            desktop.Exit += (_, _) => _ = Agents.StopAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
