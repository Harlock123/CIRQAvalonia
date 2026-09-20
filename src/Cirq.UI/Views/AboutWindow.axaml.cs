using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Views;

/// <summary>
/// The About dialog: what this is, what version of it, and what it is built on.
/// <para>
/// "Copy details" is the part that earns its place. A reader filing a bug otherwise has to
/// transcribe four version numbers by hand, and the one they get wrong is the one that mattered.
/// </para>
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();

        var copy = this.FindControl<Button>("CopyButton")!;
        copy.Click += async (_, _) =>
        {
            if (DataContext is not AboutViewModel about) return;
            if (Clipboard is null) return;

            await Clipboard.SetTextAsync(about.CopyText);

            // Say so on the button itself rather than opening a second dialog to report it.
            copy.Content = "Copied";
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
