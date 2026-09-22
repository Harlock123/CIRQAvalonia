using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// Which part is responsible for the spread — the question a tolerance analysis cannot answer,
/// and the one that decides where a tighter part is worth buying.
/// </summary>
public class SensitivityTests
{
    private static (Circuit, Resistor Top, Resistor Bottom) Divider(
        double topTolerance, double bottomTolerance)
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(10.0));
        var top = circuit.Add(new Resistor(1e3) { Tolerance = topTolerance });
        var bottom = circuit.Add(new Resistor(1e3) { Tolerance = bottomTolerance });
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);

        circuit.Probes.Add(new SignalProbe("Mid", top.B, Color.ProbePalette[0]));

        return (circuit, top, bottom);
    }

    [Fact]
    public void TwoEqualPartsShareTheBlameEqually()
    {
        var (circuit, _, _) = Divider(0.05, 0.05);

        var result = Assert.Single(new Sensitivity(circuit).Run());

        Assert.Equal("Mid", result.Label);
        Assert.Equal(5.0, result.Nominal, 6);
        Assert.Equal(2, result.Entries.Count);

        // Symmetric circuit, symmetric parts: half each, and the shares add to one.
        Assert.All(result.Entries, e => Assert.Equal(0.5, e.Share, 0.02));
        Assert.Equal(1.0, result.Entries.Sum(e => e.Share), 6);
    }

    /// <summary>The whole point: the part with the wider band is named first.</summary>
    [Fact]
    public void ThePartWithTheWiderToleranceIsRankedFirst()
    {
        var (circuit, top, _) = Divider(0.20, 0.01);

        var result = Assert.Single(new Sensitivity(circuit).Run());

        Assert.Equal(top.Name, result.Entries[0].Part);
        Assert.Equal(nameof(Resistor.Resistance), result.Entries[0].Property);

        // Twenty times the band is four hundred times the variance, so it is nearly all of it.
        Assert.True(result.Entries[0].Share > 0.95,
            $"the 20 % part only took {result.Entries[0].Share:P0} of the blame");
    }

    /// <summary>
    /// And it is the circuit's own sensitivity, not just the band: a part the output barely
    /// depends on is ranked low however loose it is.
    /// </summary>
    [Fact]
    public void APartTheOutputBarelyDependsOnIsRankedLowHoweverLooseItIs()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(10.0));
        var top = circuit.Add(new Resistor(1e3) { Tolerance = 0.01 });
        var bottom = circuit.Add(new Resistor(1e3) { Tolerance = 0.01 });

        // Hung across the output, but a thousand times its impedance, so it hardly loads it.
        var bystander = circuit.Add(new Resistor(1e6) { Tolerance = 0.20 });
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);
        circuit.Connect(bystander.A, top.B);
        circuit.Connect(bystander.B, ground.Pin);

        circuit.Probes.Add(new SignalProbe("Mid", top.B, Color.ProbePalette[0]));

        var result = Assert.Single(new Sensitivity(circuit).Run());

        var loose = result.Entries.Single(e => e.Part == bystander.Name);

        Assert.True(loose.Share < 0.1,
            $"the megohm took {loose.Share:P0} of the blame despite its 20 % band");

        // It is last, behind two parts with a twentieth of its tolerance.
        Assert.Equal(bystander.Name, result.Entries[^1].Part);
    }

    /// <summary>
    /// Elasticity: how far the output moves for a given fractional move in the part. For a divider
    /// of two equal resistors it is a half either way, which is a fact about the circuit rather
    /// than about the parts in it.
    /// </summary>
    [Fact]
    public void TheElasticityIsAPropertyOfTheCircuitRatherThanOfTheTolerance()
    {
        foreach (var tolerance in new[] { 0.01, 0.05, 0.20 })
        {
            var (circuit, _, _) = Divider(tolerance, tolerance);

            var result = Assert.Single(new Sensitivity(circuit).Run());

            foreach (var entry in result.Entries)
                Assert.Equal(0.5, entry.Elasticity(result.Nominal), 0.05);
        }
    }

    [Fact]
    public void EveryPartIsPutBackAndTheBiasPointIsRestored()
    {
        var (circuit, top, bottom) = Divider(0.10, 0.10);

        new Sensitivity(circuit).Run();

        Assert.Equal(1e3, top.Resistance);
        Assert.Equal(1e3, bottom.Resistance);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        Assert.Equal(5.0, sim.NodeVoltage(top.B), 6);
    }

    [Fact]
    public void NothingToleranced_OrNothingProbed_GivesNothing()
    {
        var (withoutTolerance, _, _) = Divider(0, 0);
        Assert.Empty(new Sensitivity(withoutTolerance).Run());

        var (withoutProbes, _, _) = Divider(0.05, 0.05);
        withoutProbes.Probes.Clear();
        Assert.Empty(new Sensitivity(withoutProbes).Run());
    }

    [Fact]
    public void EachProbeIsRankedSeparatelyBecauseTheyDependOnDifferentParts()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(10.0));

        var leftTop = circuit.Add(new Resistor(1e3) { Tolerance = 0.20 });
        var leftBottom = circuit.Add(new Resistor(1e3) { Tolerance = 0.20 });
        var rightTop = circuit.Add(new Resistor(1e3) { Tolerance = 0.01 });
        var rightBottom = circuit.Add(new Resistor(1e3) { Tolerance = 0.01 });
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);

        foreach (var (top, bottom) in new[] { (leftTop, leftBottom), (rightTop, rightBottom) })
        {
            circuit.Connect(supply.Positive, top.A);
            circuit.Connect(top.B, bottom.A);
            circuit.Connect(bottom.B, ground.Pin);
        }

        circuit.Probes.Add(new SignalProbe("Left", leftTop.B, Color.ProbePalette[0]));
        circuit.Probes.Add(new SignalProbe("Right", rightTop.B, Color.ProbePalette[1]));

        var results = new Sensitivity(circuit).Run();

        Assert.Equal(2, results.Count);

        // Two independent dividers: each output depends only on its own pair, and the other two
        // parts contribute nothing at all to it.
        var left = results.Single(r => r.Label == "Left");
        var right = results.Single(r => r.Label == "Right");

        Assert.Contains(left.Entries.Take(2), e => e.Part == leftTop.Name);
        Assert.Equal(0.0, left.Entries.Single(e => e.Part == rightTop.Name).Share, 6);

        Assert.Contains(right.Entries.Take(2), e => e.Part == rightTop.Name);
        Assert.Equal(0.0, right.Entries.Single(e => e.Part == leftTop.Name).Share, 6);
    }
}
