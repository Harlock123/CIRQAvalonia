using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// Temperature, checked against the coefficients on the datasheets rather than against anything
/// this code produced before. The sign is the whole point: a model that varies only the thermal
/// voltage makes a diode's forward drop rise with temperature, and a real one falls.
/// </summary>
public class TemperatureTests
{
    private static double ForwardDrop(DiodeModel model, double celsius, double series, double supply)
    {
        var circuit = new Circuit();
        var source = circuit.Add(new DcVoltageSource(supply));
        var resistor = circuit.Add(new Resistor(series));
        var diode = circuit.Add(new Diode(model));
        var ground = circuit.Add(new Ground());

        circuit.Connect(source.Negative, ground.Pin);
        circuit.Connect(source.Positive, resistor.A);
        circuit.Connect(resistor.B, diode.Anode);
        circuit.Connect(diode.Cathode, ground.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Settings.TemperatureKelvin = celsius + 273.15;
        sim.Reset();
        sim.SolveOperatingPoint();

        return sim.NodeVoltage(diode.Anode);
    }

    private static double MillivoltsPerDegree(
        DiodeModel model, double series, double supply, double from = 0, double to = 85) =>
        (ForwardDrop(model, to, series, supply) - ForwardDrop(model, from, series, supply))
        / (to - from) * 1000.0;

    // ---- diodes ------------------------------------------------------------

    /// <summary>
    /// The textbook figure, and the one every temperature-compensated circuit is built around.
    /// </summary>
    [Theory]
    [InlineData("1N4148", 4.3e3)]
    [InlineData("1N4001", 4.3e3)]
    public void ASiliconDiodesForwardDropFallsAboutTwoMillivoltsPerDegree(string name, double series)
    {
        var model = DiodeModel.Library.First(m => m.Name == name);

        var tempco = MillivoltsPerDegree(model, series, 5.0);

        Assert.InRange(tempco, -2.4, -1.7);
    }

    [Fact]
    public void AndItIsTheSameCoefficientOverTheWholeRangeRatherThanOnlyNearRoomTemperature()
    {
        var cold = MillivoltsPerDegree(DiodeModel.D1N4148, 4.3e3, 5.0, -40, 0);
        var hot = MillivoltsPerDegree(DiodeModel.D1N4148, 4.3e3, 5.0, 85, 125);

        Assert.InRange(cold, -2.4, -1.7);
        Assert.InRange(hot, -2.4, -1.7);
    }

    [Fact]
    public void ASchottkysDropFallsToo_ButLessSteeply()
    {
        var schottky = MillivoltsPerDegree(DiodeModel.D1N5817, 4.7e3, 5.0);
        var silicon = MillivoltsPerDegree(DiodeModel.D1N4148, 4.3e3, 5.0);

        Assert.InRange(schottky, -2.2, -1.0);
        Assert.True(schottky > silicon, "the Schottky should be the shallower of the two");
    }

    /// <summary>
    /// LEDs fall too, and the wide-gap ones fall faster — which is why a blue or white one driven
    /// from a fixed voltage through a resistor gets brighter as it warms, and then warmer.
    /// </summary>
    [Theory]
    [InlineData("LED (red)", 160.0)]
    [InlineData("LED (green)", 140.0)]
    [InlineData("LED (blue)", 100.0)]
    [InlineData("LED (white)", 100.0)]
    public void AnLedsForwardDropFallsWithTemperature(string name, double series)
    {
        var model = DiodeModel.Library.First(m => m.Name == name);

        Assert.InRange(MillivoltsPerDegree(model, series, 5.0), -5.0, -1.5);
    }

    [Fact]
    public void AtTheTemperatureTheModelsAreQuotedAtNothingHasMoved()
    {
        // 27 degC is the reference, so every parameter is exactly its nominal value there. This is
        // what keeps temperature from quietly changing every other answer in the library.
        foreach (var model in DiodeModel.Library)
            Assert.Equal(model.SaturationCurrent, model.SaturationCurrentAt(300.15), 15);

        foreach (var model in BjtModel.Library)
        {
            Assert.Equal(model.SaturationCurrent, model.SaturationCurrentAt(300.15), 15);
            Assert.Equal(model.ForwardBeta, model.ForwardBetaAt(300.15), 9);
        }
    }

    /// <summary>
    /// Leakage is the other half of the same fact, and the one that catches people: a reverse
    /// measurement taken at room temperature says nothing about a hot circuit.
    /// </summary>
    [Fact]
    public void ReverseSaturationCurrentRoughlyDoublesEveryTenDegrees()
    {
        var model = DiodeModel.D1N4148;

        var room = model.SaturationCurrentAt(300.15);
        var tenHotter = model.SaturationCurrentAt(310.15);
        var fiftyHotter = model.SaturationCurrentAt(350.15);

        Assert.InRange(tenHotter / room, 1.7, 3.0);

        // Fifty degrees is five doublings, give or take — a factor of tens, not percent.
        Assert.InRange(fiftyHotter / room, 15, 120);
    }

    /// <summary>
    /// The three figures the User Guide quotes, at the fixed current a coefficient is specified
    /// at. A resistor-fed diode is not the same measurement: the current rises as the drop falls
    /// and flatters the answer.
    /// </summary>
    [Theory]
    [InlineData(-40.0, 717)]
    [InlineData(27.0, 585)]
    [InlineData(125.0, 384)]
    public void TheGuidesFiguresForADiodeAtOneMilliampAreRight(double celsius, double millivolts)
    {
        var circuit = new Circuit();
        var drive = circuit.Add(new DcCurrentSource(1e-3));
        var diode = circuit.Add(new Diode());
        var ground = circuit.Add(new Ground());

        circuit.Connect(drive.Positive, ground.Pin);
        circuit.Connect(drive.Negative, diode.Anode);
        circuit.Connect(diode.Cathode, ground.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Settings.TemperatureKelvin = celsius + 273.15;
        sim.Reset();
        sim.SolveOperatingPoint();

        Assert.Equal(millivolts, sim.NodeVoltage(diode.Anode) * 1000, 2.0);
    }

    /// <summary>
    /// The guide says plainly that only junction-based models carry temperature, and that the
    /// others come out of a sweep as straight lines because they are silent rather than stable.
    /// If that ever stops being true the guide is wrong, so it is asserted here.
    /// </summary>
    [Fact]
    public void ThePartsTheGuideSaysDoNotCarryTemperatureStillDoNot()
    {
        double Reference(double celsius)
        {
            var circuit = new Circuit();
            var supply = circuit.Add(new DcVoltageSource(12.0));
            var series = circuit.Add(new Resistor(1e3));
            var shunt = circuit.Add(new ShuntReference());
            var ground = circuit.Add(new Ground());

            circuit.Connect(supply.Negative, ground.Pin);
            circuit.Connect(supply.Positive, series.A);
            circuit.Connect(series.B, shunt.Cathode);
            circuit.Connect(shunt.Anode, ground.Pin);
            circuit.Connect(shunt.Reference, shunt.Cathode);

            var sim = new CircuitSimulator(circuit);
            sim.Settings.TemperatureKelvin = celsius + 273.15;
            sim.Reset();
            sim.SolveOperatingPoint();

            return sim.NodeVoltage(series.B);
        }

        Assert.Equal(Reference(-40), Reference(125), 6);
    }

    // ---- transistors -------------------------------------------------------

    private static (double Vbe, double Ic, double Beta) Bias(double celsius, double baseCurrent)
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var drive = circuit.Add(new DcCurrentSource(baseCurrent));
        var transistor = circuit.Add(new BipolarTransistor());
        var load = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, load.A);
        circuit.Connect(load.B, transistor.Collector);
        circuit.Connect(transistor.Emitter, ground.Pin);
        circuit.Connect(drive.Positive, ground.Pin);
        circuit.Connect(drive.Negative, transistor.Base);

        var sim = new CircuitSimulator(circuit);
        sim.Settings.TemperatureKelvin = celsius + 273.15;
        sim.Reset();
        sim.SolveOperatingPoint();

        var collector = (5.0 - sim.NodeVoltage(transistor.Collector)) / 1e3;

        return (sim.NodeVoltage(transistor.Base), collector, collector / baseCurrent);
    }

    [Fact]
    public void ABipolarsBaseEmitterVoltageFallsAsItWarms()
    {
        var cold = Bias(-40, 10e-6);
        var hot = Bias(125, 10e-6);

        Assert.True(hot.Vbe < cold.Vbe,
            $"Vbe went from {cold.Vbe:F3} V at -40 C to {hot.Vbe:F3} V at 125 C, which is backwards");

        // Between one and two and a half millivolts a degree. Not the full textbook 2 mV/degC,
        // because that figure is quoted at a constant collector current and this is at a constant
        // base current — beta rises with temperature, so the collector current rises too and
        // pushes Vbe back up a little.
        var tempco = (hot.Vbe - cold.Vbe) / 165 * 1000;

        Assert.InRange(tempco, -2.5, -1.0);
    }

    [Fact]
    public void AndItsGainClimbs()
    {
        var cold = Bias(-40, 10e-6);
        var room = Bias(27, 10e-6);
        var hot = Bias(125, 10e-6);

        Assert.True(cold.Beta < room.Beta && room.Beta < hot.Beta);

        // Roughly double from one end of the range to the other, which is what a datasheet's
        // gain-versus-temperature curve shows — and why a bias network that depends on beta is a
        // bias network that drifts.
        Assert.InRange(hot.Beta / cold.Beta, 1.5, 3.0);
    }

    // ---- sweeping it -------------------------------------------------------

    [Fact]
    public void TemperatureCanBeSweptLikeAnyOtherParameter()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var resistor = circuit.Add(new Resistor(4.3e3));
        var diode = circuit.Add(new Diode());
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, resistor.A);
        circuit.Connect(resistor.B, diode.Anode);
        circuit.Connect(diode.Cathode, ground.Pin);

        circuit.Probes.Add(new SignalProbe("Vf", diode.Anode, Color.ProbePalette[0]));

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        var result = new DcSweep(sim).Run(new DcSweepRequest(
            SweepTarget.OverTemperature(-40, 125, 34)));

        Assert.Equal(0, result.FailedPoints);
        Assert.Equal("Temperature (°C)", result.XLabel);

        var drops = result.Curves[0].Traces[0].Values;

        // A straight line down, which is what makes a forward-biased diode a usable thermometer.
        for (var i = 1; i < drops.Count; i++)
            Assert.True(drops[i] < drops[i - 1], $"the drop rose between points {i - 1} and {i}");

        var slope = (drops[^1] - drops[0]) / (result.X[^1] - result.X[0]) * 1000;
        Assert.InRange(slope, -2.4, -1.7);

        // And the circuit is put back where it was found.
        Assert.Equal(300.15, sim.Settings.TemperatureKelvin, 6);
    }

    [Fact]
    public void ACircuitCarriesItsOwnTemperatureThroughASaveAndReload()
    {
        var circuit = new Circuit();
        var resistor = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());
        circuit.Connect(resistor.B, ground.Pin);

        circuit.AmbientTemperatureCelsius = 85.0;

        var json = Cirq.Components.Serialization.CircuitSerializer.ToJson(circuit);
        var reloaded = Cirq.Components.Serialization.CircuitSerializer.FromJson(json).Circuit;

        Assert.Equal(85.0, reloaded.AmbientTemperatureCelsius);
    }

    [Fact]
    public void AnOrdinaryCircuitSaysNothingAboutTemperatureAndComesBackAtRoomTemperature()
    {
        var circuit = new Circuit();
        var resistor = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());
        circuit.Connect(resistor.B, ground.Pin);

        var json = Cirq.Components.Serialization.CircuitSerializer.ToJson(circuit);

        Assert.DoesNotContain("Ambient", json);
        Assert.Equal(27.0, Cirq.Components.Serialization.CircuitSerializer.FromJson(json)
            .Circuit.AmbientTemperatureCelsius);
    }
}
