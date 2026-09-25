using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cirq.UI.Views;

/// <summary>What the circuit produced once, and what it produces now.</summary>
public partial class BaselineWindow : Window
{
    public BaselineWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
