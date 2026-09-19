using Cirq.Components.Digital;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class JunctionFetTests
{
    /// <summary>A JFET with a set gate-source voltage and enough drain volts to saturate it.</summary>
    private static (CircuitSimulator Sim, JunctionFet Fet) Biased(double vgs, double vdd = 15.0,
        JfetModel? model = null)
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(vdd));
        var gate = circuit.Add(new DcVoltageSource(vgs));
        var fet = circuit.Add(new JunctionFet(model));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, fet.Drain);
        circuit.Connect(fet.Source, gnd.Pin);
        circuit.Connect(gate.Negative, gnd.Pin);
        circuit.Connect(gate.Positive, fet.Gate);

        var sim = new CircuitSimulator(circuit);
        sim.SolveOperatingPoint();
        return (sim, fet);
    }

    /// <summary>
    /// The thing that separates a JFET from every MOSFET in the library: it is a depletion device,
    /// so with the gate at the source it is fully on. Wiring one expecting it to start off is the
    /// usual first surprise.
    /// </summary>
    [Fact]
    public void WithNoGateDriveItConductsItsFullSaturationCurrent()
    {
        var (_, fet) = Biased(vgs: 0.0);

        Assert.Equal(fet.Model.SaturationCurrent, Math.Abs(fet.DrainCurrent),
            fet.Model.SaturationCurrent * 0.2);
        Assert.True(fet.IsConducting);
    }

    /// <summary>The square law: Id = Idss·(1 − Vgs/Vp)², which is the curve on the datasheet.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.75)]
    [InlineData(-1.5)]
    [InlineData(-2.25)]
    public void TheDrainCurrentFollowsTheSquareLaw(double vgs)
    {
        var model = JfetModel.J2N3819;         // Idss 10 mA, pinch-off 3 V
        var (_, fet) = Biased(vgs, model: model);

        var ratio = 1.0 + (vgs / model.PinchOffVoltage);
        var expected = model.SaturationCurrent * ratio * ratio;

        // Channel-length modulation lifts it a little above the ideal square law.
        Assert.Equal(expected, Math.Abs(fet.DrainCurrent), Math.Max(expected * 0.25, 50e-6));
    }

    [Fact]
    public void AtPinchOffItStopsConducting()
    {
        var (_, fet) = Biased(vgs: -JfetModel.J2N3819.PinchOffVoltage - 0.5);

        Assert.True(Math.Abs(fet.DrainCurrent) < 1e-6, $"{fet.DrainCurrent * 1e3:0.000} mA still flowing");
        Assert.False(fet.IsConducting);
    }

    /// <summary>The reason to use one: a reverse-biased gate junction draws essentially nothing.</summary>
    [Fact]
    public void AReverseBiasedGateDrawsAlmostNoCurrent()
    {
        var (_, fet) = Biased(vgs: -1.5);

        Assert.True(Math.Abs(fet.GateCurrent) < 1e-9, $"{fet.GateCurrent * 1e9:0.0} nA of gate current");
        Assert.Empty(fet.Violations);
    }

    /// <summary>
    /// Driving the gate positive on an N-channel part forward-biases the junction, and the device
    /// stops being a FET and starts being a diode. Worth saying so.
    /// </summary>
    [Fact]
    public void AForwardBiasedGateIsReported()
    {
        var (_, fet) = Biased(vgs: 1.0);

        Assert.True(fet.IsGateForwardBiased);
        Assert.Contains("forward-biased", string.Join(" | ", fet.Violations));
    }

    /// <summary>
    /// The drain current has to cross the triode/saturation boundary without a step in it. It did
    /// not once: channel modulation was applied to the saturation branch only, so the current
    /// jumped about three percent at <c>vds = overdrive</c>. A discontinuity is the one thing
    /// Newton cannot walk down, and an amplifier whose drain dipped into triode on startup hunted
    /// either side of the boundary until the solver gave up.
    /// <para>
    /// The slope is allowed to change here — that is what the two regions mean. What is measured
    /// is the step across the join, against the slope on either side of it.
    /// </para>
    /// </summary>
    [Fact]
    public void TheDrainCurrentIsSmoothAcrossTheTriodeBoundary()
    {
        var model = JfetModel.J2N3819;      // pinch-off 3 V, so at Vgs = 0 the boundary is 3 V
        const double boundary = 3.0;
        const double step = 0.002;

        double At(double vds) => Math.Abs(Biased(vgs: 0.0, vdd: vds, model: model).Fet.DrainCurrent);

        var below = At(boundary - step);
        var above = At(boundary + step);

        // What the triode side is doing just before the join, as a scale to judge the step by.
        var triodeSlope = Math.Abs(At(boundary - step) - At(boundary - (3 * step)));

        Assert.True(Math.Abs(above - below) <= (triodeSlope * 2.0) + 1e-6,
            $"the current steps {Math.Abs(above - below) * 1e6:0.0} uA across the boundary while " +
            $"moving {triodeSlope * 1e6:0.0} uA per equivalent step beside it — the two branches " +
            "do not meet");
    }

    [Fact]
    public void APChannelPartMirrorsTheNChannelOne()
    {
        // Same magnitudes, opposite polarity throughout.
        var (_, p) = Biased(vgs: 0.0, vdd: -15.0, model: JfetModel.J2N5460);

        Assert.Equal(JfetModel.J2N5460.SaturationCurrent, Math.Abs(p.DrainCurrent),
            JfetModel.J2N5460.SaturationCurrent * 0.25);
        Assert.True(p.DrainCurrent < 0, "a P-channel part's drain current runs the other way");
    }
}

public class Cmos4000Tests
{
    /// <summary>Drives a CMOS input cleanly from a rail rather than from a TTL part.</summary>
    private static DcVoltageSource Level(Circuit circuit, Ground gnd, bool high)
    {
        var source = circuit.Add(new DcVoltageSource(high ? 5.0 : 0.0));
        circuit.Connect(source.Negative, gnd.Pin);
        return source;
    }

    private static (CircuitSimulator Sim, Ic4017 Counter, ClockSource Clock) Counter()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var counter = circuit.Add(new Ic4017());
        var clock = circuit.Add(new ClockSource(1e3) { Levels = LogicLevels.Cmos5V });
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, counter.Vcc);
        circuit.Connect(counter.Gnd, gnd.Pin);
        circuit.Connect(clock.Out, counter.Clock);
        circuit.Connect(counter.ClockInhibit, gnd.Pin);
        circuit.Connect(counter.Reset, gnd.Pin);

        // Every output needs somewhere to go.
        foreach (var output in counter.Outputs.Append(counter.CarryOut))
        {
            var load = circuit.Add(new Resistor(100e3));
            circuit.Connect(output, load.A);
            circuit.Connect(load.B, gnd.Pin);
        }

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        // Half a period, so that every later one-period step is sampled mid-period. Sampling on
        // the period boundary puts each sample exactly on a clock edge, and whether it has been
        // taken yet is then a matter of floating-point luck.
        sim.Run(0.5e-3);
        return (sim, counter, clock);
    }

    /// <summary>
    /// The 4017's selling point over a 7490: it decodes for you, so exactly one output is high and
    /// it walks along them. Ten LEDs, ten pins, no decoder.
    /// </summary>
    [Fact]
    public void ExactlyOneOutputIsHighAtATime()
    {
        var (sim, counter, _) = Counter();

        for (var step = 0; step < 12; step++)
        {
            sim.Run(1e-3);

            var high = counter.Outputs.Count(o => sim.NodeVoltage(o) > 2.5);
            Assert.Equal(1, high);
        }
    }

    [Fact]
    public void ItWalksAlongItsOutputsAndWrapsAtTen()
    {
        var (sim, counter, _) = Counter();

        var seen = new List<int>();
        for (var step = 0; step < 12; step++)
        {
            sim.Run(1e-3);
            seen.Add(counter.Count);
        }

        // Consecutive, wrapping back to zero after nine.
        for (var i = 1; i < seen.Count; i++)
            Assert.Equal((seen[i - 1] + 1) % 10, seen[i]);

        Assert.Contains(0, seen);
        Assert.Contains(9, seen);
    }

    [Fact]
    public void CarryOutIsHighForTheFirstHalfOfTheCycle()
    {
        var (sim, counter, _) = Counter();

        for (var step = 0; step < 12; step++)
        {
            sim.Run(1e-3);

            var carryHigh = sim.NodeVoltage(counter.CarryOut) > 2.5;
            Assert.Equal(counter.Count < 5, carryHigh);
        }
    }

    [Fact]
    public void HoldingResetHighKeepsItAtZero()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var counter = circuit.Add(new Ic4017());
        var clock = circuit.Add(new ClockSource(1e3) { Levels = LogicLevels.Cmos5V });
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, counter.Vcc);
        circuit.Connect(counter.Gnd, gnd.Pin);
        circuit.Connect(clock.Out, counter.Clock);
        circuit.Connect(counter.ClockInhibit, gnd.Pin);
        circuit.Connect(counter.Reset, rail.Positive);        // held high

        foreach (var output in counter.Outputs.Append(counter.CarryOut))
        {
            var load = circuit.Add(new Resistor(100e3));
            circuit.Connect(output, load.A);
            circuit.Connect(load.B, gnd.Pin);
        }

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(10e-3);

        Assert.Equal(0, counter.Count);
    }

    // ---- 4511 ------------------------------------------------------------

    private static (CircuitSimulator Sim, Ic4511 Decoder) Decoder(
        int value, bool lampTest = false, bool blank = false, bool latch = false)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var decoder = circuit.Add(new Ic4511());
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, decoder.Vcc);
        circuit.Connect(decoder.Gnd, gnd.Pin);

        var inputs = new[] { decoder.A, decoder.B, decoder.C, decoder.D };
        for (var bit = 0; bit < 4; bit++)
        {
            var level = Level(circuit, gnd, (value & (1 << bit)) != 0);
            circuit.Connect(level.Positive, inputs[bit]);
        }

        // Lamp test and blanking are active low, so they idle high.
        circuit.Connect(Level(circuit, gnd, !lampTest).Positive, decoder.LampTest);
        circuit.Connect(Level(circuit, gnd, !blank).Positive, decoder.Blanking);
        circuit.Connect(Level(circuit, gnd, latch).Positive, decoder.LatchEnable);

        foreach (var segment in decoder.Segments)
        {
            var load = circuit.Add(new Resistor(100e3));
            circuit.Connect(segment, load.A);
            circuit.Connect(load.B, gnd.Pin);
        }

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1e-3);
        return (sim, decoder);
    }

    private static string Pattern(CircuitSimulator sim, Ic4511 decoder) =>
        string.Concat(decoder.Segments.Select(s => sim.NodeVoltage(s) > 2.5 ? '1' : '0'));

    /// <summary>
    /// The glyph table, and the difference from a 7447: these outputs are active high, because
    /// this part sources current into a common-cathode display rather than sinking from a
    /// common-anode one.
    /// </summary>
    [Theory]
    [InlineData(0, "1111110")]
    [InlineData(1, "0110000")]
    [InlineData(2, "1101101")]
    [InlineData(4, "0110011")]
    [InlineData(8, "1111111")]
    public void ItDecodesBcdToTheRightSegments(int value, string expected)
    {
        var (sim, decoder) = Decoder(value);

        Assert.Equal(expected, Pattern(sim, decoder));
        Assert.Equal(value, decoder.DisplayedValue);
    }

    [Fact]
    public void ValuesAboveNineBlankTheDisplay()
    {
        // A 7447 shows odd glyphs here; a 4511 shows nothing, which is the datasheet behaviour.
        var (sim, decoder) = Decoder(12);

        Assert.Equal("0000000", Pattern(sim, decoder));
    }

    [Fact]
    public void LampTestLightsEverySegmentAndBeatsBlanking()
    {
        var (sim, decoder) = Decoder(3, lampTest: true, blank: true);

        Assert.Equal("1111111", Pattern(sim, decoder));
    }

    [Fact]
    public void BlankingTurnsEverythingOff()
    {
        var (sim, decoder) = Decoder(8, blank: true);

        Assert.Equal("0000000", Pattern(sim, decoder));
    }

    // ---- 4066 ------------------------------------------------------------

    private static (CircuitSimulator Sim, Ic4066 Switch, Resistor Load) AnalogSwitch(bool closed)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var signal = circuit.Add(new DcVoltageSource(2.0));
        var ic = circuit.Add(new Ic4066());
        var load = circuit.Add(new Resistor(10e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, ic.Vcc);
        circuit.Connect(ic.Gnd, gnd.Pin);

        var (a, b) = ic.Switch(0);
        circuit.Connect(signal.Negative, gnd.Pin);
        circuit.Connect(signal.Positive, a);
        circuit.Connect(b, load.A);
        circuit.Connect(load.B, gnd.Pin);

        circuit.Connect(Level(circuit, gnd, closed).Positive, ic.Control(0));

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1e-3);
        return (sim, ic, load);
    }

    /// <summary>
    /// A bilateral switch passes an analog signal rather than a logic level: 2 V in gives very
    /// nearly 2 V out, not a rail.
    /// </summary>
    [Fact]
    public void ClosedItPassesTheAnalogSignalThrough()
    {
        var (sim, ic, load) = AnalogSwitch(closed: true);

        Assert.True(ic.IsClosed(0));
        Assert.Equal(2.0, sim.NodeVoltage(load.A), 0.1);
    }

    [Fact]
    public void OpenItPassesNothing()
    {
        var (sim, ic, load) = AnalogSwitch(closed: false);

        Assert.False(ic.IsClosed(0));
        Assert.True(sim.NodeVoltage(load.A) < 0.01,
            $"{sim.NodeVoltage(load.A):0.000} V leaked through an open switch");
    }

    /// <summary>A closed switch is tens of ohms, not a short, and that loses signal into a load.</summary>
    [Fact]
    public void ItsOnResistanceIsRealEnoughToDivideWithTheLoad()
    {
        var (sim, ic, load) = AnalogSwitch(closed: true);

        // 2 V across 80 R + 10 k should land a touch below 2 V, by the divider ratio.
        var expected = 2.0 * 10e3 / (10e3 + ic.OnResistance);
        Assert.Equal(expected, sim.NodeVoltage(load.A), 0.01);
        Assert.True(sim.NodeVoltage(load.A) < 2.0, "a real switch resistance must lose something");
    }

    [Fact]
    public void TheFourSwitchesAreIndependent()
    {
        var ic = new Ic4066();

        var pins = Enumerable.Range(0, 4)
            .SelectMany(i => new[] { ic.Switch(i).A, ic.Switch(i).B, ic.Control(i) })
            .ToList();

        Assert.Equal(pins.Count, pins.Distinct().Count());
    }
}
