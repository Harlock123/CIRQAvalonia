using Cirq.Components.Nonlinear;
using Cirq.Components.Spice;
using Cirq.UI.Services;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The SPICE import window and the store behind it, both pointed at a temporary file — a test run
/// that edited somebody's own imported models would be a poor trade for the convenience.
/// </summary>
public class SpiceImportViewModelTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"cirq-models-{Guid.NewGuid():N}.json");

    private readonly List<string> _registered = [];

    public void Dispose()
    {
        foreach (var name in _registered)
        {
            DiodeModel.Unregister(name);
            BjtModel.Unregister(name);
            MosfetModel.Unregister(name);
        }

        if (File.Exists(_path)) File.Delete(_path);
    }

    private SpiceImportViewModel New()
    {
        return new SpiceImportViewModel(new UserModelStore(_path));
    }

    private string Card(string name, string body)
    {
        _registered.Add(name);
        return $".model {name} {body}";
    }

    [Fact]
    public void ImportingACardAddsItToTheListAndToTheLibrary()
    {
        var model = New();

        var before = DiodeModel.Library.Count;

        model.Text = Card("TESTDIODE", "D(Is=2.52n Rs=0.568 N=1.752 Bv=75)");
        model.ImportCommand.Execute(null);

        Assert.False(model.IsEmpty);
        Assert.Equal("TESTDIODE", Assert.Single(model.Imported).Name);
        Assert.Equal(before + 1, DiodeModel.Library.Count);

        // The box is cleared on a clean import, and the report names what was used.
        Assert.Empty(model.Text);
        Assert.Contains("TESTDIODE", model.Status);
        Assert.Contains("diode", model.Status);
    }

    [Fact]
    public void ImportedModelsComeBackAfterARestart()
    {
        var model = New();
        model.Text = Card("TESTBJT", "NPN(Is=7f Bf=300 Vaf=60)");
        model.ImportCommand.Execute(null);

        // A fresh store over the same file, as a new run of the application would have. Take the
        // model out of the library first, so this proves the store put it back.
        Assert.True(BjtModel.Unregister("TESTBJT"));
        Assert.DoesNotContain(BjtModel.Library, m => m.Name == "TESTBJT");

        var reopened = new SpiceImportViewModel(new UserModelStore(_path));

        Assert.Single(reopened.Imported);
        Assert.Contains(BjtModel.Library, m => m.Name == "TESTBJT");
    }

    [Fact]
    public void ACardForADeviceWithNoModelIsReportedAndTheBoxIsLeftAlone()
    {
        var model = New();

        model.Text = ".model TESTJFET NJF(Vto=-2 Beta=1m)";
        model.ImportCommand.Execute(null);

        Assert.True(model.IsEmpty);
        Assert.Contains("NJF", model.Status);

        // Text that produced a complaint is text somebody is about to correct.
        Assert.NotEmpty(model.Text);
    }

    [Fact]
    public void ParametersThatCannotBeUsedAreNamedInTheReport()
    {
        var model = New();

        model.Text = Card("TESTCJO", "D(Is=1n N=1.8 Cjo=4p Tt=11n)");
        model.ImportCommand.Execute(null);

        Assert.Contains("ignored", model.Status);
        Assert.Contains("CJO", model.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEmptyBoxSaysWhatACardLooksLike()
    {
        var model = New();

        model.ImportCommand.Execute(null);

        Assert.Contains(".model", model.Status);
        Assert.True(model.IsEmpty);
    }

    [Fact]
    public void TheExampleButtonFillsInSomethingThatImports()
    {
        var model = New();

        model.UseSampleCommand.Execute(null);

        Assert.Equal(SpiceImportViewModel.Sample, model.Text);

        _registered.Add("1N4148");
        model.ImportCommand.Execute(null);

        Assert.Single(model.Imported);
    }

    [Fact]
    public void RemovingTakesItOutOfBothTheListAndTheLibrary()
    {
        var model = New();

        model.Text = Card("TESTMOS", "NMOS(Vto=2.1 Kp=0.06 Lambda=0.02)");
        model.ImportCommand.Execute(null);

        Assert.Contains(MosfetModel.Library, m => m.Name == "TESTMOS");

        model.RemoveCommand.Execute(null);

        Assert.True(model.IsEmpty);
        Assert.DoesNotContain(MosfetModel.Library, m => m.Name == "TESTMOS");
        Assert.Contains("Removed", model.Status);
    }

    [Fact]
    public void RemovingWithNothingChosenSaysSo()
    {
        var model = New();

        model.RemoveCommand.Execute(null);

        Assert.Contains("Choose", model.Status);
    }

    /// <summary>
    /// The end of the chain: an imported model chosen on a part, saved with the circuit, and found
    /// again by name when it is opened.
    /// </summary>
    [Fact]
    public void ACircuitCanUseAnImportedModelAndReloadWithIt()
    {
        var model = New();
        model.Text = Card("TESTROUNDTRIP", "D(Is=4n N=1.9 Rs=0.3 Bv=40)");
        model.ImportCommand.Execute(null);

        var imported = DiodeModel.Library.Single(m => m.Name == "TESTROUNDTRIP");

        var circuit = new Cirq.Core.Topology.Circuit();
        var diode = circuit.Add(new Diode(imported));
        var ground = circuit.Add(new Cirq.Components.Sources.Ground());
        circuit.Connect(diode.Cathode, ground.Pin);

        var json = Cirq.Components.Serialization.CircuitSerializer.ToJson(circuit);
        var result = Cirq.Components.Serialization.CircuitSerializer.FromJson(json);

        Assert.Empty(result.Warnings);

        var reloaded = result.Circuit.Components.OfType<Diode>().Single();

        Assert.Equal("TESTROUNDTRIP", reloaded.Model.Name);
        Assert.Equal(4e-9, reloaded.Model.SaturationCurrent, 15);
    }

    // ---- keeping what a circuit brought with it ----------------------------

    /// <summary>
    /// A circuit carries the imported models it uses, so it opens complete anywhere — but opening
    /// somebody's file does not add parts to your library, because a file is not an installer.
    /// This is the deliberate act that does.
    /// </summary>
    [Fact]
    public void KeepingTheOpenCircuitsModelsPutsThemInTheLibrary()
    {
        // Imported here, saved, and then forgotten — which is the state a colleague's machine is
        // in when the file lands on it.
        var store = new UserModelStore(_path);
        store.Import(Card("TESTKEPT", "D(Is=6.8n N=1.44)"));

        var circuit = new Cirq.Core.Topology.Circuit();
        circuit.Add(new Diode(DiodeModel.Library.Single(m => m.Name == "TESTKEPT")));

        var json = Cirq.Components.Serialization.CircuitSerializer.ToJson(circuit);

        store.Remove("TESTKEPT");
        Assert.DoesNotContain(DiodeModel.Library, m => m.Name == "TESTKEPT");

        var opened = Cirq.Components.Serialization.CircuitSerializer.FromJson(json).Circuit;

        // Registered for the session by the file, but not in the store on disk.
        Assert.Contains(DiodeModel.Library, m => m.Name == "TESTKEPT");
        Assert.DoesNotContain(store.Models, m => m.Name == "TESTKEPT");

        var vm = new SpiceImportViewModel(store, opened);
        vm.KeepFromCircuitCommand.Execute(null);

        Assert.Contains(store.Models, m => m.Name == "TESTKEPT");
        Assert.Contains(vm.Imported, m => m.Name == "TESTKEPT");
        Assert.Contains("TESTKEPT", vm.Status);

        // And it survives a restart, which is the whole of what "kept" means.
        Assert.Contains(new UserModelStore(_path).Models, m => m.Name == "TESTKEPT");
    }

    /// <summary>Keeping a model that is already there changes nothing and says so.</summary>
    [Fact]
    public void KeepingAModelAlreadyInTheLibraryLeavesItAlone()
    {
        var store = new UserModelStore(_path);
        store.Import(Card("TESTALREADY", "D(Is=1.1n N=1.2)"));

        var circuit = new Cirq.Core.Topology.Circuit();
        circuit.Add(new Diode(DiodeModel.Library.Single(m => m.Name == "TESTALREADY")));

        var vm = new SpiceImportViewModel(store, circuit);
        vm.KeepFromCircuitCommand.Execute(null);

        Assert.Single(store.Models, m => m.Name == "TESTALREADY");
        Assert.Contains("not already here", vm.Status);
    }

    /// <summary>A circuit of built-in parts has nothing to keep, and the button says as much.</summary>
    [Fact]
    public void ACircuitOfBuiltInPartsHasNothingToKeep()
    {
        // A built-in nothing in this suite ever shadows. The model registries are static, so a
        // part whose name another test imports over is not a built-in for the length of that test.
        var circuit = new Cirq.Core.Topology.Circuit();
        circuit.Add(new Diode(DiodeModel.D1N5817));

        var vm = new SpiceImportViewModel(new UserModelStore(_path), circuit);
        vm.KeepFromCircuitCommand.Execute(null);

        Assert.Contains("not already here", vm.Status);
    }
}
