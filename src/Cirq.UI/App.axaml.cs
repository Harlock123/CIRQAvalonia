using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Cirq.UI.Services;
using Cirq.UI.ViewModels;
using Cirq.UI.Views;

namespace Cirq.UI;

public partial class App : Application
{
    /// <summary>The persisted preferences, shared with the settings dialog.</summary>
    public AppSettings Settings { get; private set; } = new();

    /// <summary>Where those preferences are read from and written to.</summary>
    public ISettingsStore SettingsStore { get; private set; } = new JsonSettingsStore();

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
        }

        base.OnFrameworkInitializationCompleted();
    }
}
