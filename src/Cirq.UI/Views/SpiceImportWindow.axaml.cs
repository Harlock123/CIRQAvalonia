using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cirq.UI.Views;

/// <summary>Paste a SPICE model card and get a part.</summary>
public partial class SpiceImportWindow : Window
{
    public SpiceImportWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
