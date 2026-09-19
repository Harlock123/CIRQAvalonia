using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.UI.ViewModels;

/// <summary>
/// One entry in the Simulate menu's speed submenu. Replacing the toolbar's logarithmic slider with
/// named steps also makes the setting easier to reason about: "one thousandth of real time" is a
/// clearer thing to pick than a position on a log scale.
/// </summary>
public sealed partial class SpeedOption : ObservableObject
{
    public SpeedOption(string label, double factor, bool isMaximum = false)
    {
        Label = label;
        Factor = factor;
        IsMaximum = isMaximum;
    }

    public string Label { get; }

    /// <summary>Simulated seconds per wall-clock second.</summary>
    public double Factor { get; }

    /// <summary>True for the entry that removes real-time pacing altogether.</summary>
    public bool IsMaximum { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
