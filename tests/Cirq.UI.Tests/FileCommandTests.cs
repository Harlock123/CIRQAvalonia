using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Serialization;
using Cirq.UI.Services;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>Scripted stand-in for the file pickers, so the open/save flows can be driven headlessly.</summary>
internal sealed class FakeFileDialogs : ICircuitFileDialogs
{
    public string? OpenPath { get; set; }

    public string? SavePath { get; set; }

    public bool AllowDiscard { get; set; } = true;

    /// <summary>What to answer when asked whether to bring back an autosave.</summary>
    public bool AllowRecovery { get; set; }

    /// <summary>How many times recovery was offered, so a test can check it was asked once.</summary>
    public int RecoveryPrompts { get; private set; }

    public Task<bool> ConfirmRecoveryAsync(string name, string age)
    {
        RecoveryPrompts++;
        return Task.FromResult(AllowRecovery);
    }

    public int DiscardPrompts { get; private set; }

    public List<(string Title, string Message)> Reports { get; } = [];

    public string? LastSuggestedName { get; private set; }

    public Task<string?> PickOpenPathAsync() => Task.FromResult(OpenPath);

    /// <summary>The file an import should read, or null to stand in for a cancelled picker.</summary>
    public string? WaveformPath { get; set; }

    public Task<string?> PickWaveformPathAsync() => Task.FromResult(WaveformPath);

    public Task<string?> PickSavePathAsync(string suggestedFileName)
    {
        LastSuggestedName = suggestedFileName;
        return Task.FromResult(SavePath);
    }

    public Task<bool> ConfirmDiscardChangesAsync(string circuitTitle)
    {
        DiscardPrompts++;
        return Task.FromResult(AllowDiscard);
    }

    public Task ReportAsync(string title, string message)
    {
        Reports.Add((title, message));
        return Task.CompletedTask;
    }

    /// <summary>What the export dialog will answer; null stands for the user cancelling.</summary>
    public ExportRequest? ExportRequest { get; set; }

    public int ExportPrompts { get; private set; }

    public bool LastExportOfferedTraces { get; private set; }

    public Task<ExportRequest?> PickExportAsync(string suggestedFileName, bool hasTraces)
    {
        ExportPrompts++;
        LastSuggestedName = suggestedFileName;
        LastExportOfferedTraces = hasTraces;
        return Task.FromResult(ExportRequest);
    }
}

public class FileCommandTests : IDisposable
{
    private readonly List<string> _temporaryFiles = [];

    private string TempPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cirq-ui-{Guid.NewGuid():N}{CircuitSerializer.FileExtension}");
        _temporaryFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _temporaryFiles.Where(File.Exists)) File.Delete(path);
    }

    [Fact]
    public void ANewlyOpenedApplicationIsNotYetModified()
    {
        using var vm = new MainWindowViewModel();

        Assert.False(vm.IsModified);
        Assert.Null(vm.CurrentFilePath);
        Assert.Equal("Untitled", vm.DocumentName);
        Assert.Equal("Untitled - CirqAvalonia", vm.WindowTitle);
    }

    [Fact]
    public void PlacingAComponentMarksTheCircuitModified()
    {
        using var vm = new MainWindowViewModel();
        Assert.False(vm.IsModified);

        vm.Circuit.Add(new Resistor(1000));

        Assert.True(vm.IsModified);
        Assert.Equal("Untitled* - CirqAvalonia", vm.WindowTitle);
    }

    [Fact]
    public void EditingAComponentValueMarksTheCircuitModified()
    {
        using var vm = new MainWindowViewModel();
        var resistor = vm.Circuit.Components.OfType<Resistor>().First();

        Assert.False(vm.IsModified);
        resistor.Resistance = 22_000;

        Assert.True(vm.IsModified);
    }

    [Fact]
    public void SelectingAComponentDoesNotCountAsAnEdit()
    {
        // Selection is view state, not document state; marking the file dirty for it would mean
        // the asterisk appears just from clicking around.
        using var vm = new MainWindowViewModel();
        var component = vm.Circuit.Components.First();

        component.IsSelected = true;

        Assert.False(vm.IsModified);
    }

    [Fact]
    public async Task SavingWritesTheFileAndClearsTheModifiedFlag()
    {
        using var vm = new MainWindowViewModel();
        var path = TempPath();

        vm.Circuit.Add(new Resistor(3300) { Name = "RTest" });
        Assert.True(vm.IsModified);

        Assert.True(await vm.WriteToAsync(path));

        Assert.True(File.Exists(path));
        Assert.False(vm.IsModified);
        Assert.Equal(path, vm.CurrentFilePath);
        Assert.Equal(Path.GetFileNameWithoutExtension(path), vm.DocumentName);
        Assert.DoesNotContain("*", vm.WindowTitle);
    }

    [Fact]
    public async Task SaveUsesThePickerOnlyUntilAPathIsKnown()
    {
        using var vm = new MainWindowViewModel();
        var path = TempPath();
        var dialogs = new FakeFileDialogs { SavePath = path };
        vm.FileDialogs = dialogs;

        // First save has nowhere to go, so it asks.
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(path, vm.CurrentFilePath);
        Assert.NotNull(dialogs.LastSuggestedName);

        // Second save goes straight to the same file.
        dialogs.SavePath = null;
        vm.Circuit.Add(new Resistor());
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(path, vm.CurrentFilePath);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public async Task OpeningRestoresTheCircuitIntoTheLiveDocument()
    {
        var path = TempPath();

        // Save a distinctive circuit from one session.
        using (var authoring = new MainWindowViewModel())
        {
            authoring.Circuit.Clear();
            authoring.Circuit.Title = "Saved circuit";
            var resistor = authoring.Circuit.Add(new Resistor(6800));
            var diode = authoring.Circuit.Add(new Diode(DiodeModel.D1N4001));
            authoring.Circuit.Connect(resistor.B, diode.Anode);
            Assert.True(await authoring.WriteToAsync(path));
        }

        using var vm = new MainWindowViewModel();
        var originalCircuit = vm.Circuit;

        Assert.True(await vm.LoadFromAsync(path));

        // The same Circuit instance is reused, because the canvas and scope are bound to it.
        Assert.Same(originalCircuit, vm.Circuit);
        Assert.Equal("Saved circuit", vm.Circuit.Title);
        Assert.Equal(6800, vm.Circuit.Components.OfType<Resistor>().Single().Resistance);
        Assert.Equal("1N4001", vm.Circuit.Components.OfType<Diode>().Single().Model.Name);
        Assert.Single(vm.Circuit.Wires);
        Assert.False(vm.IsModified);
        Assert.Equal(path, vm.CurrentFilePath);
    }

    [Fact]
    public async Task AnOpenedCircuitIsImmediatelySimulable()
    {
        var path = TempPath();

        using (var authoring = new MainWindowViewModel())
        {
            Assert.True(await authoring.WriteToAsync(path));
        }

        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();

        Assert.True(await vm.LoadFromAsync(path));

        Assert.NotNull(vm.Simulation.Simulator);
        Assert.False(vm.Simulation.HasError);
        Assert.StartsWith("Ready", vm.Simulation.Status);

        // And it actually steps.
        vm.Simulation.StepOnce();
        Assert.True(vm.Simulation.SimulationTime > 0);
    }

    [Fact]
    public async Task ProbesSurviveASaveAndReopen()
    {
        var path = TempPath();

        using (var authoring = new MainWindowViewModel())
        {
            Assert.Equal(2, authoring.Scope.Probes.Count);
            Assert.True(await authoring.WriteToAsync(path));
        }

        using var vm = new MainWindowViewModel();
        Assert.True(await vm.LoadFromAsync(path));

        Assert.Equal(2, vm.Scope.Probes.Count);
        Assert.All(vm.Scope.Probes, p => Assert.NotNull(p.TargetTerminal));
        Assert.Contains(vm.Scope.Probes, p => p.Label == "Input");
        Assert.Contains(vm.Scope.Probes, p => p.Label == "Output");
    }

    [Fact]
    public async Task OpeningPromptsBeforeDiscardingUnsavedWork()
    {
        var path = TempPath();
        using (var authoring = new MainWindowViewModel())
        {
            Assert.True(await authoring.WriteToAsync(path));
        }

        using var vm = new MainWindowViewModel();
        var dialogs = new FakeFileDialogs { OpenPath = path, AllowDiscard = false };
        vm.FileDialogs = dialogs;

        vm.Circuit.Add(new Resistor());
        Assert.True(vm.IsModified);
        var componentCount = vm.Circuit.Components.Count;

        await vm.OpenCommand.ExecuteAsync(null);

        // The user declined, so nothing was thrown away.
        Assert.Equal(1, dialogs.DiscardPrompts);
        Assert.Equal(componentCount, vm.Circuit.Components.Count);
        Assert.True(vm.IsModified);
        Assert.Null(vm.CurrentFilePath);
    }

    [Fact]
    public async Task OpeningDoesNotPromptWhenThereIsNothingToLose()
    {
        var path = TempPath();
        using (var authoring = new MainWindowViewModel())
        {
            Assert.True(await authoring.WriteToAsync(path));
        }

        using var vm = new MainWindowViewModel();
        var dialogs = new FakeFileDialogs { OpenPath = path };
        vm.FileDialogs = dialogs;

        Assert.False(vm.IsModified);
        await vm.OpenCommand.ExecuteAsync(null);

        Assert.Equal(0, dialogs.DiscardPrompts);
        Assert.Equal(path, vm.CurrentFilePath);
    }

    [Fact]
    public async Task CancellingThePickerChangesNothing()
    {
        using var vm = new MainWindowViewModel();
        vm.FileDialogs = new FakeFileDialogs { OpenPath = null };
        var before = vm.Circuit.Components.Count;

        await vm.OpenCommand.ExecuteAsync(null);

        Assert.Equal(before, vm.Circuit.Components.Count);
        Assert.Null(vm.CurrentFilePath);
    }

    [Fact]
    public async Task AnUnreadableFileIsReportedWithoutLosingTheCurrentCircuit()
    {
        var path = TempPath();
        File.WriteAllText(path, "this is not a circuit");

        using var vm = new MainWindowViewModel();
        var dialogs = new FakeFileDialogs();
        vm.FileDialogs = dialogs;
        var before = vm.Circuit.Components.Count;

        Assert.False(await vm.LoadFromAsync(path));

        Assert.Equal(before, vm.Circuit.Components.Count);
        Assert.Null(vm.CurrentFilePath);
        Assert.Single(dialogs.Reports);
        Assert.Contains("Could not open", dialogs.Reports[0].Title);
    }

    [Fact]
    public async Task AFileWithWarningsStillOpensAndSaysWhatWasLost()
    {
        var path = TempPath();
        File.WriteAllText(path, """
        {
          "Version": 1,
          "Title": "Mostly fine",
          "Components": [
            { "Id": "11111111-1111-1111-1111-111111111111", "Type": "Resistor", "Name": "R1",
              "Parameters": { "Resistance": 1500 } },
            { "Id": "22222222-2222-2222-2222-222222222222", "Type": "Teleporter", "Name": "U1",
              "Parameters": {} }
          ]
        }
        """);

        using var vm = new MainWindowViewModel();
        var dialogs = new FakeFileDialogs();
        vm.FileDialogs = dialogs;

        Assert.True(await vm.LoadFromAsync(path));

        Assert.Equal("Mostly fine", vm.Circuit.Title);
        Assert.Equal(1500, vm.Circuit.Components.OfType<Resistor>().Single().Resistance);
        Assert.Single(dialogs.Reports);
        Assert.Contains("warnings", dialogs.Reports[0].Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartingANewCircuitPromptsThenClearsTheFileAssociation()
    {
        var path = TempPath();
        using var vm = new MainWindowViewModel();
        vm.FileDialogs = new FakeFileDialogs { AllowDiscard = true };

        Assert.True(await vm.WriteToAsync(path));
        vm.Circuit.Add(new Resistor());

        await vm.NewCircuitCommand.ExecuteAsync(null);

        Assert.Empty(vm.Circuit.Components);
        Assert.Null(vm.CurrentFilePath);
        Assert.False(vm.IsModified);
        Assert.Equal("Untitled - CirqAvalonia", vm.WindowTitle);
    }

    [Fact]
    public async Task LoadingAnExampleStartsAFreshUnsavedDocument()
    {
        var path = TempPath();
        using var vm = new MainWindowViewModel();
        Assert.True(await vm.WriteToAsync(path));
        Assert.NotNull(vm.CurrentFilePath);

        var example = Examples.All.First(e => e.Name == "555 Astable");
        vm.LoadExampleCommand.Execute(example);

        // An example is not the file you had open, so it must not overwrite it on the next save.
        Assert.Null(vm.CurrentFilePath);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public async Task EveryExampleSurvivesASaveAndReopen()
    {
        // The broadest round-trip there is: each shipped circuit, through the file format, still
        // compiles and reports the same node count.
        foreach (var example in Examples.All)
        {
            var path = TempPath();
            int nodesBefore;

            using (var authoring = new MainWindowViewModel())
            {
                authoring.Circuit.Clear();
                authoring.Scope.ClearProbes();
                example.Build(authoring);
                Assert.True(authoring.Simulation.Rebuild(), $"'{example.Name}' failed to compile before saving.");
                nodesBefore = authoring.Simulation.Simulator!.Netlist.NodeCount;
                Assert.True(await authoring.WriteToAsync(path));
            }

            using var reopened = new MainWindowViewModel();
            Assert.True(await reopened.LoadFromAsync(path), $"'{example.Name}' failed to reopen.");

            Assert.False(reopened.Simulation.HasError, $"'{example.Name}': {reopened.Simulation.Status}");
            Assert.Equal(nodesBefore, reopened.Simulation.Simulator!.Netlist.NodeCount);
        }
    }
}
