using Cirq.Components.Passive;
using Cirq.Components.Serialization;
using Cirq.Core.Topology;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The editor's half of multi-sheet: the tab strip, what page a new part lands on, and what happens
/// to a page when it is taken away.
/// <para>
/// The document's half — which parts are on which page, and how that survives a save — is in
/// Cirq.Components.Tests.SheetTests. Nothing here checks the solver, because sheets are invisible to
/// it: every page is handed over at once.
/// </para>
/// </summary>
public class SheetEditorTests : IDisposable
{
    private readonly List<string> _files = [];

    private string TempPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cirq-sheets-{Guid.NewGuid():N}{CircuitSerializer.FileExtension}");
        _files.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _files.Where(File.Exists)) File.Delete(path);
    }

    [Fact]
    public void ADrawingStartsWithNoTabsAtAll()
    {
        using var vm = new MainWindowViewModel();

        Assert.False(vm.HasSheets);
        Assert.Empty(vm.SheetTabs);
        Assert.Null(vm.CurrentSheet);
        Assert.Empty(vm.SheetSummary);
    }

    [Fact]
    public void AddingTheFirstSheetGoesToThePageItMade()
    {
        using var vm = new MainWindowViewModel();
        var parts = vm.Circuit.Components.Count;

        vm.AddSheetCommand.Execute(null);

        Assert.True(vm.HasSheets);
        Assert.Equal(["Sheet 1", "Sheet 2"], vm.SheetTabs.Select(t => t.Name));
        Assert.Equal("Sheet 2", vm.CurrentSheet);
        Assert.True(vm.IsModified);

        // The example that was already on screen stayed where it was, and the new page is empty.
        Assert.Equal(parts, vm.Circuit.OnSheet("Sheet 1").Count());
        Assert.Empty(vm.Circuit.OnSheet("Sheet 2"));

        // One tab is the current one, and it is the one that was gone to.
        Assert.Equal(["Sheet 2"], vm.SheetTabs.Where(t => t.IsCurrent).Select(t => t.Name));
    }

    [Fact]
    public void TheSummaryCountsWhatIsOnThePage()
    {
        using var vm = new MainWindowViewModel();
        var all = vm.Circuit.Components.Count;

        vm.AddSheetCommand.Execute(null);

        Assert.Equal($"0 of {all} parts", vm.SheetSummary);

        vm.ShowSheetCommand.Execute("Sheet 1");

        // Everything on one page, so the comparison would be saying the same number twice.
        Assert.Equal($"{all} parts", vm.SheetSummary);
    }

    [Fact]
    public void APastedPartLandsOnThePageBeingLookedAt()
    {
        using var vm = new MainWindowViewModel();

        var original = vm.Circuit.Components[0];
        original.IsSelected = true;
        vm.CopySelectionCommand.Execute(null);

        vm.AddSheetCommand.Execute(null);
        Assert.Equal("Sheet 2", vm.CurrentSheet);

        vm.PasteCommand.Execute(null);

        // The copy is on the page in front of somebody rather than beside a part they cannot see.
        Assert.Equal(["Sheet 2"], vm.Circuit.OnSheet("Sheet 2").Select(c => c.Sheet).Distinct());
        Assert.Single(vm.Circuit.OnSheet("Sheet 2"));
    }

    [Fact]
    public void RemovingAPageKeepsWhatWasOnIt()
    {
        using var vm = new MainWindowViewModel();

        vm.AddSheetCommand.Execute(null);
        vm.Circuit.Add(new Resistor(4700) { Name = "Rnew", Sheet = "Sheet 2" });

        var parts = vm.Circuit.Components.Count;

        vm.RemoveSheetCommand.Execute(null);

        Assert.Equal(["Sheet 1"], vm.SheetTabs.Select(t => t.Name));
        Assert.Equal("Sheet 1", vm.CurrentSheet);
        Assert.Equal(parts, vm.Circuit.Components.Count);
        Assert.Contains("moved to Sheet 1", vm.StatusMessage);
    }

    [Fact]
    public void TheLastPageCannotBeTakenAway()
    {
        using var vm = new MainWindowViewModel();

        vm.AddSheetCommand.Execute(null);
        vm.RemoveSheetCommand.Execute(null);

        Assert.Single(vm.SheetTabs);
        Assert.False(vm.RemoveSheetCommand.CanExecute(null));
    }

    [Fact]
    public void RenamingAPageBringsItsPartsAndItsTab()
    {
        using var vm = new MainWindowViewModel();

        vm.AddSheetCommand.Execute(null);
        vm.Circuit.Add(new Resistor(4700) { Name = "Rnew", Sheet = "Sheet 2" });

        Assert.True(vm.RenameSheet("Sheet 2", "Logic"));

        Assert.Equal(["Sheet 1", "Logic"], vm.SheetTabs.Select(t => t.Name));
        Assert.Equal("Logic", vm.CurrentSheet);
        Assert.Equal("Logic", vm.Circuit.Components.Single(c => c.Name == "Rnew").Sheet);
    }

    [Fact]
    public void ANameAnotherPageHasIsRefused()
    {
        using var vm = new MainWindowViewModel();

        vm.AddSheetCommand.Execute(null);

        Assert.False(vm.RenameSheet("Sheet 2", "Sheet 1"));
        Assert.Equal(["Sheet 1", "Sheet 2"], vm.SheetTabs.Select(t => t.Name));
        Assert.Contains("needs a name of its own", vm.StatusMessage);
    }

    [Fact]
    public void SteppingWrapsRoundTheSheets()
    {
        using var vm = new MainWindowViewModel();

        vm.AddSheetCommand.Execute(null);
        vm.AddSheetCommand.Execute(null);

        Assert.Equal("Sheet 3", vm.CurrentSheet);

        vm.NextSheetCommand.Execute(null);
        Assert.Equal("Sheet 1", vm.CurrentSheet);

        vm.PreviousSheetCommand.Execute(null);
        Assert.Equal("Sheet 3", vm.CurrentSheet);
    }

    [Fact]
    public void SteppingDoesNothingOnADrawingWithNoPages()
    {
        using var vm = new MainWindowViewModel();

        vm.NextSheetCommand.Execute(null);

        Assert.Null(vm.CurrentSheet);
        Assert.False(vm.HasSheets);
    }

    [Fact]
    public async Task OpeningACircuitBringsBackItsPagesAndItsNamedNumbers()
    {
        var path = TempPath();

        using (var authoring = new MainWindowViewModel())
        {
            authoring.AddSheetCommand.Execute(null);
            authoring.RenameSheet("Sheet 2", "Logic");
            authoring.Circuit.Add(new Resistor(4700) { Name = "Rlogic", Sheet = "Logic" });
            authoring.Circuit.Parameters.Add(new CircuitParameter { Name = "Rf", Expression = "12k" });

            Assert.True(await authoring.WriteToAsync(path));
        }

        using var vm = new MainWindowViewModel();
        vm.FileDialogs = new FakeFileDialogs { OpenPath = path };

        await vm.OpenCommand.ExecuteAsync(null);

        // The pages, the tabs built from them, and the numbers the circuit gives names to — all of
        // which were being dropped on the way in before sheets made it obvious.
        Assert.Equal(["Sheet 1", "Logic"], vm.Circuit.Sheets);
        Assert.Equal(["Sheet 1", "Logic"], vm.SheetTabs.Select(t => t.Name));
        Assert.Equal("Sheet 1", vm.CurrentSheet);
        Assert.Equal(["Rf"], vm.Circuit.Parameters.Select(p => p.Name));
        Assert.Equal("Logic", vm.Circuit.Components.Single(c => c.Name == "Rlogic").Sheet);
    }

    [Fact]
    public void ANewCircuitHasNoPagesLeftOver()
    {
        using var vm = new MainWindowViewModel();

        vm.AddSheetCommand.Execute(null);
        Assert.True(vm.HasSheets);

        vm.NewCircuitCommand.Execute(null);

        Assert.False(vm.HasSheets);
        Assert.Empty(vm.SheetTabs);
        Assert.Null(vm.CurrentSheet);
    }
}
