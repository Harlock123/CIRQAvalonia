using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cirq.UI.Views;

/// <summary>What every part is dissipating, against what it is rated for.</summary>
public partial class PowerWindow : Window
{
    public PowerWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
