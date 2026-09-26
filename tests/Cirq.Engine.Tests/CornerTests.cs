using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// The worst the circuit can ever be, rather than the worst a few hundred random builds happened
/// to be.
/// <para>
/// Every answer here is checked against the arithmetic of the divider being built, because a
/// corner has a closed form: put each resistor at the end of its band that pushes the output the
/// way you are looking, and work it out.
/// </para>
/// </summary>
public class CornerTests
{
    /// <summary>A divider from a stiff supply, with both resistors toleranced.</summary>
    private static (Circuit Circuit, Resistor Top, Resistor Bottom) Divider(
        double top, double bottom, double tolerance = 0.05)
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(10.0));
        var upper = circuit.Add(new Resistor(top) { Name = "R1", Tolerance = tolerance });
        var lower = circuit.Add(new Resistor(bottom) { Name = "R2", Tolerance = tolerance });
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, upper.A);
        circuit.Connect(upper.B, lower.A);
        circuit.Connect(lower.B, ground.Pin);

        circuit.Probes.Add(new SignalProbe("Mid", lower.A, default));

        return (circuit, upper, lower);
    }

    // ---- the closed form ---------------------------------------------------

    /// <summary>
    /// A divider's output is highest when the bottom resistor is at the top of its band and the
    /// top one is at the bottom of its. With two 5 % parts that is 10 × 10.5/(9.5 + 10.5) = 5.25 V,
    /// and the other corner is 10 × 9.5/(10.5 + 9.5) = 4.75 V.
    /// </summary>
    [Fact]
    public void TheCornersAreWhereTheArithmeticSaysTheyAre()
    {
        var (circuit, _, _) = Divider(10e3, 10e3);

        var result = new CornerAnalysis(circuit).Run();

        Assert.True(result.IsUsable, result.Problem);

        Assert.Equal(5.0, result.Nominal, 1e-6);
        Assert.Equal(10.0 * 10.5 / 20.0, result.Highest!.Value, 1e-6);
        Assert.Equal(10.0 * 9.5 / 20.0, result.Lowest!.Value, 1e-6);
    }

    /// <summary>And it says which way each part had to go to get there, which is the recipe.</summary>
    [Fact]
    public void ItSaysWhichWayEachPartWentToGetThere()
    {
        var (circuit, _, _) = Divider(10e3, 10e3);

        var result = new CornerAnalysis(circuit).Run();

        var high = result.Highest!.Settings.ToDictionary(s => s.Name, s => s.Direction);

        // The output goes up when the bottom resistor grows and the top one shrinks.
        Assert.Equal(CornerDirection.Low, high["R1"]);
        Assert.Equal(CornerDirection.High, high["R2"]);

        // And the other extreme is the mirror of it.
        var low = result.Lowest!.Settings.ToDictionary(s => s.Name, s => s.Direction);

        Assert.Equal(CornerDirection.High, low["R1"]);
        Assert.Equal(CornerDirection.Low, low["R2"]);

        Assert.Contains("R1 low", result.Highest.Describe());
    }

    /// <summary>
    /// The spread and the worst error are what a specification is written from, so they are
    /// checked against the same arithmetic.
    /// </summary>
    [Fact]
    public void TheSpreadIsTheDifferenceBetweenTheCorners()
    {
        var (circuit, _, _) = Divider(10e3, 10e3);

        var result = new CornerAnalysis(circuit).Run();

        Assert.Equal(0.5 / 5.0, result.Spread, 1e-6);
        Assert.Equal(0.25 / 5.0, result.WorstFractionalError, 1e-6);
    }

    /// <summary>
    /// The whole reason this exists beside the tolerance analysis: random sampling almost never
    /// lands on the corner, so it reports a smaller spread than the circuit can actually have.
    /// </summary>
    [Fact]
    public void RandomSamplingReportsASmallerSpreadThanTheCornerDoes()
    {
        var (circuit, _, _) = Divider(10e3, 10e3);

        var corners = new CornerAnalysis(circuit).Run();
        var sampled = new MonteCarlo(circuit).Run(new MonteCarloRequest(400, Seed: 3));

        var trace = sampled.Traces[0];

        Assert.True(corners.Highest!.Value >= trace.Maximum,
            $"the corner {corners.Highest.Value:F4} should be at least the sampled maximum {trace.Maximum:F4}");

        Assert.True(corners.Lowest!.Value <= trace.Minimum,
            $"the corner {corners.Lowest.Value:F4} should be at most the sampled minimum {trace.Minimum:F4}");
    }

    /// <summary>
    /// It costs a solve per part plus a handful, not two to the power of the parts — which is what
    /// makes it usable on a circuit with twenty toleranced parts in it.
    /// </summary>
    [Fact]
    public void ItCostsOneSolvePerPartRatherThanTwoToTheNumberOfThem()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(10.0));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);

        // Ten resistors in series, which is a thousand and twenty-four corners if you try them all.
        var previous = supply.Positive;

        for (var i = 0; i < 10; i++)
        {
            var resistor = circuit.Add(new Resistor(1e3) { Name = $"R{i}", Tolerance = 0.05 });

            circuit.Connect(previous, resistor.A);
            previous = resistor.B;
        }

        circuit.Connect(previous, ground.Pin);
        circuit.Probes.Add(new SignalProbe("Tap", circuit.Components.OfType<Resistor>().Last().A, default));

        var result = new CornerAnalysis(circuit).Run();

        Assert.True(result.IsUsable, result.Problem);
        Assert.Equal(10, result.Varied);

        // Nominal, one per part, and one per corner: thirteen, not 1024.
        Assert.True(result.Solves < 20, $"{result.Solves} solves is more than it should need");
    }

    // ---- parts that do not matter ------------------------------------------

    /// <summary>
    /// A part the answer does not depend on is left at its marked value rather than pushed to an
    /// end of its band. Putting it somewhere would be noise in the recipe rather than part of it.
    /// </summary>
    [Fact]
    public void APartTheAnswerDoesNotDependOnIsLeftAlone()
    {
        var (circuit, _, _) = Divider(10e3, 10e3);

        // Hanging off the supply, so it changes nothing at the tap.
        var supply = circuit.Components.OfType<DcVoltageSource>().Single();
        var ground = circuit.Components.OfType<Ground>().Single();
        var bystander = circuit.Add(new Resistor(4.7e3) { Name = "RSPARE", Tolerance = 0.2 });

        circuit.Connect(supply.Positive, bystander.A);
        circuit.Connect(bystander.B, ground.Pin);

        var result = new CornerAnalysis(circuit).Run();

        Assert.True(result.IsUsable, result.Problem);
        Assert.Equal(3, result.Varied);

        Assert.DoesNotContain(result.Highest!.Settings, s => s.Name == "RSPARE");
        Assert.DoesNotContain(result.Lowest!.Settings, s => s.Name == "RSPARE");
    }

    /// <summary>Everything is put back where it was, so the analysis costs the circuit nothing.</summary>
    [Fact]
    public void EveryPartIsPutBackAfterwards()
    {
        var (circuit, top, bottom) = Divider(10e3, 22e3);

        new CornerAnalysis(circuit).Run();

        Assert.Equal(10e3, top.Resistance, 9);
        Assert.Equal(22e3, bottom.Resistance, 9);
    }

    // ---- refusing rather than pretending -----------------------------------

    [Fact]
    public void WithNothingTolerancedItSaysSo()
    {
        var (circuit, _, _) = Divider(10e3, 10e3, tolerance: 0);

        var result = new CornerAnalysis(circuit).Run();

        Assert.False(result.IsUsable);
        Assert.Contains("tolerance", result.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithNoProbeItSaysSo()
    {
        var (circuit, _, _) = Divider(10e3, 10e3);
        circuit.Probes.Clear();

        var result = new CornerAnalysis(circuit).Run();

        Assert.False(result.IsUsable);
        Assert.Contains("probe", result.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A stated probe is measured rather than whichever happens to be first.</summary>
    [Fact]
    public void AStatedProbeIsTheOneMeasured()
    {
        var (circuit, top, _) = Divider(10e3, 10e3);

        var second = new SignalProbe("Top", top.A, default);
        circuit.Probes.Add(second);

        var result = new CornerAnalysis(circuit).Run(new CornerRequest(second));

        Assert.Equal("Top", result.Label);

        // The top of the divider is the supply, which no resistor moves.
        Assert.Equal(10.0, result.Nominal, 1e-6);
        Assert.Equal(0.0, result.Spread, 1e-9);
    }

    // ---- the temperature axis -----------------------------------------------

    /// <summary>
    /// A diode reference: a resistor feeding a diode, whose drop falls about two millivolts a
    /// degree. Nothing here has a tolerance, so the only thing that can move the answer is the
    /// temperature — which makes it the test that proves the axis is being varied at all.
    /// </summary>
    private static Circuit Reference()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(5.0));
        var series = circuit.Add(new Resistor(10e3) { Name = "R1", Tolerance = 0 });
        var diode = circuit.Add(new Cirq.Components.Nonlinear.Diode());
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, series.A);
        circuit.Connect(series.B, diode.Anode);
        circuit.Connect(diode.Cathode, ground.Pin);

        circuit.Probes.Add(new SignalProbe("Vf", diode.Anode, default));

        return circuit;
    }

    [Fact]
    public void WithoutARangeTheTemperatureIsNotVaried()
    {
        var result = new CornerAnalysis(Reference()).Run();

        // Nothing has a tolerance and no range was asked for, so there is nothing to vary and the
        // analysis says so rather than reporting a spread of zero as though it had looked.
        Assert.False(result.IsUsable);
        Assert.Contains("no temperature range", result.Problem);
    }

    [Fact]
    public void ATemperatureRangeIsACornerAxisOfItsOwn()
    {
        var result = new CornerAnalysis(Reference())
            .Run(new CornerRequest(Over: TemperatureRange.Industrial));

        Assert.True(result.IsUsable, result.Problem);

        // A silicon diode drops more when it is cold, so the high corner is the cold end.
        Assert.Contains("temperature low", result.Highest!.Describe());
        Assert.Contains("temperature high", result.Lowest!.Describe());

        // About two millivolts a degree across 125 degrees is a quarter of a volt or so.
        Assert.InRange(result.Highest.Value - result.Lowest.Value, 0.15, 0.35);

        // And the settings name the ends of the range that were used.
        Assert.Equal(-40, result.Highest.Settings.Single().Value);
        Assert.Equal(85, result.Lowest.Settings.Single().Value);
    }

    /// <summary>
    /// The two kinds of variation together, which is the question a datasheet answers: the parts
    /// at their extremes <i>and</i> the temperature at the end of the range that makes it worse.
    /// </summary>
    [Fact]
    public void PartsAndTemperatureAreVariedTogether()
    {
        var circuit = Reference();

        // Give the feed resistor a tolerance, so there are two axes rather than one.
        circuit.Components.OfType<Resistor>().Single().Tolerance = 0.1;

        var both = new CornerAnalysis(circuit).Run(new CornerRequest(Over: TemperatureRange.Industrial));
        var partsOnly = new CornerAnalysis(circuit).Run();

        Assert.True(both.IsUsable, both.Problem);
        Assert.True(partsOnly.IsUsable, partsOnly.Problem);

        Assert.Equal(2, both.Varied);
        Assert.Equal(1, partsOnly.Varied);

        // Both axes appear in the recipe, and the answer is worse than either alone.
        Assert.Equal(2, both.Highest!.Settings.Count);
        Assert.True(both.Spread > partsOnly.Spread,
            $"{both.Spread:P2} should be wider than {partsOnly.Spread:P2}");
    }

    [Fact]
    public void TheTemperatureIsPutBackAfterwards()
    {
        var circuit = Reference();
        circuit.AmbientTemperatureCelsius = 50;

        var result = new CornerAnalysis(circuit).Run(new CornerRequest(Over: TemperatureRange.Industrial));

        Assert.True(result.IsUsable, result.Problem);

        // The analysis has its own settings, so the document's ambient is untouched — and it was
        // read rather than ignored, which is what the nominal being between the corners shows.
        Assert.Equal(50, circuit.AmbientTemperatureCelsius);
        Assert.InRange(result.Nominal, result.Lowest!.Value, result.Highest!.Value);
    }

    /// <summary>
    /// A range of no width is not a range. Asking for one should leave the analysis exactly as it
    /// was rather than adding an axis that cannot move.
    /// </summary>
    [Fact]
    public void ARangeOfNoWidthIsNotAnAxis()
    {
        var (circuit, _, _) = Divider(10e3, 10e3);

        var result = new CornerAnalysis(circuit).Run(new CornerRequest(Over: new TemperatureRange(27, 27)));

        Assert.True(result.IsUsable, result.Problem);
        Assert.Equal(2, result.Varied);
    }
}
