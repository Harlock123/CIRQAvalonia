using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cirq.UI.Views;

/// <summary>What to print and on what. The caller reads the view model back if this returns true.</summary>
public partial class PrintWindow : Window
{
    public PrintWindow()
    {
        AvaloniaXamlLoader.Load(this);

        this.FindControl<Button>("Ok")!.Click += (_, _) => Close(true);
        this.FindControl<Button>("Cancel")!.Click += (_, _) => Close(false);
    }
}
