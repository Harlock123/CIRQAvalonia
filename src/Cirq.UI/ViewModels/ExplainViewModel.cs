using System.Collections.ObjectModel;
using Cirq.Components.Explaining;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>
/// What the drawing is, read back in words.
/// <para>
/// A netlist says what is joined to what. A person reading the same schematic sees a divider
/// setting 3.3 V, a low-pass turning over at 1.6 kHz, a follower — structures, each with a number
/// that follows from it. That translation is most of what "knowing how to read a schematic" is.
/// </para>
/// </summary>
public sealed partial class ExplainViewModel : ObservableObject
{
    public ExplainViewModel(Circuit circuit)
    {
        ArgumentNullException.ThrowIfNull(circuit);

        foreach (var explanation in CircuitExplainer.Explain(circuit)) Found.Add(explanation);

        Summary = Found.Count switch
        {
            0 => "Nothing here matches anything it knows how to describe — which is not a " +
                 "criticism of the circuit. It recognises a dozen or so structures exactly and " +
                 "stays quiet about everything else, because a confident wrong description is " +
                 "worse than none.",
            1 => "One thing recognised.",
            _ => $"{Found.Count} things recognised.",
        };
    }

    public ObservableCollection<Explanation> Found { get; } = [];

    public string Summary { get; }

    public bool HasAny => Found.Count > 0;

    [ObservableProperty]
    public partial Explanation? Selected { get; set; }

    /// <summary>Raised when the parts behind an explanation should be picked out on the drawing.</summary>
    public event EventHandler<IReadOnlyList<CircuitComponent>>? RequestHighlight;

    partial void OnSelectedChanged(Explanation? value)
    {
        if (value is not null) RequestHighlight?.Invoke(this, value.Parts);
    }

    /// <summary>Everything recognised, as text somebody can paste into a note.</summary>
    [RelayCommand]
    private void Copy()
    {
        Text = string.Join(
            Environment.NewLine,
            Found.Select(e => $"{e.Headline} — {e.Detail}"));
    }

    /// <summary>The copied text, which the window puts on the clipboard.</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;
}
