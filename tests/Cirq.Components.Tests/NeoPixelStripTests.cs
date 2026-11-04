using Cirq.Components.Digital;
using Cirq.Components.Serialization;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The WS2812B strip, driven by a pattern generator with the protocol's real timing and decoded the
/// way the chip decodes it: by how long each pulse stays high, latching on a long low.
/// </summary>
public class NeoPixelStripTests
{
    private static readonly Color Red = new(255, 0, 0);
    private static readonly Color Green = new(0, 255, 0);
    private static readonly Color Blue = new(0, 0, 255);
    private static readonly Color Odd = new(0x12, 0xA5, 0x3C);

    private sealed class Rig
    {
        public Rig(string pattern, double stepRate = NeoPixelStrip.PatternStepRate, LogicLevels? levels = null,
            params int[] strips)
        {
            var gnd = Circuit.Add(new Ground());
            Supply = Circuit.Add(new DcVoltageSource(5.0));
            Circuit.Connect(Supply.Negative, gnd.Pin);

            Generator = Circuit.Add(new PatternGenerator
            {
                Pattern = pattern, StepRate = stepRate, Repeats = false, Levels = levels ?? LogicLevels.Cmos5V,
            });

            var feed = Generator.Terminals[0];

            foreach (var count in strips.Length == 0 ? [3] : strips)
            {
                var strip = Circuit.Add(new NeoPixelStrip { PixelCount = count });
                Circuit.Connect(strip.Vcc, Supply.Positive);
                Circuit.Connect(strip.Gnd, gnd.Pin);
                Circuit.Connect(feed, strip.DataIn);
                feed = strip.DataOut;
                Strips.Add(strip);
            }

            Sim = new CircuitSimulator(Circuit, new SimulationSettings { TimeStep = 1e-6, MaxTimeStep = 1e-6 });
            Sim.Reset();
            Sim.SolveOperatingPoint();
        }

        public Circuit Circuit { get; } = new();

        public DcVoltageSource Supply { get; }

        public PatternGenerator Generator { get; }

        public List<NeoPixelStrip> Strips { get; } = [];

        public CircuitSimulator Sim { get; }

        public NeoPixelStrip Strip => Strips[0];
    }

    [Fact]
    public void AFrameShowsItsColoursOnceTheLineIsHeldLow()
    {
        var rig = new Rig(NeoPixelStrip.PatternFor([[Red, Odd, Blue]]));

        // Three pixels of 24 bits at 1.2 µs: 86 µs of data, then the reset.
        rig.Sim.Run(80e-6);
        Assert.Equal(0, rig.Strip.FramesShown);
        Assert.All(rig.Strip.Pixels, p => Assert.Equal(new Color(0, 0, 0), p));

        rig.Sim.Run(80e-6);
        Assert.Equal(1, rig.Strip.FramesShown);
        Assert.Equal([Red, Odd, Blue], rig.Strip.Pixels);
    }

    /// <summary>Green goes first on the wire, then red, then blue — the WS2812's GRB order.</summary>
    [Fact]
    public void TheColourIsSentGreenRedBlue()
    {
        var pattern = NeoPixelStrip.PatternFor([[Green]]);

        // The first eight bits are green's, and all ones: 110 eight times.
        Assert.StartsWith(string.Concat(Enumerable.Repeat("110", 8)) + string.Concat(Enumerable.Repeat("100", 16)), pattern);
    }

    /// <summary>
    /// A bit is how long the line is high and nothing else. At 0.1 µs a step, a 0.5 µs pulse is on
    /// the 0 side of the 0.6 µs split and a 0.7 µs pulse on the 1 side.
    /// </summary>
    [Fact]
    public void ABitIsDecidedByItsHighTime()
    {
        const string zero = "111110000000";      // 0.5 µs high
        const string one = "111111100000";       // 0.7 µs high

        // 0xF0 in green, then red and blue zero: four ones and twenty zeros.
        var pattern = string.Concat(Enumerable.Repeat(one, 4)) + string.Concat(Enumerable.Repeat(zero, 20)) + new string('0', 700);

        var rig = new Rig(pattern, stepRate: 10e6, strips: 1);
        rig.Sim.Run(120e-6);

        Assert.Equal(new Color(0, 0xF0, 0), rig.Strip.Pixels[0]);
    }

    /// <summary>A gap shorter than the reset is not a reset: the bits carry on into the same frame.</summary>
    [Fact]
    public void ALowShorterThanTheResetTimeDoesNotLatch()
    {
        var pattern = NeoPixelStrip.PatternFor([[Red]], resetSteps: 75)
                      + NeoPixelStrip.PatternFor([[Blue]], resetSteps: 150);

        var rig = new Rig(pattern, strips: 2);
        rig.Sim.Run(200e-6);

        // 75 steps is 30 µs: one frame of two pixels, with a pause in it.
        Assert.Equal(1, rig.Strip.FramesShown);
        Assert.Equal([Red, Blue], rig.Strip.Pixels);
    }

    /// <summary>
    /// Chained on DOUT, the second strip gets what the first did not keep: the first pixel takes the
    /// first 24 bits, and so on down the line, with no addresses anywhere.
    /// </summary>
    [Fact]
    public void ASecondStripOnDoutGetsTheRestOfTheFrame()
    {
        var rig = new Rig(NeoPixelStrip.PatternFor([[Red, Green, Blue, Odd, Red]]), strips: [2, 3]);
        rig.Sim.Run(250e-6);

        Assert.Equal([Red, Green], rig.Strips[0].Pixels);
        Assert.Equal([Blue, Odd, Red], rig.Strips[1].Pixels);
    }

    [Fact]
    public void FramesFollowOneAnother()
    {
        var rig = new Rig(NeoPixelStrip.PatternFor([[Red], [Green], [Blue]]), strips: 1);

        rig.Sim.Run(400e-6);

        Assert.Equal(3, rig.Strip.FramesShown);
        Assert.Equal(Blue, rig.Strip.Pixels[0]);
    }

    /// <summary>
    /// A 5 V strip wants 0.7 × VDD to call a high, and a 3.4 V TTL-level drive never gets there — so
    /// nothing at all is decoded. It is the most common reason a strip on a 3.3 V board stays dark.
    /// </summary>
    [Fact]
    public void ADriveThatNeverReachesSevenTenthsOfTheSupplyIsNotSeen()
    {
        var rig = new Rig(NeoPixelStrip.PatternFor([[Red, Green, Blue]]), levels: LogicLevels.Ttl);
        rig.Sim.Run(200e-6);

        Assert.Equal(0, rig.Strip.BitsReceived);
        Assert.All(rig.Strip.Pixels, p => Assert.Equal(new Color(0, 0, 0), p));
    }

    [Fact]
    public void LitPixelsDrawCurrentFromTheSupply()
    {
        var rig = new Rig(NeoPixelStrip.PatternFor([[new Color(255, 255, 255), new Color(255, 255, 255)]]), strips: 2);

        var dark = rig.Strip.SupplyCurrent;
        rig.Sim.Run(150e-6);

        // Two pixels of full white: 3 × 20 mA each, and a milliamp each just for being there.
        Assert.Equal(2e-3, dark, 1e-9);
        Assert.Equal(0.122, rig.Strip.SupplyCurrent, 1e-6);
        Assert.Equal(0.122, -rig.Sim.TerminalCurrent(rig.Supply.Positive), 0.01);
    }

    [Fact]
    public void ItsLengthAndTimingSurviveTheFile()
    {
        var circuit = new Circuit();
        circuit.Add(new NeoPixelStrip { PixelCount = 30, ResetTime = 280e-6 });

        var back = CircuitSerializer.FromJson(CircuitSerializer.ToJson(circuit)).Circuit
            .Components.OfType<NeoPixelStrip>().Single();

        Assert.Equal(30, back.PixelCount);
        Assert.Equal(30, back.Pixels.Count);
        Assert.Equal(280e-6, back.ResetTime);
        Assert.Equal("30 × WS2812B", back.ValueLabel);
    }
}
