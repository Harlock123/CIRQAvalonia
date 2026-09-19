using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The menu is now the only place several commands live, so these check the view-model side of it:
/// the tool radio state, the speed submenu, and the canvas actions the menu raises as events.
/// </summary>
public class MenuCommandTests
{
    [Fact]
    public void ExactlyOneToolIsCheckedAtATime()
    {
        using var vm = new MainWindowViewModel();

        Assert.True(vm.IsSelectTool);
        Assert.False(vm.IsWireTool);

        vm.ActiveTool = EditorTool.Wire;

        Assert.False(vm.IsSelectTool);
        Assert.True(vm.IsWireTool);
        Assert.False(vm.IsProbeTool);
        Assert.False(vm.IsDeleteTool);
    }

    [Fact]
    public void CheckingAToolMenuItemSelectsThatTool()
    {
        using var vm = new MainWindowViewModel();

        vm.IsProbeTool = true;
        Assert.Equal(EditorTool.Probe, vm.ActiveTool);

        vm.IsDeleteTool = true;
        Assert.Equal(EditorTool.Delete, vm.ActiveTool);
    }

    [Fact]
    public void UncheckingARadioItemDoesNotClearTheTool()
    {
        // A radio group deselects the outgoing item; that must not leave the editor with no tool.
        using var vm = new MainWindowViewModel();
        vm.ActiveTool = EditorTool.Wire;

        vm.IsSelectTool = false;

        Assert.Equal(EditorTool.Wire, vm.ActiveTool);
    }

    [Fact]
    public void ChangingToolRaisesNotificationForEveryMenuBinding()
    {
        using var vm = new MainWindowViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        vm.ActiveTool = EditorTool.Probe;

        Assert.Contains(nameof(MainWindowViewModel.IsSelectTool), changed);
        Assert.Contains(nameof(MainWindowViewModel.IsWireTool), changed);
        Assert.Contains(nameof(MainWindowViewModel.IsProbeTool), changed);
        Assert.Contains(nameof(MainWindowViewModel.IsDeleteTool), changed);
        Assert.Contains(nameof(MainWindowViewModel.ActiveToolName), changed);
    }

    [Fact]
    public void TheStatusBarNamesTheActiveTool()
    {
        using var vm = new MainWindowViewModel();
        vm.ActiveTool = EditorTool.Wire;

        Assert.Equal("Wire", vm.ActiveToolName);
    }

    [Fact]
    public void RotateAndDeleteAreRaisedForTheCanvasToCarryOut()
    {
        using var vm = new MainWindowViewModel();
        var rotated = 0;
        var deleted = 0;

        vm.RequestRotateSelection += (_, _) => rotated++;
        vm.RequestDeleteSelection += (_, _) => deleted++;

        vm.RotateSelectionCommand.Execute(null);
        vm.DeleteSelectionCommand.Execute(null);

        Assert.Equal(1, rotated);
        Assert.Equal(1, deleted);
    }

    [Fact]
    public void ZoomToFitAndExitAreRaisedToTheWindow()
    {
        using var vm = new MainWindowViewModel();
        var fitted = 0;
        var closed = 0;

        vm.RequestZoomToFit += (_, _) => fitted++;
        vm.RequestClose += (_, _) => closed++;

        vm.ZoomToFitCommand.Execute(null);
        vm.ExitCommand.Execute(null);

        Assert.Equal(1, fitted);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void TheSpeedMenuOffersRealTimeThroughMaximum()
    {
        using var vm = new MainWindowViewModel();

        Assert.Equal(6, vm.SpeedOptions.Count);
        Assert.Contains(vm.SpeedOptions, o => o.Factor == 1.0);
        Assert.Single(vm.SpeedOptions, o => o.IsMaximum);
    }

    [Fact]
    public void PickingASpeedAppliesItAndMarksExactlyOneEntry()
    {
        using var vm = new MainWindowViewModel();
        var tenth = vm.SpeedOptions.First(o => o.Factor == 0.1);

        vm.SetSpeedCommand.Execute(tenth);

        Assert.Equal(0.1, vm.Simulation.SpeedFactor);
        Assert.False(vm.Simulation.IsMaximumThroughput);
        Assert.Single(vm.SpeedOptions, o => o.IsSelected);
        Assert.True(tenth.IsSelected);
    }

    [Fact]
    public void PickingMaximumThroughputLeavesRealTimePacingBehind()
    {
        using var vm = new MainWindowViewModel();
        var maximum = vm.SpeedOptions.Single(o => o.IsMaximum);

        vm.SetSpeedCommand.Execute(maximum);

        Assert.True(vm.Simulation.IsMaximumThroughput);
        Assert.True(maximum.IsSelected);
        Assert.Equal("max speed", vm.SpeedLabel);
    }

    [Fact]
    public void TheStatusBarReportsTheChosenSpeed()
    {
        using var vm = new MainWindowViewModel();
        var hundredth = vm.SpeedOptions.First(o => o.Factor == 0.01);

        vm.SetSpeedCommand.Execute(hundredth);

        Assert.Equal(hundredth.Label, vm.SpeedLabel);
    }

    [Fact]
    public void TheShortcutReferenceCoversEveryCommandTheMenusExpose()
    {
        // The menus show gestures, but the Help entry is where someone actually goes looking.
        foreach (var gesture in new[]
                 {
                     "Ctrl+N", "Ctrl+O", "Ctrl+S", "Ctrl+Shift+S",
                     "F5", "F6", "F8", "F9", "F10",
                     "V", "W", "P", "R", "Delete", "Esc", "F",
                 })
        {
            Assert.Contains(gesture, MainWindowViewModel.ShortcutReference);
        }
    }
}
