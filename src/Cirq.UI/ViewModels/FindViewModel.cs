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

        Summary = Term.Trim().Length == 0
            ? "Type a designator, a value, a kind of part, or a net name."
            : Matches.Count switch
            {
                0 => $"Nothing on this sheet matches \"{Term.Trim()}\".",
                1 => "1 match.",
                _ => $"{Matches.Count} matches.",
            };

        OnPropertyChanged(nameof(HasMatches));
    }

    /// <summary>Goes to whatever is selected, which is what Enter and a double-click both do.</summary>
    [RelayCommand]
    public void GoTo()
    {
        if (Selected is not { } match) return;

        RequestGoTo?.Invoke(this, match.Part);
    }
}
