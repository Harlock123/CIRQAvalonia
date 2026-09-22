using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// Running the same transient several times with one value changed — the thing people actually
/// want most often, and the one question a DC sweep cannot answer.
/// <para>
/// A DC sweep records an operating point at each value: where the circuit ends up. This records
/// how it <b>gets</b> there, which is what "try three capacitor values and watch the ringing"
/// means. Every answer here is checked against the circuit's own time constant or its damping,
/// never against a previous run.
/// </para>
/// </summary>
public class TransientStepTests
{
    /// <summary>A step into an RC, whose response is a textbook exponential.</summary>
    private static (CircuitSimulator Sim, Resistor R, Capacitor C) RcRig()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(1.0));
        var resistor = circuit.Add(new Resistor(1e3));

        // Starting discharged, which is what makes this a step response rather than a circuit
        // that was already settled before the run began. A bias point is the DC steady state, so
        // without an initial condition the capacitor is at one volt before the first time point
        // and there is nothing transient about it.
        var capacitor = circuit.Add(new Capacitor(1e-6) { InitialVoltage = 0.0 });
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, resistor.A);
        circuit.Connect(resistor.B, capacitor.A);
        circuit.Connect(capacitor.B, ground.Pin);

        circuit.Probes.Add(new Cirq.Core.Probing.SignalProbe("Vc", capacitor.A, default));

        var sim = new CircuitSimulator(circuit);
        sim.Settings.TimeStep = 1e-6;
        sim.Settings.MaxTimeStep = 1e-5;
        sim.Settings.UseInitialConditions = true;

        return (sim, resistor, capacitor);
    }

    private static double ValueAt(TransientRun run, double time)
    {
        var samples = run.Traces[0].Samples;
        var best = samples[0];

        foreach (var sample in samples)
            if (Math.Abs(sample.Time - time) < Math.Abs(best.Time - time)) best = sample;

        return best.Value;
    }

    // ---- the family --------------------------------------------------------

    [Fact]
    public void OneRunPerValue()
    {
        var (sim, resistor, _) = RcRig();

        var result = new TransientStep(sim).Run(new TransientStepRequest(
            new SweepTarget(resistor, nameof(Resistor.Resistance), 1e3, 4e3, 4), 5e-3));

        Assert.Equal(4, result.Runs.Count);
        Assert.Equal(4, result.Succeeded);
        Assert.Equal([1e3, 2e3, 3e3, 4e3], result.Runs.Select(r => r.Value));
        Assert.All(result.Runs, r => Assert.NotEmpty(r.Traces[0].Samples));
    }

    /// <summary>
    /// The measurement itself: at one time constant an RC has reached 63.2 % of the way, so a
    /// family stepping R should cross 0.632 V at 1 ms, 2 ms, 3 ms and 4 ms respectively. That is
    /// the whole point — the curves differ in <i>when</i>, which no operating point can show.
    /// </summary>
    [Fact]
    public void EachRunHasTheTimeConstantItsOwnValueGivesIt()
    {
        var (sim, resistor, capacitor) = RcRig();

        var result = new TransientStep(sim).Run(new TransientStepRequest(
            new SweepTarget(resistor, nameof(Resistor.Resistance), 1e3, 4e3, 4), 20e-3));

        foreach (var run in result.Runs)
        {
            var tau = run.Value * capacitor.Capacitance;

            Assert.Equal(1 - (1 / Math.E), ValueAt(run, tau), 0.01);

            // And five time constants in, it is there.
            Assert.Equal(1.0, ValueAt(run, 5 * tau), 0.01);
        }
    }

    /// <summary>
    /// Stepping the capacitor is the same story from the other side, and proves the parameter is
    /// genuinely being written rather than the resistor being special.
    /// </summary>
    [Fact]
    public void SteppingTheCapacitorChangesTheTimeConstantToo()
    {
        var (sim, resistor, capacitor) = RcRig();

        var result = new TransientStep(sim).Run(new TransientStepRequest(
            new SweepTarget(capacitor, nameof(Capacitor.Capacitance), 1e-6, 3e-6, 3), 30e-3));

        foreach (var run in result.Runs)
            Assert.Equal(1 - (1 / Math.E), ValueAt(run, resistor.Resistance * run.Value), 0.01);
    }

    /// <summary>
    /// Every pass starts from the same place. If one run's final state leaked into the next, the
    /// second curve would start at a volt rather than at zero — and every curve after the first
    /// would be a different experiment from the first.
    /// </summary>
    [Fact]
    public void EveryRunStartsFromTheSameConditions()
    {
        var (sim, resistor, _) = RcRig();

        var result = new TransientStep(sim).Run(new TransientStepRequest(
            new SweepTarget(resistor, nameof(Resistor.Resistance), 1e3, 3e3, 3), 20e-3));

        Assert.All(result.Runs, r =>
        {
            // The first sample is one sample interval in, not before the run started.
            Assert.InRange(r.Traces[0].Samples[0].Time, 0.0, 20e-3 / 100);
            Assert.Equal(0.0, r.Traces[0].Samples[0].Value, 0.02);
        });
    }

    // ---- putting things back -----------------------------------------------

    [Fact]
    public void TheParameterGoesBackWhereItWas()
    {
        var (sim, resistor, _) = RcRig();

        new TransientStep(sim).Run(new TransientStepRequest(
            new SweepTarget(resistor, nameof(Resistor.Resistance), 1e3, 9e3, 5), 2e-3));

        Assert.Equal(1e3, resistor.Resistance, 9);
    }

    [Fact]
    public void TheSamplingSettingsGoBackWhereTheyWere()
    {
        var (sim, resistor, _) = RcRig();

        sim.Settings.ProbeSampleInterval = 1.5e-5;
        sim.Settings.MaxTimeStep = 2.5e-5;

        new TransientStep(sim).Run(new TransientStepRequest(
            new SweepTarget(resistor, nameof(Resistor.Resistance), 1e3, 2e3, 2), 2e-3));

        Assert.Equal(1.5e-5, sim.Settings.ProbeSampleInterval, 12);
        Assert.Equal(2.5e-5, sim.Settings.MaxTimeStep, 12);
    }

    /// <summary>
    /// The circuit is left at its own bias point rather than at the last pass's final state, so
    /// the canvas is not showing the end of a sweep as though it were the state of things.
    /// <para>
    /// Checked against a bias point solved independently rather than against a number written
    /// here, so it stays true whatever the rig's initial conditions say.
    /// </para>
    /// </summary>
    [Fact]
    public void TheCircuitIsLeftAtItsOwnOperatingPoint()
    {
        var (fresh, _, freshCapacitor) = RcRig();
        fresh.Reset();
        fresh.SolveOperatingPoint();

        var expected = fresh.NodeVoltage(freshCapacitor.A);

        var (sim, resistor, capacitor) = RcRig();

        new TransientStep(sim).Run(new TransientStepRequest(
            new SweepTarget(resistor, nameof(Resistor.Resistance), 1e3, 2e3, 2), 20e-3));

        Assert.Equal(expected, sim.NodeVoltage(capacitor.A), 1e-6);
        Assert.Equal(0.0, sim.Time, 1e-9);
    }

    // ---- sampling ----------------------------------------------------------

    /// <summary>
    /// A probe keeps ten thousand points, so sampling finer than the run is long throws away its
    /// beginning — which on a step response is the only part anybody wanted. The interval is
    /// worked out from the duration unless it is stated.
    /// </summary>
    [Fact]
    public void ALongRunStillKeepsItsBeginning()
    {
        var (sim, resistor, _) = RcRig();

        // Sampled at the rig's own interval this would be a hundred thousand points, and the
        // first ninety thousand would be gone.
        var result = new TransientStep(sim).Run(new TransientStepRequest(
            new SweepTarget(resistor, nameof(Resistor.Resistance), 1e3, 2e3, 2), 1.0));

        foreach (var run in result.Runs)
        {
            Assert.Equal(0.0, run.Traces[0].Samples[0].Time, 1e-6);
            Assert.True(run.Traces[0].Samples[^1].Time > 0.9,
                $"the run should reach the end, not {run.Traces[0].Samples[^1].Time:0.###} s");
        }
    }

    [Fact]
    public void AStatedSampleIntervalIsUsed()
    {
        var (sim, resistor, _) = RcRig();

        var result = new TransientStep(sim).Run(new TransientStepRequest(
            new SweepTarget(resistor, nameof(Resistor.Resistance), 1e3, 2e3, 2), 10e-3, 1e-4));

        // Ten milliseconds at a tenth of a millisecond is about a hundred points.
        Assert.InRange(result.Runs[0].Traces[0].Samples.Count, 90, 110);
    }

    // ---- refusing and surviving --------------------------------------------

    [Fact]
    public void AParameterThatIsNotAWritableNumberIsRefused()
    {
        var (sim, resistor, _) = RcRig();

        Assert.Throws<ArgumentException>(() => new TransientStep(sim).Run(
            new TransientStepRequest(new SweepTarget(resistor, "NotAProperty", 1, 2, 2), 1e-3)));
    }

    [Fact]
    public void ARunWithNoDurationIsRefused()
    {
        var (sim, resistor, _) = RcRig();

        Assert.Throws<ArgumentException>(() => new TransientStep(sim).Run(
            new TransientStepRequest(
                new SweepTarget(resistor, nameof(Resistor.Resistance), 1, 2, 2), 0)));
    }

    [Fact]
    public void WithNoProbesThereIsNothingToRecord()
    {
        var (sim, resistor, _) = RcRig();
        sim.Circuit.Probes.Clear();

        var result = new TransientStep(sim).Run(new TransientStepRequest(
            new SweepTarget(resistor, nameof(Resistor.Resistance), 1e3, 2e3, 2), 1e-3));

        Assert.True(result.IsEmpty);
    }

    /// <summary>Every probe on the circuit is recorded, not just the first.</summary>
    [Fact]
    public void EveryProbeIsRecorded()
    {
        var (sim, resistor, _) = RcRig();

        sim.Circuit.Probes.Add(new Cirq.Core.Probing.SignalProbe(
            "Vin", resistor.A, default));

        sim.ResolveProbes();

        var result = new TransientStep(sim).Run(new TransientStepRequest(
            new SweepTarget(resistor, nameof(Resistor.Resistance), 1e3, 2e3, 2), 5e-3));

        Assert.All(result.Runs, r => Assert.Equal(2, r.Traces.Count));
        Assert.Equal(["Vc", "Vin"], result.Runs[0].Traces.Select(t => t.Label));
    }

    /// <summary>
    /// Each pass's samples are its own. A reference to the live probe buffer would be overwritten
    /// by the next pass, and every curve would end up showing the last one.
    /// </summary>
    [Fact]
    public void TheRunsDoNotShareTheirSamples()
    {
        var (sim, resistor, _) = RcRig();

        var result = new TransientStep(sim).Run(new TransientStepRequest(
            new SweepTarget(resistor, nameof(Resistor.Resistance), 1e3, 8e3, 3), 10e-3));

        // Different time constants, so at one millisecond the three are in different places.
        var early = result.Runs.Select(r => ValueAt(r, 1e-3)).ToList();

        Assert.True(early[0] > early[1] && early[1] > early[2],
            $"a bigger resistor should charge more slowly: {string.Join(", ", early.Select(v => v.ToString("0.###")))}");
    }
}
