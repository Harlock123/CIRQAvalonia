using Cirq.Components.Passive;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The noise figures the guide tells people to go and look at, asserted here so the guide cannot
/// quietly become wrong about them.
/// </summary>
public class NoiseExampleTests
{
    private static MainWindowViewModel Load(string name)
    {
        var vm = new MainWindowViewModel();

        vm.Circuit.Clear();
        vm.Scope.ClearProbes();
        Examples.All.Single(e => e.Name == name).Build(vm);

        return vm;
    }

    /// <summary>
    /// The guide says the feedback resistors are the whole of what this can see, and names the
    /// ranking as the way to find which. Both halves checked.
    /// </summary>
    [Fact]
    public void TheInvertingAmplifiersNoiseIsItsResistors()
    {
        using var vm = Load("Inverting Amplifier");

        var model = new NoiseViewModel(vm.Circuit) { StartHz = 10, StopHz = 1e5 };
        model.Run();

        Assert.True(model.HasResult, model.Status);
        Assert.NotEmpty(model.Contributors);

        // It opens on the output rather than the input, which is a node an ideal source holds and
        // which therefore has no noise on it at all.
        Assert.Equal("Output", model.Output?.Label);

        // Every generator it found is a resistor's: an op-amp's own noise is not modelled, which
        // is exactly what the guide says.
        Assert.All(model.Contributors, c => Assert.EndsWith("thermal", c.Name, StringComparison.Ordinal));
    }

    /// <summary>
    /// And the other claim: make the dominant resistor a hundred times bigger and the noise goes
    /// up by ten, because it goes as the square root of resistance.
    /// </summary>
    [Fact]
    public void AHundredTimesTheResistanceIsTenTimesTheNoise()
    {
        double Measure(double scale)
        {
            using var vm = Load("Inverting Amplifier");

            foreach (var resistor in vm.Circuit.Components.OfType<Resistor>())
                resistor.Resistance *= scale;

            var model = new NoiseViewModel(vm.Circuit) { StartHz = 10, StopHz = 1e5 };
            model.Run();

            Assert.True(model.HasResult, model.Status);

            return model.Density[0];
        }

        Assert.Equal(10.0, Measure(100.0) / Measure(1.0), 0.5);
    }

    /// <summary>
    /// The result everybody should know, on a circuit from the library rather than a rig: an RC
    /// filled with its own resistor's noise settles at √(kT/C), and changing the resistor by a
    /// hundred times does not move it.
    /// </summary>
    [Fact]
    public void TheRcLowPassRefusesToGetQuieterWhateverTheResistorIs()
    {
        double Measure(double scale)
        {
            using var vm = Load("RC Low-Pass");

            var resistor = vm.Circuit.Components.OfType<Resistor>().First();
            resistor.Resistance *= scale;

            var capacitor = vm.Circuit.Components.OfType<Capacitor>().First();
            var corner = 1.0 / (2 * Math.PI * resistor.Resistance * capacitor.Capacitance);

            var model = new NoiseViewModel(vm.Circuit)
            {
                StartHz = corner / 1e4,
                StopHz = corner * 1e4,
                PointsPerDecade = 40,
            };

            model.Run();

            Assert.True(model.HasResult, model.Status);

            return model.Rms;
        }

        var small = Measure(1.0);
        var large = Measure(100.0);

        Assert.Equal(1.0, large / small, 0.1);
    }
}
