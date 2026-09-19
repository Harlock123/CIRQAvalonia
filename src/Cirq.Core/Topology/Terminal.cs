using Cirq.Core.Primitives;

namespace Cirq.Core.Topology;

/// <summary>
/// A single connection point on a component. Terminals have identity semantics: two terminals are
/// the same terminal only when they are the same instance, which keeps them usable as dictionary
/// keys even when two components expose pins with identical names.
/// </summary>
public record Terminal(string Id, string Name, TerminalType Type, Point CanvasOffset)
{
    /// <summary>Stable per-instance identity, used for equality and for serialization round-trips.</summary>
    public Guid Uid { get; init; } = Guid.NewGuid();

    /// <summary>The component this terminal belongs to. Assigned when the component is constructed.</summary>
    public CircuitComponent? Owner { get; internal set; }

    /// <summary>Canvas offset after the owner's rotation has been applied.</summary>
    public Point RotatedOffset => Owner is null ? CanvasOffset : CanvasOffset.Rotate(Owner.RotationDegrees);

    /// <summary>Absolute canvas position of this terminal.</summary>
    public Point AbsolutePosition
    {
        get
        {
            if (Owner is null) return CanvasOffset;
            var r = RotatedOffset;
            return new Point(Owner.X + r.X, Owner.Y + r.Y);
        }
    }

    public virtual bool Equals(Terminal? other) => other is not null && Uid.Equals(other.Uid);

    public override int GetHashCode() => Uid.GetHashCode();

    public override string ToString() => Owner is null ? $"{Name}" : $"{Owner.Name}.{Name}";
}
