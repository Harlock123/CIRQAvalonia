using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cirq.UI.Controls;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Views;

/// <summary>
/// The inside of a block, drawn by the same canvas that draws the sheet.
/// <para>
/// The same canvas rather than a picture of one, because everything that makes the sheet usable —
/// zoom, pan, hit testing, the hover card, the probe tool — is in it, and a second, simpler viewer
/// would be a second thing to keep in step. It is handed a circuit whose components are the block's
/// own, so what is on screen is the block itself.
/// </para>
/// </summary>
public partial class BlockWindow : Window
{
    private CircuitCanvas? _canvas;

    public BlockWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _canvas = this.FindControl<CircuitCanvas>("Canvas");

        DataContextChanged += (_, _) => Attach();
        Attach();
    }

    private void Attach()
    {
        if (_canvas is null || DataContext is not BlockViewModel model) return;

        Title = $"Inside {model.Name}";

        _canvas.ProbeRequested -= OnProbeRequested;
        _canvas.ProbeRequested += OnProbeRequested;

        _canvas.InteractiveEditEnded -= OnEdited;
        _canvas.InteractiveEditEnded += OnEdited;

        // Fitted once the window has a size to fit into.
        Opened += (_, _) => _canvas.RequestFit();
    }

    private void OnProbeRequested(object? sender, Cirq.Core.Topology.Terminal terminal)
    {
        if (DataContext is BlockViewModel model) model.Probe(terminal);

        _canvas?.InvalidateVisual();
    }

    private void OnEdited(object? sender, EventArgs e)
    {
        if (DataContext is BlockViewModel model) model.NotifyChanged();
    }
}
