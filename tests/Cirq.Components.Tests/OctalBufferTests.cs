using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Serialization;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The 74240, 74241, 74540 and 74541 buffers beside the 74244: pinouts, enables and inversion.
/// </summary>
public class OctalBufferTests
{
    // ---- octal buffers ----------------------------------------------------

    [Theory]
    [InlineData(typeof(Ic74240), "p1=1G p2=1A1 p3=2Y4 p9=2Y1 p10=GND p11=2A1 p18=1Y1 p19=2G p20=VCC")]
    [InlineData(typeof(Ic74541), "p1=G1 p2=A1 p9=A8 p10=GND p11=Y8 p18=Y1 p19=G2 p20=VCC")]
    public void EachBufferIsOnItsPinout(Type type, string expected)
    {
        var ic = (DigitalIc)Activator.CreateInstance(type)!;

        foreach (var pair in expected.Split(' '))
        {
            var (id, name) = (pair.Split('=')[0], pair.Split('=')[1]);
            Assert.Equal(name, ic.Terminals.Single(t => t.Id == id).Name);
        }
    }

    /// <summary>
    /// Each part's enables: the 244 and 240 a group each, active low; the 241's second group active high;
    /// the 540 and 541 all eight, only while both are low. The 240 and 540 invert.
    /// </summary>
    [Theory]
    [InlineData(typeof(Ic74244))]
    [InlineData(typeof(Ic74240))]
    [InlineData(typeof(Ic74241))]
    [InlineData(typeof(Ic74540))]
    [InlineData(typeof(Ic74541))]
    public void EachBufferDrivesOnlyWhileEnabled(Type type)
    {
        var bench = new LogicBench();
        var ic = bench.Add((OctalBufferIc)Activator.CreateInstance(type)!);
        var secondHigh = ic is Ic74241;
        var g1 = bench.Toggle(ic.Enable1, true);
        var g2 = bench.Toggle(ic.Enable2, !secondHigh);
        bench.Toggles(ic.Inputs, 0b1100_0101);
        var value = ic.IsInverting ? 0b0011_1010 : 0b1100_0101;

        bench.Run();
        Assert.All(Enumerable.Range(0, 8), i => Assert.Equal(LogicState.HighImpedance, ic.GetOutputState(i)));

        g1.State = false;
        bench.Run();

        if (ic is Ic74540 or Ic74541)
        {
            // One enable is not enough on the flow-through parts.
            Assert.All(Enumerable.Range(0, 8), i => Assert.Equal(LogicState.HighImpedance, ic.GetOutputState(i)));
        }
        else
        {
            Assert.Equal(value & 0xF, bench.Byte(ic.Outputs.Take(4).ToList()));
            Assert.Equal(LogicState.HighImpedance, ic.GetOutputState(4));
        }

        g2.State = secondHigh;
        bench.Run();
        Assert.Equal(value, bench.Byte(ic.Outputs));
    }

    /// <summary>With pins 1 and 19 tied, a 74241 puts one group or the other on the bus, never both.</summary>
    [Fact]
    public void A74241WithItsEnablesTiedTogetherSwapsGroups()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic74241());
        var select = bench.Toggle(ic.Enable1);
        bench.Circuit.Connect(ic.Enable1, ic.Enable2);
        bench.Toggles(ic.Inputs, 0xFF);

        bench.Run();
        Assert.Equal((true, false), ic.GroupEnabled);

        select.State = true;
        bench.Run();
        Assert.Equal((false, true), ic.GroupEnabled);
    }
}
