using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>
/// One row in the browser. It exists so a row can know whether it is the selected one: a plain
/// list of records cannot, and every other way of showing the selection across several separate
/// lists ends in a binding fighting itself.
/// </summary>
public sealed partial class ExampleRowViewModel : ObservableObject
{
    public ExampleRowViewModel(ExampleCircuit example)
    {
        Example = example;
    }

    public ExampleCircuit Example { get; }

    public string Name => Example.Name;

    public string Description => Example.Description;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>One group in the browser, with the open/closed state the dialog keeps for it.</summary>
public sealed partial class ExampleGroupViewModel : ObservableObject
{
    public ExampleGroupViewModel(string name, IReadOnlyList<ExampleRowViewModel> items, bool isExpanded)
    {
        Name = name;
        Items = items;
        IsExpanded = isExpanded;
    }

    public string Name { get; }

    public IReadOnlyList<ExampleRowViewModel> Items { get; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Disclosure triangle, as the palette draws one.</summary>
    public string Chevron => IsExpanded ? "▾" : "▸";

    public int Count => Items.Count;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(Chevron));
}

/// <summary>
/// The example browser.
/// <para>
/// This replaced a submenu, and the reason is worth writing down: a menu of forty-odd entries is
/// something to be got through rather than something to look at, and every example added made it
/// worse. A dialog can group them, describe them, and be searched — and the cost of adding the
/// next example goes back to nothing.
/// </para>
/// </summary>
public sealed partial class ExampleBrowserViewModel : ObservableObject
{
    private readonly IReadOnlyList<ExampleCategory> _source;

    public ExampleBrowserViewModel(IReadOnlyList<ExampleCategory>? categories = null)
    {
        _source = categories ?? Examples.Categories;

        Rebuild();
    }

    /// <summary>The groups as they are being shown, which narrows as a search is typed.</summary>
    public ObservableCollection<ExampleGroupViewModel> Groups { get; } = [];

    /// <summary>What is typed in the search box. Empty shows everything.</summary>
    [ObservableProperty]
    public partial string Search { get; set; } = string.Empty;

    /// <summary>The example whose description is being shown.</summary>
    [ObservableProperty]
    public partial ExampleCircuit? Selected { get; set; }

    /// <summary>The one the dialog was closed with, or null if it was cancelled.</summary>
    public ExampleCircuit? Chosen { get; private set; }

    /// <summary>Raised when the dialog should close, with <see cref="Chosen"/> set or not.</summary>
    public event EventHandler? RequestClose;

    /// <summary>True when a search has hidden everything, so the dialog can say so.</summary>
    public bool IsEmpty => Groups.Count == 0;

    /// <summary>How many examples are on offer at the moment.</summary>
    public int MatchCount => Groups.Sum(g => g.Count);

    /// <summary>The total, which the summary line compares the matches against.</summary>
    public int TotalCount => _source.Sum(c => c.Items.Count);

    public string Summary => Search.Length == 0
        ? $"{TotalCount} examples in {_source.Count} groups"
        : $"{MatchCount} of {TotalCount} match “{Search}”";

    /// <summary>Picks a row out, which is what a single click does.</summary>
    [RelayCommand]
    private void Select(ExampleRowViewModel? row)
    {
        if (row is null) return;

        Selected = row.Example;
    }

    [RelayCommand]
    private void Open(ExampleCircuit? example)
    {
        var chosen = example ?? Selected;
        if (chosen is null) return;

        Chosen = chosen;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A double click picks the row and opens it in one go.</summary>
    [RelayCommand]
    private void OpenRow(ExampleRowViewModel? row)
    {
        if (row is null) return;

        Selected = row.Example;
        Open(row.Example);
    }

    partial void OnSelectedChanged(ExampleCircuit? value)
    {
        foreach (var group in Groups)
            foreach (var row in group.Items)
                row.IsSelected = ReferenceEquals(row.Example, value);
    }

    [RelayCommand]
    private void Cancel()
    {
        Chosen = null;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSearchChanged(string value) => Rebuild();

    /// <summary>
    /// Rebuilds the visible groups for the current search. A search opens every group that still
    /// has something in it, because leaving a match folded away inside a closed group is the same
    /// as not matching at all.
    /// </summary>
    private void Rebuild()
    {
        var previous = Groups.ToDictionary(g => g.Name, g => g.IsExpanded);
        var term = Search.Trim();

        Groups.Clear();

        foreach (var category in _source)
        {
            var matching = term.Length == 0
                ? category.Items
                : [.. category.Items.Where(e => Matches(e, category.Name, term))];

            if (matching.Count == 0) continue;

            // The group name is stamped on here rather than in the declaration above, so the
            // details pane can say where a thing lives without the list repeating itself.
            IReadOnlyList<ExampleRowViewModel> items =
                [.. matching.Select(e => new ExampleRowViewModel(e with { Category = category.Name }))];

            var expanded = term.Length > 0 || !previous.TryGetValue(category.Name, out var was) || was;

            Groups.Add(new ExampleGroupViewModel(category.Name, items, expanded));
        }

        // Keep the selection if it survived the search; otherwise take the first thing on offer,
        // so the description pane is never blank next to a list that has entries in it.
        var stillThere = Selected is not null
            && Groups.Any(g => g.Items.Any(r => ReferenceEquals(r.Example, Selected)));

        Selected = stillThere ? Selected : Groups.FirstOrDefault()?.Items.FirstOrDefault()?.Example;

        // The rows are new objects after a rebuild, so the marks have to be put back on.
        OnSelectedChanged(Selected);

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(MatchCount));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>
    /// Matches on the name, the description and the group, because people look for "I2C" as often
    /// as they look for the name of a circuit they half remember.
    /// </summary>
    private static bool Matches(ExampleCircuit example, string category, string term) =>
        example.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
        || example.Description.Contains(term, StringComparison.OrdinalIgnoreCase)
        || category.Contains(term, StringComparison.OrdinalIgnoreCase);
}
