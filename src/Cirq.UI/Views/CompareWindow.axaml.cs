using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cirq.UI.Views;

/// <summary>What changed, in the words a person would use.</summary>
public partial class CompareWindow : Window
{
    public CompareWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
