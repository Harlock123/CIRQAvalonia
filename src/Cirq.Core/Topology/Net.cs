namespace Cirq.Core.Topology;

/// <summary>A set of terminals that are electrically the same point.</summary>
public sealed class Net
{
    public Net(Guid id, int index, IReadOnlyList<Terminal> terminals, bool isGround)
    {
        Id = id;
        Index = index;
        Terminals = terminals;
        IsGround = isGround;
    }

    public Guid Id { get; }

    /// <summary>MNA node index: <c>-1</c> for ground, otherwise a dense index from 0.</summary>
    public int Index { get; }

    public IReadOnlyList<Terminal> Terminals { get; }

    public bool IsGround { get; }

    public string Name => IsGround ? "GND" : $"N{Index}";

    public override string ToString() => $"{Name} [{string.Join(", ", Terminals)}]";
}
