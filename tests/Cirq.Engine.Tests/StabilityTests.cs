using Cirq.Components.Ics;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// How much gain goes round a feedback loop, and how close it is to going round it the wrong way.
/// <para>
/// A single-pole op-amp in a feedback network has a loop gain with a closed form: T(f) =
/// Aol(f)·β, where β is the fraction of the output the divider feeds back and Aol falls at
/// 20 dB/decade from the dominant pole. So the DC loop gain is Aol·β, the crossover is at
/// GBW·β, and the phase margin of a one-pole loop is ninety degrees. Every figure here is checked
/// against that rather than against a previous run.
/// </para>
/// </summary>
public class StabilityTests
{
    /// <summary>
    /// A non-inverting amplifier with a loop probe between the feedback divider and the inverting
    /// input — the right place to break it, because the impedance looking into an op-amp input is
    /// enormous and the impedance looking back out of the divider is not.
    /// </summary>
    private static (CircuitSimulator Sim, LoopProbe Probe, OpAmpModel Model, double Beta) Loop(
        double rf, double rg, double loadFarads = 0.0)
    {
        var circuit = new Circuit();

        var positive = circuit.Add(new DcVoltageSource(15.0));
        var negative = circuit.Add(new DcVoltageSource(-15.0));

        var amp = circuit.Add(new OperationalAmplifier { Name = "U1", Model = OpAmpModel.Lm741 });
        var feedback = circuit.Add(new Resistor(rf) { Name = "RF" });
        var gain = circuit.Add(new Resistor(rg) { Name = "RG" });
        var probe = circuit.Add(new LoopProbe { Name = "LP1" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(positive.Negative, ground.Pin);
        circuit.Connect(negative.Positive, ground.Pin);
        circuit.Connect(amp.PositiveSupply, positive.Positive);
        circuit.Connect(amp.NegativeSupply, negative.Negative);

        circuit.Connect(amp.NonInverting, ground.Pin);

        // Output → RF → divider tap → probe → inverting input, and RG from the tap to ground.
        circuit.Connect(amp.Output, feedback.A);
        circuit.Connect(feedback.B, gain.A);
        circuit.Connect(gain.B, ground.Pin);
        circuit.Connect(gain.A, probe.From);
        circuit.Connect(probe.To, amp.Inverting);

        if (loadFarads > 0)
        {
            // A capacitive load works against the op-amp's output resistance to make a second
            // pole, which is the classic way to turn a comfortable loop into a ringing one.
            var load = circuit.Add(new Capacitor(loadFarads) { Name = "CL" });

            circuit.Connect(amp.Output, load.A);
            circuit.Connect(load.B, ground.Pin);
        }

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        return (sim, probe, amp.Model, rg / (rg + rf));
    }

    private static StabilityResult Measure(
        CircuitSimulator sim, LoopProbe probe, double startHz = 0.01, double stopHz = 1e7) =>
        new StabilityAnalysis(sim).Run(
            new StabilityRequest(new AcSweepRequest(startHz, stopHz, 30), probe));

    // ---- the closed form ---------------------------------------------------

    /// <summary>
    /// The DC loop gain is the open-loop gain times the feedback fraction, which for a gain-of-ten
    /// amplifier built from an Aol of 200 000 is 20 000 — 86 dB.
    /// </summary>
    [Theory]
    [InlineData(9e3, 1e3)]
    [InlineData(99e3, 1e3)]
    public void TheDcLoopGainIsTheOpenLoopGainTimesTheFeedbackFraction(double rf, double rg)
    {
        var (sim, probe, model, beta) = Loop(rf, rg);

        var result = Measure(sim, probe);

        Assert.True(result.IsUsable, result.Problem);

        var expected = 20 * Math.Log10(model.OpenLoopGain * beta);

        Assert.NotNull(result.LowFrequencyDecibels);
        Assert.Equal(expected, result.LowFrequencyDecibels.Value, 0.5);
    }

    /// <summary>
    /// The loop gain reaches one at the gain-bandwidth product times the feedback fraction — which
    /// is the same thing as saying a closed-loop gain of ten out of a 1 MHz part turns over at
    /// 100 kHz. The same number, arrived at from the other side.
    /// </summary>
    [Fact]
    public void TheCrossoverIsTheGainBandwidthProductTimesTheFeedbackFraction()
    {
        var (sim, probe, model, beta) = Loop(9e3, 1e3);

        var result = Measure(sim, probe);

        Assert.NotNull(result.CrossoverHz);
        Assert.Equal(model.GainBandwidthProduct * beta, result.CrossoverHz.Value,
            model.GainBandwidthProduct * beta * 0.05);
    }

    /// <summary>
    /// A single-pole loop has ninety degrees of phase margin, because one pole can only ever cost
    /// ninety degrees. That is what "unconditionally stable" means, and it is why an internally
    /// compensated op-amp is sold as being it.
    /// </summary>
    [Fact]
    public void ASinglePoleLoopHasNinetyDegreesOfPhaseMargin()
    {
        var (sim, probe, _, _) = Loop(9e3, 1e3);

        var result = Measure(sim, probe);

        Assert.NotNull(result.PhaseMarginDegrees);
        Assert.Equal(90.0, result.PhaseMarginDegrees.Value, 3.0);

        Assert.Contains("Comfortable", result.Verdict);
    }

    /// <summary>
    /// And it never reaches −180°, so there is no gain margin to report. That is not a fault, and
    /// reporting a number there would be inventing one.
    /// </summary>
    [Fact]
    public void ASinglePoleLoopHasNoGainMarginBecauseItNeverInverts()
    {
        var (sim, probe, _, _) = Loop(9e3, 1e3);

        Assert.Null(Measure(sim, probe).GainMarginDb);
    }

    /// <summary>
    /// More feedback is less stable, which is the trade the whole subject is about — but for a
    /// one-pole loop it costs bandwidth rather than margin. A follower has the most loop gain and
    /// the highest crossover of any configuration, and still ninety degrees.
    /// </summary>
    [Fact]
    public void MoreFeedbackMeansMoreLoopGainAndAHigherCrossover()
    {
        StabilityResult At(double rf, double rg)
        {
            var (sim, probe, _, _) = Loop(rf, rg);
            return Measure(sim, probe);
        }

        var gainTen = At(9e3, 1e3);
        var gainHundred = At(99e3, 1e3);

        Assert.True(gainTen.LowFrequencyDecibels > gainHundred.LowFrequencyDecibels,
            "a gain of ten should have more loop gain than a gain of a hundred");

        Assert.True(gainTen.CrossoverHz > gainHundred.CrossoverHz,
            "and a higher crossover with it");
    }

    // ---- the case people actually hit --------------------------------------

    /// <summary>
    /// A capacitive load is the classic way to turn a comfortable loop into a ringing one: it
    /// works against the amplifier's output resistance to make a second pole, and a second pole
    /// inside the loop is what eats phase margin. This is the measurement somebody reaches for
    /// when a follower driving a cable starts to sing.
    /// </summary>
    [Fact]
    public void ACapacitiveLoadEatsThePhaseMargin()
    {
        var bare = Loop(9e3, 1e3);
        var clean = Measure(bare.Sim, bare.Probe);

        var (sim, probe, _, _) = Loop(9e3, 1e3, loadFarads: 1e-6);
        var loaded = Measure(sim, probe);

        Assert.NotNull(clean.PhaseMarginDegrees);
        Assert.NotNull(loaded.PhaseMarginDegrees);

        Assert.True(loaded.PhaseMarginDegrees < clean.PhaseMarginDegrees - 20,
            $"a microfarad should cost phase margin: {loaded.PhaseMarginDegrees:0.#}° " +
            $"against {clean.PhaseMarginDegrees:0.#}°");
    }

    /// <summary>
    /// Enough capacitance and the margin goes altogether. The verdict says so in words rather
    /// than leaving somebody to read a number off a plot.
    /// </summary>
    [Fact]
    public void EnoughCapacitanceMakesItRingAndTheVerdictSaysSo()
    {
        var (sim, probe, _, _) = Loop(9e3, 1e3, loadFarads: 100e-6);

        var result = Measure(sim, probe, stopHz: 1e8);

        Assert.NotNull(result.PhaseMarginDegrees);
        Assert.True(result.PhaseMarginDegrees < 45,
            $"a hundred microfarads should leave it ringing, not {result.PhaseMarginDegrees:0.#}°");

        Assert.DoesNotContain("Comfortable", result.Verdict);
    }

    // ---- the probe costs nothing -------------------------------------------

    /// <summary>
    /// A loop probe is a piece of wire everywhere except in this analysis. Having one in a circuit
    /// must not change any answer the circuit would otherwise give, or it would be a measurement
    /// that altered what it measured.
    /// </summary>
    [Fact]
    public void AProbeInTheLoopChangesNothingAboutTheCircuit()
    {
        var (withProbe, probe, _, _) = Loop(9e3, 1e3);

        // The same amplifier, wired without the break.
        var circuit = new Circuit();

        var positive = circuit.Add(new DcVoltageSource(15.0));
        var negative = circuit.Add(new DcVoltageSource(-15.0));
        var drive = circuit.Add(new DcVoltageSource(0.1) { Name = "VIN" });

        var amp = circuit.Add(new OperationalAmplifier { Model = OpAmpModel.Lm741 });
        var feedback = circuit.Add(new Resistor(9e3));
        var gain = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(positive.Negative, ground.Pin);
        circuit.Connect(negative.Positive, ground.Pin);
        circuit.Connect(amp.PositiveSupply, positive.Positive);
        circuit.Connect(amp.NegativeSupply, negative.Negative);
        circuit.Connect(drive.Negative, ground.Pin);
        circuit.Connect(drive.Positive, amp.NonInverting);
        circuit.Connect(amp.Output, feedback.A);
        circuit.Connect(feedback.B, gain.A);
        circuit.Connect(gain.B, ground.Pin);
        circuit.Connect(gain.A, amp.Inverting);

        var plain = new CircuitSimulator(circuit);
        plain.Reset();
        plain.SolveOperatingPoint();

        // The probed one, driven the same way.
        var probedAmp = withProbe.Circuit.Components.OfType<OperationalAmplifier>().Single();
        var probedDrive = withProbe.Circuit.Add(new DcVoltageSource(0.1) { Name = "VIN" });
        var probedGround = withProbe.Circuit.Components.OfType<Ground>().First();

        withProbe.Circuit.Wires.Remove(withProbe.Circuit.Wires.Single(w =>
            w.SourceTerminal == probedAmp.NonInverting || w.TargetTerminal == probedAmp.NonInverting));

        withProbe.Circuit.Connect(probedDrive.Negative, probedGround.Pin);
        withProbe.Circuit.Connect(probedDrive.Positive, probedAmp.NonInverting);

        var rebuilt = new CircuitSimulator(withProbe.Circuit);
        rebuilt.Reset();
        rebuilt.SolveOperatingPoint();

        // Gain of ten either way, to the last millivolt.
        Assert.Equal(
            plain.NodeVoltage(amp.Output), rebuilt.NodeVoltage(probedAmp.Output), 1e-6);

        // And no volts across the probe, which is what makes it a wire.
        Assert.Equal(
            rebuilt.NodeVoltage(probe.From), rebuilt.NodeVoltage(probe.To), 1e-9);
    }

    /// <summary>The injection is put back afterwards, so the next analysis sees a wire again.</summary>
    [Fact]
    public void TheInjectionIsPutBackAfterwards()
    {
        var (sim, probe, _, _) = Loop(9e3, 1e3);

        Measure(sim, probe);

        Assert.Equal(0.0, probe.Injection, 12);
    }

    /// <summary>
    /// A sweep that stops before the crossover cannot report a margin, and says so rather than
    /// reporting the last point it happened to reach.
    /// </summary>
    [Fact]
    public void ASweepThatNeverReachesCrossoverSaysSo()
    {
        var (sim, probe, _, _) = Loop(9e3, 1e3);

        var result = Measure(sim, probe, startHz: 0.1, stopHz: 100);

        Assert.Null(result.CrossoverHz);
        Assert.Null(result.PhaseMarginDegrees);
        Assert.Contains("never reaches one", result.Verdict);
    }

    /// <summary>
    /// A stability sweep is the only thing driving the circuit while it runs.
    /// <para>
    /// A loop gain is a ratio either side of the break, so another source pushing the circuit at
    /// the same time adds its own response to both ends and the ratio is not a loop gain at all.
    /// The failure is silent — the numbers stay plausible — which is how it survived: the guide's
    /// own illustration of this analysis read 6 dB of loop gain for a follower that has 106, and
    /// nothing said so until somebody noticed a follower cannot have 6 dB.
    /// </para>
    /// </summary>
    [Fact]
    public void AnotherSourceDrivingTheCircuitDoesNotChangeTheAnswer()
    {
        var quiet = Loop(9e3, 1e3);
        var expected = Measure(quiet.Sim, quiet.Probe);

        // The same circuit with a generator hanging off the input, as any real one would have.
        var (sim, probe, _, _) = Loop(9e3, 1e3);

        var amp = sim.Circuit.Components.OfType<OperationalAmplifier>().Single();
        var ground = sim.Circuit.Components.OfType<Ground>().First();

        var generator = sim.Circuit.Add(new FunctionGenerator(Waveform.Sine, 1e3, 1.0));

        Assert.NotEqual(0, generator.AcMagnitude);

        sim.Circuit.Wires.Remove(sim.Circuit.Wires.Single(w =>
            w.SourceTerminal == amp.NonInverting || w.TargetTerminal == amp.NonInverting));

        sim.Circuit.Connect(generator.Return, ground.Pin);
        sim.Circuit.Connect(generator.Output, amp.NonInverting);

        var rebuilt = new CircuitSimulator(sim.Circuit);
        rebuilt.Reset();
        rebuilt.SolveOperatingPoint();

        var measured = Measure(rebuilt, probe);

        Assert.NotNull(expected.LowFrequencyDecibels);
        Assert.NotNull(measured.LowFrequencyDecibels);

        Assert.Equal(expected.LowFrequencyDecibels.Value, measured.LowFrequencyDecibels.Value, 0.5);
        Assert.Equal(expected.PhaseMarginDegrees!.Value, measured.PhaseMarginDegrees!.Value, 2.0);

        // And the generator is left driving again afterwards, since the circuit still needs it.
        Assert.NotEqual(0, generator.AcMagnitude);
    }
}
