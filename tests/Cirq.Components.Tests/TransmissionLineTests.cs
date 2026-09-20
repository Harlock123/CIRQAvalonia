using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// Reflections, which is the only reason the component exists. Every figure here comes from the
/// two-line derivation in any textbook — the wave is halved by the source impedance, multiplied by
/// the reflection coefficient at each end, and takes one delay to travel — so the simulation is
/// being checked against arithmetic rather than against a previous run of itself.
/// </summary>
public class TransmissionLineTests
{
    private sealed record Rig(CircuitSimulator Sim, TransmissionLine Line, Terminal Near, Terminal Far);

    /// <summary>A step from a source of a chosen impedance into a line with a chosen load.</summary>
    private static Rig Build(double sourceResistance, double load, double amplitude = 10.0)
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());

        // Half a period is far longer than the run, so within the run this is simply a step.
        var source = circuit.Add(new FunctionGenerator(Waveform.Square, 1e6, amplitude)
        {
            DcOffset = amplitude / 2, EdgeTime = 100e-12, OutputResistance = sourceResistance,
        });

        var line = circuit.Add(new TransmissionLine
        {
            CharacteristicImpedance = 50, Length = 1.0, VelocityFactor = 0.66,
        });

        var terminator = circuit.Add(new Resistor(load));

        circuit.Connect(source.Return, gnd.Pin);
        circuit.Connect(source.Output, line.NearPlus);
        circuit.Connect(line.NearMinus, gnd.Pin);
        circuit.Connect(line.FarMinus, gnd.Pin);
        circuit.Connect(line.FarPlus, terminator.A);
        circuit.Connect(terminator.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit,
            new SimulationSettings { TimeStep = 200e-12, MaxTimeStep = 200e-12 });

        sim.Reset();
        sim.SolveOperatingPoint();

        return new Rig(sim, line, source.Output, terminator.A);
    }

    private static double At(Rig rig, Terminal terminal, double time)
    {
        while (rig.Sim.Time < time) rig.Sim.Step();

        return rig.Sim.NodeVoltage(terminal);
    }

    /// <summary>
    /// The thing that surprises people: a matched source driving a line sees <i>half</i> its
    /// voltage at the near end, because for the first round trip the line is simply a 50 Ω
    /// resistor. Nothing is wrong, and nothing about the far end has reached the driver yet.
    /// </summary>
    [Fact]
    public void ALineLooksLikeItsOwnImpedanceUntilTheFarEndAnswers()
    {
        var rig = Build(sourceResistance: 50, load: 1e6);

        Assert.Equal(5.0, At(rig, rig.Near, rig.Line.Delay * 0.5), 0.2);
    }

    [Fact]
    public void ThereIsNothingAtTheFarEndUntilTheWaveGetsThere()
    {
        var rig = Build(sourceResistance: 50, load: 1e6);

        Assert.Equal(0.0, At(rig, rig.Far, rig.Line.Delay * 0.8), 0.2);
        Assert.Equal(10.0, At(rig, rig.Far, rig.Line.Delay * 1.3), 0.2);
    }

    /// <summary>
    /// An open end sends the whole wave back the same way up, so the far end briefly sits at
    /// twice what was sent — and the near end knows nothing about it for another whole delay.
    /// </summary>
    [Fact]
    public void AnOpenEndDoublesTheWaveAndTheNearEndHearsItOneDelayLater()
    {
        var rig = Build(sourceResistance: 50, load: 1e6);

        Assert.Equal(10.0, At(rig, rig.Far, rig.Line.Delay * 1.5), 0.2);
        Assert.Equal(5.0, At(rig, rig.Near, rig.Line.Delay * 1.8), 0.2);
        Assert.Equal(10.0, At(rig, rig.Near, rig.Line.Delay * 2.4), 0.2);
    }

    /// <summary>A short sends it back inverted, and the two cancel.</summary>
    [Fact]
    public void AShortedEndSendsTheWaveBackUpsideDown()
    {
        var rig = Build(sourceResistance: 50, load: 0.01);

        Assert.Equal(0.0, At(rig, rig.Far, rig.Line.Delay * 1.5), 0.2);
        Assert.Equal(0.0, At(rig, rig.Near, rig.Line.Delay * 2.5), 0.3);
    }

    /// <summary>
    /// Terminated in its own impedance the line behaves as a wire with a delay: half the source
    /// voltage arrives, once, and nothing comes back. This is what termination is <i>for</i>.
    /// </summary>
    [Fact]
    public void AMatchedLoadAbsorbsTheWaveCompletely()
    {
        var rig = Build(sourceResistance: 50, load: 50);

        Assert.Equal(5.0, At(rig, rig.Far, rig.Line.Delay * 1.5), 0.2);
        Assert.Equal(5.0, At(rig, rig.Near, rig.Line.Delay * 3.0), 0.2);
        Assert.Equal(5.0, At(rig, rig.Far, rig.Line.Delay * 5.0), 0.2);
    }

    /// <summary>
    /// The staircase. A stiff driver into an unterminated line reflects at both ends, so the far
    /// end climbs towards the final voltage in steps two delays apart instead of arriving at it —
    /// which on a scope looks like a broken driver and is nothing of the kind.
    /// </summary>
    [Fact]
    public void AStiffDriverIntoAnOpenLineGivesAStaircase()
    {
        // 5 Ω against 50 Ω: the near end starts at about a tenth of the way and works up.
        var rig = Build(sourceResistance: 5, load: 1e6);

        var first = At(rig, rig.Far, rig.Line.Delay * 1.5);
        var second = At(rig, rig.Far, rig.Line.Delay * 3.5);
        var third = At(rig, rig.Far, rig.Line.Delay * 5.5);

        Assert.True(first > 15.0, $"the first step overshoots hard, not {first:0.00} V");
        Assert.True(second < first, $"the second step should come back down, {second:0.00} vs {first:0.00}");
        Assert.True(Math.Abs(third - 10.0) < Math.Abs(first - 10.0),
            "each round trip should land nearer the final voltage");
    }

    /// <summary>
    /// And the cure that is not where people expect it. A series resistor at the <b>driver</b>,
    /// making the source impedance match the line, kills the staircase — because the reflection
    /// that comes back from the far end is absorbed there instead of being sent out again.
    /// </summary>
    [Fact]
    public void ASeriesResistorAtTheSourceStopsTheRinging()
    {
        var rig = Build(sourceResistance: 50, load: 1e6);

        // One overshoot to the final value and then nothing: no second reflection at all.
        var settled = At(rig, rig.Far, rig.Line.Delay * 1.5);
        var later = At(rig, rig.Far, rig.Line.Delay * 5.5);

        Assert.Equal(10.0, settled, 0.2);
        Assert.Equal(10.0, later, 0.2);
    }

    [Fact]
    public void TheDelayIsTheLengthOverTheSpeedOfTheWave()
    {
        var line = new TransmissionLine { Length = 1.0, VelocityFactor = 0.66 };

        // 1 m at 0.66c is about 5 ns, which is the number every layout rule of thumb comes from.
        Assert.Equal(5.05e-9, line.Delay, 0.1e-9);
    }

    [Theory]
    [InlineData(50.0, 0.0)]
    [InlineData(150.0, 0.5)]
    [InlineData(0.0, -1.0)]
    public void TheReflectionCoefficientIsTheTextbookOne(double load, double expected)
    {
        var line = new TransmissionLine { CharacteristicImpedance = 50 };

        Assert.Equal(expected, line.ReflectionFrom(load), 3);
    }
}
