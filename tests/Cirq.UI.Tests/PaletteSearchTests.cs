using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// Finding a part among 183 of them.
/// <para>
/// The palette is sixteen collapsible groups, and browsing only works if you already know which
/// one a part lives in — that a 4017 is under "40xx Series", an optocoupler under "Switching &amp;
/// Isolation", a thermistor under "Sensors &amp; Actuators". Somebody who knows they want a 555
/// should not have to guess where it was filed.
/// </para>
/// </summary>
public class PaletteSearchTests
{
    private static IEnumerable<PaletteItem> Shown(MainWindowViewModel vm) =>
        vm.Palette.SelectMany(c => c.Items);

    [Fact]
    public void EverythingIsThereBeforeAnythingIsTyped()
    {
        using var vm = new MainWindowViewModel();

        Assert.Equal(ComponentCatalog.Categories.Count, vm.Palette.Count);
        Assert.Equal(ComponentCatalog.Categories.Sum(c => c.Items.Count), Shown(vm).Count());
        Assert.False(vm.IsPaletteEmpty);
        Assert.False(vm.IsPaletteSearching);
    }

    /// <summary>Everything closed to begin with, so all sixteen headings are on screen at once.</summary>
    [Fact]
    public void TheGroupsStartClosed()
    {
        using var vm = new MainWindowViewModel();

        Assert.All(vm.Palette, c => Assert.False(c.IsExpanded));
    }

    [Theory]
    [InlineData("555", "NE555")]
    [InlineData("4017", "4017")]
    [InlineData("74164", "74164")]
    public void ThePartNumberOnAChipFindsIt(string term, string expected)
    {
        using var vm = new MainWindowViewModel { PaletteSearch = term };

        Assert.Contains(Shown(vm), i => i.Name == expected);
    }

    /// <summary>
    /// What a part does, not only what it is called. People look for "a shift register" far more
    /// often than for the number printed on one.
    /// </summary>
    [Theory]
    [InlineData("shift register")]
    [InlineData("galvanically")]
    [InlineData("photovoltaic")]
    public void WhatAPartDoesFindsItToo(string term)
    {
        using var vm = new MainWindowViewModel { PaletteSearch = term };

        var found = Shown(vm).ToList();

        Assert.NotEmpty(found);

        // None of them is named that, so the only way they were found is by what they do.
        Assert.All(found, i =>
            Assert.DoesNotContain(term, i.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The group name counts as well, so "logic" gives you the logic gates even though not one of
    /// them has the word in its name.
    /// </summary>
    [Fact]
    public void AGroupNameFindsEverythingInIt()
    {
        using var vm = new MainWindowViewModel { PaletteSearch = "Logic Gates" };

        var group = Assert.Single(vm.Palette);

        Assert.Equal("Logic Gates", group.Name);
        Assert.Equal(
            ComponentCatalog.Categories.Single(c => c.Name == "Logic Gates").Items.Count,
            group.Count);
    }

    [Fact]
    public void SearchingIgnoresCase()
    {
        using var lower = new MainWindowViewModel { PaletteSearch = "ne555" };
        using var upper = new MainWindowViewModel { PaletteSearch = "NE555" };

        Assert.Equal(Shown(lower).Select(i => i.Name), Shown(upper).Select(i => i.Name));
    }

    /// <summary>
    /// A search opens every group it matched. A match folded away inside a closed group is the
    /// same as not matching at all.
    /// </summary>
    [Fact]
    public void ASearchOpensWhatItMatched()
    {
        using var vm = new MainWindowViewModel { PaletteSearch = "diode" };

        Assert.NotEmpty(vm.Palette);
        Assert.All(vm.Palette, c => Assert.True(c.IsExpanded, $"{c.Name} should be open"));
    }

    /// <summary>A group with nothing left in it is gone, rather than sitting there empty.</summary>
    [Fact]
    public void GroupsWithNoMatchesAreNotShown()
    {
        using var vm = new MainWindowViewModel { PaletteSearch = "NE555" };

        Assert.All(vm.Palette, c => Assert.NotEmpty(c.Items));
        Assert.True(vm.Palette.Count < ComponentCatalog.Categories.Count);
    }

    [Fact]
    public void ASearchThatMatchesNothingSaysSoRatherThanShowingEverything()
    {
        using var vm = new MainWindowViewModel { PaletteSearch = "kryptonite" };

        Assert.Empty(vm.Palette);
        Assert.True(vm.IsPaletteEmpty);
        Assert.Contains("0 of", vm.PaletteSummary);
    }

    [Fact]
    public void ClearingTheSearchPutsEverythingBack()
    {
        using var vm = new MainWindowViewModel { PaletteSearch = "NE555" };

        vm.ClearPaletteSearchCommand.Execute(null);

        Assert.Equal(string.Empty, vm.PaletteSearch);
        Assert.Equal(ComponentCatalog.Categories.Count, vm.Palette.Count);
        Assert.False(vm.IsPaletteSearching);
    }

    /// <summary>
    /// The group somebody had open survives a search and comes back when it is cleared. A search
    /// opens everything, so what is on screen during one says nothing about what they chose.
    /// </summary>
    [Fact]
    public void TheGroupThatWasOpenComesBackAfterASearch()
    {
        using var vm = new MainWindowViewModel();

        vm.Palette.Single(c => c.Name == "Passive").IsExpanded = true;

        vm.PaletteSearch = "NE555";
        vm.PaletteSearch = string.Empty;

        Assert.True(vm.Palette.Single(c => c.Name == "Passive").IsExpanded);
        Assert.All(vm.Palette.Where(c => c.Name != "Passive"), c => Assert.False(c.IsExpanded));
    }

    /// <summary>
    /// Opening a group during a search is still the person opening a group, so it is the one that
    /// comes back — not whatever was open before they started typing.
    /// </summary>
    [Fact]
    public void AGroupOpenedWhileSearchingIsNotMistakenForTheSearchsDoing()
    {
        using var vm = new MainWindowViewModel();

        vm.Palette.Single(c => c.Name == "Passive").IsExpanded = true;
        vm.PaletteSearch = "NE555";
        vm.PaletteSearch = string.Empty;

        // Back to Passive, and now a deliberate change with no search in the way.
        vm.Palette.Single(c => c.Name == "Sources").IsExpanded = true;

        vm.PaletteSearch = "NE555";
        vm.PaletteSearch = string.Empty;

        Assert.True(vm.Palette.Single(c => c.Name == "Sources").IsExpanded);
    }

    /// <summary>The summary says how much of the catalogue is on screen, so a filter is never silent.</summary>
    [Fact]
    public void TheSummaryCountsWhatIsShowingAgainstTheWhole()
    {
        var total = ComponentCatalog.Categories.Sum(c => c.Items.Count);

        using var all = new MainWindowViewModel();
        Assert.Contains($"{total} parts", all.PaletteSummary);

        using var some = new MainWindowViewModel { PaletteSearch = "NE555" };
        Assert.Contains($"{some.Palette.Sum(c => c.Count)} of {total}", some.PaletteSummary);
    }

    /// <summary>
    /// Ctrl+F, type, Enter, click: the part is armed for placement without the mouse ever going
    /// near the palette.
    /// </summary>
    [Fact]
    public void EnterArmsTheFirstMatchForPlacement()
    {
        using var vm = new MainWindowViewModel { PaletteSearch = "NE555" };

        vm.ChooseFirstMatchCommand.Execute(null);

        Assert.Equal("NE555", vm.PendingItem?.Name);
    }

    [Fact]
    public void EnterOnASearchThatMatchedNothingArmsNothing()
    {
        using var vm = new MainWindowViewModel { PaletteSearch = "kryptonite" };

        vm.ChooseFirstMatchCommand.Execute(null);

        Assert.Null(vm.PendingItem);
    }

    /// <summary>Asking for the search box opens the palette first — it cannot be typed into folded away.</summary>
    [Fact]
    public void AskingForTheSearchBoxOpensThePalette()
    {
        using var vm = new MainWindowViewModel { IsPaletteExpanded = false };

        var asked = false;
        vm.FocusPaletteSearchRequested += (_, _) => asked = true;

        vm.FocusPaletteSearchCommand.Execute(null);

        Assert.True(vm.IsPaletteExpanded);
        Assert.True(asked);
    }

    /// <summary>
    /// Every part in the catalogue can be found by its own name. A part nobody can search for is
    /// one that has to be browsed to, which is what this exists to stop.
    /// </summary>
    [Fact]
    public void EveryPartCanBeFoundByItsOwnName()
    {
        using var vm = new MainWindowViewModel();

        foreach (var item in ComponentCatalog.Categories.SelectMany(c => c.Items))
        {
            vm.PaletteSearch = item.Name;

            Assert.Contains(Shown(vm), i => i.Name == item.Name);
        }
    }
}
