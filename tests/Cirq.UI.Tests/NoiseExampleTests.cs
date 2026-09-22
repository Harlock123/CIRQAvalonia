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
    /// The guide says to open this one and see the op-amp's own voltage noise against its
    /// feedback resistors'. Both halves checked, including that the amplifier leads.
    /// </summary>
    [Fact]
    public void TheInvertingAmplifiersNoiseIsMostlyTheOpAmpsOwn()
    {
        using var vm = Load("Inverting Amplifier");

        var model = new NoiseViewModel(vm.Circuit) { StartHz = 10, StopHz = 1e5 };
        model.Run();

        Assert.True(model.HasResult, model.Status);

        // It opens on the output rather than the input, which is a node a source holds at no
        // noise at all.
        Assert.Equal("Output", model.Output?.Label);

        // The amplifier and the resistors are both there, and the amplifier leads.
        Assert.Contains(model.Contributors, c => c.Name.EndsWith("voltage noise", StringComparison.Ordinal));
        Assert.Contains(model.Contributors, c => c.Name.EndsWith("thermal", StringComparison.Ordinal));

        Assert.EndsWith("voltage noise", model.Contributors[0].Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the other claim the guide makes: scale every resistor up by a hundred and the leader
    /// changes from the amplifier's voltage noise to its current noise.
    /// <para>
    /// Which is the whole trade in one measurement. The voltage noise did not change — it is a
    /// property of the part — but the current noise now has a hundred times the impedance to
    /// develop across. Same amplifier, same gain, and a completely different thing to go and fix.
    /// </para>
    /// </summary>
    [Fact]
    public void ScalingTheResistorsUpTurnsItIntoACurrentNoiseProblem()
    {
        (string Leader, double Rms) Measure(double scale)
        {
            using var vm = Load("Inverting Amplifier");

            foreach (var resistor in vm.Circuit.Components.OfType<Resistor>())
                resistor.Resistance *= scale;

            var model = new NoiseViewModel(vm.Circuit) { StartHz = 10, StopHz = 1e5 };
            model.Run();

            Assert.True(model.HasResult, model.Status);

            return (model.Contributors[0].Name, model.Rms);
        }

        var nominal = Measure(1.0);
        var large = Measure(100.0);

        Assert.EndsWith("voltage noise", nominal.Leader, StringComparison.Ordinal);
        Assert.EndsWith("current noise (−)", large.Leader, StringComparison.Ordinal);

        // And it is much noisier for it.
        Assert.True(large.Rms > nominal.Rms * 5,
            $"{large.Rms:E2} should be far above {nominal.Rms:E2}");
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
