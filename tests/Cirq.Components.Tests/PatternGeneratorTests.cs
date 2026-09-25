using Cirq.Components.Digital;
using Cirq.Core.Digital;

namespace Cirq.Components.Tests;

/// <summary>
/// A written sequence of bits, played onto the logic lines.
/// <para>
/// The notation is the point as much as the playback: a pattern is read far more often than it is
/// written, and a row per line with a character per step is a timing diagram somebody can check
/// against the one in the datasheet.
/// </para>
/// </summary>
public class PatternGeneratorTests
{
    /// <summary>A two-bit count: the low line toggling every step, the high one every two.</summary>
    private static PatternGenerator Counting() => new()
    {
        Pattern = "0101\n0011",
        StepRate = 1000.0,
        Name = "PG1",
        Repeats = false,
    };

    [Fact]
    public void ThePatternIsPlayedOneStepAtATime()
    {
        var pattern = Counting();

        // A step is a millisecond; sample the middle of each so no rounding is being tested.
        Assert.Equal(LogicState.Low, pattern.StateAt(0.5e-3, 0));
        Assert.Equal(LogicState.High, pattern.StateAt(1.5e-3, 0));
        Assert.Equal(LogicState.Low, pattern.StateAt(2.5e-3, 0));
        Assert.Equal(LogicState.High, pattern.StateAt(3.5e-3, 0));
    }

    /// <summary>The second row is a second line, moving at half the rate of the first.</summary>
    [Fact]
    public void EachRowIsItsOwnLine()
    {
        var pattern = Counting();

        Assert.Equal(LogicState.Low, pattern.StateAt(0.5e-3, 1));
        Assert.Equal(LogicState.Low, pattern.StateAt(1.5e-3, 1));
        Assert.Equal(LogicState.High, pattern.StateAt(2.5e-3, 1));
        Assert.Equal(LogicState.High, pattern.StateAt(3.5e-3, 1));
    }

    /// <summary>Spaces group a long pattern without being steps, which is the only way to count one.</summary>
    [Fact]
    public void SpacesAreGroupingRatherThanSteps()
    {
        var spaced = new PatternGenerator { Pattern = "0101 0101", StepRate = 1000.0 };
        var bare = new PatternGenerator { Pattern = "01010101", StepRate = 1000.0 };

        Assert.Equal(8, spaced.StepCount);
        Assert.Equal(bare.StepCount, spaced.StepCount);
    }

    /// <summary>A dash releases the line, so a pattern can share a bus with something else.</summary>
    [Fact]
    public void ADashReleasesTheLine()
    {
        var pattern = new PatternGenerator { Pattern = "1-01", StepRate = 1000.0, Repeats = false };

        Assert.Equal(LogicState.High, pattern.StateAt(0.5e-3, 0));
        Assert.Equal(LogicState.Unknown, pattern.StateAt(1.5e-3, 0));
        Assert.Equal(LogicState.Low, pattern.StateAt(2.5e-3, 0));
    }

    /// <summary>
    /// A row shorter than the longest holds its last value, so a line that never changes is one
    /// character rather than a row padded out to the length of its neighbour.
    /// </summary>
    [Fact]
    public void AShortRowHoldsItsLastValue()
    {
        var pattern = new PatternGenerator { Pattern = "00110011\n1", StepRate = 1000.0, Repeats = false };

        Assert.Equal(8, pattern.StepCount);

        foreach (var step in new[] { 0.5e-3, 3.5e-3, 7.5e-3 })
            Assert.Equal(LogicState.High, pattern.StateAt(step, 1));
    }

    /// <summary>A line no row was written for is released rather than driven to a level.</summary>
    [Fact]
    public void ALineThePatternNeverMentionsIsReleased()
    {
        var pattern = new PatternGenerator { Pattern = "0101", StepRate = 1000.0 };

        // Four pins, one row: the other three have nothing said about them.
        Assert.Equal(4, pattern.OutputTerminals.Count);
        Assert.Equal(LogicState.Unknown, pattern.StateAt(0.5e-3, 2));
    }

    [Fact]
    public void ItRepeatsWhenAskedAndHoldsWhenNot()
    {
        var repeating = Counting();
        repeating.Repeats = true;

        Assert.Equal(repeating.StateAt(1.5e-3, 0), repeating.StateAt(5.5e-3, 0));

        var once = Counting();

        // Past the end it stays where the last step left it.
        Assert.Equal(LogicState.High, once.StateAt(50e-3, 0));
    }

    /// <summary>Nothing happens before the delay, and the delay itself is a breakpoint.</summary>
    [Fact]
    public void AStartDelayHoldsEverythingOff()
    {
        var pattern = Counting();
        pattern.StartDelay = 2e-3;

        Assert.Equal(LogicState.Unknown, pattern.StateAt(1e-3, 0));
        Assert.Equal(LogicState.Low, pattern.StateAt(2.5e-3, 0));
        Assert.Equal(2e-3, pattern.NextBreakpointAfter(0)!.Value, 1e-12);
    }

    /// <summary>
    /// Every step boundary is offered to the solver. Without it a fast pattern arrives at the
    /// circuit with pulses missing, because the transient loop is free to step over anything
    /// nothing declared.
    /// </summary>
    [Fact]
    public void EveryStepBoundaryIsABreakpoint()
    {
        var pattern = Counting();

        Assert.Equal(1e-3, pattern.NextBreakpointAfter(0.5e-3)!.Value, 1e-12);
        Assert.Equal(2e-3, pattern.NextBreakpointAfter(1e-3)!.Value, 1e-12);
        Assert.Equal(3e-3, pattern.NextBreakpointAfter(2.5e-3)!.Value, 1e-12);

        // The last step's end is the last thing that happens, and then nothing does.
        Assert.Null(pattern.NextBreakpointAfter(3.5e-3));
    }

    /// <summary>A repeating pattern never runs out of boundaries.</summary>
    [Fact]
    public void ARepeatingPatternKeepsOfferingBoundaries()
    {
        var pattern = Counting();
        pattern.Repeats = true;

        Assert.Equal(11e-3, pattern.NextBreakpointAfter(10.5e-3)!.Value, 1e-12);
    }

    /// <summary>An empty pattern drives nothing and asks for nothing, rather than failing.</summary>
    [Fact]
    public void AnEmptyPatternIsQuiet()
    {
        var pattern = new PatternGenerator { Pattern = string.Empty };

        Assert.Equal(0, pattern.StepCount);
        Assert.Equal(LogicState.Unknown, pattern.StateAt(1e-3, 0));
        Assert.Null(pattern.NextBreakpointAfter(0));
        Assert.Equal("empty", pattern.ValueLabel);
    }
}
