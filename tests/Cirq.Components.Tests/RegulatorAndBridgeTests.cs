using Cirq.Components.Bridges;
using Cirq.Components.Digital;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class VoltageRegulatorTests
{
    private static (CircuitSimulator Sim, VoltageRegulator Reg, DcVoltageSource Supply, Resistor Load)
        Regulated(double inputVoltage, double loadResistance, RegulatorModel? model = null)
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(inputVoltage));
        var gnd = circuit.Add(new Ground());
        var reg = circuit.Add(new VoltageRegulator(model));
        var load = circuit.Add(new Resistor(loadResistance));

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, reg.Input);
        circuit.Connect(reg.Common, gnd.Pin);
        circuit.Connect(reg.Output, load.A);
        circuit.Connect(load.B, gnd.Pin);

        return (new CircuitSimulator(circuit), reg, supply, load);
    }

    [Theory]
    [InlineData(9.0)]
    [InlineData(12.0)]
    [InlineData(18.0)]
    [InlineData(24.0)]
    public void Lm7805HoldsFiveVoltsAcrossItsInputRange(double inputVoltage)
    {
        var (sim, reg, _, load) = Regulated(inputVoltage, 100);
        sim.SolveOperatingPoint();

        Assert.Equal(5.0, sim.NodeVoltage(load.A), 0.05);
        Assert.False(reg.IsInDropout);
    }

    [Fact]
    public void OutputFollowsTheInputDownOnceInDropout()
    {
        // 6 V in on a 7805 with a 2 V dropout leaves only 4 V available.
        var (sim, reg, _, load) = Regulated(6.0, 100);
        sim.SolveOperatingPoint();

        Assert.Equal(4.0, sim.NodeVoltage(load.A), 0.15);
        Assert.True(reg.IsInDropout);
    }

    [Fact]
    public void LoadRegulationHoldsAcrossThreeDecadesOfCurrent()
    {
        var voltages = new List<double>();
        foreach (var resistance in new[] { 10e3, 1e3, 100.0, 10.0 })
        {
            var (sim, _, _, load) = Regulated(12.0, resistance);
            sim.SolveOperatingPoint();
            voltages.Add(sim.NodeVoltage(load.A));
        }

        Assert.All(voltages, v => Assert.Equal(5.0, v, 0.05));
    }

    [Fact]
    public void LoadCurrentIsDrawnFromTheInputSupply()
    {
        var (sim, reg, supply, _) = Regulated(12.0, 50);
        sim.SolveOperatingPoint();

        // 5 V across 50 R is 100 mA, which the input supply must actually deliver.
        Assert.Equal(0.1, reg.OutputCurrent, 0.005);
        Assert.Equal(0.1 + reg.Model.QuiescentCurrent, supply.OutputCurrent, 0.005);
    }

    [Fact]
    public void PowerDissipationIsTheDropTimesTheCurrent()
    {
        var (sim, reg, _, _) = Regulated(12.0, 50);
        sim.SolveOperatingPoint();

        // (12 - 5) V * 100 mA = 0.70 W in the pass element, plus 12 V * 5 mA of quiescent
        // current, for 0.76 W of total device dissipation.
        Assert.Equal(0.76, reg.PowerDissipation, 0.03);
        Assert.True(reg.JunctionTemperature > reg.AmbientTemperature);
    }

    [Fact]
    public void ShortCircuitIsHeldAtTheCurrentLimit()
    {
        var (sim, reg, _, _) = Regulated(12.0, 0.1);
        sim.Reset();
        sim.SolveOperatingPoint();
        // The fold-back settles over a few points rather than instantly.
        sim.Run(1e-3);

        Assert.True(reg.IsCurrentLimited);
        Assert.True(reg.OutputCurrent <= reg.Model.CurrentLimit * 1.15,
            $"Current limit was exceeded: {reg.OutputCurrent:0.###} A.");
    }

    [Fact]
    public void Lm317SetsItsOutputFromTheAdjustDivider()
    {
        // Vout = 1.25 * (1 + R2/R1). With R1 = 240 and R2 = 720 that is 5 V.
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(15.0));
        var gnd = circuit.Add(new Ground());
        var reg = circuit.Add(new VoltageRegulator(RegulatorModel.Lm317));
        var r1 = circuit.Add(new Resistor(240));
        var r2 = circuit.Add(new Resistor(720));
        var load = circuit.Add(new Resistor(1e3));

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, reg.Input);
        circuit.Connect(reg.Output, r1.A);
        circuit.Connect(r1.B, reg.Common);
        circuit.Connect(reg.Common, r2.A);
        circuit.Connect(r2.B, gnd.Pin);
        circuit.Connect(reg.Output, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.SolveOperatingPoint();

        Assert.Equal(5.0, sim.NodeVoltage(reg.Output), 0.15);
    }

    [Fact]
    public void NegativeRegulatorHoldsANegativeRail()
    {
        var (sim, _, _, load) = Regulated(-12.0, 100, RegulatorModel.Lm7905);
        sim.SolveOperatingPoint();

        Assert.Equal(-5.0, sim.NodeVoltage(load.A), 0.05);
    }
}

public class BridgeTests
{
    [Fact]
    public void AdcBridgeSquaresUpASineWave()
    {
        var circuit = new Circuit();
        var gen = circuit.Add(new FunctionGenerator(Waveform.Sine, 1e3, 6.0) { DcOffset = 1.4 });
        var adc = circuit.Add(new AdcBridge());
        var gnd = circuit.Add(new Ground());

        circuit.Connect(gen.Return, gnd.Pin);
        circuit.Connect(gen.Output, adc.AnalogIn);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-7, MaxTimeStep = 1e-7 });
        sim.Reset();
        sim.SolveOperatingPoint();

        var seenHigh = false;
        var seenLow = false;
        var illegal = 0;
        while (sim.Time < 3e-3)
        {
            sim.Step();
            var v = sim.NodeVoltage(adc.DigitalOut);
            if (v > LogicLevels.Ttl.Vih) seenHigh = true;
            else if (v < LogicLevels.Ttl.Vil) seenLow = true;
            else illegal++;
        }

        Assert.True(seenHigh && seenLow, "Expected the bridge to swing between both logic levels.");
        // The output is a clean digital signal, so it should almost never sit mid-rail.
        Assert.True(illegal < 40, $"Output lingered between levels for {illegal} points.");
    }

    [Fact]
    public void AdcBridgeHysteresisRejectsAThresholdCrossing()
    {
        var circuit = new Circuit();
        var source = circuit.Add(new DcVoltageSource(0.0));
        var adc = circuit.Add(new AdcBridge());
        var gnd = circuit.Add(new Ground());

        circuit.Connect(source.Negative, gnd.Pin);
        circuit.Connect(source.Positive, adc.AnalogIn);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-8, MaxTimeStep = 1e-8 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(200e-9);
        Assert.Equal(LogicState.Low, adc.GetOutputState(0));

        // 1.5 V is above the falling threshold but below the rising one: no change yet.
        source.Voltage = 1.5;
        sim.Run(200e-9);
        Assert.Equal(LogicState.Low, adc.GetOutputState(0));

        source.Voltage = 3.0;
        sim.Run(200e-9);
        Assert.Equal(LogicState.High, adc.GetOutputState(0));

        // Coming back down, it holds high until it passes the lower threshold.
        source.Voltage = 1.5;
        sim.Run(200e-9);
        Assert.Equal(LogicState.High, adc.GetOutputState(0));

        source.Voltage = 0.5;
        sim.Run(200e-9);
        Assert.Equal(LogicState.Low, adc.GetOutputState(0));
    }

    [Fact]
    public void DacBridgeDrivesItsConfiguredAnalogRails()
    {
        var circuit = new Circuit();
        var toggle = circuit.Add(new LogicToggle(false));
        var dac = circuit.Add(new DacBridge { AnalogHigh = 12.0, AnalogLow = -3.0, SourceResistance = 10 });
        var load = circuit.Add(new Resistor(100e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(toggle.Out, dac.DigitalIn);
        circuit.Connect(dac.AnalogOut, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 5e-9, MaxTimeStep = 5e-9 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(200e-9);
        Assert.Equal(-3.0, sim.NodeVoltage(dac.AnalogOut), 0.02);

        toggle.State = true;
        sim.Run(200e-9);
        Assert.Equal(12.0, sim.NodeVoltage(dac.AnalogOut), 0.02);
    }

    [Fact]
    public void AnalogAndDigitalDomainsRoundTripThroughBothBridges()
    {
        // Sine -> ADC -> 7404 inverter -> DAC -> analog load.
        var circuit = new Circuit();
        var gen = circuit.Add(new FunctionGenerator(Waveform.Sine, 10e3, 6.0) { DcOffset = 1.4 });
        var adc = circuit.Add(new AdcBridge());
        var ic = circuit.Add(new Ic7404());
        var dac = circuit.Add(new DacBridge { AnalogHigh = 10.0, AnalogLow = 0.0 });
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var load = circuit.Add(new Resistor(10e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(ic.Vcc, supply.Positive);
        circuit.Connect(ic.Gnd, gnd.Pin);
        circuit.Connect(gen.Return, gnd.Pin);
        circuit.Connect(gen.Output, adc.AnalogIn);
        circuit.Connect(adc.DigitalOut, ic.Inverter(0).A);
        circuit.Connect(ic.Inverter(0).Y, dac.DigitalIn);
        circuit.Connect(dac.AnalogOut, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-8, MaxTimeStep = 1e-8 });
        sim.Reset();
        sim.SolveOperatingPoint();

        var inverted = 0;
        var samples = 0;
        while (sim.Time < 3e-4)
        {
            sim.Step();
            var analogIn = sim.NodeVoltage(gen.Output);
            var analogOut = sim.NodeVoltage(dac.AnalogOut);

            // Stay clear of the edges, where the propagation delays mean the two disagree.
            if (Math.Abs(analogIn - 1.4) < 1.0) continue;
            samples++;
            if (analogIn > 2.0 && analogOut < 1.0) inverted++;
            else if (analogIn < 0.8 && analogOut > 9.0) inverted++;
        }

        Assert.True(samples > 100, $"Only {samples} usable samples.");
        Assert.True(inverted > samples * 0.95,
            $"Only {inverted} of {samples} samples showed the expected inversion.");
    }
}
