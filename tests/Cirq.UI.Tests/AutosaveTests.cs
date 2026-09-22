using Cirq.Components.Passive;
using Cirq.Core.Topology;
using Cirq.UI.Services;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// Keeping a copy of the circuit while it is being worked on, so a crash costs the last few
/// minutes rather than the afternoon.
/// <para>
/// It matters more than it used to: a saved circuit now carries the SPICE cards for the imported
/// models it uses, so the file is the only copy of more than it once was.
/// </para>
/// </summary>
public class AutosaveTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"cirq-recovery-{Guid.NewGuid():N}.cirq");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        if (File.Exists(_path + ".tmp")) File.Delete(_path + ".tmp");
    }

    private MainWindowViewModel Fresh(FakeFileDialogs? dialogs = null)
    {
        var vm = new MainWindowViewModel
        {
            Autosave = new AutosaveStore(_path),
            FileDialogs = dialogs ?? new FakeFileDialogs(),
        };

        return vm;
    }

    // ---- the store ---------------------------------------------------------

    [Fact]
    public void NothingIsPendingBeforeAnythingIsWritten()
    {
        Assert.Null(new AutosaveStore(_path).Pending());
    }

    [Fact]
    public void WhatIsWrittenComesBack()
    {
        var store = new AutosaveStore(_path);

        var circuit = new Circuit { Title = "Bench supply" };
        circuit.Add(new Resistor(4.7e3) { Name = "R1" });

        Assert.True(store.Write(circuit, "/tmp/bench.cirq"));

        var pending = store.Pending();

        Assert.NotNull(pending);
        Assert.Equal("/tmp/bench.cirq", pending.OriginalPath);
        Assert.Equal("bench.cirq", pending.Name);
        Assert.Contains("4700", pending.Json);
    }

    /// <summary>A circuit that was never saved has no path, and the prompt says so in words.</summary>
    [Fact]
    public void AnUnsavedCircuitIsNamedAsOne()
    {
        var store = new AutosaveStore(_path);

        store.Write(new Circuit(), null);

        Assert.Equal("an unsaved circuit", store.Pending()!.Name);
    }

    [Fact]
    public void DiscardingThrowsItAway()
    {
        var store = new AutosaveStore(_path);

        store.Write(new Circuit(), null);
        store.Discard();

        Assert.Null(store.Pending());
    }

    /// <summary>
    /// A damaged snapshot counts as nothing rather than as an error. It is the one file where
    /// failing loudly helps least — the application is starting, and the alternative to a quiet
    /// "nothing to recover" is a crash on launch.
    /// </summary>
    [Fact]
    public void ADamagedSnapshotIsNothingRatherThanACrash()
    {
        File.WriteAllText(_path, "{ this is not json");

        Assert.Null(new AutosaveStore(_path).Pending());
    }

    /// <summary>
    /// Written through a temporary file and moved into place, so an interrupted autosave cannot
    /// destroy the previous good one — which is the moment it is most needed.
    /// </summary>
    [Fact]
    public void AnInterruptedWriteCannotDestroyTheLastGoodOne()
    {
        var store = new AutosaveStore(_path);

        var first = new Circuit { Title = "First" };
        first.Add(new Resistor(1e3));

        store.Write(first, null);

        var before = File.ReadAllText(_path);

        // A stray temporary file from a write that never finished.
        File.WriteAllText(_path + ".tmp", "half written");

        Assert.Equal(before, File.ReadAllText(_path));
        Assert.NotNull(store.Pending());
    }

    // ---- the view model ----------------------------------------------------

    /// <summary>
    /// Nothing is written when there is nothing to lose. The file on disk is already a better
    /// copy, and a snapshot taken anyway would mean a recovery prompt on the next start offering
    /// to restore something that was never lost.
    /// </summary>
    [Fact]
    public void AnUnmodifiedCircuitIsNotSnapshotted()
    {
        using var vm = Fresh();

        Assert.False(vm.IsModified);
        Assert.False(vm.AutosaveNow());
        Assert.Null(vm.Autosave.Pending());
    }

    [Fact]
    public void AModifiedCircuitIsSnapshotted()
    {
        using var vm = Fresh();

        vm.Circuit.Add(new Resistor(2.2e3) { Name = "RNEW" });

        Assert.True(vm.IsModified);
        Assert.True(vm.AutosaveNow());
        Assert.NotNull(vm.Autosave.Pending());
    }

    /// <summary>Saving for real leaves nothing to recover.</summary>
    [Fact]
    public async Task SavingDiscardsTheSnapshot()
    {
        using var vm = Fresh();

        vm.Circuit.Add(new Resistor(2.2e3));
        vm.AutosaveNow();

        Assert.NotNull(vm.Autosave.Pending());

        var saved = Path.Combine(Path.GetTempPath(), $"cirq-{Guid.NewGuid():N}.cirq");

        try
        {
            Assert.True(await vm.WriteToAsync(saved));
            Assert.Null(vm.Autosave.Pending());
        }
        finally
        {
            if (File.Exists(saved)) File.Delete(saved);
        }
    }

    // ---- recovery ----------------------------------------------------------

    [Fact]
    public async Task WithNothingPendingNothingIsOffered()
    {
        var dialogs = new FakeFileDialogs();
        using var vm = Fresh(dialogs);

        Assert.False(await vm.OfferRecoveryAsync());
        Assert.Equal(0, dialogs.RecoveryPrompts);
    }

    /// <summary>
    /// The one that matters: what was left behind comes back, with the part that was added to it.
    /// </summary>
    [Fact]
    public async Task AcceptingRecoveryBringsTheCircuitBack()
    {
        // A session that ended badly.
        using (var lost = Fresh())
        {
            lost.Circuit.Add(new Resistor(8.2e3) { Name = "RLOST" });
            Assert.True(lost.AutosaveNow());
        }

        var dialogs = new FakeFileDialogs { AllowRecovery = true };
        using var vm = Fresh(dialogs);

        Assert.True(await vm.OfferRecoveryAsync());
        Assert.Equal(1, dialogs.RecoveryPrompts);

        Assert.Contains(vm.Circuit.Components, c => c.Name == "RLOST");

        // Still marked as modified, because what came back is by definition not what is in any
        // file — and the snapshot is gone, so the question is not asked twice.
        Assert.True(vm.IsModified);
        Assert.Null(vm.Autosave.Pending());
    }

    /// <summary>
    /// Declining throws it away too. A snapshot that outlived its question would be offered again
    /// on the next start, and a prompt that keeps appearing is one people learn to dismiss without
    /// reading.
    /// </summary>
    [Fact]
    public async Task DecliningRecoveryAlsoThrowsItAway()
    {
        using (var lost = Fresh())
        {
            lost.Circuit.Add(new Resistor(8.2e3) { Name = "RLOST" });
            lost.AutosaveNow();
        }

        var dialogs = new FakeFileDialogs { AllowRecovery = false };
        using var vm = Fresh(dialogs);

        Assert.False(await vm.OfferRecoveryAsync());
        Assert.Equal(1, dialogs.RecoveryPrompts);

        Assert.DoesNotContain(vm.Circuit.Components, c => c.Name == "RLOST");
        Assert.Null(vm.Autosave.Pending());
    }

    /// <summary>
    /// A recovered circuit remembers where it belonged, so saving puts it back rather than asking
    /// where to put it.
    /// </summary>
    [Fact]
    public async Task ARecoveredCircuitRemembersItsPath()
    {
        var store = new AutosaveStore(_path);

        var circuit = new Circuit { Title = "Bench" };
        circuit.Add(new Resistor(1e3));

        store.Write(circuit, "/tmp/somewhere/bench.cirq");

        using var vm = Fresh(new FakeFileDialogs { AllowRecovery = true });

        Assert.True(await vm.OfferRecoveryAsync());
        Assert.Equal("/tmp/somewhere/bench.cirq", vm.CurrentFilePath);
    }
}
