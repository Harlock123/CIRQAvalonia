using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// Differential and power probes, checked against arithmetic done by hand on the same circuit.
/// </summary>
public class DerivedProbeTests
{
    /// <summary>A 12 V supply across 100 Ω and 200 Ω in series: 40 mA, tapped at 8 V.</summary>
    private static (Circuit, Resistor Top, Resistor Bottom, DcVoltageSource) Divider()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(12.0));
        var top = circuit.Add(new Resistor(100.0));
        var bottom = circuit.Add(new Resistor(200.0));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);

        return (circuit, top, bottom, supply);
    }

    private static CircuitSimulator Ready(Circuit circuit)
    {
        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        return sim;
    }

    private static SignalProbe Probe(
        Circuit circuit, Terminal target, ProbeKind kind, Terminal? reference = null)
    {
        var probe = new SignalProbe("P", target, Color.ProbePalette[0])
        {
            Kind = kind,
            ReferenceTerminal = reference,
        };

        circuit.Probes.Add(probe);
        return probe;
    }

    // ---- differential ------------------------------------------------------

    [Fact]
    public void ADifferentialProbeReadsTheVoltageBetweenItsTwoPoints()
    {
        var (circuit, top, _, _) = Divider();
        var probe = Probe(circuit, top.A, ProbeKind.Differential, top.B);

        var sim = Ready(circuit);
        sim.ResolveProbes();

        // 40 mA through 100 Ω is four volts, and both ends are well away from ground.
        Assert.Equal(4.0, sim.SampleProbe(probe), 6);
        Assert.Equal("V", probe.Unit);
        Assert.True(probe.IsDerived);
    }

    [Fact]
    public void WithNoReferenceItIsTheSameAnswerAnOrdinaryVoltageProbeGives()
    {
        var (circuit, top, _, _) = Divider();

        var plain = Probe(circuit, top.B, ProbeKind.Voltage);
        var derived = Probe(circuit, top.B, ProbeKind.Differential);

        var sim = Ready(circuit);
        sim.ResolveProbes();

        Assert.Equal(8.0, sim.SampleProbe(plain), 6);
        Assert.Equal(sim.SampleProbe(plain), sim.SampleProbe(derived), 9);
    }

    [Fact]
    public void ReversingTheTwoPointsReversesTheSign()
    {
        var (circuit, top, _, _) = Divider();

        var forward = Probe(circuit, top.A, ProbeKind.Differential, top.B);
        var backward = Probe(circuit, top.B, ProbeKind.Differential, top.A);

        var sim = Ready(circuit);
        sim.ResolveProbes();

        Assert.Equal(4.0, sim.SampleProbe(forward), 6);
        Assert.Equal(-4.0, sim.SampleProbe(backward), 6);
    }

    /// <summary>
    /// The case the kind exists for: a few millivolts across a shunt, with both of its ends sitting
    /// near a rail. Against ground neither end tells you anything.
    /// </summary>
    [Fact]
    public void AShuntHighInTheRailIsReadableDifferentiallyAndNotOtherwise()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(24.0));
        var shunt = circuit.Add(new Resistor(0.1));
        var load = circuit.Add(new Resistor(48.0));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, shunt.A);
        circuit.Connect(shunt.B, load.A);
        circuit.Connect(load.B, ground.Pin);

        var across = Probe(circuit, shunt.A, ProbeKind.Differential, shunt.B);
        var against = Probe(circuit, shunt.B, ProbeKind.Voltage);

        var sim = Ready(circuit);
        sim.ResolveProbes();

        // 24 V across 48.1 Ω is 499.0 mA, and that through a tenth of an ohm is 49.90 mV.
        var current = 24.0 / 48.1;

        Assert.Equal(current * 0.1, sim.SampleProbe(across), 6);

        // Where the plain probe reads the rail and the shunt's drop is lost in it — fifty
        // millivolts on top of twenty-four volts is the fourth significant figure.
        Assert.Equal(24.0 - (current * 0.1), sim.SampleProbe(against), 6);
    }

    // ---- power -------------------------------------------------------------

    [Fact]
    public void APowerProbeMultipliesTheVoltageAcrossByTheCurrentThrough()
    {
        var (circuit, top, bottom, _) = Divider();

        var inTop = Probe(circuit, top.A, ProbeKind.Power, top.B);
        var inBottom = Probe(circuit, bottom.A, ProbeKind.Power, bottom.B);

        var sim = Ready(circuit);
        sim.ResolveProbes();

        // I²R: 40 mA through 100 Ω is 160 mW, and through 200 Ω is 320 mW.
        Assert.Equal(0.16, Math.Abs(sim.SampleProbe(inTop)), 4);
        Assert.Equal(0.32, Math.Abs(sim.SampleProbe(inBottom)), 4);

        Assert.Equal("W", inTop.Unit);
    }

    [Fact]
    public void TheSupplyDeliversWhatTheResistorsBetweenThemDissipate()
    {
        var (circuit, top, bottom, supply) = Divider();

        var fromSupply = Probe(circuit, supply.Positive, ProbeKind.Power, supply.Negative);
        var inTop = Probe(circuit, top.A, ProbeKind.Power, top.B);
        var inBottom = Probe(circuit, bottom.A, ProbeKind.Power, bottom.B);

        var sim = Ready(circuit);
        sim.ResolveProbes();

        var delivered = Math.Abs(sim.SampleProbe(fromSupply));
        var dissipated = Math.Abs(sim.SampleProbe(inTop)) + Math.Abs(sim.SampleProbe(inBottom));

        // Conservation of energy, which is as good a check on the whole chain as there is.
        Assert.Equal(delivered, dissipated, 6);
        Assert.Equal(12.0 * 0.04, delivered, 4);
    }

    [Fact]
    public void AProbeOnSomethingCarryingNoCurrentReadsNoPower()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var load = circuit.Add(new Resistor(1e3));
        var dangling = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, load.A);
        circuit.Connect(load.B, ground.Pin);
        circuit.Connect(dangling.A, supply.Positive);

        var probe = Probe(circuit, dangling.A, ProbeKind.Power, dangling.B);

        var sim = Ready(circuit);
        sim.ResolveProbes();

        Assert.Equal(0.0, sim.SampleProbe(probe), 9);
    }

    // ---- bookkeeping -------------------------------------------------------

    [Fact]
    public void ChangingTheReferenceThrowsAwayTheHistoryBecauseItIsADifferentQuantity()
    {
        var (circuit, top, _, _) = Divider();
        var probe = Probe(circuit, top.A, ProbeKind.Differential);

        probe.Record(0, 1.0);
        probe.Record(1, 2.0);
        Assert.Equal(2, probe.HistoryBuffer.Count);

        probe.ReferenceTerminal = top.B;

        Assert.Equal(0, probe.HistoryBuffer.Count);
        Assert.Equal(top.B.ToString(), probe.ReferenceLabel);
    }

    [Fact]
    public void AReferenceOnAPartThatIsGoneFallsBackToGroundRatherThanThrowing()
    {
        var (circuit, top, _, _) = Divider();

        var orphan = new Resistor(1e3);
        var probe = Probe(circuit, top.A, ProbeKind.Differential, orphan.A);

        var sim = Ready(circuit);
        sim.ResolveProbes();

        // Against ground, which is what an unresolvable reference means.
        Assert.Equal(12.0, sim.SampleProbe(probe), 6);
    }
}

/// <summary>A derived probe has to survive being saved, or it is lost on every reopen.</summary>
public class DerivedProbeSerializationTests
{
    [Fact]
    public void ADifferentialProbesReferenceSurvivesARoundTrip()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(12.0));
        var shunt = circuit.Add(new Resistor(0.1));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, shunt.A);
        circuit.Connect(shunt.B, ground.Pin);

        circuit.Probes.Add(new SignalProbe("Shunt", shunt.A, Color.ProbePalette[0])
        {
            Kind = ProbeKind.Power,
            ReferenceTerminal = shunt.B,
        });

        var json = Cirq.Components.Serialization.CircuitSerializer.ToJson(circuit);
        var result = Cirq.Components.Serialization.CircuitSerializer.FromJson(json);
        var reloaded = result.Circuit;

        Assert.Empty(result.Warnings);

        var probe = Assert.Single(reloaded.Probes);

        Assert.Equal(ProbeKind.Power, probe.Kind);
        Assert.NotNull(probe.ReferenceTerminal);
        Assert.Equal("Shunt", probe.Label);

        // The same point, on the reloaded circuit's own copy of the part.
        var reloadedShunt = reloaded.Components.OfType<Resistor>().Single();
        Assert.Same(reloadedShunt.B, probe.ReferenceTerminal);
    }

    [Fact]
    public void AnOrdinaryProbeStillRoundTripsWithNoReferenceAtAll()
    {
        var circuit = new Circuit();
        var resistor = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());
        circuit.Connect(resistor.B, ground.Pin);

        circuit.Probes.Add(new SignalProbe("V", resistor.A, Color.ProbePalette[0]));

        var json = Cirq.Components.Serialization.CircuitSerializer.ToJson(circuit);
        var reloaded = Cirq.Components.Serialization.CircuitSerializer.FromJson(json).Circuit;

        var probe = Assert.Single(reloaded.Probes);

        Assert.Null(probe.ReferenceTerminal);
        Assert.Equal("ground", probe.ReferenceLabel);
    }
}
