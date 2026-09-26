using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cirq.UI.Controls;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Views;

/// <summary>
/// Drawing a block's symbol, with the symbol itself on the left.
/// <para>
/// The preview is the ordinary canvas drawing the ordinary block — not a picture of what the symbol
/// might look like, but the thing itself, redrawn as each box is typed in. A symbol editor where
/// you press Apply to find out what you drew is one nobody can draw with.
/// </para>
/// </summary>
public partial class SymbolWindow : Window
{
    private CircuitCanvas? _preview;

    public SymbolWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _preview = this.FindControl<CircuitCanvas>("Preview");

        DataContextChanged += (_, _) => Attach();
        Attach();
    }

    private void Attach()
    {
        if (DataContext is not SymbolViewModel model) return;

        Title = $"Symbol — {model.Label}";

        model.Changed -= OnChanged;
        model.Changed += OnChanged;

        Opened += (_, _) => _preview?.RequestFit();
    }

    private void OnChanged(object? sender, EventArgs e) => _preview?.InvalidateVisual();
}
