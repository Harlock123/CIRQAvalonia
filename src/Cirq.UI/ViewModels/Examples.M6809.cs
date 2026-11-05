using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Processors;
using Cirq.Components.Sources;
using Cirq.Core.Topology;

namespace Cirq.UI.ViewModels;

/// <summary>A 6809 single-board computer: RAM, an EPROM, a 74138 and a 7400 decoding, and its peripherals.</summary>
public static partial class Examples
{
    /// <summary>
    /// The 6809 monitor, assembled from the source in the guide (a test checks these are its
    /// bytes): a banner over the 6850 at 115,200 baud, a prompt, and a D command that dumps eight
    /// bytes in hex. Runs of bytes and where each starts.
    /// </summary>
    internal static readonly (int Address, byte[] Bytes)[] Monitor6809Program =
    [
        (0xE000, [
            0x10, 0xCE, 0x80, 0x00, 0x86, 0x03, 0xB7, 0xA0, 0x00, 0x86, 0x15, 0xB7, 0xA0, 0x00, 0x8E, 0xE0,
            0xE6, 0x8D, 0x61, 0x8E, 0xE0, 0xFA, 0x8D, 0x5C, 0x10, 0x8E, 0x01, 0x00, 0xB6, 0xA0, 0x00, 0x85,
            0x01, 0x27, 0xF9, 0xB6, 0xA0, 0x01, 0x8D, 0x3E, 0x81, 0x0D, 0x27, 0x04, 0xA7, 0xA0, 0x20, 0xEC,
            0x6F, 0xA4, 0x86, 0x0A, 0x8D, 0x30, 0x10, 0x8E, 0x01, 0x00, 0xA6, 0xA0, 0x84, 0xDF, 0x81, 0x44,
            0x26, 0x1D, 0x8D, 0x5D, 0x25, 0x19, 0x1F, 0x10, 0x8D, 0x3B, 0x86, 0x3A, 0x8D, 0x18, 0xC6, 0x08,
            0x86, 0x20, 0x8D, 0x12, 0xA6, 0x80, 0x8D, 0x33, 0x5A, 0x26, 0xF5, 0x8D, 0x20, 0x20, 0xB4, 0x8E,
            0xE0, 0xFD, 0x8D, 0x10, 0x20, 0xAD, 0x34, 0x04, 0xF6, 0xA0, 0x00, 0xC5, 0x02, 0x27, 0xF9, 0xB7,
            0xA0, 0x01, 0x35, 0x84, 0xA6, 0x80, 0x27, 0x04, 0x8D, 0xEC, 0x20, 0xF8, 0x39, 0x86, 0x0D, 0x8D,
            0xE5, 0x86, 0x0A, 0x20, 0xE1, 0x34, 0x04, 0x8D, 0x02, 0x35, 0x02, 0x34, 0x02, 0x44, 0x44, 0x44,
            0x44, 0x8D, 0x02, 0x35, 0x02, 0x84, 0x0F, 0x8B, 0x30, 0x81, 0x39, 0x23, 0x02, 0x8B, 0x07, 0x20,
            0xC5, 0x8E, 0x00, 0x00, 0xA6, 0xA0, 0x81, 0x20, 0x27, 0xFA, 0x8D, 0x1B, 0x25, 0x18, 0x34, 0x02,
            0x1F, 0x10, 0x58, 0x49, 0x58, 0x49, 0x58, 0x49, 0x58, 0x49, 0xEA, 0xE0, 0x1F, 0x01, 0xA6, 0xA0,
            0x8D, 0x05, 0x24, 0xEA, 0x1C, 0xFE, 0x39, 0x81, 0x30, 0x25, 0x18, 0x81, 0x39, 0x23, 0x0F, 0x84,
            0xDF, 0x81, 0x41, 0x25, 0x0E, 0x81, 0x46, 0x22, 0x0A, 0x80, 0x37, 0x1C, 0xFE, 0x39, 0x80, 0x30,
            0x1C, 0xFE, 0x39, 0x1A, 0x01, 0x39, 0x43, 0x49, 0x52, 0x51, 0x20, 0x36, 0x38, 0x30, 0x39, 0x20,
            0x6D, 0x6F, 0x6E, 0x69, 0x74, 0x6F, 0x72, 0x0D, 0x0A, 0x00, 0x3E, 0x20, 0x00, 0x3F, 0x0D, 0x0A,
            0x00,
        ]),
        (0xFFFE, [
            0xE0, 0x00,
        ]),
    ];

    /// <summary>
    /// The PIA example's program, assembled from the guide's source: an HD44780 on port B and
    /// PA5-PA7, and the presses of a button on CA1, counted in the interrupt handler and shown.
    /// </summary>
    internal static readonly (int Address, byte[] Bytes)[] Buttons6809Program =
    [
        (0xE000, [
            0x10, 0xCE, 0x80, 0x00, 0x7F, 0x00, 0x10, 0x86, 0xFF, 0xB7, 0x00, 0x11, 0x86, 0x04, 0xB7, 0xC0,
            0x01, 0x86, 0xC0, 0xB7, 0xC0, 0x00, 0x7F, 0xC0, 0x01, 0x86, 0xE0, 0xB7, 0xC0, 0x00, 0x7F, 0xC0,
            0x03, 0x86, 0xFF, 0xB7, 0xC0, 0x02, 0x86, 0x05, 0xB7, 0xC0, 0x01, 0x86, 0x04, 0xB7, 0xC0, 0x03,
            0x86, 0x40, 0xB7, 0xC0, 0x00, 0x8E, 0xE0, 0xD7, 0xA6, 0x80, 0x27, 0x04, 0x8D, 0x4D, 0x20, 0xF8,
            0x86, 0x80, 0x8D, 0x47, 0x8E, 0xE0, 0xDB, 0x8D, 0x39, 0x1C, 0xEF, 0xB6, 0x00, 0x10, 0xB1, 0x00,
            0x11, 0x27, 0xF8, 0xB7, 0x00, 0x11, 0x86, 0xC0, 0x8D, 0x31, 0x8E, 0xE0, 0xEC, 0x8D, 0x23, 0xB6,
            0x00, 0x11, 0x44, 0x44, 0x44, 0x44, 0x8B, 0x30, 0x8D, 0x25, 0xB6, 0x00, 0x11, 0x84, 0x0F, 0x8B,
            0x30, 0x8D, 0x1C, 0x20, 0xD6, 0xB6, 0xC0, 0x00, 0xB6, 0x00, 0x10, 0x8B, 0x01, 0x19, 0xB7, 0x00,
            0x10, 0x3B, 0xA6, 0x80, 0x27, 0x04, 0x8D, 0x07, 0x20, 0xF8, 0x39, 0xC6, 0x00, 0x20, 0x02, 0xC6,
            0x20, 0x8D, 0x13, 0xB7, 0xC0, 0x02, 0xF7, 0xC0, 0x00, 0x34, 0x04, 0xCA, 0x80, 0xF7, 0xC0, 0x00,
            0x35, 0x04, 0xF7, 0xC0, 0x00, 0x39, 0x34, 0x06, 0x7F, 0xC0, 0x03, 0x7F, 0xC0, 0x02, 0x86, 0x04,
            0xB7, 0xC0, 0x03, 0x86, 0x40, 0xB7, 0xC0, 0x00, 0x86, 0xC0, 0xB7, 0xC0, 0x00, 0xF6, 0xC0, 0x02,
            0x86, 0x40, 0xB7, 0xC0, 0x00, 0x5D, 0x2B, 0xEB, 0x7F, 0xC0, 0x03, 0x86, 0xFF, 0xB7, 0xC0, 0x02,
            0x86, 0x04, 0xB7, 0xC0, 0x03, 0x35, 0x86, 0x38, 0x0C, 0x06, 0x00, 0x43, 0x49, 0x52, 0x51, 0x20,
            0x36, 0x38, 0x30, 0x39, 0x20, 0x2B, 0x20, 0x36, 0x38, 0x32, 0x31, 0x00, 0x50, 0x72, 0x65, 0x73,
            0x73, 0x65, 0x73, 0x3A, 0x20, 0x00,
        ]),
        (0xFFF8, [
            0xE0, 0x75,
        ]),
        (0xFFFE, [
            0xE0, 0x00,
        ]),
    ];

    /// <summary>A program as the 8K image of the 2764 at $E000, unused bytes zero.</summary>
    internal static byte[] Image6809((int Address, byte[] Bytes)[] program)
    {
        var image = new byte[0x2000];
        foreach (var (address, bytes) in program) bytes.CopyTo(image, address - 0xE000);
        return image;
    }

    /// <summary>The parts every 6809 example shares.</summary>
    private sealed record Computer6809(Circuit Circuit, Cpu6809 Cpu, Eprom2764 Rom, Sram62256 Ram, Ic74138 Decoder, Ic7400 Nand);

    /// <summary>
    /// An MC6809 with its E at 1.8432 MHz — a 7.3728 MHz oscillator into EXTAL — 32K of RAM (a 62256)
    /// at $0000-$7FFF selected by A15 low, and an 8K 2764 at $E000-$FFFF. A 74138 on A13-A15 splits
    /// the top half into 8K blocks: Y5 is ACIA_SEL ($A000), Y6 PIA_SEL ($C000) and Y7 ROM_SEL. A 7400
    /// makes /RD = NAND(E, R/W) for the RAM's /OE and /WR = NAND(E, NOT R/W) for its /WE, so the RAM
    /// only acts while E is high, as on Grant Searle's simple 6809; the EPROM's /OE is tied low and
    /// its /CE alone selects it. /IRQ is pulled up for the peripherals' open-drain requests; /RESET
    /// has a button. 1.8432 MHz is a 68B09's speed, and E ÷ 16 is 115,200 baud for a 6850.
    /// </summary>
    private static Computer6809 Build6809(MainWindowViewModel vm, byte[] image)
    {
        var circuit = vm.Circuit;

        var supply = Place(circuit, new DcVoltageSource(5.0), -560, 440);
        var ground = Place(circuit, new Ground(), -560, 560);
        supply.RotationDegrees = 90;
        circuit.Connect(supply.Negative, ground.Pin);
        Tag(circuit, supply.Positive, "VCC", 270);
        Tag(circuit, ground.Pin, "GND", 180);

        var clock = Place(circuit, new OscillatorModule(7.3728e6), -560, -250);
        Tag(circuit, clock.Vcc, "VCC", 270);
        Tag(circuit, clock.Gnd, "GND");
        Tag(circuit, clock.Output, "EXTAL");

        // /RESET pulled up, and a button to pull it down.
        var pullUp = Place(circuit, new Resistor(4.7e3), -560, -100);
        var reset = Place(circuit, new PushButton(), -560, 20);
        pullUp.RotationDegrees = 90;
        reset.RotationDegrees = 90;
        Tag(circuit, pullUp.A, "VCC", 270);
        circuit.Connect(pullUp.B, reset.A);
        Tag(circuit, pullUp.B, "RESET", 360);
        Tag(circuit, reset.B, "GND", 90);

        // /IRQ pulled up, so the peripherals' open-drain outputs only pull it down.
        var irqPullUp = Place(circuit, new Resistor(4.7e3), -560, 200);
        irqPullUp.RotationDegrees = 90;
        Tag(circuit, irqPullUp.A, "VCC", 270);
        Tag(circuit, irqPullUp.B, "IRQ", 360);

        var cpu = Place(circuit, new Cpu6809(), -200, 0);
        for (var i = 0; i < 16; i++) Tag(circuit, cpu.Address[i], $"A{i}");
        for (var i = 0; i < 8; i++) Tag(circuit, cpu.Data[i], $"D{i}");
        Tag(circuit, cpu.ReadWrite, "RW");
        Tag(circuit, cpu.E, "E");
        Tag(circuit, cpu.Q, "Q");
        Tag(circuit, cpu.Extal!, "EXTAL");
        Tag(circuit, cpu.Reset, "RESET");
        Tag(circuit, cpu.Irq, "IRQ");
        foreach (var pin in new[] { cpu.Halt, cpu.Nmi, cpu.Firq, cpu.DmaRequest!, cpu.MemoryReady! }) Tag(circuit, pin, "VCC");
        Tag(circuit, cpu.BusAvailable, "BA");
        Tag(circuit, cpu.BusStatus, "BS");
        Tag(circuit, cpu.Vcc, "VCC");
        Tag(circuit, cpu.Gnd, "GND");

        var rom = Place(circuit, new Eprom2764(), 180, -60);
        rom.Load(MemoryImage.FromBinary(image));
        var ram = Place(circuit, new Sram62256(), 520, -60);

        for (var i = 0; i < 13; i++) Tag(circuit, rom.Address[i], $"A{i}");
        for (var i = 0; i < 15; i++) Tag(circuit, ram.Address[i], $"A{i}");
        foreach (var memory in new ParallelMemory[] { rom, ram })
        {
            for (var i = 0; i < 8; i++) Tag(circuit, memory.Data[i], $"D{i}");
            Tag(circuit, memory.Vcc, "VCC");
            Tag(circuit, memory.Gnd, "GND");
        }

        Tag(circuit, rom.ChipEnable, "ROM_SEL");
        Tag(circuit, rom.OutputEnable, "GND");
        foreach (var pin in rom.Terminals.Where(t => t.Name is "VPP" or "PGM")) Tag(circuit, pin, "VCC");
        Tag(circuit, ram.ChipEnable, "A15");
        Tag(circuit, ram.OutputEnable, "RD");
        Tag(circuit, ram.WriteEnable, "WR");

        // The 74138 on A13-A15, always enabled: 8K blocks in the top half.
        var decoder = Place(circuit, new Ic74138(), 180, 360);
        Tag(circuit, decoder.Vcc, "VCC");
        Tag(circuit, decoder.Gnd, "GND");
        Tag(circuit, decoder.SelectA, "A13");
        Tag(circuit, decoder.SelectB, "A14");
        Tag(circuit, decoder.SelectC, "A15");
        Tag(circuit, decoder.Enable1, "GND");
        Tag(circuit, decoder.Enable2, "GND");
        Tag(circuit, decoder.Enable3, "VCC");
        Tag(circuit, decoder.Outputs[5], "ACIA_SEL");
        Tag(circuit, decoder.Outputs[6], "PIA_SEL");
        Tag(circuit, decoder.Outputs[7], "ROM_SEL");

        // The 7400: NOT R/W, then /RD and /WR, each only while E is high.
        var nand = Place(circuit, new Ic7400(), 520, 360);
        Tag(circuit, nand.Vcc, "VCC");
        Tag(circuit, nand.Gnd, "GND");
        foreach (var (gate, a, b, y) in new[] { (0, "RW", "RW", "NRW"), (1, "E", "RW", "RD"), (2, "E", "NRW", "WR"), (3, "GND", "GND", "") })
        {
            var (ga, gb, gy) = nand.Gate(gate);
            Tag(circuit, ga, a);
            Tag(circuit, gb, b);
            if (y.Length > 0) Tag(circuit, gy, y);
        }

        return new Computer6809(circuit, cpu, rom, ram, decoder, nand);
    }

    /// <summary>
    /// The 6809 computer with a 6850 at $A000 (/CS2 = ACIA_SEL, RS = A0) clocked from E on both
    /// TxCLK and RxCLK, talking to the serial terminal at 115,200 baud: a monitor that prints a
    /// banner and dumps memory on request — the terminal asks for the ROM at $E000.
    /// </summary>
    public static void Load6809Monitor(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "A 6809 single-board computer";

        Build6809(vm, Image6809(Monitor6809Program));

        var acia = Place(circuit, new Acia6850(), 880, -60);
        for (var i = 0; i < 8; i++) Tag(circuit, acia.Data[i], $"D{i}");
        Tag(circuit, acia.RegisterSelect, "A0");
        Tag(circuit, acia.ChipSelect0, "VCC");
        Tag(circuit, acia.ChipSelect1, "VCC");
        Tag(circuit, acia.ChipSelect2, "ACIA_SEL");
        Tag(circuit, acia.Enable, "E");
        Tag(circuit, acia.ReadWrite, "RW");
        Tag(circuit, acia.TransmitClock, "E");
        Tag(circuit, acia.ReceiveClock, "E");
        Tag(circuit, acia.Irq, "IRQ");
        Tag(circuit, acia.ClearToSend, "GND");
        Tag(circuit, acia.CarrierDetect, "GND");
        Tag(circuit, acia.Vcc, "VCC");
        Tag(circuit, acia.Gnd, "GND");

        var terminal = Place(circuit, new SerialTerminal
        {
            BaudRate = 115200,
            Message = "D E000\r",
            StartDelay = 2.5e-3,
        }, 1260, -60);
        Tag(circuit, acia.Transmit, "TXD");
        Tag(circuit, terminal.Receive, "TXD");
        Tag(circuit, acia.Receive, "RXD");
        Tag(circuit, terminal.Transmit, "RXD");
        Tag(circuit, terminal.Vcc, "VCC");
        Tag(circuit, terminal.Gnd, "GND");

        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(acia.Transmit, "TxData");
        vm.Scope.AddProbe(acia.Receive, "RxData");
    }

    /// <summary>
    /// The 6809 computer with a 6821 at $C000 (/CS2 = PIA_SEL, RS0 = A0, RS1 = A1): an HD44780 on
    /// port B and PA5-PA7, and a button on CA1 whose presses interrupt through /IRQA and are counted
    /// on the display.
    /// </summary>
    public static void Load6809Buttons(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Counting presses on a 6821";

        Build6809(vm, Image6809(Buttons6809Program));

        var pia = Place(circuit, new Pia6821(), 880, -60);
        for (var i = 0; i < 8; i++) Tag(circuit, pia.Data[i], $"D{i}");
        Tag(circuit, pia.RegisterSelect0, "A0");
        Tag(circuit, pia.RegisterSelect1, "A1");
        Tag(circuit, pia.ChipSelect0, "VCC");
        Tag(circuit, pia.ChipSelect1, "VCC");
        Tag(circuit, pia.ChipSelect2, "PIA_SEL");
        Tag(circuit, pia.Enable, "E");
        Tag(circuit, pia.ReadWrite, "RW");
        Tag(circuit, pia.Reset, "RESET");
        Tag(circuit, pia.IrqA, "IRQ");
        Tag(circuit, pia.Vcc, "VCC");
        Tag(circuit, pia.Gnd, "GND");
        Tag(circuit, pia.Ca1, "BUTTON");
        Tag(circuit, pia.Cb1, "VCC");

        // Port B is the LCD's data bus; PA5, PA6 and PA7 its RS, RW and E.
        for (var i = 0; i < 8; i++) Tag(circuit, pia.PortB[i], $"PB{i}");
        Tag(circuit, pia.PortA[5], "LCD_RS");
        Tag(circuit, pia.PortA[6], "LCD_RW");
        Tag(circuit, pia.PortA[7], "LCD_E");

        var lcd = Place(circuit, new CharacterLcd(), 880, 440);
        Tag(circuit, lcd.Gnd, "GND", 270);
        Tag(circuit, lcd.Vcc, "VCC", 270);
        Tag(circuit, lcd.RegisterSelect, "LCD_RS", 270);
        Tag(circuit, lcd.ReadWrite, "LCD_RW", 270);
        Tag(circuit, lcd.Enable, "LCD_E", 270);
        for (var i = 0; i < 8; i++) Tag(circuit, lcd.Data[i], $"PB{i}", 270);

        var contrast = Place(circuit, new Potentiometer(10e3, 0.3), 560, 600);
        Tag(circuit, contrast.A, "GND", 180);
        Tag(circuit, contrast.B, "VCC");
        Tag(circuit, contrast.Wiper, "LCD_V0", 270);
        Tag(circuit, lcd.Contrast, "LCD_V0", 270);

        var backlight = Place(circuit, new Resistor(220), 1220, 440);
        backlight.RotationDegrees = 90;
        Tag(circuit, backlight.A, "VCC", 270);
        Tag(circuit, backlight.B, "LCD_A", 90);
        Tag(circuit, lcd.Backlight, "LCD_A", 270);
        Tag(circuit, lcd.BacklightCathode, "GND", 270);

        // The button on CA1, pulled up: a press is CA1's falling edge.
        var buttonPullUp = Place(circuit, new Resistor(4.7e3), 1260, -100);
        var button = Place(circuit, new PushButton(), 1260, 20);
        buttonPullUp.RotationDegrees = 90;
        button.RotationDegrees = 90;
        Tag(circuit, buttonPullUp.A, "VCC", 270);
        circuit.Connect(buttonPullUp.B, button.A);
        Tag(circuit, buttonPullUp.B, "BUTTON", 360);
        Tag(circuit, button.B, "GND", 90);

        vm.Scope.TimebasePerDivision = 0.5e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(pia.Ca1, "CA1");
        vm.Scope.AddProbe(pia.IrqA, "/IRQA");
        vm.Scope.AddProbe(pia.PortA[7], "LCD E");
    }
}
