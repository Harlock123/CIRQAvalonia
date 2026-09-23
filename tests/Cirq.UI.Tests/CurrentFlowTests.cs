using Avalonia;
using Cirq.UI.Rendering;

namespace Cirq.UI.Tests;

/// <summary>
/// The dots that show current moving along a wire.
/// <para>
/// Arithmetic rather than drawing, so it can be checked without a window: which way they go, how
/// fast, and that a dot never leaves the wire it belongs to.
/// </para>
/// </summary>
public class CurrentFlowTests
{
    private static readonly List<Point> Straight = [new(0, 0), new(200, 0)];

    private static readonly List<Point> Elbow = [new(0, 0), new(100, 0), new(100, 100)];

    [Fact]
    public void NothingMovesBelowTheFloor()
    {
        Assert.Equal(0.0, CurrentFlow.Speed(0));
        Assert.Equal(0.0, CurrentFlow.Speed(CurrentFlow.Floor / 10));
        Assert.Empty(CurrentFlow.Dots(Straight, 1e-15, 1.0));
    }

    /// <summary>
    /// The speed is logarithmic, because current in a circuit spans decades. A microamp of base
    /// current has to be visibly moving next to an amp of collector current, and on a linear
    /// scale it would be indistinguishable from stopped.
    /// </summary>
    [Fact]
    public void SpeedIsLogarithmicSoSmallCurrentsStillMove()
    {
        var microamp = CurrentFlow.Speed(1e-6);
        var milliamp = CurrentFlow.Speed(1e-3);
        var amp = CurrentFlow.Speed(1.0);

        Assert.True(microamp > 0.2, $"a microamp should visibly move, got {microamp:0.###}");
        Assert.True(milliamp > microamp);
        Assert.True(amp > milliamp);

        // Each thousandfold step covers the same ground, which is what logarithmic means.
        Assert.Equal(milliamp - microamp, amp - milliamp, 1e-9);
    }

    [Fact]
    public void SpeedSaturatesAtTheCeiling()
    {
        Assert.Equal(1.0, CurrentFlow.Speed(CurrentFlow.Ceiling), 1e-9);
        Assert.Equal(1.0, CurrentFlow.Speed(CurrentFlow.Ceiling * 1000), 1e-9);
    }

    /// <summary>The sign of the current is which way the dots go, and nothing else.</summary>
    [Fact]
    public void TheSignIsTheDirection()
    {
        Assert.Equal(-CurrentFlow.Speed(0.5), CurrentFlow.Speed(-0.5), 1e-12);

        var forward = CurrentFlow.Dots(Straight, 0.5, 0.05);
        var backward = CurrentFlow.Dots(Straight, -0.5, 0.05);

        // Same dots, offset the opposite way round the spacing.
        Assert.Equal(forward.Count, backward.Count);
        Assert.NotEqual(forward[0].X, backward[0].X, 1e-6);
    }

    /// <summary>More current is more ground covered in the same time.</summary>
    [Fact]
    public void MoreCurrentMovesFurther()
    {
        // Measured as the leading dot's position, before it wraps.
        var slow = CurrentFlow.Dots(Straight, 1e-4, 0.01)[0].X;
        var fast = CurrentFlow.Dots(Straight, 1.0, 0.01)[0].X;

        Assert.True(fast > slow, $"{fast:0.###} should be further than {slow:0.###}");
    }

    /// <summary>
    /// A dot never leaves its wire, whatever the clock says. The offset is taken modulo the
    /// spacing, so a circuit left running for an hour draws the same dots as one just started.
    /// </summary>
    [Fact]
    public void DotsStayOnTheWireForever()
    {
        foreach (var seconds in (double[])[0, 0.37, 12.5, 3600, 86_400])
        {
            var dots = CurrentFlow.Dots(Elbow, 0.25, seconds);

            Assert.NotEmpty(dots);

            foreach (var dot in dots)
            {
                Assert.InRange(dot.X, -1e-9, 100 + 1e-9);
                Assert.InRange(dot.Y, -1e-9, 100 + 1e-9);

                // On the elbow itself: either along the top run or down the side.
                var onTop = Math.Abs(dot.Y) < 1e-6;
                var onSide = Math.Abs(dot.X - 100) < 1e-6;

                Assert.True(onTop || onSide, $"({dot.X:0.##}, {dot.Y:0.##}) is off the wire");
            }
        }
    }

    /// <summary>They are evenly spaced, which is what makes the motion readable as a speed.</summary>
    [Fact]
    public void TheyAreEvenlySpaced()
    {
        var dots = CurrentFlow.Dots(Straight, 0.1, 0.2);

        Assert.True(dots.Count >= 3);

        for (var i = 1; i < dots.Count; i++)
            Assert.Equal(CurrentFlow.Spacing, dots[i].X - dots[i - 1].X, 1e-6);
    }

    /// <summary>A stub too short to hold a dot gets none, rather than one jammed at its end.</summary>
    [Fact]
    public void AVeryShortWireGetsNone()
    {
        List<Point> stub = [new(0, 0), new(4, 0)];

        Assert.Empty(CurrentFlow.Dots(stub, 1.0, 0.5));
    }

    [Fact]
    public void ADegeneratePathIsHandled()
    {
        Assert.Empty(CurrentFlow.Dots([], 1.0, 0));
        Assert.Empty(CurrentFlow.Dots([new Point(5, 5)], 1.0, 0));
    }
}
