using Cirq.Core.Primitives;

namespace Cirq.Components.Boards;

/// <summary>
/// Where a board's pins sit on the canvas. Two columns rather than the physical header outline:
/// a schematic symbol is not a picture of the board, and a Mega's 54 digital pins down one edge
/// would be unusable to wire.
/// </summary>
public static class BoardPackage
{
    /// <summary>Spacing between adjacent pins, matching the DIP packages.</summary>
    public const double Pitch = 20.0;

    /// <summary>Distance from the centre line to a pin's outer end.</summary>
    public const double HalfWidth = 95.0;

    /// <summary>Clearance above the first pin and below the last, leaving room for the caption.</summary>
    public const double Margin = 26.0;

    public static double BodyHeight(BoardProfile profile) =>
        (Math.Max(profile.Rows, 1) - 1) * Pitch + (Margin * 2);

    /// <summary>Pin position relative to the symbol's centre.</summary>
    public static Point PinOffset(BoardProfile profile, int index, bool left)
    {
        var top = -BodyHeight(profile) / 2 + Margin;
        var y = top + (index * Pitch);
        return new Point(left ? -HalfWidth : HalfWidth, y);
    }
}
