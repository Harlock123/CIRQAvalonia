using Cirq.Components.Serialization;
using Cirq.Core.Topology;
using Cirq.Mcp;
using Cirq.UI.Rendering;
using Cirq.UI.Services;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Agents;

/// <summary>
/// The editor's palette, examples and schematic drawing as the MCP server sees them: the same
/// <see cref="ComponentCatalog"/> a person places parts from, the same <see cref="Examples"/>, and the same
/// <see cref="CircuitExporter"/> that writes a PNG, in its print colours.
/// </summary>
public sealed class EditorLibrary : ICircuitLibrary
{
    public IReadOnlyList<PartKind> Parts { get; } =
        [.. ComponentCatalog.Categories.SelectMany(c => c.Items.Select(i => new PartKind(c.Name, i.Name, i.Description, i.Create)))];

    public IReadOnlyList<ExampleKind> Examples { get; } =
        [.. ViewModels.Examples.All.Select(e => new ExampleKind(e.Category, e.Name, e.Description))];

    /// <summary>
    /// Builds an example in an editor of its own, as the Examples menu does, and hands back a copy of what it made:
    /// the examples are written against an editor (they set its scope as well as drawing the circuit).
    /// </summary>
    public LoadedExample LoadExample(string name)
    {
        var example = ViewModels.Examples.All.First(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        using var editor = new MainWindowViewModel();
        editor.Simulation.Pause();
        editor.Circuit.Clear();
        example.Build(editor);
        var copy = CircuitSerializer.FromJson(CircuitSerializer.ToJson(editor.Circuit)).Circuit;
        return new LoadedExample(copy, editor.Scope.TimebasePerDivision, editor.Scope.VoltsPerDivision);
    }

    /// <summary>The schematic as a PNG, drawn black on white as it prints, whatever theme the editor is in.</summary>
    public byte[] Picture(Circuit circuit, string? sheet, double scale)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cirq-agent-{Guid.NewGuid():N}.png");
        try
        {
            using (CanvasTheme.ForPrinting())
                CircuitExporter.Export(circuit, null, path, new ExportOptions(ExportFormat.Png, RasterScale: scale, Sheet: sheet));
            return File.ReadAllBytes(path);
        }
        finally
        {
            try { File.Delete(path); }
            catch (IOException) { }
        }
    }
}
