using Cirq.Components.Ics;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// Noise has to be noise — the right size, the right shape, band-limited rather than a fresh
/// number every time point, and the same on every run.
/// </summary>
public class NoiseSourceTests
{
    private static (CircuitSimulator Sim, NoiseSource Source, Resistor Load) Rig(
        double rms = 0.1, double bandwidth = 100e3, int seed = 1)
    {
        var circuit = new Circuit();
        var noise = circuit.Add(new NoiseSource
        {
            RmsVoltage = rms,
            Bandwidth = bandwidth,
            Seed = seed,
            SeriesResistance = 1.0,
        });

        var load = circuit.Add(new Resistor(1e6));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(noise.A, load.A);
        circuit.Connect(load.B, gnd.Pin);
        circuit.Connect(noise.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit,
            new SimulationSettings { TimeStep = 1e-6, MaxTimeStep = 2e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, noise, load);
    }

    private static List<double> Collect(CircuitSimulator sim, Resistor load, int samples, double step)
    {
        List<double> values = [];

        for (var i = 0; i < samples; i++)
        {
            sim.Run(step);
            values.Add(sim.NodeVoltage(load.A));
        }

        return values;
    }

    /// <summary>The RMS of what comes out is the RMS that was asked for.</summary>
    [Theory]
    [InlineData(0.05)]
    [InlineData(0.2)]
    public void TheAmplitudeIsTheRmsYouAskedFor(double rms)
    {
        var (sim, _, load) = Rig(rms, bandwidth: 50e3);
        var values = Collect(sim, load, 3000, 4e-6);

        var mean = values.Average();
        var measured = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);

        Assert.Equal(rms, measured, rms * 0.2);
    }

    /// <summary>And it is centred on zero, so it adds noise rather than an offset.</summary>
    [Fact]
    public void ItHasNoOffsetOfItsOwn()
    {
        var (sim, _, load) = Rig(0.1, bandwidth: 50e3);
        var values = Collect(sim, load, 4000, 4e-6);

        Assert.Equal(0.0, values.Average(), 0.02);
    }

    /// <summary>
    /// The same circuit run twice gives the same noise. Results that cannot be reproduced are
    /// not much use for working out why something misbehaved.
    /// </summary>
    [Fact]
    public void TheSameSeedGivesTheSameNoise()
    {
        var first = Rig(0.1, seed: 7);
        var second = Rig(0.1, seed: 7);

        var a = Collect(first.Sim, first.Load, 200, 4e-6);
        var b = Collect(second.Sim, second.Load, 200, 4e-6);

        Assert.Equal(a, b);
    }

    /// <summary>A different seed gives different noise of the same character.</summary>
    [Fact]
    public void ADifferentSeedGivesDifferentNoise()
    {
        var first = Rig(0.1, seed: 1);
        var second = Rig(0.1, seed: 2);

        var a = Collect(first.Sim, first.Load, 200, 4e-6);
        var b = Collect(second.Sim, second.Load, 200, 4e-6);

        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// It is band-limited, not a fresh number every time point. Sampling several times inside one
    /// interval has to give the same value, or the noise would have no bandwidth at all and its
    /// character would depend on the solver's step size.
    /// </summary>
    [Fact]
    public void ASampleIsHeldForItsWholeInterval()
    {
        // One sample every hundred microseconds, looked at every ten.
        var (sim, source, load) = Rig(0.1, bandwidth: 10e3);

        Assert.Equal(100e-6, source.SampleSeconds, 1e-9);

        var values = Collect(sim, load, 40, 10e-6);
        var distinct = values.Distinct().Count();

        // Forty looks across four hundred microseconds is four or five distinct values, not forty.
        Assert.InRange(distinct, 2, 8);
    }

    /// <summary>Turning it down turns the noise down rather than changing its character.</summary>
    [Fact]
    public void TurningItDownTurnsTheNoiseDown()
    {
        var loud = Rig(0.2, seed: 3);
        var quiet = Rig(0.02, seed: 3);

        var a = Collect(loud.Sim, loud.Load, 1000, 4e-6);
        var b = Collect(quiet.Sim, quiet.Load, 1000, 4e-6);

        static double Rms(List<double> v)
        {
            var mean = v.Average();
            return Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / v.Count);
        }

        Assert.Equal(10.0, Rms(a) / Rms(b), 2.0);
    }
}

/// <summary>
/// The output swing of an op-amp is not symmetric on the parts where it matters, and the
/// difference between an LM358 and a rail-to-rail part is the whole point of having both.
/// </summary>
public class OutputSwingTests
{
    /// <summary>A follower on a single supply, driven at <paramref name="input"/> volts.</summary>
    private static double FollowerOutput(OpAmpModel model, double input, double supply = 5.0)
    {
        var circuit = new Circuit();

        var rail = circuit.Add(new DcVoltageSource(supply));
        var signal = circuit.Add(new DcVoltageSource(input));
        var amp = circuit.Add(new OperationalAmplifier(model));
        var load = circuit.Add(new Resistor(100e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(signal.Negative, gnd.Pin);

        circuit.Connect(amp.PositiveSupply, rail.Positive);
        circuit.Connect(amp.NegativeSupply, gnd.Pin);

        circuit.Connect(amp.NonInverting, signal.Positive);
        circuit.Connect(amp.Output, amp.Inverting);
        circuit.Connect(amp.Output, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(2e-3);

        return sim.NodeVoltage(amp.Output);
    }

    /// <summary>
    /// How much of a five volt supply each part can actually use. This is the comparison the
    /// MCP6002 exists to make: a rail-to-rail output reaches within a few tens of millivolts of
    /// the supply, where the others stop the better part of a volt short.
    /// </summary>
    [Fact]
    public void ARailToRailPartUsesMoreOfTheSupplyThanTheOthers()
    {
        var mcp = FollowerOutput(OpAmpModel.Mcp6002, 4.9);
        var lm358 = FollowerOutput(OpAmpModel.Lm358, 4.9);
        var lm741 = FollowerOutput(OpAmpModel.Lm741, 4.9);

        Assert.True(mcp > 4.8, $"a rail-to-rail part should reach 4.9 V, not {mcp:0.00}");
        Assert.True(lm358 < mcp - 0.5, $"{lm358:0.00} should be well below {mcp:0.00}");
        Assert.True(lm741 < lm358, $"{lm741:0.00} should be below the LM358's {lm358:0.00}");
    }

    /// <summary>And at the bottom of the supply as well as the top.</summary>
    [Fact]
    public void ARailToRailPartReachesTheBottomToo()
    {
        var mcp = FollowerOutput(OpAmpModel.Mcp6002, 0.05);
        var lm741 = FollowerOutput(OpAmpModel.Lm741, 0.05);

        Assert.True(mcp < 0.1, $"{mcp:0.000} V should be within a whisker of ground");
        Assert.True(lm741 > mcp + 0.5, $"{lm741:0.00} should be well above it");
    }

    /// <summary>
    /// Halfway up the supply every part is just a buffer and they all agree, so the difference
    /// between them really is about the rails rather than about accuracy.
    /// </summary>
    [Fact]
    public void HalfwayUpTheyAllAgree()
    {
        foreach (var model in new[] { OpAmpModel.Lm741, OpAmpModel.Lm358, OpAmpModel.Mcp6002 })
            Assert.Equal(2.5, FollowerOutput(model, 2.5), 0.05);
    }

    /// <summary>
    /// And the reason the other two exist: on a single five volt supply an LM741 cannot put out
    /// one volt at all. Its output stops a volt and a half above the negative rail, so a follower
    /// asked for one volt sits at one and a half and looks broken — which is exactly what happens
    /// on a breadboard, and why "single supply" is a thing you check before choosing a part.
    /// </summary>
    [Fact]
    public void AnLm741CannotReachOneVoltOnASingleSupply()
    {
        var lm741 = FollowerOutput(OpAmpModel.Lm741, 1.0);
        var mcp = FollowerOutput(OpAmpModel.Mcp6002, 1.0);

        Assert.True(lm741 > 1.3, $"{lm741:0.00} V — the 741 should have clipped well above one volt");
        Assert.Equal(1.0, mcp, 0.05);
    }
}

/// <summary>
/// The point of having a noise source at all: it makes the reason hysteresis exists visible.
/// A comparator watching a slow signal cross a threshold should change once. With noise on the
/// signal it changes many times, and adding hysteresis stops it.
/// </summary>
public class HysteresisTests
{
    /// <summary>
    /// A comparator on a slow ramp through its threshold, with noise on the input and optionally
    /// a feedback resistor to give it two thresholds instead of one.
    /// </summary>
    private static int CountTransitions(double noiseRms, double feedback)
    {
        var circuit = new Circuit();

        var rail = circuit.Add(new DcVoltageSource(5.0));
        var reference = circuit.Add(new DcVoltageSource(2.5));

        var ramp = circuit.Add(new FunctionGenerator
        {
            Shape = Waveform.Triangle,
            Frequency = 20,
            AmplitudePeakToPeak = 2.0,
            DcOffset = 2.5,
        });

        var noise = circuit.Add(new NoiseSource { RmsVoltage = noiseRms, Bandwidth = 50e3, Seed = 5 });
        var comparator = circuit.Add(new Comparator(ComparatorModel.Lm393));
        var pullUp = circuit.Add(new Resistor(4.7e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(reference.Negative, gnd.Pin);
        circuit.Connect(ramp.Return, gnd.Pin);

        circuit.Connect(comparator.PositiveSupply, rail.Positive);
        circuit.Connect(comparator.NegativeSupply, gnd.Pin);

        // A resistor between the source and the input. Hysteresis is a divider between this and
        // the feedback resistor, so without it the feedback works against the generator's own
        // fifty ohms and moves the threshold by a millivolt — nothing at all against the noise.
        var series = circuit.Add(new Resistor(10e3));

        circuit.Connect(ramp.Output, noise.A);
        circuit.Connect(noise.B, series.A);
        circuit.Connect(series.B, comparator.NonInverting);
        circuit.Connect(reference.Positive, comparator.Inverting);

        circuit.Connect(rail.Positive, pullUp.A);
        circuit.Connect(pullUp.B, comparator.Output);

        if (feedback > 0)
        {
            var hysteresis = circuit.Add(new Resistor(feedback));
            circuit.Connect(comparator.Output, hysteresis.A);
            circuit.Connect(hysteresis.B, comparator.NonInverting);
        }

        var sim = new CircuitSimulator(circuit,
            new SimulationSettings { TimeStep = 2e-6, MaxTimeStep = 4e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();

        var transitions = 0;
        var wasHigh = sim.NodeVoltage(comparator.Output) > 2.5;

        // A 20 Hz triangle starts at its minimum and reaches the middle a quarter of a period
        // later, so the crossing is at 12.5 ms — the window has to run past that to contain it.
        for (var i = 0; i < 5000; i++)
        {
            sim.Run(4e-6);

            var high = sim.NodeVoltage(comparator.Output) > 2.5;
            if (high != wasHigh) transitions++;

            wasHigh = high;
        }

        return transitions;
    }

    /// <summary>A clean ramp crosses once and the output changes once. That is the ideal case.</summary>
    [Fact]
    public void WithoutNoiseItChangesOnce()
    {
        Assert.InRange(CountTransitions(noiseRms: 0, feedback: 0), 1, 2);
    }

    /// <summary>
    /// With noise on the signal it changes many times. Every wobble either side of the threshold
    /// is another transition, and a circuit downstream counting edges counts all of them.
    /// </summary>
    [Fact]
    public void WithNoiseItChatters()
    {
        var transitions = CountTransitions(noiseRms: 12e-3, feedback: 0);

        Assert.True(transitions > 6,
            $"{transitions} transitions — a bare comparator on a noisy ramp should chatter");
    }

    /// <summary>
    /// And hysteresis stops it. The feedback resistor moves the threshold away from the signal
    /// the moment the output changes, so the noise cannot reach back across it. This is what the
    /// LM311, the LM393 and the 74HC14 are for.
    /// </summary>
    [Fact]
    public void HysteresisStopsTheChatter()
    {
        var bare = CountTransitions(noiseRms: 12e-3, feedback: 0);
        var withFeedback = CountTransitions(noiseRms: 12e-3, feedback: 470e3);

        Assert.InRange(withFeedback, 1, 2);
        Assert.True(withFeedback < bare,
            $"{withFeedback} with hysteresis against {bare} without is not the lesson");
    }
}
