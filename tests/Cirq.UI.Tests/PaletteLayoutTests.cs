using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The palette's grouping and the collapsible side panels. These exist so the editor stays usable
/// in a narrow window, which is exactly the case that is awkward to check by hand.
/// </summary>
public class PaletteLayoutTests
{
    [Fact]
    public void PaletteIsGroupedWithOnlyTheFirstCategoryOpen()
    {
        using var vm = new MainWindowViewModel();

        Assert.Equal(ComponentCatalog.Categories.Count, vm.Palette.Count);
        Assert.True(vm.Palette[0].IsExpanded);
        Assert.All(vm.Palette.Skip(1), c => Assert.False(c.IsExpanded));
    }

    [Fact]
    public void EachGroupReportsHowManyPartsItHolds()
    {
        using var vm = new MainWindowViewModel();

        foreach (var category in vm.Palette)
        {
            Assert.Equal(category.Items.Count, category.Count);
            Assert.NotEmpty(category.Items);
        }

        // Every catalogue entry is reachable through exactly one group.
        Assert.Equal(ComponentCatalog.AllItems.Count(), vm.Palette.Sum(c => c.Count));
    }

    [Fact]
    public void TheGroupChevronPointsDownWhenOpenAndRightWhenClosed()
    {
        using var vm = new MainWindowViewModel();
        var group = vm.Palette[1];

        Assert.False(group.IsExpanded);
        Assert.Equal("▸", group.Chevron);

        group.IsExpanded = true;
        Assert.Equal("▾", group.Chevron);
    }

    [Fact]
    public void TheChevronIsNotifiedSoTheHeaderRedraws()
    {
        // The header draws the chevron from the view model rather than from the Expander's own
        // template, so it has to raise change notification for it.
        using var vm = new MainWindowViewModel();
        var group = vm.Palette[2];
        var changed = new List<string>();
        group.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        group.IsExpanded = true;

        Assert.Contains(nameof(PaletteCategoryViewModel.Chevron), changed);
    }

    [Fact]
    public void ToggleAllOpensEverythingThenClosesEverything()
    {
        using var vm = new MainWindowViewModel();

        vm.TogglePaletteGroupsCommand.Execute(null);
        Assert.All(vm.Palette, c => Assert.True(c.IsExpanded));

        vm.TogglePaletteGroupsCommand.Execute(null);
        Assert.All(vm.Palette, c => Assert.False(c.IsExpanded));
    }

    [Fact]
    public void ExpansionStateIsPerWindowNotShared()
    {
        // The categories themselves are static, so the expanded flag has to live on the wrapper.
        using var first = new MainWindowViewModel();
        using var second = new MainWindowViewModel();

        first.Palette[3].IsExpanded = true;

        Assert.False(second.Palette[3].IsExpanded);
    }

    [Fact]
    public void CollapsingASidePanelLeavesOnlyItsRail()
    {
        using var vm = new MainWindowViewModel();

        var expandedPalette = vm.PaletteColumnWidth.Value;
        var expandedInspector = vm.InspectorColumnWidth.Value;

        vm.IsPaletteExpanded = false;
        vm.IsInspectorExpanded = false;

        // A collapsed panel shrinks to the rail rather than vanishing, so there is always
        // something on screen to click to bring it back.
        Assert.Equal(MainWindowViewModel.CollapsedRailWidth, vm.PaletteColumnWidth.Value);
        Assert.Equal(MainWindowViewModel.CollapsedRailWidth, vm.InspectorColumnWidth.Value);
        Assert.True(vm.PaletteColumnWidth.Value < expandedPalette);
        Assert.True(vm.InspectorColumnWidth.Value < expandedInspector);

        // The splitters do go entirely: a collapsed panel cannot be dragged wider.
        Assert.Equal(0, vm.PaletteSplitterWidth.Value);
        Assert.Equal(0, vm.InspectorSplitterWidth.Value);
    }

    [Fact]
    public void CollapsingBothPanelsGivesAlmostTheWholeWindowToTheCanvas()
    {
        using var vm = new MainWindowViewModel();

        var before = vm.PaletteColumnWidth.Value + vm.PaletteSplitterWidth.Value
                     + vm.InspectorColumnWidth.Value + vm.InspectorSplitterWidth.Value;

        vm.IsPaletteExpanded = false;
        vm.IsInspectorExpanded = false;

        var after = vm.PaletteColumnWidth.Value + vm.PaletteSplitterWidth.Value
                    + vm.InspectorColumnWidth.Value + vm.InspectorSplitterWidth.Value;

        Assert.True(after < before * 0.2,
            $"Collapsing should reclaim most of the chrome: {before} px became {after} px.");
    }

    [Fact]
    public void ToggleCommandsFlipEachPanelIndependently()
    {
        using var vm = new MainWindowViewModel();

        vm.TogglePaletteCommand.Execute(null);
        Assert.False(vm.IsPaletteExpanded);
        Assert.True(vm.IsInspectorExpanded);

        vm.ToggleInspectorCommand.Execute(null);
        Assert.False(vm.IsInspectorExpanded);

        vm.TogglePaletteCommand.Execute(null);
        Assert.True(vm.IsPaletteExpanded);
        Assert.False(vm.IsInspectorExpanded);
    }

    [Fact]
    public void ChevronsPointTheWayThePanelWillMove()
    {
        using var vm = new MainWindowViewModel();

        // Expanded: the palette collapses leftwards, the inspector rightwards.
        Assert.Equal("❮", vm.PaletteToggleGlyph);
        Assert.Equal("❯", vm.InspectorToggleGlyph);

        vm.IsPaletteExpanded = false;
        vm.IsInspectorExpanded = false;

        // Collapsed: they now point back towards the canvas.
        Assert.Equal("❯", vm.PaletteToggleGlyph);
        Assert.Equal("❮", vm.InspectorToggleGlyph);
    }

    [Fact]
    public void PanelWidthsRaiseChangeNotificationSoTheGridRelayouts()
    {
        using var vm = new MainWindowViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        vm.IsPaletteExpanded = false;
        vm.IsInspectorExpanded = false;

        Assert.Contains(nameof(MainWindowViewModel.PaletteColumnWidth), changed);
        Assert.Contains(nameof(MainWindowViewModel.PaletteSplitterWidth), changed);
        Assert.Contains(nameof(MainWindowViewModel.PaletteToggleGlyph), changed);
        Assert.Contains(nameof(MainWindowViewModel.InspectorColumnWidth), changed);
        Assert.Contains(nameof(MainWindowViewModel.InspectorSplitterWidth), changed);
        Assert.Contains(nameof(MainWindowViewModel.InspectorToggleGlyph), changed);
    }

    [Fact]
    public void CollapsingAPanelSaysSoInTheStatusBar()
    {
        using var vm = new MainWindowViewModel();

        vm.IsPaletteExpanded = false;
        Assert.Contains("collapsed", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);

        vm.IsPaletteExpanded = true;
        Assert.Contains("shown", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BothPanelsStartExpanded()
    {
        using var vm = new MainWindowViewModel();

        Assert.True(vm.IsPaletteExpanded);
        Assert.True(vm.IsInspectorExpanded);
    }
}
