using Cirq.Components.Digital;
using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class ComparatorTests
{
    /// <summary>Comparator on a single 5 V supply with a pull-up on its open-collector output.</summary>
    private static (CircuitSimulator Sim, Comparator U, DcVoltageSource Input, DcVoltageSource Reference)
        SingleSupply(double inputVoltage, double referenceVoltage, ComparatorModel? model = null)
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var input = circuit.Add(new DcVoltageSource(inputVoltage));
        var reference = circuit.Add(new DcVoltageSource(referenceVoltage));
        var u = circuit.Add(new Comparator(model));
        var pullUp = circuit.Add(new Resistor(10e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(input.Negative, gnd.Pin);
        circuit.Connect(reference.Negative, gnd.Pin);
        circuit.Connect(u.PositiveSupply, supply.Positive);
        circuit.Connect(u.NegativeSupply, gnd.Pin);
        circuit.Connect(u.NonInverting, input.Positive);
        circuit.Connect(u.Inverting, reference.Positive);
        circuit.Connect(pullUp.A, supply.Positive);
        circuit.Connect(pullUp.B, u.Output);

        var settings = new SimulationSettings { TimeStep = 20e-9, MaxTimeStep = 20e-9 };
        return (new CircuitSimulator(circuit, settings), u, input, reference);
    }

    [Fact]
    public void OutputReleasesWhenTheNonInvertingInputIsHigher()
    {
        var (sim, u, _, _) = SingleSupply(3.0, 1.0);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(2e-6);

        Assert.True(u.IsOutputHigh);
        // Released output, so the pull-up takes the pin to the supply.
        Assert.Equal(5.0, sim.NodeVoltage(u.Output), 0.05);
    }

    [Fact]
    public void OutputPullsLowWhenTheInvertingInputIsHigher()
    {
        var (sim, u, _, _) = SingleSupply(1.0, 3.0);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(2e-6);

        Assert.False(u.IsOutputHigh);
        Assert.InRange(sim.NodeVoltage(u.Output), 0.0, 0.4);
    }

    [Fact]
    public void TheOpenCollectorOutputCanBePulledToAHigherRail()
    {
        // The point of an open-collector output: a 5 V part driving a 12 V rail.
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var highRail = circuit.Add(new DcVoltageSource(12.0));
        var input = circuit.Add(new DcVoltageSource(4.0));
        var reference = circuit.Add(new DcVoltageSource(1.0));
        var u = circuit.Add(new Comparator());
        var pullUp = circuit.Add(new Resistor(4.7e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(highRail.Negative, gnd.Pin);
        circuit.Connect(input.Negative, gnd.Pin);
        circuit.Connect(reference.Negative, gnd.Pin);
        circuit.Connect(u.PositiveSupply, supply.Positive);
        circuit.Connect(u.NegativeSupply, gnd.Pin);
        circuit.Connect(u.NonInverting, input.Positive);
        circuit.Connect(u.Inverting, reference.Positive);
        circuit.Connect(pullUp.A, highRail.Positive);
        circuit.Connect(pullUp.B, u.Output);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 20e-9, MaxTimeStep = 20e-9 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(2e-6);

        Assert.Equal(12.0, sim.NodeVoltage(u.Output), 0.05);
    }

    [Fact]
    public void APushPullPartNeedsNoPullUp()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var input = circuit.Add(new DcVoltageSource(4.0));
        var reference = circuit.Add(new DcVoltageSource(1.0));
        var u = circuit.Add(new Comparator(ComparatorModel.Tlv3501));
        var load = circuit.Add(new Resistor(100e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(input.Negative, gnd.Pin);
        circuit.Connect(reference.Negative, gnd.Pin);
        circuit.Connect(u.PositiveSupply, supply.Positive);
        circuit.Connect(u.NegativeSupply, gnd.Pin);
        circuit.Connect(u.NonInverting, input.Positive);
        circuit.Connect(u.Inverting, reference.Positive);
        circuit.Connect(load.A, u.Output);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 2e-9, MaxTimeStep = 2e-9 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(200e-9);

        Assert.InRange(sim.NodeVoltage(u.Output), 4.5, 5.0);
    }

    [Fact]
    public void ACrossingSquaresUpIntoACleanDigitalEdge()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var generator = circuit.Add(new FunctionGenerator(Waveform.Sine, 1e3, 4.0) { DcOffset = 2.5 });
        var reference = circuit.Add(new DcVoltageSource(2.5));
        var u = circuit.Add(new Comparator());
        var pullUp = circuit.Add(new Resistor(10e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(reference.Negative, gnd.Pin);
        circuit.Connect(generator.Return, gnd.Pin);
        circuit.Connect(u.PositiveSupply, supply.Positive);
        circuit.Connect(u.NegativeSupply, gnd.Pin);
        circuit.Connect(u.NonInverting, generator.Output);
        circuit.Connect(u.Inverting, reference.Positive);
        circuit.Connect(pullUp.A, supply.Positive);
        circuit.Connect(pullUp.B, u.Output);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 200e-9, MaxTimeStep = 200e-9 });
        sim.Reset();
        sim.SolveOperatingPoint();

        var edges = new List<double>();
        var wasHigh = sim.NodeVoltage(u.Output) > 2.5;

        while (sim.Time < 5e-3)
        {
            sim.Step();
            var isHigh = sim.NodeVoltage(u.Output) > 2.5;
            if (isHigh && !wasHigh) edges.Add(sim.Time);
            wasHigh = isHigh;
        }

        Assert.True(edges.Count >= 4, $"Expected one rising edge per cycle, saw {edges.Count}.");
        var period = (edges[^1] - edges[0]) / (edges.Count - 1);
        Assert.Equal(1e-3, period, 5e-5);
    }

    [Fact]
    public void AnUnpoweredComparatorDrivesNothing()
    {
        var (sim, u, _, _) = SingleSupply(3.0, 1.0);
        // Rebuild with the supply collapsed.
        sim.Circuit.Components.OfType<DcVoltageSource>().First().Voltage = 0.5;
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(2e-6);

        Assert.Equal(LogicState.HighImpedance, u.GetOutputState(0));
    }
}

public class SwitchTests
{
    private static (CircuitSimulator Sim, T Device, Resistor Load) Series<T>(T device)
        where T : CircuitComponent
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(10.0));
        var load = circuit.Add(new Resistor(1e3));
        var gnd = circuit.Add(new Ground());
        circuit.Add(device);

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, load.A);
        circuit.Connect(load.B, device.Terminals[0]);
        circuit.Connect(device.Terminals[1], gnd.Pin);

        return (new CircuitSimulator(circuit), device, load);
    }

    [Fact]
    public void AnOpenSwitchBlocksAndAClosedOnePasses()
    {
        var (sim, sw, load) = Series(new ToggleSwitch());

        sim.SolveOperatingPoint();
        Assert.Equal(10.0, sim.NodeVoltage(load.B), 0.01);

        sw.IsClosed = true;
        sim.Reset();
        sim.SolveOperatingPoint();
        Assert.InRange(sim.NodeVoltage(load.B), 0.0, 0.01);
    }

    [Fact]
    public void InteractingFlipsTheSwitch()
    {
        var sw = new ToggleSwitch();
        Assert.False(sw.IsClosed);

        sw.Interact();
        Assert.True(sw.IsClosed);

        sw.Interact();
        Assert.False(sw.IsClosed);
    }

    [Fact]
    public void ANormallyOpenButtonConductsOnlyWhilePressed()
    {
        var (sim, button, load) = Series(new PushButton());

        sim.SolveOperatingPoint();
        Assert.False(button.IsConducting);
        Assert.Equal(10.0, sim.NodeVoltage(load.B), 0.01);

        button.Interact();
        sim.Reset();
        sim.SolveOperatingPoint();
        Assert.True(button.IsConducting);
        Assert.InRange(sim.NodeVoltage(load.B), 0.0, 0.01);
    }

    [Fact]
    public void ANormallyClosedButtonIsTheOtherWayRound()
    {
        var (sim, button, load) = Series(new PushButton(normallyOpen: false));

        sim.SolveOperatingPoint();
        Assert.True(button.IsConducting);

        button.Interact();
        sim.Reset();
        sim.SolveOperatingPoint();
        Assert.False(button.IsConducting);
        Assert.Equal(10.0, sim.NodeVoltage(load.B), 0.01);
    }

    [Fact]
    public void AChangeoverSwitchSelectsExactlyOneThrow()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(10.0));
        var sw = circuit.Add(new SpdtSwitch());
        var loadA = circuit.Add(new Resistor(1e3));
        var loadB = circuit.Add(new Resistor(1e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, sw.Common);
        circuit.Connect(sw.ThrowA, loadA.A);
        circuit.Connect(loadA.B, gnd.Pin);
        circuit.Connect(sw.ThrowB, loadB.A);
        circuit.Connect(loadB.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);

        sim.SolveOperatingPoint();
        Assert.Equal(10.0, sim.NodeVoltage(loadA.A), 0.02);
        Assert.InRange(sim.NodeVoltage(loadB.A), 0.0, 0.01);

        sw.Interact();
        sim.Reset();
        sim.SolveOperatingPoint();
        Assert.InRange(sim.NodeVoltage(loadA.A), 0.0, 0.01);
        Assert.Equal(10.0, sim.NodeVoltage(loadB.A), 0.02);
    }
}

public class SevenSegmentTests
{
    /// <summary>
    /// The full chain: clock -> 7490 counter -> 7447 decoder -> common-anode display, with a
    /// current-limiting resistor on every segment.
    /// </summary>
    private static (CircuitSimulator Sim, Ic7490 Counter, Ic7447 Decoder, SevenSegmentDisplay Display, ClockSource Clock)
        CounterChain(double clockHz = 10e3)
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());

        var counter = circuit.Add(new Ic7490());
        var decoder = circuit.Add(new Ic7447());
        var display = circuit.Add(new SevenSegmentDisplay(commonAnode: true));
        var clock = circuit.Add(new ClockSource(clockHz));

        circuit.Connect(supply.Negative, gnd.Pin);
        foreach (var vcc in new[] { counter.Vcc, decoder.Vcc })
            circuit.Connect(vcc, supply.Positive);
        foreach (var ground in new[] { counter.Gnd, decoder.Gnd })
            circuit.Connect(ground, gnd.Pin);

        // Counter in BCD mode with both reset pairs released.
        circuit.Connect(clock.Out, counter.ClockA);
        circuit.Connect(counter.Qa, counter.ClockB);
        foreach (var reset in new[] { counter.Reset0A, counter.Reset0B, counter.Reset9A, counter.Reset9B })
            circuit.Connect(reset, gnd.Pin);

        circuit.Connect(counter.Qa, decoder.InputA);
        circuit.Connect(counter.Qb, decoder.InputB);
        circuit.Connect(counter.Qc, decoder.InputC);
        circuit.Connect(counter.Qd, decoder.InputD);

        // Control pins inactive: no lamp test, no blanking.
        circuit.Connect(decoder.LampTest, supply.Positive);
        circuit.Connect(decoder.BlankingInput, supply.Positive);
        circuit.Connect(decoder.RippleBlankingInput, supply.Positive);

        // Common anode to the supply, each segment through its own resistor to the decoder.
        circuit.Connect(display.Common, supply.Positive);
        for (var i = 0; i < 7; i++)
        {
            var resistor = circuit.Add(new Resistor(330));
            circuit.Connect(display.Segments[i], resistor.A);
            circuit.Connect(resistor.B, decoder.Segments[i]);
        }

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 2e-8, MaxTimeStep = 2e-8 });
        return (sim, counter, decoder, display, clock);
    }

    [Theory]
    [InlineData(0, 0b1111110)]
    [InlineData(1, 0b0110000)]
    [InlineData(2, 0b1101101)]
    [InlineData(3, 0b1111001)]
    [InlineData(4, 0b0110011)]
    [InlineData(5, 0b1011011)]
    [InlineData(6, 0b1011111)]
    [InlineData(7, 0b1110000)]
    [InlineData(8, 0b1111111)]
    [InlineData(9, 0b1111011)]
    public void DecoderProducesTheStandardGlyphForEachDigit(int value, int expected)
    {
        Assert.Equal(expected, Ic7447.PatternFor(value));
    }

    [Fact]
    public void EveryDigitHasADistinctPattern()
    {
        var patterns = Enumerable.Range(0, 10).Select(Ic7447.PatternFor).ToList();
        Assert.Equal(10, patterns.Distinct().Count());
    }

    [Fact]
    public void DecoderOutputsAreActiveLow()
    {
        // Driving "1" lights only b and c, so exactly those two pins pull low.
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());
        var decoder = circuit.Add(new Ic7447());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(decoder.Vcc, supply.Positive);
        circuit.Connect(decoder.Gnd, gnd.Pin);
        circuit.Connect(decoder.InputA, supply.Positive);   // A = 1
        circuit.Connect(decoder.InputB, gnd.Pin);
        circuit.Connect(decoder.InputC, gnd.Pin);
        circuit.Connect(decoder.InputD, gnd.Pin);
        circuit.Connect(decoder.LampTest, supply.Positive);
        circuit.Connect(decoder.BlankingInput, supply.Positive);
        circuit.Connect(decoder.RippleBlankingInput, supply.Positive);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 2e-8, MaxTimeStep = 2e-8 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1e-6);

        Assert.Equal(1, decoder.DecodedValue);
        Assert.Equal(0b0110000, decoder.SegmentPattern);

        // b and c low, everything else high.
        Assert.Equal(LogicState.Low, decoder.GetOutputState(1));
        Assert.Equal(LogicState.Low, decoder.GetOutputState(2));
        // Unlit segments are released, not driven: these are open-collector sink drivers.
        Assert.Equal(LogicState.HighImpedance, decoder.GetOutputState(0));
        Assert.Equal(LogicState.HighImpedance, decoder.GetOutputState(6));
    }

    [Fact]
    public void BlankingBeatsLampTest()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());
        var decoder = circuit.Add(new Ic7447());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(decoder.Vcc, supply.Positive);
        circuit.Connect(decoder.Gnd, gnd.Pin);
        foreach (var input in new[] { decoder.InputA, decoder.InputB, decoder.InputC, decoder.InputD })
            circuit.Connect(input, gnd.Pin);
        circuit.Connect(decoder.LampTest, gnd.Pin);        // Lamp test asserted...
        circuit.Connect(decoder.BlankingInput, gnd.Pin);   // ...but blanking too.
        circuit.Connect(decoder.RippleBlankingInput, supply.Positive);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 2e-8, MaxTimeStep = 2e-8 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1e-6);

        Assert.Equal(0, decoder.SegmentPattern);
    }

    [Fact]
    public void LampTestLightsEverySegment()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());
        var decoder = circuit.Add(new Ic7447());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(decoder.Vcc, supply.Positive);
        circuit.Connect(decoder.Gnd, gnd.Pin);
        foreach (var input in new[] { decoder.InputA, decoder.InputB, decoder.InputC, decoder.InputD })
            circuit.Connect(input, gnd.Pin);
        circuit.Connect(decoder.LampTest, gnd.Pin);
        circuit.Connect(decoder.BlankingInput, supply.Positive);
        circuit.Connect(decoder.RippleBlankingInput, supply.Positive);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 2e-8, MaxTimeStep = 2e-8 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1e-6);

        Assert.Equal(0b1111111, decoder.SegmentPattern);
    }

    [Fact]
    public void ADrivenDisplayLightsTheRightSegmentsAtASensibleCurrent()
    {
        var (sim, _, decoder, display, _) = CounterChain();
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(5e-6);

        var pattern = decoder.SegmentPattern;

        for (var i = 0; i < 7; i++)
        {
            var shouldBeLit = (pattern & (1 << (6 - i))) != 0;
            if (shouldBeLit)
            {
                Assert.True(display.SegmentBrightness[i] > 0.3,
                    $"Segment {(char)('a' + i)} should be lit but reads {display.SegmentBrightness[i]:0.##}.");
                // 5 V less two diode drops across 330 R is a handful of milliamps.
                Assert.InRange(display.SegmentCurrents[i], 1e-3, 25e-3);
            }
            else
            {
                Assert.True(display.SegmentBrightness[i] < 0.05,
                    $"Segment {(char)('a' + i)} should be dark but reads {display.SegmentBrightness[i]:0.##}.");
            }
        }
    }

    [Fact]
    public void TheCounterChainWalksThroughEveryDigitZeroToNine()
    {
        var (sim, counter, _, display, clock) = CounterChain(clockHz: 20e3);
        sim.Reset();
        sim.SolveOperatingPoint();

        var seen = new List<int>();
        var wasHigh = true;

        while (sim.Time < 800e-6 && seen.Count < 14)
        {
            sim.Step();
            var isHigh = sim.NodeVoltage(clock.Out) > LogicLevels.Ttl.Vih;
            if (!isHigh && wasHigh)
            {
                // Let the ripple through the counter, decoder and display settle.
                sim.Run(4e-6);
                var digit = display.DisplayedDigit;
                if (digit is { } value && (seen.Count == 0 || seen[^1] != value)) seen.Add(value);
            }
            wasHigh = isHigh;
        }

        Assert.True(seen.Count >= 11, $"Only read {seen.Count} digits off the display: {string.Join(",", seen)}");

        var start = seen.IndexOf(0);
        Assert.True(start >= 0, $"The display never showed zero: {string.Join(",", seen)}");

        for (var i = 0; i < 10 && start + i < seen.Count; i++)
            Assert.Equal(i, seen[start + i]);
    }

    [Fact]
    public void ACommonCathodeDisplayIsDrivenFromTheOtherDirection()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());
        var display = circuit.Add(new SevenSegmentDisplay(commonAnode: false));
        var resistor = circuit.Add(new Resistor(330));

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(display.Common, gnd.Pin);
        circuit.Connect(supply.Positive, resistor.A);
        circuit.Connect(resistor.B, display.Segment("a"));

        var sim = new CircuitSimulator(circuit);
        sim.SolveOperatingPoint();

        Assert.True(display.SegmentBrightness[0] > 0.3);
        Assert.All(display.SegmentBrightness.Skip(1), b => Assert.True(b < 0.05));
    }

    [Fact]
    public void LedColoursHaveTheForwardDropTheirPhysicsImplies()
    {
        // Forward voltage rises with photon energy: red lowest, blue and white highest.
        static double ForwardVoltage(Led led)
        {
            var circuit = new Circuit();
            var supply = circuit.Add(new DcVoltageSource(5.0));
            var resistor = circuit.Add(new Resistor(330));
            var gnd = circuit.Add(new Ground());
            circuit.Add(led);

            circuit.Connect(supply.Negative, gnd.Pin);
            circuit.Connect(supply.Positive, resistor.A);
            circuit.Connect(resistor.B, led.Anode);
            circuit.Connect(led.Cathode, gnd.Pin);

            var sim = new CircuitSimulator(circuit);
            sim.SolveOperatingPoint();
            return led.JunctionVoltage;
        }

        var red = ForwardVoltage(Led.OfColour("Red"));
        var green = ForwardVoltage(Led.OfColour("Green"));
        var blue = ForwardVoltage(Led.OfColour("Blue"));

        Assert.InRange(red, 1.6, 2.1);
        Assert.InRange(green, 1.9, 2.5);
        Assert.InRange(blue, 2.6, 3.4);
        Assert.True(red < green && green < blue,
            $"Expected Vf to rise with photon energy, got {red:0.##}, {green:0.##}, {blue:0.##}.");
    }

    [Fact]
    public void EveryPaletteColourIsDistinctAndLights()
    {
        Assert.Equal(6, Led.Colours.Count);
        Assert.Equal(6, Led.Colours.Select(c => c.Name).Distinct().Count());

        foreach (var (name, _, _) in Led.Colours)
        {
            var led = Led.OfColour(name);
            Assert.Equal(name, Led.Colours.First(c => c.Name == name).Name);
            Assert.NotNull(led.Model);
        }
    }
}
