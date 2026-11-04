using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Processors;
using Cirq.Components.Sources;
using Cirq.Core.Topology;

namespace Cirq.UI.ViewModels;

/// <summary>The Commodore 64's chips on a breadboard: a 6510 with a 6526 and a 6581.</summary>
public static partial class Examples
{
    /// <summary>
    /// The SID player's program, assembled from the source in the guide (and a test checks these are
    /// its bytes): the 6526's timer A interrupting 200 times a second, and on each interrupt a tick
    /// of a sixteen-step tune — a filtered sawtooth bass, a pulse arpeggio with its width sweeping and
    /// a triangle lead with vibrato — with the 6510's P0 blinking in time.
    /// </summary>
    internal static readonly byte[] SidPlayerProgram =
    [
        0x78, 0xA2, 0xFF, 0x9A, 0xA9, 0x01, 0x85, 0x00, 0xA9, 0x00, 0xA2, 0x18, 0x9D, 0x00, 0x50, 0xCA,
        0x10, 0xFA, 0x85, 0x02, 0x85, 0x03, 0x85, 0x04, 0x85, 0x05, 0x85, 0x06, 0xA9, 0x02, 0x85, 0x07,
        0xA9, 0x00, 0x8D, 0x05, 0x50, 0xA9, 0x80, 0x8D, 0x06, 0x50, 0xA9, 0x00, 0x8D, 0x0C, 0x50, 0xA9,
        0xF0, 0x8D, 0x0D, 0x50, 0xA9, 0x00, 0x8D, 0x13, 0x50, 0xA9, 0xA0, 0x8D, 0x14, 0x50, 0xA9, 0x81,
        0x8D, 0x17, 0x50, 0xA9, 0x1F, 0x8D, 0x18, 0x50, 0xA9, 0x41, 0x8D, 0x0B, 0x50, 0xA9, 0x87, 0x8D,
        0x04, 0x40, 0xA9, 0x13, 0x8D, 0x05, 0x40, 0xA9, 0x81, 0x8D, 0x0D, 0x40, 0xA9, 0x11, 0x8D, 0x0E,
        0x40, 0x58, 0x4C, 0x62, 0x80, 0x48, 0x8A, 0x48, 0x98, 0x48, 0xAD, 0x0D, 0x40, 0xA5, 0x02, 0xD0,
        0x03, 0x20, 0x98, 0x80, 0x20, 0xD4, 0x80, 0x20, 0x15, 0x81, 0x20, 0x59, 0x81, 0xE6, 0x02, 0xA5,
        0x02, 0xC9, 0x05, 0xD0, 0x0D, 0xA9, 0x00, 0x85, 0x02, 0xA5, 0x03, 0x18, 0x69, 0x01, 0x29, 0x0F,
        0x85, 0x03, 0x68, 0xA8, 0x68, 0xAA, 0x68, 0x40, 0xA6, 0x03, 0xBC, 0x6F, 0x81, 0xB9, 0xA3, 0x81,
        0x8D, 0x00, 0x50, 0xB9, 0xB3, 0x81, 0x8D, 0x01, 0x50, 0xA9, 0x21, 0x8D, 0x04, 0x50, 0xA9, 0x68,
        0x85, 0x0A, 0xBC, 0x7F, 0x81, 0xF0, 0x14, 0xB9, 0xA3, 0x81, 0x85, 0x08, 0xB9, 0xB3, 0x81, 0x85,
        0x09, 0xA9, 0x10, 0x8D, 0x12, 0x50, 0xA9, 0x11, 0x8D, 0x12, 0x50, 0x8A, 0x4A, 0x29, 0x01, 0x49,
        0x01, 0x85, 0x01, 0x60, 0xA5, 0x03, 0x4A, 0x4A, 0x85, 0x0B, 0x0A, 0x18, 0x65, 0x0B, 0x65, 0x04,
        0xA8, 0xBE, 0x8F, 0x81, 0xBD, 0xA3, 0x81, 0x8D, 0x07, 0x50, 0xBD, 0xB3, 0x81, 0x8D, 0x08, 0x50,
        0xA6, 0x04, 0xE8, 0xE0, 0x03, 0xD0, 0x02, 0xA2, 0x00, 0x86, 0x04, 0xA5, 0x06, 0x18, 0x69, 0x40,
        0x85, 0x06, 0x8D, 0x09, 0x50, 0xA5, 0x07, 0x69, 0x00, 0xC9, 0x0E, 0xD0, 0x02, 0xA9, 0x02, 0x85,
        0x07, 0x8D, 0x0A, 0x50, 0x60, 0xA5, 0x08, 0x85, 0x0C, 0xA5, 0x09, 0x85, 0x0D, 0xA6, 0x05, 0xBC,
        0x9B, 0x81, 0xF0, 0x24, 0x30, 0x12, 0x18, 0xA5, 0x0C, 0x65, 0x09, 0x85, 0x0C, 0xA5, 0x0D, 0x69,
        0x00, 0x85, 0x0D, 0x88, 0xD0, 0xF0, 0xF0, 0x10, 0x38, 0xA5, 0x0C, 0xE5, 0x09, 0x85, 0x0C, 0xA5,
        0x0D, 0xE9, 0x00, 0x85, 0x0D, 0xC8, 0xD0, 0xF0, 0xA5, 0x0C, 0x8D, 0x0E, 0x50, 0xA5, 0x0D, 0x8D,
        0x0F, 0x50, 0xE8, 0x8A, 0x29, 0x07, 0x85, 0x05, 0x60, 0xA5, 0x0A, 0x8D, 0x16, 0x50, 0x38, 0xE9,
        0x0C, 0x85, 0x0A, 0xA5, 0x02, 0xC9, 0x04, 0xD0, 0x05, 0xA9, 0x20, 0x8D, 0x04, 0x50, 0x60, 0x04,
        0x04, 0x01, 0x04, 0x02, 0x02, 0x06, 0x02, 0x03, 0x03, 0x07, 0x03, 0x01, 0x01, 0x05, 0x01, 0x0F,
        0x00, 0x0E, 0x0D, 0x0B, 0x00, 0x0D, 0x00, 0x0C, 0x00, 0x0E, 0x00, 0x0F, 0x00, 0x00, 0x0C, 0x0B,
        0x0D, 0x0F, 0x09, 0x0B, 0x0D, 0x0A, 0x0C, 0x0E, 0x08, 0x0A, 0x0C, 0x00, 0x01, 0x02, 0x01, 0x00,
        0xFF, 0xFE, 0xFF, 0x00, 0x67, 0xB9, 0x6C, 0x35, 0x17, 0x93, 0x9F, 0x9A, 0xE3, 0xB1, 0xD6, 0x5E,
        0x4B, 0x7E, 0x34, 0x00, 0x05, 0x05, 0x06, 0x07, 0x08, 0x08, 0x09, 0x15, 0x16, 0x19, 0x1C, 0x20,
        0x22, 0x26, 0x2B,
    ];

    /// <summary>Where the SID player's interrupt handler starts: its IRQ vector.</summary>
    internal const ushort SidPlayerIrq = 0x8065;

    /// <summary>
    /// The CIA keypad program, assembled from the guide's source: the 4x4 keypad scanned through the
    /// 6526's ports a row at a time, each key shown on a four-bit HD44780 as it goes down, every
    /// delay timed by timer B in one-shot mode.
    /// </summary>
    internal static readonly byte[] CiaKeypadProgram =
    [
        0xA2, 0xFF, 0x9A, 0xA9, 0x00, 0x8D, 0x00, 0x40, 0xA9, 0x30, 0x8D, 0x02, 0x40, 0xA9, 0xF0, 0x8D,
        0x03, 0x40, 0xA9, 0x08, 0x8D, 0x0F, 0x40, 0xA0, 0x00, 0xA2, 0x00, 0xBD, 0xE7, 0x80, 0xF0, 0x0B,
        0x20, 0xB2, 0x80, 0xA9, 0x00, 0x20, 0xC1, 0x80, 0xE8, 0xD0, 0xF0, 0xA2, 0x00, 0xBD, 0xEC, 0x80,
        0xF0, 0x06, 0x20, 0xA1, 0x80, 0xE8, 0xD0, 0xF5, 0xA9, 0x07, 0x20, 0xC1, 0x80, 0xA0, 0x10, 0xA2,
        0x00, 0xBD, 0xF1, 0x80, 0xF0, 0x06, 0x20, 0xA1, 0x80, 0xE8, 0xD0, 0xF5, 0xA9, 0x00, 0x85, 0x02,
        0x20, 0x68, 0x80, 0xC5, 0x02, 0xF0, 0x09, 0x85, 0x02, 0xC9, 0x00, 0xF0, 0x03, 0x20, 0xA1, 0x80,
        0xA9, 0x04, 0x20, 0xC1, 0x80, 0x4C, 0x50, 0x80, 0xA2, 0x00, 0xBD, 0xD3, 0x80, 0x8D, 0x02, 0x40,
        0xAD, 0x01, 0x40, 0x29, 0x0F, 0xC9, 0x0F, 0xD0, 0x0D, 0xE8, 0xE0, 0x04, 0xD0, 0xEC, 0xA9, 0x30,
        0x8D, 0x02, 0x40, 0xA9, 0x00, 0x60, 0xA0, 0x00, 0x4A, 0x90, 0x03, 0xC8, 0xD0, 0xFA, 0x84, 0x03,
        0x8A, 0x0A, 0x0A, 0x05, 0x03, 0xAA, 0xA9, 0x30, 0x8D, 0x02, 0x40, 0xA0, 0x10, 0xBD, 0xD7, 0x80,
        0x60, 0x48, 0x20, 0xB2, 0x80, 0x68, 0x0A, 0x0A, 0x0A, 0x0A, 0x20, 0xB2, 0x80, 0xA9, 0x00, 0x4C,
        0xC1, 0x80, 0x29, 0xF0, 0x8D, 0x01, 0x40, 0x98, 0x09, 0x20, 0x8D, 0x00, 0x40, 0x8C, 0x00, 0x40,
        0x60, 0x48, 0xA9, 0x40, 0x8D, 0x06, 0x40, 0x68, 0x8D, 0x07, 0x40, 0xAD, 0x0D, 0x40, 0x29, 0x02,
        0xF0, 0xF9, 0x60, 0x31, 0x32, 0x34, 0x38, 0x31, 0x32, 0x33, 0x41, 0x34, 0x35, 0x36, 0x42, 0x37,
        0x38, 0x39, 0x43, 0x2A, 0x30, 0x23, 0x44, 0x30, 0x30, 0x30, 0x20, 0x00, 0x28, 0x0C, 0x06, 0x01,
        0x00, 0x4B, 0x65, 0x79, 0x73, 0x3A, 0x20,
    ];

    /// <summary>A program for a 32K ROM at $8000, the reset vector at its start and the IRQ vector at <paramref name="irq"/>.</summary>
    internal static byte[] RomImage(byte[] program, ushort irq)
    {
        var image = RomImage(program);
        image[0x7FFE] = (byte)irq;
        image[0x7FFF] = (byte)(irq >> 8);
        return image;
    }

    /// <summary>The parts the two 6510 examples share.</summary>
    private sealed record Computer6510(Circuit Circuit, Cpu6510 Cpu, Eeprom28C256 Rom, Sram62256 Ram, Ic74139 Decoder);

    /// <summary>
    /// A 6510 with a 28C256 at $8000-$FFFF and a 62256 at $0000-$3FFF, decoded by a 7400 as the Ben
    /// Eater computer is, and a 74139 splitting the I/O select at $4000-$7FFF by A12 and A13 into
    /// CIA_CS ($4000) and SID_CS ($5000). The oscillator drives Φ0 and everything else runs from the
    /// processor's Φ2 output, as on a C64.
    /// </summary>
    private static Computer6510 Build6510(MainWindowViewModel vm, byte[] rom)
    {
        var circuit = vm.Circuit;

        var supply = Place(circuit, new DcVoltageSource(5.0), -560, 440);
        var ground = Place(circuit, new Ground(), -560, 560);
        supply.RotationDegrees = 90;
        circuit.Connect(supply.Negative, ground.Pin);
        Tag(circuit, supply.Positive, "VCC", 270);
        Tag(circuit, ground.Pin, "GND", 180);

        var clock = Place(circuit, new OscillatorModule(1e6), -560, -170);
        Tag(circuit, clock.Vcc, "VCC", 270);
        Tag(circuit, clock.Gnd, "GND");
        Tag(circuit, clock.Output, "PHI0");

        var pullUp = Place(circuit, new Resistor(1e3), -560, -20);
        var reset = Place(circuit, new PushButton(), -560, 100);
        pullUp.RotationDegrees = 90;
        reset.RotationDegrees = 90;
        Tag(circuit, pullUp.A, "VCC", 270);
        circuit.Connect(pullUp.B, reset.A);
        Tag(circuit, pullUp.B, "RES", 360);
        Tag(circuit, reset.B, "GND", 90);

        var irqPullUp = Place(circuit, new Resistor(1e3), -560, 280);
        irqPullUp.RotationDegrees = 90;
        Tag(circuit, irqPullUp.A, "VCC", 270);
        Tag(circuit, irqPullUp.B, "IRQ", 360);

        var cpu = Place(circuit, new Cpu6510(), -200, 0);
        for (var i = 0; i < 16; i++) Tag(circuit, cpu.Address[i], $"A{i}");
        for (var i = 0; i < 8; i++) Tag(circuit, cpu.Data[i], $"D{i}");
        Tag(circuit, cpu.ReadWrite, "RW");
        Tag(circuit, cpu.Clock, "PHI0");
        Tag(circuit, cpu.Terminals.Single(t => t.Name == "PHI2"), "PHI2");
        Tag(circuit, cpu.Reset, "RES");
        Tag(circuit, cpu.Irq, "IRQ");
        foreach (var pin in new[] { cpu.Ready, cpu.Nmi, cpu.BusEnable! }) Tag(circuit, pin, "VCC");
        Tag(circuit, cpu.Vcc, "VCC");
        Tag(circuit, cpu.Gnd, "GND");

        var memory = Place(circuit, new Eeprom28C256(), 180, -60);
        memory.Load(MemoryImage.FromBinary(rom));
        var ram = Place(circuit, new Sram62256(), 520, -60);

        foreach (var chip in new ParallelMemory[] { memory, ram })
        {
            for (var i = 0; i < 15; i++) Tag(circuit, chip.Address[i], $"A{i}");
            for (var i = 0; i < 8; i++) Tag(circuit, chip.Data[i], $"D{i}");
            Tag(circuit, chip.Vcc, "VCC");
            Tag(circuit, chip.Gnd, "GND");
        }

        Tag(circuit, memory.ChipEnable, "ROM_CE");
        Tag(circuit, memory.OutputEnable, "GND");
        Tag(circuit, memory.WriteEnable, "VCC");
        Tag(circuit, ram.ChipEnable, "RAM_CE");
        Tag(circuit, ram.OutputEnable, "A14");
        Tag(circuit, ram.WriteEnable, "RW");

        var nand = Place(circuit, new Ic7400(), 180, 300);
        Tag(circuit, nand.Vcc, "VCC");
        Tag(circuit, nand.Gnd, "GND");

        var (a0, b0, y0) = nand.Gate(0);
        Tag(circuit, a0, "A15");
        Tag(circuit, b0, "A15");
        Tag(circuit, y0, "ROM_CE");

        var (a1, b1, y1) = nand.Gate(1);
        Tag(circuit, a1, "PHI2");
        Tag(circuit, b1, "ROM_CE");
        Tag(circuit, y1, "RAM_CE");

        var (a2, b2, y2) = nand.Gate(2);
        Tag(circuit, a2, "ROM_CE");
        Tag(circuit, b2, "A14");
        Tag(circuit, y2, "IO_SEL");

        var (a3, b3, _) = nand.Gate(3);
        Tag(circuit, a3, "GND");
        Tag(circuit, b3, "GND");

        // The 74139's first half splits $4000-$7FFF four ways by A12 and A13; the second is unused.
        var decoder = Place(circuit, new Ic74139(), 520, 300);
        Tag(circuit, decoder.Vcc, "VCC");
        Tag(circuit, decoder.Gnd, "GND");
        Tag(circuit, decoder.Enable[0], "IO_SEL");
        Tag(circuit, decoder.SelectA[0], "A12");
        Tag(circuit, decoder.SelectB[0], "A13");
        Tag(circuit, decoder.Outputs(0)[0], "CIA_CS");
        Tag(circuit, decoder.Outputs(0)[1], "SID_CS");
        Tag(circuit, decoder.Enable[1], "VCC");
        Tag(circuit, decoder.SelectA[1], "GND");
        Tag(circuit, decoder.SelectB[1], "GND");

        return new Computer6510(circuit, cpu, memory, ram, decoder);
    }

    /// <summary>A 6526 at $4000 on the 6510's bus: /CS from the decoder, RS0-RS3 on A0-A3.</summary>
    private static Cia6526 PlaceCia(Circuit circuit, double x, double y)
    {
        var cia = Place(circuit, new Cia6526(), x, y);
        for (var i = 0; i < 8; i++) Tag(circuit, cia.Data[i], $"D{i}");
        for (var i = 0; i < 4; i++) Tag(circuit, cia.RegisterSelect[i], $"A{i}");
        Tag(circuit, cia.ChipSelect, "CIA_CS");
        Tag(circuit, cia.Clock, "PHI2");
        Tag(circuit, cia.ReadWrite, "RW");
        Tag(circuit, cia.Reset, "RES");
        Tag(circuit, cia.Irq, "IRQ");
        Tag(circuit, cia.Vcc, "VCC");
        Tag(circuit, cia.Gnd, "GND");
        Tag(circuit, cia.TimeOfDayInput, "GND");
        return cia;
    }

    /// <summary>
    /// A 6510, a 6526 and a 6581 playing a tune: the CIA's timer A interrupts 200 times a second, the
    /// handler steps three voices through sixteen steps, and AUDIO OUT goes through a coupling
    /// capacitor to an amplifier and a speaker.
    /// </summary>
    public static void LoadSidPlayer(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "A 6510 SID player";

        var computer = Build6510(vm, RomImage(SidPlayerProgram, SidPlayerIrq));
        var cpu = computer.Cpu;

        // The 6510's own port: P0 goes high and low with the beat, on the scope. (An LED would need
        // the solver to iterate every time point, for a diode, and halve the speed.)
        Tag(circuit, cpu.Port[0], "BEAT");

        PlaceCia(circuit, 880, -10);

        var sid = Place(circuit, new Sid6581(), 1240, -60);
        for (var i = 0; i < 8; i++) Tag(circuit, sid.Data[i], $"D{i}");
        for (var i = 0; i < 5; i++) Tag(circuit, sid.Address[i], $"A{i}");
        Tag(circuit, sid.ChipSelect, "SID_CS");
        Tag(circuit, sid.Clock, "PHI2");
        Tag(circuit, sid.ReadWrite, "RW");
        Tag(circuit, sid.Reset, "RES");
        Tag(circuit, sid.Vcc, "VCC");
        Tag(circuit, sid.Vdd, "VDD");
        Tag(circuit, sid.Gnd, "GND");
        Tag(circuit, sid.Cap1A, "CAP1A");
        Tag(circuit, sid.Cap1B, "CAP1B");
        Tag(circuit, sid.Cap2A, "CAP2A");
        Tag(circuit, sid.Cap2B, "CAP2B");
        Tag(circuit, sid.AudioOut, "AUDIO");

        // The filter's two capacitors, the C64's 470 pF: they set where the cutoff register lands.
        var c1 = Place(circuit, new Capacitor(470e-12), 1240, -340);
        var c2 = Place(circuit, new Capacitor(470e-12), 1240, -260);
        Tag(circuit, c1.A, "CAP1A", 180);
        Tag(circuit, c1.B, "CAP1B");
        Tag(circuit, c2.A, "CAP2A", 180);
        Tag(circuit, c2.B, "CAP2B");

        // The SID's 12 V.
        var vdd = Place(circuit, new DcVoltageSource(12.0), 1240, 440);
        var vddGround = Place(circuit, new Ground(), 1240, 560);
        vdd.RotationDegrees = 90;
        circuit.Connect(vdd.Negative, vddGround.Pin);
        Tag(circuit, vdd.Positive, "VDD", 270);

        // AUDIO OUT: the C64's 1 kΩ to ground, a coupling capacitor charged to the SID's 6 V, and an
        // amplifier — a voltage-controlled source standing in for the TV's — driving the speaker.
        var load = Place(circuit, new Resistor(1e3), 1560, 40);
        load.RotationDegrees = 90;
        Tag(circuit, load.A, "AUDIO", 270);
        Tag(circuit, load.B, "GND", 90);

        var coupling = Place(circuit, new Capacitor(10e-6) { InitialVoltage = 6.0 }, 1700, -60);
        Tag(circuit, coupling.A, "AUDIO", 180);
        var input = Place(circuit, new Resistor(10e3), 1820, 40);
        input.RotationDegrees = 90;
        circuit.Connect(coupling.B, input.A);
        Tag(circuit, input.B, "GND", 90);

        var amplifier = Place(circuit, new VoltageControlledVoltageSource(2.0), 1960, -40);
        circuit.Connect(amplifier.ControlPositive, input.A);
        Tag(circuit, amplifier.ControlNegative, "GND", 180);
        Tag(circuit, amplifier.OutputNegative, "GND");

        var speaker = Place(circuit, new Speaker(8.0), 2140, 40);
        speaker.RotationDegrees = 90;
        circuit.Connect(amplifier.OutputPositive, speaker.A);
        Tag(circuit, speaker.B, "GND", 90);

        vm.Scope.TimebasePerDivision = 2e-3;
        vm.Scope.VoltsPerDivision = 0.5;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(load.A, "AUDIO OUT");
        vm.Scope.AddProbe(speaker.A, "Speaker");
        vm.Scope.AddProbe(cpu.Port[0], "BEAT (P0)");
    }

    /// <summary>
    /// A 6526 doing what the C64's first CIA does: scanning a keyboard matrix — here the 4x4 keypad,
    /// rows on port A and columns on port B — with each key shown on an HD44780 in four-bit mode on
    /// the ports' other lines, and every delay timed by timer B.
    /// </summary>
    public static void LoadCiaKeypad(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "A 6526 scanning a keypad";

        Build6510(vm, RomImage(CiaKeypadProgram));
        var cia = PlaceCia(circuit, 880, -10);

        // Rows on PA0-PA3 and columns on PB0-PB3: the CIA's own pull-ups hold the columns high.
        for (var i = 0; i < 4; i++)
        {
            Tag(circuit, cia.PortA[i], $"ROW{i + 1}");
            Tag(circuit, cia.PortB[i], $"COL{i + 1}");
        }

        Tag(circuit, cia.PortA[4], "LCD_RS");
        Tag(circuit, cia.PortA[5], "LCD_E");
        for (var i = 4; i < 8; i++) Tag(circuit, cia.PortB[i], $"PB{i}");

        var keypad = Place(circuit, new MatrixKeypad(), 1280, -60);
        for (var i = 0; i < 4; i++)
        {
            Tag(circuit, keypad.Rows[i], $"ROW{i + 1}", 90);
            Tag(circuit, keypad.Columns[i], $"COL{i + 1}", 90);
        }

        var lcd = Place(circuit, new CharacterLcd(), 880, 440);
        Tag(circuit, lcd.Gnd, "GND", 270);
        Tag(circuit, lcd.Vcc, "VCC", 270);
        Tag(circuit, lcd.RegisterSelect, "LCD_RS", 270);
        Tag(circuit, lcd.ReadWrite, "GND", 270);
        Tag(circuit, lcd.Enable, "LCD_E", 270);
        for (var i = 4; i < 8; i++) Tag(circuit, lcd.Data[i], $"PB{i}", 270);

        var contrast = Place(circuit, new Potentiometer(10e3, 0.3), 560, 500);
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

        // The scan: each row taken low in turn, a millisecond apart, and E pulsing for the display.
        vm.Scope.TimebasePerDivision = 0.5e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(keypad.Rows[0], "ROW1");
        vm.Scope.AddProbe(keypad.Rows[1], "ROW2");
        vm.Scope.AddProbe(lcd.Enable, "LCD E");
    }
}
