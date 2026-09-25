using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;
using Cirq.UI.Services;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The power window: the verdict, the rows, and the sentence that says which kind of answer this
/// is. The arithmetic is tested against closed form in the engine's own tests; what is checked here
/// is that it reaches the window intact and that nothing is dressed up on the way.
/// </summary>
public class PowerViewModelTests
{
    private static SimulationController Loop(double ohms = 100.0, double rating = 0.25, bool battery = false)
    {
        var circuit = new Circuit();

        var supply = battery
            ? circuit.Add<CircuitComponent>(new Battery { Name = "BT1", Discharges = false })
            : circuit.Add<CircuitComponent>(new DcVoltageSource(10.0) { Name = "V1" });

        var positive = battery ? ((Battery)supply).Positive : ((DcVoltageSource)supply).Positive;
        var negative = battery ? ((Battery)supply).Negative : ((DcVoltageSource)supply).Negative;

        var r = circuit.Add(new Resistor(ohms) { Name = "R1", PowerRating = rating });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Wires.Add(new WireSegment { SourceTerminal = positive, TargetTerminal = r.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = r.B, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = negative, TargetTerminal = ground.Pin });

        var controller = new SimulationController(circuit);
        Assert.True(controller.Rebuild(), controller.Status);

        return controller;
    }

    /// <summary>A watt in a quarter-watt part reaches the window as a watt in a quarter-watt part.</summary>
    [Fact]
    public void TheRowsSayWhatThePartIsDoing()
    {
        var model = new PowerViewModel(Loop()) { AverageOverSeconds = 0 };
        model.Run();

        var row = model.Parts.Single(p => p.Name == "R1");

        Assert.Equal("1W", row.Watts);
        Assert.Equal("Over", row.Status);
        Assert.Contains("250mW", row.Rating, StringComparison.Ordinal);
        Assert.Contains("400%", row.Rating, StringComparison.Ordinal);
    }

    /// <summary>
    /// A part nobody rated shows a dash rather than a zero or a blank. Both of those read as
    /// measurements of something, and "nothing has been said about this" is not a measurement.
    /// </summary>
    [Fact]
    public void AnUnratedPartShowsADash()
    {
        var controller = Loop(rating: 0);

        var model = new PowerViewModel(controller) { AverageOverSeconds = 0 };
        model.Run();

        var row = model.Parts.Single(p => p.Name == "R1");

        Assert.Equal("—", row.Rating);
        Assert.Equal("—", row.Status);
        Assert.Equal("—", row.Die);
    }

    /// <summary>
    /// The window says which kind of answer it is showing, because the two mean different things
    /// and one of them is a trap for a circuit that switches.
    /// </summary>
    [Fact]
    public void ItSaysWhetherItIsAnAverageOrAnInstant()
    {
        var controller = Loop();

        var instant = new PowerViewModel(controller) { AverageOverSeconds = 0 };
        instant.Run();

        Assert.Contains("operating point", instant.Basis, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("switches", instant.Basis, StringComparison.OrdinalIgnoreCase);

        var averaged = new PowerViewModel(controller) { AverageOverSeconds = 1e-4 };
        averaged.Run();

        Assert.Contains("Averaged", averaged.Basis, StringComparison.Ordinal);
    }

    /// <summary>The verdict names the part, because "something is over" is not actionable.</summary>
    [Fact]
    public void TheSummaryNamesThePart()
    {
        var model = new PowerViewModel(Loop()) { AverageOverSeconds = 0 };
        model.Run();

        Assert.Contains("R1", model.Summary, StringComparison.Ordinal);
    }

    /// <summary>A circuit with nothing over its rating says so rather than listing nothing.</summary>
    [Fact]
    public void ACircuitThatIsFineSaysSo()
    {
        var model = new PowerViewModel(Loop(ohms: 10_000)) { AverageOverSeconds = 0 };
        model.Run();

        Assert.Contains("inside half its rating", model.Summary, StringComparison.Ordinal);
        Assert.False(model.IsEmpty);
    }

    /// <summary>
    /// A battery on the sheet gets a life estimate, taken off the cell rather than asked for again
    /// — the drawing already says what it is powered by.
    /// </summary>
    [Fact]
    public void ABatteryOnTheSheetGetsALifeEstimate()
    {
        var model = new PowerViewModel(Loop(ohms: 1000, battery: true)) { AverageOverSeconds = 0 };
        model.Run();

        Assert.True(model.HasBattery);
        Assert.Contains("mAh", model.BatteryLife, StringComparison.Ordinal);

        // And it says plainly that it is arithmetic, because it is.
        Assert.Contains("arithmetic only", model.BatteryLife, StringComparison.Ordinal);
    }

    /// <summary>No battery, no estimate — rather than an estimate about a battery that is not there.</summary>
    [Fact]
    public void NoBatteryMeansNoEstimate()
    {
        var model = new PowerViewModel(Loop()) { AverageOverSeconds = 0 };
        model.Run();

        Assert.False(model.HasBattery);
        Assert.Equal(string.Empty, model.BatteryLife);
    }

    /// <summary>Durations are spelled the way somebody would say them.</summary>
    [Theory]
    [InlineData(0.5, "30 minutes")]
    [InlineData(5, "5 hours")]
    [InlineData(100, "4.2 days")]
    [InlineData(24 * 800, "2.2 years")]
    public void ALifeIsSpelledInTheUnitsSomebodyWouldSayIt(double hours, string expected) =>
        Assert.Equal(expected, PowerViewModel.Spell(TimeSpan.FromHours(hours)));

    /// <summary>
    /// A running simulation is paused for the measurement and started again after. Averaging steps
    /// the circuit forward, and two things stepping the same simulator is two things corrupting it.
    /// </summary>
    [Fact]
    public void ARunningSimulationIsPutBackTheWayItWasFound()
    {
        var controller = Loop();

        controller.Play();

        try
        {
            var model = new PowerViewModel(controller) { AverageOverSeconds = 1e-4 };
            model.Run();

            Assert.True(controller.IsRunning, "the run was not restarted after the measurement");
        }
        finally
        {
            controller.Pause();
            controller.Dispose();
        }
    }
}
