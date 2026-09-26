using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;
using Cirq.Engine.Tests.Support;

namespace Cirq.Engine.Tests;

/// <summary>
/// The adaptive step controller: shorter steps where the waveform bends, longer where it does not,
/// and landing on the instant a part switches rather than somewhere past it.
/// <para>
/// Every test here holds the controller to an answer that is known independently — an exponential,
/// a sine, an oscillator's arithmetic period — because "it ran faster" is not a result. The one
/// thing a step controller must never do is buy its speed with the answer.
/// </para>
/// </summary>
public class AdaptiveStepTests
{
    private static SimulationSettings Adaptive(double ceiling = 1e-4, double tolerance = 1e-3) => new()
    {
        AdaptiveTimeStep = true,
        TimeStep = 1e-7,
        MaxTimeStep = ceiling,
        MinTimeStep = 1e-12,
        StepErrorTolerance = tolerance,
        UseInitialConditions = true,
    };

    private static SimulationSettings Fixed(double step) => new()
    {
        TimeStep = step,
        MaxTimeStep = step,
        MinTimeStep = 1e-12,
        UseInitialConditions = true,
    };

    /// <summary>Every step the run took, in order.</summary>
    private static List<double> StepsOf(CircuitSimulator sim, double duration)
    {
        List<double> steps = [];
        var end = sim.Time + duration;

        while (sim.Time < end - sim.Settings.MinTimeStep)
            steps.Add(sim.Step(end - sim.Time));

        return steps;
    }

    [Fact]
    public void ItIsOffUnlessAskedFor()
    {
        Assert.False(new SimulationSettings().AdaptiveTimeStep);

        var (circuit, output, _, _, _) = CircuitFactory.RcLowPass(1e3, 1e-6, 5.0);
        var sim = new CircuitSimulator(circuit, Fixed(1e-6));

        sim.Reset();
        sim.SolveOperatingPoint();

        // A fixed run takes the step it was told to, every time, and reports no error estimate
        // because none was measured.
        var steps = StepsOf(sim, 1e-4);

        Assert.All(steps, s => Assert.Equal(1e-6, s, 1e-15));
        Assert.Equal(0, sim.LastStepError);
        Assert.Equal(0, sim.RejectedSteps);
        Assert.True(sim.NodeVoltage(output) > 0);
    }

    /// <summary>
    /// An RC charging curve is steepest at the start and flat at the end, so the steps should be
    /// short at the start and long at the end — which is the whole idea, stated as an assertion.
    /// </summary>
    [Fact]
    public void TheStepFollowsTheCurvature()
    {
        var (circuit, output, _, _, _) = CircuitFactory.RcLowPass(1e3, 1e-6, 5.0);
        var sim = new CircuitSimulator(circuit, Adaptive());

        sim.Reset();
        sim.SolveOperatingPoint();

        var steps = StepsOf(sim, 5e-3);

        var early = steps.Take(20).Average();
        var late = steps.TakeLast(20).Average();

        Assert.True(late > early * 10,
            $"the step should have grown as the curve flattened: {early:g3} s to {late:g3} s");

        // And the answer is still the exponential. Five time constants in, which is 4.9663 V and
        // not 5 — the point of checking against the formula rather than against "near enough".
        Assert.Equal(5.0 * (1 - Math.Exp(-5)), sim.NodeVoltage(output), 1e-3);
    }

    /// <summary>
    /// The accuracy is the point. A tolerance ten times looser has to cost accuracy and buy steps,
    /// or the knob is not connected to anything.
    /// </summary>
    [Fact]
    public void TheToleranceDecidesBothTheStepsAndTheError()
    {
        static (int Steps, double Error) Charge(double tolerance)
        {
            var (circuit, output, _, resistor, capacitor) = CircuitFactory.RcLowPass(1e3, 1e-6, 5.0);
            var sim = new CircuitSimulator(circuit, Adaptive(tolerance: tolerance));

            sim.Reset();
            sim.SolveOperatingPoint();

            var tau = resistor.Resistance * capacitor.Capacitance;
            var worst = 0.0;
            var steps = 0;
            var end = 3 * tau;

            while (sim.Time < end)
            {
                sim.Step(end - sim.Time);
                steps++;

                var exact = 5.0 * (1 - Math.Exp(-sim.Time / tau));
                worst = Math.Max(worst, Math.Abs(sim.NodeVoltage(output) - exact));
            }

            return (steps, worst);
        }

        var tight = Charge(1e-4);
        var loose = Charge(1e-2);

        Assert.True(loose.Steps < tight.Steps,
            $"a looser tolerance should take fewer steps: {loose.Steps} against {tight.Steps}");

        Assert.True(loose.Error > tight.Error,
            $"and should cost accuracy: {loose.Error:g3} V against {tight.Error:g3} V");

        // Both are still recognisably the exponential — this is a step controller, not a gamble.
        Assert.True(tight.Error < 1e-3, $"the tight run was out by {tight.Error:g3} V");
        Assert.True(loose.Error < 0.1, $"the loose run was out by {loose.Error:g3} V");
    }

    /// <summary>
    /// A sine has curvature everywhere, so there is nothing to be saved and the controller should
    /// settle on a step that resolves it rather than growing until it does not.
    /// </summary>
    [Fact]
    public void ASineIsResolvedRatherThanOutrun()
    {
        var circuit = new Circuit();
        var source = circuit.Add(new FunctionGenerator(Waveform.Sine, 1e3, 4.0));
        var resistor = circuit.Add(new Resistor(1e3));
        var capacitor = circuit.Add(new Capacitor(1e-9) { InitialVoltage = 0 });
        var ground = circuit.Add(new Ground());

        circuit.Connect(source.B, ground.Pin);
        circuit.Connect(source.A, resistor.A);
        circuit.Connect(resistor.B, capacitor.A);
        circuit.Connect(capacitor.B, ground.Pin);

        var sim = new CircuitSimulator(circuit, Adaptive(ceiling: 1e-3));
        sim.Reset();
        sim.SolveOperatingPoint();

        var steps = StepsOf(sim, 5e-3);

        // Five cycles at a kilohertz. Well over forty points a cycle, and not the ceiling either:
        // the controller found a step the waveform justifies.
        Assert.True(steps.Count > 5 * 40, $"only {steps.Count} steps across five cycles");
        Assert.True(steps.Max() < 1e-3, "the controller grew all the way to the ceiling on a sine");
    }

    [Fact]
    public void TheBoundsAreRespected()
    {
        var (circuit, _, _, _, _) = CircuitFactory.RcLowPass(1e3, 1e-6, 5.0);
        var settings = Adaptive();
        settings.MinTimeStep = 1e-9;
        settings.MaxTimeStep = 1e-5;

        var sim = new CircuitSimulator(circuit, settings);
        sim.Reset();
        sim.SolveOperatingPoint();

        var steps = StepsOf(sim, 1e-3);

        Assert.All(steps, s => Assert.InRange(s, 1e-9, 1e-5 + 1e-15));
    }

    /// <summary>
    /// The failure that sank the first attempt at this, kept as a test. A logic node steps from zero
    /// to five volts between two time points because that is what logic does, and a controller that
    /// read that as a truncation error would grind the step down to nothing and never come back.
    /// </summary>
    [Fact]
    public void LogicDoesNotDriveTheStepIntoTheGround()
    {
        var circuit = new Circuit();

        var clock = circuit.Add(new ClockSource(1e5) { PropagationDelay = 0 });
        var first = circuit.Add(new LogicGate(GateFunction.Not, 1) { PropagationDelay = 1e-8 });
        var second = circuit.Add(new LogicGate(GateFunction.Not, 1) { PropagationDelay = 1e-8 });
        circuit.Add(new Ground());

        circuit.Connect(clock.Out, first.InputTerminals[0]);
        circuit.Connect(first.OutputTerminals[0], second.InputTerminals[0]);

        var settings = Adaptive(ceiling: 1e-5);
        settings.MinTimeStep = 1e-12;

        var sim = new CircuitSimulator(circuit, settings);
        sim.Reset();
        sim.SolveOperatingPoint();

        var steps = StepsOf(sim, 2e-4);

        // Twenty clock periods, and there is nothing in this circuit that integrates — so the
        // controller has nothing to measure and gets out of the way: the steps are the ones the
        // logic asks for, a long one between events and a short one to land on each. A hundred and
        // eighteen of them, not the two hundred million a step at the floor would need.
        Assert.True(steps.Count < 500, $"{steps.Count} steps for twenty clock cycles");
        Assert.True(steps.Max() >= 1e-6, "the step never recovered after an edge");

        // The clock's own edges: half a period is 5 us, and that is the long step between them.
        Assert.Equal(5e-6, steps.Max(), 1e-9);
    }
}
