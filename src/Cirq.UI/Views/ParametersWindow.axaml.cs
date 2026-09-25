using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cirq.UI.Views;

/// <summary>The numbers a circuit gives names to.</summary>
public partial class ParametersWindow : Window
{
    public ParametersWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
