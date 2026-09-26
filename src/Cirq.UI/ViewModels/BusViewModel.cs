using Cirq.Components.Annotations;
using Cirq.Components.Buses;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>
/// Putting a bus on the drawing: a row of taps, numbered, and the line that shows where they go.
/// <para>
/// Eight taps and a line rather than eight net labels typed by hand. The saving is not the typing —
/// it is that <c>D3</c> typed as <c>D4</c> on one of sixteen is a mistake that costs an afternoon,
/// because the schematic looks perfectly right and the simulation quietly answers a different
/// question.
/// </para>
/// </summary>
public sealed partial class BusViewModel : ObservableObject
{
    private readonly Circuit _circuit;

    public BusViewModel(Circuit circuit)
    {
        ArgumentNullException.ThrowIfNull(circuit);

        _circuit = circuit;
    }

    /// <summary>What the bus is called. The signals are this with their number run on: D0, D1.</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = "D";

    /// <summary>The lowest number in it — usually zero, sometimes not.</summary>
    [ObservableProperty]
    public partial int First { get; set; }

    /// <summary>How many signals. Eight by default, because that is what a byte is.</summary>
    [ObservableProperty]
    public partial int Width { get; set; } = 8;

    public static IReadOnlyList<int> WidthOptions { get; } = [2, 3, 4, 5, 6, 8, 12, 16, 24, 32];

    /// <summary>Whether to draw the heavy line as well as the taps.</summary>
    [ObservableProperty]
    public partial bool DrawTheLine { get; set; } = true;

    /// <summary>Where the first tap goes, and the ones after it march down from there.</summary>
    [ObservableProperty]
    public partial double X { get; set; } = 200;

    [ObservableProperty]
    public partial double Y { get; set; } = 200;

    /// <summary>What the bus is called as a range, which is what the line is labelled with.</summary>
    public string Range => $"{Name.Trim()}[{First}..{First + Math.Max(Width, 1) - 1}]";

    /// <summary>The names it will make, for the sentence under the boxes.</summary>
    public string Summary => Width < 1
        ? "A bus needs at least one signal."
        : Width <= 4
            ? $"{Range} — {string.Join(", ", Names())}"
            : $"{Range} — {string.Join(", ", Names().Take(3))} … {Names().Last()}";

    /// <summary>The taps that were placed, for the caller to select them.</summary>
    public IReadOnlyList<CircuitComponent> Placed { get; private set; } = [];

    /// <summary>Raised when the taps have been added, so the canvas redraws and the file is dirty.</summary>
    public event EventHandler? Added;

    partial void OnNameChanged(string value) => Announce();

    partial void OnFirstChanged(int value) => Announce();

    partial void OnWidthChanged(int value) => Announce();

    private void Announce()
    {
        OnPropertyChanged(nameof(Range));
        OnPropertyChanged(nameof(Summary));
    }

    private IEnumerable<string> Names() =>
        Enumerable.Range(First, Math.Max(Width, 1)).Select(i => $"{Name.Trim()}{i}");

    /// <summary>
    /// Places the taps in a column, and the line beside them.
    /// <para>
    /// A column on a twenty-unit pitch, which is the pitch every package on this canvas puts its
    /// pins on — so a tap lands opposite the pin it is for rather than somewhere near it.
    /// </para>
    /// </summary>
    [RelayCommand]
    public void Place()
    {
        var count = Math.Clamp(Width, 1, 64);
        var name = Name.Trim().Length == 0 ? "D" : Name.Trim();

        const double Pitch = 20.0;

        List<CircuitComponent> placed = [];

        for (var i = 0; i < count; i++)
        {
            var tap = new BusTap(name, First + i)
            {
                X = X,
                Y = Y + (i * Pitch),
            };

            _circuit.Add(tap);
            placed.Add(tap);
        }

        if (DrawTheLine)
        {
            // Standing on end beside the column, long enough to run past both ends of it, which is
            // how a bus is drawn where its signals come off it.
            var line = new BusLine(Range, (count + 1) * Pitch)
            {
                X = X + 60,
                Y = Y + ((count - 1) * Pitch / 2.0),
                RotationDegrees = 90,
            };

            _circuit.Add(line);
            placed.Add(line);
        }

        Placed = placed;

        Added?.Invoke(this, EventArgs.Empty);
    }
}
