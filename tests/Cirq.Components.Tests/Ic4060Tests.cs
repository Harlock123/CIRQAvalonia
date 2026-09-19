using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class Ic4060Tests
{
    /// <summary>
    /// The datasheet's RC oscillator: Rt from REXT, Ct from CEXT and Rs from RS, all three meeting
    /// at one node.
    /// </summary>
    private static (CircuitSimulator Sim, Ic4060 Ic, DcVoltageSource Reset) Oscillator(
        double rt, double ct, double rs = 0)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var ic = circuit.Add(new Ic4060());
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, ic.Vcc);
        circuit.Connect(ic.Gnd, gnd.Pin);

        var reset = CmosRig.Level(circuit, gnd, false);
        circuit.Connect(reset.Positive, ic.MasterReset);

        var timingResistor = circuit.Add(new Resistor(rt));
        var timingCapacitor = circuit.Add(new Capacitor(ct));
        var isolation = circuit.Add(new Resistor(rs > 0 ? rs : 5.0 * rt));

        circuit.Connect(ic.ResistorPin, timingResistor.A);
        circuit.Connect(ic.CapacitorPin, timingCapacitor.A);
        circuit.Connect(ic.ClockOscillator, isolation.A);

        // The free ends all meet at the timing node.
        circuit.Connect(timingResistor.B, timingCapacitor.B);
        circuit.Connect(timingCapacitor.B, isolation.B);

        foreach (var output in ic.Outputs) CmosRig.Load(circuit, gnd, output);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, ic, reset);
    }

    /// <summary>
    /// The one that matters: nothing here sets a frequency. The part contains two inverters and
    /// the test hangs a resistor and a capacitor off them, so whatever comes out is what the
    /// network does — and what it does is the datasheet's <c>f = 1/(2.3·Rt·Ct)</c>.
    /// </summary>
    [Theory]
    [InlineData(10e3, 10e-9)]
    [InlineData(20e3, 10e-9)]
    [InlineData(10e3, 20e-9)]
    public void TheOnChipOscillatorRunsAtTheDatasheetFrequency(double rt, double ct)
    {
        var (sim, ic, _) = Oscillator(rt, ct);

        // Every oscillator cycle advances the counter once, so the count is the cycle tally and
        // no sampling is needed to measure the frequency.
        const double window = 20e-3;
        sim.Run(window);

        var measured = ic.Count / window;
        var expected = 1.0 / (2.3 * rt * ct);

        Assert.InRange(measured, expected * 0.95, expected * 1.05);
    }

    /// <summary>
    /// Proof the frequency is not announced but computed: doubling the capacitor halves it, and
    /// the two runs share every other value.
    /// </summary>
    [Fact]
    public void DoublingTheTimingCapacitorHalvesTheFrequency()
    {
        var (fast, fastIc, _) = Oscillator(10e3, 10e-9);
        var (slow, slowIc, _) = Oscillator(10e3, 20e-9);

        fast.Run(20e-3);
        slow.Run(20e-3);

        Assert.InRange((double)fastIc.Count / slowIc.Count, 1.95, 2.05);
    }

    /// <summary>
    /// The outputs run Q4 to Q10 and then jump to Q12 — stages one to three and eleven never reach
    /// a pin, so the divisions available start at 16 and skip 2048.
    /// </summary>
    [Fact]
    public void ItBringsOutTenStagesWithTwoGapsInThem()
    {
        var ic = new Ic4060();

        Assert.Equal(10, ic.Outputs.Count);
        Assert.Equal([4, 5, 6, 7, 8, 9, 10, 12, 13, 14], Ic4060.Stages);
        Assert.Equal(["Q4", "Q5", "Q6", "Q7", "Q8", "Q9", "Q10", "Q12", "Q13", "Q14"],
            ic.Outputs.Select(t => t.Name));
    }

    /// <summary>Each brought-out stage carries its own bit of the internal count.</summary>
    [Fact]
    public void EachOutputCarriesItsOwnStageOfTheCount()
    {
        var (sim, ic, _) = Oscillator(10e3, 10e-9);

        // Long enough for the low stages to have moved several times over.
        sim.Run(20e-3);

        for (var i = 0; i < ic.Outputs.Count; i++)
        {
            var expected = (ic.Count & (1 << (Ic4060.Stages[i] - 1))) != 0;
            Assert.Equal(expected, sim.NodeVoltage(ic.Outputs[i]) > 2.5);
        }

        // Q4 is the first stage out, so it has toggled many times over this window.
        Assert.True(ic.Count > 16, "the counter should have passed the first available stage");
    }

    /// <summary>
    /// Master reset is active high and stops the oscillator as well as clearing the count, so a
    /// floating MR pin gets you a chip that does nothing at all.
    /// </summary>
    [Fact]
    public void MasterResetIsActiveHighAndStopsTheOscillator()
    {
        var (sim, ic, reset) = Oscillator(10e3, 10e-9);

        sim.Run(5e-3);
        Assert.True(ic.Count > 0, "it should have been running");

        reset.Voltage = 5.0;
        sim.Run(1e-3);
        Assert.Equal(0, ic.Count);

        // Held in reset it stays at zero rather than carrying on from there.
        sim.Run(5e-3);
        Assert.Equal(0, ic.Count);

        foreach (var output in ic.Outputs) Assert.True(sim.NodeVoltage(output) < 2.5);

        // ...and it picks up again once reset is released.
        reset.Voltage = 0.0;
        sim.Run(5e-3);
        Assert.True(ic.Count > 0, "releasing reset should restart the oscillator");
    }

    /// <summary>
    /// The oscillator can be left out entirely: drive RS yourself with REXT and CEXT floating and
    /// it is a plain fourteen-stage counter. It counts on the falling edge.
    /// </summary>
    [Fact]
    public void ItCanBeClockedExternallyOnRsAndCountsOnTheFallingEdge()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var ic = circuit.Add(new Ic4060());
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, ic.Vcc);
        circuit.Connect(ic.Gnd, gnd.Pin);

        var clock = CmosRig.Level(circuit, gnd, false);
        var reset = CmosRig.Level(circuit, gnd, false);
        circuit.Connect(clock.Positive, ic.ClockOscillator);
        circuit.Connect(reset.Positive, ic.MasterReset);

        foreach (var output in ic.Outputs) CmosRig.Load(circuit, gnd, output);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        void Settle()
        {
            sim.Run(1e-6);
            sim.Run(1e-6);
        }

        clock.Voltage = 5.0;
        Settle();
        Assert.Equal(0, ic.Count);      // rising edge: nothing

        clock.Voltage = 0.0;
        Settle();
        Assert.Equal(1, ic.Count);      // falling edge: counts

        void Pulse()
        {
            clock.Voltage = 5.0;
            Settle();
            clock.Voltage = 0.0;
            Settle();
        }

        // Q4 is the fourth stage, so it goes high half way through its own period of sixteen.
        for (var i = 0; i < 7; i++) Pulse();
        Assert.Equal(8, ic.Count);
        Assert.True(sim.NodeVoltage(ic.Outputs[0]) > 2.5, "Q4 should be high after eight counts");

        // ...and back low at sixteen, which is what dividing by sixteen means.
        for (var i = 0; i < 8; i++) Pulse();
        Assert.Equal(16, ic.Count);
        Assert.True(sim.NodeVoltage(ic.Outputs[0]) < 2.5, "Q4 should have completed a full cycle");
    }
}
