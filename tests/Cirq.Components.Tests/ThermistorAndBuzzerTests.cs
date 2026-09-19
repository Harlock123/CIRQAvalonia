using Cirq.Components.Digital;
using Cirq.Components.Ics;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class ThermistorTests
{
    /// <summary>
    /// The Beta equation is the curve a datasheet actually gives, so the tests check against it
    /// rather than against recorded numbers. A 10k B3950 bead reads about 33 k at freezing and
    /// 2.5 k at 60 C.
    /// </summary>
    [Theory]
    [InlineData(0.0, 33000)]
    [InlineData(25.0, 10000)]
    [InlineData(60.0, 2500)]
    public void AnNtcFollowsTheBetaEquation(double celsius, double expected)
    {
        var thermistor = new Thermistor { Temperature = celsius, ResistanceAt25 = 10e3, Beta = 3950 };

        Assert.Equal(expected, thermistor.Resistance, expected * 0.06);
    }

    [Fact]
    public void AnNtcFallsWithTemperatureAndAPtcRises()
    {
        var ntc = new Thermistor(ThermistorKind.Ntc);
        var ptc = new Thermistor(ThermistorKind.Ptc);

        ntc.Temperature = 0;
        ptc.Temperature = 0;
        var ntcCold = ntc.Resistance;
        var ptcCold = ptc.Resistance;

        ntc.Temperature = 60;
        ptc.Temperature = 60;

        Assert.True(ntc.Resistance < ntcCold, "an NTC should fall as it warms");
        Assert.True(ptc.Resistance > ptcCold, "a PTC should rise as it warms");
    }

    [Fact]
    public void ItReadsItsQuotedResistanceAtTwentyFive()
    {
        foreach (var kind in new[] { ThermistorKind.Ntc, ThermistorKind.Ptc })
        {
            var thermistor = new Thermistor(kind) { ResistanceAt25 = 4700, Temperature = 25.0 };
            Assert.Equal(4700, thermistor.Resistance, 1.0);
        }
    }

    [Fact]
    public void WarmingItIsAnInteractionLikeOperatingASwitch()
    {
        var thermistor = new Thermistor();
        Assert.False(thermistor.IsWarm);

        var cold = thermistor.Resistance;
        thermistor.Interact();

        Assert.True(thermistor.IsWarm);
        // 25 C to 60 C is about a fourfold drop for a B3950 bead.
        Assert.True(thermistor.Resistance < cold / 3, "warming an NTC should drop it sharply");

        thermistor.Interact();
        Assert.False(thermistor.IsWarm);
    }

    /// <summary>
    /// What a thermistor is for: one arm of a divider read by a comparator, so a circuit can act
    /// on temperature. Warming the bead has to flip the output.
    /// </summary>
    [Fact]
    public void ItDrivesAComparatorAcrossATemperatureThreshold()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());

        var thermistor = circuit.Add(new Thermistor());
        var lower = circuit.Add(new Resistor(10e3));
        var upper = circuit.Add(new Resistor(10e3));
        var referenceLower = circuit.Add(new Resistor(10e3));
        var comparator = circuit.Add(new Comparator(ComparatorModel.Lm393));
        var pullUp = circuit.Add(new Resistor(4.7e3));

        circuit.Connect(supply.Negative, gnd.Pin);

        circuit.Connect(supply.Positive, thermistor.A);
        circuit.Connect(thermistor.B, lower.A);
        circuit.Connect(lower.B, gnd.Pin);

        circuit.Connect(supply.Positive, upper.A);
        circuit.Connect(upper.B, referenceLower.A);
        circuit.Connect(referenceLower.B, gnd.Pin);

        circuit.Connect(thermistor.B, comparator.NonInverting);
        circuit.Connect(upper.B, comparator.Inverting);
        circuit.Connect(comparator.PositiveSupply, supply.Positive);
        circuit.Connect(comparator.NegativeSupply, gnd.Pin);
        circuit.Connect(supply.Positive, pullUp.A);
        circuit.Connect(pullUp.B, comparator.Output);

        double Solve()
        {
            var sim = new CircuitSimulator(circuit);
            sim.SolveOperatingPoint();
            return sim.NodeVoltage(comparator.Output);
        }

        // Cold: the NTC is the large resistance, so the divider sits low.
        thermistor.Temperature = Thermistor.ColdTemperature;
        var cold = Solve();

        thermistor.Interact();      // warm it: the NTC falls and the divider rises
        var warm = Solve();

        Assert.True(cold < 1.0, $"cold output was {cold:0.00} V");
        Assert.True(warm > 3.0, $"warm output was {warm:0.00} V");
    }
}

public class BuzzerTests
{
    /// <summary>A buzzer driven from a logic clock, through a series resistor.</summary>
    private static (CircuitSimulator Sim, Buzzer Buzzer) Driven(BuzzerKind kind, double frequency)
    {
        var circuit = new Circuit();
        var clock = circuit.Add(new ClockSource(frequency));
        var buzzer = circuit.Add(new Buzzer(kind));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(clock.Out, buzzer.A);
        circuit.Connect(buzzer.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { UseInitialConditions = true });
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, buzzer);
    }

    private static (CircuitSimulator Sim, Buzzer Buzzer) OnDc(BuzzerKind kind, double volts)
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(volts));
        var buzzer = circuit.Add(new Buzzer(kind));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, buzzer.A);
        circuit.Connect(buzzer.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { UseInitialConditions = true });
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, buzzer);
    }

    /// <summary>
    /// The headline. A passive piezo is a capacitor: a steady voltage across it moves nothing and
    /// makes no sound, which is why a first buzzer circuit wired to a pin that is simply switched
    /// high is silent.
    /// </summary>
    [Fact]
    public void APassivePiezoOnDcIsSilentAndSaysSo()
    {
        var (sim, buzzer) = OnDc(BuzzerKind.Passive, 5.0);

        sim.Run(200e-3);

        Assert.False(buzzer.IsSounding);
        Assert.True(buzzer.IsDrivenWithoutAlternating);
        Assert.Contains("alternating drive", string.Join(" | ", buzzer.Violations));
    }

    [Fact]
    public void AnActiveBuzzerOnTheSameDcSoundsHappily()
    {
        // The identical circuit, with the part that has its own oscillator behind the element.
        var (sim, buzzer) = OnDc(BuzzerKind.Active, 5.0);

        sim.Run(50e-3);

        Assert.True(buzzer.IsSounding);
        Assert.Empty(buzzer.Violations);
        Assert.Equal(buzzer.SelfOscillationFrequency, buzzer.SoundFrequency, 1.0);
    }

    /// <summary>A passive element sounds at whatever it is fed, so the frequency is measured.</summary>
    [Theory]
    [InlineData(1000.0)]
    [InlineData(2500.0)]
    [InlineData(4000.0)]
    public void APassivePiezoSoundsAtTheFrequencyItIsDrivenAt(double frequency)
    {
        var (sim, buzzer) = Driven(BuzzerKind.Passive, frequency);

        sim.Run(20e-3);

        Assert.True(buzzer.IsSounding);
        Assert.Equal(frequency, buzzer.SoundFrequency, frequency * 0.05);
        Assert.Empty(buzzer.Violations);
    }

    [Fact]
    public void AnUndrivenBuzzerIsSilentWithoutComplaining()
    {
        // Nothing connected to it: silent, but there is no mistake to report.
        var (sim, buzzer) = OnDc(BuzzerKind.Passive, 0.0);

        sim.Run(200e-3);

        Assert.False(buzzer.IsSounding);
        Assert.Empty(buzzer.Violations);
    }

    [Fact]
    public void AnActiveBuzzerDrawsItsRatedCurrent()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var buzzer = circuit.Add(new Buzzer(BuzzerKind.Active));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, buzzer.A);
        circuit.Connect(buzzer.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.SolveOperatingPoint();

        Assert.Equal(buzzer.RatedCurrent, Math.Abs(supply.OutputCurrent), buzzer.RatedCurrent * 0.05);
    }

    /// <summary>
    /// A passive element really is a capacitor, so it moves more charge per second as the drive
    /// gets faster — the same fact as the silence on DC, seen from the supply side.
    /// <para>
    /// Mean current, not peak: into a capacitor from a square wave the peak is whatever the
    /// source resistance allows at the edge, and that is the same at any frequency. It is the
    /// number of edges per second that changes.
    /// </para>
    /// </summary>
    [Fact]
    public void APassivePiezoMovesMoreChargePerSecondAsTheDriveGetsFaster()
    {
        double MeanCurrent(double frequency)
        {
            var (sim, buzzer) = Driven(BuzzerKind.Passive, frequency);

            var charge = 0.0;
            var previous = 0.0;
            sim.TimePointAccepted += s =>
            {
                charge += Math.Abs(buzzer.Current) * (s.Time - previous);
                previous = s.Time;
            };

            var window = 20.0 / frequency;
            sim.Run(window);
            return charge / window;
        }

        var slow = MeanCurrent(500.0);
        var fast = MeanCurrent(5000.0);

        // Ten times the edges per second should move several times the charge.
        Assert.True(fast > slow * 3,
            $"{slow * 1e6:0.0} uA at 500 Hz against {fast * 1e6:0.0} uA at 5 kHz");
    }
}
