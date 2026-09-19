using Cirq.Core.Primitives;

namespace Cirq.Components.Digital;

/// <summary>
/// Canvas geometry for a dual-inline package: pin 1 at the top left, counting down the left side
/// and back up the right, which is how the parts are actually numbered.
/// </summary>
public static class DipPackage
{
    public const double Pitch = 20.0;
    public const double HalfWidth = 45.0;

    public static Point PinOffset(int pin, int pinCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pin, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pin, pinCount);

        var half = pinCount / 2;
        var span = (half - 1) * Pitch;

        if (pin <= half) return new Point(-HalfWidth, -span / 2 + (pin - 1) * Pitch);

        var fromBottom = pin - half - 1;
        return new Point(HalfWidth, span / 2 - fromBottom * Pitch);
    }

    /// <summary>Body height needed to fit the pins, used by the symbol renderer.</summary>
    public static double BodyHeight(int pinCount) => (pinCount / 2 - 1) * Pitch + 30;
}
