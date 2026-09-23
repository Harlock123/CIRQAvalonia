using System.Collections.ObjectModel;
using Cirq.Components.Serialization;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.UI.ViewModels;

/// <summary>
/// What changed between the circuit on screen and a saved one.
/// </summary>
public sealed partial class CompareViewModel : ObservableObject
{
    public CompareViewModel(string against, IReadOnlyList<CircuitChange> changes)
    {
        Against = against;

        foreach (var change in changes) Changes.Add(change);

        Summary = CircuitDiff.Summarise(changes);
    }

    /// <summary>What it was compared with, for the heading.</summary>
    public string Against { get; }

    public ObservableCollection<CircuitChange> Changes { get; } = [];

    public string Summary { get; }

    public bool HasChanges => Changes.Count > 0;

    /// <summary>Builds the comparison, or says why it could not be made.</summary>
    public static CompareViewModel? Build(Circuit current, string path, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(current);

        problem = null;

        try
        {
            var loaded = CircuitSerializer.FromJson(File.ReadAllText(path));

            // Warnings are worth carrying: a file this version could not read completely will
            // look as though parts were deleted, and that is a fact about the load rather than
            // about the design.
            var changes = CircuitDiff.Compare(loaded.Circuit, current);

            var model = new CompareViewModel(Path.GetFileName(path), changes);

            if (!loaded.IsClean)
            {
                problem = "That file did not load cleanly, so some of these differences may be " +
                          "the load rather than the design: " + string.Join("; ", loaded.Warnings);
            }

            return model;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Text.Json.JsonException)
        {
            problem = $"Could not read {Path.GetFileName(path)}: {ex.Message}";
            return null;
        }
    }
}
