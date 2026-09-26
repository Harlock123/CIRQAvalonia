using Cirq.Core.Primitives;

namespace Cirq.Core.Topology;

/// <summary>
/// A single connection point on a component. Terminals have identity semantics: two terminals are
/// the same terminal only when they are the same instance, which keeps them usable as dictionary
/// keys even when two components expose pins with identical names.
/// </summary>
public record Terminal(string Id, string Name, TerminalType Type, Point CanvasOffset)
{
    /// <summary>
    /// What the pin is called on the symbol — <c>IN+</c>, <c>OUT</c>, <c>GND</c>.
    /// <para>
    /// Settable for the same reason the offset is, and only by the same thing: naming the pins is
    /// most of what turns a block into a part somebody else can read. It is a label, not an
    /// identity — that is <see cref="Uid"/> — so renaming a pin leaves every wire, probe and net
    /// exactly where it was.
    /// </para>
    /// </summary>
    public string Name { get; set; } = Name;

    /// <summary>
    /// Where the pin sits on the symbol, relative to the part's position.
    /// <para>
    /// Settable, and only one thing sets it: the symbol editor, moving a pin of a block somebody is
    /// drawing. Identity is <see cref="Uid"/> rather than position, so a terminal that has moved is
    /// still the same terminal — the wires on it, the probes watching it and the nets it belongs to
    /// are all unaffected, which is what makes moving a pin a drawing operation rather than a
    /// rewiring one.
    /// </para>
    /// </summary>
    public Point CanvasOffset { get; set; } = CanvasOffset;

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
