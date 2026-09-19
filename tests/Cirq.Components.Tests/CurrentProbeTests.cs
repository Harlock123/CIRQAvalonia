using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class CurrentProbeTests
{
    private static SignalProbe Clamp(Circuit circuit, Terminal terminal, string label = "I")
    {
        var probe = new SignalProbe(label, terminal, Color.Yellow) { Kind = ProbeKind.Current };
        circuit.Probes.Add(probe);
        return probe;
    }

    /// <summary>A resistor across a supply: the current is the one thing Ohm's law is for.</summary>
    [Fact]
    public void AResistorReportsOhmsLaw()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(12.0));
        var load = circuit.Add(new Resistor(240));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var probe = Clamp(circuit, load.A);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        Assert.Equal(12.0 / 240.0, sim.SampleProbe(probe), 1e-6);
    }

    /// <summary>
    /// Clamping the other end reads the same current the other way round, which is what a clamp
    /// meter does when you turn it over — and the sign convention the whole feature rests on.
    /// </summary>
    [Fact]
    public void ProbingTheOtherEndReadsEqualAndOpposite()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(9.0));
        var load = circuit.Add(new Resistor(1e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var into = Clamp(circuit, load.A, "into");
        var outOf = Clamp(circuit, load.B, "out of");

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        Assert.Equal(9e-3, sim.SampleProbe(into), 1e-7);
        Assert.Equal(-9e-3, sim.SampleProbe(outOf), 1e-7);
    }

    /// <summary>
    /// A capacitor charging through a resistor: at the instant the step arrives the capacitor is
    /// still at zero volts, so the resistor sets the current and all of it goes into the cap.
    /// </summary>
    [Fact]
    public void ACapacitorReportsTheCurrentChargingIt()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(10.0));
        var resistor = circuit.Add(new Resistor(10e3));
        // Pinned discharged: without an initial condition the bias point is the steady state,
        // where the capacitor is already charged and nothing is flowing into it.
        var capacitor = circuit.Add(new Capacitor(100e-9) { InitialVoltage = 0 });
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, resistor.A);
        circuit.Connect(resistor.B, capacitor.A);
        circuit.Connect(capacitor.B, gnd.Pin);

        var probe = Clamp(circuit, capacitor.A);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        // One RC is 1 ms. A tenth of the way in, the current has decayed by e^-0.1.
        sim.Run(100e-6);

        var expected = (10.0 / 10e3) * Math.Exp(-0.1);
        Assert.Equal(expected, sim.SampleProbe(probe), expected * 0.05);
    }

    /// <summary>A switch carries the load current closed and essentially none open.</summary>
    [Fact]
    public void ASwitchCarriesCurrentOnlyWhenClosed()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var contact = circuit.Add(new ToggleSwitch(closed: true));
        var load = circuit.Add(new Resistor(100));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, contact.A);
        circuit.Connect(contact.B, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var probe = Clamp(circuit, contact.A);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        Assert.Equal(0.05, sim.SampleProbe(probe), 1e-3);

        contact.IsClosed = false;
        var open = new CircuitSimulator(circuit);
        open.Reset();
        open.SolveOperatingPoint();
        Assert.True(Math.Abs(open.SampleProbe(probe)) < 1e-6);
    }

    /// <summary>
    /// The three terminals of a transistor have to add up. Probing all of them is the quickest
    /// way to see that the base current is the small one doing the work.
    /// </summary>
    [Fact]
    public void ATransistorsThreeCurrentsObeyKirchhoff()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(9.0));
        var baseResistor = circuit.Add(new Resistor(47e3));
        var collectorResistor = circuit.Add(new Resistor(1e3));
        var transistor = circuit.Add(new BipolarTransistor(BjtModel.N2N3904));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, baseResistor.A);
        circuit.Connect(baseResistor.B, transistor.Base);
        circuit.Connect(supply.Positive, collectorResistor.A);
        circuit.Connect(collectorResistor.B, transistor.Collector);
        circuit.Connect(transistor.Emitter, gnd.Pin);

        var ic = Clamp(circuit, transistor.Collector, "Ic");
        var ib = Clamp(circuit, transistor.Base, "Ib");
        var ie = Clamp(circuit, transistor.Emitter, "Ie");

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        var c = sim.SampleProbe(ic);
        var b = sim.SampleProbe(ib);
        var e = sim.SampleProbe(ie);

        Assert.True(c > 0 && b > 0, "an NPN in conduction sources current into both");
        Assert.True(b < c, "base current is the small one");
        Assert.Equal(0.0, c + b + e, 1e-9);
    }

    /// <summary>A diode's current probe agrees with the diode's own exponential.</summary>
    [Fact]
    public void ADiodeReportsItsOwnConduction()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var resistor = circuit.Add(new Resistor(1e3));
        var diode = circuit.Add(new Diode());
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, resistor.A);
        circuit.Connect(resistor.B, diode.Anode);
        circuit.Connect(diode.Cathode, gnd.Pin);

        var probe = Clamp(circuit, diode.Anode);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        Assert.Equal(diode.Current, sim.SampleProbe(probe), 1e-9);

        // About (5 - 0.7) / 1k, since the drop is what a silicon diode drops.
        Assert.InRange(sim.SampleProbe(probe), 4.0e-3, 4.5e-3);
    }

    /// <summary>The readout is in engineering notation: 3.03 mA, not 0.00303 A.</summary>
    [Fact]
    public void TheReadingCarriesItsUnitAndPrefix()
    {
        var probe = new SignalProbe("I", new Resistor(1).A, Color.Yellow) { Kind = ProbeKind.Current };

        probe.LastValue = 3.03e-3;
        Assert.Contains("mA", probe.Reading);
        Assert.Equal("A", probe.Unit);

        probe.Kind = ProbeKind.Voltage;
        probe.LastValue = 5.0;
        Assert.Equal("V", probe.Unit);
        Assert.Contains("V", probe.Reading);
    }

    /// <summary>Switching what a probe measures throws away history that is now in wrong units.</summary>
    [Fact]
    public void ChangingTheProbeKindClearsTheTrace()
    {
        var probe = new SignalProbe("P", new Resistor(1).A, Color.Yellow);
        probe.Record(0.0, 5.0);
        probe.Record(1e-6, 5.0);
        Assert.Equal(2, probe.HistoryBuffer.Count);

        probe.Kind = ProbeKind.Current;

        Assert.Empty(probe.HistoryBuffer);
    }
}
