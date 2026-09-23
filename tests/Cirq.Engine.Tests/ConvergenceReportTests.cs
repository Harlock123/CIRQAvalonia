using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// What the solver says when it cannot find an answer.
/// <para>
/// The old message was "failed to converge, try a smaller time step", which is only useful to
/// somebody who already knows what is wrong — and that is the one person who does not need it.
/// What is checked here is that the failure names somewhere on the drawing to go and look.
/// </para>
/// </summary>
public class ConvergenceReportTests
{
    /// <summary>
    /// A diode driven straight from an ideal source, with the solver allowed one iteration.
    /// <para>
    /// The iteration limit is the test's doing, and deliberately: what is under test is the
    /// <i>reporting</i>, not how robust the solver is, and a circuit that genuinely defeats it is
    /// both hard to write down and liable to stop defeating it the next time the limiter improves.
    /// One iteration reaches exactly the state the report describes — a circuit still moving when
    /// the solver ran out of attempts — deterministically, which a pathological circuit would not.
    /// </para>
    /// <para>
    /// The circuit is a real one for all that: an unlimited junction across a stiff source is the
    /// commonest thing a beginner draws, and the reason it usually works here is the bulk
    /// resistance inside the diode model rather than anything in the drawing.
    /// </para>
    /// </summary>
    private static ConvergenceException Impossible()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(5.0) { Name = "V1" });
        var diode = circuit.Add(new Diode(DiodeModel.D1N4148) { Name = "D1" });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, diode.Anode);
        circuit.Connect(diode.Cathode, ground.Pin);

        var sim = new CircuitSimulator(circuit);

        sim.Settings.MaxNewtonIterations = 1;
        sim.Reset();

        return Assert.Throws<ConvergenceException>(sim.SolveOperatingPoint);
    }

    [Fact]
    public void ItSaysWhenItGaveUpAndHowFarItWasStillMoving()
    {
        var report = Impossible().Report;

        Assert.True(report.Iterations > 0);
        Assert.True(report.Residual > 0);
        Assert.Contains("still moving", report.Describe());
    }

    /// <summary>
    /// The part that turns a shrug into a place to start: the net that was moving most, and what
    /// is attached to it. Both are things you can go and look at on the drawing.
    /// </summary>
    [Fact]
    public void ItNamesTheNetAndWhatIsOnIt()
    {
        var report = Impossible().Report;

        Assert.NotNull(report.WorstNode);
        Assert.NotEmpty(report.WorstParts);

        // The junction that cannot be satisfied is between the source and the diode.
        Assert.Contains("D1", report.WorstParts);

        var described = report.Describe();

        Assert.Contains(report.WorstNode, described);
        Assert.Contains("D1", described);
    }

    /// <summary>
    /// A part is only named as unsettled when it says so itself, and the two questions are
    /// genuinely different: a node can still be moving while every device on it is perfectly
    /// happy with where it has been put. Blaming a part that did not complain would send somebody
    /// to look at the wrong thing, which is the failure this whole report exists to avoid.
    /// </summary>
    [Fact]
    public void APartThatSaysItSettledIsNotBlamed()
    {
        var report = Impossible().Report;

        // The node is still moving — that is why the solve failed.
        Assert.True(report.Residual > 0);

        // But the diode's own limiter is content, so it is not accused.
        Assert.Empty(report.Unsettled);
        Assert.DoesNotContain("has not settled", report.Describe());
    }

    /// <summary>And when a part does say so, it is named — that is the device's own opinion.</summary>
    [Fact]
    public void APartThatSaysItHasNotSettledIsNamed()
    {
        var one = new ConvergenceReport(0, 10, 1.0, "N3", ["Q1"], ["Q1"]);
        var two = new ConvergenceReport(0, 10, 1.0, "N3", ["Q1"], ["Q1", "D2"]);

        Assert.Contains("Q1 says it has not settled", one.Describe());
        Assert.Contains("Q1 and D2 say they have not settled", two.Describe());
    }

    /// <summary>
    /// And it still gives the remedies, because a named node plus the usual causes is a place to
    /// start where either alone is not.
    /// </summary>
    [Fact]
    public void ItStillSaysWhatIsUsuallyWrong()
    {
        var described = Impossible().Report.Describe();

        Assert.Contains("no DC path to ground", described);
        Assert.Contains("smaller time step", described);
    }

    /// <summary>
    /// A branch current is a current inside a part rather than a place on the drawing, so it is
    /// left unnamed rather than reported as a row number — which would be worse than silence.
    /// </summary>
    [Fact]
    public void ABranchUnknownIsLeftUnnamedRatherThanCalledARowNumber()
    {
        var report = new ConvergenceReport(0, 10, 1.0, null, [], ["Q1"]);

        var described = report.Describe();

        Assert.DoesNotContain("net", described, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Q1 says it has not settled", described);
    }

    /// <summary>A circuit that solves does not produce one of these at all.</summary>
    [Fact]
    public void AWorkingCircuitNeverGetsHere()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(5.0) { Name = "V1" });
        var series = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var diode = circuit.Add(new Diode(DiodeModel.D1N4148) { Name = "D1" });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, series.A);
        circuit.Connect(series.B, diode.Anode);
        circuit.Connect(diode.Cathode, ground.Pin);

        var sim = new CircuitSimulator(circuit);

        sim.Reset();
        sim.SolveOperatingPoint();

        // The one resistor is the whole difference between this and the circuit above.
        Assert.InRange(sim.NodeVoltage(diode.Anode), 0.55, 0.85);
    }
}
