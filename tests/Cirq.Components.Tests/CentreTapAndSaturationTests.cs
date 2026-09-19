using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class CentreTappedTransformerTests
{
    /// <summary>The classic full-wave rectifier: two diodes and a tap, no bridge.</summary>
    private static (CircuitSimulator Sim, CentreTappedTransformer T, Resistor Load, Capacitor Smoothing)
        FullWave(double amplitude = 40.0)
    {
        var circuit = new Circuit();
        var mains = circuit.Add(new FunctionGenerator(Waveform.Sine, 50.0, amplitude));
        var transformer = circuit.Add(new CentreTappedTransformer(1.0, 1.0, 0.999));
        var upper = circuit.Add(new Diode(DiodeModel.D1N4001));
        var lower = circuit.Add(new Diode(DiodeModel.D1N4001));
        var smoothing = circuit.Add(new Capacitor(470e-6) { InitialVoltage = 0 });
        var load = circuit.Add(new Resistor(470));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(mains.Return, gnd.Pin);
        circuit.Connect(mains.Output, transformer.P1);
        circuit.Connect(transformer.P2, gnd.Pin);

        // The tap is the zero volt line, and the two ends swing against it in opposite directions.
        circuit.Connect(transformer.CentreTap, gnd.Pin);
        circuit.Connect(transformer.S1, upper.Anode);
        circuit.Connect(transformer.S2, lower.Anode);
        circuit.Connect(upper.Cathode, smoothing.A);
        circuit.Connect(lower.Cathode, smoothing.A);
        circuit.Connect(smoothing.B, gnd.Pin);
        circuit.Connect(smoothing.A, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, transformer, load, smoothing);
    }

    /// <summary>
    /// The two halves are in antiphase about the tap. That is the whole reason the arrangement
    /// exists: one half conducts on each half cycle.
    /// </summary>
    [Fact]
    public void TheTwoHalvesSwingOppositeWaysAboutTheTap()
    {
        var circuit = new Circuit();
        var mains = circuit.Add(new FunctionGenerator(Waveform.Sine, 50.0, 40.0));
        var transformer = circuit.Add(new CentreTappedTransformer(1.0, 1.0, 0.999));
        var upper = circuit.Add(new Resistor(10e3));
        var lower = circuit.Add(new Resistor(10e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(mains.Return, gnd.Pin);
        circuit.Connect(mains.Output, transformer.P1);
        circuit.Connect(transformer.P2, gnd.Pin);
        circuit.Connect(transformer.CentreTap, gnd.Pin);
        circuit.Connect(transformer.S1, upper.A);
        circuit.Connect(upper.B, gnd.Pin);
        circuit.Connect(transformer.S2, lower.A);
        circuit.Connect(lower.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        var opposed = 0;
        for (var i = 0; i < 400; i++)
        {
            sim.Run(50e-6);

            var a = sim.NodeVoltage(transformer.S1);
            var b = sim.NodeVoltage(transformer.S2);

            if (Math.Abs(a) > 1.0 && Math.Sign(a) != Math.Sign(b)) opposed++;
        }

        Assert.True(opposed > 300, $"only {opposed} samples had the halves in antiphase");
    }

    /// <summary>
    /// Two diodes give full-wave rectification, and the output ripples at twice the mains
    /// frequency rather than at it — which is how you can tell full wave from half.
    /// </summary>
    [Fact]
    public void TwoDiodesAndATapGiveFullWaveRectification()
    {
        var (sim, _, load, _) = FullWave();
        sim.Run(300e-3);

        var peaks = 0;
        var rising = false;
        var previous = sim.NodeVoltage(load.A);

        for (var i = 0; i < 4000; i++)
        {
            sim.Run(25e-6);

            var now = sim.NodeVoltage(load.A);
            var goingUp = now > previous;

            if (rising && !goingUp) peaks++;
            rising = goingUp;
            previous = now;
        }

        // 100 ms of 50 Hz mains, rectified full wave, is ten ripple peaks.
        Assert.InRange(peaks, 8, 12);
        Assert.True(sim.NodeVoltage(load.A) > 5.0, "it should be producing a real DC output");
    }

    /// <summary>
    /// The point of the arrangement: one diode drop in the path rather than the two a bridge
    /// costs, which is most of a volt.
    /// </summary>
    [Fact]
    public void OnlyOneDiodeDropIsInThePath()
    {
        var (sim, transformer, load, _) = FullWave();
        sim.Run(300e-3);

        var peakHalf = 0.0;
        var output = 0.0;

        for (var i = 0; i < 2000; i++)
        {
            sim.Run(25e-6);
            peakHalf = Math.Max(peakHalf, sim.NodeVoltage(transformer.S1));
            output = Math.Max(output, sim.NodeVoltage(load.A));
        }

        // One silicon drop between the winding's peak and the reservoir, not two.
        Assert.InRange(peakHalf - output, 0.3, 1.1);
    }
}

public class InductorSaturationTests
{
    /// <summary>An inductor with a supply across it, so its current ramps.</summary>
    private static (CircuitSimulator Sim, Inductor Coil) Ramping(double saturation)
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(10.0));
        var coil = circuit.Add(new Inductor(1e-3)
        {
            SeriesResistance = 0.01,
            InitialCurrent = 0,
            SaturationCurrent = saturation,
        });
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, coil.A);
        circuit.Connect(coil.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, coil);
    }

    /// <summary>Left at zero, nothing changes: every circuit built before this behaves as it did.</summary>
    [Fact]
    public void WithNoSaturationCurrentItIsIdeal()
    {
        var (sim, coil) = Ramping(0);
        sim.Run(1e-3);

        // di/dt = V/L, so ten volts across a millihenry for a millisecond is ten amps.
        Assert.Equal(10.0, coil.Current, 0.2);
        Assert.Equal(1e-3, coil.EffectiveInductance, 1e-9);
        Assert.False(coil.IsSaturating);
        Assert.Empty(coil.Violations);
    }

    /// <summary>
    /// Past the knee the core gives up and the inductance goes with it, so the current runs away
    /// rather than continuing its straight ramp. This is the failure that destroys converters.
    /// </summary>
    [Fact]
    public void PastTheKneeTheCurrentRunsAway()
    {
        var (ideal, idealCoil) = Ramping(0);
        var (real, realCoil) = Ramping(2.0);

        ideal.Run(1e-3);
        real.Run(1e-3);

        Assert.True(realCoil.Current > idealCoil.Current * 1.5,
            $"saturating coil reached {realCoil.Current:0.0} A against the ideal one's " +
            $"{idealCoil.Current:0.0} A — it should have run away well past it");

        Assert.True(realCoil.IsSaturating);
        Assert.Contains("core is giving up", string.Join(" ", realCoil.Violations));
    }

    /// <summary>At the stated current the inductance has halved, which is what the figure means.</summary>
    [Fact]
    public void AtTheStatedCurrentTheInductanceHasHalved()
    {
        var (sim, coil) = Ramping(2.0);

        // Run until it is carrying about its saturation current.
        for (var i = 0; i < 4000 && Math.Abs(coil.Current) < 2.0; i++) sim.Run(1e-6);

        Assert.Equal(2.0, coil.Current, 0.15);
        Assert.Equal(0.5e-3, coil.EffectiveInductance, 0.06e-3);
    }

    /// <summary>Below the knee it holds its inductance, rather than sagging from the start.</summary>
    [Fact]
    public void WellBelowTheKneeItKeepsItsInductance()
    {
        var (sim, coil) = Ramping(10.0);

        for (var i = 0; i < 2000 && Math.Abs(coil.Current) < 2.0; i++) sim.Run(1e-6);

        // A fifth of the saturation current costs well under a percent.
        Assert.Equal(1e-3, coil.EffectiveInductance, 1e-5);
        Assert.False(coil.IsSaturating);
    }
}
