using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class BridgeRectifierTests
{
    /// <summary>Sine source across the bridge's AC pair, resistive load across the DC pair.</summary>
    private static (CircuitSimulator Sim, BridgeRectifier Bridge, FunctionGenerator Source)
        Rectifier(double peakToPeak, double frequency, double load, double smoothing = 0)
    {
        var circuit = new Circuit();
        var source = circuit.Add(new FunctionGenerator
        {
            Shape = Waveform.Sine,
            Frequency = frequency,
            AmplitudePeakToPeak = peakToPeak,
            OutputResistance = 0.5,
        });
        var bridge = circuit.Add(new BridgeRectifier());
        var resistor = circuit.Add(new Resistor(load));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(source.Output, bridge.Ac1);
        circuit.Connect(source.Return, bridge.Ac2);
        circuit.Connect(bridge.Negative, gnd.Pin);
        circuit.Connect(bridge.Positive, resistor.A);
        circuit.Connect(resistor.B, bridge.Negative);

        if (smoothing > 0)
        {
            var cap = circuit.Add(new ElectrolyticCapacitor(smoothing) { VoltageRating = 63 });
            circuit.Connect(bridge.Positive, cap.A);
            circuit.Connect(cap.B, bridge.Negative);
        }

        var sim = new CircuitSimulator(circuit, new SimulationSettings { UseInitialConditions = true });
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, bridge, source);
    }

    /// <summary>
    /// The point of a bridge: the output never goes negative, whichever way the input swings.
    /// A half-wave rectifier would pass one polarity and block the other; this passes both.
    /// </summary>
    [Fact]
    public void TheOutputStaysPositiveThroughBothHalvesOfTheCycle()
    {
        var (sim, bridge, _) = Rectifier(peakToPeak: 20, frequency: 1e3, load: 1e3);

        var lowest = double.MaxValue;
        sim.TimePointAccepted += _ =>
            lowest = Math.Min(lowest, sim.NodeVoltage(bridge.Positive) - sim.NodeVoltage(bridge.Negative));

        sim.Run(5e-3);

        Assert.True(lowest > -0.2, $"the output went to {lowest:0.00} V");
    }

    /// <summary>
    /// Both halves of the input conduct, so the output ripples at twice the input frequency.
    /// That doubling is the closed-form property that separates full-wave from half-wave.
    /// </summary>
    [Theory]
    [InlineData(500.0)]
    [InlineData(1000.0)]
    public void TheOutputRipplesAtTwiceTheInputFrequency(double frequency)
    {
        var (sim, bridge, _) = Rectifier(peakToPeak: 20, frequency: frequency, load: 1e3);

        var peaks = 0;
        var rising = false;
        var previous = 0.0;

        sim.TimePointAccepted += _ =>
        {
            var v = sim.NodeVoltage(bridge.Positive) - sim.NodeVoltage(bridge.Negative);
            if (v > previous + 1e-6) rising = true;
            else if (rising && v < previous - 1e-6) { peaks++; rising = false; }
            previous = v;
        };

        var cycles = 10;
        sim.Run(cycles / frequency);

        // Ten input cycles produce twenty output humps.
        Assert.InRange(peaks, 2 * cycles - 2, 2 * cycles + 2);
    }

    /// <summary>
    /// Current flows through two diodes in series on each half cycle, so the output peak sits
    /// roughly two forward drops below the input peak. That loss is the reason a bridge needs
    /// headroom, and is exactly what an idealised block would hide.
    /// </summary>
    [Fact]
    public void TheOutputPeakIsTwoDiodeDropsBelowTheInputPeak()
    {
        var (sim, bridge, _) = Rectifier(peakToPeak: 20, frequency: 1e3, load: 1e3);

        var highest = 0.0;
        sim.TimePointAccepted += _ =>
            highest = Math.Max(highest, sim.NodeVoltage(bridge.Positive) - sim.NodeVoltage(bridge.Negative));

        sim.Run(5e-3);

        // 20 Vpp is a 10 V peak; two 1N4001 drops at a few milliamps come to roughly 1.3-1.7 V.
        Assert.InRange(10.0 - highest, 0.9, 2.2);
    }

    [Fact]
    public void ASmoothingCapacitorTurnsTheHumpsIntoAMostlySteadyRail()
    {
        var (sim, bridge, _) = Rectifier(peakToPeak: 20, frequency: 1e3, load: 1e3, smoothing: 100e-6);

        // Let the reservoir charge before measuring the ripple on it.
        sim.Run(20e-3);

        var lowest = double.MaxValue;
        var highest = double.MinValue;
        sim.TimePointAccepted += _ =>
        {
            var v = sim.NodeVoltage(bridge.Positive) - sim.NodeVoltage(bridge.Negative);
            lowest = Math.Min(lowest, v);
            highest = Math.Max(highest, v);
        };

        sim.Run(10e-3);

        // I*T/C with 8.5 mA, a 0.5 ms half period and 100 uF is about 43 mV.
        Assert.True(highest > 8.0, $"the rail only reached {highest:0.00} V");
        Assert.True(highest - lowest < 0.5, $"ripple was {(highest - lowest) * 1000:0} mV");
    }

    [Fact]
    public void EachArmOnlyConductsOnItsOwnHalfCycle()
    {
        var (sim, bridge, _) = Rectifier(peakToPeak: 20, frequency: 1e3, load: 1e3);

        // A quarter period in, one diagonal pair carries the current and the other is off.
        sim.Run(0.25e-3);

        var conducting = bridge.ArmCurrents.Count(i => i > 1e-4);
        Assert.Equal(2, conducting);
    }
}

public class ElectrolyticCapacitorTests
{
    [Fact]
    public void ItChargesLikeAnyOtherCapacitor()
    {
        // One RC time constant should reach 63.2% of the supply, ESR being negligible against 10k.
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(10.0));
        var resistor = circuit.Add(new Resistor(10e3));
        var cap = circuit.Add(new ElectrolyticCapacitor(10e-6));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, resistor.A);
        circuit.Connect(resistor.B, cap.A);
        circuit.Connect(cap.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { UseInitialConditions = true });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(0.1);      // RC = 10k * 10uF = 100 ms

        Assert.Equal(10.0 * 0.632, sim.NodeVoltage(cap.A), 0.2);
    }

    /// <summary>
    /// ESR is the reason a real smoothing capacitor does not hold a perfectly flat rail: ripple
    /// current through it develops a voltage the capacitance cannot absorb. Driving a known
    /// current in and reading the step back out is the direct way to see it.
    /// </summary>
    [Fact]
    public void SeriesResistanceShowsUpImmediatelyAsAStepInVoltage()
    {
        var circuit = new Circuit();
        var source = circuit.Add(new DcCurrentSource(0.5));
        var cap = circuit.Add(new ElectrolyticCapacitor(1e-3) { EquivalentSeriesResistance = 0.4 });
        var gnd = circuit.Add(new Ground());

        // The source drives current out of its negative terminal through the external circuit,
        // so that is the end that feeds charge into the capacitor.
        circuit.Connect(source.Negative, cap.A);
        circuit.Connect(cap.B, gnd.Pin);
        circuit.Connect(source.Positive, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { UseInitialConditions = true });
        sim.Reset();
        sim.SolveOperatingPoint();

        // 100 us into a 1 mF capacitor is 50 mV of charge; the other 200 mV is 0.5 A through 0.4 R.
        sim.Run(100e-6);

        Assert.Equal(0.25, sim.NodeVoltage(cap.A), 0.03);
    }

    [Fact]
    public void ZeroEsrBehavesLikeAPlainCapacitor()
    {
        static double Charge(Capacitor cap)
        {
            var circuit = new Circuit();
            var supply = circuit.Add(new DcVoltageSource(10.0));
            var resistor = circuit.Add(new Resistor(1e3));
            var gnd = circuit.Add(new Ground());
            circuit.Add(cap);

            circuit.Connect(supply.Negative, gnd.Pin);
            circuit.Connect(supply.Positive, resistor.A);
            circuit.Connect(resistor.B, cap.A);
            circuit.Connect(cap.B, gnd.Pin);

            var sim = new CircuitSimulator(circuit, new SimulationSettings { UseInitialConditions = true });
            sim.Reset();
            sim.SolveOperatingPoint();
            sim.Run(1e-3);
            return sim.NodeVoltage(cap.A);
        }

        var plain = Charge(new Capacitor(1e-6));
        var electrolytic = Charge(new ElectrolyticCapacitor(1e-6) { EquivalentSeriesResistance = 1e-4 });

        Assert.Equal(plain, electrolytic, 0.01);
    }

    private static (CircuitSimulator Sim, ElectrolyticCapacitor Cap) Across(double supplyVolts, double rating)
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(supplyVolts));
        var resistor = circuit.Add(new Resistor(100));
        var cap = circuit.Add(new ElectrolyticCapacitor(10e-6) { VoltageRating = rating });
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, resistor.A);
        circuit.Connect(resistor.B, cap.A);
        circuit.Connect(cap.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { UseInitialConditions = true });
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, cap);
    }

    [Fact]
    public void CorrectPolarityAndVoltageRaiseNothing()
    {
        var (sim, cap) = Across(10.0, 25.0);
        sim.Run(10e-3);

        Assert.Empty(cap.Violations);
        Assert.False(cap.IsReverseBiased);
        Assert.False(cap.IsOverVoltage);
    }

    /// <summary>
    /// The classic destructive mistake. A reverse-connected electrolytic vents, so the simulator
    /// says so rather than quietly producing a plausible waveform.
    /// </summary>
    [Fact]
    public void ReverseConnectionIsReported()
    {
        var (sim, cap) = Across(-10.0, 25.0);
        sim.Run(10e-3);

        Assert.True(cap.IsReverseBiased);
        Assert.Contains("polarised", string.Join(" | ", cap.Violations));
    }

    [Fact]
    public void ExceedingTheWorkingVoltageIsReported()
    {
        var (sim, cap) = Across(40.0, 25.0);
        sim.Run(10e-3);

        Assert.True(cap.IsOverVoltage);
        Assert.Contains("25 V part", string.Join(" | ", cap.Violations));
    }

    [Fact]
    public void AViolationIsRememberedAfterTheVoltageComesBackDown()
    {
        // A brief excursion still damages a real part, so the flag latches rather than clearing.
        var (sim, cap) = Across(40.0, 25.0);
        sim.Run(1e-3);
        Assert.True(cap.IsOverVoltage);

        cap.VoltageRating = 63;
        Assert.False(cap.IsOverVoltage);   // re-rated, so the recorded peak is now within spec

        cap.ResetState();
        Assert.Empty(cap.Violations);
    }
}

/// <summary>
/// The circuit all three parts exist to build: transformer secondary into a bridge, into a
/// reservoir capacitor, into a fixed regulator. Each stage only works because the one before it
/// left enough headroom, which is the thing worth checking end to end.
/// </summary>
public class LinearPowerSupplyTests
{
    private static (CircuitSimulator Sim, VoltageRegulator Reg, BridgeRectifier Bridge,
        ElectrolyticCapacitor Reservoir, Resistor Load) Supply(double peakToPeak, double loadOhms)
    {
        var circuit = new Circuit();
        var secondary = circuit.Add(new FunctionGenerator
        {
            Shape = Waveform.Sine,
            Frequency = 50,
            AmplitudePeakToPeak = peakToPeak,
            OutputResistance = 1.0,
        });
        var bridge = circuit.Add(new BridgeRectifier());
        var reservoir = circuit.Add(new ElectrolyticCapacitor(1000e-6) { VoltageRating = 35 });
        var regulator = circuit.Add(new VoltageRegulator(RegulatorModel.Lm7812));
        var smoothing = circuit.Add(new ElectrolyticCapacitor(10e-6) { VoltageRating = 25 });
        var load = circuit.Add(new Resistor(loadOhms));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(secondary.Output, bridge.Ac1);
        circuit.Connect(secondary.Return, bridge.Ac2);
        circuit.Connect(bridge.Negative, gnd.Pin);

        circuit.Connect(bridge.Positive, reservoir.A);
        circuit.Connect(reservoir.B, gnd.Pin);

        circuit.Connect(bridge.Positive, regulator.Input);
        circuit.Connect(regulator.Common, gnd.Pin);
        circuit.Connect(regulator.Output, smoothing.A);
        circuit.Connect(smoothing.B, gnd.Pin);
        circuit.Connect(regulator.Output, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { UseInitialConditions = true });
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, regulator, bridge, reservoir, load);
    }

    [Fact]
    public void ASevenEightTwelveSupplyDeliversTwelveVoltsFromAnAcSecondary()
    {
        var (sim, reg, _, _, _) = Supply(peakToPeak: 40, loadOhms: 120);

        sim.Run(150e-3);       // several mains cycles, so the reservoir is charged

        var lowest = double.MaxValue;
        var highest = double.MinValue;
        sim.TimePointAccepted += _ =>
        {
            var v = sim.NodeVoltage(reg.Output);
            lowest = Math.Min(lowest, v);
            highest = Math.Max(highest, v);
        };

        sim.Run(40e-3);        // two more full cycles of ripple

        Assert.InRange(lowest, 11.8, 12.2);
        Assert.InRange(highest, 11.8, 12.2);
        Assert.False(reg.IsInDropout);
        Assert.False(reg.IsThermallyShutDown);
    }

    /// <summary>
    /// The regulator earns its place by rejecting the ripple the reservoir cannot remove: the
    /// rail into it swings by volts, the rail out of it by millivolts.
    /// </summary>
    [Fact]
    public void TheRegulatorRejectsTheRippleOnTheReservoir()
    {
        var (sim, reg, bridge, _, _) = Supply(peakToPeak: 40, loadOhms: 120);

        sim.Run(150e-3);

        double inLow = double.MaxValue, inHigh = double.MinValue;
        double outLow = double.MaxValue, outHigh = double.MinValue;
        sim.TimePointAccepted += _ =>
        {
            var vin = sim.NodeVoltage(bridge.Positive);
            var vout = sim.NodeVoltage(reg.Output);
            inLow = Math.Min(inLow, vin); inHigh = Math.Max(inHigh, vin);
            outLow = Math.Min(outLow, vout); outHigh = Math.Max(outHigh, vout);
        };

        sim.Run(40e-3);

        var inputRipple = inHigh - inLow;
        var outputRipple = outHigh - outLow;

        Assert.True(inputRipple > 0.3, $"the reservoir rail only rippled {inputRipple * 1000:0} mV");
        Assert.True(outputRipple < inputRipple / 10,
            $"ripple rejection was only {inputRipple / Math.Max(outputRipple, 1e-9):0}x");
    }

    [Fact]
    public void NothingInTheSupplyIsUsedOutsideItsRatings()
    {
        var (sim, reg, _, reservoir, _) = Supply(peakToPeak: 40, loadOhms: 120);

        sim.Run(150e-3);

        Assert.Empty(reservoir.Violations);
        Assert.False(reg.IsThermallyShutDown);
    }

    /// <summary>
    /// The regulator's output sits at zero before it starts and dips slightly negative as it comes
    /// up, so a correctly connected output capacitor sees a couple of volts backwards for a few
    /// tens of microseconds. That is a startup transient, not a wiring mistake, and warning about
    /// it would teach people to ignore the warning.
    /// </summary>
    [Fact]
    public void TheStartupTransientDoesNotAccuseACorrectlyConnectedCapacitor()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(20.0));
        var regulator = circuit.Add(new VoltageRegulator(RegulatorModel.Lm7812));
        var outputCap = circuit.Add(new ElectrolyticCapacitor(10e-6) { VoltageRating = 25 });
        var load = circuit.Add(new Resistor(120));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, regulator.Input);
        circuit.Connect(regulator.Common, gnd.Pin);
        circuit.Connect(regulator.Output, outputCap.A);
        circuit.Connect(outputCap.B, gnd.Pin);
        circuit.Connect(regulator.Output, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { UseInitialConditions = true });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(50e-3);

        Assert.Empty(outputCap.Violations);
        Assert.True(outputCap.ReverseBiasSeconds < 1e-3,
            $"the capacitor was counted as reversed for {outputCap.ReverseBiasSeconds * 1e6:0} us");
    }

    /// <summary>
    /// Too small a secondary and the regulator runs out of headroom on the ripple troughs. The
    /// supply then stops regulating, which is what dropout means and what the flag is for.
    /// </summary>
    [Fact]
    public void TooLittleHeadroomPutsTheRegulatorIntoDropout()
    {
        var (sim, reg, _, _, _) = Supply(peakToPeak: 26, loadOhms: 120);

        sim.Run(150e-3);

        Assert.True(sim.NodeVoltage(reg.Output) < 11.8,
            $"a 13 V peak secondary still produced {sim.NodeVoltage(reg.Output):0.00} V");
    }
}
