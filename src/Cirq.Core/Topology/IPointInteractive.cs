using Cirq.Core.Primitives;

namespace Cirq.Core.Topology;

/// <summary>
/// An operable part with more than one thing on it to operate — a keypad's sixteen keys — so where
/// on the symbol it was double-clicked decides what happens.
/// <para>
/// The point is in the part's own drawing coordinates, before rotation, with the origin at its
/// placement point: the same space its terminals' offsets are given in. The canvas undoes the
/// rotation before asking, so a part never has to know how it was turned.
/// </para>
/// </summary>
public interface IPointInteractive : IInteractiveComponent
{
    /// <summary>Operates whatever is under the point. False when nothing is there.</summary>
    bool InteractAt(Point local);

    /// <summary>What operating the point would do, for the status bar, or null when nothing is there.</summary>
    string? InteractionHintAt(Point local);
}
