using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Processors;
using Cirq.Components.Sources;

namespace Cirq.UI.ViewModels;

public static partial class Examples
{
    /// <summary>
    /// The blink firmware, as the guide prints it assembled: Timer1 in CTC mode at clk/256 with OCR1A at 31249, so its
    /// compare interrupt comes twice a second and toggles PB5 (an Uno's pin 13) by writing PINB5. A UI test assembles the
    /// guide's source and checks it comes out as these bytes.
    /// </summary>
    public static readonly byte[] AtmegaBlinkFirmware =
    [
        0x16, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x13, 0xC0, 0x08, 0xE0, 0x0E, 0xBF, 0x0F, 0xEF, 0x0D, 0xBF, 0x25, 0x9A, 0x0A, 0xE7, 0x00, 0x93,
        0x89, 0x00, 0x01, 0xE1, 0x00, 0x93, 0x88, 0x00, 0x0C, 0xE0, 0x00, 0x93, 0x81, 0x00, 0x02, 0xE0, 0x00, 0x93, 0x6F, 0x00,
        0x78, 0x94, 0xFF, 0xCF, 0x1D, 0x9A, 0x18, 0x95,
    ];

    /// <summary>An ATmega328P on a 5 V supply running the blink, an LED and its resistor on PB5.</summary>
    public static void LoadAtmegaBlink(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "ATmega328P blink";

        var supply = Place(circuit, new DcVoltageSource(5.0), -320, 40);
        var chip = Place(circuit, new Atmega328p(), 0, 0);
        var resistor = Place(circuit, new Resistor(330), 200, 100);
        var led = Place(circuit, new Led(), 280, 180);
        var ground = Place(circuit, new Ground(), -320, 200);
        var ground2 = Place(circuit, new Ground(), 0, 220);
        var ground3 = Place(circuit, new Ground(), 280, 260);
        led.RotationDegrees = 90;

        chip.Load(MemoryImage.FromBinary(AtmegaBlinkFirmware));

        circuit.Connect(supply.Terminals[1], ground.Pin);
        circuit.Connect(supply.Terminals[0], chip.Vcc);
        circuit.Connect(supply.Terminals[0], chip.Avcc);
        circuit.Connect(chip.Gnd, ground2.Pin);
        circuit.Connect(chip.Gnd2, ground2.Pin);
        var pb5 = chip.TerminalByName("PB5");
        circuit.Connect(pb5, resistor.A);
        circuit.Connect(resistor.B, led.A);
        circuit.Connect(led.B, ground3.Pin);

        vm.Scope.TimebasePerDivision = 0.5;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.AddProbe(pb5, "PB5");
        vm.Simulation.SpeedFactor = 1.0;
    }
    /// <summary>The Uno's SPI counter, as the guide prints it assembled: the count shifted into a 74595 and latched, ten times a second.</summary>
    public static readonly byte[] UnoSpiCounterFirmware =
    [
        0x00, 0xC0, 0x08, 0xE0, 0x0E, 0xBF, 0x0F, 0xEF, 0x0D, 0xBF, 0x0E, 0xE2, 0x04, 0xB9, 0x01, 0xE5, 0x0C, 0xBD, 0x44, 0x27,
        0x4E, 0xBD, 0x0D, 0xB5, 0x07, 0xFF, 0xFD, 0xCF, 0x0E, 0xB5, 0x29, 0x9A, 0x29, 0x98, 0x43, 0x95, 0x26, 0xE0, 0x88, 0x27,
        0x99, 0x27, 0x01, 0x97, 0xF1, 0xF7, 0x2A, 0x95, 0xD1, 0xF7, 0xF0, 0xCF,
    ];

    /// <summary>The Uno's I2C EEPROM program, as the guide prints it assembled: "HI!" written, read back and printed at 9600 baud.</summary>
    public static readonly byte[] UnoI2cEepromFirmware =
    [
        0x00, 0xC0, 0x08, 0xE0, 0x0E, 0xBF, 0x0F, 0xEF, 0x0D, 0xBF, 0x00, 0xE0, 0x00, 0x93, 0xC5, 0x00, 0x07, 0xE6, 0x00, 0x93,
        0xC4, 0x00, 0x08, 0xE0, 0x00, 0x93, 0xC1, 0x00, 0x08, 0xE4, 0x00, 0x93, 0xB8, 0x00, 0x2A, 0xD0, 0x80, 0xEA, 0x2A, 0xD0,
        0x80, 0xE0, 0x28, 0xD0, 0x27, 0xD0, 0x88, 0xE4, 0x25, 0xD0, 0x89, 0xE4, 0x23, 0xD0, 0x81, 0xE2, 0x21, 0xD0, 0x2A, 0xD0,
        0x1D, 0xD0, 0x80, 0xEA, 0x1D, 0xD0, 0x80, 0xE0, 0x1B, 0xD0, 0x1A, 0xD0, 0x17, 0xD0, 0x81, 0xEA, 0x17, 0xD0, 0x04, 0xEC,
        0x18, 0xD0, 0x80, 0x91, 0xBB, 0x00, 0x23, 0xD0, 0x04, 0xEC, 0x13, 0xD0, 0x80, 0x91, 0xBB, 0x00, 0x1E, 0xD0, 0x04, 0xE8,
        0x0E, 0xD0, 0x80, 0x91, 0xBB, 0x00, 0x19, 0xD0, 0x11, 0xD0, 0x8D, 0xE0, 0x16, 0xD0, 0x8A, 0xE0, 0x14, 0xD0, 0xFF, 0xCF,
        0x04, 0xEA, 0x03, 0xC0, 0x80, 0x93, 0xBB, 0x00, 0x04, 0xE8, 0x00, 0x93, 0xBC, 0x00, 0x00, 0x91, 0xBC, 0x00, 0x07, 0xFF,
        0xFC, 0xCF, 0x08, 0x95, 0x04, 0xE9, 0x00, 0x93, 0xBC, 0x00, 0x08, 0xEC, 0x0A, 0x95, 0xF1, 0xF7, 0x08, 0x95, 0x00, 0x91,
        0xC0, 0x00, 0x05, 0xFF, 0xFC, 0xCF, 0x80, 0x93, 0xC6, 0x00, 0x08, 0x95,
    ];

    /// <summary>An Arduino Uno counting in binary on eight LEDs, through a 74595 shift register on its SPI pins.</summary>
    public static void LoadUnoSpiCounter(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Arduino Uno SPI counter";
        var uno = Place(circuit, new ArduinoUnoFirmware(), -200, 0);
        uno.Load(MemoryImage.FromBinary(UnoSpiCounterFirmware));
        var register = Place(circuit, new Ic74595(), 160, 0);
        var ground = Place(circuit, new Ground(), 160, 200);
        var five = uno.TerminalByName("5V");
        circuit.Connect(uno.TerminalByName("GND"), ground.Pin);
        circuit.Connect(register.Vcc, five);
        circuit.Connect(register.Gnd, ground.Pin);
        circuit.Connect(register.TerminalByName("SRCLR"), five);
        circuit.Connect(register.TerminalByName("OE"), ground.Pin);
        circuit.Connect(register.TerminalByName("SER"), uno.TerminalByName("D11"));
        circuit.Connect(register.TerminalByName("SRCLK"), uno.TerminalByName("D13/LED"));
        circuit.Connect(register.TerminalByName("RCLK"), uno.TerminalByName("D9"));
        var ledGround = Place(circuit, new Ground(), 560, 240);
        for (var i = 0; i < 8; i++)
        {
            var resistor = Place(circuit, new Resistor(330), 380, -140 + (i * 40));
            var led = Place(circuit, new Led(), 480, -140 + (i * 40));
            circuit.Connect(register.Outputs[7 - i], resistor.A);
            circuit.Connect(resistor.B, led.A);
            circuit.Connect(led.B, ledGround.Pin);
        }

        vm.Scope.TimebasePerDivision = 0.1;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.AddProbe(register.Outputs[7], "QH (bit 0)");
        vm.Scope.AddProbe(register.Outputs[6], "QG (bit 1)");
        vm.Simulation.SpeedFactor = 1.0;
    }

    /// <summary>An Arduino Uno writing to a 24LC256 over I2C, reading it back, and printing what it read on a terminal.</summary>
    public static void LoadUnoI2cEeprom(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Arduino Uno I2C EEPROM";
        var uno = Place(circuit, new ArduinoUnoFirmware(), -200, 0);
        uno.Load(MemoryImage.FromBinary(UnoI2cEepromFirmware));
        var eeprom = Place(circuit, new I2cEeprom(), 200, -60);
        var sdaPull = Place(circuit, new Resistor(4.7e3), 60, -180);
        var sclPull = Place(circuit, new Resistor(4.7e3), 140, -180);
        var terminal = Place(circuit, new SerialTerminal { BaudRate = 9600, Message = "" }, 220, 140);
        var ground = Place(circuit, new Ground(), 200, 300);
        sdaPull.RotationDegrees = 90;
        sclPull.RotationDegrees = 90;
        var five = uno.TerminalByName("5V");
        circuit.Connect(uno.TerminalByName("GND"), ground.Pin);
        circuit.Connect(eeprom.Vcc, five);
        circuit.Connect(eeprom.Gnd, ground.Pin);
        var sda = uno.TerminalByName("A4");
        var scl = uno.TerminalByName("A5");
        circuit.Connect(eeprom.Sda, sda);
        circuit.Connect(eeprom.Scl, scl);
        circuit.Connect(sdaPull.A, five);
        circuit.Connect(sdaPull.B, sda);
        circuit.Connect(sclPull.A, five);
        circuit.Connect(sclPull.B, scl);
        circuit.Connect(terminal.Receive, uno.TerminalByName("D1/TX"));
        circuit.Connect(terminal.Vcc, five);
        circuit.Connect(terminal.Gnd, ground.Pin);

        vm.Scope.TimebasePerDivision = 0.5e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = Cirq.UI.ViewModels.ScopeLayout.Tiled;
        vm.Scope.AddProbe(sda, "SDA");
        vm.Scope.AddProbe(scl, "SCL");
        vm.Scope.AddProbe(uno.TerminalByName("D1/TX"), "TX");
    }
}
