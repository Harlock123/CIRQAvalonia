using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Serialization;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The TIP120, against the handful of figures from its datasheet that decide whether a circuit
/// built round one works: the gain, the turn-on voltage, the saturation voltage and the diode.
/// </summary>
public class DarlingtonTests
{
    /// <summary>
    /// A base current pushed in, the collector held at a fixed voltage, everything else measured.
    /// </summary>
    private static DarlingtonTransistor Biased(double baseCurrent, double collectorVolts)
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());
        var drive = circuit.Add(new DcCurrentSource(baseCurrent));
        var collector = circuit.Add(new DcVoltageSource(collectorVolts));
        var q = circuit.Add(new DarlingtonTransistor());

        circuit.Connect(drive.Positive, gnd.Pin);
        circuit.Connect(drive.Negative, q.Base);
        circuit.Connect(collector.Negative, gnd.Pin);
        circuit.Connect(collector.Positive, q.Collector);
        circuit.Connect(q.Emitter, gnd.Pin);

        new CircuitSimulator(circuit).SolveOperatingPoint();
        return q;
    }

    /// <summary>The datasheet's minimum hFE is a thousand, at half an amp and at three.</summary>
    [Fact]
    public void ItsGainIsOverAThousandAtAmps()
    {
        var q = Biased(0.4e-3, 3.0);

        Assert.InRange(q.CollectorCurrent, 0.3, 3.0);
        Assert.True(q.CurrentGain > 1000, $"gain was {q.CurrentGain:0}");
    }

    /// <summary>Two junctions in series: about 1.4 V to turn on, not a single transistor's 0.7.</summary>
    [Fact]
    public void ItTakesTwoJunctionDropsToTurnOn()
    {
        var q = Biased(0.2e-3, 3.0);

        Assert.InRange(q.Vbe, 1.2, 1.6);
    }

    /// <summary>
    /// Saturated it still drops about a volt, because the output transistor's collector can never be
    /// pulled below its own base by the driver feeding it. The datasheet allows two volts at 3 A.
    /// </summary>
    [Fact]
    public void SaturatedItStillDropsAboutAVolt()
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());
        var supply = circuit.Add(new DcVoltageSource(12.0));
        var load = circuit.Add(new Resistor(12.0));
        var baseResistor = circuit.Add(new Resistor(1e3));
        var logic = circuit.Add(new DcVoltageSource(5.0));
        var q = circuit.Add(new DarlingtonTransistor());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(logic.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, load.A);
        circuit.Connect(load.B, q.Collector);
        circuit.Connect(logic.Positive, baseResistor.A);
        circuit.Connect(baseResistor.B, q.Base);
        circuit.Connect(q.Emitter, gnd.Pin);

        new CircuitSimulator(circuit).SolveOperatingPoint();

        // A logic pin's 3.6 mA switches most of an amp.
        Assert.InRange(q.BaseCurrent, 3e-3, 4e-3);
        Assert.InRange(q.Vce, 0.7, 1.5);
        Assert.True(q.CollectorCurrent > 0.85, $"it passed only {q.CollectorCurrent:0.00} A");
        Assert.InRange(q.PowerDissipation, 0.6, 1.5);
    }

    /// <summary>
    /// With the base left open the two resistors hold it off: the output transistor's base is tied
    /// to its emitter through 120 Ω, so leakage has nowhere to be multiplied.
    /// </summary>
    [Fact]
    public void WithItsBaseOpenItIsOff()
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());
        var supply = circuit.Add(new DcVoltageSource(12.0));
        var load = circuit.Add(new Resistor(12.0));
        var q = circuit.Add(new DarlingtonTransistor());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, load.A);
        circuit.Connect(load.B, q.Collector);
        circuit.Connect(q.Emitter, gnd.Pin);

        new CircuitSimulator(circuit).SolveOperatingPoint();

        Assert.True(Math.Abs(q.CollectorCurrent) < 1e-6, $"it leaked {q.CollectorCurrent * 1e6:0.0} µA");
        Assert.False(q.IsConducting);
    }

    /// <summary>The diode from emitter to collector conducts when the collector is driven below the emitter.</summary>
    [Fact]
    public void ItsInternalDiodeConductsWhenTheCollectorGoesBelowTheEmitter()
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());
        var negative = circuit.Add(new DcVoltageSource(5.0));
        var series = circuit.Add(new Resistor(10.0));
        var q = circuit.Add(new DarlingtonTransistor());

        // The supply's positive side on ground, so its negative side is at -5 V.
        circuit.Connect(negative.Positive, gnd.Pin);
        circuit.Connect(negative.Negative, series.A);
        circuit.Connect(series.B, q.Collector);
        circuit.Connect(q.Emitter, gnd.Pin);

        new CircuitSimulator(circuit).SolveOperatingPoint();

        Assert.True(q.DiodeCurrent > 0.3, $"the diode carried only {q.DiodeCurrent:0.000} A");
        Assert.True(q.CollectorCurrent < -0.3);
        Assert.InRange(q.Vce, -1.3, -0.6);
    }

    [Fact]
    public void ItsModelIsSavedByName()
    {
        var circuit = new Circuit();
        circuit.Add(new DarlingtonTransistor());
        circuit.Add(new BipolarTransistor(BjtModel.N2N2222));

        var back = CircuitSerializer.FromJson(CircuitSerializer.ToJson(circuit)).Circuit;

        Assert.Same(DarlingtonModel.Tip120, back.Components.OfType<DarlingtonTransistor>().Single().Model);
        Assert.Equal("2N2222", back.Components.OfType<BipolarTransistor>().Single().Model.Name);
    }
}
