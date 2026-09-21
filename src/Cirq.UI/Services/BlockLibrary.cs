using System.Text.Json;
using System.Text.RegularExpressions;
using Cirq.Components.Hierarchy;
using Cirq.Components.Serialization;
using Cirq.Core.Topology;

namespace Cirq.UI.Services;

/// <summary>One saved block, as it sits in the library.</summary>
/// <param name="Name">What it is called. Unique within the library.</param>
/// <param name="Parts">How many components are inside it, at every depth.</param>
/// <param name="Pins">How many pins it has.</param>
/// <param name="SavedUtc">When it was put there.</param>
/// <param name="Json">The block and its contents, in the ordinary circuit format.</param>
public sealed record SavedBlock(
    string Name, int Parts, int Pins, DateTimeOffset SavedUtc, string Json)
{
    public string Summary =>
        $"{Parts} part{(Parts == 1 ? string.Empty : "s")}, " +
        $"{Pins} pin{(Pins == 1 ? string.Empty : "s")}";
}

/// <summary>
/// A library of blocks that can be placed more than once.
/// <para>
/// Grouping a selection makes one block. That is useful on its own, but the reason hierarchy
/// exists is <i>reuse</i>: build a filter stage once and put four of them in. This is where a
/// block goes so it can be placed again — in this circuit, or in the next one.
/// </para>
/// <para>
/// What comes out is a <b>copy</b>, not a reference. Placing a block twice gives two independent
/// sets of parts, and editing one afterwards does not touch the other. That is a real limitation
/// and worth being plain about rather than implying a link that is not there — but it is also what
/// keeps the behaviour predictable, since a live link would mean every instance's state and
/// identity had to be reconciled with a definition that could change underneath it.
/// </para>
/// </summary>
public sealed partial class BlockLibrary
{
    private readonly string _path;
    private readonly List<SavedBlock> _blocks = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public BlockLibrary(string? path = null)
    {
        _path = path ?? DefaultPath();
        Reload();
    }

    public static string DefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.Create),
            "CirqAvalonia",
            "blocks.json");

    /// <summary>Where the library lives, so it can be found, backed up or deleted.</summary>
    public string Location => _path;

    /// <summary>What is in it, newest first.</summary>
    public IReadOnlyList<SavedBlock> Blocks => _blocks;

    /// <summary>
    /// Puts a block in the library under a name, replacing anything already saved under it.
    /// </summary>
    public SavedBlock Save(string name, Subcircuit block)
    {
        ArgumentNullException.ThrowIfNull(block);

        var trimmed = (name ?? string.Empty).Trim();

        if (trimmed.Length == 0)
            throw new ArgumentException("A saved block needs a name.", nameof(name));

        // Serialised as an ordinary one-component circuit, so the library uses the same format as
        // a saved file and gains every part the file format already understands.
        var holder = new Circuit { Title = trimmed };
        holder.Components.Add(block);

        var saved = new SavedBlock(
            trimmed,
            block.Descendants().Count(),
            block.Ports.Count,
            DateTimeOffset.UtcNow,
            CircuitSerializer.ToJson(holder));

        _blocks.RemoveAll(b => string.Equals(b.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        _blocks.Insert(0, saved);

        Flush();
        return saved;
    }

    /// <summary>
    /// Makes a fresh instance of a saved block, ready to be added to a circuit. Null when the
    /// library has nothing under that name, or when what it has will not load.
    /// </summary>
    public Subcircuit? Create(string name, Circuit target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var saved = _blocks.FirstOrDefault(b =>
            string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

        if (saved is null) return null;

        Subcircuit? block;

        try
        {
            // Fresh identities before loading. Two instances of the same block in one circuit
            // would otherwise share component ids, and a wire looking one up by id would find
            // whichever came first.
            var result = CircuitSerializer.FromJson(Reidentify(saved.Json));

            block = result.Circuit.Components.OfType<Subcircuit>().FirstOrDefault();
        }
        catch (Exception ex) when (ex is CircuitFormatException or JsonException)
        {
            return null;
        }

        if (block is null) return null;

        block.BlockName = saved.Name;
        Rename(block, target);

        return block;
    }

    /// <summary>Takes a block out of the library.</summary>
    public bool Remove(string name)
    {
        var removed = _blocks.RemoveAll(b =>
            string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;

        if (removed) Flush();

        return removed;
    }

    /// <summary>True when something is already saved under that name.</summary>
    public bool Contains(string name) =>
        _blocks.Any(b => string.Equals(b.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Rewrites every identifier in the saved JSON to a fresh one, consistently — so the copy is
    /// self-consistent and shares nothing with any other instance.
    /// </summary>
    private static string Reidentify(string json)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return GuidPattern().Replace(json, match =>
        {
            if (!map.TryGetValue(match.Value, out var replacement))
            {
                replacement = Guid.NewGuid().ToString("D");
                map[match.Value] = replacement;
            }

            return replacement;
        });
    }

    /// <summary>
    /// Gives the block and everything in it designators that are free in the circuit it is going
    /// into. Without this a second instance arrives calling itself X1 alongside the first.
    /// </summary>
    private static void Rename(Subcircuit block, Circuit target)
    {
        // Flattened, so names already used inside another block count as taken. Looking only at
        // the sheet gives the second instance of a block the same inner designators as the first,
        // because the first one's parts are not on the sheet — they are inside it.
        var taken = new HashSet<string>(
            Flattening.Flatten(target.Components).Select(c => c.Name),
            StringComparer.OrdinalIgnoreCase);

        string Next(string prefix)
        {
            var n = 1;
            while (taken.Contains($"{prefix}{n}")) n++;

            var name = $"{prefix}{n}";
            taken.Add(name);

            return name;
        }

        block.Name = Next(block.DesignatorPrefix);

        foreach (var component in block.Descendants())
            component.Name = Next(component.DesignatorPrefix);
    }

    private void Reload()
    {
        _blocks.Clear();

        try
        {
            if (!File.Exists(_path)) return;

            var loaded = JsonSerializer.Deserialize<List<SavedBlock>>(
                File.ReadAllText(_path), Options);

            if (loaded is not null) _blocks.AddRange(loaded);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged library is an empty one rather than a crash at startup. The file is left
            // alone so it can be looked at.
        }
    }

    private void Flush()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(_path, JsonSerializer.Serialize(_blocks, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not being able to write the library is not a reason to lose the circuit.
        }
    }

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();
}
