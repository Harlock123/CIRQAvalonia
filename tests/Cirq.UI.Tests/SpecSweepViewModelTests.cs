using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Core.Verification;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The window that asks whether the circuit still does what it is supposed to across a range.
/// <para>
/// The circuit is a silicon diode fed from a resistor, whose forward drop falls about two
/// millivolts a degree — so a requirement for six hundred millivolts is met cold and not met hot,
/// and which end of the range fails is arithmetic rather than opinion.
/// </para>
/// </summary>
public class SpecSweepViewModelTests
{
    private static (Circuit Circuit, Resistor Series) Reference()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(5.0));
        var series = circuit.Add(new Resistor(10e3));
        var diode = circuit.Add(new Diode());
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, series.A);
        circuit.Connect(series.B, diode.Anode);
        circuit.Connect(diode.Cathode, ground.Pin);

        circuit.Probes.Add(new SignalProbe("Vf", diode.Anode, default));

        return (circuit, series);
    }

    private static DesignSpec AtLeast(double volts, string name = "Reference") => new()
    {
        Name = name,
        Trace = "Vf",
        Quantity = SpecQuantity.Mean,
        Comparison = SpecComparison.AtLeast,
        Limit = volts,
    };

    [Fact]
    public void WithNoRequirementsItSaysWhereToWriteThemDown()
    {
        var (circuit, _) = Reference();
        var vm = new SpecSweepViewModel(circuit);

        Assert.Contains("No requirements yet", vm.Status);

        vm.RunCommand.Execute(null);

        Assert.Empty(vm.Rows);
        Assert.Contains("Requirements", vm.Status);
    }

    [Fact]
    public void ItStartsOverTheCommercialTemperatureRange()
    {
        var (circuit, _) = Reference();
        var vm = new SpecSweepViewModel(circuit);

        Assert.True(vm.Parameter?.IsTemperature);
        Assert.Equal(-40, vm.Start);
        Assert.Equal(85, vm.Stop);
        Assert.Equal("Temperature (°C)", vm.AxisLabel);
    }

    [Fact]
    public void ChoosingAPartRangesRoundWhatItIsSetTo()
    {
        var (circuit, series) = Reference();
        var vm = new SpecSweepViewModel(circuit);

        vm.Parameter = vm.Options.First(o => o.Component == series && o.PropertyName == nameof(Resistor.Resistance));

        Assert.Equal(series.Resistance / 2, vm.Start);
        Assert.Equal(series.Resistance * 2, vm.Stop);
        Assert.Contains("Resistance", vm.AxisLabel);
    }

    [Fact]
    public void ARequirementThatGivesOutHotIsReportedWithWhereItHolds()
    {
        var (circuit, _) = Reference();
        circuit.Specs.Add(AtLeast(0.6));

        var vm = new SpecSweepViewModel(circuit) { Duration = 2e-4, Count = 10 };

        vm.RunCommand.Execute(null);

        var row = Assert.Single(vm.Rows);

        Assert.Equal("Fails", row.Status);
        Assert.True(row.IsFailing);
        Assert.Contains("Met from", row.Detail);
        Assert.Contains("worst at 85", row.Detail);
        Assert.Contains("it holds from", vm.Status);

        // And there is a curve to draw, with a point per temperature.
        var curve = Assert.Single(vm.Curves);
        Assert.Equal(10, curve.Values.Count);
        Assert.Equal("Reference", curve.Label);

        // Margin falls with temperature and crosses zero, which is the picture.
        Assert.True(curve.Margins[0] > 0);
        Assert.True(curve.Margins[^1] < 0);
    }

    [Fact]
    public void ARequirementMetEverywhereSaysHowMuchRoomIsLeft()
    {
        var (circuit, _) = Reference();
        circuit.Specs.Add(AtLeast(0.2));

        var vm = new SpecSweepViewModel(circuit) { Duration = 2e-4, Count = 6 };

        vm.RunCommand.Execute(null);

        var row = Assert.Single(vm.Rows);

        Assert.Equal("Holds", row.Status);
        Assert.False(row.IsFailing);
        Assert.Contains("Met everywhere", row.Detail);
        Assert.Contains("to spare", row.Detail);
        Assert.StartsWith("Every requirement is met from", vm.Status);
    }

    [Fact]
    public void FailuresAreListedFirst()
    {
        var (circuit, _) = Reference();
        circuit.Specs.Add(AtLeast(0.2, "Comfortable"));
        circuit.Specs.Add(AtLeast(0.6, "Tight"));

        var vm = new SpecSweepViewModel(circuit) { Duration = 2e-4, Count = 6 };

        vm.RunCommand.Execute(null);

        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("Tight", vm.Rows[0].Name);
        Assert.True(vm.Rows[0].IsFailing);
    }

    [Fact]
    public void ARequirementAboutATraceThatIsNotThereIsNotCountedAsAFailure()
    {
        var (circuit, _) = Reference();

        var spec = AtLeast(0.6);
        spec.Trace = "Nowhere";
        circuit.Specs.Add(spec);

        var vm = new SpecSweepViewModel(circuit) { Duration = 2e-4, Count = 4 };

        vm.RunCommand.Execute(null);

        var row = Assert.Single(vm.Rows);

        Assert.Equal("No verdict", row.Status);
        Assert.False(row.IsFailing);
        Assert.True(row.IsUnjudged);
        Assert.Empty(vm.Curves);
    }

    [Fact]
    public void WithNothingProbedThereIsNothingToMeasure()
    {
        var (circuit, _) = Reference();
        circuit.Probes.Clear();
        circuit.Specs.Add(AtLeast(0.6));

        var vm = new SpecSweepViewModel(circuit) { Duration = 2e-4 };

        vm.RunCommand.Execute(null);

        Assert.Empty(vm.Rows);
        Assert.Contains("put a probe", vm.Status);
    }
}
