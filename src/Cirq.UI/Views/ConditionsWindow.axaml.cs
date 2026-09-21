using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cirq.UI.Views;

/// <summary>The run conditions: at present, the temperature everything in the circuit is at.</summary>
public partial class ConditionsWindow : Window
{
    public ConditionsWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
