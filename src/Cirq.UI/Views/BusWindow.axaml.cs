using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Views;

/// <summary>Asks what the bus is called and how wide it is, then places it.</summary>
public partial class BusWindow : Window
{
    public BusWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        if (this.FindControl<Button>("PlaceButton") is { } place)
        {
            place.Click += (_, _) =>
            {
                if (DataContext is BusViewModel model) model.PlaceCommand.Execute(null);

                Close();
            };
        }
    }
}
