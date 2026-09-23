using Cirq.Components.Passive;
using Cirq.Core.Topology;
using Cirq.UI.Controls;
using Cirq.UI.Services;

namespace Cirq.UI.Tests;

/// <summary>
/// Lining parts up and spreading them out.
/// <para>
/// Checked as arithmetic rather than by eye: where each part ends up, that the extremes of a
/// spread do not move, and that an arrangement which changes nothing reports no moves at all —
/// because one that reported them would leave an undo step for something that did not happen.
/// </para>
/// </summary>
public class ArrangeTests
{
    private static List<CircuitComponent> Row(params (double X, double Y)[] at)
    {
        List<CircuitComponent> parts = [];

        for (var i = 0; i < at.Length; i++)
            parts.Add(new Resistor(1e3) { Name = $"R{i + 1}", X = at[i].X, Y = at[i].Y });

        return parts;
    }

    private static double Left(CircuitComponent part) => CircuitCanvas.BoundsOf(part).Left;

    private static double CentreX(CircuitComponent part) => CircuitCanvas.BoundsOf(part).Center.X;

    private static double CentreY(CircuitComponent part) => CircuitCanvas.BoundsOf(part).Center.Y;

    /// <summary>Applies a set of moves, so the assertions can read positions off the parts.</summary>
    private static void Move(IReadOnlyDictionary<CircuitComponent, (double X, double Y)> moves)
    {
        foreach (var (part, (x, y)) in moves)
        {
            part.X = x;
            part.Y = y;
        }
    }

    [Fact]
    public void AligningLeftPutsEveryLeftEdgeOnTheLeftmost()
    {
        var parts = Row((100, 0), (140, 40), (80, 80));

        var leftmost = parts.Min(Left);

        Move(Arrange.Align(parts, AlignTo.Left));

        Assert.All(parts, p => Assert.Equal(leftmost, Left(p), 0.01));

        // And nothing moved vertically.
        Assert.Equal([0, 40, 80], parts.Select(p => p.Y));
    }

    [Fact]
    public void AligningTopPutsEveryTopEdgeOnTheTopmost()
    {
        var parts = Row((0, 100), (40, 140), (80, 60));

        var topmost = parts.Min(p => CircuitCanvas.BoundsOf(p).Top);

        Move(Arrange.Align(parts, AlignTo.Top));

        Assert.All(parts, p => Assert.Equal(topmost, CircuitCanvas.BoundsOf(p).Top, 0.01));
        Assert.Equal([0, 40, 80], parts.Select(p => p.X));
    }

    /// <summary>A centre line is the average of the middles, so the selection does not drift.</summary>
    [Fact]
    public void AligningToACentreLinePutsThemAllOnTheAverage()
    {
        var parts = Row((0, 0), (60, 0), (120, 0));

        var average = parts.Average(CentreX);

        Move(Arrange.Align(parts, AlignTo.HorizontalCentre));

        Assert.All(parts, p => Assert.Equal(average, CentreX(p), 0.01));
    }

    /// <summary>Spreading leaves the two outermost exactly where they were.</summary>
    [Fact]
    public void SpreadingKeepsTheExtremesStill()
    {
        var parts = Row((0, 0), (10, 0), (20, 0), (300, 0));

        var before = parts.Select(CentreX).ToList();

        Move(Arrange.Spread(parts, SpreadAlong.Horizontal));

        var after = parts.Select(CentreX).ToList();

        Assert.Equal(before[0], after[0], 0.01);
        Assert.Equal(before[3], after[3], 0.01);
    }

    /// <summary>And puts equal gaps between everything in between.</summary>
    [Fact]
    public void SpreadingLeavesEqualGaps()
    {
        var parts = Row((0, 0), (10, 0), (20, 0), (300, 0));

        Move(Arrange.Spread(parts, SpreadAlong.Horizontal));

        var centres = parts.Select(CentreX).Order().ToList();

        var gap = centres[1] - centres[0];

        for (var i = 2; i < centres.Count; i++)
            Assert.Equal(gap, centres[i] - centres[i - 1], 0.01);
    }

    [Fact]
    public void SpreadingVerticallyWorksTheSameWayDown()
    {
        var parts = Row((0, 0), (0, 5), (0, 9), (0, 200));

        Move(Arrange.Spread(parts, SpreadAlong.Vertical));

        var centres = parts.Select(CentreY).Order().ToList();
        var gap = centres[1] - centres[0];

        for (var i = 2; i < centres.Count; i++)
            Assert.Equal(gap, centres[i] - centres[i - 1], 0.01);

        // Nothing moved sideways.
        Assert.All(parts, p => Assert.Equal(0.0, p.X, 0.01));
    }

    /// <summary>
    /// Anything already in place is left out, so an arrangement that changes nothing produces no
    /// moves — and therefore no undo step for something that did not happen.
    /// </summary>
    [Fact]
    public void PartsAlreadyInLineAreNotMoved()
    {
        var parts = Row((50, 0), (50, 40), (50, 80));

        Assert.Empty(Arrange.Align(parts, AlignTo.Left));
        Assert.Empty(Arrange.Align(parts, AlignTo.HorizontalCentre));
    }

    [Fact]
    public void ItTakesTwoToAlignAndThreeToSpread()
    {
        Assert.Empty(Arrange.Align(Row((0, 0)), AlignTo.Left));
        Assert.Empty(Arrange.Align([], AlignTo.Left));

        // Two parts are already evenly spread by definition.
        Assert.Empty(Arrange.Spread(Row((0, 0), (100, 0)), SpreadAlong.Horizontal));
    }

    /// <summary>
    /// Lining up must not knock parts off the grid, or the next wire drawn to one will not meet
    /// it. The moves are rounded when snapping is on.
    /// </summary>
    [Fact]
    public void SnappingKeepsThemOnTheGrid()
    {
        var parts = Row((0, 0), (33, 0), (67, 0), (100, 0));

        var moves = Arrange.OnGrid(Arrange.Spread(parts, SpreadAlong.Horizontal), grid: 10);

        Move(moves);

        Assert.All(parts, p => Assert.Equal(0.0, p.X % 10, 0.01));
    }
}
