using Cirq.Components.Ics;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// W = XY/10 + Z, and everything a multiplier does is that one line read differently.
/// </summary>
public class AnalogMultiplierTests
{
    private sealed record Rig(CircuitSimulator Sim, AnalogMultiplier Multiplier);

    private static Rig Build(double x, double y, double z = 0.0, double supply = 15.0)
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());
        var pos = circuit.Add(new DcVoltageSource(supply));
        var neg = circuit.Add(new DcVoltageSource(supply));
        var vx = circuit.Add(new DcVoltageSource(x));
        var vy = circuit.Add(new DcVoltageSource(y));
        var vz = circuit.Add(new DcVoltageSource(z));
        var u = circuit.Add(new AnalogMultiplier());
        var load = circuit.Add(new Resistor(100e3));

        circuit.Connect(pos.Negative, gnd.Pin);
        circuit.Connect(neg.Positive, gnd.Pin);
        circuit.Connect(vx.Negative, gnd.Pin);
        circuit.Connect(vy.Negative, gnd.Pin);
        circuit.Connect(vz.Negative, gnd.Pin);

        circuit.Connect(u.PositiveSupply, pos.Positive);
        circuit.Connect(u.NegativeSupply, neg.Negative);
        circuit.Connect(u.X1, vx.Positive);
        circuit.Connect(u.X2, gnd.Pin);
        circuit.Connect(u.Y1, vy.Positive);
        circuit.Connect(u.Y2, gnd.Pin);
        circuit.Connect(u.Z, vz.Positive);
        circuit.Connect(u.Output, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        return new Rig(sim, u);
    }

    /// <summary>
    /// The divisor, and the surprise everybody meets: two inputs at five volts give two and a
    /// half out, not twenty-five.
    /// </summary>
    [Theory]
    [InlineData(5.0, 5.0, 2.5)]
    [InlineData(10.0, 10.0, 10.0)]
    [InlineData(2.0, 3.0, 0.6)]
    [InlineData(-4.0, 2.5, -1.0)]
    [InlineData(-3.0, -3.0, 0.9)]
    [InlineData(0.0, 7.0, 0.0)]
    public void TheOutputIsTheProductOverTenVolts(double x, double y, double expected)
    {
        var rig = Build(x, y);

        Assert.Equal(expected, rig.Sim.NodeVoltage(rig.Multiplier.Output), 0.01);
    }

    /// <summary>Z adds straight through, which is what makes it a summing multiplier.</summary>
    [Fact]
    public void TheZInputAddsToTheProduct()
    {
        var rig = Build(4.0, 5.0, z: 1.5);

        Assert.Equal((4.0 * 5.0 / 10.0) + 1.5, rig.Sim.NodeVoltage(rig.Multiplier.Output), 0.01);
    }

    /// <summary>Tie the two inputs together and it squares, which is where true RMS starts.</summary>
    [Theory]
    [InlineData(3.0, 0.9)]
    [InlineData(-3.0, 0.9)]
    [InlineData(7.0, 4.9)]
    public void TiedTogetherItSquares(double input, double expected)
    {
        var rig = Build(input, input);

        Assert.Equal(expected, rig.Sim.NodeVoltage(rig.Multiplier.Output), 0.02);
        Assert.False(rig.Multiplier.IsClipping);
    }

    /// <summary>
    /// And it cannot leave its supplies, whatever the arithmetic asks for. A product that would
    /// need thirty volts out of a fifteen volt part stops at the rail and says so.
    /// </summary>
    [Fact]
    public void ItStopsAtTheRailAndSaysSo()
    {
        var rig = Build(10.0, 10.0, supply: 5.0);

        Assert.True(rig.Multiplier.IsClipping);
        Assert.True(rig.Sim.NodeVoltage(rig.Multiplier.Output) < 5.0);
    }

    /// <summary>
    /// Amplitude modulation, which is not something done to a carrier but simply a carrier
    /// multiplied by a signal. With a carrier at ten volts peak, the envelope is the modulation.
    /// </summary>
    [Fact]
    public void MultiplyingACarrierByASignalIsAmplitudeModulation()
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());
        var pos = circuit.Add(new DcVoltageSource(15));
        var neg = circuit.Add(new DcVoltageSource(15));

        var carrier = circuit.Add(new FunctionGenerator(Waveform.Sine, 100e3, 20.0));
        var modulation = circuit.Add(new FunctionGenerator(Waveform.Sine, 1e3, 10.0) { DcOffset = 6.0 });

        var u = circuit.Add(new AnalogMultiplier());
        var load = circuit.Add(new Resistor(100e3));

        circuit.Connect(pos.Negative, gnd.Pin);
        circuit.Connect(neg.Positive, gnd.Pin);
        circuit.Connect(carrier.Return, gnd.Pin);
        circuit.Connect(modulation.Return, gnd.Pin);

        circuit.Connect(u.PositiveSupply, pos.Positive);
        circuit.Connect(u.NegativeSupply, neg.Negative);
        circuit.Connect(u.X1, carrier.Output);
        circuit.Connect(u.X2, gnd.Pin);
        circuit.Connect(u.Y1, modulation.Output);
        circuit.Connect(u.Y2, gnd.Pin);
        circuit.Connect(u.Z, gnd.Pin);
        circuit.Connect(u.Output, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit,
            new SimulationSettings { TimeStep = 100e-9, MaxTimeStep = 200e-9 });

        sim.Reset();
        sim.SolveOperatingPoint();

        // The envelope is largest where the modulation peaks and smallest where it troughs, so
        // the carrier's peak is measured in a narrow window round each: a 1 kHz sine peaks at
        // 250 µs and troughs at 750 µs.
        double PeakBetween(double from, double until)
        {
            while (sim.Time < from) sim.Step();

            var highest = 0.0;
            while (sim.Time < until)
            {
                sim.Step();
                highest = Math.Max(highest, Math.Abs(sim.NodeVoltage(u.Output)));
            }

            return highest;
        }

        var earlyPeak = PeakBetween(220e-6, 280e-6);
        var latePeak = PeakBetween(720e-6, 780e-6);

        // Modulation swings 1 V to 11 V, so the envelope swings by the same ratio.
        Assert.True(earlyPeak > latePeak * 2.0,
            $"the envelope should follow the modulation: {earlyPeak:0.00} V against {latePeak:0.00} V");
    }

    /// <summary>The scale is a property, because not every multiplier divides by ten.</summary>
    [Fact]
    public void TheDivisorCanBeChanged()
    {
        var rig = Build(4.0, 5.0);
        rig.Multiplier.ScaleVoltage = 1.0;

        rig.Sim.Reset();
        rig.Sim.SolveOperatingPoint();

        Assert.Equal(15.0 - 2.0, rig.Sim.NodeVoltage(rig.Multiplier.Output), 0.5);
        Assert.True(rig.Multiplier.IsClipping, "20 V out of a 15 V part has to clip");
    }
}
