using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// A source whose output is a formula.
/// <para>
/// Every test holds it against something known independently: a linear formula has to give exactly
/// what the linear part it imitates gives, a product has to produce the sum and difference
/// frequencies that multiplying two sines produces, and a square root has to be a square root. A
/// numerically linearised device that is <i>nearly</i> right is the failure worth catching, because
/// nothing about the waveform looks wrong.
/// </para>
/// </summary>
public class BehaviouralSourceTests
{
    private sealed record Rig(CircuitSimulator Sim, Circuit Circuit, BehaviouralSource Source, Terminal Out);

    /// <summary>
    /// A source driven from a DC input, with the behavioural source's output across a load. The
    /// input is a plain voltage source so the formula's variable can be set exactly.
    /// </summary>
    private static Rig Driven(string formula, double input, BehaviouralOutput output = BehaviouralOutput.Volts)
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(input) { Name = "V1" });
        var source = circuit.Add(new BehaviouralSource(formula) { Name = "B1", Output = output });
        var load = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, source.A);

        circuit.Connect(source.OutputPositive, load.A);
        circuit.Connect(load.B, ground.Pin);
        circuit.Connect(source.OutputNegative, ground.Pin);

        var sim = new CircuitSimulator(circuit);

        sim.Reset();
        sim.SolveOperatingPoint();

        return new Rig(sim, circuit, source, source.OutputPositive);
    }

    [Fact]
    public void ALinearFormulaIsExactlyTheLinearPartItImitates()
    {
        // The same thing twice: a VCVS of gain 3, and "= 3 * a".
        var behavioural = Driven("= 3 * a", 1.5);

        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(1.5));
        var amplifier = circuit.Add(new VoltageControlledVoltageSource(3.0));
        var load = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, amplifier.ControlPositive);
        circuit.Connect(amplifier.ControlNegative, ground.Pin);
        circuit.Connect(amplifier.OutputPositive, load.A);
        circuit.Connect(load.B, ground.Pin);
        circuit.Connect(amplifier.OutputNegative, ground.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        Assert.Equal(4.5, sim.NodeVoltage(amplifier.OutputPositive), 1e-9);
        Assert.Equal(4.5, behavioural.Sim.NodeVoltage(behavioural.Out), 1e-6);
    }

    [Theory]
    [InlineData("= a * a", 2.0, 4.0)]
    [InlineData("= sqrt(a)", 9.0, 3.0)]
    [InlineData("= abs(a)", -2.5, 2.5)]
    [InlineData("= exp(a)", 1.0, Math.E)]
    [InlineData("= 2.5 + a / 2", 3.0, 4.0)]
    [InlineData("= a ^ 3", 2.0, 8.0)]
    public void TheFormulaIsWhatComesOut(string formula, double input, double expected)
    {
        var rig = Driven(formula, input);

        Assert.Equal(expected, rig.Sim.NodeVoltage(rig.Out), Math.Abs(expected) * 1e-4 + 1e-6);
    }

    /// <summary>
    /// The one an ideal source cannot do: a stage that runs out of room. Newton has to find the
    /// flat part rather than walking off up the straight one.
    /// </summary>
    [Theory]
    [InlineData(0.1, 0.24917)]
    [InlineData(1.0, 1.90399)]
    [InlineData(5.0, 2.49977)]
    public void ASaturatingStageSaturates(double input, double expected)
    {
        var rig = Driven("= 2.5 * tanh(a)", input);

        // Against the arithmetic to five figures: a numerically linearised device that is nearly
        // right is exactly the failure worth catching, because the waveform looks fine.
        Assert.Equal(expected, rig.Sim.NodeVoltage(rig.Out), 1e-5);
    }

    [Fact]
    public void ACurrentOutputDrivesItsLoad()
    {
        // A milliamp per volt into a kilohm is a volt per volt across the load.
        var rig = Driven("= a * 1e-3", 2.0, BehaviouralOutput.Amps);

        Assert.Equal(2.0, rig.Sim.NodeVoltage(rig.Out), 1e-6);
        Assert.Equal(0, rig.Source.VoltageSourceCount);
    }

    /// <summary>
    /// A formula in time alone is a waveform generator, and a linear one: nothing in the circuit
    /// depends on the solution, so the solver has no reason to iterate.
    /// </summary>
    [Fact]
    public void AFormulaInTimeAloneIsAWaveform()
    {
        var circuit = new Circuit();

        var source = circuit.Add(new BehaviouralSource("= sin(2 * pi * 1000 * t)") { Name = "B1" });
        var load = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(source.OutputPositive, load.A);
        circuit.Connect(load.B, ground.Pin);
        circuit.Connect(source.OutputNegative, ground.Pin);

        Assert.False(source.IsNonlinear);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-6 });

        sim.Reset();
        sim.SolveOperatingPoint();

        // A quarter of the way through a millisecond period is the peak.
        sim.Run(250e-6);

        Assert.Equal(1.0, sim.NodeVoltage(source.OutputPositive), 0.02);

        sim.Run(500e-6);

        Assert.Equal(-1.0, sim.NodeVoltage(source.OutputPositive), 0.02);
    }

    /// <summary>
    /// Two sines multiplied give their sum and difference and nothing at either original frequency,
    /// which is what a mixer is. Checked with the spectrum rather than by eye.
    /// </summary>
    [Fact]
    public void MultiplyingTwoSinesGivesTheSumAndTheDifference()
    {
        var circuit = new Circuit();

        var carrier = circuit.Add(new FunctionGenerator(Waveform.Sine, 10e3, 2.0) { Name = "V1" });
        var tone = circuit.Add(new FunctionGenerator(Waveform.Sine, 1e3, 2.0) { Name = "V2" });
        var mixer = circuit.Add(new BehaviouralSource("= a * b") { Name = "B1" });
        var load = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(carrier.B, ground.Pin);
        circuit.Connect(tone.B, ground.Pin);
        circuit.Connect(carrier.A, mixer.A);
        circuit.Connect(tone.A, mixer.B);

        circuit.Connect(mixer.OutputPositive, load.A);
        circuit.Connect(load.B, ground.Pin);
        circuit.Connect(mixer.OutputNegative, ground.Pin);

        var probe = new SignalProbe("Out", mixer.OutputPositive, default);
        circuit.Probes.Add(probe);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-6 });

        sim.Reset();
        sim.SolveOperatingPoint();
        sim.ResolveProbes();
        sim.Run(16e-3);

        var samples = probe.HistoryBuffer.ToArray();
        var spectrum = Spectrum.Of(samples, samples[0].Time, samples[^1].Time);

        double At(double hertz) => Enumerable.Range(0, spectrum.Frequencies.Count)
            .Where(i => Math.Abs(spectrum.Frequencies[i] - hertz) < 150)
            .Select(i => spectrum.Magnitudes[i])
            .DefaultIfEmpty(0)
            .Max();

        // Sum and difference are there; the two inputs are not.
        Assert.True(At(9e3) > 0.2, $"no difference term: {At(9e3):g3}");
        Assert.True(At(11e3) > 0.2, $"no sum term: {At(11e3):g3}");
        Assert.True(At(10e3) < At(9e3) / 5, $"the carrier came through: {At(10e3):g3}");
        Assert.True(At(1e3) < At(9e3) / 5, $"the tone came through: {At(1e3):g3}");
    }

    // ---- being wrong -------------------------------------------------------

    [Fact]
    public void AFormulaThatDoesNotParseSaysSoAndPutsOutNothing()
    {
        var rig = Driven("= 3 * ", 1.0);

        Assert.NotNull(rig.Source.Problem);
        Assert.Single(rig.Source.Violations);
        Assert.Contains("a, b, c", rig.Source.Violations[0]);

        // Zero volts rather than an arbitrary number, and the rest of the circuit still solves.
        Assert.Equal(0.0, rig.Sim.NodeVoltage(rig.Out), 1e-9);
    }

    [Fact]
    public void AnUnknownNameIsRefusedRatherThanTreatedAsZero()
    {
        var rig = Driven("= vin * 2", 1.0);

        Assert.NotNull(rig.Source.Problem);
        Assert.Contains("vin", rig.Source.Problem!);
    }

    /// <summary>
    /// A formula can produce something that is not a number — a division by zero, the log of a
    /// negative. That must cost this one part its output rather than the whole circuit its solve.
    /// </summary>
    [Fact]
    public void ANumberThatIsNotANumberDoesNotPoisonTheMatrix()
    {
        var rig = Driven("= 1 / (a - 1)", 1.0);

        var volts = rig.Sim.NodeVoltage(rig.Out);

        Assert.True(double.IsFinite(volts), "the matrix took a NaN");
    }

    [Fact]
    public void TheLeadingEqualsIsOptional()
    {
        var withIt = Driven("= 2 * a", 2.0);
        var without = Driven("2 * a", 2.0);

        Assert.Equal(4.0, withIt.Sim.NodeVoltage(withIt.Out), 1e-6);
        Assert.Equal(4.0, without.Sim.NodeVoltage(without.Out), 1e-6);
    }

    [Fact]
    public void AnUnwiredInputIsZeroRatherThanASingularMatrix()
    {
        // b and c are joined to nothing at all, which must not stop the circuit solving.
        var rig = Driven("= a + b + c", 2.0);

        Assert.Equal(2.0, rig.Sim.NodeVoltage(rig.Out), 1e-3);
    }
}
