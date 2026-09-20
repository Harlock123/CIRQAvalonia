using Cirq.Components.Ics;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// Where the output stops, over every combination of rails and headroom worth trying.
/// <para>
/// A matrix rather than a handful of circuits, because this is the part of the macromodel that has
/// been wrong twice, and both times a symmetric part on a split supply hid it completely. The
/// cases that matter are the lopsided ones: a part that reaches one rail and not the other, on a
/// supply that is itself not centred on ground, asked for a voltage it cannot produce.
/// </para>
/// </summary>
public class OpAmpSwingTests
{
    public static IEnumerable<object[]> Cases()
    {
        (string Name, double Pos, double Neg)[] supplies =
        [
            ("+-15", 15, -15), ("+-5", 5, -5), ("0..5", 5, 0), ("0..12", 12, 0),
            ("+15/-5", 15, -5), ("+12/-3", 12, -3), ("+3/-12", 3, -12),
        ];

        // Symmetric, rail-to-rail, and lopsided both ways round.
        (double Hi, double Lo)[] headrooms =
        [
            (1.5, 1.5), (0.025, 0.025), (1.5, 0.02), (0.02, 1.5), (0.7, 0.02), (2.0, 0.1), (0.1, 2.0),
        ];

        foreach (var (hp, hn) in headrooms)
        foreach (var s in supplies)
        {
            var hi = s.Pos - hp;
            var lo = s.Neg + hn;
            if (hi <= lo) continue;

            var model = OpAmpModel.Lm741 with { OutputSwingHeadroom = hp, NegativeSwingHeadroom = hn };
            var span = hi - lo;

            // Mid-window, near each end of it, and well outside it in both directions.
            foreach (var asked in new[] { (hi + lo) * 0.5, lo + (0.1 * span), hi - (0.1 * span), s.Pos + 2.0, s.Neg - 2.0 })
                yield return [model, $"{hp}/{hn} on {s.Name}", s.Pos, s.Neg, asked, Math.Clamp(asked, lo, hi)];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void AFollowerStopsAtTheRails(
        OpAmpModel model, string label, double pos, double neg, double asked, double expected)
    {
        var circuit = new Circuit();
        var vp = circuit.Add(new DcVoltageSource(pos));
        var vn = circuit.Add(new DcVoltageSource(neg));
        var signal = circuit.Add(new DcVoltageSource(asked));
        var amp = circuit.Add(new OperationalAmplifier(model));
        var load = circuit.Add(new Resistor(100e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(vp.Negative, gnd.Pin);
        circuit.Connect(vn.Negative, gnd.Pin);
        circuit.Connect(signal.Negative, gnd.Pin);
        circuit.Connect(amp.PositiveSupply, vp.Positive);
        circuit.Connect(amp.NegativeSupply, vn.Positive);
        circuit.Connect(amp.NonInverting, signal.Positive);
        circuit.Connect(amp.Output, amp.Inverting);
        circuit.Connect(amp.Output, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        Assert.True(Math.Abs(sim.NodeVoltage(amp.Output) - expected) < 0.05,
            $"{label}, asked for {asked:0.00} V: expected {expected:0.000}, got {sim.NodeVoltage(amp.Output):0.000}");
    }

    /// <summary>
    /// How long it takes to come back from being driven hard into a rail.
    /// <para>
    /// This is the test that pins the gain node down. Nothing in the macromodel bounds that node
    /// by itself: a saturated input stage pushes its whole tail current into a gigaohm, which as
    /// an equilibrium is several kilovolts. Let it get there and the amplifier still <i>looks</i>
    /// right in the DC answer — the output is at the rail either way — but the next time the input
    /// changes its mind, the node has to slew all the way back at the datasheet slew rate, and a
    /// part that should recover in a microsecond takes the better part of a second.
    /// </para>
    /// </summary>
    [Fact]
    public void ItComesOutOfSaturationPromptly()
    {
        var circuit = new Circuit();
        var pos = circuit.Add(new DcVoltageSource(15));
        var neg = circuit.Add(new DcVoltageSource(15));
        var gnd = circuit.Add(new Ground());
        var u = circuit.Add(new OpAmp741());

        // Open loop and well overdriven, so it slams into a rail, sits there for most of the half
        // cycle, and then has to travel the whole way back when the input changes sign.
        var input = circuit.Add(new FunctionGenerator(Waveform.Square, 1e3, 4.0) { EdgeTime = 1e-9 });

        circuit.Connect(pos.Negative, gnd.Pin);
        circuit.Connect(neg.Positive, gnd.Pin);
        circuit.Connect(u.PositiveSupply, pos.Positive);
        circuit.Connect(u.NegativeSupply, neg.Negative);
        circuit.Connect(input.Return, gnd.Pin);
        circuit.Connect(input.Output, u.NonInverting);
        circuit.Connect(u.Inverting, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 100e-9, MaxTimeStep = 100e-9 });
        sim.Reset();
        sim.SolveOperatingPoint();

        // Sit against the rail long enough that an unbounded gain node would have climbed into the
        // hundreds of volts.
        while (sim.NodeVoltage(u.Output) < 13.0 && sim.Time < 2e-3) sim.Step();
        Assert.True(sim.NodeVoltage(u.Output) > 13.0, $"should reach the top rail, not {sim.NodeVoltage(u.Output):0.00}");

        var parked = sim.Time;
        while (sim.Time - parked < 300e-6 && sim.NodeVoltage(input.Output) > 0) sim.Step();

        // Then wait for the falling edge and time the crossing.
        while (sim.NodeVoltage(input.Output) > 0) sim.Step();

        var edge = sim.Time;
        while (sim.NodeVoltage(u.Output) > 0 && sim.Time - edge < 5e-3) sim.Step();

        var recovery = sim.Time - edge;

        // 27 V of swing at 0.5 V/us is 54 us, and that is all it should be: the gain node starts
        // at the rail, not somewhere out in the kilovolts.
        Assert.True(recovery < 100e-6, $"took {recovery * 1e6:0} us to come out of saturation");
    }
}
