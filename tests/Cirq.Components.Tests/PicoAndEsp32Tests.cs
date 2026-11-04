using Cirq.Components.Boards;
using Cirq.Components.Passive;
using Cirq.Components.Serialization;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The Raspberry Pi Pico and the ESP32 DevKit: their headers against the boards' own pinout cards,
/// and their pins behaving as 3.3 V pins that are not 5 V tolerant.
/// </summary>
public class PicoAndEsp32Tests
{
    private static (Circuit Circuit, T Board) Grounded<T>() where T : DeveloperBoard, new()
    {
        var circuit = new Circuit();
        var board = circuit.Add(new T());
        circuit.Connect(board.GroundPins[0], circuit.Add(new Ground()).Pin);
        return (circuit, board);
    }

    private static CircuitSimulator Settle(Circuit circuit)
    {
        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1e-6);
        return sim;
    }

    [Fact]
    public void ThePicoHasFortyPinsNumberedRoundTheBoard()
    {
        var pico = BoardProfile.RaspberryPiPico;

        Assert.Equal(40, pico.PinCount);
        Assert.Equal(Enumerable.Range(1, 20), pico.LeftPins.Select(p => p.Number));

        // The right-hand side counts back up from 21 at the bottom to VBUS at the top.
        Assert.Equal(Enumerable.Range(21, 20).Reverse(), pico.RightPins.Select(p => p.Number));
        Assert.Equal("VBUS", pico.RightPins[0].Name);
        Assert.Equal("GP16", pico.RightPins[^1].Name);
    }

    [Fact]
    public void ThePicoHasTwentySixGpioThreeOfThemAdcs()
    {
        var pins = BoardProfile.RaspberryPiPico.AllPins;

        Assert.Equal(26, pins.Count(p => p.Function is PinFunction.Gpio));
        Assert.Equal(8, pins.Count(p => p.Function is PinFunction.Ground));
        Assert.Equal(["GP26", "GP27", "GP28"], pins.Where(p => p.HasAdc).Select(p => p.Name).Order());
        Assert.All(pins.Where(p => p.Function is PinFunction.Gpio), p => Assert.True(p.SupportsPwm));

        // A few pins checked against the card: GP0 is pin 1, GP15 pin 20, GP26 pin 31, 3V3 pin 36.
        Assert.Equal(1, pins.Single(p => p.Name == "GP0").Number);
        Assert.Equal(20, pins.Single(p => p.Name == "GP15").Number);
        Assert.Equal(31, pins.Single(p => p.Name == "GP26").Number);
        Assert.Equal(36, pins.Single(p => p.Name == "3V3").Number);
        Assert.Equal(33, pins.Single(p => p.Name == "AGND").Number);
    }

    [Fact]
    public void TheEsp32HasThirtyEightPinsAndItsThreeKindsOfAwkwardOne()
    {
        var esp = BoardProfile.Esp32DevKit;

        Assert.Equal(38, esp.PinCount);
        Assert.Equal(19, esp.LeftPins.Count);
        Assert.Equal(19, esp.RightPins.Count);

        var pins = esp.AllPins;
        Assert.Equal(22, pins.Count(p => p.Function is PinFunction.Gpio));

        // Input only: an analog pin that can be read and never driven.
        Assert.Equal(["GPIO34", "GPIO35", "GPIO36", "GPIO39"],
            pins.Where(p => p.Function is PinFunction.AnalogIn).Select(p => p.Name).Order());

        // GPIO6 to 11 are the flash chip's, and EN is the reset button.
        Assert.Equal(["CLK", "CMD", "EN", "SD0", "SD1", "SD2", "SD3"],
            pins.Where(p => p.Function is PinFunction.Reserved).Select(p => p.Name).Order());
    }

    [Theory]
    [InlineData("GPIO34=high", "GPIO34 is an analog input and can only be read")]
    [InlineData("SD2=high", "SD2 is a reserved pin and cannot be set")]
    public void TheEsp32RefusesWhatItsPinsCannotDo(string text, string expected)
    {
        var result = PinConfiguration.Parse(text, BoardProfile.Esp32DevKit);

        Assert.Contains(expected, result.Problems);
    }

    [Fact]
    public void BothDriveThreePointThreeVolts()
    {
        foreach (var (board, pin) in new (DeveloperBoard, string)[] { (new RaspberryPiPicoBoard(), "GP15"), (new Esp32DevKitBoard(), "GPIO2") })
        {
            var circuit = new Circuit();
            circuit.Add(board);
            circuit.Connect(board.GroundPins[0], circuit.Add(new Ground()).Pin);
            board.Pins = $"{pin}=high";

            var load = circuit.Add(new Resistor(10e3));
            circuit.Connect(board.Pin(pin), load.A);
            circuit.Connect(load.B, board.GroundPins[1]);

            Assert.Equal(3.3, Settle(circuit).NodeVoltage(board.Pin(pin)), 0.05);
        }
    }

    /// <summary>A Pico ADC pin reads as a voltage, from a divider across its own 3V3 rail.</summary>
    [Fact]
    public void APicoAdcPinReadsVolts()
    {
        var (circuit, pico) = Grounded<RaspberryPiPicoBoard>();
        pico.Pins = "GP26=in; GP14=in-pullup";

        var top = circuit.Add(new Resistor(10e3));
        var bottom = circuit.Add(new Resistor(10e3));
        circuit.Connect(pico.Pin("3V3"), top.A);
        circuit.Connect(top.B, pico.Pin("GP26"));
        circuit.Connect(pico.Pin("GP26"), bottom.A);
        circuit.Connect(bottom.B, pico.GroundPins[1]);

        Settle(circuit);

        Assert.Contains("GP26=1.65V", pico.InputSummary);
        Assert.Contains("GP14=1", pico.InputSummary);
    }

    [Fact]
    public void FiveVoltsOnEitherIsFlagged()
    {
        foreach (var (board, pin) in new (DeveloperBoard, string)[] { (new RaspberryPiPicoBoard(), "GP2"), (new Esp32DevKitBoard(), "GPIO4") })
        {
            var circuit = new Circuit();
            circuit.Add(board);
            circuit.Connect(board.GroundPins[0], circuit.Add(new Ground()).Pin);
            board.Pins = $"{pin}=in";

            var five = circuit.Add(new DcVoltageSource(5.0));
            circuit.Connect(five.Positive, board.Pin(pin));
            circuit.Connect(five.Negative, board.GroundPins[1]);

            var sim = Settle(circuit);
            board.CheckLimits(sim.System);

            Assert.Contains("not 5V tolerant", string.Join(" | ", board.Violations));
        }
    }

    [Fact]
    public void TheyRoundTripThroughTheFile()
    {
        var circuit = new Circuit();
        circuit.Add(new RaspberryPiPicoBoard { Pins = "GP15=pwm@1kHz:25%" });
        circuit.Add(new Esp32DevKitBoard { Pins = "GPIO2=clock@2kHz" });

        var back = CircuitSerializer.FromJson(CircuitSerializer.ToJson(circuit)).Circuit;

        Assert.Equal("GP15=pwm@1kHz:25%", back.Components.OfType<RaspberryPiPicoBoard>().Single().Pins);
        Assert.Equal("GPIO2=clock@2kHz", back.Components.OfType<Esp32DevKitBoard>().Single().Pins);
    }
}
