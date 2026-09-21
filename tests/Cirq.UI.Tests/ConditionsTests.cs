using Cirq.Core.Topology;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>The run-conditions dialog: the temperature the whole circuit is at.</summary>
public class ConditionsTests
{
    [Fact]
    public void ItStartsAtTheCircuitsOwnTemperature()
    {
        var circuit = new Circuit { AmbientTemperatureCelsius = 85 };

        Assert.Equal(85, new ConditionsViewModel(circuit).AmbientCelsius);
    }

    [Fact]
    public void ChangingItWritesBackToTheCircuitAndAnnouncesIt()
    {
        var circuit = new Circuit();
        var model = new ConditionsViewModel(circuit);

        var raised = 0;
        model.Changed += (_, _) => raised++;

        model.AmbientCelsius = -40;

        Assert.Equal(-40, circuit.AmbientTemperatureCelsius);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void ResetGoesBackToWhatTheModelsAreCharacterisedAt()
    {
        var circuit = new Circuit { AmbientTemperatureCelsius = 125 };
        var model = new ConditionsViewModel(circuit);

        model.ResetCommand.Execute(null);

        Assert.Equal(27.0, model.AmbientCelsius);
        Assert.Equal(27.0, circuit.AmbientTemperatureCelsius);
    }

    [Theory]
    [InlineData(27, "room temperature")]
    [InlineData(-40, "Below freezing")]
    [InlineData(-55, "extrapolating")]
    [InlineData(125, "Leakage")]
    public void TheNoteSaysWhatTheSettingImplies(double celsius, string expected)
    {
        var model = new ConditionsViewModel(new Circuit()) { AmbientCelsius = celsius };

        Assert.Contains(expected, model.Note);
    }

    /// <summary>
    /// The circuit owns the temperature and the engine reads it from the settings; the rebuild is
    /// where the two are joined, so a change has to actually reach the models.
    /// </summary>
    [Fact]
    public void ARebuildCarriesTheCircuitsTemperatureIntoTheEngine()
    {
        var vm = new MainWindowViewModel();
        vm.Circuit.Clear();

        var supply = vm.Circuit.Add(new Cirq.Components.Sources.DcVoltageSource(5.0));
        var series = vm.Circuit.Add(new Cirq.Components.Passive.Resistor(4.3e3));
        var diode = vm.Circuit.Add(new Cirq.Components.Nonlinear.Diode());
        var ground = vm.Circuit.Add(new Cirq.Components.Sources.Ground());

        vm.Circuit.Connect(supply.Negative, ground.Pin);
        vm.Circuit.Connect(supply.Positive, series.A);
        vm.Circuit.Connect(series.B, diode.Anode);
        vm.Circuit.Connect(diode.Cathode, ground.Pin);

        double DropAt(double celsius)
        {
            vm.Circuit.AmbientTemperatureCelsius = celsius;
            Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

            return vm.Simulation.Simulator!.NodeVoltage(diode.Anode);
        }

        var room = DropAt(27);
        var hot = DropAt(125);

        // About two millivolts a degree lower, which is ~200 mV over that span.
        Assert.InRange(room - hot, 0.15, 0.25);
    }
}
