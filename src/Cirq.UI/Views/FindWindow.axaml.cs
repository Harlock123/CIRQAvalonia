using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Views;

/// <summary>
/// Find on the sheet: type, pick, go.
/// <para>
/// The box takes focus on open and Enter goes to the top match, because the whole of this is
/// meant to cost four keystrokes and not a hunt with the mouse. Up and down move through the
/// list without leaving the box, for the same reason.
/// </para>
/// </summary>
public partial class FindWindow : Window
{
    public FindWindow()
    {
        AvaloniaXamlLoader.Load(this);

        var term = this.FindControl<TextBox>("Term");
        var results = this.FindControl<ListBox>("Results");

        Opened += (_, _) => term?.Focus();

        if (results is not null)
            results.DoubleTapped += (_, _) => Go();

        KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Enter:
                    Go();
                    e.Handled = true;
                    break;

                case Key.Escape:
                    Close();
                    e.Handled = true;
                    break;

                // Moving the selection from the text box, so the hands stay where they are.
                case Key.Down or Key.Up when results is not null && results.ItemCount > 0:
                    var step = e.Key == Key.Down ? 1 : -1;
                    results.SelectedIndex =
                        Math.Clamp(results.SelectedIndex + step, 0, results.ItemCount - 1);
                    e.Handled = true;
                    break;
            }
        };
    }

    private void Go()
    {
        if (DataContext is not FindViewModel model || model.Selected is null) return;

        model.GoToCommand.Execute(null);
        Close();
    }
}
