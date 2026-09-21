using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Views;

/// <summary>The bus decode list. Decodes once on opening, so it arrives with an answer.</summary>
public partial class BusDecodeWindow : Window
{
    public BusDecodeWindow()
    {
        AvaloniaXamlLoader.Load(this);

        DataContextChanged += (_, _) =>
        {
            if (DataContext is BusDecodeViewModel model) model.RunCommand.Execute(null);
        };
    }
}
