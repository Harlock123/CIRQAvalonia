using Cirq.Components.Ics;
using Cirq.Components.Passive;
using Cirq.Components.Serialization;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The LM35 and TMP36, against the line on their datasheets: ten millivolts a degree, from zero at
/// freezing on the LM35 and from half a volt on the TMP36.
/// </summary>
public class TemperatureSensorIcTests
{
    private static TemperatureSensorIc Powered(
        TemperatureSensorModel model, double celsius, double supply = 5.0, SimulationSettings? settings = null,
        double load = 100e3)
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());
        var rail = circuit.Add(new DcVoltageSource(supply));
        var sensor = circuit.Add(new TemperatureSensorIc(model) { Temperature = celsius });
        var adc = circuit.Add(new Resistor(load));

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, sensor.Supply);
        circuit.Connect(sensor.Ground, gnd.Pin);
        circuit.Connect(sensor.Output, adc.A);
        circuit.Connect(adc.B, gnd.Pin);

        new CircuitSimulator(circuit, settings).SolveOperatingPoint();
        return sensor;
    }

    [Theory]
    [InlineData(0.0, 0.000)]
    [InlineData(25.0, 0.250)]
    [InlineData(100.0, 1.000)]
    [InlineData(150.0, 1.500)]
    public void AnLm35PutsOutTenMillivoltsADegreeFromZero(double celsius, double volts)
    {
        var sensor = Powered(TemperatureSensorModel.Lm35, celsius);

        Assert.Equal(volts, sensor.OutputVoltage, 1e-3);
    }

    [Theory]
    [InlineData(-40.0, 0.100)]
    [InlineData(0.0, 0.500)]
    [InlineData(25.0, 0.750)]
    [InlineData(125.0, 1.750)]
    public void ATmp36PutsOutTenMillivoltsADegreeFromHalfAVolt(double celsius, double volts)
    {
        var sensor = Powered(TemperatureSensorModel.Tmp36, celsius, supply: 3.3);

        Assert.Equal(volts, sensor.OutputVoltage, 1e-3);
    }

    /// <summary>
    /// Below freezing on a single supply an LM35 has nowhere to go, because its output cannot pull
    /// below its own ground. That is exactly what the TMP36's offset is for.
    /// </summary>
    [Fact]
    public void BelowFreezingAnLm35ReadsZeroAndATmp36StillReads()
    {
        Assert.Equal(0.0, Powered(TemperatureSensorModel.Lm35, -10.0).OutputVoltage, 1e-3);
        Assert.Equal(0.4, Powered(TemperatureSensorModel.Tmp36, -10.0).OutputVoltage, 1e-3);
    }

    [Fact]
    public void BelowItsMinimumSupplyItPutsOutNothing()
    {
        var lm35 = Powered(TemperatureSensorModel.Lm35, 50.0, supply: 3.3);

        Assert.False(lm35.IsPowered);
        Assert.True(lm35.OutputVoltage < 0.01, $"read {lm35.OutputVoltage:0.000} V");
        Assert.NotEmpty(lm35.Violations);

        // The same supply runs a TMP36, which is why that is the one for a 3.3 V board.
        Assert.True(Powered(TemperatureSensorModel.Tmp36, 50.0, supply: 3.3).IsPowered);
    }

    [Fact]
    public void ATmp36OnTooHighASupplyIsCalledOut() =>
        Assert.NotEmpty(Powered(TemperatureSensorModel.Tmp36, 25.0, supply: 9.0).Violations);

    [Fact]
    public void ItsOutputBarelyMovesUnderAnAdcLoad()
    {
        var light = Powered(TemperatureSensorModel.Lm35, 40.0, load: 1e6);
        var heavy = Powered(TemperatureSensorModel.Lm35, 40.0, load: 10e3);

        Assert.Equal(light.OutputVoltage, heavy.OutputVoltage, 1e-3);
    }

    /// <summary>
    /// Set to follow the ambient, it reads the circuit's temperature and not its own setting — so a
    /// DC sweep over temperature draws its line.
    /// </summary>
    [Fact]
    public void FollowingTheAmbientItReadsTheCircuitsTemperature()
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var sensor = circuit.Add(new TemperatureSensorIc(TemperatureSensorModel.Tmp36) { Temperature = 0, FollowsAmbient = true });

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, sensor.Supply);
        circuit.Connect(sensor.Ground, gnd.Pin);

        new CircuitSimulator(circuit, new SimulationSettings { TemperatureKelvin = 273.15 + 60 }).SolveOperatingPoint();

        Assert.Equal(60.0, sensor.SensedCelsius, 1e-9);
        Assert.Equal(1.1, sensor.OutputVoltage, 1e-3);
    }

    [Fact]
    public void DoubleClickingItWarmsItAndAgainCoolsIt()
    {
        var sensor = new TemperatureSensorIc { Temperature = 22, AlternateTemperature = 36 };

        sensor.Interact();
        Assert.Equal(36, sensor.Temperature);

        sensor.Interact();
        Assert.Equal(22, sensor.Temperature);
    }

    [Fact]
    public void ItsModelAndTemperatureSurviveTheFile()
    {
        var circuit = new Circuit();
        circuit.Add(new TemperatureSensorIc(TemperatureSensorModel.Tmp36) { Temperature = 41.5, FollowsAmbient = true });

        var back = CircuitSerializer.FromJson(CircuitSerializer.ToJson(circuit)).Circuit
            .Components.OfType<TemperatureSensorIc>().Single();

        Assert.Same(TemperatureSensorModel.Tmp36, back.Model);
        Assert.Equal(41.5, back.Temperature);
        Assert.True(back.FollowsAmbient);
    }
}
