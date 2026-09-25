using Cirq.Components.Digital;
using Cirq.Components.Serialization;
using Cirq.Components.Sources;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Verification;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// A real gate's propagation delay, measured off the traces.
/// <para>
/// This is the number on the front of every logic datasheet, and until now a library shipping
/// twenty-one 74xx parts, fifteen 40xx parts and an event scheduler had no way to measure it. The
/// figure is one the part was <i>told</i>, so this is a round trip: set a delay on the device,
/// run the circuit, and measure it back off the waveforms without asking the device anything.
/// </para>
/// </summary>
public class GateTimingTests
{
    /// <summary>A clock into an inverter, probed either side of it.</summary>
    private static (CircuitSimulator Sim, Circuit Circuit) Inverter(double delay)
    {
        var circuit = new Circuit();

        var clock = circuit.Add(new ClockSource(1e6) { Name = "CLK1", PropagationDelay = 0 });
        var gate = circuit.Add(new LogicGate(GateFunction.Not, 1) { Name = "U1", PropagationDelay = delay });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Wires.Add(new WireSegment
        {
            SourceTerminal = clock.Out,
            TargetTerminal = gate.InputTerminals[0],
        });

        circuit.Probes.Add(new SignalProbe { Label = "in", TargetTerminal = clock.Out });
        circuit.Probes.Add(new SignalProbe { Label = "out", TargetTerminal = gate.OutputTerminals[0] });

        circuit.Add(ground);

        var sim = new CircuitSimulator(circuit, new SimulationSettings
        {
            TimeStep = 1e-9,
            MaxTimeStep = 5e-9,
            ProbeSampleInterval = 1e-9,
        });

        sim.Reset();
        sim.SolveOperatingPoint();

        return (sim, circuit);
    }

    private static IReadOnlyList<Cirq.Core.Primitives.DataPoint> Trace(Circuit circuit, string label) =>
        circuit.Probes.Single(p => p.Label == label).HistoryBuffer.ToArray();

    /// <summary>
    /// The delay a gate was given is the delay measured between its input and its output.
    /// </summary>
    [Theory]
    [InlineData(10e-9)]
    [InlineData(25e-9)]
    [InlineData(50e-9)]
    public void AGatesDelayMeasuresBackAsWhatItWasGiven(double delay)
    {
        var (sim, circuit) = Inverter(delay);

        while (sim.Time < 5e-6) sim.Step();

        var measured = TraceTiming.PropagationDelay(Trace(circuit, "in"), Trace(circuit, "out"));

        Assert.NotNull(measured);

        // Within a couple of sample intervals: the probe records every nanosecond, and the edge
        // itself is not instantaneous once the output's series resistance has driven the node.
        Assert.Equal(delay, measured.Value, 4e-9);
    }

    /// <summary>
    /// And it reaches a requirement, which is the point: "this gate has to respond inside forty
    /// nanoseconds" is a sentence a circuit can now be held to.
    /// </summary>
    [Fact]
    public void ADelayRequirementHoldsARealGateToIt()
    {
        var (sim, circuit) = Inverter(25e-9);

        while (sim.Time < 5e-6) sim.Step();

        var spec = new DesignSpec
        {
            Name = "Gate responds",
            Trace = "out",
            Against = "in",
            Quantity = SpecQuantity.PropagationDelay,
            Comparison = SpecComparison.AtMost,
            Limit = 40e-9,
            Unit = "s",
        };

        Func<string, IReadOnlyList<Cirq.Core.Primitives.DataPoint>?> samples =
            label => circuit.Probes.FirstOrDefault(p => p.Label == label)?.HistoryBuffer.ToArray();

        Assert.True(SpecCheck.EvaluateFrom(spec, samples).Passed);

        spec.Limit = 10e-9;

        Assert.False(SpecCheck.EvaluateFrom(spec, samples).Passed);
    }

    /// <summary>
    /// The second trace survives a save. A requirement that lost half of what it measures on the
    /// way to disk would come back saying it needs a trace nobody removed.
    /// </summary>
    [Fact]
    public void TheSecondTraceSurvivesARoundTrip()
    {
        var circuit = new Circuit();

        circuit.Add(new Cirq.Components.Passive.Resistor(1e3) { Name = "R1" });

        circuit.Specs.Add(new DesignSpec
        {
            Name = "Gate responds",
            Trace = "out",
            Against = "in",
            Quantity = SpecQuantity.PropagationDelay,
            Limit = 40e-9,
            Unit = "s",
        });

        var back = CircuitSerializer.FromJson(CircuitSerializer.ToJson(circuit)).Circuit;

        var spec = Assert.Single(back.Specs);

        Assert.Equal("out", spec.Trace);
        Assert.Equal("in", spec.Against);
        Assert.Equal(SpecQuantity.PropagationDelay, spec.Quantity);
    }

    /// <summary>
    /// A requirement that does not need a second trace writes none, so an ordinary file is what it
    /// always was.
    /// </summary>
    [Fact]
    public void AnOrdinaryRequirementWritesNoSecondTrace()
    {
        var circuit = new Circuit();

        circuit.Add(new Cirq.Components.Passive.Resistor(1e3) { Name = "R1" });
        circuit.Specs.Add(new DesignSpec { Name = "Ripple", Trace = "rail" });

        Assert.DoesNotContain("\"against\"", CircuitSerializer.ToJson(circuit),
            StringComparison.OrdinalIgnoreCase);
    }
}
