using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Electromechanical;
using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Engine.Simulation;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The batch that closed the last of the guide sections describing a circuit that did not ship,
/// and most of the remaining parts with nothing demonstrating them.
/// </summary>
public class RemainingPartsExampleTests
{
    private static MainWindowViewModel Load(string name)
    {
        var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        vm.Scope.ClearProbes();
        Examples.All.Single(e => e.Name == name).Build(vm);
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);
        return vm;
    }

    /// <summary>
    /// A charge pump inverts its supply, and the useful part is what that then allows: an output
    /// that goes below ground, which on a single supply it cannot.
    /// </summary>
    [Fact]
    public void TheNegativeRailExampleInvertsItsSupplyAndSwingsBothWays()
    {
        using var vm = Load("Negative Rail");

        var pump = vm.Circuit.Components.OfType<ChargePump>().Single();
        var amplifier = vm.Circuit.Components.OfType<OperationalAmplifier>().Single();
        var sim = vm.Simulation.Simulator!;

        sim.Run(20e-3);

        // Not quite minus five: the switches have resistance and the load is pulling through them.
        Assert.InRange(pump.OutputVoltage, -5.0, -4.5);
        Assert.Empty(pump.Violations);

        var low = double.MaxValue;
        var high = double.MinValue;

        while (sim.Time < 24e-3)
        {
            sim.Step();

            var v = sim.NodeVoltage(amplifier.Output);
            low = Math.Min(low, v);
            high = Math.Max(high, v);
        }

        // A 4 Vpp sine centred on ground: the half below zero is the half that needs the rail.
        Assert.Equal(-2.0, low, 0.15);
        Assert.Equal(2.0, high, 0.15);
    }

    /// <summary>
    /// A 3.3 V controller reading and writing a 5 V memory through the shifter, which is what the
    /// part is for: an open-drain bus where every high level is a pull-up and nothing drives up.
    /// </summary>
    [Fact]
    public void TheLevelShifterCarriesAnI2cBusAcrossTheBoundary()
    {
        using var vm = Load("Level Shifting");

        var shifter = vm.Circuit.Components.OfType<LevelShifter>().Single();
        var master = vm.Circuit.Components.OfType<I2cMaster>().Single();
        var sim = vm.Simulation.Simulator!;

        var lowIdle = 0.0;
        var highIdle = 0.0;
        var lowFloor = double.MaxValue;
        var highFloor = double.MaxValue;

        while (sim.Time < 3e-3)
        {
            sim.Step();

            var low = sim.NodeVoltage(shifter.Low(0));
            var high = sim.NodeVoltage(shifter.High(0));

            lowIdle = Math.Max(lowIdle, low);
            highIdle = Math.Max(highIdle, high);
            lowFloor = Math.Min(lowFloor, low);
            highFloor = Math.Min(highFloor, high);
        }

        Assert.True(shifter.IsUsable);

        // Each side idles at its own rail, which is the whole job.
        Assert.Equal(3.3, lowIdle, 0.25);
        Assert.Equal(5.0, highIdle, 0.25);

        // And both sides are actually being pulled down, rather than sitting idle all run.
        Assert.True(lowFloor < 0.6, $"the 3.3 V side never fell below {lowFloor:0.00} V");
        Assert.True(highFloor < 0.6, $"the 5 V side never fell below {highFloor:0.00} V");

        // The transfer completed, which means the acknowledgements came back down through the
        // same channels the data went up through.
        Assert.True(master.LastTransferAcknowledged);
    }

    /// <summary>
    /// And the bytes make the round trip: written up to a 5 V memory and read back down, which
    /// nothing but a bidirectional part could do on one wire.
    /// </summary>
    [Fact]
    public void AndTheBytesComeBackFromTheOtherSide()
    {
        using var vm = Load("Level Shifting");

        var master = vm.Circuit.Components.OfType<I2cMaster>().Single();
        vm.Simulation.Simulator!.Run(14e-3);

        Assert.Equal([0x43, 0x49, 0x52, 0x51], master.ReceivedBytes);
    }

    /// <summary>
    /// Why a crystal is not a coil and a capacitor. Both resonate at a megahertz; only one of them
    /// is still selective once a load is hung on it.
    /// </summary>
    [Fact]
    public void TheCrystalHoldsItsSelectivityUnderLoadAndTheTunedCircuitDoesNot()
    {
        using var vm = Load("Crystal Q");

        var sweep = new AcSweep(vm.Simulation.Simulator!);
        var result = sweep.Run(new AcSweepRequest(0.3e6, 3e6, 8000, SweepSpacing.Linear));

        var crystal = Width(result, "Through crystal");
        var tank = Width(result, "Through tank");

        // Both are tuned to the same megahertz.
        Assert.Equal(1e6, crystal.PeakHz, 20e3);
        Assert.Equal(1e6, tank.PeakHz, 60e3);

        // And the crystal is the one that is still a filter: two orders of magnitude narrower.
        Assert.True(crystal.BandwidthHz < 40e3,
            $"the crystal passed {crystal.BandwidthHz / 1e3:0} kHz, which is not selective");
        Assert.True(tank.BandwidthHz > crystal.BandwidthHz * 20,
            $"the tank passed {tank.BandwidthHz / 1e3:0} kHz against the crystal's {crystal.BandwidthHz / 1e3:0} kHz");
    }

    /// <summary>
    /// And the reason is one number: the motional inductance is thousands of times anything you
    /// can wind, so a load that swamps the coil barely touches the crystal.
    /// </summary>
    [Fact]
    public void AndTheReasonIsAnInductanceNobodyCouldWind()
    {
        using var vm = Load("Crystal Q");

        var crystal = vm.Circuit.Components.OfType<Crystal>().Single();
        var coil = vm.Circuit.Components.OfType<Inductor>().Single(i => i is not Speaker);

        Assert.True(crystal.MotionalInductance > coil.Inductance * 100,
            $"{crystal.MotionalInductance:0.###} H against {coil.Inductance:E2} H");

        // Unloaded, both are respectable; it is the loading that separates them.
        Assert.True(crystal.QualityFactor > 10 * (2 * Math.PI * 1e6 * coil.Inductance / coil.SeriesResistance));
    }

    /// <summary>
    /// The one thing two separate inductors cannot do: pass what is between the wires and stop
    /// what is on both of them at once.
    /// </summary>
    [Fact]
    public void TheChokePassesTheSignalAndStopsTheCommonMode()
    {
        using var vm = Load("Common-Mode Choke");

        var choke = vm.Circuit.Components.OfType<CommonModeChoke>().Single();
        var sim = vm.Simulation.Simulator!;

        sim.Run(20e-6);

        var (differential, common, injected) = (Span.Empty, Span.Empty, Span.Empty);

        while (sim.Time < 40e-6)
        {
            sim.Step();

            var b1 = sim.NodeVoltage(choke.B1);
            var b2 = sim.NodeVoltage(choke.B2);

            differential = differential.With(b1 - b2);
            common = common.With((b1 + b2) / 2.0);
            injected = injected.With((sim.NodeVoltage(choke.A1) + sim.NodeVoltage(choke.A2)) / 2.0);
        }

        // The signal is across the pair and gets through very nearly untouched.
        Assert.Equal(2.0, differential.PeakToPeak, 0.2);

        // The noise is on both wires at once and does not.
        Assert.True(injected.PeakToPeak > 4.0, $"only {injected.PeakToPeak:0.00} V of noise was injected");
        Assert.True(common.PeakToPeak < injected.PeakToPeak / 20.0,
            $"{common.PeakToPeak:0.000} V of common mode got through out of {injected.PeakToPeak:0.00} V");
    }

    /// <summary>
    /// The same controller, the same four-bit dance, two wires instead of six. Both lines of text
    /// arrive, which means every byte of it went round the bus and came out of the expander right.
    /// </summary>
    [Fact]
    public void TheI2cBackpackDrivesTheSameDisplayOverTwoWires()
    {
        using var vm = Load("I2C LCD");

        var lcd = vm.Circuit.Components.OfType<CharacterLcd>().Single();
        var expander = vm.Circuit.Components.OfType<Pcf8574>().Single();

        vm.Simulation.Simulator!.Run(80e-3);

        Assert.Equal(0x27, expander.Address);
        Assert.True(lcd.IsFourBitMode);
        Assert.True(lcd.DisplayOn);

        Assert.Equal("I2C BACKPACK", lcd.Line(0).TrimEnd());
        Assert.Equal("TWO WIRES", lcd.Line(1).TrimEnd());
    }

    /// <summary>
    /// An NTC falls as it warms, so the divider round it rises — and the thing being tested is
    /// that the output changes once on the way through rather than several times.
    /// </summary>
    [Fact]
    public void TheThermostatSwitchesOverAndTheThermistorFallsAsItWarms()
    {
        var cold = Thermostat(10.0);
        var warm = Thermostat(50.0);

        // An NTC: hotter is less resistance, which is the whole of what N stands for.
        Assert.True(warm.Resistance < cold.Resistance / 4,
            $"{cold.Resistance:0} ohms at 10 C against {warm.Resistance:0} at 50 C");

        // Cold: the output is pulled down, so the lamp and the buzzer are on.
        Assert.True(cold.Output < 1.5, $"the output was at {cold.Output:0.00} V when cold");
        Assert.True(cold.Sounding);

        // Warm: it has let go, and they are off.
        Assert.True(warm.Output > 4.0, $"the output only reached {warm.Output:0.00} V when warm");
        Assert.False(warm.Sounding);
    }

    /// <summary>
    /// And the feedback resistor is not a detail: it moves the threshold the moment the output
    /// changes, so the switching temperature going up is not the one coming down.
    /// </summary>
    [Fact]
    public void AndTheHysteresisMovesTheThresholdBehindIt()
    {
        var warming = SwitchPoint(rising: true);
        var cooling = SwitchPoint(rising: false);

        // It turns off hotter than it turns back on. Without the feedback resistor these would be
        // the same temperature, and a sensor wobbling either side of it would chatter.
        Assert.True(warming > cooling + 0.5,
            $"switches off at {warming:0.0} C and back on at {cooling:0.0} C — barely any hysteresis");
    }

    private static (double Resistance, double Output, bool Sounding) Thermostat(double celsius)
    {
        using var vm = Load("Thermostat");

        var thermistor = vm.Circuit.Components.OfType<Thermistor>().Single();
        thermistor.Temperature = celsius;
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var comparator = vm.Circuit.Components.OfType<Comparator>().Single();
        var buzzer = vm.Circuit.Components.OfType<Buzzer>().Single();
        var sim = vm.Simulation.Simulator!;

        sim.Run(30e-3);

        return (thermistor.Resistance, sim.NodeVoltage(comparator.Output), buzzer.IsSounding);
    }

    /// <summary>
    /// The temperature at which it changes over, approached from one side. It has to be one run
    /// with the temperature moved as it goes: the whole point of hysteresis is that where it
    /// switches depends on where it has been.
    /// </summary>
    private static double SwitchPoint(bool rising)
    {
        using var vm = Load("Thermostat");

        var thermistor = vm.Circuit.Components.OfType<Thermistor>().Single();
        var comparator = vm.Circuit.Components.OfType<Comparator>().Single();

        thermistor.Temperature = rising ? 10.0 : 60.0;
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var sim = vm.Simulation.Simulator!;
        sim.Run(20e-3);

        var wasHigh = sim.NodeVoltage(comparator.Output) > 2.5;

        for (var step = 0; step < 100; step++)
        {
            thermistor.Temperature += rising ? 0.5 : -0.5;
            sim.Run(2e-3);

            var high = sim.NodeVoltage(comparator.Output) > 2.5;
            if (high != wasHigh) return thermistor.Temperature;
        }

        throw new Xunit.Sdk.XunitException(
            $"it never changed over {(rising ? "warming up" : "cooling down")}");
    }

    /// <summary>
    /// Four windings energised in turn, which is what a stepper is, and the shaft following them
    /// round one step at a time without losing any.
    /// </summary>
    [Fact]
    public void TheStepperTurnsOneStepPerClockWithoutSlipping()
    {
        using var vm = Load("Stepper Motor");

        var motor = vm.Circuit.Components.OfType<StepperMotor>().Single();
        var clock = vm.Circuit.Components.OfType<ClockSource>().Single();
        var sim = vm.Simulation.Simulator!;

        sim.Run(300e-3);

        Assert.True(motor.IsEnergised);
        Assert.False(motor.IsSlipping);
        Assert.Empty(motor.Violations);

        // One step per clock edge, and the clock has had a known number of them.
        var expected = clock.Frequency * 0.3;
        Assert.True(motor.Steps > expected * 0.7,
            $"{motor.Steps:0} steps in {expected:0} clocks — it is losing them");

        // And it is going forwards, which is what the sequence order decides.
        Assert.True(motor.Angle > 0);
    }

    /// <summary>
    /// And the array's freewheeling diodes are doing their job, so no winding drives its output
    /// anywhere a transistor would not survive.
    /// </summary>
    [Fact]
    public void AndNoWindingKicksPastTheSupplyItIsClampedTo()
    {
        using var vm = Load("Stepper Motor");

        var motor = vm.Circuit.Components.OfType<StepperMotor>().Single();
        var sim = vm.Simulation.Simulator!;

        var peak = 0.0;

        while (sim.Time < 200e-3)
        {
            sim.Step();

            foreach (var coil in motor.Coils) peak = Math.Max(peak, sim.NodeVoltage(coil));
        }

        // Twelve volts plus a diode drop, and nothing like the kilovolts an unclamped coil makes.
        Assert.InRange(peak, 12.0, 14.0);
    }

    /// <summary>
    /// The same load on three cells, and the only thing telling them apart is the resistance
    /// inside them — which is the number that decides what a battery can actually run.
    /// </summary>
    [Fact]
    public void TheBatteryExampleShowsWhatInternalResistanceCosts()
    {
        using var vm = Load("Battery Resistance");

        var cells = vm.Circuit.Components.OfType<Battery>().ToList();
        Assert.Equal(3, cells.Count);

        var sim = vm.Simulation.Simulator!;
        sim.Run(20e-3);

        foreach (var cell in cells)
        {
            var drop = cell.OpenCircuitVoltage - sim.NodeVoltage(cell.Positive);

            // Ohm's law through the cell, with the load taking the current it was set to.
            Assert.Equal(cell.OutputCurrent * cell.Model.InternalResistance, drop, 0.02);
        }

        var coin = cells.Single(c => c.Model == BatteryModel.CoinCell2032);
        var lithium = cells.Single(c => c.Model == BatteryModel.Lithium18650);

        // A coin cell loses a sixth of its voltage at fifty milliamps; an 18650 loses millivolts.
        var coinDrop = coin.OpenCircuitVoltage - sim.NodeVoltage(coin.Positive);
        var lithiumDrop = lithium.OpenCircuitVoltage - sim.NodeVoltage(lithium.Positive);

        Assert.True(coinDrop > 0.4, $"the coin cell only dropped {coinDrop:0.000} V");
        Assert.True(lithiumDrop < 0.02, $"the 18650 dropped {lithiumDrop:0.000} V");
        Assert.True(coinDrop > lithiumDrop * 50);
    }

    /// <summary>A running minimum and maximum, for measuring a swing without keeping the samples.</summary>
    private readonly record struct Span(double Low, double High)
    {
        public static Span Empty => new(double.MaxValue, double.MinValue);

        public Span With(double value) => new(Math.Min(Low, value), Math.Max(High, value));

        public double PeakToPeak => High - Low;
    }

    private static (double PeakHz, double BandwidthHz) Width(AcSweepResult result, string label)
    {
        var trace = result.Traces.Single(t => t.Label == label);

        var peak = 0;
        var peakDb = double.MinValue;

        for (var i = 0; i < trace.Response.Count; i++)
        {
            var db = trace.Decibels(i);
            if (db > peakDb) (peakDb, peak) = (db, i);
        }

        int low = peak, high = peak;

        while (low > 0 && trace.Decibels(low) > peakDb - 3.0) low--;
        while (high < trace.Response.Count - 1 && trace.Decibels(high) > peakDb - 3.0) high++;

        return (result.Frequencies[peak], result.Frequencies[high] - result.Frequencies[low]);
    }
}
