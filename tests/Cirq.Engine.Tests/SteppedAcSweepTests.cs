using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// A Bode plot with a family of curves on it rather than one.
/// <para>
/// The arithmetic is an RC low-pass, whose corner is <c>1/(2πRC)</c> and nothing else. Stepping R
/// therefore moves the corner by exactly the ratio R was stepped by, which is a prediction this has
/// no way to satisfy by accident.
/// </para>
/// </summary>
public class SteppedAcSweepTests
{
    private static (CircuitSimulator Sim, Resistor R, Capacitor C) LowPass(
        double ohms = 1e3, double farads = 1e-7)
    {
        var circuit = new Circuit();

        var source = circuit.Add(new FunctionGenerator { Name = "FG1", AcMagnitude = 1.0 });
        var r = circuit.Add(new Resistor(ohms) { Name = "R1" });
        var c = circuit.Add(new Capacitor(farads) { Name = "C1" });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Wires.Add(new WireSegment { SourceTerminal = source.Output, TargetTerminal = r.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = r.B, TargetTerminal = c.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = c.B, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = source.Return, TargetTerminal = ground.Pin });

        circuit.Probes.Add(new SignalProbe { Label = "out", TargetTerminal = c.A });

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        return (sim, r, c);
    }

    /// <summary>One pass per value, in the order they were asked for.</summary>
    [Fact]
    public void ThereIsOnePassPerValue()
    {
        var (sim, r, _) = LowPass();

        var result = new SteppedAcSweep(sim).Run(new AcStepRequest(
            new AcSweepRequest(10, 1e6, 20),
            new SweepTarget(r, nameof(Resistor.Resistance), 1e3, 4e3, 4)));

        Assert.Equal(4, result.Runs.Count);
        Assert.Equal([1e3, 2e3, 3e3, 4e3], result.Runs.Select(p => p.Value));
        Assert.All(result.Runs, p => Assert.True(p.Succeeded));
    }

    /// <summary>
    /// Doubling the resistance halves the corner, because the corner is 1/(2πRC) and there is
    /// nothing else in it.
    /// </summary>
    [Fact]
    public void SteppingTheResistanceMovesTheCornerByTheSameRatio()
    {
        var (sim, r, _) = LowPass(farads: 1e-7);

        var result = new SteppedAcSweep(sim).Run(new AcStepRequest(
            new AcSweepRequest(10, 1e6, 40),
            new SweepTarget(r, nameof(Resistor.Resistance), 1e3, 2e3, 2)));

        var corners = result.Corners("out").ToList();

        Assert.Equal(2, corners.Count);

        foreach (var (ohms, corner) in corners.Zip((double[])[1e3, 2e3], (c, o) => (o, c.Corner)))
        {
            Assert.NotNull(corner);
            Assert.Equal(1.0 / (2 * Math.PI * ohms * 1e-7), corner.Value, corner.Value * 0.05);
        }
    }

    /// <summary>
    /// And the same for the capacitor, which is the other half of the same product — a check that
    /// the stepping is reaching the part it was pointed at rather than any part.
    /// </summary>
    [Fact]
    public void SteppingTheCapacitanceMovesItToo()
    {
        var (sim, _, c) = LowPass(ohms: 1e3);

        var result = new SteppedAcSweep(sim).Run(new AcStepRequest(
            new AcSweepRequest(10, 1e6, 40),
            new SweepTarget(c, nameof(Capacitor.Capacitance), 1e-7, 4e-7, 2)));

        var corners = result.Corners("out").Select(p => p.Corner!.Value).ToList();

        // Four times the capacitance is a quarter of the corner.
        Assert.Equal(4.0, corners[0] / corners[1], 0.2);
    }

    /// <summary>
    /// The parameter goes back to what it was. A sweep that left the circuit holding its last value
    /// would silently edit the document somebody is working on.
    /// </summary>
    [Fact]
    public void TheParameterIsPutBack()
    {
        var (sim, r, _) = LowPass(ohms: 2200);

        new SteppedAcSweep(sim).Run(new AcStepRequest(
            new AcSweepRequest(10, 1e5, 10),
            new SweepTarget(r, nameof(Resistor.Resistance), 1e3, 1e4, 3)));

        Assert.Equal(2200, r.Resistance, 1e-9);
    }

    /// <summary>
    /// A value the circuit will not bias at loses that pass and no other. The family is more useful
    /// with a gap in it than not at all.
    /// </summary>
    [Fact]
    public void OneFailedPassDoesNotTakeTheOthersWithIt()
    {
        var (sim, r, _) = LowPass();

        // A resistance of zero is not a resistance; whatever the solver makes of it, the passes
        // either side of it are ordinary and must still be there.
        var result = new SteppedAcSweep(sim).Run(new AcStepRequest(
            new AcSweepRequest(10, 1e5, 10),
            new SweepTarget(r, nameof(Resistor.Resistance), 0.0, 2e3, 3)));

        Assert.Equal(3, result.Runs.Count);
        Assert.Contains(result.Runs, p => p.Succeeded);
    }

    /// <summary>
    /// Each pass biases afresh, and the proof is a response that only the bias point can explain.
    /// <para>
    /// A diode's small-signal resistance is <c>nVt/Id</c> — inversely proportional to the current
    /// through it. Put a capacitor across it and the corner is <c>1/(2π·rd·C)</c>, so a hundredfold
    /// change in the bias current is a hundredfold change in the corner frequency. Nothing about
    /// the resistor itself does that: it is the operating point, and a family swept about one stale
    /// bias point would put all four curves on top of each other.
    /// </para>
    /// <para>
    /// The obvious version of this test does not work, and it is worth saying why. A plain divider
    /// of resistor against diode barely changes gain at all when the resistor is stepped, because
    /// <c>rd</c> rises in very nearly the same proportion as <c>R</c> and the ratio between them
    /// stays put. That is a real and slightly surprising property of the circuit, and it makes it
    /// useless as a test of anything.
    /// </para>
    /// </summary>
    [Fact]
    public void EachPassBiasesAtItsOwnValue()
    {
        var circuit = new Circuit();

        var source = circuit.Add(new DcVoltageSource(5.0) { Name = "V1", AcMagnitude = 1.0 });
        var r = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var d = circuit.Add(new Cirq.Components.Nonlinear.Diode { Name = "D1" });
        var c = circuit.Add(new Capacitor(1e-6) { Name = "C1" });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Wires.Add(new WireSegment { SourceTerminal = source.Positive, TargetTerminal = r.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = r.B, TargetTerminal = d.Anode });
        circuit.Wires.Add(new WireSegment { SourceTerminal = d.Cathode, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = d.Anode, TargetTerminal = c.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = c.B, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = source.Negative, TargetTerminal = ground.Pin });

        circuit.Probes.Add(new SignalProbe { Label = "out", TargetTerminal = d.Anode });

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        var result = new SteppedAcSweep(sim).Run(new AcStepRequest(
            new AcSweepRequest(10, 1e6, 30),
            new SweepTarget(r, nameof(Resistor.Resistance), 1e3, 1e5, 2)));

        var corners = result.Corners("out").Select(p => p.Corner).ToList();

        Assert.Equal(2, corners.Count);
        Assert.All(corners, corner => Assert.NotNull(corner));

        // A hundred times the resistor is a hundredth of the bias current, so a hundred times the
        // dynamic resistance and a hundredth of the corner. Within a factor of two, because the
        // diode's drop moves a little as well and that is not part of the ratio.
        var ratio = corners[0]!.Value / corners[1]!.Value;

        Assert.True(ratio is > 50 and < 200,
            $"the corner should move about a hundredfold with the bias, not {ratio:0.#}×");
    }

    /// <summary>A circuit with no probes has nothing to sweep, and says so rather than throwing.</summary>
    [Fact]
    public void NoProbesMeansNoFamily()
    {
        var (sim, r, _) = LowPass();

        sim.Circuit.Probes.Clear();
        sim.ResolveProbes();

        var result = new SteppedAcSweep(sim).Run(new AcStepRequest(
            new AcSweepRequest(), new SweepTarget(r, nameof(Resistor.Resistance), 1e3, 2e3, 2)));

        Assert.Empty(result.Runs);
    }

    /// <summary>Stepping something that is not a writable number is refused, by name.</summary>
    [Fact]
    public void SteppingSomethingThatIsNotThereIsRefused()
    {
        var (sim, r, _) = LowPass();

        var thrown = Assert.Throws<ArgumentException>(() =>
            new SteppedAcSweep(sim).Run(new AcStepRequest(
                new AcSweepRequest(), new SweepTarget(r, "NoSuchProperty", 1, 2, 2))));

        Assert.Contains("NoSuchProperty", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("R1", thrown.Message, StringComparison.Ordinal);
    }
}
