using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cirq.UI.Views;

/// <summary>The rule-check list. Selecting a row and pressing the button points the canvas at it.</summary>
public partial class RuleCheckWindow : Window
{
    public RuleCheckWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
