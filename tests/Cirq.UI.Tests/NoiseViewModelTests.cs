using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>The noise window's own logic, driven without a window.</summary>
public class NoiseViewModelTests
{
    private static Circuit Divider(double top, double bottom)
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(5.0));
        var upper = circuit.Add(new Resistor(top) { Name = "R1" });
        var lower = circuit.Add(new Resistor(bottom) { Name = "R2" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, upper.A);
        circuit.Connect(upper.B, lower.A);
        circuit.Connect(lower.B, ground.Pin);

        circuit.Probes.Add(new SignalProbe("Mid", lower.A, default));

        return circuit;
    }

    [Fact]
    public void ItMeasuresTheDividerAndRanksWhatMadeIt()
    {
        var model = new NoiseViewModel(Divider(100e3, 1e3)) { StartHz = 1, StopHz = 1e4 };

        model.Run();

        Assert.True(model.HasResult, model.Status);
        Assert.Equal(model.Frequencies.Count, model.Density.Count);

        // Two resistors, two generators, largest first.
        Assert.Equal(2, model.Contributors.Count);
        Assert.Equal("R2 thermal", model.Contributors[0].Name);
        Assert.True(model.Contributors[0].Share > model.Contributors[1].Share);

        // The shares account for all of it.
        Assert.Equal(1.0, model.Contributors.Sum(c => c.Share), 0.001);
    }

    /// <summary>
    /// The level is the parallel combination's, because to noise a divider fed from a stiff
    /// source is simply its two resistors in parallel.
    /// </summary>
    [Fact]
    public void TheLevelIsTheParallelCombinations()
    {
        var circuit = Divider(100e3, 1e3);

        var model = new NoiseViewModel(circuit) { StartHz = 1, StopHz = 1e4 };
        model.Run();

        var parallel = 1.0 / ((1 / 100e3) + (1 / 1e3));
        var expected = Math.Sqrt(4 * NoisePhysics.Boltzmann * 300.15 * parallel);

        Assert.Equal(expected, model.Density[0], expected * 0.05);
    }

    /// <summary>The status line gives the total, the worst point, and what to go and change.</summary>
    [Fact]
    public void TheStatusSaysHowMuchAndWhatToChange()
    {
        var model = new NoiseViewModel(Divider(100e3, 1e3)) { StartHz = 1, StopHz = 1e4 };

        model.Run();

        Assert.Contains("rms", model.Status);
        Assert.Contains("worst", model.Status);
        Assert.Contains("R2 thermal", model.Status);
    }

    /// <summary>A bigger resistor makes more noise, which is the one thing everybody expects.</summary>
    [Fact]
    public void ABiggerResistorMakesMoreNoise()
    {
        double Level(double ohms)
        {
            var model = new NoiseViewModel(Divider(ohms, ohms)) { StartHz = 1, StopHz = 1e4 };
            model.Run();

            return model.Density[0];
        }

        // Ten times the resistance is √10 times the noise.
        Assert.Equal(Math.Sqrt(10.0), Level(100e3) / Level(10e3), 0.05);
    }

    /// <summary>The bar is a picture of the share, so the ranking reads without comparing numbers.</summary>
    [Fact]
    public void TheBarIsAsLongAsTheShare()
    {
        var model = new NoiseViewModel(Divider(100e3, 1e3)) { StartHz = 1, StopHz = 1e4 };
        model.Run();

        Assert.True(model.Contributors[0].Bar.Length > model.Contributors[1].Bar.Length);
    }

    // ---- refusing rather than pretending -----------------------------------

    [Fact]
    public void WithNoProbeItSaysSo()
    {
        var circuit = Divider(10e3, 10e3);
        circuit.Probes.Clear();

        var model = new NoiseViewModel(circuit);
        model.Run();

        Assert.False(model.HasResult);
        Assert.Contains("put a probe", model.Status, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A circuit with nothing noisy in it reports that rather than a flat zero, which would read
    /// as a measurement rather than as an absence.
    /// </summary>
    [Fact]
    public void ACircuitOfIdealPartsSaysItMakesNoNoise()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(5.0));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Probes.Add(new SignalProbe("Out", supply.Positive, default));

        var model = new NoiseViewModel(circuit);
        model.Run();

        Assert.False(model.HasResult);
        Assert.Contains("noise", model.Status, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An empty canvas is a message, not an exception.</summary>
    [Fact]
    public void AnEmptyCanvasIsAMessage()
    {
        var model = new NoiseViewModel(new Circuit());
        model.Run();

        Assert.False(model.HasResult);
        Assert.NotEmpty(model.Status);
    }
}
