using System.ComponentModel;

namespace Cirq.UI.ViewModels;

/// <summary>A group in a list that opens and closes.</summary>
public interface IExpandableGroup : INotifyPropertyChanged
{
    /// <summary>Whether the group's contents are showing.</summary>
    bool IsExpanded { get; set; }
}

/// <summary>
/// Keeps at most one group in a list open at a time.
/// <para>
/// The palette holds sixteen groups and the example browser twelve. Opened independently they
/// stack up: four open groups is more than a sidebar's height, so finding the fifth means
/// scrolling past the four you already finished with. Closing the last one as the next opens keeps
/// every heading on screen, which is what makes a long list browsable rather than a thing to
/// scroll through.
/// </para>
/// <para>
/// Bulk changes — an expand-all button, or a search opening everything it matched — go through
/// <see cref="SetAll"/> and are exempt. Those are not somebody opening a group, and collapsing
/// fifteen of the sixteen a button just opened would be an odd way to answer the button.
/// </para>
/// </summary>
public sealed class Accordion
{
    private readonly List<IExpandableGroup> _groups = [];

    private bool _settling;

    /// <summary>
    /// Watches a set of groups, letting go of any it was watching before. The example browser
    /// rebuilds its groups on every keystroke, so this is called far more often than once.
    /// </summary>
    public void Track(IEnumerable<IExpandableGroup> groups)
    {
        foreach (var group in _groups) group.PropertyChanged -= OnGroupChanged;

        _groups.Clear();
        _groups.AddRange(groups);

        foreach (var group in _groups) group.PropertyChanged += OnGroupChanged;
    }

    /// <summary>Opens or closes every group at once, without the one-at-a-time rule applying.</summary>
    public void SetAll(bool expanded)
    {
        _settling = true;

        try
        {
            foreach (var group in _groups) group.IsExpanded = expanded;
        }
        finally
        {
            _settling = false;
        }
    }

    /// <summary>Closes every group. What both lists start out as.</summary>
    public void CollapseAll() => SetAll(false);

    private void OnGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_settling || e.PropertyName != nameof(IExpandableGroup.IsExpanded)) return;
        if (sender is not IExpandableGroup opened || !opened.IsExpanded) return;

        // Re-entrant by construction: closing the others raises this again for each of them.
        _settling = true;

        try
        {
            foreach (var group in _groups)
                if (!ReferenceEquals(group, opened)) group.IsExpanded = false;
        }
        finally
        {
            _settling = false;
        }
    }
}
