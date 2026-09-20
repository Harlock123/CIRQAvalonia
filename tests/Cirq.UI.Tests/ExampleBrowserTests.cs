using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The browser that replaced the Examples submenu. The point of it is that the set can grow
/// without the way in getting worse, so the tests are about grouping, searching and choosing
/// rather than about any particular example.
/// </summary>
public class ExampleBrowserTests
{
    [Fact]
    public void EveryExampleIsFiledUnderExactlyOneGroup()
    {
        var all = Examples.Categories.SelectMany(c => c.Items.Select(e => e.Name)).ToList();

        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(Examples.All.Count, all.Count);
    }

    /// <summary>
    /// The flattened list carries the group each example came from, so the details pane can say
    /// where a thing lives without the browser having to look it up.
    /// </summary>
    [Fact]
    public void TheFlattenedListKnowsWhichGroupEachCameFrom()
    {
        Assert.All(Examples.All, e => Assert.NotEmpty(e.Category));

        var rc = Examples.All.Single(e => e.Name == "RC Low-Pass");
        Assert.Equal("Fundamentals", rc.Category);

        // And the browser's rows carry it too, so the details pane can show it.
        var browser = new ExampleBrowserViewModel();
        Assert.Equal("Fundamentals", browser.Groups[0].Items[0].Example.Category);
    }

    [Fact]
    public void NoGroupIsEmptyAndNoneIsEnormous()
    {
        Assert.All(Examples.Categories, c => Assert.NotEmpty(c.Items));

        // The whole point was to stop a single list being too long to read.
        Assert.All(Examples.Categories, c => Assert.True(c.Items.Count <= 12, $"{c.Name} has {c.Items.Count}"));
    }

    [Fact]
    public void ItOpensShowingEverything()
    {
        var browser = new ExampleBrowserViewModel();

        Assert.Equal(Examples.Categories.Count, browser.Groups.Count);
        Assert.Equal(Examples.All.Count, browser.MatchCount);
        Assert.False(browser.IsEmpty);
    }

    /// <summary>Something is always described, so the right-hand pane is never blank.</summary>
    [Fact]
    public void AndWithSomethingAlreadySelected()
    {
        var browser = new ExampleBrowserViewModel();

        Assert.NotNull(browser.Selected);
        Assert.True(browser.Groups[0].Items[0].IsSelected);
    }

    /// <summary>
    /// Twelve groups' worth of examples will not fit on one screen, so none of them opens until
    /// somebody asks — which puts every heading in view instead of one group's contents.
    /// </summary>
    [Fact]
    public void ItOpensWithEveryGroupClosed()
    {
        var browser = new ExampleBrowserViewModel();

        Assert.All(browser.Groups, g => Assert.False(g.IsExpanded));
    }

    /// <summary>And opening one closes the last, so the headings never scroll away.</summary>
    [Fact]
    public void OpeningAGroupClosesTheOneThatWasOpen()
    {
        var browser = new ExampleBrowserViewModel();

        browser.Groups[1].IsExpanded = true;
        Assert.Single(browser.Groups, g => g.IsExpanded);

        browser.Groups[4].IsExpanded = true;

        Assert.False(browser.Groups[1].IsExpanded);
        Assert.True(browser.Groups[4].IsExpanded);
        Assert.Single(browser.Groups, g => g.IsExpanded);
    }

    /// <summary>
    /// A search is the exception, and has to be: a match folded away inside a closed group is the
    /// same as no match at all.
    /// </summary>
    [Fact]
    public void ASearchOpensEveryGroupItMatched()
    {
        var browser = new ExampleBrowserViewModel { Search = "e" };

        Assert.True(browser.Groups.Count > 1, "this search was meant to match several groups");
        Assert.All(browser.Groups, g => Assert.True(g.IsExpanded));
    }

    /// <summary>And clearing it closes them again rather than leaving twelve groups open.</summary>
    [Fact]
    public void AndClearingItClosesThemAgain()
    {
        var browser = new ExampleBrowserViewModel { Search = "e" };

        browser.Search = string.Empty;

        Assert.All(browser.Groups, g => Assert.False(g.IsExpanded));
    }

    /// <summary>
    /// The group left open survives a search and comes back with it, because the search rebuilds
    /// the list from scratch and the state is matched up by name.
    /// </summary>
    [Fact]
    public void TheOpenGroupIsStillOpenAfterASearchIsCleared()
    {
        var browser = new ExampleBrowserViewModel();
        var name = browser.Groups[3].Name;

        browser.Groups[3].IsExpanded = true;

        browser.Search = "zzzz";
        browser.Search = string.Empty;

        var group = browser.Groups.Single(g => g.Name == name);
        Assert.True(group.IsExpanded);
        Assert.Single(browser.Groups, g => g.IsExpanded);
    }

    [Fact]
    public void SearchingNarrowsItToWhatMatches()
    {
        var browser = new ExampleBrowserViewModel { Search = "I2C" };

        Assert.True(browser.MatchCount > 0);
        Assert.True(browser.MatchCount < Examples.All.Count);

        Assert.All(browser.Groups.SelectMany(g => g.Items), row =>
            Assert.Contains("i2c", (row.Name + row.Description).ToLowerInvariant()));
    }

    /// <summary>
    /// The description and the group name are searched as well as the name, because people look
    /// for what a circuit does at least as often as for what it is called.
    /// </summary>
    [Fact]
    public void SearchLooksAtMoreThanTheName()
    {
        var byDescription = new ExampleBrowserViewModel { Search = "hysteresis" };
        var byGroup = new ExampleBrowserViewModel { Search = "Sensors" };

        Assert.Contains(byDescription.Groups.SelectMany(g => g.Items), r => r.Name == "Noise and Hysteresis");
        Assert.Single(byGroup.Groups);
        Assert.Equal("Sensors", byGroup.Groups[0].Name);
    }

    /// <summary>
    /// A search opens the groups it matched in. Leaving a match folded away inside a closed group
    /// is the same as not matching at all.
    /// </summary>
    [Fact]
    public void SearchingOpensWhateverItMatched()
    {
        var browser = new ExampleBrowserViewModel();

        foreach (var group in browser.Groups) group.IsExpanded = false;

        browser.Search = "counter";

        Assert.All(browser.Groups, g => Assert.True(g.IsExpanded));
    }

    [Fact]
    public void ASearchThatMatchesNothingSaysSo()
    {
        var browser = new ExampleBrowserViewModel { Search = "zzzznothing" };

        Assert.True(browser.IsEmpty);
        Assert.Equal(0, browser.MatchCount);
        Assert.Null(browser.Selected);
    }

    [Fact]
    public void ClearingTheSearchBringsEverythingBack()
    {
        var browser = new ExampleBrowserViewModel { Search = "I2C" };
        browser.Search = string.Empty;

        Assert.Equal(Examples.All.Count, browser.MatchCount);
    }

    /// <summary>Only one row is marked at a time, across all the groups rather than within one.</summary>
    [Fact]
    public void SelectingARowUnmarksTheRest()
    {
        var browser = new ExampleBrowserViewModel();
        var target = browser.Groups[2].Items[1];

        browser.SelectCommand.Execute(target);

        Assert.Same(target.Example, browser.Selected);
        Assert.Single(browser.Groups.SelectMany(g => g.Items), r => r.IsSelected);
        Assert.True(target.IsSelected);
    }

    [Fact]
    public void OpeningClosesTheDialogWithAChoice()
    {
        var browser = new ExampleBrowserViewModel();
        var closed = 0;
        browser.RequestClose += (_, _) => closed++;

        var wanted = browser.Groups[1].Items[0];
        browser.OpenRowCommand.Execute(wanted);

        Assert.Equal(1, closed);
        Assert.Same(wanted.Example, browser.Chosen);
    }

    [Fact]
    public void CancellingClosesItWithout()
    {
        var browser = new ExampleBrowserViewModel();
        var closed = 0;
        browser.RequestClose += (_, _) => closed++;

        browser.CancelCommand.Execute(null);

        Assert.Equal(1, closed);
        Assert.Null(browser.Chosen);
    }

    /// <summary>The summary says what is on offer, and changes to say what matched.</summary>
    [Fact]
    public void TheSummaryCountsWhatIsShowing()
    {
        var browser = new ExampleBrowserViewModel();

        Assert.Contains(Examples.All.Count.ToString(), browser.Summary);

        browser.Search = "I2C";

        Assert.Contains("match", browser.Summary);
        Assert.Contains(browser.MatchCount.ToString(), browser.Summary);
    }
}
