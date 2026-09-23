using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// A part's own dissipation raising its own temperature, and the temperature changing the part.
/// <para>
/// The loop is the point. Every temperature model here already took the circuit's ambient, which
/// is right for something dissipating nothing and wrong for something dissipating watts. What is
/// checked is that the answer is <b>self-consistent</b> — the die sits exactly where the power
/// it is dissipating puts it, and that power is the one the device produces at that temperature —
/// and that the direction each device moves in is the one its datasheet gives.
/// </para>
/// </summary>
public class SelfHeatingTests
{
    private const double Ambient = 25.0;

    private static CircuitSimulator Solved(Circuit circuit, double ambientCelsius = Ambient)
    {
        var sim = new CircuitSimulator(circuit);

        sim.Settings.TemperatureKelvin = ambientCelsius + 273.15;
        sim.Reset();
        sim.SolveOperatingPoint();

        return sim;
    }

    /// <summary>
    /// A power MOSFET switching a resistive load from a rail, which is most of what they do.
    /// <para>
    /// A logic-level part driven well past its threshold, so it sits deep in the triode region
    /// and behaves as a resistance — which is the case where self-heating bites, because
    /// on-resistance climbs with temperature. A small-signal part in saturation does the opposite
    /// and runs <i>cooler</i> as it heats, since mobility falls faster than the threshold does.
    /// </para>
    /// </summary>
    private static (Circuit Circuit, Mosfet Fet) Switch(double thermalResistance, double load = 2.0)
    {
        var circuit = new Circuit();

        var rail = circuit.Add(new DcVoltageSource(12.0) { Name = "V1" });
        var drive = circuit.Add(new DcVoltageSource(10.0) { Name = "V2" });
        var resistor = circuit.Add(new Resistor(load) { Name = "R1" });
        var fet = circuit.Add(new Mosfet
        {
            Name = "Q1",
            Model = MosfetModel.IrlZ44N,
            ThermalResistance = thermalResistance,
        });
        var ground = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, ground.Pin);
        circuit.Connect(drive.Negative, ground.Pin);
        circuit.Connect(rail.Positive, resistor.A);
        circuit.Connect(resistor.B, fet.Drain);
        circuit.Connect(fet.Source, ground.Pin);
        circuit.Connect(fet.Gate, drive.Positive);

        return (circuit, fet);
    }

    /// <summary>
    /// With no thermal resistance given, nothing happens at all: the die is the room and the part
    /// behaves exactly as it always did. This is the default, and it has to stay the default —
    /// a part is a part until somebody says what it is mounted on.
    /// </summary>
    [Fact]
    public void ItIsOffUntilAThermalResistanceIsGiven()
    {
        var (circuit, fet) = Switch(thermalResistance: 0);

        Solved(circuit);

        Assert.False(((Cirq.Core.Simulation.ISelfHeating)fet).IsSelfHeating);
        Assert.Equal(Ambient, fet.JunctionTemperature, 1e-9);
        Assert.Equal(0.0, ((Cirq.Core.Simulation.ISelfHeating)fet).TemperatureRise, 1e-9);
    }

    /// <summary>
    /// The die ends up exactly where its own dissipation puts it: ambient plus watts times degrees
    /// per watt. Both halves are read back off the device, so this is the fixed point rather than
    /// an arithmetic check — the power was produced at that temperature, and that temperature came
    /// from that power.
    /// </summary>
    [Fact]
    public void TheDieSettlesWhereItsOwnDissipationPutsIt()
    {
        const double theta = 40.0;

        var (circuit, fet) = Switch(theta);

        Solved(circuit);

        Assert.True(fet.PowerDissipation > 0);
        Assert.Equal(Ambient, fet.AmbientTemperature, 1e-9);
        Assert.Equal(Ambient + (fet.PowerDissipation * theta), fet.JunctionTemperature, 1e-6);
    }

    /// <summary>
    /// A MOSFET's on-resistance climbs with temperature, so one that is allowed to heat itself
    /// drops more across itself than the same part held at ambient — and burns more doing it.
    /// That is the whole of why a switch has to be specified hot rather than at room temperature.
    /// </summary>
    [Fact]
    public void AHotMosfetDropsMoreThanACoolOne()
    {
        var (cold, coldFet) = Switch(thermalResistance: 0);
        var (hot, hotFet) = Switch(thermalResistance: 60.0);

        var coldSim = Solved(cold);
        var hotSim = Solved(hot);

        var coldDrop = coldSim.NodeVoltage(coldFet.Drain);
        var hotDrop = hotSim.NodeVoltage(hotFet.Drain);

        Assert.True(hotFet.JunctionTemperature > coldFet.JunctionTemperature + 5,
            $"expected a real rise, got {hotFet.JunctionTemperature:0.#} °C");

        Assert.True(hotDrop > coldDrop,
            $"a hot part should drop more: {hotDrop:0.####} V against {coldDrop:0.####} V");

        Assert.True(hotFet.PowerDissipation > coldFet.PowerDissipation);
    }

    /// <summary>
    /// More thermal resistance is a hotter die, every time — a monotone relationship, which is
    /// the sanity check that the feedback is wired the right way round. Wired backwards it would
    /// still converge, to a part that cools itself down by working.
    /// </summary>
    [Fact]
    public void AWorseHeatsinkIsAlwaysAHotterDie()
    {
        double previous = double.NegativeInfinity;

        foreach (var theta in (double[])[0.0, 5.0, 20.0, 40.0])
        {
            var (circuit, fet) = Switch(theta);

            Solved(circuit);

            Assert.True(fet.JunctionTemperature > previous,
                $"θ = {theta} gave {fet.JunctionTemperature:0.#} °C, not above {previous:0.#} °C");

            previous = fet.JunctionTemperature;
        }
    }

    /// <summary>
    /// The room still counts. A part in a warm enclosure starts warm, and the rise its own
    /// dissipation adds is on top of that rather than instead of it.
    /// </summary>
    [Fact]
    public void TheRiseSitsOnTopOfTheAmbient()
    {
        // Modest enough that neither reaches the rated limit, or the comparison would be between
        // two parts both sitting on the same ceiling.
        var (cool, coolFet) = Switch(15.0);
        var (warm, warmFet) = Switch(15.0);

        Solved(cool, ambientCelsius: 25.0);
        Solved(warm, ambientCelsius: 70.0);

        Assert.Equal(25.0, coolFet.AmbientTemperature, 1e-9);
        Assert.Equal(70.0, warmFet.AmbientTemperature, 1e-9);

        Assert.False(coolFet.IsOverTemperature);
        Assert.False(warmFet.IsOverTemperature);

        // Forty-five degrees of ambient, and then a little more, because the warmer part is also
        // the one dissipating more.
        Assert.True(warmFet.JunctionTemperature > coolFet.JunctionTemperature + 45,
            $"{warmFet.JunctionTemperature:0.#} °C against {coolFet.JunctionTemperature:0.#} °C");
    }

    /// <summary>
    /// A diode heating itself sits lower than the same diode held at ambient, at the same current.
    /// <para>
    /// Not by two millivolts a degree, though. That figure belongs to a small-signal diode at a
    /// milliamp; the coefficient is <c>(Vf − Eg/q − 3Vt)/T</c>, which gets <b>less</b> negative as
    /// the forward drop rises, so a rectifier at an amp — nearly a volt across it — moves at about
    /// nine tenths of a millivolt a degree. Asserting two here would be asserting the wrong
    /// physics and would have had to be explained away as a modelling error.
    /// </para>
    /// </summary>
    [Fact]
    public void AHotDiodeDropsLess()
    {
        static (double Drop, Diode Part) Forward(double theta)
        {
            var circuit = new Circuit();

            // Driven from a current source, because the two-millivolts-a-degree coefficient is
            // quoted at a held current. Through a resistor the current rises as the drop falls
            // and partly cancels it, which measures something else entirely.
            var supply = circuit.Add(new DcCurrentSource(1.0) { Name = "I1" });
            var diode = circuit.Add(new Diode(DiodeModel.D1N4001)
            {
                Name = "D1",
                ThermalResistance = theta,
            });
            var ground = circuit.Add(new Ground());

            // A and B, not plus and minus: the stamp draws the current out of A and pushes it
            // into B, so the anode goes on B for the diode to be driven forward.
            circuit.Connect(supply.A, ground.Pin);
            circuit.Connect(supply.B, diode.Anode);
            circuit.Connect(diode.Cathode, ground.Pin);

            var sim = new CircuitSimulator(circuit);

            sim.Settings.TemperatureKelvin = Ambient + 273.15;
            sim.Reset();
            sim.SolveOperatingPoint();

            return (sim.NodeVoltage(diode.Anode), diode);
        }

        var (coolDrop, cool) = Forward(0.0);
        var (hotDrop, hot) = Forward(120.0);

        var rise = hot.JunctionTemperature - cool.JunctionTemperature;

        Assert.True(rise > 10, $"expected the die to heat up, got {rise:0.#} °C");
        Assert.True(hotDrop < coolDrop, $"{hotDrop:0.####} V should be below {coolDrop:0.####} V");

        var slope = (coolDrop - hotDrop) / rise;

        // The coefficient the drop itself implies: (Vf − Eg/q − 3Vt)/T, with silicon's gap and
        // the thermal voltage at the temperature it is sitting at.
        var kelvin = cool.JunctionTemperature + 273.15;
        var thermal = 8.617333262e-5 * kelvin;
        var predicted = ((1.12 + (3 * thermal)) - coolDrop) / kelvin;

        Assert.Equal(predicted, slope, predicted * 0.35);
        Assert.InRange(slope, 0.5e-3, 1.5e-3);
    }

    /// <summary>
    /// A bipolar's base-emitter drop falls with temperature too, so a stage biased from a fixed
    /// base voltage draws <i>more</i> current as it warms — which is the positive feedback that
    /// makes thermal runaway a real failure and not a figure of speech.
    /// </summary>
    [Fact]
    public void ABipolarDrawsMoreCurrentAsItHeats()
    {
        static BipolarTransistor Stage(double theta)
        {
            var circuit = new Circuit();

            var rail = circuit.Add(new DcVoltageSource(12.0) { Name = "V1" });
            var bias = circuit.Add(new DcVoltageSource(0.75) { Name = "V2" });
            var load = circuit.Add(new Resistor(220.0) { Name = "R1" });
            var baseStop = circuit.Add(new Resistor(1e3) { Name = "R2" });
            var transistor = circuit.Add(new BipolarTransistor
            {
                Name = "Q1",
                ThermalResistance = theta,
            });
            var ground = circuit.Add(new Ground());

            circuit.Connect(rail.Negative, ground.Pin);
            circuit.Connect(bias.Negative, ground.Pin);
            circuit.Connect(rail.Positive, load.A);
            circuit.Connect(load.B, transistor.Collector);
            circuit.Connect(bias.Positive, baseStop.A);
            circuit.Connect(baseStop.B, transistor.Base);
            circuit.Connect(transistor.Emitter, ground.Pin);

            var sim = new CircuitSimulator(circuit);

            sim.Settings.TemperatureKelvin = Ambient + 273.15;
            sim.Reset();
            sim.SolveOperatingPoint();

            return transistor;
        }

        var cool = Stage(0.0);
        var hot = Stage(200.0);

        Assert.True(hot.JunctionTemperature > cool.JunctionTemperature + 5);
        Assert.True(Math.Abs(hot.CollectorCurrent) > Math.Abs(cool.CollectorCurrent),
            $"{hot.CollectorCurrent:0.####} A should exceed {cool.CollectorCurrent:0.####} A");
    }

    /// <summary>
    /// A part asked to dissipate more than its heatsink can carry does not settle anywhere: every
    /// degree adds more dissipation than it took to produce, and the loop runs away. It stops at
    /// the temperature the part is rated to and says what happened, rather than leaving the solver
    /// to fail after a hundred iterations with a message about time steps.
    /// </summary>
    [Fact]
    public void ARunawayIsReportedRatherThanLeftToTheSolver()
    {
        var (circuit, fet) = Switch(thermalResistance: 120.0, load: 1.0);

        Solved(circuit);

        Assert.True(fet.IsOverTemperature);
        Assert.Equal(fet.MaximumJunctionTemperature, fet.JunctionTemperature, 1e-9);

        var complaint = Assert.Single(fet.Violations);

        Assert.Contains("150 °C limit", complaint);
        Assert.Contains("heatsink", complaint);
    }

    /// <summary>And a part inside its ratings has nothing to say.</summary>
    [Fact]
    public void APartWithinItsRatingsDoesNotComplain()
    {
        var (circuit, fet) = Switch(thermalResistance: 20.0);

        Solved(circuit);

        Assert.False(fet.IsOverTemperature);
        Assert.Empty(fet.Violations);
        Assert.True(fet.JunctionTemperature < fet.MaximumJunctionTemperature);
    }

    /// <summary>
    /// Thermal mass: in a transient the die follows its dissipation rather than jumping to it,
    /// and after one time constant it has covered 1 − 1/e of the distance. This is why a part
    /// survives a pulse that would destroy it held on, and modelling the rise as instant loses
    /// exactly that.
    /// </summary>
    [Fact]
    public void TheDieLagsItsDissipationInATransient()
    {
        var (circuit, fet) = Switch(thermalResistance: 60.0);

        fet.ThermalTimeConstant = 0.2;

        var sim = new CircuitSimulator(circuit);

        sim.Settings.TemperatureKelvin = Ambient + 273.15;
        sim.Settings.UseInitialConditions = true;
        sim.Settings.TimeStep = 1e-3;
        sim.Reset();
        sim.SolveOperatingPoint();

        // Cold at the instant the supply arrives, whatever it is dissipating.
        Assert.Equal(Ambient, fet.JunctionTemperature, 1.0);

        var start = fet.JunctionTemperature;

        sim.Run(fet.ThermalTimeConstant);

        var afterOne = fet.JunctionTemperature;
        var steady = fet.AmbientTemperature + (fet.PowerDissipation * 60.0);

        // One time constant is 63 % of the way there for a fixed target. The target is not fixed
        // — the part dissipates more as it warms, so the finishing line moves away while the die
        // walks towards it — which puts the measured fraction a little under the textbook one.
        var covered = (afterOne - start) / (steady - start);

        Assert.InRange(covered, 0.45, 0.72);

        // And left alone for long enough it arrives.
        sim.Run(fet.ThermalTimeConstant * 8);

        Assert.Equal(steady, fet.JunctionTemperature, Math.Abs(steady) * 0.02);
    }
}
