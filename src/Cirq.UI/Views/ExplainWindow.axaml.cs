using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Views;

/// <summary>What the drawing is, read back in words.</summary>
public partial class ExplainWindow : Window
{
    public ExplainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        DataContextChanged += (_, _) =>
        {
            if (DataContext is not ExplainViewModel model) return;

            // The copy button fills a string; putting it on the clipboard is the window's job,
            // because a view model has no business knowing there is one.
            model.PropertyChanged += async (_, e) =>
            {
                if (e.PropertyName != nameof(ExplainViewModel.Text)) return;
                if (model.Text.Length == 0) return;
                if (Clipboard is not { } clipboard) return;

                await clipboard.SetTextAsync(model.Text);
            };
        };
    }
}
