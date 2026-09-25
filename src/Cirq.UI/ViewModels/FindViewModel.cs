using System.Collections.ObjectModel;
using Cirq.Core.Topology;
using Cirq.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>
/// Finding something on the sheet: type a designator, a value, a kind of part or a net name, and
/// go to it.
/// </summary>
public sealed partial class FindViewModel : ObservableObject
{
    private readonly Circuit _circuit;

    public FindViewModel(Circuit circuit)
    {
        _circuit = circuit;
        Search();
    }

    /// <summary>What is typed. The list follows as it changes rather than on a button.</summary>
    [ObservableProperty]
    public partial string Term { get; set; } = string.Empty;

    [ObservableProperty]
    public partial SheetMatch? Selected { get; set; }

    public ObservableCollection<SheetMatch> Matches { get; } = [];

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    public bool HasMatches => Matches.Count > 0;

    /// <summary>Raised when a match should be gone to, with the window closing behind it.</summary>
    public event EventHandler<CircuitComponent>? RequestGoTo;

    partial void OnTermChanged(string value) => Search();

    private void Search()
    {
        Matches.Clear();

        foreach (var match in SheetSearch.Find(_circuit, Term)) Matches.Add(match);

        Selected = Matches.FirstOrDefault();

        var was = Property;

        Properties.Clear();

        foreach (var property in SheetEdit.SharedProperties(Matches.Select(m => m.Part).Distinct()))
            Properties.Add(property);

        // Kept across a search where it still applies, so typing in the box does not keep resetting
        // what somebody chose to change.
        Property = was is not null && Properties.Contains(was) ? was : Properties.FirstOrDefault();

        Summary = Term.Trim().Length == 0
            ? "Type a designator, a value, a kind of part, or a net name."
            : Matches.Count switch
            {
                0 => $"Nothing on this sheet matches \"{Term.Trim()}\".",
                1 => "1 match.",
                _ => $"{Matches.Count} matches.",
            };

        OnPropertyChanged(nameof(HasMatches));
        OnPropertyChanged(nameof(CanEdit));
    }

    // ---- changing what was found -------------------------------------------

    /// <summary>
    /// The settings every matched part has, so the picker cannot offer one that would skip half of
    /// them. Empty until something has been found.
    /// </summary>
    public ObservableCollection<string> Properties { get; } = [];

    [ObservableProperty]
    public partial string? Property { get; set; }

    /// <summary>What to set them to, or the expression to bind them to.</summary>
    [ObservableProperty]
    public partial string Replacement { get; set; } = string.Empty;

    /// <summary>The name to give the value they already share.</summary>
    [ObservableProperty]
    public partial string ParameterName { get; set; } = string.Empty;

    /// <summary>What the last change did, in a sentence.</summary>
    [ObservableProperty]
    public partial string EditSummary { get; set; } = string.Empty;

    /// <summary>True once there is something to change and a setting to change on it.</summary>
    public bool CanEdit => Matches.Count > 0 && Properties.Count > 0;

    /// <summary>Raised after anything changes, so the document is dirty and the canvas repaints.</summary>
    public event EventHandler? Changed;

    private IReadOnlyList<CircuitComponent> Found => [.. Matches.Select(m => m.Part).Distinct()];

    /// <summary>Sets the chosen setting on everything found, from what is typed.</summary>
    [RelayCommand]
    public void Replace()
    {
        if (Property is not { } property) return;

        Announce(SheetEdit.Set(Found, property, Replacement));
    }

    /// <summary>Points everything found at a parameter, or at arithmetic over them.</summary>
    [RelayCommand]
    public void Bind()
    {
        if (Property is not { } property) return;

        Announce(SheetEdit.Bind(_circuit, Found, property, Replacement));
    }

    /// <summary>
    /// Gives the value they already share a name, and binds them all to it — which is how a circuit
    /// full of literals becomes one with a parameter in it.
    /// </summary>
    [RelayCommand]
    public void Extract()
    {
        if (Property is not { } property) return;

        Announce(SheetEdit.Extract(_circuit, Found, property, ParameterName));
    }

    private void Announce(SheetEditResult result)
    {
        EditSummary = result.Summary();

        if (result.Changed > 0) Changed?.Invoke(this, EventArgs.Empty);

        // The values have moved, so what the list says about them has too.
        Search();
    }

    /// <summary>Goes to whatever is selected, which is what Enter and a double-click both do.</summary>
    [RelayCommand]
    public void GoTo()
    {
        if (Selected is not { } match) return;

        RequestGoTo?.Invoke(this, match.Part);
    }
}
