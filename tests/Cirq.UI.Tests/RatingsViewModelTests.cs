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
public class RatingsViewModelTests
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
        var model = new RatingsViewModel(Loop()) { AverageOverSeconds = 0 };
        model.Run();

        var part = model.Parts.Single(p => p.Name == "R1");

        Assert.Equal("Over", part.Status);

        var power = part.Findings.Single(f => f.Kind == "power");

        Assert.Contains("1W of 250mW", power.Detail, StringComparison.Ordinal);
        Assert.Contains("400%", power.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A part with no rating of any kind is not listed at all, rather than listed with dashes.
    /// A row that says nothing is a row somebody has to read before finding that out.
    /// </summary>
    [Fact]
    public void AnUnratedPartIsNotListed()
    {
        var controller = Loop(rating: 0);

        var model = new RatingsViewModel(controller) { AverageOverSeconds = 0 };
        model.Run();

        Assert.DoesNotContain(model.Parts, p => p.Name == "R1");
    }

    /// <summary>
    /// Every limit a part is near shows under that part, rather than as separate rows scattered
    /// through the list. A part has several and they are read together.
    /// </summary>
    [Fact]
    public void EveryLimitAboutOnePartIsUnderThatPart()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(100.0) { Name = "V1" });
        var c = circuit.Add(new Capacitor(1e-6) { Name = "C1", VoltageRating = 50.0 });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Positive, TargetTerminal = c.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = c.B, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Negative, TargetTerminal = ground.Pin });

        var controller = new SimulationController(circuit);
        Assert.True(controller.Rebuild(), controller.Status);

        var model = new RatingsViewModel(controller) { AverageOverSeconds = 0 };
        model.Run();

        var part = model.Parts.Single(p => p.Name == "C1");

        Assert.Equal("Over", part.Status);
        Assert.Contains(part.Findings, f => f.Kind == "voltage");
    }

    /// <summary>
    /// The window says which kind of answer it is showing, because the two mean different things
    /// and one of them is a trap for a circuit that switches.
    /// </summary>
    [Fact]
    public void ItSaysWhetherItIsAnAverageOrAnInstant()
    {
        var controller = Loop();

        var instant = new RatingsViewModel(controller) { AverageOverSeconds = 0 };
        instant.Run();

        Assert.Contains("operating point", instant.Basis, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("switches", instant.Basis, StringComparison.OrdinalIgnoreCase);

        var averaged = new RatingsViewModel(controller) { AverageOverSeconds = 1e-4 };
        averaged.Run();

        Assert.Contains("Peaks and averages", averaged.Basis, StringComparison.Ordinal);
    }

    /// <summary>The verdict names the part, because "something is over" is not actionable.</summary>
    [Fact]
    public void TheSummaryNamesThePart()
    {
        var model = new RatingsViewModel(Loop()) { AverageOverSeconds = 0 };
        model.Run();

        Assert.Contains("R1", model.Summary, StringComparison.Ordinal);
    }

    /// <summary>A circuit with nothing over its rating says so rather than listing nothing.</summary>
    [Fact]
    public void ACircuitThatIsFineSaysSo()
    {
        var model = new RatingsViewModel(Loop(ohms: 10_000)) { AverageOverSeconds = 0 };
        model.Run();

        Assert.Contains("comfortably inside", model.Summary, StringComparison.Ordinal);
        Assert.False(model.IsEmpty);
    }

    /// <summary>
    /// A battery on the sheet gets a life estimate, taken off the cell rather than asked for again
    /// — the drawing already says what it is powered by.
    /// </summary>
    [Fact]
    public void ABatteryOnTheSheetGetsALifeEstimate()
    {
        var model = new RatingsViewModel(Loop(ohms: 1000, battery: true)) { AverageOverSeconds = 0 };
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
        var model = new RatingsViewModel(Loop()) { AverageOverSeconds = 0 };
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
        Assert.Equal(expected, RatingsViewModel.Spell(TimeSpan.FromHours(hours)));

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
            var model = new RatingsViewModel(controller) { AverageOverSeconds = 1e-4 };
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
