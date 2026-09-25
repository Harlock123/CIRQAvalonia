using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// What the circuit costs to run, and what it costs the parts running it.
/// <para>
/// Checked against arithmetic that can be done on the back of the schematic. Ten volts across a
/// hundred ohms is a watt, in a quarter-watt part that is four times its rating, and if this ever
/// says otherwise it is this that is wrong.
/// </para>
/// </summary>
public class PowerBudgetTests
{
    /// <summary>A supply, a resistor and a ground: the whole of Ohm's law, wired up.</summary>
    private static (CircuitSimulator Sim, Circuit Circuit, Resistor R, DcVoltageSource V) Loop(
        double volts = 10.0, double ohms = 100.0, double rating = 0.25)
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(volts) { Name = "V1" });
        var r = circuit.Add(new Resistor(ohms) { Name = "R1", PowerRating = rating });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Positive, TargetTerminal = r.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = r.B, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Negative, TargetTerminal = ground.Pin });

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        return (sim, circuit, r, supply);
    }

    /// <summary>Ten volts, a hundred ohms, one watt. Which is V²/R and nothing else.</summary>
    [Fact]
    public void AResistorDissipatesVoltsSquaredOverOhms()
    {
        var (sim, _, _, _) = Loop();

        var row = new PowerBudget(sim).At().Parts.Single(p => p.Name == "R1");

        Assert.Equal(1.0, row.Watts, 1e-6);
        Assert.Equal(0.25, row.Rating, 1e-12);
        Assert.Equal(4.0, row.Fraction, 1e-6);
    }

    /// <summary>
    /// Four times its rating is over it, and that is the row the whole report exists to surface.
    /// </summary>
    [Fact]
    public void APartPastItsRatingIsCalledOut()
    {
        var (sim, _, _, _) = Loop();

        var budget = new PowerBudget(sim).At();

        Assert.Equal(PowerVerdict.Over, budget.Parts.Single(p => p.Name == "R1").VerdictAt(25.0));
        Assert.Contains("R1", budget.Summary(), StringComparison.Ordinal);
        Assert.Contains("past", budget.Summary(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Inside its rating but over half of it is "warm", because a part run at its full rating is a
    /// part at the temperature that rating was measured at, in free air, which is not where it is.
    /// </summary>
    [Theory]
    [InlineData(1.0, PowerVerdict.Over)]     // a watt in a one-watt part
    [InlineData(1.5, PowerVerdict.Warm)]     // two thirds of it
    [InlineData(4.0, PowerVerdict.Fine)]     // a quarter
    public void HalfTheRatingIsWhereComfortStops(double rating, PowerVerdict expected)
    {
        var (sim, _, _, _) = Loop(rating: rating);

        Assert.Equal(expected, new PowerBudget(sim).At().Parts.Single(p => p.Name == "R1").VerdictAt(25.0));
    }

    /// <summary>
    /// A part nobody has rated gets no verdict rather than a flattering one. Zero watts of rating
    /// is not a rating of zero watts.
    /// </summary>
    [Fact]
    public void AnUnratedPartGetsNoVerdict()
    {
        var (sim, _, r, _) = Loop();

        r.PowerRating = 0;

        var row = new PowerBudget(sim).At().Parts.Single(p => p.Name == "R1");

        Assert.Equal(PowerVerdict.Unrated, row.VerdictAt(25.0));
        Assert.True(double.IsNaN(row.Fraction), "an unrated part has no fraction of its rating");
    }

    /// <summary>
    /// The supply delivers what the circuit dissipates. Nothing else is in the loop, so the two
    /// have to agree — and if they ever stop agreeing, something is being counted twice or not at
    /// all.
    /// </summary>
    [Fact]
    public void WhatGoesInComesOut()
    {
        var (sim, _, _, _) = Loop();

        var budget = new PowerBudget(sim).At();

        Assert.Equal(1.0, budget.SuppliedWatts, 1e-6);
        Assert.Equal(1.0, budget.DissipatedWatts, 1e-6);
        Assert.Equal(0.1, budget.SuppliedAmps, 1e-6);
    }

    /// <summary>
    /// A source is told from a load by which way the power is going, not by what it is called. A
    /// resistor is never listed as a supply however it is wired.
    /// </summary>
    [Fact]
    public void OnlyWhatDeliversPowerIsCalledASupply()
    {
        var (sim, _, _, _) = Loop();

        var budget = new PowerBudget(sim).At();

        Assert.Equal("V1", Assert.Single(budget.Supplies).Name);
        Assert.DoesNotContain(budget.Supplies, s => s.Name == "R1");
    }

    /// <summary>
    /// A capacitor is not a dissipation. It has volts across it and amps through it like anything
    /// else, and the product of those is energy going in and coming back out — so counting it as
    /// heat would put a wholly imaginary watt in the budget of every filter in the library.
    /// </summary>
    [Fact]
    public void ACapacitorDissipatesNothing()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new FunctionGenerator(Waveform.Square, 1e3, 10.0) { Name = "FG1" });
        var r = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var c = circuit.Add(new Capacitor(1e-7) { Name = "C1" });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Output, TargetTerminal = r.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = r.B, TargetTerminal = c.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = c.B, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Return, TargetTerminal = ground.Pin });

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-6, MaxTimeStep = 1e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();

        for (var i = 0; i < 200; i++) sim.Step();

        var budget = new PowerBudget(sim).At();

        Assert.DoesNotContain(budget.Parts, p => p.Name == "C1");
        Assert.Contains(budget.Parts, p => p.Name == "R1");
    }

    /// <summary>
    /// A switching circuit dissipates its mean, and the mean is what the duty cycle sets.
    /// <para>
    /// This is the whole reason averaging exists. A part fed a square wave is at one of two powers
    /// and never at anything between them, so the instant the solver happens to be sitting at is
    /// never the answer — it is one of the two extremes, and which one is a matter of where the
    /// clock stopped. Averaging across a run is the only thing that turns that into the figure a
    /// heatsink gets chosen against.
    /// </para>
    /// <para>
    /// Twenty volts peak to peak on a ten volt offset swings between nothing and twenty, which is
    /// 400 mW into a kilohm while it is high and nothing while it is low. So the average is 400 mW
    /// times the duty cycle, and a snapshot could not produce these numbers at any phase.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0.25, 0.100)]
    [InlineData(0.50, 0.200)]
    [InlineData(0.75, 0.300)]
    public void ASwitchingPartDissipatesItsAverageRatherThanItsInstant(double duty, double expected)
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new FunctionGenerator(Waveform.Square, 1e3, 20.0)
        {
            Name = "FG1",
            DutyCycle = duty,
            DcOffset = 10.0,
            EdgeTime = 1e-9,
        });

        var r = circuit.Add(new Resistor(1e3) { Name = "R1", PowerRating = 0.25 });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Output, TargetTerminal = r.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = r.B, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Return, TargetTerminal = ground.Pin });

        var sim = new CircuitSimulator(circuit, new SimulationSettings
        {
            TimeStep = 1e-7,
            MaxTimeStep = 1e-7,
        });

        sim.Reset();
        sim.SolveOperatingPoint();

        // Twenty cycles, so the edges are a vanishing fraction of what is being averaged.
        var averaged = new PowerBudget(sim).Over(0.02).Parts.Single(p => p.Name == "R1");

        Assert.Equal(expected, averaged.Watts, 0.005);

        // And the instant is one of the two extremes, never the average — which is the trap this
        // whole method exists to avoid walking somebody into.
        var instant = new PowerBudget(sim).At().Parts.Single(p => p.Name == "R1").Watts;

        Assert.True(Math.Abs(instant - 0.4) < 1e-3 || instant < 1e-6,
            $"a square wave is at 400 mW or at nothing, never at {instant}");
    }

    /// <summary>An averaged budget says it is one, so nobody reads it as a snapshot.</summary>
    [Fact]
    public void AnAveragedBudgetSaysSo()
    {
        var (sim, _, _, _) = Loop();

        Assert.False(new PowerBudget(sim).At().IsAveraged);
        Assert.True(new PowerBudget(sim).Over(1e-4).IsAveraged);
    }

    /// <summary>
    /// A transistor dissipating through a thermal resistance is ranked on whichever limit it is
    /// nearer — the package's watts or the die's degrees — because they are different limits and a
    /// part can be comfortably inside one and past the other.
    /// </summary>
    [Fact]
    public void TheDieIsALimitOfItsOwn()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(12.0) { Name = "V1" });
        var bias = circuit.Add(new DcVoltageSource(0.85) { Name = "V2" });
        var load = circuit.Add(new Resistor(47.0) { Name = "RL", PowerRating = 5.0 });
        var q = circuit.Add(new BipolarTransistor
        {
            Name = "Q1",
            ThermalResistance = 400.0,
            MaximumJunctionTemperature = 150.0,
        });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Positive, TargetTerminal = load.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = load.B, TargetTerminal = q.Collector });
        circuit.Wires.Add(new WireSegment { SourceTerminal = bias.Positive, TargetTerminal = q.Base });
        circuit.Wires.Add(new WireSegment { SourceTerminal = q.Emitter, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Negative, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = bias.Negative, TargetTerminal = ground.Pin });

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        var budget = new PowerBudget(sim).At();
        var row = budget.Parts.Single(p => p.Name == "Q1");

        Assert.NotNull(row.Celsius);
        Assert.Equal(150.0, row.MaximumCelsius!.Value, 1e-9);

        // The transistor has no watts rating, so its only limit is the die — and that has to be
        // what the verdict comes from rather than the missing one.
        Assert.Equal(0.0, row.Rating, 1e-12);
        Assert.NotEqual(PowerVerdict.Unrated, row.VerdictAt(budget.AmbientCelsius));
    }

    /// <summary>
    /// The worst part comes first, and worst means nearest a limit rather than hottest. A half watt
    /// in a five-watt package is a part doing its job; eighty milliwatts in a hundred-milliwatt one
    /// is the part about to fail.
    /// </summary>
    [Fact]
    public void TheOrderIsHowCloseToFailingRatherThanHowHot()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(10.0) { Name = "V1" });

        // 10 V across 200 Ω is 500 mW in a 5 W part: a tenth of its rating.
        var big = circuit.Add(new Resistor(200.0) { Name = "RBIG", PowerRating = 5.0 });

        // 10 V across 1.25 kΩ is 80 mW in a 100 mW part: four fifths of its rating.
        var small = circuit.Add(new Resistor(1250.0) { Name = "RSMALL", PowerRating = 0.1 });

        var ground = circuit.Add(new Ground { Name = "GND1" });

        foreach (var r in (Resistor[])[big, small])
        {
            circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Positive, TargetTerminal = r.A });
            circuit.Wires.Add(new WireSegment { SourceTerminal = r.B, TargetTerminal = ground.Pin });
        }

        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Negative, TargetTerminal = ground.Pin });

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        var parts = new PowerBudget(sim).At().Parts;

        Assert.Equal("RSMALL", parts[0].Name);
        Assert.True(parts[0].Watts < parts[1].Watts,
            "the part nearest its rating came first despite dissipating less, which is the point");
    }

    /// <summary>
    /// Battery life is capacity over draw, and nothing cleverer. A 500 mAh cell at 100 mA is five
    /// hours.
    /// </summary>
    [Fact]
    public void BatteryLifeIsCapacityOverDraw()
    {
        var (sim, _, _, _) = Loop();

        var budget = new PowerBudget(sim).At();

        // The loop draws 100 mA.
        Assert.Equal(5.0, budget.RunsFor(500)!.Value.TotalHours, 1e-3);
    }

    /// <summary>
    /// A circuit drawing nothing does not last forever, it has no answer — and a nanoamp sleep
    /// current would otherwise overflow the arithmetic on its way to one.
    /// </summary>
    [Fact]
    public void NoDrawGivesNoAnswerRatherThanAnAbsurdOne()
    {
        Assert.Null(PowerBudgetResult.Empty.RunsFor(500));
        Assert.Null(new PowerBudgetResult([], [new SupplyRow("V1", "x", 5, 1e-15, 5e-15)], 0, 25).RunsFor(500));
    }
}
