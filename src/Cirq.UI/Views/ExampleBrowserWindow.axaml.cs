using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Views;

/// <summary>
/// The example browser: groups down the left, a description on the right, and a search across
/// everything. It replaced a submenu of forty-odd entries, which had stopped being a way of
/// finding an example and become a thing to scroll past.
/// </summary>
public partial class ExampleBrowserWindow : Window
{
    public ExampleBrowserWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        DataContextChanged += (_, _) => Attach();
        Opened += (_, _) => this.FindControl<TextBox>("SearchBox")?.Focus();

        Attach();
    }

    private void Attach()
    {
        if (DataContext is not ExampleBrowserViewModel model) return;

        model.RequestClose -= OnRequestClose;
        model.RequestClose += OnRequestClose;
    }

    private void OnRequestClose(object? sender, EventArgs e) => Close();

    /// <summary>Double-clicking a row picks it and opens it, which is what a list should do.</summary>
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not ExampleBrowserViewModel model) return;
        if (sender is not Control { DataContext: ExampleRowViewModel row }) return;

        model.OpenRowCommand.Execute(row);
    }
}
