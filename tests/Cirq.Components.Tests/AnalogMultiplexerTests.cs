using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Serialization;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The 4052, 4053 and 4067 analog multiplexers beside the 4051.
/// </summary>
public class AnalogMultiplexerTests
{
    // ---- analog multiplexers ---------------------------------------------

    /// <summary>A source on every channel of a section, each a different voltage, and the common reads the one addressed.</summary>
    private static void EveryChannelReachesItsCommon(AnalogMultiplexerIc ic, Terminal[] address)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());
        circuit.Add(ic);
        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(ic.Vcc, rail.Positive);
        circuit.Connect(ic.Gnd, gnd.Pin);
        circuit.Connect(ic.Inhibit, gnd.Pin);

        var vee = ic.Terminals.SingleOrDefault(t => t.Name == "VEE");
        if (vee is not null) circuit.Connect(vee, gnd.Pin);

        var bits = address.Select(pin =>
        {
            var source = circuit.Add(new DcVoltageSource(0.0));
            circuit.Connect(source.Negative, gnd.Pin);
            circuit.Connect(source.Positive, pin);
            return source;
        }).ToArray();

        for (var s = 0; s < ic.SectionCount; s++)
        {
            for (var c = 0; c < ic.ChannelCount(s); c++)
            {
                var source = circuit.Add(new DcVoltageSource(0.1 + (0.2 * c) + (0.05 * s)));
                circuit.Connect(source.Negative, gnd.Pin);
                circuit.Connect(source.Positive, ic.ChannelOf(s, c));
            }

            var load = circuit.Add(new Resistor(100e3));
            circuit.Connect(load.A, ic.CommonOf(s));
            circuit.Connect(load.B, gnd.Pin);
        }

        var sim = new CircuitSimulator(circuit, new() { TimeStep = 1e-7, MaxTimeStep = 1e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();

        var widest = Enumerable.Range(0, ic.SectionCount).Max(ic.ChannelCount);
        for (var code = 0; code < (1 << address.Length); code++)
        {
            for (var b = 0; b < bits.Length; b++) bits[b].Voltage = (code & (1 << b)) != 0 ? 5.0 : 0.0;
            sim.Run(5e-6);

            for (var s = 0; s < ic.SectionCount; s++)
            {
                var channel = ic.SelectedChannelOf(s);
                Assert.InRange(channel, 0, widest - 1);
                Assert.Equal(0.1 + (0.2 * channel) + (0.05 * s), sim.NodeVoltage(ic.CommonOf(s)), 2);
            }
        }
    }

    [Fact]
    public void The4052PicksAPairByOneAddress()
    {
        var ic = new Ic4052();
        Assert.Equal(["Y0", "Y2", "Y", "Y3", "Y1", "INH", "VEE", "VSS", "B", "A", "X3", "X0", "X", "X1", "X2", "VDD"],
            ic.Terminals.Select(t => t.Name));
        EveryChannelReachesItsCommon(ic, [ic.A, ic.B]);
    }

    [Fact]
    public void The4053ThrowsEachSwitchByItsOwnPin()
    {
        var ic = new Ic4053();
        Assert.Equal(["2CH1", "2CH0", "3CH1", "3COM", "3CH0", "INH", "VEE", "VSS", "C", "B", "A", "1CH0", "1CH1", "1COM", "2COM", "VDD"],
            ic.Terminals.Select(t => t.Name));
        EveryChannelReachesItsCommon(ic, [ic.A, ic.B, ic.C]);
    }

    [Fact]
    public void The4067PicksOneOfSixteen()
    {
        var ic = new Ic4067();
        Assert.Equal("COM", ic.Terminals[0].Name);
        Assert.Equal("CH0", ic.Terminals[8].Name);
        Assert.Equal("CH15", ic.Terminals[15].Name);
        Assert.Equal("CH8", ic.Terminals[22].Name);
        Assert.Equal(24, ic.Terminals.Count);
        EveryChannelReachesItsCommon(ic, [ic.A, ic.B, ic.C, ic.D]);
    }

    /// <summary>Inhibit opens every section, and the common then reads only what its load pulls it to.</summary>
    [Fact]
    public void InhibitOpensEverySection()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());
        var ic = circuit.Add(new Ic4053());
        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(ic.Vcc, rail.Positive);
        circuit.Connect(ic.Gnd, gnd.Pin);
        circuit.Connect(ic.Inhibit, rail.Positive);
        foreach (var pin in new[] { ic.A, ic.B, ic.C, ic.NegativeSupply }) circuit.Connect(pin, gnd.Pin);
        circuit.Connect(ic.ChannelOf(0, 0), rail.Positive);
        var load = circuit.Add(new Resistor(100e3));
        circuit.Connect(load.A, ic.CommonOf(0));
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new() { TimeStep = 1e-7, MaxTimeStep = 1e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(5e-6);

        Assert.Equal(-1, ic.SelectedChannelOf(0));
        Assert.True(sim.NodeVoltage(ic.CommonOf(0)) < 0.01);
    }
}
