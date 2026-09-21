namespace Cirq.Core.Topology;

/// <summary>A set of terminals that are electrically the same point.</summary>
public sealed class Net
{
    public Net(Guid id, int index, IReadOnlyList<Terminal> terminals, bool isGround, string? label = null)
    {
        Id = id;
        Index = index;
        Terminals = terminals;
        IsGround = isGround;
        Label = label;
    }

    public Guid Id { get; }

    /// <summary>MNA node index: <c>-1</c> for ground, otherwise a dense index from 0.</summary>
    public int Index { get; }

    public IReadOnlyList<Terminal> Terminals { get; }

    public bool IsGround { get; }

    /// <summary>
    /// What a net label on this net calls it, or null when nothing names it. A name given by a
    /// person beats a generated one everywhere it is shown — on a probe, in an error message, in
    /// an exported netlist.
    /// </summary>
    public string? Label { get; }

    public string Name => Label ?? (IsGround ? "GND" : $"N{Index}");

    public override string ToString() => $"{Name} [{string.Join(", ", Terminals)}]";
}
