using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.UI.ViewModels;

/// <summary>
/// One page's tab.
/// <para>
/// A tab that can be renamed in place rather than through a dialog: a page is named while looking
/// at what is on it, which is not something worth interrupting with a window. Double-tapping the
/// tab already on screen starts the edit, Enter or leaving it commits, Escape puts back the name it
/// had.
/// </para>
/// </summary>
public sealed partial class SheetTabViewModel(string name) : ObservableObject
{
    /// <summary>What the page is called, as typed. Committing it is the view's job.</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = name;

    /// <summary>True for the page on screen, which is how the tab is drawn as the chosen one.</summary>
    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    /// <summary>True while the name is being typed, which swaps the label for a box.</summary>
    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    /// <summary>What it was called when the edit started, to put back on Escape.</summary>
    public string Committed { get; set; } = name;
}
