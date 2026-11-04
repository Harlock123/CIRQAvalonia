using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Tests;

/// <summary>
/// The rest of the gate packages: the open-collector 7403, 7406 and 7407, the three-input 7411 and
/// 7427, the eight-input 7430, the Schmitt 74132, the tri-state 74125 and 74126, and the CMOS 4002,
/// 4012, 4023, 4025, 4049 and 4050 — every row of every gate, through the analog solve.
/// </summary>
public class GateFamilyTests
{
    /// <summary>
    /// Drives every gate in a package through every combination of its inputs and checks the output
    /// against <paramref name="function"/>. Open-collector outputs get a pull-up each.
    /// </summary>
    private static void EveryRow(MultiGateIc ic, Func<bool[], bool> function, bool cmos = false)
    {
        var bench = new LogicBench { ToggleLevels = cmos ? LogicLevels.Cmos5V : LogicLevels.Ttl };
        bench.Add(ic);

        var toggles = Enumerable.Range(0, ic.GateCount).Select(g => bench.Toggles(ic.GateInputs(g))).ToList();

        if (ic.IsOpenCollector)
        {
            for (var g = 0; g < ic.GateCount; g++)
            {
                var pullUp = bench.Circuit.Add(new Resistor(4.7e3));
                bench.Circuit.Connect(pullUp.A, bench.Rail.Positive);
                bench.Circuit.Connect(pullUp.B, ic.GateOutput(g));
            }
        }

        var width = ic.GateInputs(0).Count;

        for (var row = 0; row < 1 << width; row++)
        {
            // Each gate gets a different row at the same time, so a pin swapped between gates shows.
            for (var g = 0; g < ic.GateCount; g++) LogicBench.Set(toggles[g], (row + g) % (1 << width));

            bench.Run(1e-6);

            for (var g = 0; g < ic.GateCount; g++)
            {
                var value = (row + g) % (1 << width);
                var inputs = Enumerable.Range(0, width).Select(i => (value & (1 << i)) != 0).ToArray();
                Assert.True(function(inputs) == bench.High(ic.GateOutput(g)),
                    $"{ic.PartNumber} gate {g + 1}, inputs {Convert.ToString(value, 2).PadLeft(width, '0')}");
            }
        }
    }

    private static bool Nand(bool[] x) => !x.All(b => b);

    private static bool And(bool[] x) => x.All(b => b);

    private static bool Nor(bool[] x) => !x.Any(b => b);

    [Fact]
    public void The7403IsANandThatOnlyPullsDown() => EveryRow(new Ic7403(), Nand);

    [Fact]
    public void The7406InvertsThroughItsPullUps() => EveryRow(new Ic7406(), x => !x[0]);

    [Fact]
    public void The7407BuffersThroughItsPullUps() => EveryRow(new Ic7407(), x => x[0]);

    [Fact]
    public void The7411IsATripleAnd() => EveryRow(new Ic7411(), And);

    [Fact]
    public void The7427IsATripleNor() => EveryRow(new Ic7427(), Nor);

    [Fact]
    public void The7430IsAnEightInputNand() => EveryRow(new Ic7430(), Nand);

    [Fact]
    public void The4002IsADualFourInputNor() => EveryRow(new Ic4002(), Nor, cmos: true);

    [Fact]
    public void The4012IsADualFourInputNand() => EveryRow(new Ic4012(), Nand, cmos: true);

    [Fact]
    public void The4023IsATripleNand() => EveryRow(new Ic4023(), Nand, cmos: true);

    [Fact]
    public void The4025IsATripleNor() => EveryRow(new Ic4025(), Nor, cmos: true);

    [Fact]
    public void The4049Inverts() => EveryRow(new Ic4049(), x => !x[0], cmos: true);

    [Fact]
    public void The4050Buffers() => EveryRow(new Ic4050(), x => x[0], cmos: true);

    /// <summary>Pin by pin, the datasheet's numbering — where these parts differ from their neighbours is the point.</summary>
    [Theory]
    [InlineData(typeof(Ic7403), "p1=1A p2=1B p3=1Y p4=2A p6=2Y p8=3Y p9=3B p11=4Y p7=GND p14=VCC")]
    [InlineData(typeof(Ic7406), "p1=1A p2=1Y p9=6A p8=6Y p13=4A p12=4Y")]
    [InlineData(typeof(Ic7411), "p1=1A p2=1B p13=1C p12=1Y p3=2A p6=2Y p9=3A p8=3Y")]
    [InlineData(typeof(Ic7427), "p1=1A p13=1C p12=1Y p5=2C p6=2Y p11=3C p8=3Y")]
    [InlineData(typeof(Ic7430), "p1=1A p6=1F p11=1G p12=1H p8=1Y p9=NC p10=NC p13=NC")]
    [InlineData(typeof(Ic4002), "p1=1Y p2=1A p5=1D p6=NC p8=NC p9=2A p12=2D p13=2Y p7=GND p14=VCC")]
    [InlineData(typeof(Ic4023), "p1=1A p2=1B p8=1C p9=1Y p3=2A p5=2C p6=2Y p11=3A p13=3C p10=3Y")]
    [InlineData(typeof(Ic4049), "p1=VCC p2=1Y p3=1A p8=GND p9=4A p10=4Y p13=NC p14=6A p15=6Y p16=NC")]
    [InlineData(typeof(Ic4050), "p1=VCC p8=GND p16=NC p11=5A p12=5Y")]
    public void EachIsOnItsDatasheetPinout(Type type, string expected)
    {
        var ic = (DigitalIc)Activator.CreateInstance(type)!;

        foreach (var pair in expected.Split(' '))
        {
            var (id, name) = (pair.Split('=')[0], pair.Split('=')[1]);
            Assert.Equal(name, ic.Terminals.Single(t => t.Id == id).Name);
        }

        Assert.Equal(ic.PinCount, ic.Terminals.Count);
    }

    /// <summary>
    /// Open collector means wired-AND: two 7403 outputs on one pull-up are low if either gate pulls,
    /// and the pins are bus pins, so the rule check does not call it a clash.
    /// </summary>
    [Fact]
    public void Two7403OutputsOnOnePullUpAreAWiredAnd()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic7403());

        var (a1, b1, y1) = ic.Gate(0);
        var (a2, b2, y2) = ic.Gate(1);
        var first = bench.Toggle(a1);
        bench.Tie(b1, true);
        var second = bench.Toggle(a2);
        bench.Tie(b2, true);

        var pullUp = bench.Circuit.Add(new Resistor(4.7e3));
        bench.Circuit.Connect(pullUp.A, bench.Rail.Positive);
        bench.Circuit.Connect(pullUp.B, y1);
        bench.Circuit.Connect(y1, y2);

        bench.Run();
        Assert.True(bench.High(y1));

        first.State = true;
        bench.Run();
        Assert.False(bench.High(y1));

        first.State = false;
        second.State = true;
        bench.Run();
        Assert.False(bench.High(y1));

        Assert.Equal(TerminalType.Bidirectional, y1.Type);
        Assert.Equal(LogicState.Low, ic.GetOutputState(1));
        Assert.Equal(LogicState.HighImpedance, ic.GetOutputState(0));
    }

    /// <summary>A 7406's released output goes where its pull-up takes it — twelve volts, not five.</summary>
    [Fact]
    public void A7406PullsUpToAHigherRail()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic7406());
        var (a, y) = ic.Inverter(0);
        var input = bench.Toggle(a, true);

        var rail = bench.Circuit.Add(new Cirq.Components.Sources.DcVoltageSource(12.0));
        bench.Circuit.Connect(rail.Negative, bench.Ground.Pin);
        var pullUp = bench.Circuit.Add(new Resistor(1e3));
        bench.Circuit.Connect(pullUp.A, rail.Positive);
        bench.Circuit.Connect(pullUp.B, y);

        bench.Run();
        Assert.True(bench.Simulator.NodeVoltage(y) < 0.5);

        input.State = false;
        bench.Run();
        Assert.Equal(12.0, bench.Simulator.NodeVoltage(y), 1);
    }

    /// <summary>
    /// The 74132 is the 7400 with hysteresis: a slow ramp on a tied pair switches the output low only
    /// above the upper threshold, and back high only below the lower one.
    /// </summary>
    [Fact]
    public void The74132HasHysteresisAtTtlThresholds()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic74132());
        var (a, b, y) = ic.Gate(0);

        var drive = bench.Circuit.Add(new Cirq.Components.Sources.DcVoltageSource(0.0));
        bench.Circuit.Connect(drive.Negative, bench.Ground.Pin);
        bench.Circuit.Connect(drive.Positive, a);
        bench.Circuit.Connect(a, b);

        double Switch(double from, double to, bool wantHigh)
        {
            for (var v = from; Math.Abs(v - to) > 1e-9; v += Math.Sign(to - from) * 0.05)
            {
                drive.Voltage = v;
                bench.Run(200e-9);
                if (bench.High(y) == wantHigh) return v;
            }

            return double.NaN;
        }

        bench.Run();
        Assert.True(bench.High(y));

        var rising = Switch(0, 3.0, wantHigh: false);
        var falling = Switch(3.0, 0, wantHigh: true);

        Assert.InRange(rising, 1.5, 1.9);
        Assert.InRange(falling, 0.7, 1.1);
    }

    /// <summary>Each 74125 buffer drives only while its enable is low; the 74126 the other way round.</summary>
    [Theory]
    [InlineData(typeof(Ic74125), true)]
    [InlineData(typeof(Ic74126), false)]
    public void EachTriStateBufferDrivesOnlyWhileEnabled(Type type, bool activeLow)
    {
        var bench = new LogicBench();
        var ic = bench.Add((QuadTriStateBufferIc)Activator.CreateInstance(type)!);

        Assert.Equal(["1OE", "1A", "1Y", "2OE", "2A", "2Y", "GND", "3Y", "3A", "3OE", "4Y", "4A", "4OE", "VCC"],
            ic.Terminals.Select(t => t.Name));

        var enables = new LogicToggle[4];
        for (var i = 0; i < 4; i++)
        {
            var (enable, a, _) = ic.Buffer(i);
            enables[i] = bench.Toggle(enable, activeLow);      // all disabled
            bench.Toggle(a, i % 2 == 0);
        }

        bench.Run();
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(LogicState.HighImpedance, ic.GetOutputState(i)));

        for (var i = 0; i < 4; i++)
        {
            enables[i].State = !activeLow;
            bench.Run();
            Assert.Equal(i % 2 == 0, bench.High(ic.Buffer(i).Y));
            Assert.All(Enumerable.Range(i + 1, 3 - i), j => Assert.Equal(LogicState.HighImpedance, ic.GetOutputState(j)));
        }
    }

    /// <summary>
    /// Four 74125 buffers on one wire take turns, and two enabled at once that disagree meet in the
    /// middle — neither level.
    /// </summary>
    [Fact]
    public void Two74125BuffersOnOneWireFightWhenBothEnabled()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic74125());

        var (e1, a1, y1) = ic.Buffer(0);
        var (e2, a2, y2) = ic.Buffer(1);
        var first = bench.Toggle(e1);
        var second = bench.Toggle(e2, true);
        bench.Tie(a1, true);
        bench.Tie(a2, false);
        bench.Circuit.Connect(y1, y2);

        bench.Run();
        Assert.True(bench.High(y1));

        second.State = false;
        bench.Run();
        Assert.False(bench.Level(y1).IsDriven());

        first.State = true;
        bench.Run();
        Assert.Equal(LogicState.Low, bench.Level(y1));
    }
}
