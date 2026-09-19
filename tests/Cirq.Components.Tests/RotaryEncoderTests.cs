using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class RotaryEncoderTests
{
    /// <summary>The usual wiring: common to ground, both contacts pulled up.</summary>
    private static (CircuitSimulator Sim, RotaryEncoder Encoder, Resistor PullA, Resistor PullB) Rig()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var encoder = circuit.Add(new RotaryEncoder());
        var pullA = circuit.Add(new Resistor(10e3));
        var pullB = circuit.Add(new Resistor(10e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(encoder.Common, gnd.Pin);
        circuit.Connect(rail.Positive, pullA.A);
        circuit.Connect(pullA.B, encoder.OutputA);
        circuit.Connect(rail.Positive, pullB.A);
        circuit.Connect(pullB.B, encoder.OutputB);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, encoder, pullA, pullB);
    }

    /// <summary>Samples both lines across one detent of rotation.</summary>
    private static List<(bool A, bool B)> Sample(
        CircuitSimulator sim, RotaryEncoder encoder, Resistor pullA, Resistor pullB, double seconds)
    {
        var trace = new List<(bool, bool)>();
        var steps = (int)(seconds / 50e-6);

        for (var i = 0; i < steps; i++)
        {
            sim.Run(50e-6);
            trace.Add((sim.NodeVoltage(pullA.B) < 2.5, sim.NodeVoltage(pullB.B) < 2.5));
        }

        return trace;
    }

    [Fact]
    public void AtRestBothContactsAreOpen()
    {
        var (sim, encoder, pullA, pullB) = Rig();

        sim.Run(5e-3);

        Assert.False(encoder.IsAClosed);
        Assert.False(encoder.IsBClosed);
        Assert.True(sim.NodeVoltage(pullA.B) > 4.9, "an open contact should leave the pull-up alone");
    }

    /// <summary>
    /// Turning it produces quadrature: both lines pulse, and they are a quarter of a cycle apart
    /// rather than together. That stagger is the only thing that carries direction.
    /// </summary>
    [Fact]
    public void TurningItProducesTwoStaggeredPulses()
    {
        var (sim, encoder, pullA, pullB) = Rig();
        encoder.Interact();

        var trace = Sample(sim, encoder, pullA, pullB, 30e-3);

        Assert.Contains(trace, s => s.A);
        Assert.Contains(trace, s => s.B);

        // They are not the same signal: there are samples where one is closed and the other is not.
        Assert.Contains(trace, s => s.A && !s.B);
        Assert.Contains(trace, s => !s.A && s.B);
    }

    /// <summary>
    /// Direction is which line moves first, and nothing else. Forwards, A leads; backwards, B.
    /// </summary>
    [Fact]
    public void WhichContactMovesFirstIsTheDirection()
    {
        static int FirstToClose(bool reverse)
        {
            var (sim, encoder, pullA, pullB) = Rig();
            encoder.Reverse = reverse;
            encoder.Interact();

            foreach (var (a, b) in Sample(sim, encoder, pullA, pullB, 30e-3))
            {
                if (a && !b) return 0;
                if (b && !a) return 1;
            }

            return -1;
        }

        Assert.Equal(0, FirstToClose(reverse: false));
        Assert.Equal(1, FirstToClose(reverse: true));
    }

    [Fact]
    public void TurningItCountsDetentsEitherWay()
    {
        var (_, encoder, _, _) = Rig();

        encoder.Interact();
        encoder.Interact();
        Assert.Equal(2, encoder.Detent);

        encoder.Reverse = true;
        encoder.Interact();
        encoder.Interact();
        encoder.Interact();
        Assert.Equal(-1, encoder.Detent);
    }

    /// <summary>
    /// The reason encoder code is hard: every edge bounces, and a counter that treats each
    /// transition as a step reads far more of them than the shaft actually made.
    /// </summary>
    [Fact]
    public void TheContactsBounceOnEveryEdge()
    {
        var (sim, encoder, pullA, pullB) = Rig();
        encoder.Interact();

        var trace = Sample(sim, encoder, pullA, pullB, 30e-3);

        var transitions = 0;
        for (var i = 1; i < trace.Count; i++)
            if (trace[i].A != trace[i - 1].A) transitions++;

        // One detent should be two clean edges on A. Bounce makes it many more.
        Assert.True(transitions > 4,
            $"only {transitions} edges on A — the contacts are not bouncing, and they should be");
    }

    /// <summary>Turning the bounce off gives the clean signal a debounced circuit would see.</summary>
    [Fact]
    public void WithoutBounceTheEdgesAreClean()
    {
        var (sim, encoder, pullA, pullB) = Rig();
        encoder.BounceDuration = 0;
        encoder.Interact();

        var trace = Sample(sim, encoder, pullA, pullB, 30e-3);

        var transitions = 0;
        for (var i = 1; i < trace.Count; i++)
            if (trace[i].A != trace[i - 1].A) transitions++;

        Assert.Equal(2, transitions);
    }
}
