using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// The DC sweep, checked against the curves the parts are specified by rather than against
/// anything this code produced before.
/// </summary>
public class DcSweepTests
{
    private static SignalProbe Probe(Circuit circuit, Terminal terminal, string label, ProbeKind kind)
    {
        var probe = new SignalProbe(label, terminal, Color.ProbePalette[0]) { Kind = kind };
        circuit.Probes.Add(probe);
        return probe;
    }

    private static CircuitSimulator Ready(Circuit circuit)
    {
        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        return sim;
    }

    // ---- the range itself -------------------------------------------------

    [Fact]
    public void ASweepWalksFromStartToStopInclusive()
    {
        var values = new SweepTarget(new Resistor(1e3), "Resistance", 0.0, 10.0, 11).Values();

        Assert.Equal(11, values.Count);
        Assert.Equal(0.0, values[0]);
        Assert.Equal(10.0, values[^1]);
        Assert.Equal(5.0, values[5]);
    }

    [Fact]
    public void APropertyThatIsNotAWritableNumberIsRefused()
    {
        var resistor = new Resistor(1e3);

        Assert.NotNull(new SweepTarget(resistor, "Resistance", 0, 1).Resolve());

        // Not a number, and not a property at all.
        Assert.Null(new SweepTarget(resistor, "ComponentType", 0, 1).Resolve());
        Assert.Null(new SweepTarget(resistor, "NoSuchThing", 0, 1).Resolve());
    }

    // ---- a diode's exponential --------------------------------------------

    [Fact]
    public void ADiodeSweepsOutItsExponentialAtAboutAHundredMillivoltsPerDecade()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(0.0));
        var diode = circuit.Add(new Diode());
        var series = circuit.Add(new Resistor(100.0));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, diode.Anode);
        circuit.Connect(diode.Cathode, series.A);
        circuit.Connect(series.B, ground.Pin);

        Probe(circuit, diode.Anode, "Id", ProbeKind.Current);

        var sim = Ready(circuit);

        var result = new DcSweep(sim).Run(new DcSweepRequest(
            new SweepTarget(supply, nameof(DcVoltageSource.Voltage), 0.0, 1.0, 101)));

        Assert.Equal(0, result.FailedPoints);
        Assert.False(result.IsEmpty);
        Assert.Single(result.Curves);
        Assert.Null(result.StepLabel);

        var current = result.Curves[0].Traces[0].Values;

        // Monotonic, and starting from nothing.
        Assert.Equal(0.0, current[0], 6);
        for (var i = 1; i < current.Count; i++)
            Assert.True(current[i] >= current[i - 1] - 1e-12, $"current fell back at point {i}");

        // Two points a decade of current apart, with the diode's own voltage worked out by taking
        // the series resistor's drop off the supply. The slope between them is the thing every
        // textbook quotes, and it falls out of the sweep rather than being asserted into it.
        var (lowIndex, highIndex) = (Nearest(current, 100e-6), Nearest(current, 1e-3));

        var lowDiode = result.X[lowIndex] - (current[lowIndex] * 100.0);
        var highDiode = result.X[highIndex] - (current[highIndex] * 100.0);

        var perDecade = (highDiode - lowDiode) / Math.Log10(current[highIndex] / current[lowIndex]);

        Assert.InRange(perDecade, 0.055, 0.135);
    }

    private static int Nearest(IReadOnlyList<double> values, double wanted)
    {
        var best = 0;
        for (var i = 1; i < values.Count; i++)
            if (Math.Abs(values[i] - wanted) < Math.Abs(values[best] - wanted)) best = i;

        return best;
    }

    // ---- a transistor's output characteristic ------------------------------

    [Fact]
    public void SteppingTheBaseCurrentDrawsTheFamilyOfCurvesOffTheDatasheet()
    {
        var circuit = new Circuit();
        var collector = circuit.Add(new DcVoltageSource(5.0));
        var baseDrive = circuit.Add(new DcCurrentSource(10e-6));
        var transistor = circuit.Add(new BipolarTransistor());
        var ground = circuit.Add(new Ground());

        circuit.Connect(collector.Negative, ground.Pin);
        circuit.Connect(collector.Positive, transistor.Collector);
        circuit.Connect(transistor.Emitter, ground.Pin);

        // Current out of the source's positive terminal and into the base.
        circuit.Connect(baseDrive.Positive, ground.Pin);
        circuit.Connect(baseDrive.Negative, transistor.Base);

        Probe(circuit, collector.Positive, "Ic", ProbeKind.Current);

        var sim = Ready(circuit);

        var result = new DcSweep(sim).Run(new DcSweepRequest(
            new SweepTarget(collector, nameof(DcVoltageSource.Voltage), 0.0, 5.0, 26),
            new SweepTarget(baseDrive, nameof(DcCurrentSource.Current), 10e-6, 40e-6, 4)));

        Assert.Equal(4, result.Curves.Count);
        Assert.Equal(0, result.FailedPoints);
        Assert.NotNull(result.StepLabel);

        // In the flat region the collector current is the base current times beta, and beta is the
        // same number on every curve — which is the whole point of the fan.
        var betas = new List<double>();

        foreach (var curve in result.Curves)
        {
            var atFourVolts = Math.Abs(curve.Traces[0].Values[20]);
            betas.Add(atFourVolts / curve.StepValue);
        }

        foreach (var beta in betas) Assert.InRange(beta, 50, 400);
        Assert.InRange(betas.Max() - betas.Min(), 0, betas.Average() * 0.05);

        // And the curves are ordered and separated: more base current is more collector current,
        // everywhere, not just on average.
        for (var i = 1; i < result.Curves.Count; i++)
        for (var x = 10; x < result.X.Count; x++)
        {
            var below = Math.Abs(result.Curves[i - 1].Traces[0].Values[x]);
            var above = Math.Abs(result.Curves[i].Traces[0].Values[x]);

            Assert.True(above > below, $"curve {i} was not above curve {i - 1} at point {x}");
        }
    }

    [Fact]
    public void TheCurvesTiltUpwardsWhichIsTheEarlyEffect()
    {
        var circuit = new Circuit();
        var collector = circuit.Add(new DcVoltageSource(5.0));
        var baseDrive = circuit.Add(new DcCurrentSource(20e-6));
        var transistor = circuit.Add(new BipolarTransistor());
        var ground = circuit.Add(new Ground());

        circuit.Connect(collector.Negative, ground.Pin);
        circuit.Connect(collector.Positive, transistor.Collector);
        circuit.Connect(transistor.Emitter, ground.Pin);
        circuit.Connect(baseDrive.Positive, ground.Pin);
        circuit.Connect(baseDrive.Negative, transistor.Base);

        Probe(circuit, collector.Positive, "Ic", ProbeKind.Current);

        var sim = Ready(circuit);

        var result = new DcSweep(sim).Run(new DcSweepRequest(
            new SweepTarget(collector, nameof(DcVoltageSource.Voltage), 1.0, 5.0, 21)));

        var values = result.Curves[0].Traces[0].Values.Select(Math.Abs).ToList();

        // Flat would be a horizontal line. It is not — a real transistor's output resistance is
        // finite, and the slope of the flat region is where that number comes from.
        Assert.True(values[^1] > values[0], "the characteristic did not rise with Vce at all");

        // But only slightly: a few percent across four volts, not a few times.
        Assert.InRange(values[^1] / values[0], 1.005, 1.30);
    }

    // ---- the sweep tidies up after itself ---------------------------------

    [Fact]
    public void EverySweptPropertyIsPutBackAndTheBiasPointIsRestored()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(3.0));
        var load = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, load.A);
        circuit.Connect(load.B, ground.Pin);

        Probe(circuit, load.A, "V", ProbeKind.Voltage);

        var sim = Ready(circuit);

        new DcSweep(sim).Run(new DcSweepRequest(
            new SweepTarget(supply, nameof(DcVoltageSource.Voltage), 0.0, 10.0, 11),
            new SweepTarget(load, nameof(Resistor.Resistance), 1e3, 4e3, 4)));

        Assert.Equal(3.0, supply.Voltage);
        Assert.Equal(1e3, load.Resistance);

        // And the canvas is showing the circuit's own operating point, not the sweep's last one.
        Assert.Equal(3.0, sim.NodeVoltage(load.A), 6);
    }

    [Fact]
    public void ADividerSweepsOutTheStraightLineItShould()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(0.0));
        var top = circuit.Add(new Resistor(1e3));
        var bottom = circuit.Add(new Resistor(3e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);

        Probe(circuit, top.B, "Mid", ProbeKind.Voltage);

        var sim = Ready(circuit);

        var result = new DcSweep(sim).Run(new DcSweepRequest(
            new SweepTarget(supply, nameof(DcVoltageSource.Voltage), 0.0, 10.0, 11)));

        var midpoint = result.Curves[0].Traces[0].Values;

        // Three quarters of the input, at every point, because that is what the divider is.
        for (var i = 0; i < result.X.Count; i++)
            Assert.Equal(result.X[i] * 0.75, midpoint[i], 6);
    }

    [Fact]
    public void APointTheSolverCannotReachLeavesAGapRatherThanThrowingTheCurveAway()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var load = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, load.A);
        circuit.Connect(load.B, ground.Pin);

        Probe(circuit, load.A, "V", ProbeKind.Voltage);

        var sim = Ready(circuit);

        // Sweeping the resistance down to zero shorts the supply at the last point.
        var result = new DcSweep(sim).Run(new DcSweepRequest(
            new SweepTarget(load, nameof(Resistor.Resistance), 1e3, 0.0, 11)));

        // Whatever happened at the end, the beginning is still a usable curve.
        var values = result.Curves[0].Traces[0].Values;

        Assert.Equal(11, values.Count);
        Assert.Equal(5.0, values[0], 6);
        Assert.False(result.IsEmpty);
    }

    [Fact]
    public void SweepingSomethingThatIsNotThereIsRefusedWithTheNameInTheMessage()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var load = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, load.A);
        circuit.Connect(load.B, ground.Pin);

        var sim = Ready(circuit);

        var error = Assert.Throws<ArgumentException>(() =>
            new DcSweep(sim).Run(new DcSweepRequest(
                new SweepTarget(supply, "Flavour", 0, 1))));

        Assert.Contains("Flavour", error.Message);
        Assert.Contains(supply.Name, error.Message);
    }
}
