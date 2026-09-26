using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Verification;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// The requirements, held against the circuit across a range rather than at one point.
/// <para>
/// The circuit under test is the simplest one whose answer moves with temperature and moves by a
/// known amount: a silicon diode fed from a resistor. Its forward drop falls about two millivolts a
/// degree, so a requirement for six hundred millivolts is met in a cold room and not in a hot one,
/// and the temperature where it stops being met can be worked out on paper before the test runs.
/// </para>
/// </summary>
public class SpecSweepTests
{
    private static CircuitSimulator DiodeReference()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(5.0));
        var series = circuit.Add(new Resistor(10e3));
        var diode = circuit.Add(new Diode());
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, series.A);
        circuit.Connect(series.B, diode.Anode);
        circuit.Connect(diode.Cathode, ground.Pin);

        circuit.Probes.Add(new SignalProbe("Vf", diode.Anode, default));

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.ResolveProbes();

        return sim;
    }

    private static DesignSpec AtLeast(double volts) => new()
    {
        Name = "Reference",
        Trace = "Vf",
        Quantity = SpecQuantity.Mean,
        Comparison = SpecComparison.AtLeast,
        Limit = volts,
    };

    [Fact]
    public void ARequirementIsCheckedAtEveryTemperature()
    {
        var sim = DiodeReference();
        sim.Circuit.Specs.Add(AtLeast(0.6));

        var result = new SpecSweep(sim).Run(
            new SpecSweepRequest(SweepTarget.OverTemperature(-40, 125, 12), 2e-4));

        var margin = Assert.Single(result.Margins);

        Assert.Equal(12, margin.Points.Count);
        Assert.Equal(12, result.Values.Count);
        Assert.Equal(-40, result.Values[0]);
        Assert.Equal(125, result.Values[^1]);
        Assert.Empty(result.Problems);

        // Met cold, not met hot: a diode drops about two millivolts a degree less as it warms, so
        // six hundred millivolts is there at the bottom of the range and gone at the top.
        Assert.True(margin.Points[0].Result.Passed);
        Assert.False(margin.Points[^1].Result.Passed);

        Assert.True(margin.Fails);
        Assert.False(margin.Holds);
        Assert.False(result.Holds);
    }

    [Fact]
    public void TheWorstCaseIsTheEndOfTheRangeItFailsAt()
    {
        var sim = DiodeReference();
        sim.Circuit.Specs.Add(AtLeast(0.6));

        var result = new SpecSweep(sim).Run(
            new SpecSweepRequest(SweepTarget.OverTemperature(-40, 125, 12), 2e-4));

        var margin = result.Margins[0];

        Assert.NotNull(margin.Worst);
        Assert.Equal(125, margin.Worst!.Value);

        // And it names where it does hold, which is the sentence somebody swept to get.
        var window = margin.Window;

        Assert.NotNull(window);
        Assert.Equal(-40, window!.Value.From);
        Assert.InRange(window.Value.To, -40, 60);
    }

    [Fact]
    public void ARequirementMetEverywhereSaysSo()
    {
        var sim = DiodeReference();

        // A limit no temperature in the range takes it past.
        sim.Circuit.Specs.Add(AtLeast(0.2));

        var result = new SpecSweep(sim).Run(
            new SpecSweepRequest(SweepTarget.OverTemperature(-40, 125, 8), 2e-4));

        Assert.True(result.Holds);
        Assert.True(result.Margins[0].Holds);
        Assert.False(result.Margins[0].Fails);
        Assert.StartsWith("Every requirement is met from", result.Summary());
        Assert.Contains("to spare", result.Summary());
    }

    [Fact]
    public void TheSummaryNamesTheRangeItHoldsOver()
    {
        var sim = DiodeReference();
        sim.Circuit.Specs.Add(AtLeast(0.6));

        var result = new SpecSweep(sim).Run(
            new SpecSweepRequest(SweepTarget.OverTemperature(-40, 125, 12), 2e-4));

        Assert.Contains("it holds from", result.Summary());
    }

    [Fact]
    public void ARequirementAboutATraceThatIsNotThereIsNotAFailure()
    {
        var sim = DiodeReference();

        var spec = AtLeast(0.6);
        spec.Trace = "Nowhere";
        sim.Circuit.Specs.Add(spec);

        var result = new SpecSweep(sim).Run(
            new SpecSweepRequest(SweepTarget.OverTemperature(0, 50, 4), 2e-4));

        var margin = result.Margins[0];

        // No verdict anywhere, which is not the same as failing everywhere: the circuit has not
        // been shown to break anything.
        Assert.Empty(margin.Judged);
        Assert.False(margin.Fails);
        Assert.False(margin.Holds);
        Assert.All(margin.Points, p => Assert.Contains("No trace called", p.Result.Explanation));
    }

    [Fact]
    public void NothingToCheckIsNotAnAnswer()
    {
        var sim = DiodeReference();

        var result = new SpecSweep(sim).Run(
            new SpecSweepRequest(SweepTarget.OverTemperature(0, 50, 4), 2e-4));

        Assert.True(result.IsEmpty);
        Assert.Contains("add a requirement", result.Summary());
    }

    /// <summary>
    /// A sweep leaves the circuit where it found it: same temperature, same operating point. It is
    /// run from a window somebody is in the middle of looking at.
    /// </summary>
    [Fact]
    public void TheCircuitIsPutBackAfterwards()
    {
        var sim = DiodeReference();
        sim.Circuit.Specs.Add(AtLeast(0.6));

        var before = sim.Settings.TemperatureKelvin;
        var drop = sim.NodeVoltage(sim.Circuit.Probes[0].TargetTerminal!);

        new SpecSweep(sim).Run(new SpecSweepRequest(SweepTarget.OverTemperature(-40, 125, 6), 2e-4));

        Assert.Equal(before, sim.Settings.TemperatureKelvin, 1e-9);
        Assert.Equal(drop, sim.NodeVoltage(sim.Circuit.Probes[0].TargetTerminal!), 1e-6);
    }

    /// <summary>
    /// It sweeps anything a DC sweep can, which is the other half of "does it work over the range":
    /// not only the temperature, but a part anywhere in its tolerance band.
    /// </summary>
    [Fact]
    public void ItSweepsAComponentValueToo()
    {
        var sim = DiodeReference();
        sim.Circuit.Specs.Add(AtLeast(0.6));

        var series = sim.Circuit.Components.OfType<Resistor>().Single();

        var result = new SpecSweep(sim).Run(
            new SpecSweepRequest(new SweepTarget(series, nameof(Resistor.Resistance), 1e3, 1e6, 7), 2e-4));

        // More series resistance is less current, and less current is less forward drop — so the
        // requirement holds at the low end and gives out somewhere above it.
        Assert.True(result.Margins[0].Points[0].Result.Passed);
        Assert.True(result.Margins[0].Fails);

        // And the resistor is back where it started.
        Assert.Equal(10e3, series.Resistance, 1e-6);
    }
}
