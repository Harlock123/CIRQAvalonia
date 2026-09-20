using Cirq.Components.Buses;
using Cirq.Components.Ics;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The I²C DAC, and the loop it closes with the ADC. Every voltage here is code over full scale
/// times the supply, worked out by hand.
/// </summary>
public class DacTests
{
    private sealed record Rig(CircuitSimulator Sim, Mcp4725 Dac, Terminal Out);

    private static Rig Build(int code, double supply = 5.0, double load = 0.0, string? script = null)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(supply));
        var gnd = circuit.Add(new Ground());
        var dac = circuit.Add(new Mcp4725 { Code = code });

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(dac.Vcc, rail.Positive);
        circuit.Connect(dac.Gnd, gnd.Pin);

        if (load > 0)
        {
            var resistor = circuit.Add(new Resistor(load));
            circuit.Connect(dac.Output, resistor.A);
            circuit.Connect(resistor.B, gnd.Pin);
        }
        else
        {
            // Something has to reference the output node even when nothing is loading it.
            var leak = circuit.Add(new Resistor(1e9));
            circuit.Connect(dac.Output, leak.A);
            circuit.Connect(leak.B, gnd.Pin);
        }

        if (script is not null)
        {
            var master = circuit.Add(new I2cMaster { Transactions = script, ClockFrequency = 400e3 });
            var pullSda = circuit.Add(new Resistor(4.7e3));
            var pullScl = circuit.Add(new Resistor(4.7e3));

            circuit.Connect(master.Vcc, rail.Positive);
            circuit.Connect(master.Gnd, gnd.Pin);
            circuit.Connect(master.Sda, dac.Sda);
            circuit.Connect(master.Scl, dac.Scl);
            circuit.Connect(pullSda.A, rail.Positive);
            circuit.Connect(pullSda.B, dac.Sda);
            circuit.Connect(pullScl.A, rail.Positive);
            circuit.Connect(pullScl.B, dac.Scl);
        }

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-6, MaxTimeStep = 2e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();

        return new Rig(sim, dac, dac.Output);
    }

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(1024, 1.2503)]
    [InlineData(2048, 2.5006)]
    [InlineData(4095, 5.0)]
    public void TheCodeIsAFractionOfFullScale(int code, double expected)
    {
        var rig = Build(code);

        Assert.Equal(expected, rig.Sim.NodeVoltage(rig.Out), 0.005);
    }

    /// <summary>
    /// There is no reference pin: full scale is the supply, so every voltage it makes moves with
    /// the rail it is running from. Fine for a control voltage, useless as a standard.
    /// </summary>
    [Fact]
    public void FullScaleIsWhateverTheSupplyHappensToBe()
    {
        var low = Build(2048, supply: 3.3);
        var high = Build(2048, supply: 5.0);

        Assert.Equal(1.65, low.Sim.NodeVoltage(low.Out), 0.01);
        Assert.Equal(2.50, high.Sim.NodeVoltage(high.Out), 0.01);
    }

    /// <summary>Twelve bits over five volts is a step of about 1.2 mV, which is the resolution.</summary>
    [Fact]
    public void AStepIsFullScaleOverFourThousandAndNinetySix()
    {
        var rig = Build(0, supply: 5.0);

        Assert.Equal(5.0 / 4096, rig.Dac.StepVoltage, 1e-6);
    }

    /// <summary>
    /// And what it cannot do. The output is a divider behind a small buffer: ask it for half scale
    /// into a couple of kilohms and it gives you appreciably less, because a kilohm of output
    /// resistance and a 2 kΩ load is a divider like any other.
    /// </summary>
    [Fact]
    public void ItCannotDriveALoad()
    {
        var unloaded = Build(2048);
        var loaded = Build(2048, load: 2e3);

        var open = unloaded.Sim.NodeVoltage(unloaded.Out);
        var under = loaded.Sim.NodeVoltage(loaded.Out);

        // 2.5 V behind 1 kΩ into 2 kΩ is two thirds of it.
        Assert.Equal(open * 2.0 / 3.0, under, 0.02);
    }

    /// <summary>The usual fix, and what a follower is for: an op-amp buffer restores the voltage.</summary>
    [Fact]
    public void AnOpAmpFollowerGivesItBackAgain()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());
        var dac = circuit.Add(new Mcp4725 { Code = 2048 });
        var buffer = circuit.Add(new OperationalAmplifier(OpAmpModel.Mcp6002));
        var load = circuit.Add(new Resistor(2e3));

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(dac.Vcc, rail.Positive);
        circuit.Connect(dac.Gnd, gnd.Pin);
        circuit.Connect(buffer.PositiveSupply, rail.Positive);
        circuit.Connect(buffer.NegativeSupply, gnd.Pin);
        circuit.Connect(buffer.NonInverting, dac.Output);
        circuit.Connect(buffer.Output, buffer.Inverting);
        circuit.Connect(buffer.Output, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        Assert.Equal(2.5, sim.NodeVoltage(buffer.Output), 0.02);
    }

    /// <summary>A write over the bus, in the two-byte "fast" form that has no command byte.</summary>
    [Fact]
    public void AFastWriteSetsTheOutput()
    {
        // 0x0800 is half scale: the top nibble of the first byte is the power-down setting.
        var rig = Build(0, script: "w 60 08 00");
        rig.Sim.Run(2e-3);

        Assert.Equal(2048, rig.Dac.Code);
        Assert.Equal(2.5, rig.Sim.NodeVoltage(rig.Out), 0.01);
    }

    /// <summary>
    /// And in the command form, where the twelve bits sit at the top of the two bytes rather than
    /// the bottom. Getting these two the wrong way round puts the output sixteen times out.
    /// </summary>
    [Fact]
    public void ACommandWriteUsesTheOtherAlignment()
    {
        var rig = Build(0, script: "w 60 40 80 00");
        rig.Sim.Run(2e-3);

        Assert.Equal(2048, rig.Dac.Code);
    }

    /// <summary>
    /// The EEPROM, and the difference between the two write commands. 0x60 stores the code as well
    /// as setting it, so the part comes back to it after a power cycle; 0x40 does not.
    /// </summary>
    [Fact]
    public void OnlyTheEepromWriteSurvivesAPowerCycle()
    {
        var stored = Build(0, script: "w 60 60 80 00");
        stored.Sim.Run(2e-3);
        Assert.Equal(2048, stored.Dac.Code);

        stored.Sim.Reset();
        Assert.Equal(2048, stored.Dac.Code);

        var volatileOnly = Build(0, script: "w 60 40 80 00");
        volatileOnly.Sim.Run(2e-3);
        Assert.Equal(2048, volatileOnly.Dac.Code);

        volatileOnly.Sim.Reset();
        Assert.Equal(0, volatileOnly.Dac.Code);
    }

    /// <summary>Powered down, the output is held near ground through a resistor rather than driven.</summary>
    [Fact]
    public void PoweringItDownLetsTheOutputGo()
    {
        var rig = Build(4095);

        // After the reset, because powering up is what clears it.
        rig.Sim.Reset();
        rig.Dac.PowerDownMode = 1;
        rig.Sim.SolveOperatingPoint();

        Assert.True(rig.Dac.IsPoweredDown);
        Assert.Equal(0.0, rig.Sim.NodeVoltage(rig.Out), 0.01);
    }

    /// <summary>
    /// The loop the two parts exist to close: a voltage produced by the DAC, measured by the ADC
    /// beside it, and the number coming back the one that went out.
    /// </summary>
    [Fact]
    public void TheAdcReadsBackWhatTheDacPutOut()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());
        var dac = circuit.Add(new Mcp4725 { Code = 1024 });
        var adc = circuit.Add(new Ads1115());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(dac.Vcc, rail.Positive);
        circuit.Connect(dac.Gnd, gnd.Pin);
        circuit.Connect(adc.Vcc, rail.Positive);
        circuit.Connect(adc.Gnd, gnd.Pin);
        circuit.Connect(dac.Output, adc.Input(0));

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-3, MaxTimeStep = 5e-3 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(0.5);

        // 1024 of 4095 on a 5 V supply is 1.25 V, and the ADC defaults to a ±4.096 V range.
        Assert.Equal(1.2503, adc.LastVoltage, 0.01);
    }
}
