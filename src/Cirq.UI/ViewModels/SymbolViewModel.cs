using System.Collections.ObjectModel;
using Cirq.Components.Hierarchy;
using Cirq.Core.Primitives;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>Which edge of the symbol a pin sits on.</summary>
public enum PinSide
{
    Left,
    Right,
    Top,
    Bottom,
}

/// <summary>
/// One pin, as the symbol editor deals with it: a name, an edge, and how far along that edge it is.
/// <para>
/// An edge and a distance rather than a pair of coordinates, because that is how somebody thinks
/// about a symbol — "the clock goes bottom left, the outputs go down the right" — and because it
/// keeps a pin on the body however the body is resized.
/// </para>
/// </summary>
public sealed partial class PinViewModel : ObservableObject
{
    private readonly Terminal _terminal;
    private readonly Action _changed;

    public PinViewModel(Terminal terminal, Subcircuit block, Action changed)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(block);

        _terminal = terminal;
        _changed = changed;

        Name = terminal.Name;

        var offset = terminal.CanvasOffset;

        var acrossness = block.HalfWidth > 0 ? Math.Abs(offset.X) / block.HalfWidth : 0;
        var downness = block.HalfHeight > 0 ? Math.Abs(offset.Y) / block.HalfHeight : 0;

        Side = acrossness >= downness
            ? offset.X < 0 ? PinSide.Left : PinSide.Right
            : offset.Y < 0 ? PinSide.Top : PinSide.Bottom;

        Along = Side is PinSide.Left or PinSide.Right ? offset.Y : offset.X;
    }

    /// <summary>The terminal this stands for. Its identity never changes, only where it is drawn.</summary>
    public Terminal Terminal => _terminal;

    /// <summary>What the pin is called on the symbol.</summary>
    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial PinSide Side { get; set; }

    /// <summary>How far along that edge, from the middle of it.</summary>
    [ObservableProperty]
    public partial double Along { get; set; }

    public static IReadOnlyList<PinSide> Sides { get; } = Enum.GetValues<PinSide>();

    /// <summary>Where this pin goes on a symbol of the given size.</summary>
    public Point OffsetOn(double halfWidth, double halfHeight) => Side switch
    {
        PinSide.Left => new Point(-halfWidth, Clamp(Along, halfHeight)),
        PinSide.Right => new Point(halfWidth, Clamp(Along, halfHeight)),
        PinSide.Top => new Point(Clamp(Along, halfWidth), -halfHeight),
        _ => new Point(Clamp(Along, halfWidth), halfHeight),
    };

    /// <summary>A pin cannot be further along an edge than the edge is long.</summary>
    private static double Clamp(double along, double half) => Math.Clamp(along, -half + 8, half - 8);

    partial void OnSideChanged(PinSide value) => _changed();

    partial void OnAlongChanged(double value) => _changed();

    partial void OnNameChanged(string value) => _changed();
}

/// <summary>
/// Drawing a block's symbol.
/// <para>
/// A block is how a section of a circuit becomes one part, and until now every one of them was the
/// same grey box with its pins alternating down the sides. That is fine for hiding a section and
/// useless for making a <i>part</i>: what tells somebody at a glance that this is an amplifier and
/// that is a regulator is its shape and where its pins are.
/// </para>
/// <para>
/// So: a size, and a pin on whichever edge you want it, at whatever height. Nothing here touches
/// what the block <i>does</i> — the contents do that, and a pin that moves keeps its identity, so
/// everything wired to it stays wired to it. This is drawing, and only drawing.
/// </para>
/// </summary>
public sealed partial class SymbolViewModel : ObservableObject
{
    private readonly Subcircuit _block;
    private bool _loading;

    public SymbolViewModel(Subcircuit block)
    {
        ArgumentNullException.ThrowIfNull(block);

        _block = block;

        _loading = true;

        Label = block.BlockName;
        Width = block.HalfWidth * 2;
        Height = block.HalfHeight * 2;

        foreach (var (outer, _) in block.Ports) Pins.Add(new PinViewModel(outer, block, Apply));

        _loading = false;

        // The block on its own, for the preview to draw. The same part, so what is on screen is the
        // symbol itself rather than a picture of what it might look like.
        Preview.Components.Add(block);
    }

    /// <summary>The pins, in the order they were brought out.</summary>
    public ObservableCollection<PinViewModel> Pins { get; } = [];

    /// <summary>A circuit holding just this block, for the preview canvas.</summary>
    public Circuit Preview { get; } = new();

    /// <summary>What is written across the middle of the symbol.</summary>
    [ObservableProperty]
    public partial string Label { get; set; }

    [ObservableProperty]
    public partial double Width { get; set; }

    [ObservableProperty]
    public partial double Height { get; set; }

    /// <summary>Raised whenever the symbol changed, so the preview redraws.</summary>
    public event EventHandler? Changed;

    public string Summary =>
        $"{Pins.Count} pin(s). Moving one changes where it is drawn and nothing else — whatever is " +
        "wired to it stays wired to it.";

    partial void OnLabelChanged(string value) => Apply();

    partial void OnWidthChanged(double value) => Apply();

    partial void OnHeightChanged(double value) => Apply();

    /// <summary>Puts every pin back the way a block arranges itself: alternating down the sides.</summary>
    [RelayCommand]
    public void Arrange()
    {
        _loading = true;

        _block.SymbolWidth = 0;
        _block.SymbolHeight = 0;
        _block.ArrangePorts();

        Width = _block.HalfWidth * 2;
        Height = _block.HalfHeight * 2;

        Pins.Clear();

        foreach (var (outer, _) in _block.Ports) Pins.Add(new PinViewModel(outer, _block, Apply));

        _loading = false;

        // Deliberately not Apply: arranging puts the block back to how it was before anybody drew
        // it, which includes having no stated size at all. Touching any box after this makes it a
        // drawn symbol again.
        OnPropertyChanged(nameof(Summary));

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Writes the symbol back to the block. Called on every change rather than on a button, because
    /// the preview is the point: a symbol editor where you press Apply to see what you drew is a
    /// symbol editor nobody can draw with.
    /// </summary>
    public void Apply()
    {
        if (_loading) return;

        _block.BlockName = Label;
        _block.SymbolWidth = Math.Clamp(Width, 40, 600);
        _block.SymbolHeight = Math.Clamp(Height, 40, 600);

        foreach (var pin in Pins)
        {
            _block.PlacePort(pin.Terminal, pin.OffsetOn(_block.HalfWidth, _block.HalfHeight));

            if (!string.Equals(pin.Terminal.Name, pin.Name, StringComparison.Ordinal))
                _block.RenamePort(pin.Terminal, pin.Name);
        }

        OnPropertyChanged(nameof(Summary));

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
