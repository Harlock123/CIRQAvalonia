using Cirq.Components.Boards;
using Cirq.Components.Digital;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class BoardHeaderTests
{
    [Fact]
    public void ThePiHeaderHasTheFortyPinsTheRealOneHas()
    {
        var pi = BoardProfile.RaspberryPi40;

        Assert.Equal(40, pi.PinCount);
        Assert.Equal(20, pi.LeftPins.Count);
        Assert.Equal(20, pi.RightPins.Count);

        // Odd numbers down one side, even down the other, exactly as the header is printed —
        // counting pins on screen has to match counting them on the board.
        Assert.All(pi.LeftPins, p => Assert.True(p.Number % 2 == 1));
        Assert.All(pi.RightPins, p => Assert.True(p.Number % 2 == 0));
        Assert.Equal(Enumerable.Range(1, 40), pi.AllPins.Select(p => p.Number).Order());
    }

    [Fact]
    public void ThePiHeaderHasTheRightMixOfFunctions()
    {
        var pins = BoardProfile.RaspberryPi40.AllPins;

        Assert.Equal(26, pins.Count(p => p.Function is PinFunction.Gpio));
        Assert.Equal(8, pins.Count(p => p.Function is PinFunction.Ground));
        Assert.Equal(2, pins.Count(p => p.Name == "5V"));
        Assert.Equal(2, pins.Count(p => p.Name == "3V3"));
        Assert.Equal(2, pins.Count(p => p.Function is PinFunction.Reserved));   // ID_SD, ID_SC
    }

    [Theory]
    [InlineData("GPIO2", "SDA")]
    [InlineData("GPIO14", "TXD")]
    [InlineData("GPIO10", "MOSI")]
    public void PeripheralPinsCarryTheirAlternateName(string pin, string alternate)
    {
        var definition = BoardProfile.RaspberryPi40.AllPins.Single(p => p.Name == pin);

        Assert.Equal(alternate, definition.Alternate);
    }

    [Fact]
    public void TheUnoHasFourteenDigitalAndSixAnalogPins()
    {
        var uno = BoardProfile.ArduinoUno;

        Assert.Equal(14, uno.AllPins.Count(p => p.Function is PinFunction.Gpio));
        Assert.Equal(6, uno.AllPins.Count(p => p.Function is PinFunction.AnalogIn));

        // PWM is on 3, 5, 6, 9, 10 and 11 — the pins marked with a tilde on the board.
        var pwm = uno.AllPins.Where(p => p.SupportsPwm).Select(p => p.Name).ToHashSet();
        Assert.Equal(new[] { "D10", "D11", "D3", "D5", "D6", "D9" }.ToHashSet(), pwm);
    }

    [Fact]
    public void TheMegaHasItsFullPinComplementSplitAcrossBothColumns()
    {
        var mega = BoardProfile.ArduinoMega;

        Assert.Equal(54, mega.AllPins.Count(p => p.Function is PinFunction.Gpio));
        Assert.Equal(16, mega.AllPins.Count(p => p.Function is PinFunction.AnalogIn));

        // Balanced, so the symbol is not a 54-row strip taller than the screen.
        var difference = Math.Abs(mega.LeftPins.Count - mega.RightPins.Count);
        Assert.True(difference <= 1, $"columns differ by {difference}");
    }

    [Fact]
    public void EveryBoardNumbersItsPinsUniquely()
    {
        foreach (var profile in BoardProfile.Library)
        {
            var numbers = profile.AllPins.Select(p => p.Number).ToList();
            Assert.Equal(numbers.Count, numbers.Distinct().Count());
        }
    }

    [Fact]
    public void EveryBoardExposesOneTerminalPerHeaderPin()
    {
        foreach (var board in AllBoards())
        {
            Assert.Equal(board.Profile.PinCount, board.Terminals.Count);
            Assert.Equal(board.Profile.PinCount, board.Header.Count);
        }
    }

    [Fact]
    public void PinsCanBeLookedUpByTheNamePrintedOnTheBoard()
    {
        var uno = new ArduinoUnoBoard();

        Assert.Equal("D13/LED", uno.Pin("D13").Name);
        Assert.Throws<ArgumentException>(() => uno.Pin("GPIO17"));
    }

    internal static IEnumerable<DeveloperBoard> AllBoards()
    {
        yield return new RaspberryPiBoard();
        yield return new ArduinoUnoBoard();
        yield return new ArduinoNanoBoard();
        yield return new ArduinoMegaBoard();
    }
}

public class PinConfigurationTests
{
    private static readonly BoardProfile Pi = BoardProfile.RaspberryPi40;

    [Fact]
    public void AnEmptyConfigurationLeavesEveryPinReading()
    {
        var result = PinConfiguration.Parse("", Pi);

        Assert.Empty(result.Settings);
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("GPIO17=high", PinMode.OutputHigh)]
    [InlineData("GPIO17=low", PinMode.OutputLow)]
    [InlineData("GPIO17=1", PinMode.OutputHigh)]
    [InlineData("GPIO17=0", PinMode.OutputLow)]
    [InlineData("GPIO17=in", PinMode.Input)]
    [InlineData("GPIO17=in-pullup", PinMode.InputPullUp)]
    [InlineData("GPIO17=pulldown", PinMode.InputPullDown)]
    public void ModesAreRecognisedByTheirCommonSpellings(string text, PinMode expected)
    {
        var result = PinConfiguration.Parse(text, Pi);

        Assert.True(result.IsValid, string.Join("; ", result.Problems));
        Assert.Equal(expected, result.Settings.Single().Mode);
    }

    [Fact]
    public void AClockCarriesItsFrequencyInEngineeringNotation()
    {
        var setting = PinConfiguration.Parse("GPIO18=clock@1kHz", Pi).Settings.Single();

        Assert.Equal(PinMode.Clock, setting.Mode);
        Assert.Equal(1000.0, setting.Frequency, 6);
        Assert.Equal(0.5, setting.DutyCycle, 6);
    }

    [Fact]
    public void PwmCarriesItsDutyCycle()
    {
        var setting = PinConfiguration.Parse("GPIO12=pwm@500Hz:25%", Pi).Settings.Single();

        Assert.Equal(PinMode.Pwm, setting.Mode);
        Assert.Equal(500.0, setting.Frequency, 6);
        Assert.Equal(0.25, setting.DutyCycle, 6);
    }

    [Fact]
    public void SeveralPinsAreConfiguredInOneString()
    {
        var result = PinConfiguration.Parse("GPIO17=high; GPIO18=clock@2kHz; GPIO27=in-pullup", Pi);

        Assert.True(result.IsValid, string.Join("; ", result.Problems));
        Assert.Equal(3, result.Settings.Count);
        Assert.Equal(2, result.Settings.Count(s => s.IsDriving));
    }

    [Theory]
    [InlineData("GPIO99=high", "not a pin")]
    [InlineData("GPIO17=sideways", "not a mode")]
    [InlineData("GPIO17", "not pin=mode")]
    [InlineData("GPIO18=clock", "needs a frequency")]
    [InlineData("GPIO18=clock@banana", "not a frequency")]
    [InlineData("GPIO12=pwm@1kHz:150%", "not a duty cycle")]
    [InlineData("GND=high", "cannot be set")]
    [InlineData("ID_SD=high", "cannot be set")]
    public void MalformedEntriesAreReportedRatherThanThrown(string text, string expected)
    {
        var result = PinConfiguration.Parse(text, Pi);

        Assert.False(result.IsValid);
        Assert.Contains(expected, string.Join("; ", result.Problems));
    }

    [Fact]
    public void OneBadEntryDoesNotDiscardTheGoodOnes()
    {
        // A typo in the middle of a line should cost that pin, not the whole setup.
        var result = PinConfiguration.Parse("GPIO17=high; GPIO99=low; GPIO27=in", Pi);

        Assert.Equal(2, result.Settings.Count);
        Assert.Single(result.Problems);
    }

    [Fact]
    public void ConfiguringTheSamePinTwiceIsReported()
    {
        var result = PinConfiguration.Parse("GPIO17=high; GPIO17=low", Pi);

        Assert.Single(result.Settings);
        Assert.Contains("more than once", string.Join("; ", result.Problems));
    }

    [Fact]
    public void AnAnalogPinCanBeReadButNotDriven()
    {
        var uno = BoardProfile.ArduinoUno;

        Assert.True(PinConfiguration.Parse("A0=in", uno).IsValid);
        Assert.Contains("can only be read", string.Join("; ", PinConfiguration.Parse("A0=high", uno).Problems));
    }

    [Fact]
    public void SettingsRoundTripThroughTheirTextForm()
    {
        const string original = "GPIO17=high; GPIO18=clock@1kHz; GPIO27=in-pullup";

        var once = PinConfiguration.Parse(original, Pi).Settings;
        var twice = PinConfiguration.Parse(PinConfiguration.Format(once), Pi).Settings;

        Assert.Equal(once, twice);
    }
}

public class BoardCircuitTests
{
    /// <summary>A board with its first ground pin tied to circuit ground.</summary>
    private static (Circuit Circuit, T Board) Grounded<T>() where T : DeveloperBoard, new()
    {
        var circuit = new Circuit();
        var board = circuit.Add(new T());
        var ground = circuit.Add(new Ground());
        circuit.Connect(board.GroundPins[0], ground.Terminals[0]);
        return (circuit, board);
    }

    private static CircuitSimulator Settle(Circuit circuit, double seconds = 1e-6)
    {
        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(seconds);
        return sim;
    }

    [Fact]
    public void APiOutputDrivesItsThreePointThreeVoltRail()
    {
        var (circuit, pi) = Grounded<RaspberryPiBoard>();
        pi.Pins = "GPIO17=high";

        // A load, so the pin is driving something rather than sitting open.
        var load = circuit.Add(new Resistor(10e3));
        circuit.Connect(pi.Pin("GPIO17"), load.Terminals[0]);
        circuit.Connect(load.Terminals[1], pi.GroundPins[1]);

        var sim = Settle(circuit);

        Assert.Equal(3.3, sim.NodeVoltage(pi.Pin("GPIO17")), 1);
    }

    [Fact]
    public void AnArduinoOutputDrivesFiveVolts()
    {
        var (circuit, uno) = Grounded<ArduinoUnoBoard>();
        uno.Pins = "D13=high";

        var load = circuit.Add(new Resistor(10e3));
        circuit.Connect(uno.Pin("D13"), load.Terminals[0]);
        circuit.Connect(load.Terminals[1], uno.GroundPins[1]);

        var sim = Settle(circuit);

        Assert.Equal(5.0, sim.NodeVoltage(uno.Pin("D13")), 1);
    }

    [Fact]
    public void AnUnconfiguredPinIsReleasedRatherThanDriven()
    {
        // A board powers up with its pins as inputs; a pulled-up node must stay high.
        var (circuit, pi) = Grounded<RaspberryPiBoard>();

        var pullUp = circuit.Add(new Resistor(10e3));
        var rail = circuit.Add(new DcVoltageSource(3.3));
        circuit.Connect(rail.Terminals[0], pullUp.Terminals[0]);
        circuit.Connect(pullUp.Terminals[1], pi.Pin("GPIO22"));
        circuit.Connect(rail.Terminals[1], pi.GroundPins[1]);

        var sim = Settle(circuit);

        Assert.True(sim.NodeVoltage(pi.Pin("GPIO22")) > 3.0,
            $"released pin was pulled to {sim.NodeVoltage(pi.Pin("GPIO22")):0.00} V");
    }

    [Fact]
    public void AnInternalPullUpHoldsAFloatingPinHigh()
    {
        var (circuit, pi) = Grounded<RaspberryPiBoard>();
        pi.Pins = "GPIO22=in-pullup";

        var sim = Settle(circuit);

        Assert.True(sim.NodeVoltage(pi.Pin("GPIO22")) > 3.0,
            $"pull-up settled at {sim.NodeVoltage(pi.Pin("GPIO22")):0.00} V");
    }

    [Fact]
    public void AnInternalPullDownHoldsAFloatingPinLow()
    {
        var (circuit, pi) = Grounded<RaspberryPiBoard>();
        pi.Pins = "GPIO22=in-pulldown";

        var sim = Settle(circuit);

        Assert.True(sim.NodeVoltage(pi.Pin("GPIO22")) < 0.3,
            $"pull-down settled at {sim.NodeVoltage(pi.Pin("GPIO22")):0.00} V");
    }

    [Fact]
    public void ThePowerPinsSupplyTheCircuitAroundTheBoard()
    {
        var (circuit, pi) = Grounded<RaspberryPiBoard>();

        var load = circuit.Add(new Resistor(1e3));
        circuit.Connect(pi.Pin("5V"), load.Terminals[0]);
        circuit.Connect(load.Terminals[1], pi.GroundPins[1]);

        var sim = Settle(circuit);

        Assert.Equal(5.0, sim.NodeVoltage(pi.Pin("5V")), 2);
        Assert.Equal(3.3, sim.NodeVoltage(pi.Pin("3V3")), 2);
    }

    [Fact]
    public void TurningOffTheSupplyReleasesTheRails()
    {
        var (circuit, pi) = Grounded<RaspberryPiBoard>();
        pi.SuppliesPower = false;

        var bleed = circuit.Add(new Resistor(1e3));
        circuit.Connect(pi.Pin("5V"), bleed.Terminals[0]);
        circuit.Connect(bleed.Terminals[1], pi.GroundPins[1]);

        var sim = Settle(circuit);

        Assert.Equal(0.0, sim.NodeVoltage(pi.Pin("5V")), 2);
    }

    [Fact]
    public void EveryGroundPinIsTheSameCopper()
    {
        var (circuit, pi) = Grounded<RaspberryPiBoard>();
        var sim = Settle(circuit);

        foreach (var pin in pi.GroundPins)
            Assert.Equal(0.0, sim.NodeVoltage(pin), 6);
    }

    [Theory]
    [InlineData(5.0, "D2=1")]
    [InlineData(0.0, "D2=0")]
    public void ABoardReadsTheLevelDrivenOntoItsInputPin(double volts, string expected)
    {
        var (circuit, uno) = Grounded<ArduinoUnoBoard>();
        uno.Pins = "D2=in";

        var source = circuit.Add(new DcVoltageSource(volts));
        circuit.Connect(source.Terminals[0], uno.Pin("D2"));
        circuit.Connect(source.Terminals[1], uno.GroundPins[1]);

        Settle(circuit, 1e-6);

        Assert.Contains(expected, uno.InputSummary);
    }

    /// <summary>
    /// A 74xx TTL output tops out at 3.4 V, and a 5 V CMOS input wants 3.5 V to call it high — so
    /// an Arduino reads a bare TTL gate as undefined rather than as a one. That is the real
    /// interfacing problem, not a modelling artefact, and it is worth pinning down so nobody
    /// later "fixes" the thresholds to make a circuit appear to work.
    /// </summary>
    [Fact]
    public void ATtlOutputDoesNotMeetTheArduinosHighThreshold()
    {
        var (circuit, uno) = Grounded<ArduinoUnoBoard>();
        uno.Pins = "D2=in";

        var toggle = circuit.Add(new LogicToggle(true));
        circuit.Connect(toggle.Out, uno.Pin("D2"));

        Settle(circuit, 1e-6);

        Assert.Contains("D2=X", uno.InputSummary);

        // The same part on CMOS levels clears it comfortably.
        toggle.Levels = Cirq.Core.Digital.LogicLevels.Cmos5V;
        Settle(circuit, 1e-6);

        Assert.Contains("D2=1", uno.InputSummary);
    }

    [Fact]
    public void AnAnalogPinReportsAVoltageRatherThanALevel()
    {
        var (circuit, uno) = Grounded<ArduinoUnoBoard>();
        uno.Pins = "A0=in";

        // A 2:1 divider off the board's own 5V rail puts 2.5 V on the pin.
        var top = circuit.Add(new Resistor(10e3));
        var bottom = circuit.Add(new Resistor(10e3));
        circuit.Connect(uno.Pin("5V"), top.Terminals[0]);
        circuit.Connect(top.Terminals[1], uno.Pin("A0"));
        circuit.Connect(uno.Pin("A0"), bottom.Terminals[0]);
        circuit.Connect(bottom.Terminals[1], uno.GroundPins[1]);

        Settle(circuit, 1e-6);

        Assert.Contains("A0=2.5", uno.InputSummary);
    }

    /// <summary>
    /// A clock pin has to produce the frequency it was asked for. Counting edges over a known
    /// window and comparing against f is the same check the 555 astable test makes.
    /// </summary>
    [Theory]
    [InlineData(1e3)]
    [InlineData(5e3)]
    [InlineData(20e3)]
    public void AClockPinOscillatesAtTheRequestedFrequency(double frequency)
    {
        var (circuit, pi) = Grounded<RaspberryPiBoard>();
        pi.Pins = $"GPIO18=clock@{frequency}Hz";

        var load = circuit.Add(new Resistor(10e3));
        circuit.Connect(pi.Pin("GPIO18"), load.Terminals[0]);
        circuit.Connect(load.Terminals[1], pi.GroundPins[1]);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        var window = 20.0 / frequency;          // twenty cycles
        var rising = 0;
        var wasHigh = sim.NodeVoltage(pi.Pin("GPIO18")) > 1.65;

        sim.TimePointAccepted += _ =>
        {
            var isHigh = sim.NodeVoltage(pi.Pin("GPIO18")) > 1.65;
            if (isHigh && !wasHigh) rising++;
            wasHigh = isHigh;
        };

        sim.Run(window);

        Assert.InRange(rising, 19, 21);
    }

    [Fact]
    public void APwmPinHoldsItsDutyCycle()
    {
        var (circuit, pi) = Grounded<RaspberryPiBoard>();
        pi.Pins = "GPIO12=pwm@1kHz:25%";

        var load = circuit.Add(new Resistor(10e3));
        circuit.Connect(pi.Pin("GPIO12"), load.Terminals[0]);
        circuit.Connect(load.Terminals[1], pi.GroundPins[1]);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        var high = 0.0;
        var previous = 0.0;

        sim.TimePointAccepted += s =>
        {
            var dt = s.Time - previous;
            previous = s.Time;
            if (sim.NodeVoltage(pi.Pin("GPIO12")) > 1.65) high += dt;
        };

        sim.Run(20e-3);

        Assert.Equal(0.25, high / 20e-3, 1);
    }
}

public class BoardRuleCheckTests
{
    private static (Circuit Circuit, T Board) Grounded<T>() where T : DeveloperBoard, new()
    {
        var circuit = new Circuit();
        var board = circuit.Add(new T());
        circuit.Connect(board.GroundPins[0], circuit.Add(new Ground()).Terminals[0]);
        return (circuit, board);
    }

    /// <summary>
    /// The headline check. A Pi GPIO is 3.3V and not 5V tolerant, and tying one to 5V logic is the
    /// most common way people destroy a Pi — a simulator that reported the right waveform while
    /// the real board died would not be much use.
    /// </summary>
    [Fact]
    public void DrivingAPiPinFromFiveVoltsIsFlagged()
    {
        var (circuit, pi) = Grounded<RaspberryPiBoard>();
        pi.Pins = "GPIO22=in";

        var five = circuit.Add(new DcVoltageSource(5.0));
        circuit.Connect(five.Terminals[0], pi.Pin("GPIO22"));
        circuit.Connect(five.Terminals[1], pi.GroundPins[1]);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        pi.CheckLimits(sim.System);

        Assert.Contains("not 5V tolerant", string.Join(" | ", pi.Violations));
        Assert.Contains("GPIO22", string.Join(" | ", pi.Violations));
    }

    [Fact]
    public void TheSameFiveVoltsOnAnArduinoIsFine()
    {
        // The Uno *is* a 5V board, so the identical circuit must not complain.
        var (circuit, uno) = Grounded<ArduinoUnoBoard>();
        uno.Pins = "D2=in";

        var five = circuit.Add(new DcVoltageSource(5.0));
        circuit.Connect(five.Terminals[0], uno.Pin("D2"));
        circuit.Connect(five.Terminals[1], uno.GroundPins[1]);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        uno.CheckLimits(sim.System);

        Assert.Empty(uno.Violations);
    }

    [Fact]
    public void AnOverloadedPinIsFlaggedWithItsCurrent()
    {
        var (circuit, uno) = Grounded<ArduinoUnoBoard>();
        uno.Pins = "D13=high";

        // 100 ohms straight to ground pulls 50 mA from a 20 mA pin — the classic
        // LED-without-a-proper-resistor mistake.
        var load = circuit.Add(new Resistor(100));
        circuit.Connect(uno.Pin("D13"), load.Terminals[0]);
        circuit.Connect(load.Terminals[1], uno.GroundPins[1]);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        uno.CheckLimits(sim.System);

        Assert.Contains("over its 20 mA rating", string.Join(" | ", uno.Violations));
    }

    [Fact]
    public void AProperlyResistoredLedRaisesNothing()
    {
        var (circuit, uno) = Grounded<ArduinoUnoBoard>();
        uno.Pins = "D13=high";

        var resistor = circuit.Add(new Resistor(330));
        var led = circuit.Add(Led.OfColour("Red"));
        circuit.Connect(uno.Pin("D13"), resistor.Terminals[0]);
        circuit.Connect(resistor.Terminals[1], led.Terminals[0]);
        circuit.Connect(led.Terminals[1], uno.GroundPins[1]);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        uno.CheckLimits(sim.System);

        Assert.Empty(uno.Violations);
        Assert.InRange(uno.GpioCurrent, 0.005, 0.020);
    }

    [Fact]
    public void TheTotalGpioBudgetIsCheckedAsWellAsEachPin()
    {
        var (circuit, pi) = Grounded<RaspberryPiBoard>();

        // Six pins at 12 mA each: every pin is inside its own 16 mA rating, but together they
        // are over the Pi's 50 mA total budget.
        var names = new[] { "GPIO17", "GPIO27", "GPIO22", "GPIO5", "GPIO6", "GPIO26" };
        pi.Pins = string.Join("; ", names.Select(n => $"{n}=high"));

        foreach (var name in names)
        {
            var load = circuit.Add(new Resistor(270));
            circuit.Connect(pi.Pin(name), load.Terminals[0]);
            circuit.Connect(load.Terminals[1], pi.GroundPins[1]);
        }

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        pi.CheckLimits(sim.System);

        var reported = string.Join(" | ", pi.Violations);
        Assert.Contains("total GPIO current", reported);
        Assert.DoesNotContain("over its 16 mA rating", reported);
    }

    [Fact]
    public void AnIdleBoardReportsNothing()
    {
        foreach (var board in BoardHeaderTests.AllBoards())
        {
            var circuit = new Circuit();
            circuit.Add(board);
            circuit.Connect(board.GroundPins[0], circuit.Add(new Ground()).Terminals[0]);

            var sim = new CircuitSimulator(circuit);
            sim.Reset();
            sim.SolveOperatingPoint();
            board.CheckLimits(sim.System);

            Assert.Empty(board.Violations);
        }
    }
}

public class BoardPersistenceTests
{
    [Fact]
    public void AConfiguredBoardKeepsItsPinSetupAcrossSaveAndReload()
    {
        var circuit = new Circuit();
        var pi = circuit.Add(new RaspberryPiBoard());
        pi.Pins = "GPIO17=high; GPIO18=clock@1kHz; GPIO27=in-pullup";
        pi.PullResistance = 47e3;
        pi.SuppliesPower = false;

        var path = Path.Combine(Path.GetTempPath(), $"cirq-board-{Guid.NewGuid():N}.cirq");
        try
        {
            Serialization.CircuitSerializer.Save(circuit, path);
            var reloaded = Serialization.CircuitSerializer.Load(path).Circuit;
            var board = reloaded.Components.OfType<RaspberryPiBoard>().Single();

            Assert.Equal(pi.Pins, board.Pins);
            Assert.Equal(47e3, board.PullResistance);
            Assert.False(board.SuppliesPower);

            // The setup has to still *mean* the same thing, not just be the same text.
            var settings = PinConfiguration.Parse(board.Pins, board.Profile).Settings;
            Assert.Equal(3, settings.Count);
            Assert.Equal(1000.0, settings.Single(s => s.Mode is PinMode.Clock).Frequency, 6);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AReloadedBoardStillHasEveryHeaderPinWired()
    {
        var circuit = new Circuit();
        var uno = circuit.Add(new ArduinoUnoBoard());
        uno.Pins = "D13=high";

        var resistor = circuit.Add(new Passive.Resistor(330));
        circuit.Connect(uno.Pin("D13"), resistor.Terminals[0]);
        circuit.Connect(resistor.Terminals[1], uno.GroundPins[0]);
        circuit.Add(new Ground());

        var path = Path.Combine(Path.GetTempPath(), $"cirq-board-{Guid.NewGuid():N}.cirq");
        try
        {
            Serialization.CircuitSerializer.Save(circuit, path);
            var reloaded = Serialization.CircuitSerializer.Load(path);
            var board = reloaded.Circuit.Components.OfType<ArduinoUnoBoard>().Single();

            Assert.Empty(reloaded.Warnings);
            Assert.Equal(uno.Terminals.Count, board.Terminals.Count);

            // The wire must come back on D13 specifically, not just on some pin.
            Assert.Contains(reloaded.Circuit.Wires, w =>
                w.SourceTerminal == board.Pin("D13") || w.TargetTerminal == board.Pin("D13"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

public class PinSequenceParsingTests
{
    private static readonly BoardProfile Pi = BoardProfile.RaspberryPi40;

    private static PinSetting Single(string text)
    {
        var result = PinConfiguration.Parse(text, Pi);
        Assert.True(result.IsValid, string.Join("; ", result.Problems));
        return result.Settings.Single();
    }

    [Fact]
    public void ALoopingSequenceCarriesItsRateAndPattern()
    {
        var setting = Single("GPIO17=seq@1kHz:10110");

        Assert.Equal(PinMode.Sequence, setting.Mode);
        Assert.Equal(1000.0, setting.Frequency, 6);
        Assert.Equal("10110", setting.Pattern);
        Assert.True(setting.IsSequence);
        Assert.True(setting.IsTimed);
        Assert.True(setting.IsDriving);
    }

    [Fact]
    public void AOneShotSequenceIsADistinctMode()
    {
        var setting = Single("GPIO17=once@500Hz:1110");

        Assert.Equal(PinMode.SequenceOnce, setting.Mode);
        Assert.Equal("1110", setting.Pattern);
    }

    [Theory]
    [InlineData("GPIO17=sequence@1kHz:101", PinMode.Sequence)]
    [InlineData("GPIO17=seq-once@1kHz:101", PinMode.SequenceOnce)]
    public void BothSpellingsOfEachSequenceModeWork(string text, PinMode expected) =>
        Assert.Equal(expected, Single(text).Mode);

    [Fact]
    public void UnderscoresAndSpacesGroupThePatternForReading()
    {
        // 1100_1010 is far easier to check by eye than 11001010.
        Assert.Equal("11001010", Single("GPIO17=seq@1kHz:1100_1010").Pattern);
        Assert.Equal("1010", Single("GPIO17=seq@1kHz:10 10").Pattern);
    }

    [Fact]
    public void APatternMayReleaseThePinOnAStep()
    {
        Assert.Equal("1z0z", Single("GPIO17=seq@1kHz:1Z0z").Pattern);
    }

    [Fact]
    public void ThePatternDurationIsItsLengthOverTheStepRate()
    {
        var setting = Single("GPIO17=seq@1kHz:10110");

        Assert.Equal(5e-3, setting.PatternDuration, 9);
    }

    [Theory]
    [InlineData("GPIO17=seq", "needs a step rate and a pattern")]
    [InlineData("GPIO17=seq@1kHz", "needs a pattern after the rate")]
    [InlineData("GPIO17=seq@1kHz:", "pattern is empty")]
    [InlineData("GPIO17=seq@1kHz:10201", "'2' is not a pattern step")]
    [InlineData("GPIO17=once@1kHz:abc", "'a' is not a pattern step")]
    [InlineData("GPIO17=seq@banana:101", "not a frequency")]
    public void MalformedSequencesAreReported(string text, string expected)
    {
        var result = PinConfiguration.Parse(text, Pi);

        Assert.False(result.IsValid);
        Assert.Contains(expected, string.Join("; ", result.Problems));
    }

    [Fact]
    public void AnAbsurdlyLongPatternIsRefusedRatherThanAccepted()
    {
        var text = $"GPIO17=seq@1kHz:{new string('1', PinConfiguration.MaximumPatternLength + 1)}";

        Assert.Contains("over the 256 limit", string.Join("; ", PinConfiguration.Parse(text, Pi).Problems));
    }

    [Fact]
    public void APatternAtExactlyTheLimitIsAccepted()
    {
        var text = $"GPIO17=seq@1kHz:{new string('1', PinConfiguration.MaximumPatternLength)}";

        Assert.True(PinConfiguration.Parse(text, Pi).IsValid);
    }

    [Fact]
    public void SequencesRoundTripThroughTheirTextForm()
    {
        const string original = "GPIO17=seq@1kHz:1011z; GPIO18=once@2kHz:1100; GPIO27=in-pullup";

        var once = PinConfiguration.Parse(original, Pi).Settings;
        var twice = PinConfiguration.Parse(PinConfiguration.Format(once), Pi).Settings;

        Assert.Equal(once, twice);
    }
}

public class PinSequenceBehaviourTests
{
    private static (Circuit Circuit, RaspberryPiBoard Board, Terminal Pin) Rig(string pins, string pinName = "GPIO17")
    {
        var circuit = new Circuit();
        var pi = circuit.Add(new RaspberryPiBoard());
        circuit.Connect(pi.GroundPins[0], circuit.Add(new Ground()).Terminals[0]);
        pi.Pins = pins;

        var load = circuit.Add(new Resistor(10e3));
        circuit.Connect(pi.Pin(pinName), load.Terminals[0]);
        circuit.Connect(load.Terminals[1], pi.GroundPins[1]);

        return (circuit, pi, pi.Pin(pinName));
    }

    /// <summary>Samples the pin in the middle of each step, where the level is unambiguous.</summary>
    private static string Play(string pins, double rate, int steps, string pinName = "GPIO17")
    {
        var (circuit, _, pin) = Rig(pins, pinName);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        var read = new char[steps];
        for (var i = 0; i < steps; i++)
        {
            sim.Run((i + 0.5) / rate - sim.Time);
            var v = sim.NodeVoltage(pin);
            read[i] = v > 1.65 ? '1' : v > 0.2 ? 'z' : '0';
        }

        return new string(read);
    }

    [Fact]
    public void ASequencePlaysItsPatternInOrder()
    {
        Assert.Equal("10110", Play("GPIO17=seq@1kHz:10110", 1e3, 5));
    }

    [Fact]
    public void ASequenceRepeatsOnceItReachesTheEnd()
    {
        // Two full passes of a four-step pattern.
        Assert.Equal("11001100", Play("GPIO17=seq@1kHz:1100", 1e3, 8));
    }

    [Fact]
    public void AOneShotHoldsItsLastStepInsteadOfRepeating()
    {
        // The same pattern either way, so the two modes can only differ in what happens after
        // the fourth step: looping starts again, one-shot holds the final zero.
        Assert.Equal("11101110", Play("GPIO17=seq@1kHz:1110", 1e3, 8));
        Assert.Equal("11100000", Play("GPIO17=once@1kHz:1110", 1e3, 8));
    }

    [Fact]
    public void AOneShotIsUsableAsAStartupResetPulse()
    {
        // Low for two steps, then released high for the rest of the run: exactly a reset.
        Assert.Equal("00111", Play("GPIO17=once@2kHz:001", 2e3, 5));
    }

    /// <summary>
    /// A 'z' step releases the pin. Without leakage stamped on the released steps the node has
    /// nothing holding it, so this also guards the matrix against going singular mid-pattern.
    /// </summary>
    [Fact]
    public void AReleasedStepLetsAnExternalPullDecideTheLevel()
    {
        var circuit = new Circuit();
        var pi = circuit.Add(new RaspberryPiBoard());
        circuit.Connect(pi.GroundPins[0], circuit.Add(new Ground()).Terminals[0]);
        pi.Pins = "GPIO17=seq@1kHz:1z0z";

        // An external pull-up: the released steps should follow it to 3.3 V.
        var pull = circuit.Add(new Resistor(4.7e3));
        var rail = circuit.Add(new DcVoltageSource(3.3));
        circuit.Connect(rail.Terminals[0], pull.Terminals[0]);
        circuit.Connect(pull.Terminals[1], pi.Pin("GPIO17"));
        circuit.Connect(rail.Terminals[1], pi.GroundPins[1]);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        var read = new char[4];
        for (var i = 0; i < 4; i++)
        {
            sim.Run((i + 0.5) / 1e3 - sim.Time);
            read[i] = sim.NodeVoltage(pi.Pin("GPIO17")) > 1.65 ? '1' : '0';
        }

        // Steps 0 and 1 are high (driven, then pulled up), step 2 is driven low, step 3 pulled up.
        Assert.Equal("1101", new string(read));
    }

    [Fact]
    public void TheInternalPullUpAlsoWinsOnAReleasedStep()
    {
        var (circuit, pi, pin) = Rig("GPIO17=seq@1kHz:0z");

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        sim.Run(1.5e-3);

        // The 10k load to ground beats the pin's own leakage, so a released step sits low.
        Assert.True(sim.NodeVoltage(pin) < 0.5, $"released step sat at {sim.NodeVoltage(pin):0.00} V");
    }

    [Fact]
    public void ASequenceRunsAtTheRateItWasGiven()
    {
        // An alternating pattern at 1 kHz step rate is a 500 Hz square wave: twenty rising
        // edges in forty milliseconds.
        var (circuit, _, pin) = Rig("GPIO17=seq@1kHz:10");

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        var rising = 0;
        var wasHigh = sim.NodeVoltage(pin) > 1.65;
        sim.TimePointAccepted += _ =>
        {
            var isHigh = sim.NodeVoltage(pin) > 1.65;
            if (isHigh && !wasHigh) rising++;
            wasHigh = isHigh;
        };

        sim.Run(40e-3);

        Assert.InRange(rising, 19, 21);
    }

    [Fact]
    public void SeveralPinsRunTheirOwnSequencesIndependently()
    {
        var circuit = new Circuit();
        var pi = circuit.Add(new RaspberryPiBoard());
        circuit.Connect(pi.GroundPins[0], circuit.Add(new Ground()).Terminals[0]);
        pi.Pins = "GPIO17=seq@1kHz:1100; GPIO27=seq@1kHz:1010";

        foreach (var name in new[] { "GPIO17", "GPIO27" })
        {
            var load = circuit.Add(new Resistor(10e3));
            circuit.Connect(pi.Pin(name), load.Terminals[0]);
            circuit.Connect(load.Terminals[1], pi.GroundPins[1]);
        }

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        var a = new char[4];
        var b = new char[4];
        for (var i = 0; i < 4; i++)
        {
            sim.Run((i + 0.5) / 1e3 - sim.Time);
            a[i] = sim.NodeVoltage(pi.Pin("GPIO17")) > 1.65 ? '1' : '0';
            b[i] = sim.NodeVoltage(pi.Pin("GPIO27")) > 1.65 ? '1' : '0';
        }

        Assert.Equal("1100", new string(a));
        Assert.Equal("1010", new string(b));
    }

    [Fact]
    public void ASequencedBoardSurvivesSaveAndReloadStillPlaying()
    {
        var circuit = new Circuit();
        var pi = circuit.Add(new RaspberryPiBoard());
        pi.Pins = "GPIO17=seq@1kHz:1011z; GPIO18=once@2kHz:110";

        var path = Path.Combine(Path.GetTempPath(), $"cirq-seq-{Guid.NewGuid():N}.cirq");
        try
        {
            Serialization.CircuitSerializer.Save(circuit, path);
            var board = Serialization.CircuitSerializer.Load(path)
                .Circuit.Components.OfType<RaspberryPiBoard>().Single();

            var settings = PinConfiguration.Parse(board.Pins, board.Profile).Settings;

            Assert.Equal("1011z", settings.Single(s => s.PinName == "GPIO17").Pattern);
            Assert.Equal(PinMode.SequenceOnce, settings.Single(s => s.PinName == "GPIO18").Mode);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
