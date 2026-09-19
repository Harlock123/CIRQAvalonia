using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>Shared rigging for the 4000-series parts, which want rail-level drive rather than TTL.</summary>
internal static class CmosRig
{
    /// <summary>Drives a CMOS input cleanly from a rail rather than from a TTL part.</summary>
    public static DcVoltageSource Level(Circuit circuit, Ground gnd, bool high)
    {
        var source = circuit.Add(new DcVoltageSource(high ? 5.0 : 0.0));
        circuit.Connect(source.Negative, gnd.Pin);
        return source;
    }

    /// <summary>Gives an output somewhere to drive, so it is not left hanging.</summary>
    public static void Load(Circuit circuit, Ground gnd, Terminal pin, double ohms = 100e3)
    {
        var load = circuit.Add(new Resistor(ohms));
        circuit.Connect(pin, load.A);
        circuit.Connect(load.B, gnd.Pin);
    }
}

public class Cmos4000GateTests
{
    /// <summary>Feeds one gate of a quad package and reads what it drives.</summary>
    private static bool Evaluate<T>(bool a, bool b, int gate = 0) where T : QuadTwoInputCmosGateIc, new()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var ic = circuit.Add(new T());
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, ic.Vcc);
        circuit.Connect(ic.Gnd, gnd.Pin);

        var (pinA, pinB, pinY) = ic.Gate(gate);
        circuit.Connect(CmosRig.Level(circuit, gnd, a).Positive, pinA);
        circuit.Connect(CmosRig.Level(circuit, gnd, b).Positive, pinB);
        CmosRig.Load(circuit, gnd, pinY);

        // Anything the gate does not drive still needs somewhere to go.
        for (var other = 0; other < ic.GateCount; other++)
        {
            if (other == gate) continue;

            var (otherA, otherB, otherY) = ic.Gate(other);
            circuit.Connect(CmosRig.Level(circuit, gnd, false).Positive, otherA);
            circuit.Connect(CmosRig.Level(circuit, gnd, false).Positive, otherB);
            CmosRig.Load(circuit, gnd, otherY);
        }

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1e-6);

        return sim.NodeVoltage(pinY) > 2.5;
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void The4011IsAQuadNand(bool a, bool b, bool y) => Assert.Equal(y, Evaluate<Ic4011>(a, b));

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public void The4001IsAQuadNor(bool a, bool b, bool y) => Assert.Equal(y, Evaluate<Ic4001>(a, b));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void The4081IsAQuadAnd(bool a, bool b, bool y) => Assert.Equal(y, Evaluate<Ic4081>(a, b));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void The4071IsAQuadOr(bool a, bool b, bool y) => Assert.Equal(y, Evaluate<Ic4071>(a, b));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void The4070IsAQuadXor(bool a, bool b, bool y) => Assert.Equal(y, Evaluate<Ic4070>(a, b));

    /// <summary>All four gates work, not just the first — the pin map has to be right for each.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryGateInThePackageWorks(int gate)
    {
        Assert.True(Evaluate<Ic4011>(false, false, gate));
        Assert.False(Evaluate<Ic4011>(true, true, gate));
    }

    /// <summary>
    /// The trap this part sets. A 7400 puts gate two on pins 4 and 5 driving 6; a 4011 puts it on
    /// 5 and 6 driving 4. Dropping one into a board laid out for the other gets you two working
    /// gates and two that do nothing sensible.
    /// </summary>
    [Fact]
    public void TheCmosQuadPinoutIsNotTheTtlOne()
    {
        var cmos = new Ic4011();
        var ttl = new Ic7400();

        static string Pins(IReadOnlyList<Terminal> inputs, Terminal output) =>
            $"{string.Join(",", inputs.Select(t => t.Id))}->{output.Id}";

        // Gates one and four land in the same places on both parts...
        Assert.Equal(Pins(ttl.GateInputs(0), ttl.GateOutput(0)), Pins(cmos.GateInputs(0), cmos.GateOutput(0)));

        // ...and the two in the middle do not.
        Assert.NotEqual(Pins(ttl.GateInputs(1), ttl.GateOutput(1)), Pins(cmos.GateInputs(1), cmos.GateOutput(1)));
        Assert.NotEqual(Pins(ttl.GateInputs(2), ttl.GateOutput(2)), Pins(cmos.GateInputs(2), cmos.GateOutput(2)));

        Assert.Equal("p5,p6->p4", Pins(cmos.GateInputs(1), cmos.GateOutput(1)));
        Assert.Equal("p8,p9->p10", Pins(cmos.GateInputs(2), cmos.GateOutput(2)));
    }

    /// <summary>The CMOS NOR shares the NAND's pinout, where the TTL 7402 moves its outputs.</summary>
    [Fact]
    public void TheCmosNorSharesTheNandPinoutUnlikeTtl()
    {
        var nand = new Ic4011();
        var nor = new Ic4001();

        for (var gate = 0; gate < 4; gate++)
            Assert.Equal(nand.GateOutput(gate).Id, nor.GateOutput(gate).Id);

        Assert.NotEqual(new Ic7400().GateOutput(1).Id, new Ic7402().GateOutput(1).Id);
    }

    [Fact]
    public void The4069IsAHexInverter()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var ic = circuit.Add(new Ic4069());
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, ic.Vcc);
        circuit.Connect(ic.Gnd, gnd.Pin);

        // Alternate the drive so both directions are covered across the six gates.
        for (var i = 0; i < 6; i++)
        {
            var (a, y) = ic.Inverter(i);
            circuit.Connect(CmosRig.Level(circuit, gnd, i % 2 == 0).Positive, a);
            CmosRig.Load(circuit, gnd, y);
        }

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1e-6);

        for (var i = 0; i < 6; i++)
        {
            var (_, y) = ic.Inverter(i);
            Assert.Equal(i % 2 != 0, sim.NodeVoltage(y) > 2.5);
        }
    }
}

public class Ic4093Tests
{
    /// <summary>One gate with both inputs tied, so it behaves as a Schmitt inverter.</summary>
    private static (CircuitSimulator Sim, Ic4093 Ic, DcVoltageSource Input) Rig()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var input = circuit.Add(new DcVoltageSource(0.0));
        var ic = circuit.Add(new Ic4093());
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, ic.Vcc);
        circuit.Connect(ic.Gnd, gnd.Pin);
        circuit.Connect(input.Negative, gnd.Pin);

        var (a, b, y) = ic.Gate(0);
        circuit.Connect(input.Positive, a);
        circuit.Connect(input.Positive, b);
        CmosRig.Load(circuit, gnd, y, 10e3);

        for (var other = 1; other < 4; other++)
        {
            var (otherA, otherB, otherY) = ic.Gate(other);
            circuit.Connect(gnd.Pin, otherA);
            circuit.Connect(gnd.Pin, otherB);
            CmosRig.Load(circuit, gnd, otherY);
        }

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, ic, input);
    }

    /// <summary>
    /// The difference from a 4011: sweeping the input up switches at the upper threshold and
    /// sweeping it back down switches at the lower one, so there is a band in the middle where the
    /// gate keeps what it last decided.
    /// </summary>
    [Fact]
    public void ItSwitchesAtDifferentPointsGoingUpAndComingDown()
    {
        var (sim, ic, input) = Rig();
        var (_, _, y) = ic.Gate(0);

        double SweepTo(double volts)
        {
            input.Voltage = volts;

            // Twice: the first solve is where the gate sees the new input and queues its
            // transition one propagation delay out, and the second lets that land.
            sim.Run(1e-6);
            sim.Run(1e-6);
            return sim.NodeVoltage(y);
        }

        SweepTo(0.0);
        Assert.True(SweepTo(2.5) > 4.0, "a rising input below VT+ should not have switched yet");
        Assert.True(SweepTo(3.2) < 1.0, "past VT+ the output should have gone low");

        Assert.True(SweepTo(2.5) < 1.0, "a falling input above VT- should stay switched");
        Assert.True(SweepTo(1.5) > 4.0, "past VT- the output should have returned high");
    }

    [Fact]
    public void ItStillBehavesAsANandWhenTheInputsDiffer()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var ic = circuit.Add(new Ic4093());
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, ic.Vcc);
        circuit.Connect(ic.Gnd, gnd.Pin);

        var (a, b, y) = ic.Gate(0);
        circuit.Connect(CmosRig.Level(circuit, gnd, true).Positive, a);
        circuit.Connect(CmosRig.Level(circuit, gnd, false).Positive, b);
        CmosRig.Load(circuit, gnd, y);

        for (var other = 1; other < 4; other++)
        {
            var (otherA, otherB, otherY) = ic.Gate(other);
            circuit.Connect(gnd.Pin, otherA);
            circuit.Connect(gnd.Pin, otherB);
            CmosRig.Load(circuit, gnd, otherY);
        }

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1e-6);

        // NAND of high and low is high.
        Assert.True(sim.NodeVoltage(y) > 2.5);
    }

    /// <summary>
    /// The reason this part exists. One gate, a resistor from output back to the tied inputs and a
    /// capacitor to ground, and it runs on its own: the cap charges until it crosses VT+, the
    /// output flips and it discharges until it crosses VT-.
    /// <para>
    /// The period is the two exponentials end to end, so it can be predicted rather than recorded:
    /// <c>T = RC·[ln((VOH−VT−)/(VOH−VT+)) + ln((VT+−VOL)/(VT−−VOL))]</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void OneGateAResistorAndACapacitorMakeAnOscillator()
    {
        const double r = 100e3;
        const double c = 100e-9;

        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var ic = circuit.Add(new Ic4093());
        var feedback = circuit.Add(new Resistor(r));
        var timing = circuit.Add(new Capacitor(c));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, ic.Vcc);
        circuit.Connect(ic.Gnd, gnd.Pin);

        var (a, b, y) = ic.Gate(0);
        circuit.Connect(a, b);                  // both inputs tied: a Schmitt inverter
        circuit.Connect(y, feedback.A);
        circuit.Connect(feedback.B, a);
        circuit.Connect(a, timing.A);
        circuit.Connect(timing.B, gnd.Pin);

        for (var other = 1; other < 4; other++)
        {
            var (otherA, otherB, otherY) = ic.Gate(other);
            circuit.Connect(gnd.Pin, otherA);
            circuit.Connect(gnd.Pin, otherB);
            CmosRig.Load(circuit, gnd, otherY);
        }

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        // Count transitions over a window, which gives the frequency without caring about phase.
        const double window = 100e-3;
        const double step = 50e-6;

        var transitions = 0;
        var last = sim.NodeVoltage(y) > 2.5;

        for (var t = 0.0; t < window; t += step)
        {
            sim.Run(step);

            var now = sim.NodeVoltage(y) > 2.5;
            if (now != last) transitions++;
            last = now;
        }

        var measured = transitions / 2.0 / window;

        // The output drives through its own 50 R, which is in series with the feedback resistor.
        var tau = (r + 50.0) * c;
        var upper = ic.UpperThresholdFraction * 5.0;
        var lower = ic.LowerThresholdFraction * 5.0;
        var expected = 1.0 / (tau * (Math.Log((4.95 - lower) / (4.95 - upper))
                                     + Math.Log((upper - 0.05) / (lower - 0.05))));

        Assert.InRange(measured, expected * 0.85, expected * 1.15);
    }
}
