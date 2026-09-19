using Cirq.Components.Electromechanical;
using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class ThermocoupleTests
{
    private static (CircuitSimulator Sim, Thermocouple Tc, Resistor Load) Rig(double hot, double cold = 25.0)
    {
        var circuit = new Circuit();
        var tc = circuit.Add(new Thermocouple { Temperature = hot, ColdJunctionTemperature = cold });
        var load = circuit.Add(new Resistor(10e6));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(tc.B, gnd.Pin);
        circuit.Connect(tc.A, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, tc, load);
    }

    /// <summary>Forty-one microvolts per degree is what a K type gives, and it is tiny on purpose.</summary>
    [Fact]
    public void ItProducesMicrovoltsPerDegree()
    {
        var (sim, tc, load) = Rig(hot: 125.0);

        // 100 degrees above the cold junction at 41 uV each.
        Assert.Equal(100 * 41e-6, sim.NodeVoltage(load.A), 1e-7);
        Assert.Equal(100 * 41e-6, tc.Emf, 1e-12);
    }

    /// <summary>
    /// The thing the whole subject is about: it measures a difference. The same junction
    /// temperature gives a different reading when the cold end moves, and an instrument that
    /// assumed the cold end was at zero would be wrong by exactly that much.
    /// </summary>
    [Fact]
    public void ItMeasuresADifferenceAndNotATemperature()
    {
        var (_, cool, _) = Rig(hot: 100.0, cold: 0.0);
        var (_, warm, _) = Rig(hot: 100.0, cold: 25.0);

        Assert.Equal(100 * 41e-6, cool.Emf, 1e-12);
        Assert.Equal(75 * 41e-6, warm.Emf, 1e-12);

        // Read back without compensation, the warm one appears to be 75 degrees, not 100.
        Assert.Equal(100.0, cool.UncompensatedTemperature, 1e-6);
        Assert.Equal(75.0, warm.UncompensatedTemperature, 1e-6);
    }

    /// <summary>Each alloy pair has its own coefficient.</summary>
    [Fact]
    public void DifferentTypesGiveDifferentOutputs()
    {
        var k = new Thermocouple(ThermocoupleType.K) { Temperature = 125, ColdJunctionTemperature = 25 };
        var e = new Thermocouple(ThermocoupleType.E) { Temperature = 125, ColdJunctionTemperature = 25 };

        Assert.Equal(100 * 41e-6, k.Emf, 1e-12);
        Assert.Equal(100 * 68e-6, e.Emf, 1e-12);
    }

    /// <summary>Past the alloy's limit it is out of its depth and says so.</summary>
    [Fact]
    public void PastItsRangeItIsReported()
    {
        var (_, tc, _) = Rig(hot: 1400.0);

        Assert.NotEmpty(tc.Violations);
        Assert.Contains("limit", string.Join(" ", tc.Violations));
    }

    /// <summary>
    /// The point of putting it next to an instrumentation amp: two millivolts of thermocouple
    /// becomes something an ADC could read.
    /// </summary>
    [Fact]
    public void AnInstrumentationAmpMakesTheSignalUsable()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(12.0));
        var reference = circuit.Add(new DcVoltageSource(2.5));
        var tc = circuit.Add(new Thermocouple { Temperature = 525, ColdJunctionTemperature = 25 });
        var amp = circuit.Add(new Ina126 { DifferentialGain = 200 });
        var load = circuit.Add(new Resistor(100e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(reference.Negative, gnd.Pin);
        circuit.Connect(amp.PositiveSupply, rail.Positive);
        circuit.Connect(amp.NegativeSupply, gnd.Pin);
        circuit.Connect(amp.Reference, reference.Positive);

        circuit.Connect(tc.B, reference.Positive);          // the cold end sits at the reference
        circuit.Connect(tc.A, amp.InPlus);
        circuit.Connect(amp.InMinus, reference.Positive);
        circuit.Connect(amp.Output, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        // 500 degrees is 20.5 mV, times 200 is 4.1 V above the 2.5 V reference.
        Assert.Equal(2.5 + (500 * 41e-6 * 200), sim.NodeVoltage(amp.Output), 0.1);
    }
}

public class LoadCellTests
{
    private static (CircuitSimulator Sim, LoadCell Cell) Rig(double load, double excitation = 10.0)
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(excitation));
        var cell = circuit.Add(new LoadCell { LoadKilograms = load });
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, cell.ExcitationPositive);
        circuit.Connect(cell.ExcitationNegative, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, cell);
    }

    /// <summary>Rated output is millivolts per volt: two per volt, ten volts, twenty millivolts.</summary>
    [Fact]
    public void AtFullLoadItGivesItsRatedOutput()
    {
        var (_, cell) = Rig(load: 5.0);

        Assert.Equal(20e-3, cell.Output, 1e-4);
        Assert.Equal(10.0, cell.Excitation, 1e-6);
    }

    /// <summary>Half the load is half the output: strain gauges are linear over their range.</summary>
    [Fact]
    public void TheOutputIsProportionalToTheLoad()
    {
        var (_, half) = Rig(load: 2.5);
        var (_, none) = Rig(load: 0.0);

        Assert.Equal(10e-3, half.Output, 1e-4);
        Assert.Equal(0.0, none.Output, 1e-6);
    }

    /// <summary>
    /// Ratiometric, which is why load cells are rated per volt: halve the excitation and the
    /// output halves with it, load unchanged.
    /// </summary>
    [Fact]
    public void TheOutputFollowsTheExcitation()
    {
        var (_, ten) = Rig(load: 5.0, excitation: 10.0);
        var (_, five) = Rig(load: 5.0, excitation: 5.0);

        Assert.Equal(2.0, ten.Output / five.Output, 0.01);
    }

    /// <summary>
    /// No excitation, no output, however much is on it. A bridge is a divider and a divider with
    /// nothing across it divides nothing.
    /// </summary>
    [Fact]
    public void WithoutExcitationItSaysNothing()
    {
        var (_, cell) = Rig(load: 5.0, excitation: 0.0);

        Assert.Equal(0.0, cell.Output, 1e-9);
        Assert.Contains("excitation", string.Join(" ", cell.Violations));
    }

    /// <summary>Well past rated, a real cell does not come back.</summary>
    [Fact]
    public void OverloadingItIsReported()
    {
        var (_, cell) = Rig(load: 10.0);

        Assert.NotEmpty(cell.Violations);
        Assert.Contains("rated", string.Join(" ", cell.Violations));
    }
}

public class ChargePumpTests
{
    private static (CircuitSimulator Sim, ChargePump Pump, Resistor Load) Rig(
        double pump, double reservoir, double loadOhms)
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var chip = circuit.Add(new ChargePump());
        var flying = circuit.Add(new Capacitor(pump));
        var smoothing = circuit.Add(new Capacitor(reservoir));
        var load = circuit.Add(new Resistor(loadOhms));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, chip.Supply);
        circuit.Connect(chip.Ground, gnd.Pin);

        circuit.Connect(chip.CapacitorPositive, flying.A);
        circuit.Connect(flying.B, chip.CapacitorNegative);

        circuit.Connect(chip.Output, smoothing.A);
        circuit.Connect(smoothing.B, gnd.Pin);
        circuit.Connect(chip.Output, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, chip, load);
    }

    /// <summary>
    /// The whole trick: charge a capacitor across the supply, turn it round, and the output ends
    /// up below ground by about as much as the input is above it.
    /// </summary>
    [Fact]
    public void ItMakesANegativeRailFromAPositiveOne()
    {
        var (sim, pump, _) = Rig(pump: 10e-6, reservoir: 100e-6, loadOhms: 10e3);

        sim.Run(50e-3);

        Assert.True(pump.OutputVoltage < -4.0,
            $"{pump.OutputVoltage:0.00} V is not a negative rail worth having");
        Assert.Equal(-5.0, pump.OutputVoltage, 1.0);
    }

    /// <summary>
    /// There is no regulation in it: the output follows the input, so a supply that droops takes
    /// the negative rail with it.
    /// </summary>
    [Fact]
    public void TheOutputFollowsTheInputBecauseNothingRegulates()
    {
        var (fiveSim, five, _) = Rig(pump: 10e-6, reservoir: 100e-6, loadOhms: 100e3);
        fiveSim.Run(50e-3);

        Assert.InRange(five.OutputVoltage / five.InputVoltage, -1.05, -0.85);
    }

    /// <summary>
    /// A charge pump moves a capacitor's worth of charge per cycle, so its output impedance is
    /// about 1/(f·C). Shrink the capacitor and the rail sags under the same load — which is the
    /// surprise this part exists to teach, and no amount of smoothing fixes it.
    /// </summary>
    [Fact]
    public void ASmallPumpCapacitorSagsUnderLoad()
    {
        var (bigSim, big, _) = Rig(pump: 22e-6, reservoir: 100e-6, loadOhms: 1e3);
        var (smallSim, small, _) = Rig(pump: 0.1e-6, reservoir: 100e-6, loadOhms: 1e3);

        bigSim.Run(50e-3);
        smallSim.Run(50e-3);

        Assert.True(big.OutputVoltage < small.OutputVoltage - 1.0,
            $"a 22 uF pump gave {big.OutputVoltage:0.00} V and a 0.1 uF pump {small.OutputVoltage:0.00} V, " +
            "which is not the difference the charge per cycle implies");

        Assert.NotEmpty(small.Violations);
    }

    /// <summary>The impedance figure it reports is the one the sag follows from.</summary>
    [Fact]
    public void ItReportsTheOutputImpedanceItsCapacitorImplies()
    {
        var pump = new ChargePump { OscillatorFrequency = 10e3 };

        Assert.Equal(1.0 / (10e3 * 10e-6), pump.OutputImpedanceFor(10e-6), 1e-9);
        Assert.Equal(1000.0, pump.OutputImpedanceFor(0.1e-6), 1e-6);
    }
}
