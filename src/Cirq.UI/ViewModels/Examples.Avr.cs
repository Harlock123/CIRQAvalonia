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
}
