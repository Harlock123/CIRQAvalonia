using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// Everything the circuit has to say about whether its parts are inside what they are sold for.
/// <para>
/// The point of the class is that all of this existed and none of it was in one place, so most of
/// what is checked here is that each kind of limit reaches the list — and that the three which need
/// different arithmetic get it.
/// </para>
/// </summary>
public class RatingsCheckTests
{
    private static (CircuitSimulator Sim, Circuit Circuit) Solved(Circuit circuit)
    {
        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        return (sim, circuit);
    }

    /// <summary>Ten volts across a hundred ohms is a watt, in a quarter-watt part.</summary>
    [Fact]
    public void ADissipationPastItsRatingIsFound()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(10.0) { Name = "V1" });
        var r = circuit.Add(new Resistor(100.0) { Name = "R1", PowerRating = 0.25 });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Positive, TargetTerminal = r.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = r.B, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Negative, TargetTerminal = ground.Pin });

        var (sim, _) = Solved(circuit);

        var finding = new RatingsCheck(sim).At().Findings
            .Single(f => f.Name == "R1" && f.Kind == RatingKind.Power);

        Assert.Equal(PowerVerdict.Over, finding.Verdict);
        Assert.Equal(4.0, finding.Fraction, 1e-6);
    }

    /// <summary>
    /// A capacitor over its voltage rating is found, which it never was before: a plain ceramic was
    /// the one passive in the library with no rating of any kind.
    /// </summary>
    [Fact]
    public void ACapacitorOverItsVoltageIsFound()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(100.0) { Name = "V1" });
        var c = circuit.Add(new Capacitor(1e-6) { Name = "C1", VoltageRating = 50.0 });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Positive, TargetTerminal = c.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = c.B, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Negative, TargetTerminal = ground.Pin });

        var (sim, _) = Solved(circuit);

        var finding = new RatingsCheck(sim).At().Findings
            .Single(f => f.Name == "C1" && f.Kind == RatingKind.Voltage);

        Assert.Equal(PowerVerdict.Over, finding.Verdict);
        Assert.Equal(2.0, finding.Fraction, 0.01);
        Assert.Contains("100V of 50V", finding.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// An LED past its rated current is found. It has always carried the rating — the brightness is
    /// drawn from it — and never said a word when it was exceeded.
    /// </summary>
    [Fact]
    public void AnLedOverItsCurrentIsFound()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(5.0) { Name = "V1" });
        var r = circuit.Add(new Resistor(22.0) { Name = "R1", PowerRating = 1.0 });
        var led = circuit.Add(new Led { Name = "LED1", RatedCurrent = 20e-3 });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Positive, TargetTerminal = r.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = r.B, TargetTerminal = led.Anode });
        circuit.Wires.Add(new WireSegment { SourceTerminal = led.Cathode, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Negative, TargetTerminal = ground.Pin });

        var (sim, _) = Solved(circuit);

        var finding = new RatingsCheck(sim).At().Findings
            .Single(f => f.Name == "LED1" && f.Kind == RatingKind.Current);

        Assert.Equal(PowerVerdict.Over, finding.Verdict);
        Assert.True(finding.Actual > 20e-3, $"22 ohms from five volts is more than 20 mA, not {finding.Actual}");
    }

    /// <summary>
    /// A part's own complaint reaches the list. Thirty-two types have something to say and it used
    /// to reach a hover card and a red ring drawn by about a dozen of the symbols, which means a
    /// part could be complaining and drawing nothing at all.
    /// </summary>
    [Fact]
    public void APartsOwnComplaintReachesTheList()
    {
        var circuit = new Circuit();

        // A cell being asked for most of what it can deliver, which it objects to. Chosen over the
        // relay's missing-flyback complaint because that one only fires on turn-off, and a steady
        // circuit is a simpler thing for a test to be about.
        var cell = circuit.Add(new Battery { Name = "BT1", Discharges = false });
        var load = circuit.Add(new Resistor(0.5) { Name = "R1", PowerRating = 100.0 });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Wires.Add(new WireSegment { SourceTerminal = cell.Positive, TargetTerminal = load.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = load.B, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = cell.Negative, TargetTerminal = ground.Pin });

        var (sim, _) = Solved(circuit);

        for (var i = 0; i < 20; i++) sim.Step();

        var result = new RatingsCheck(sim).At();

        var complaints = result.Findings.Where(f => f.Kind == RatingKind.Complaint).ToList();

        Assert.NotEmpty(complaints);
        Assert.All(complaints, c => Assert.NotEqual(string.Empty, c.Message));

        // A complaint is a decision the part has already made, so it is never merely "warm".
        Assert.All(complaints, c => Assert.Equal(PowerVerdict.Over, c.Verdict));
    }

    /// <summary>
    /// Peaks for the limits, averages for the heat — the distinction that makes this more than the
    /// power budget with extra rows.
    /// <para>
    /// A square wave at a quarter duty puts its full voltage across the capacitor a quarter of the
    /// time. The voltage rating is about that peak, because a dielectric breaks down at the instant
    /// it arrives; the resistor's dissipation is about the average, because a part warms up over
    /// many cycles. So the same run has to report a peak for one and a mean for the other.
    /// </para>
    /// </summary>
    [Fact]
    public void PeaksForTheLimitsAndAveragesForTheHeat()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new FunctionGenerator(Waveform.Square, 1e3, 20.0)
        {
            Name = "FG1",
            DutyCycle = 0.25,
            DcOffset = 10.0,
            EdgeTime = 1e-9,
        });

        var r = circuit.Add(new Resistor(1e3) { Name = "R1", PowerRating = 0.25 });
        var c = circuit.Add(new Capacitor(1e-9) { Name = "C1", VoltageRating = 50.0 });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Output, TargetTerminal = r.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = r.B, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Output, TargetTerminal = c.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = c.B, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Return, TargetTerminal = ground.Pin });

        var sim = new CircuitSimulator(circuit, new SimulationSettings
        {
            TimeStep = 1e-7,
            MaxTimeStep = 1e-7,
        });

        sim.Reset();
        sim.SolveOperatingPoint();

        var result = new RatingsCheck(sim).Over(0.02);

        Assert.True(result.IsAveraged);

        // The capacitor sees twenty volts at the top of every cycle: the peak, not the mean of 5.
        var volts = result.Findings.Single(f => f.Name == "C1" && f.Kind == RatingKind.Voltage);

        Assert.Equal(20.0, volts.Actual, 0.5);

        // The resistor dissipates 400 mW a quarter of the time: the mean of 100 mW, not the peak.
        var watts = result.Findings.Single(f => f.Name == "R1" && f.Kind == RatingKind.Power);

        Assert.Equal(0.1, watts.Actual, 0.01);
    }

    /// <summary>
    /// A circuit with nothing wrong says so, rather than returning an empty list that reads the
    /// same as a check that never ran.
    /// </summary>
    [Fact]
    public void ACircuitThatIsFineSaysSo()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(5.0) { Name = "V1" });
        var r = circuit.Add(new Resistor(10e3) { Name = "R1", PowerRating = 0.25 });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Positive, TargetTerminal = r.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = r.B, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Negative, TargetTerminal = ground.Pin });

        var (sim, _) = Solved(circuit);

        var result = new RatingsCheck(sim).At();

        Assert.Empty(result.Over);
        Assert.Contains("comfortably inside", result.Summary(), StringComparison.Ordinal);
    }

    /// <summary>The worst thing comes first, and a complaint comes before any fraction.</summary>
    [Fact]
    public void TheWorstComesFirst()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(10.0) { Name = "V1" });
        var comfortable = circuit.Add(new Resistor(10e3) { Name = "RFINE", PowerRating = 0.25 });
        var stressed = circuit.Add(new Resistor(100.0) { Name = "RBAD", PowerRating = 0.25 });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        foreach (var r in (Resistor[])[comfortable, stressed])
        {
            circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Positive, TargetTerminal = r.A });
            circuit.Wires.Add(new WireSegment { SourceTerminal = r.B, TargetTerminal = ground.Pin });
        }

        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Negative, TargetTerminal = ground.Pin });

        var (sim, _) = Solved(circuit);

        var findings = new RatingsCheck(sim).At().Findings;

        Assert.Equal("RBAD", findings[0].Name);
        Assert.Contains("RBAD", new RatingsCheck(sim).At().Summary(), StringComparison.Ordinal);
    }
}
