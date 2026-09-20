using Cirq.Components.Buses;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// High-side current sensing, and the limit that makes it different from any other amplifier.
/// </summary>
public class CurrentSensorTests
{
    private sealed record Rig(CircuitSimulator Sim, Ina219 Sensor, Resistor Load);

    private static Rig Build(double rail = 5.0, double load = 100.0, double shunt = 0.1)
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());
        var supply = circuit.Add(new DcVoltageSource(rail));
        var logic = circuit.Add(new DcVoltageSource(3.3));
        var sensor = circuit.Add(new Ina219 { ShuntResistance = shunt });
        var resistor = circuit.Add(new Resistor(load));

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(logic.Negative, gnd.Pin);

        // The part's own supply is the logic rail; what it measures is somewhere else entirely,
        // and that separation is the whole idea.
        circuit.Connect(sensor.Vcc, logic.Positive);
        circuit.Connect(sensor.Gnd, gnd.Pin);

        circuit.Connect(supply.Positive, sensor.ShuntPlus);
        circuit.Connect(sensor.ShuntMinus, resistor.A);
        circuit.Connect(resistor.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-5 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1e-3);

        return new Rig(sim, sensor, resistor);
    }

    /// <summary>
    /// 5 V across 100 Ω and a 0.1 Ω shunt: about 50 mA, and the shunt drops five millivolts of the
    /// five volts — which is the price of knowing.
    /// </summary>
    [Fact]
    public void ItReadsTheCurrentThroughItsShunt()
    {
        var rig = Build();

        Assert.Equal(0.04995, rig.Sensor.Current, 0.0005);
        Assert.Equal(4.995, rig.Sensor.BusVoltage, 0.005);
        Assert.Equal(0.005, rig.Sensor.ShuntVoltage, 0.0005);
    }

    [Theory]
    [InlineData(10.0, 0.5)]
    [InlineData(50.0, 0.1)]
    [InlineData(1e3, 0.005)]
    public void AndAcrossTheRangeOfLoads(double load, double expected)
    {
        var rig = Build(load: load);

        Assert.Equal(expected, rig.Sensor.Current, expected * 0.02);
    }

    /// <summary>The power the load is taking, which is the third thing it reports.</summary>
    [Fact]
    public void ItReportsThePowerAsWell()
    {
        var rig = Build(rail: 12.0, load: 100.0);

        Assert.Equal(12.0 * 12.0 / 100.0, rig.Sensor.Power, 0.02);
    }

    /// <summary>
    /// The trap the part is here for. A 30 V rail is outside the 26 V common-mode range, and what
    /// comes back over the bus is not a reading with an error in it — it is a number with nothing
    /// behind it, which is why the part says so.
    /// </summary>
    [Fact]
    public void ARailAboveItsCommonModeRangeIsNotMeasured()
    {
        var fine = Build(rail: 24.0);
        var beyond = Build(rail: 30.0);

        Assert.False(fine.Sensor.IsOutOfCommonModeRange);
        Assert.Empty(fine.Sensor.Violations);

        Assert.True(beyond.Sensor.IsOutOfCommonModeRange);
        Assert.NotEmpty(beyond.Sensor.Violations);
    }

    /// <summary>
    /// The shunt is a compromise, and the model makes it one. Ten times the resistance is ten
    /// times the signal and ten times the loss — a whole volt out of five, at half an amp.
    /// </summary>
    [Fact]
    public void ALargerShuntReadsBetterAndWastesMore()
    {
        var small = Build(load: 10.0, shunt: 0.01);
        var large = Build(load: 10.0, shunt: 1.0);

        Assert.True(large.Sensor.ShuntVoltage > small.Sensor.ShuntVoltage * 50,
            "a bigger shunt gives a bigger signal");

        Assert.True(large.Sensor.BusVoltage < small.Sensor.BusVoltage - 0.4,
            "and takes it out of the load's supply");
    }

    /// <summary>
    /// The registers as the datasheet defines them: ten microvolts a count on the shunt, four
    /// millivolts on the bus with the reading in the top thirteen bits.
    /// </summary>
    [Fact]
    public void TheRegistersAreInTheDatasheetsUnits()
    {
        var rig = Build(rail: 5.0, load: 100.0);

        // 5 mV of shunt is 500 counts of 10 µV.
        Assert.InRange(rig.Sensor.ShuntRegister, 495, 505);

        // 4.995 V of bus is 1249 counts of 4 mV, shifted up three bits.
        Assert.InRange((rig.Sensor.BusRegister >> 3) & 0x1FFF, 1247, 1251);
    }

    /// <summary>Past the gain setting the register stops rising rather than reporting more.</summary>
    [Fact]
    public void OverRangeStopsRatherThanWrappingRound()
    {
        var rig = Build(load: 0.5, shunt: 0.5);

        Assert.True(rig.Sensor.IsOverRange);
        Assert.InRange(rig.Sensor.ShuntRegister, 31990, 32010);
    }

    /// <summary>And the reading comes back over the bus, which is the point of it being I²C.</summary>
    [Fact]
    public void TheShuntRegisterCanBeReadOverTheBus()
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var sensor = circuit.Add(new Ina219 { ShuntResistance = 0.1 });
        var load = circuit.Add(new Resistor(100.0));
        var master = circuit.Add(new I2cMaster { Transactions = "w 40 01; r 40 2", ClockFrequency = 400e3 });
        var pullSda = circuit.Add(new Resistor(4.7e3));
        var pullScl = circuit.Add(new Resistor(4.7e3));

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(sensor.Vcc, supply.Positive);
        circuit.Connect(sensor.Gnd, gnd.Pin);
        circuit.Connect(master.Vcc, supply.Positive);
        circuit.Connect(master.Gnd, gnd.Pin);
        circuit.Connect(supply.Positive, sensor.ShuntPlus);
        circuit.Connect(sensor.ShuntMinus, load.A);
        circuit.Connect(load.B, gnd.Pin);
        circuit.Connect(master.Sda, sensor.Sda);
        circuit.Connect(master.Scl, sensor.Scl);
        circuit.Connect(pullSda.A, supply.Positive);
        circuit.Connect(pullSda.B, sensor.Sda);
        circuit.Connect(pullScl.A, supply.Positive);
        circuit.Connect(pullScl.B, sensor.Scl);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-6, MaxTimeStep = 2e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(3e-3);

        Assert.True(master.LastTransferAcknowledged, "the sensor should have acknowledged");
        Assert.Equal(2, master.ReceivedBytes.Count);

        var counts = (short)((master.ReceivedBytes[0] << 8) | master.ReceivedBytes[1]);

        Assert.Equal(0.005, counts * 10e-6, 0.0005);
    }
}
