using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cirq.UI.Views;

/// <summary>The block library: save the selected block, or place a copy of a saved one.</summary>
public partial class BlockLibraryWindow : Window
{
    public BlockLibraryWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
