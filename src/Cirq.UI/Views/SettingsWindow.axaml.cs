using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cirq.UI.Views;

/// <summary>
/// The settings dialog. There is no OK or Cancel: every change is applied and saved as it is made,
/// which is the behaviour people now expect from preferences and removes any chance of losing a
/// setting by closing the window the wrong way.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();

        // The view model listens for theme changes while it is open; let it go when the dialog does.
        Closed += (_, _) => (DataContext as ViewModels.SettingsViewModel)?.Dispose();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
