using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// How much noise a circuit makes, checked against the closed forms noise has.
/// <para>
/// Thermal noise is one of the few things in electronics with an exact answer: a resistance R at
/// temperature T makes 4kTR volts squared per hertz across itself, and no circuit, material or
/// manufacturer changes that. So every figure here is checked against arithmetic rather than
/// against a previous run.
/// </para>
/// </summary>
public class NoiseTests
{
    private const double Room = 300.0;

    private static double Boltzmann => NoisePhysics.Boltzmann;

    /// <summary>A resistor from a node to ground, probed at that node.</summary>
    private static (CircuitSimulator Sim, NoiseRequest Request) Lone(double ohms)
    {
        var circuit = new Circuit();

        var resistor = circuit.Add(new Resistor(ohms) { Name = "R1" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(resistor.B, ground.Pin);

        var probe = new SignalProbe("Out", resistor.A, default);
        circuit.Probes.Add(probe);

        var sim = new CircuitSimulator(circuit);
        sim.Settings.TemperatureKelvin = Room;
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.ResolveProbes();

        return (sim, new NoiseRequest(new AcSweepRequest(1, 1e5, 40), probe));
    }

    // ---- the closed form ---------------------------------------------------

    /// <summary>
    /// A resistor on its own: the voltage across it is √(4kTR) per root hertz, flat with
    /// frequency. 10 kΩ at 300 K is 12.9 nV/√Hz, which is the number on every noise chart.
    /// </summary>
    [Theory]
    [InlineData(1e3)]
    [InlineData(1e4)]
    [InlineData(1e5)]
    public void AResistorsNoiseIsTheSquareRootOfFourKTR(double ohms)
    {
        var (sim, request) = Lone(ohms);

        var result = new NoiseAnalysis(sim).Run(request);

        Assert.True(result.IsUsable, result.Problem);

        var expected = Math.Sqrt(4 * Boltzmann * Room * ohms);

        // Flat across the whole band — thermal noise is white.
        Assert.All(result.Density, d => Assert.Equal(expected, d, expected * 0.01));
    }

    /// <summary>
    /// Two resistors in parallel make the noise of their parallel value, not the sum of their
    /// noises — because each one's noise is shunted by the other exactly as much as it is made.
    /// </summary>
    [Fact]
    public void TwoResistorsInParallelMakeTheNoiseOfTheParallelValue()
    {
        var circuit = new Circuit();

        var a = circuit.Add(new Resistor(10e3) { Name = "R1" });
        var b = circuit.Add(new Resistor(10e3) { Name = "R2" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(a.A, b.A);
        circuit.Connect(a.B, ground.Pin);
        circuit.Connect(b.B, ground.Pin);

        var probe = new SignalProbe("Out", a.A, default);
        circuit.Probes.Add(probe);

        var sim = new CircuitSimulator(circuit);
        sim.Settings.TemperatureKelvin = Room;
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.ResolveProbes();

        var result = new NoiseAnalysis(sim).Run(
            new NoiseRequest(new AcSweepRequest(1, 1e4, 20), probe));

        var expected = Math.Sqrt(4 * Boltzmann * Room * 5e3);

        Assert.Equal(expected, result.Density[0], expected * 0.01);

        // And each contributes half the power, which is the same statement from the other side.
        Assert.Equal(2, result.Contributors.Count);
        Assert.All(result.Contributors, c => Assert.Equal(0.5, c.Share, 0.01));
    }

    /// <summary>
    /// The classic result, and the one worth knowing: an RC low-pass filled with a resistor's own
    /// noise settles at <c>√(kT/C)</c> volts RMS — <b>whatever the resistance is</b>. A bigger
    /// resistor makes more noise per root hertz and rolls it off proportionally sooner, and the
    /// two cancel exactly.
    /// </summary>
    [Theory]
    [InlineData(1e3)]
    [InlineData(1e4)]
    [InlineData(1e5)]
    public void AnRcSettlesAtRootKtOverCWhateverTheResistorIs(double ohms)
    {
        const double farads = 1e-9;

        var circuit = new Circuit();

        var resistor = circuit.Add(new Resistor(ohms) { Name = "R1" });
        var capacitor = circuit.Add(new Capacitor(farads) { Name = "C1" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(resistor.B, capacitor.A);
        circuit.Connect(capacitor.B, ground.Pin);
        circuit.Connect(resistor.A, ground.Pin);

        var probe = new SignalProbe("Out", capacitor.A, default);
        circuit.Probes.Add(probe);

        var sim = new CircuitSimulator(circuit);
        sim.Settings.TemperatureKelvin = Room;
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.ResolveProbes();

        // Far past the corner either way, so the whole of the curve is inside the band.
        var corner = 1.0 / (2 * Math.PI * ohms * farads);

        var result = new NoiseAnalysis(sim).Run(
            new NoiseRequest(new AcSweepRequest(corner / 1e4, corner * 1e4, 100), probe));

        var expected = Math.Sqrt(Boltzmann * Room / farads);

        Assert.Equal(expected, result.Rms, expected * 0.03);
    }

    // ---- the ranking -------------------------------------------------------

    /// <summary>
    /// The half that makes it useful, and a result worth knowing.
    /// <para>
    /// A divider driven from a stiff source is, to noise, simply its two resistors in parallel —
    /// the source end is an AC ground. So the output noise is √(4kT·R∥), set almost entirely by
    /// the <b>smaller</b> resistor, and each generator's share goes as 1/R: 100 kΩ beside 1 kΩ
    /// contributes one part in a hundred and one. Making the big one bigger changes nothing worth
    /// measuring; the small one is the whole answer.
    /// </para>
    /// </summary>
    [Fact]
    public void TheRankingSaysWhichResistorToChange()
    {
        var circuit = new Circuit();

        var top = circuit.Add(new Resistor(100e3) { Name = "RBIG" });
        var bottom = circuit.Add(new Resistor(1e3) { Name = "RSMALL" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);
        circuit.Connect(top.A, ground.Pin);

        var probe = new SignalProbe("Out", bottom.A, default);
        circuit.Probes.Add(probe);

        var sim = new CircuitSimulator(circuit);
        sim.Settings.TemperatureKelvin = Room;
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.ResolveProbes();

        var result = new NoiseAnalysis(sim).Run(
            new NoiseRequest(new AcSweepRequest(1, 1e4, 20), probe));

        var shares = result.Contributors.ToDictionary(c => c.Name, c => c.Share);

        // 1/R each, normalised: one part in a hundred and one against a hundred.
        Assert.Equal(1.0 / 101.0, shares["RBIG thermal"], 0.002);
        Assert.Equal(100.0 / 101.0, shares["RSMALL thermal"], 0.002);

        // Largest first, and the shares account for all of it.
        Assert.Equal("RSMALL thermal", result.Contributors[0].Name);
        Assert.Equal(1.0, result.Contributors.Sum(c => c.Share), 0.001);

        // And the level itself is the parallel value's, not either resistor's.
        var parallel = Math.Sqrt(4 * Boltzmann * Room * (1.0 / ((1 / 100e3) + (1 / 1e3))));

        Assert.Equal(parallel, result.Density[0], parallel * 0.01);
    }

    /// <summary>Each contributor's own RMS adds in quadrature to the total, because powers add.</summary>
    [Fact]
    public void TheContributorsAddInQuadratureToTheTotal()
    {
        var circuit = new Circuit();

        var a = circuit.Add(new Resistor(10e3) { Name = "R1" });
        var b = circuit.Add(new Resistor(22e3) { Name = "R2" });
        var c = circuit.Add(new Resistor(4.7e3) { Name = "R3" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(a.A, b.A);
        circuit.Connect(b.B, c.A);
        circuit.Connect(a.B, ground.Pin);
        circuit.Connect(c.B, ground.Pin);

        var probe = new SignalProbe("Out", b.A, default);
        circuit.Probes.Add(probe);

        var sim = new CircuitSimulator(circuit);
        sim.Settings.TemperatureKelvin = Room;
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.ResolveProbes();

        var result = new NoiseAnalysis(sim).Run(
            new NoiseRequest(new AcSweepRequest(1, 1e4, 20), probe));

        Assert.Equal(3, result.Contributors.Count);

        var quadrature = Math.Sqrt(result.Contributors.Sum(c => c.Rms * c.Rms));

        Assert.Equal(result.Rms, quadrature, result.Rms * 1e-6);
    }

    // ---- temperature -------------------------------------------------------

    /// <summary>
    /// Thermal noise is proportional to the square root of absolute temperature, which is why
    /// cooling a front end helps and why it helps so much less than people expect: liquid
    /// nitrogen is a factor of three, not a factor of ten.
    /// </summary>
    [Fact]
    public void ColderIsQuieterByTheSquareRootOfTheRatio()
    {
        double DensityAt(double kelvin)
        {
            var (sim, request) = Lone(10e3);
            sim.Settings.TemperatureKelvin = kelvin;
            sim.Reset();
            sim.SolveOperatingPoint();

            return new NoiseAnalysis(sim).Run(request).Density[0];
        }

        var warm = DensityAt(300.0);
        var cold = DensityAt(77.0);

        Assert.Equal(Math.Sqrt(77.0 / 300.0), cold / warm, 0.01);
    }

    // ---- refusing rather than misleading -----------------------------------

    [Fact]
    public void ProbingGroundIsRefused()
    {
        var circuit = new Circuit();

        var resistor = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(resistor.B, ground.Pin);

        var probe = new SignalProbe("Gnd", ground.Pin, default);
        circuit.Probes.Add(probe);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.ResolveProbes();

        var result = new NoiseAnalysis(sim).Run(
            new NoiseRequest(new AcSweepRequest(1, 1e3, 10), probe));

        Assert.False(result.IsUsable);
        Assert.NotNull(result.Problem);
    }

    /// <summary>
    /// A circuit of ideal parts is silent, and says so rather than reporting zero as though zero
    /// were a measurement.
    /// </summary>
    [Fact]
    public void ACircuitWithNothingNoisyInItSaysSo()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(5.0));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);

        var probe = new SignalProbe("Out", supply.Positive, default);
        circuit.Probes.Add(probe);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.ResolveProbes();

        var result = new NoiseAnalysis(sim).Run(
            new NoiseRequest(new AcSweepRequest(1, 1e3, 10), probe));

        Assert.False(result.IsUsable);
        Assert.Contains("noise", result.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The analysis leaves the simulator where it found it, as the AC sweep does.</summary>
    [Fact]
    public void TheSimulatorIsPutBack()
    {
        var (sim, request) = Lone(10e3);

        var mode = sim.State.Mode;

        new NoiseAnalysis(sim).Run(request);

        Assert.Equal(mode, sim.State.Mode);
    }

    // ---- junctions ---------------------------------------------------------

    /// <summary>
    /// A diode's shot noise is <c>2qI</c>, and the voltage it makes across the diode's own dynamic
    /// resistance <c>r = nkT/qI</c> works out at <c>√(2·n·kT·r)</c> — which is <b>half</b> the
    /// thermal noise of a resistor of that value, and that factor of two is a real and slightly
    /// surprising fact about junctions.
    /// </summary>
    [Fact]
    public void ADiodesShotNoiseIsHalfTheThermalNoiseOfItsOwnSlope()
    {
        const double milliamps = 1e-3;

        var circuit = new Circuit();

        var source = circuit.Add(new DcCurrentSource(milliamps));
        var diode = circuit.Add(new Cirq.Components.Nonlinear.Diode { Name = "D1" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(source.Negative, diode.Anode);
        circuit.Connect(diode.Cathode, ground.Pin);
        circuit.Connect(source.Positive, ground.Pin);

        var probe = new SignalProbe("Out", diode.Anode, default);
        circuit.Probes.Add(probe);

        var sim = new CircuitSimulator(circuit);
        sim.Settings.TemperatureKelvin = Room;
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.ResolveProbes();

        var result = new NoiseAnalysis(sim).Run(
            new NoiseRequest(new AcSweepRequest(10, 1e5, 20), probe));

        Assert.True(result.IsUsable, result.Problem);

        // The junction's own slope, from the current it settled at.
        var n = diode.Model.EmissionCoefficient;
        var thermalVolt = n * Boltzmann * Room / NoisePhysics.ElementaryCharge;
        var slope = thermalVolt / Math.Abs(diode.Current);

        Assert.Equal(milliamps, diode.Current, milliamps * 0.01);

        // √(2q·I)·r, which is √(2·n·kT·r).
        var expected = Math.Sqrt(2 * NoisePhysics.ElementaryCharge * Math.Abs(diode.Current)) * slope;

        Assert.Equal(expected, result.Density[0], expected * 0.05);

        // Which is 1/√2 of what a resistor of the same slope would make, allowing for n.
        var resistorWould = Math.Sqrt(4 * Boltzmann * Room * slope);

        Assert.Equal(Math.Sqrt(n / 2.0), result.Density[0] / resistorWould, 0.05);
    }

    /// <summary>
    /// Shot noise does not depend on temperature and thermal noise does — which is the cleanest
    /// way to tell that the two are being modelled as different things rather than as one fudge.
    /// Ten times the current is √10 times the shot noise, whatever the temperature.
    /// </summary>
    [Fact]
    public void ShotNoiseFollowsTheCurrentRatherThanTheTemperature()
    {
        double DensityAt(double amps)
        {
            var circuit = new Circuit();

            var source = circuit.Add(new DcCurrentSource(amps));
            var diode = circuit.Add(new Cirq.Components.Nonlinear.Diode { Name = "D1" });
            var ground = circuit.Add(new Ground());

            circuit.Connect(source.Negative, diode.Anode);
            circuit.Connect(diode.Cathode, ground.Pin);
            circuit.Connect(source.Positive, ground.Pin);

            var probe = new SignalProbe("Out", diode.Anode, default);
            circuit.Probes.Add(probe);

            var sim = new CircuitSimulator(circuit);
            sim.Settings.TemperatureKelvin = Room;
            sim.Reset();
            sim.SolveOperatingPoint();
            sim.ResolveProbes();

            return new NoiseAnalysis(sim).Run(
                new NoiseRequest(new AcSweepRequest(10, 1e4, 10), probe)).Density[0];
        }

        // The current noise rises as √I, but the slope it works into falls as 1/I — so the voltage
        // noise across the junction falls as 1/√I. Ten times the current is √10 times quieter.
        Assert.Equal(Math.Sqrt(10.0), DensityAt(1e-4) / DensityAt(1e-3), 0.1);
    }

    /// <summary>
    /// A bipolar has two shot generators, not one: the base current and the collector current
    /// each cross a junction. Both show up by name, and which of them dominates depends on the
    /// impedance the base is driven from — which is the whole of why a microphone preamp and a
    /// photodiode preamp are biased nothing like each other.
    /// <para>
    /// Driven from a current source, the base sees an infinite source impedance: every bit of its
    /// shot noise goes into the transistor and comes out multiplied by beta. So the base's
    /// generator leads, by about that factor.
    /// </para>
    /// </summary>
    [Fact]
    public void ABipolarReportsBothOfItsShotGenerators()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(10.0));
        // 2 µA of base current is about 200 µA of collector current, which drops 2 V of the 10 V
        // across the load — well inside the active region, where the collector is a current
        // source and its shot noise has the full load impedance to work into.
        var bias = circuit.Add(new DcCurrentSource(2e-6));
        var load = circuit.Add(new Resistor(10e3) { Name = "RL" });
        var transistor = circuit.Add(new Cirq.Components.Nonlinear.BipolarTransistor { Name = "Q1" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, load.A);
        circuit.Connect(load.B, transistor.Collector);
        circuit.Connect(bias.Negative, transistor.Base);
        circuit.Connect(bias.Positive, ground.Pin);
        circuit.Connect(transistor.Emitter, ground.Pin);

        var probe = new SignalProbe("Out", transistor.Collector, default);
        circuit.Probes.Add(probe);

        var sim = new CircuitSimulator(circuit);
        sim.Settings.TemperatureKelvin = Room;
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.ResolveProbes();

        var result = new NoiseAnalysis(sim).Run(
            new NoiseRequest(new AcSweepRequest(10, 1e5, 20), probe));

        Assert.True(result.IsUsable, result.Problem);

        Assert.Contains(result.Contributors, c => c.Name == "Q1 shot (base)");
        Assert.Contains(result.Contributors, c => c.Name == "Q1 shot (collector)");
        Assert.Contains(result.Contributors, c => c.Name == "RL thermal");

        // Beta times the base's noise against the collector's own, so the base leads.
        Assert.Equal("Q1 shot (base)", result.Contributors[0].Name);
    }

    /// <summary>
    /// The other half of the same trade. Drive the same base from something stiff and its shot
    /// noise is shunted away before it can be amplified, leaving the collector's own generator as
    /// the one that matters. Same transistor, same current, opposite answer — which is why "which
    /// part is noisy" is not a question that can be answered about a part on its own.
    /// </summary>
    [Fact]
    public void AStiffSourceAtTheBaseLeavesTheCollectorsShotNoiseInCharge()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(10.0));
        var drive = circuit.Add(new DcVoltageSource(0.62) { Name = "VB" });
        var load = circuit.Add(new Resistor(10e3) { Name = "RL" });
        var degeneration = circuit.Add(new Resistor(100) { Name = "RE" });
        var transistor = circuit.Add(new Cirq.Components.Nonlinear.BipolarTransistor { Name = "Q1" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, load.A);
        circuit.Connect(load.B, transistor.Collector);
        circuit.Connect(drive.Positive, transistor.Base);
        circuit.Connect(drive.Negative, ground.Pin);
        circuit.Connect(transistor.Emitter, degeneration.A);
        circuit.Connect(degeneration.B, ground.Pin);

        var probe = new SignalProbe("Out", transistor.Collector, default);
        circuit.Probes.Add(probe);

        var sim = new CircuitSimulator(circuit);
        sim.Settings.TemperatureKelvin = Room;
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.ResolveProbes();

        var result = new NoiseAnalysis(sim).Run(
            new NoiseRequest(new AcSweepRequest(10, 1e5, 20), probe));

        Assert.True(result.IsUsable, result.Problem);

        var shares = result.Contributors.ToDictionary(c => c.Name, c => c.Share);

        Assert.True(shares["Q1 shot (collector)"] > shares["Q1 shot (base)"],
            $"a stiff drive should leave the collector in charge: collector " +
            $"{shares["Q1 shot (collector)"]:0.###} against base {shares["Q1 shot (base)"]:0.###}");
    }

    // ---- flicker -----------------------------------------------------------

    /// <summary>
    /// Flicker noise rises as 1/f in power and 1/√f in amplitude, so on log axes it is a straight
    /// line — and there is no low enough frequency for it to stop mattering. At the corner the
    /// density is √2 times the white floor, which is what "corner" means.
    /// </summary>
    [Fact]
    public void FlickerNoiseIsTheWhiteFloorTimesOnePlusTheCornerOverF()
    {
        const double white = 4e-18;
        const double corner = 1e3;

        // At the corner the two terms are equal: twice the power, √2 the amplitude.
        Assert.Equal(2 * white, NoisePhysics.Flicker(white, corner, corner), white * 1e-9);

        // A decade below, ten times the power.
        Assert.Equal(11 * white, NoisePhysics.Flicker(white, corner, corner / 10), white * 1e-9);

        // Far above it, the floor and nothing else.
        Assert.Equal(white, NoisePhysics.Flicker(white, corner, corner * 1e4), white * 1e-3);

        // And a part with no corner has no flicker at all.
        Assert.Equal(white, NoisePhysics.Flicker(white, 0, 1e-3), white * 1e-9);
    }

    /// <summary>
    /// The reason a low-frequency front end is built out of bipolars: a MOSFET's flicker corner is
    /// three orders of magnitude higher, so at ten hertz it is buried in 1/f noise where a bipolar
    /// is still near its shot floor. Both parts here are set to their own typical corners.
    /// </summary>
    [Fact]
    public void AMosfetsFlickerCornerIsFarAboveABipolars()
    {
        var mosfet = new Cirq.Components.Nonlinear.Mosfet();
        var bipolar = new Cirq.Components.Nonlinear.BipolarTransistor();

        Assert.True(mosfet.FlickerCornerHz > bipolar.FlickerCornerHz * 100,
            $"{mosfet.FlickerCornerHz:0} Hz against {bipolar.FlickerCornerHz:0} Hz");

        // Which at ten hertz is the difference between a hundredfold rise and a thirtyfold one.
        var atTen = NoisePhysics.Flicker(1.0, mosfet.FlickerCornerHz, 10)
                    / NoisePhysics.Flicker(1.0, bipolar.FlickerCornerHz, 10);

        Assert.True(atTen > 100, $"the MOSFET should be far worse at ten hertz, not {atTen:0.#}×");
    }

    /// <summary>
    /// And it shows up in a measurement rather than only in the arithmetic: the same transistor
    /// with its corner switched off is flat, and with it on rises towards DC.
    /// </summary>
    [Fact]
    public void TurningTheFlickerCornerOffMakesTheNoiseFlat()
    {
        double Ratio(double corner)
        {
            var circuit = new Circuit();

            var supply = circuit.Add(new DcVoltageSource(10.0));
            var bias = circuit.Add(new DcCurrentSource(2e-6));
            var load = circuit.Add(new Resistor(10e3) { Name = "RL" });
            var transistor = circuit.Add(new Cirq.Components.Nonlinear.BipolarTransistor
            {
                Name = "Q1",
                FlickerCornerHz = corner,
            });
            var ground = circuit.Add(new Ground());

            circuit.Connect(supply.Negative, ground.Pin);
            circuit.Connect(supply.Positive, load.A);
            circuit.Connect(load.B, transistor.Collector);
            circuit.Connect(bias.Negative, transistor.Base);
            circuit.Connect(bias.Positive, ground.Pin);
            circuit.Connect(transistor.Emitter, ground.Pin);

            var probe = new SignalProbe("Out", transistor.Collector, default);
            circuit.Probes.Add(probe);

            var sim = new CircuitSimulator(circuit);
            sim.Settings.TemperatureKelvin = Room;
            sim.Reset();
            sim.SolveOperatingPoint();
            sim.ResolveProbes();

            var result = new NoiseAnalysis(sim).Run(
                new NoiseRequest(new AcSweepRequest(1, 1e4, 20), probe));

            return result.Density[0] / result.Density[^1];
        }

        Assert.Equal(1.0, Ratio(0), 0.05);
        Assert.True(Ratio(300) > 5, $"with a 300 Hz corner, 1 Hz should be far noisier: {Ratio(300):0.#}×");
    }
}
