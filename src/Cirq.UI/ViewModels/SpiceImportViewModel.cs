using System.Collections.ObjectModel;
using Cirq.Components.Spice;
using Cirq.Core.Topology;
using Cirq.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>
/// The SPICE import window: paste a model card, get a part.
/// <para>
/// Manufacturers publish SPICE models for almost everything, and the parameters in them are the
/// same ones this library's models use — both come from the same formulation. So a card off a
/// datasheet becomes a device here with nothing in between, and the parts list stops being
/// "whatever was built in".
/// </para>
/// </summary>
public sealed partial class SpiceImportViewModel : ObservableObject
{
    private readonly UserModelStore _store;
    private readonly Circuit? _circuit;
    private readonly BlockLibrary? _blocks;

    public SpiceImportViewModel(
        UserModelStore store, Circuit? circuit = null, BlockLibrary? blocks = null)
    {
        _store = store;
        _circuit = circuit;
        _blocks = blocks;

        Refresh();
    }

    /// <summary>The card or cards to read.</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    /// <summary>What has been imported, newest first.</summary>
    public ObservableCollection<UserModel> Imported { get; } = [];

    [ObservableProperty]
    public partial UserModel? Selected { get; set; }

    public bool IsEmpty => Imported.Count == 0;

    /// <summary>Where the imported models live, so they can be found or backed up.</summary>
    public string Location => _store.Location;

    /// <summary>An example of the shape, offered as a starting point.</summary>
    public const string Sample =
        ".model 1N4148 D(Is=2.52n Rs=0.568 N=1.752 Bv=75 Ibv=5u)";

    /// <summary>The other shape, offered the same way.</summary>
    public const string SubcircuitSample = """
        .subckt DIVIDER in out gnd
        R1 in out 10k
        R2 out gnd 10k
        .ends
        """;

    [RelayCommand]
    public void UseSubcircuitSample() => Text = SubcircuitSample;

    [RelayCommand]
    public void Import()
    {
        if (string.IsNullOrWhiteSpace(Text))
        {
            Status = $"Paste a .model card or a .subckt definition. For example:  {Sample}";
            return;
        }

        // Which of the two kinds this is, decided by what is in the text rather than by a setting.
        // Somebody pasting from a datasheet should not have to say which sort of thing they have.
        var hasSubcircuit = Text.Contains(".subckt", StringComparison.OrdinalIgnoreCase);
        var hasModel = Text.Contains(".model", StringComparison.OrdinalIgnoreCase);

        List<string> lines = [];
        var clean = true;

        if (hasModel)
        {
            var (imported, problems) = _store.Import(Text);

            foreach (var model in imported) lines.Add($"{model.Name} — {model.Summary}");
            lines.AddRange(problems);

            if (imported.Count == 0 || problems.Count > 0) clean = false;
        }

        if (hasSubcircuit)
        {
            var (built, problems) = ImportSubcircuits();

            lines.AddRange(built);
            lines.AddRange(problems);

            if (built.Count == 0 || problems.Count > 0) clean = false;
        }

        if (!hasModel && !hasSubcircuit)
        {
            Status = "That is neither a .model card nor a .subckt definition. " +
                     $"A card looks like:  {Sample}";
            return;
        }

        Refresh();

        Status = lines.Count == 0 ? "Nothing to import." : string.Join("\n", lines);

        // Only clear the box on a clean import: text that produced a complaint is text somebody
        // is about to correct.
        if (clean) Text = string.Empty;
    }

    /// <summary>
    /// Reads every subcircuit in the text and puts it in the block library.
    /// <para>
    /// A subcircuit <i>is</i> a block — a pin list and a little netlist, which is exactly what
    /// grouping a selection produces — so it goes where blocks go rather than into a library of
    /// its own. Everything a block already does then applies to it: place it as many times as you
    /// like, open it to see inside, and it travels inside a saved circuit.
    /// </para>
    /// </summary>
    private (List<string> Built, List<string> Problems) ImportSubcircuits()
    {
        var parsed = SpiceSubcircuitReader.Parse(Text);

        List<string> built = [];
        List<string> problems = [.. parsed.Problems];

        if (_blocks is null && parsed.Subcircuits.Count > 0)
        {
            problems.Add("There is no block library to put a subcircuit in.");
            return (built, problems);
        }

        foreach (var definition in parsed.Subcircuits)
        {
            var result = SpiceSubcircuitImport.Build(definition);

            if (!result.Succeeded)
            {
                problems.AddRange(result.Problems);
                continue;
            }

            _blocks!.Save(result.Name, result.Block!);

            built.Add($"{result.Name} — {result.Summary}, saved to the block library");
        }

        return (built, problems);
    }

    [RelayCommand]
    public void Remove()
    {
        if (Selected is null)
        {
            Status = "Choose an imported model to remove.";
            return;
        }

        var name = Selected.Name;

        _store.Remove(name);
        Refresh();

        Status = $"Removed '{name}'. Circuits already using it will load with the default model " +
                 "and say so.";
    }

    /// <summary>
    /// Takes the models the open circuit brought with it into the library, so they are here for
    /// the next circuit too.
    /// </summary>
    [RelayCommand]
    public void KeepFromCircuit()
    {
        if (_circuit is null)
        {
            Status = "No circuit is open.";
            return;
        }

        var kept = _store.KeepUsedBy(_circuit);

        Refresh();

        Status = kept.Count == 0
            ? "The open circuit uses no imported models that are not already here."
            : $"Kept {string.Join(", ", kept)}. They are in the library now, for any circuit.";
    }

    [RelayCommand]
    public void UseSample() => Text = Sample;

    private void Refresh()
    {
        Imported.Clear();

        foreach (var model in _store.Models) Imported.Add(model);

        Selected = Imported.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }
}
