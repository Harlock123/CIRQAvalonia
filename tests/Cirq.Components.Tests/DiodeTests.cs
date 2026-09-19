using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class DiodeTests
{
    /// <summary>Series source -> resistor -> diode -> ground.</summary>
    private static (CircuitSimulator Sim, Diode D, DcVoltageSource V, Terminal Anode)
        SeriesDiode(double supply, double resistance, DiodeModel? model = null)
    {
        var circuit = new Circuit();
        var source = circuit.Add(new DcVoltageSource(supply));
        var r = circuit.Add(new Resistor(resistance));
        var d = circuit.Add(new Diode(model));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(source.Positive, r.A);
        circuit.Connect(r.B, d.Anode);
        circuit.Connect(d.Cathode, gnd.Pin);
        circuit.Connect(source.Negative, gnd.Pin);

        return (new CircuitSimulator(circuit), d, source, d.Anode);
    }

    [Fact]
    public void ForwardBiasedSiliconDiodeSitsNearItsDatasheetDrop()
    {
        // 5 V through 430 R puts roughly 10 mA through a 1N4148, where Vf is ~0.72 V.
        var (sim, diode, _, anode) = SeriesDiode(5.0, 430);
        sim.SolveOperatingPoint();

        var vf = sim.NodeVoltage(anode);
        Assert.InRange(vf, 0.6, 0.85);
        Assert.InRange(diode.Current, 8e-3, 12e-3);
    }

    [Fact]
    public void ReverseBiasedDiodeBlocksAllButLeakage()
    {
        var (sim, diode, source, anode) = SeriesDiode(-5.0, 1e3);
        sim.SolveOperatingPoint();

        Assert.True(Math.Abs(diode.Current) < 1e-6, $"Leakage was {diode.Current:g3} A.");
        // Essentially the whole reverse voltage is dropped across the diode.
        Assert.InRange(sim.NodeVoltage(anode), -5.001, -4.99);
        Assert.Equal(-5.0, source.Voltage);
    }

    [Fact]
    public void KirchhoffHoldsAcrossTheOperatingPoint()
    {
        const double supply = 5.0;
        const double r = 1e3;
        var (sim, diode, _, anode) = SeriesDiode(supply, r);
        sim.SolveOperatingPoint();

        var resistorCurrent = (supply - sim.NodeVoltage(anode)) / r;
        Assert.Equal(resistorCurrent, diode.Current, resistorCurrent * 1e-3);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(5.0)]
    [InlineData(12.0)]
    [InlineData(100.0)]
    public void ConvergesAcrossFourDecadesOfDrive(double supply)
    {
        var (sim, diode, _, _) = SeriesDiode(supply, 1e3);
        sim.SolveOperatingPoint();

        var expected = (supply - diode.JunctionVoltage) / 1e3;
        Assert.Equal(expected, diode.Current, Math.Abs(expected) * 0.02 + 1e-9);
    }

    [Fact]
    public void DiodeDropRisesLogarithmicallyWithCurrent()
    {
        // A decade of current costs about n·Vt·ln(10) of extra forward voltage.
        var (lowSim, lowDiode, _, _) = SeriesDiode(5.0, 100e3);
        lowSim.SolveOperatingPoint();
        var (highSim, highDiode, _, _) = SeriesDiode(5.0, 100);
        highSim.SolveOperatingPoint();

        Assert.True(highDiode.Current > lowDiode.Current * 100);
        var decades = Math.Log10(highDiode.Current / lowDiode.Current);
        var perDecade = (highDiode.JunctionVoltage - lowDiode.JunctionVoltage) / decades;

        // 1N4148 has n = 1.75, so expect ~1.75 * 25.85 mV * ln(10) ≈ 104 mV per decade.
        Assert.InRange(perDecade, 0.06, 0.15);
    }

    [Fact]
    public void ZenerClampsAtItsBreakdownVoltage()
    {
        var (sim, diode, _, anode) = SeriesDiode(-12.0, 1e3, DiodeModel.Zener(5.1));
        sim.SolveOperatingPoint();

        // The cathode is at ground, so the anode sits at roughly -Vz.
        Assert.InRange(sim.NodeVoltage(anode), -6.2, -4.8);
        Assert.True(diode.Current < -1e-3, "Expected the zener to conduct in breakdown.");
    }

    [Fact]
    public void HalfWaveRectifierPassesOnlyThePositiveHalfCycle()
    {
        var circuit = new Circuit();
        var gen = circuit.Add(new FunctionGenerator(Waveform.Sine, 1e3, 20.0));
        var d = circuit.Add(new Diode(DiodeModel.D1N4001));
        var load = circuit.Add(new Resistor(1e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(gen.Output, d.Anode);
        circuit.Connect(d.Cathode, load.A);
        circuit.Connect(load.B, gnd.Pin);
        circuit.Connect(gen.Return, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-6, MaxTimeStep = 1e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();

        var minimum = double.MaxValue;
        var maximum = double.MinValue;
        while (sim.Time < 3e-3)
        {
            sim.Step();
            var v = sim.NodeVoltage(load.A);
            minimum = Math.Min(minimum, v);
            maximum = Math.Max(maximum, v);
        }

        // Positive peak is the 10 V input peak less the rectifier drop.
        Assert.InRange(maximum, 8.5, 9.6);
        // The negative half cycle is blocked.
        Assert.InRange(minimum, -0.05, 0.05);
    }

    [Fact]
    public void LedBrightnessTracksForwardCurrent()
    {
        var circuit = new Circuit();
        var source = circuit.Add(new DcVoltageSource(5.0));
        var r = circuit.Add(new Resistor(150));
        var led = circuit.Add(new Led());
        var gnd = circuit.Add(new Ground());

        circuit.Connect(source.Positive, r.A);
        circuit.Connect(r.B, led.Anode);
        circuit.Connect(led.Cathode, gnd.Pin);
        circuit.Connect(source.Negative, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.SolveOperatingPoint();

        Assert.InRange(led.JunctionVoltage, 1.6, 2.4);
        Assert.InRange(led.Current, 15e-3, 25e-3);
        Assert.InRange(led.Brightness, 0.8, 1.0);
    }
}
