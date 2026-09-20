using Cirq.Components.Buses;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// An ADC on a bus is only worth having if the number it returns is the voltage that was really
/// there — and if the ways it can lie to you are the ways the real part lies.
/// </summary>
public class Ads1115Tests
{
    private static (CircuitSimulator Sim, I2cMaster Master, Ads1115 Adc, DcVoltageSource Signal) Rig(
        string script, double signal, int configuration = 0x4283)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(3.3));
        var source = circuit.Add(new DcVoltageSource(signal));
        var gnd = circuit.Add(new Ground());

        var master = circuit.Add(new I2cMaster { Transactions = script, ClockFrequency = 100e3 });
        var adc = circuit.Add(new Ads1115 { Configuration = configuration });

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(source.Negative, gnd.Pin);

        circuit.Connect(master.Vcc, rail.Positive);
        circuit.Connect(master.Gnd, gnd.Pin);
        circuit.Connect(adc.Vcc, rail.Positive);
        circuit.Connect(adc.Gnd, gnd.Pin);

        circuit.Connect(adc.Sda, master.Sda);
        circuit.Connect(adc.Scl, master.Scl);

        // The signal under test, on A0.
        circuit.Connect(adc.Input(0), source.Positive);

        var sdaPull = circuit.Add(new Resistor(4.7e3));
        var sclPull = circuit.Add(new Resistor(4.7e3));
        circuit.Connect(rail.Positive, sdaPull.A);
        circuit.Connect(sdaPull.B, master.Sda);
        circuit.Connect(rail.Positive, sclPull.A);
        circuit.Connect(sclPull.B, master.Scl);

        var sim = new CircuitSimulator(circuit,
            new SimulationSettings { TimeStep = 1e-6, MaxTimeStep = 2e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, master, adc, source);
    }

    /// <summary>Two bytes, most significant first, is one signed sixteen-bit reading.</summary>
    private static int Combine(IReadOnlyList<int> bytes)
    {
        var raw = (bytes[0] << 8) | bytes[1];
        return raw >= 0x8000 ? raw - 0x10000 : raw;
    }

    /// <summary>
    /// The headline: a voltage on a node, read back over two wires as a number that means the
    /// same thing.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(2.5)]
    [InlineData(0.25)]
    public void AVoltageOnTheNodeComesBackOverTheBus(double signal)
    {
        // Point at the conversion register and read its two bytes.
        var (sim, master, adc, _) = Rig("w 48 00; r 48 2", signal);

        sim.Run(10e-3);

        Assert.True(master.IsFinished);
        Assert.Equal(2, master.ReceivedBytes.Count);

        var counts = Combine(master.ReceivedBytes);
        var volts = counts / 32767.0 * adc.FullScale;

        Assert.Equal(signal, volts, 0.01);
    }

    /// <summary>
    /// The gain trap. Set to ±2.048 V and fed three, it does not complain — it returns the
    /// largest number it has and goes on returning it.
    /// </summary>
    [Fact]
    public void BeyondFullScaleItPinsRatherThanComplaining()
    {
        // PGA = 2 is ±2.048 V.
        const int config = 0x4283 | (2 << 9);

        var (sim, master, adc, _) = Rig("w 48 00; r 48 2", 3.0, config & ~(7 << 9) | (2 << 9));

        sim.Run(10e-3);

        Assert.Equal(2.048, adc.FullScale, 0.001);
        Assert.True(adc.IsClipping);
        Assert.Equal(32767, Combine(master.ReceivedBytes));
    }

    /// <summary>And pushing further past it changes nothing, which is what makes it hard to spot.</summary>
    [Fact]
    public void PushingFurtherPastFullScaleChangesNothing()
    {
        const int config = 0x4283 & ~(7 << 9) | (2 << 9);

        var (threeSim, _, three, _) = Rig("w 48 00; r 48 2", 3.0, config);
        var (fiveSim, _, five, _) = Rig("w 48 00; r 48 2", 5.0, config);

        threeSim.Run(10e-3);
        fiveSim.Run(10e-3);

        Assert.Equal(three.LastValue, five.LastValue);
        Assert.True(three.IsClipping && five.IsClipping);
    }

    /// <summary>A wider range fits the same signal, at the cost of resolution.</summary>
    [Fact]
    public void AWiderRangeFitsWhatTheNarrowOneClipped()
    {
        const int narrow = 0x4283 & ~(7 << 9) | (2 << 9);   // ±2.048
        const int wide = 0x4283 & ~(7 << 9) | (0 << 9);     // ±6.144

        var (narrowSim, _, narrowAdc, _) = Rig("w 48 00; r 48 2", 3.0, narrow);
        var (wideSim, _, wideAdc, _) = Rig("w 48 00; r 48 2", 3.0, wide);

        narrowSim.Run(10e-3);
        wideSim.Run(10e-3);

        Assert.True(narrowAdc.IsClipping);
        Assert.False(wideAdc.IsClipping);
        Assert.Equal(3.0, wideAdc.LastVoltage, 0.01);
    }

    /// <summary>
    /// Conversion takes time. At eight samples a second the register holds the same answer for a
    /// hundred and twenty-five milliseconds however often it is read.
    /// </summary>
    [Fact]
    public void ReadingFasterThanItConvertsReturnsTheSameAnswer()
    {
        // Data rate 0 is eight samples a second.
        const int slow = 0x4283 & ~(7 << 5);

        var (sim, _, adc, source) = Rig("w 48 00; r 48 2", 1.0, slow);

        Assert.Equal(8.0, adc.DataRate, 0.001);

        sim.Run(5e-3);
        var first = adc.LastValue;

        // Move the input a long way and read again, well inside one conversion period.
        source.Voltage = 2.0;
        sim.Run(5e-3);

        Assert.Equal(first, adc.LastValue);
    }

    /// <summary>Wait out a conversion period and it has caught up.</summary>
    [Fact]
    public void GivenTimeItFollowsTheInput()
    {
        const int fast = 0x4283 & ~(7 << 5) | (7 << 5);   // 860 samples a second

        var (sim, _, adc, source) = Rig("w 48 00; r 48 2", 1.0, fast);

        sim.Run(3e-3);
        var first = adc.LastVoltage;

        source.Voltage = 2.0;
        sim.Run(5e-3);

        Assert.Equal(1.0, first, 0.02);
        Assert.Equal(2.0, adc.LastVoltage, 0.02);
    }

    /// <summary>The configuration register can be written over the bus and read back.</summary>
    [Fact]
    public void TheConfigurationCanBeSetOverTheBus()
    {
        // Write 0x4383 to register 1, then point at it and read it back.
        var (sim, master, adc, _) = Rig("w 48 01 43 83; w 48 01; r 48 2", 1.0);

        sim.Run(20e-3);

        Assert.Equal(0x4383, adc.Configuration);
        Assert.Equal(0x4383, (master.ReceivedBytes[0] << 8) | master.ReceivedBytes[1]);
    }

    /// <summary>
    /// Single-ended measures against the chip's own ground; differential measures between two
    /// inputs, which is what you reach for when the two grounds are not the same.
    /// </summary>
    [Fact]
    public void DifferentialMeasuresTheDifference()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(3.3));
        var high = circuit.Add(new DcVoltageSource(2.0));
        var low = circuit.Add(new DcVoltageSource(1.4));
        var gnd = circuit.Add(new Ground());

        var master = circuit.Add(new I2cMaster { Transactions = "w 48 00; r 48 2", ClockFrequency = 100e3 });

        // Multiplexer 0 is A0 against A1.
        var adc = circuit.Add(new Ads1115 { Configuration = 0x0283 });

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(high.Negative, gnd.Pin);
        circuit.Connect(low.Negative, gnd.Pin);
        circuit.Connect(master.Vcc, rail.Positive);
        circuit.Connect(master.Gnd, gnd.Pin);
        circuit.Connect(adc.Vcc, rail.Positive);
        circuit.Connect(adc.Gnd, gnd.Pin);
        circuit.Connect(adc.Sda, master.Sda);
        circuit.Connect(adc.Scl, master.Scl);
        circuit.Connect(adc.Input(0), high.Positive);
        circuit.Connect(adc.Input(1), low.Positive);

        var sdaPull = circuit.Add(new Resistor(4.7e3));
        var sclPull = circuit.Add(new Resistor(4.7e3));
        circuit.Connect(rail.Positive, sdaPull.A);
        circuit.Connect(sdaPull.B, master.Sda);
        circuit.Connect(rail.Positive, sclPull.A);
        circuit.Connect(sclPull.B, master.Scl);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-6, MaxTimeStep = 2e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(10e-3);

        Assert.Equal(0, adc.Multiplexer);
        Assert.Equal(0.6, adc.LastVoltage, 0.01);
    }

    /// <summary>Talking to an address nobody answers to leaves the bus alone.</summary>
    [Fact]
    public void ItOnlyAnswersToItsOwnAddress()
    {
        var (sim, master, _, _) = Rig("w 49 00; r 49 2", 1.0);

        sim.Run(10e-3);

        Assert.False(master.LastTransferAcknowledged);
    }
}
