using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cirq.UI.Views;

/// <summary>
/// What the circuit is supposed to do, listed beside what it did.
/// <para>
/// Nothing here but the list: the arithmetic is in <c>SpecCheck</c> and the requirements
/// themselves belong to the circuit, so this window can be closed and reopened without anything
/// being lost.
/// </para>
/// </summary>
public partial class SpecsWindow : Window
{
    public SpecsWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
