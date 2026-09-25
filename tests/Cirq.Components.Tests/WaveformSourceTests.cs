using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// A source that plays back a list of points.
/// <para>
/// Most of what is checked here is the interpolation and the breakpoints, because those are the two
/// things that decide whether the circuit is fed the waveform on the screen or something that
/// merely resembles it.
/// </para>
/// </summary>
public class WaveformSourceTests
{
    /// <summary>A ten microsecond ramp from nothing to five volts, then flat.</summary>
    private static WaveformSource Ramp() =>
        new() { Points = "0 0; 10u 5; 20u 5", Name = "AWG1" };

    [Fact]
    public void ItHoldsTheFirstValueBeforeTheWaveformStarts()
    {
        var source = Ramp();

        Assert.Equal(0.0, source.ValueAt(-1.0), 1e-12);
        Assert.Equal(0.0, source.ValueAt(0.0), 1e-12);
    }

    /// <summary>Halfway up a linear ramp is half of it, which is the whole of the interpolation.</summary>
    [Theory]
    [InlineData(2.5e-6, 1.25)]
    [InlineData(5e-6, 2.5)]
    [InlineData(7.5e-6, 3.75)]
    [InlineData(10e-6, 5.0)]
    public void ItInterpolatesLinearlyBetweenPoints(double time, double expected) =>
        Assert.Equal(expected, Ramp().ValueAt(time), 1e-9);

    /// <summary>Times and values both read SI prefixes, because that is how anybody writes them.</summary>
    [Fact]
    public void PrefixesAreUnderstoodOnBothColumns()
    {
        var source = new WaveformSource { Points = "0 0; 1m 4.5; 2m 4.5" };

        Assert.Equal(4.5, source.ValueAt(1e-3), 1e-9);
        Assert.Equal(2.25, source.ValueAt(0.5e-3), 1e-9);
    }

    /// <summary>
    /// A table out of order is sorted rather than rejected. A column of points pasted from a
    /// spreadsheet that happens to be sorted by voltage is a mistake worth recovering from.
    /// </summary>
    [Fact]
    public void AnOutOfOrderTableIsSorted()
    {
        var source = new WaveformSource { Points = "20u 5; 0 0; 10u 5" };

        Assert.Equal(2.5, source.ValueAt(5e-6), 1e-9);
        Assert.Equal(3, source.PointCount);
    }

    [Theory]
    [InlineData(WaveformEnding.Hold, 5.0)]
    [InlineData(WaveformEnding.Zero, 0.0)]
    public void TheEndingDecidesWhatHappensAfterTheLastPoint(WaveformEnding ending, double expected)
    {
        var source = Ramp();
        source.Ending = ending;

        Assert.Equal(expected, source.ValueAt(1.0), 1e-9);
    }

    /// <summary>A repeat starts again, so a point one full span on reads what the start reads.</summary>
    [Fact]
    public void ARepeatStartsAgain()
    {
        var source = new WaveformSource { Points = "0 0; 10u 10; 20u 0", Ending = WaveformEnding.Repeat };

        Assert.Equal(source.ValueAt(5e-6), source.ValueAt(25e-6), 1e-9);
        Assert.Equal(source.ValueAt(5e-6), source.ValueAt(45e-6), 1e-9);
    }

    /// <summary>Scale, offset and time scale all reshape the table without editing it.</summary>
    [Fact]
    public void TheTableCanBeReshapedWithoutBeingRewritten()
    {
        var source = Ramp();

        source.Scale = 2.0;
        Assert.Equal(5.0, source.ValueAt(5e-6), 1e-9);

        source.Scale = 1.0;
        source.DcOffset = -2.5;
        Assert.Equal(0.0, source.ValueAt(5e-6), 1e-9);

        source.DcOffset = 0;
        source.TimeScale = 2.0;
        Assert.Equal(2.5, source.ValueAt(10e-6), 1e-9);
    }

    /// <summary>A start delay shifts the whole waveform without touching its shape.</summary>
    [Fact]
    public void AStartDelayShiftsTheWholeThing()
    {
        var source = Ramp();
        source.StartDelay = 1e-3;

        Assert.Equal(0.0, source.ValueAt(0.5e-3), 1e-9);
        Assert.Equal(2.5, source.ValueAt(1e-3 + 5e-6), 1e-9);
    }

    /// <summary>
    /// Every corner is offered to the solver, in order. This is what keeps a short pulse from being
    /// stepped over: between breakpoints the transient loop takes whatever step the local error
    /// allows, and a two-microsecond glitch nothing declared is a glitch it never visits.
    /// </summary>
    [Fact]
    public void EveryCornerIsABreakpoint()
    {
        var source = Ramp();

        Assert.Equal(0.0, source.NextBreakpointAfter(-1e-6)!.Value, 1e-15);
        Assert.Equal(10e-6, source.NextBreakpointAfter(0.0)!.Value, 1e-15);
        Assert.Equal(20e-6, source.NextBreakpointAfter(10e-6)!.Value, 1e-15);
        Assert.Null(source.NextBreakpointAfter(20e-6));
    }

    /// <summary>A repeating waveform keeps offering corners forever, shifted by whole passes.</summary>
    [Fact]
    public void ARepeatKeepsOfferingCorners()
    {
        var source = Ramp();
        source.Ending = WaveformEnding.Repeat;

        Assert.Equal(30e-6, source.NextBreakpointAfter(25e-6)!.Value, 1e-12);
        Assert.Equal(40e-6, source.NextBreakpointAfter(30e-6)!.Value, 1e-12);
    }

    /// <summary>The corners move with the time scale and the start delay, like everything else.</summary>
    [Fact]
    public void TheCornersFollowTheTimeScaleAndDelay()
    {
        var source = Ramp();
        source.TimeScale = 2.0;
        source.StartDelay = 1e-3;

        Assert.Equal(1e-3 + 20e-6, source.NextBreakpointAfter(1e-3)!.Value, 1e-12);
    }

    /// <summary>
    /// The source drives a real circuit: a ramp across a resistor puts a proportional current
    /// through it, which is the end-to-end check that the stamping and the playback agree.
    /// </summary>
    [Fact]
    public void ItDrivesACircuit()
    {
        var circuit = new Circuit();

        var source = circuit.Add(Ramp());
        var r = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Wires.Add(new WireSegment { SourceTerminal = source.Output, TargetTerminal = r.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = r.B, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = source.Return, TargetTerminal = ground.Pin });

        var sim = new CircuitSimulator(circuit, new SimulationSettings
        {
            TimeStep = 1e-7,
            MaxTimeStep = 1e-7,
        });

        sim.Reset();
        sim.SolveOperatingPoint();

        while (sim.Time < 15e-6) sim.Step();

        // Past the top of the ramp: five volts across a kilohm is five milliamps.
        Assert.Equal(5e-3, sim.TerminalCurrent(r.A), 1e-6);
    }

    /// <summary>
    /// A table written out and read back is the same waveform. This is the path an import takes,
    /// so a rounding that lost the shape would lose it silently.
    /// </summary>
    [Fact]
    public void ATabulatedWaveformPlaysBackAsItWasWritten()
    {
        double[] samples = [0.0, 0.25, 0.5, 0.75, 1.0];

        var source = new WaveformSource { Points = WaveformSource.Tabulate(samples, 1e-3) };

        Assert.Equal(5, source.PointCount);

        for (var i = 0; i < samples.Length; i++)
            Assert.Equal(samples[i], source.ValueAt(i * 1e-3), 1e-6);
    }

    /// <summary>Nonsense in the table is skipped rather than throwing at the part that parses it.</summary>
    [Fact]
    public void RubbishInTheTableIsSkipped()
    {
        var source = new WaveformSource { Points = "time value\n0 0\nnonsense\n1m 5" };

        Assert.Equal(2, source.PointCount);
        Assert.Equal(2.5, source.ValueAt(0.5e-3), 1e-9);
    }

    /// <summary>An empty table produces the offset and nothing else, rather than failing.</summary>
    [Fact]
    public void AnEmptyTableIsFlat()
    {
        var source = new WaveformSource { Points = string.Empty, DcOffset = 1.5 };

        Assert.Equal(0, source.PointCount);
        Assert.Equal(1.5, source.ValueAt(1.0), 1e-12);
        Assert.Null(source.NextBreakpointAfter(0));
    }
}
