using Cirq.Components.Passive;
using Cirq.Components.Serialization;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The tungsten lamp, checked against the two figures anybody knows about one: its hot resistance
/// is V²/P, and its cold resistance is about a tenth of that — which is the inrush.
/// </summary>
public class IncandescentLampTests
{
    private static (Circuit Circuit, IncandescentLamp Lamp, DcVoltageSource Supply) Lit(double volts)
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(volts));
        var lamp = circuit.Add(new IncandescentLamp());
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, lamp.A);
        circuit.Connect(lamp.B, gnd.Pin);

        return (circuit, lamp, supply);
    }

    [Fact]
    public void ItsHotResistanceIsTheRatingAndItsColdOneATenthOfThat()
    {
        var lamp = new IncandescentLamp(12.0, 5.0);

        // 12 V at 5 W is 28.8 Ω, and the cold figure is what a meter reads on a lamp that is off.
        Assert.Equal(28.8, lamp.HotResistance, 1e-9);
        Assert.Equal(2.88, lamp.ColdResistance, 1e-9);
        Assert.Equal(lamp.ColdResistance, lamp.ResistanceAt(300.0), 1e-9);
        Assert.Equal(lamp.HotResistance, lamp.ResistanceAt(IncandescentLamp.RatedFilamentKelvin), 1e-9);
    }

    [Fact]
    public void AtItsRatedVoltageItDrawsItsRatedPower()
    {
        var (circuit, lamp, _) = Lit(12.0);

        var sim = new CircuitSimulator(circuit);
        sim.SolveOperatingPoint();

        // 5 W at 12 V is 417 mA. The room is at 27 °C rather than the 300 K the rating is quoted
        // against, which is worth a fraction of a percent and no more.
        Assert.Equal(5.0 / 12.0, lamp.Current, 0.01);
        Assert.Equal(5.0, lamp.Power, 0.05);
        Assert.Equal(IncandescentLamp.RatedFilamentKelvin, lamp.FilamentKelvin, 10.0);
        Assert.Equal(1.0, lamp.LightOutput, 0.05);
    }

    /// <summary>
    /// The reason the part exists. Switched on cold, the first instant sees the cold resistance —
    /// ten times the running current — and the filament warms over tens of milliseconds to settle
    /// at the rated figure.
    /// </summary>
    [Fact]
    public void SwitchedOnColdItDrawsAnInrushOfTenTimesItsRunningCurrent()
    {
        var (circuit, lamp, _) = Lit(12.0);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { UseInitialConditions = true, TimeStep = 20e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();

        var peak = 0.0;
        sim.Step();
        peak = Math.Max(peak, lamp.Current);

        // 12 V into 2.88 Ω.
        Assert.Equal(12.0 / 2.88, peak, 0.1);

        sim.Run(0.4);

        Assert.Equal(5.0 / 12.0, lamp.Current, 0.01);
        Assert.InRange(peak / lamp.Current, 9.0, 11.0);
    }

    [Fact]
    public void TheInrushIsOverWithinAFewTimeConstants()
    {
        var (circuit, lamp, _) = Lit(12.0);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { UseInitialConditions = true, TimeStep = 20e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();

        sim.Run(2e-3);
        var early = lamp.Current;

        sim.Run(0.2);
        var later = lamp.Current;

        // Two milliseconds in it is still well above its running current; four time constants on
        // it has settled to within a few percent of it.
        Assert.True(early > 1.5 * later, $"at 2 ms it drew {early:0.00} A against {later:0.00} A settled");
        Assert.Equal(5.0 / 12.0, later, 0.02);
    }

    /// <summary>
    /// Half the voltage is not half the light. The filament runs cooler, so its resistance is lower
    /// and it takes more than a quarter of the power — and it gives about a tenth of the light,
    /// which is the V^3.4 law lamp makers quote.
    /// </summary>
    [Fact]
    public void AtHalfVoltageItGivesAboutATenthOfTheLight()
    {
        var (circuit, lamp, _) = Lit(6.0);

        var sim = new CircuitSimulator(circuit);
        sim.SolveOperatingPoint();

        Assert.True(lamp.Power > 5.0 / 4.0, $"at half voltage it took {lamp.Power:0.00} W");
        Assert.True(lamp.Resistance < lamp.HotResistance);
        Assert.InRange(lamp.LightOutput, Math.Pow(0.5, 3.4) * 0.7, Math.Pow(0.5, 3.4) * 1.4);
        Assert.True(lamp.Brightness is > 0 and < 1);
    }

    /// <summary>
    /// Thermal mass is why a lamp on the mains does not flicker at 100 Hz: the filament cannot cool
    /// appreciably in a half-cycle. Driven by a slow square wave instead it has time to go out.
    /// </summary>
    [Fact]
    public void ItsFilamentAveragesFastPowerButFollowsSlowPower()
    {
        double Swing(double frequency)
        {
            var circuit = new Circuit();
            var source = circuit.Add(new FunctionGenerator(Waveform.Square, frequency, 12.0) { DcOffset = 6.0 });
            var lamp = circuit.Add(new IncandescentLamp());
            var gnd = circuit.Add(new Ground());

            circuit.Connect(source.Return, gnd.Pin);
            circuit.Connect(source.Output, lamp.A);
            circuit.Connect(lamp.B, gnd.Pin);

            var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 50e-6 });
            sim.Reset();
            sim.SolveOperatingPoint();
            sim.Run(0.5);

            var low = double.MaxValue;
            var high = double.MinValue;
            var end = sim.Time + (2.0 / frequency);

            while (sim.Time < end)
            {
                sim.Step();
                low = Math.Min(low, lamp.FilamentKelvin);
                high = Math.Max(high, lamp.FilamentKelvin);
            }

            return high - low;
        }

        var fast = Swing(200.0);
        var slow = Swing(2.0);

        Assert.True(fast < 150, $"at 200 Hz the filament swung {fast:0} K");
        Assert.True(slow > 1000, $"at 2 Hz the filament swung only {slow:0} K");
    }

    [Fact]
    public void RunningItWellOverItsRatingIsCalledOut()
    {
        var (circuit, lamp, _) = Lit(18.0);

        var sim = new CircuitSimulator(circuit);
        sim.SolveOperatingPoint();

        Assert.NotEmpty(lamp.Violations);

        var (normal, ok, _) = Lit(12.0);
        new CircuitSimulator(normal).SolveOperatingPoint();
        Assert.Empty(ok.Violations);
    }

    [Fact]
    public void ItsRatingSurvivesTheFile()
    {
        var circuit = new Circuit();
        circuit.Add(new IncandescentLamp(24.0, 21.0) { ColdResistanceRatio = 0.07, ThermalTimeConstant = 0.12 });

        var back = CircuitSerializer.FromJson(CircuitSerializer.ToJson(circuit)).Circuit
            .Components.OfType<IncandescentLamp>().Single();

        Assert.Equal(24.0, back.RatedVoltage);
        Assert.Equal(21.0, back.RatedPower);
        Assert.Equal(0.07, back.ColdResistanceRatio);
        Assert.Equal(0.12, back.ThermalTimeConstant);
        Assert.Equal("24V 21W", back.ValueLabel);
    }
}
