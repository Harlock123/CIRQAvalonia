using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Processors;
using Cirq.Components.Sources;
using Cirq.Core.Topology;

namespace Cirq.UI.ViewModels;

/// <summary>A 68000 single-board computer: a ROM pair, a RAM pair, a DTACK generator and its peripherals.</summary>
public static partial class Examples
{
    /// <summary>
    /// The 68000 monitor, assembled from the source in the guide (a test checks these are its
    /// bytes): a banner over the 68681's channel A at 38,400 baud, a prompt, and a D command that
    /// dumps eight bytes in hex. Runs of bytes and where each starts.
    /// </summary>
    internal static readonly (uint Address, byte[] Bytes)[] MonitorProgram =
    [
        (0x000000, [
            0x00, 0x21, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00,
        ]),
        (0x000400, [
            0x13, 0xFC, 0x00, 0x10, 0x00, 0xC0, 0x00, 0x05, 0x13, 0xFC, 0x00, 0x13, 0x00, 0xC0, 0x00, 0x01,
            0x13, 0xFC, 0x00, 0x07, 0x00, 0xC0, 0x00, 0x01, 0x13, 0xFC, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x09,
            0x13, 0xFC, 0x00, 0xCC, 0x00, 0xC0, 0x00, 0x03, 0x13, 0xFC, 0x00, 0x05, 0x00, 0xC0, 0x00, 0x05,
            0x41, 0xFA, 0x01, 0x00, 0x61, 0x00, 0x00, 0xEA, 0x41, 0xFA, 0x01, 0x0D, 0x61, 0x00, 0x00, 0xE2,
            0x43, 0xF9, 0x00, 0x20, 0x00, 0x00, 0x08, 0x39, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x03, 0x67, 0xF6,
            0x10, 0x39, 0x00, 0xC0, 0x00, 0x07, 0x61, 0x00, 0x00, 0xB6, 0x0C, 0x00, 0x00, 0x0D, 0x67, 0x04,
            0x12, 0xC0, 0x60, 0xE2, 0x42, 0x11, 0x70, 0x0A, 0x61, 0x00, 0x00, 0xA4, 0x45, 0xF9, 0x00, 0x20,
            0x00, 0x00, 0x0C, 0x1A, 0x00, 0x44, 0x66, 0x30, 0x61, 0x00, 0x00, 0x38, 0x26, 0x41, 0x24, 0x01,
            0x76, 0x05, 0x61, 0x00, 0x00, 0x60, 0x70, 0x3A, 0x61, 0x00, 0x00, 0x84, 0x78, 0x07, 0x70, 0x20,
            0x61, 0x00, 0x00, 0x7C, 0x74, 0x00, 0x14, 0x1B, 0x76, 0x01, 0x61, 0x00, 0x00, 0x48, 0x51, 0xCC,
            0xFF, 0xEE, 0x61, 0x00, 0x00, 0x86, 0x60, 0x90, 0x41, 0xFA, 0x00, 0xA0, 0x61, 0x00, 0x00, 0x72,
            0x60, 0x86, 0x72, 0x00, 0x10, 0x1A, 0x0C, 0x00, 0x00, 0x20, 0x67, 0xF8, 0x0C, 0x00, 0x00, 0x30,
            0x6D, 0x20, 0x0C, 0x00, 0x00, 0x39, 0x6F, 0x0E, 0x0C, 0x00, 0x00, 0x41, 0x6D, 0x14, 0x0C, 0x00,
            0x00, 0x46, 0x6E, 0x0E, 0x5F, 0x00, 0x04, 0x00, 0x00, 0x30, 0xE9, 0x89, 0x82, 0x00, 0x10, 0x1A,
            0x60, 0xDA, 0x4E, 0x75, 0x3C, 0x03, 0x52, 0x46, 0xE5, 0x4E, 0x44, 0x46, 0x06, 0x46, 0x00, 0x20,
            0xED, 0xAA, 0xE9, 0x9A, 0x10, 0x02, 0x02, 0x00, 0x00, 0x0F, 0x0C, 0x00, 0x00, 0x0A, 0x6D, 0x02,
            0x5E, 0x00, 0x06, 0x00, 0x00, 0x30, 0x61, 0x06, 0x51, 0xCB, 0xFF, 0xE8, 0x4E, 0x75, 0x08, 0x39,
            0x00, 0x02, 0x00, 0xC0, 0x00, 0x03, 0x67, 0xF6, 0x13, 0xC0, 0x00, 0xC0, 0x00, 0x07, 0x4E, 0x75,
            0x10, 0x18, 0x67, 0x04, 0x61, 0xE8, 0x60, 0xF8, 0x4E, 0x75, 0x70, 0x0D, 0x61, 0xE0, 0x70, 0x0A,
            0x60, 0xDC, 0x43, 0x49, 0x52, 0x51, 0x20, 0x36, 0x38, 0x30, 0x30, 0x30, 0x20, 0x6D, 0x6F, 0x6E,
            0x69, 0x74, 0x6F, 0x72, 0x0D, 0x0A, 0x00, 0x3E, 0x20, 0x00, 0x3F, 0x0D, 0x0A, 0x00,
        ]),
    ];

    /// <summary>
    /// The MFP example's program, assembled from the guide's source: timer A interrupting 1,600 times
    /// a second through vector $4D, the count on GPIP0-3 and printed every eighth tick over the
    /// USART at 19,200 baud from timer D, and a button on GPIP7 setting it back to zero.
    /// </summary>
    internal static readonly (uint Address, byte[] Bytes)[] TicksProgram =
    [
        (0x000000, [
            0x00, 0x21, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00,
        ]),
        (0x000134, [
            0x00, 0x00, 0x04, 0x9C,
        ]),
        (0x00013C, [
            0x00, 0x00, 0x04, 0xAE,
        ]),
        (0x000400, [
            0x42, 0x79, 0x00, 0x20, 0x00, 0x00, 0x42, 0x79, 0x00, 0x20, 0x00, 0x02, 0x13, 0xFC, 0x00, 0x0F,
            0x00, 0xC0, 0x00, 0x05, 0x13, 0xFC, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x03, 0x13, 0xFC, 0x00, 0x01,
            0x00, 0xC0, 0x00, 0x25, 0x13, 0xFC, 0x00, 0x01, 0x00, 0xC0, 0x00, 0x1D, 0x13, 0xFC, 0x00, 0x88,
            0x00, 0xC0, 0x00, 0x29, 0x13, 0xFC, 0x00, 0x05, 0x00, 0xC0, 0x00, 0x2D, 0x13, 0xFC, 0x00, 0x60,
            0x00, 0xC0, 0x00, 0x1F, 0x13, 0xFC, 0x00, 0x40, 0x00, 0xC0, 0x00, 0x17, 0x13, 0xFC, 0x00, 0xA0,
            0x00, 0xC0, 0x00, 0x07, 0x13, 0xFC, 0x00, 0xA0, 0x00, 0xC0, 0x00, 0x13, 0x13, 0xFC, 0x00, 0x03,
            0x00, 0xC0, 0x00, 0x19, 0x41, 0xFA, 0x00, 0x9C, 0x61, 0x00, 0x00, 0x8E, 0x46, 0xFC, 0x20, 0x00,
            0x34, 0x39, 0x00, 0x20, 0x00, 0x00, 0x32, 0x02, 0xE6, 0x49, 0xB2, 0x79, 0x00, 0x20, 0x00, 0x02,
            0x67, 0xEE, 0x33, 0xC1, 0x00, 0x20, 0x00, 0x02, 0x76, 0x03, 0x61, 0x00, 0x00, 0x30, 0x70, 0x0D,
            0x61, 0x00, 0x00, 0x54, 0x70, 0x0A, 0x61, 0x00, 0x00, 0x4E, 0x60, 0xD4, 0x52, 0x79, 0x00, 0x20,
            0x00, 0x00, 0x13, 0xF9, 0x00, 0x20, 0x00, 0x01, 0x00, 0xC0, 0x00, 0x01, 0x4E, 0x73, 0x42, 0x79,
            0x00, 0x20, 0x00, 0x00, 0x42, 0x79, 0x00, 0x20, 0x00, 0x02, 0x4E, 0x73, 0x3C, 0x03, 0x52, 0x46,
            0xE5, 0x4E, 0x44, 0x46, 0x06, 0x46, 0x00, 0x20, 0xED, 0xAA, 0xE9, 0x9A, 0x10, 0x02, 0x02, 0x00,
            0x00, 0x0F, 0x0C, 0x00, 0x00, 0x0A, 0x6D, 0x02, 0x5E, 0x00, 0x06, 0x00, 0x00, 0x30, 0x61, 0x06,
            0x51, 0xCB, 0xFF, 0xE8, 0x4E, 0x75, 0x08, 0x39, 0x00, 0x07, 0x00, 0xC0, 0x00, 0x2D, 0x67, 0xF6,
            0x13, 0xC0, 0x00, 0xC0, 0x00, 0x2F, 0x4E, 0x75, 0x10, 0x18, 0x67, 0x04, 0x61, 0xE8, 0x60, 0xF8,
            0x4E, 0x75, 0x4D, 0x46, 0x50, 0x20, 0x74, 0x69, 0x63, 0x6B, 0x73, 0x0D, 0x0A, 0x00,
        ]),
    ];

    /// <summary>A program as a 64K image for the ROM pair at $000000, unused bytes zero.</summary>
    internal static byte[] Image68000((uint Address, byte[] Bytes)[] program)
    {
        var image = new byte[0x10000];
        foreach (var (address, bytes) in program) bytes.CopyTo(image, address);
        return image;
    }

    /// <summary>The parts every 68000 example shares.</summary>
    private sealed record Computer68000(Circuit Circuit, Cpu68000 Cpu, Eeprom28C256 RomEven, Eeprom28C256 RomOdd,
        Sram62256 RamEven, Sram62256 RamOdd, Ic74138 Decoder, Ic7432 Selects, Ic7408 And, Ic74164 DtackCounter);

    /// <summary>
    /// An MC68000 at 1 MHz with 64K of ROM at $000000 and 64K of RAM at $200000, each a pair of
    /// byte-wide chips — the even bytes on D8-D15 selected by /UDS, the odd on D0-D7 by /LDS. A
    /// 74138 under /AS splits the address space into 2 MB regions by A21-A23: ROM_SEL ($000000),
    /// RAM_SEL ($200000), IO_SEL ($C00000), and IACK_SEL ($E00000-$FFFFFF), which only the interrupt
    /// acknowledge's address reaches here. A 7432 ORs each region with its byte's strobe into a chip
    /// enable. The DTACK generator is a 74164: while memory is selected (a 7408 ANDs the two
    /// selects) it shifts ones in on CLK, and QB, inverted, is /DTACK — so every memory cycle has one
    /// wait state. Peripherals pull IO_DTACK (pulled up) low, ANDed in by the 7408.
    /// </summary>
    private static Computer68000 Build68000(MainWindowViewModel vm, byte[] image)
    {
        var circuit = vm.Circuit;

        var supply = Place(circuit, new DcVoltageSource(5.0), -560, 520);
        var ground = Place(circuit, new Ground(), -560, 640);
        supply.RotationDegrees = 90;
        circuit.Connect(supply.Negative, ground.Pin);
        Tag(circuit, supply.Positive, "VCC", 270);
        Tag(circuit, ground.Pin, "GND", 180);

        var clock = Place(circuit, new OscillatorModule(1e6), -560, -250);
        Tag(circuit, clock.Vcc, "VCC", 270);
        Tag(circuit, clock.Gnd, "GND");
        Tag(circuit, clock.Output, "CLK");

        // /RESET and /HALT together, pulled up, and a button to pull them down.
        var pullUp = Place(circuit, new Resistor(4.7e3), -560, -100);
        var reset = Place(circuit, new PushButton(), -560, 20);
        pullUp.RotationDegrees = 90;
        reset.RotationDegrees = 90;
        Tag(circuit, pullUp.A, "VCC", 270);
        circuit.Connect(pullUp.B, reset.A);
        Tag(circuit, pullUp.B, "RESET", 360);
        Tag(circuit, reset.B, "GND", 90);

        // The peripherals' open-drain /DTACK and interrupt request, pulled up.
        var dtackPullUp = Place(circuit, new Resistor(4.7e3), -560, 200);
        dtackPullUp.RotationDegrees = 90;
        Tag(circuit, dtackPullUp.A, "VCC", 270);
        Tag(circuit, dtackPullUp.B, "IO_DTACK", 360);
        var irqPullUp = Place(circuit, new Resistor(4.7e3), -560, 360);
        irqPullUp.RotationDegrees = 90;
        Tag(circuit, irqPullUp.A, "VCC", 270);
        Tag(circuit, irqPullUp.B, "IRQ", 360);

        var cpu = Place(circuit, new Cpu68000(), -200, 0);
        for (var i = 0; i < 23; i++) Tag(circuit, cpu.Address[i], $"A{i + 1}");
        for (var i = 0; i < 16; i++) Tag(circuit, cpu.Data[i], $"D{i}");
        Tag(circuit, cpu.AddressStrobe, "AS");
        Tag(circuit, cpu.UpperDataStrobe, "UDS");
        Tag(circuit, cpu.LowerDataStrobe, "LDS");
        Tag(circuit, cpu.ReadWrite, "RW");
        Tag(circuit, cpu.DataAcknowledge, "DTACK");
        Tag(circuit, cpu.Clock, "CLK");
        Tag(circuit, cpu.Reset, "RESET");
        Tag(circuit, cpu.Halt, "RESET");
        foreach (var pin in new[] { cpu.BusError, cpu.ValidPeripheralAddress, cpu.BusRequest, cpu.BusGrantAcknowledge, cpu.InterruptLevel[0] })
            Tag(circuit, pin, "VCC");
        Tag(circuit, cpu.InterruptLevel[1], "IRQ");
        Tag(circuit, cpu.InterruptLevel[2], "IRQ");
        for (var i = 0; i < 3; i++) Tag(circuit, cpu.FunctionCode[i], $"FC{i}");
        Tag(circuit, cpu.Vcc, "VCC");
        Tag(circuit, cpu.Gnd, "GND");
        Tag(circuit, cpu.Terminals.Single(t => t.Name == "VCC2"), "VCC");
        Tag(circuit, cpu.Terminals.Single(t => t.Name == "GND2"), "GND");

        var romEven = Place(circuit, new Eeprom28C256(), 180, -250);
        var romOdd = Place(circuit, new Eeprom28C256(), 180, 130);
        var ramEven = Place(circuit, new Sram62256(), 520, -250);
        var ramOdd = Place(circuit, new Sram62256(), 520, 130);
        romEven.Load(MemoryImage.FromBinary(image).Lane(ByteLane.Even));
        romOdd.Load(MemoryImage.FromBinary(image).Lane(ByteLane.Odd));

        foreach (var (memory, high) in new (ParallelMemory, bool)[] { (romEven, true), (romOdd, false), (ramEven, true), (ramOdd, false) })
        {
            for (var i = 0; i < 15; i++) Tag(circuit, memory.Address[i], $"A{i + 1}");
            for (var i = 0; i < 8; i++) Tag(circuit, memory.Data[i], $"D{(high ? 8 : 0) + i}");
            Tag(circuit, memory.OutputEnable, "GND");
            Tag(circuit, memory.Vcc, "VCC");
            Tag(circuit, memory.Gnd, "GND");
        }

        Tag(circuit, romEven.WriteEnable, "VCC");
        Tag(circuit, romOdd.WriteEnable, "VCC");
        Tag(circuit, ramEven.WriteEnable, "RW");
        Tag(circuit, ramOdd.WriteEnable, "RW");
        Tag(circuit, romEven.ChipEnable, "ROM_E_CE");
        Tag(circuit, romOdd.ChipEnable, "ROM_O_CE");
        Tag(circuit, ramEven.ChipEnable, "RAM_E_CE");
        Tag(circuit, ramOdd.ChipEnable, "RAM_O_CE");

        // The 74138: A21-A23 pick a region while /AS is low.
        var decoder = Place(circuit, new Ic74138(), 180, 560);
        Tag(circuit, decoder.Vcc, "VCC");
        Tag(circuit, decoder.Gnd, "GND");
        Tag(circuit, decoder.SelectA, "A21");
        Tag(circuit, decoder.SelectB, "A22");
        Tag(circuit, decoder.SelectC, "A23");
        Tag(circuit, decoder.Enable1, "AS");
        Tag(circuit, decoder.Enable2, "GND");
        Tag(circuit, decoder.Enable3, "VCC");
        Tag(circuit, decoder.Outputs[0], "ROM_SEL");
        Tag(circuit, decoder.Outputs[1], "RAM_SEL");
        Tag(circuit, decoder.Outputs[6], "IO_SEL");
        Tag(circuit, decoder.Outputs[7], "IACK_SEL");

        // A chip enable is its region's select ORed with its byte's strobe, all active low.
        var selects = Place(circuit, new Ic7432(), 520, 560);
        Tag(circuit, selects.Vcc, "VCC");
        Tag(circuit, selects.Gnd, "GND");
        foreach (var (gate, region, strobe, enable) in new[]
                 {
                     (0, "ROM_SEL", "UDS", "ROM_E_CE"), (1, "ROM_SEL", "LDS", "ROM_O_CE"),
                     (2, "RAM_SEL", "UDS", "RAM_E_CE"), (3, "RAM_SEL", "LDS", "RAM_O_CE"),
                 })
        {
            var (a, b, y) = selects.Gate(gate);
            Tag(circuit, a, region);
            Tag(circuit, b, strobe);
            Tag(circuit, y, enable);
        }

        // The DTACK generator: memory selected lets the 74164 count clocks; QB, inverted, answers.
        var and = Place(circuit, new Ic7408(), 860, 560);
        Tag(circuit, and.Vcc, "VCC");
        Tag(circuit, and.Gnd, "GND");
        var (m1, m2, my) = and.Gate(0);
        Tag(circuit, m1, "ROM_SEL");
        Tag(circuit, m2, "RAM_SEL");
        Tag(circuit, my, "MEM_SEL");
        var (d1, d2, dy) = and.Gate(1);
        Tag(circuit, d1, "MEM_DTACK");
        Tag(circuit, d2, "IO_DTACK");
        Tag(circuit, dy, "DTACK");
        for (var g = 2; g < 4; g++)
        {
            var (a, b, _) = and.Gate(g);
            Tag(circuit, a, "GND");
            Tag(circuit, b, "GND");
        }

        var not = Place(circuit, new Ic7404(), 1200, 560);
        Tag(circuit, not.Vcc, "VCC");
        Tag(circuit, not.Gnd, "GND");
        var (n0, n0y) = not.Inverter(0);
        Tag(circuit, n0, "MEM_SEL");
        Tag(circuit, n0y, "COUNT");
        var (n1, n1y) = not.Inverter(1);
        Tag(circuit, n1, "WAIT1");
        Tag(circuit, n1y, "MEM_DTACK");
        for (var i = 2; i < 6; i++) Tag(circuit, not.Inverter(i).A, "GND");

        var counter = Place(circuit, new Ic74164(), 1540, 560);
        Tag(circuit, counter.Vcc, "VCC");
        Tag(circuit, counter.Gnd, "GND");
        Tag(circuit, counter.SerialA, "VCC");
        Tag(circuit, counter.SerialB, "VCC");
        Tag(circuit, counter.Clock, "CLK");
        Tag(circuit, counter.Clear, "COUNT");
        Tag(circuit, counter.Outputs[1], "WAIT1");

        return new Computer68000(circuit, cpu, romEven, romOdd, ramEven, ramOdd, decoder, selects, and, counter);
    }

    /// <summary>
    /// The 68000 computer with a 68681 DUART at $C00001 (on the odd bytes, /CS = IO_SEL ORed with
    /// /LDS) talking to the serial terminal at 38,400 baud: a monitor that prints a banner and dumps
    /// memory on request — the terminal asks for the ROM at $000400.
    /// </summary>
    public static void Load68000Monitor(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "A 68000 single-board computer";

        Build68000(vm, Image68000(MonitorProgram));

        // A second 7432 for the DUART's select: IO_SEL ORed with /LDS.
        var or = Place(circuit, new Ic7432(), 1540, 220);
        Tag(circuit, or.Vcc, "VCC");
        Tag(circuit, or.Gnd, "GND");
        var (oa, ob, oy) = or.Gate(0);
        Tag(circuit, oa, "IO_SEL");
        Tag(circuit, ob, "LDS");
        Tag(circuit, oy, "DUART_CS");
        for (var g = 1; g < 4; g++)
        {
            var (a, b, _) = or.Gate(g);
            Tag(circuit, a, "GND");
            Tag(circuit, b, "GND");
        }

        var duart = Place(circuit, new Duart68681(), 880, -170);
        for (var i = 0; i < 8; i++) Tag(circuit, duart.Data[i], $"D{i}");
        for (var i = 0; i < 4; i++) Tag(circuit, duart.RegisterSelect[i], $"A{i + 1}");
        Tag(circuit, duart.ChipSelect, "DUART_CS");
        Tag(circuit, duart.ReadWrite, "RW");
        Tag(circuit, duart.Dtack, "IO_DTACK");
        Tag(circuit, duart.Reset, "RESET");
        Tag(circuit, duart.Interrupt, "IRQ");
        Tag(circuit, duart.InterruptAcknowledge, "VCC");
        Tag(circuit, duart.Vcc, "VCC");
        Tag(circuit, duart.Gnd, "GND");
        Tag(circuit, duart.ReceiveB, "VCC");
        foreach (var ip in duart.InputPort) Tag(circuit, ip, "VCC");

        var terminal = Place(circuit, new SerialTerminal
        {
            BaudRate = 38400,
            Message = "D 000400\r",
            StartDelay = 8e-3,
        }, 1260, -170);
        Tag(circuit, duart.TransmitA, "TXD");
        Tag(circuit, terminal.Receive, "TXD");
        Tag(circuit, duart.ReceiveA, "RXD");
        Tag(circuit, terminal.Transmit, "RXD");
        Tag(circuit, terminal.Vcc, "VCC");
        Tag(circuit, terminal.Gnd, "GND");

        vm.Scope.TimebasePerDivision = 2e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(duart.TransmitA, "TxDA");
        vm.Scope.AddProbe(duart.ReceiveA, "RxDA");
    }

    /// <summary>
    /// The 68000 computer with a 68901 MFP at $C00001 (/CS = IO_SEL, /DS = /LDS): timer A's
    /// interrupts, vectored through the MFP at level 6, counting on GPIP0-3; the count printed
    /// over the MFP's USART, clocked by timer D, to the serial terminal; a button on GPIP7.
    /// </summary>
    public static void Load68000Ticks(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "68000 timer interrupts on a 68901";

        Build68000(vm, Image68000(TicksProgram));

        var mfp = Place(circuit, new Mfp68901(), 880, -170);
        for (var i = 0; i < 8; i++) Tag(circuit, mfp.Data[i], $"D{i}");
        for (var i = 0; i < 5; i++) Tag(circuit, mfp.RegisterSelect[i], $"A{i + 1}");
        Tag(circuit, mfp.ChipSelect, "IO_SEL");
        Tag(circuit, mfp.DataStrobe, "LDS");
        Tag(circuit, mfp.ReadWrite, "RW");
        Tag(circuit, mfp.Dtack, "IO_DTACK");
        Tag(circuit, mfp.Reset, "RESET");
        Tag(circuit, mfp.Irq, "IRQ");
        Tag(circuit, mfp.InterruptAcknowledge, "IACK_SEL");
        Tag(circuit, mfp.InterruptEnableIn, "GND");
        Tag(circuit, mfp.Clock, "CLK");
        Tag(circuit, mfp.Vcc, "VCC");
        Tag(circuit, mfp.Gnd, "GND");
        Tag(circuit, mfp.TimerAInput, "VCC");
        Tag(circuit, mfp.TimerBInput, "VCC");
        for (var i = 0; i < 4; i++) Tag(circuit, mfp.Gpip[i], $"COUNT{i}");
        for (var i = 4; i < 7; i++) Tag(circuit, mfp.Gpip[i], "VCC");
        Tag(circuit, mfp.Gpip[7], "BUTTON");
        Tag(circuit, mfp.TimerOutputs[3], "BAUD");
        Tag(circuit, mfp.TransmitClock, "BAUD");
        Tag(circuit, mfp.ReceiveClock, "BAUD");

        var buttonPullUp = Place(circuit, new Resistor(4.7e3), 1260, 230);
        var button = Place(circuit, new PushButton(), 1260, 350);
        buttonPullUp.RotationDegrees = 90;
        button.RotationDegrees = 90;
        Tag(circuit, buttonPullUp.A, "VCC", 270);
        circuit.Connect(buttonPullUp.B, button.A);
        Tag(circuit, buttonPullUp.B, "BUTTON", 360);
        Tag(circuit, button.B, "GND", 90);

        // SO floats until the program has the transmitter set up: a pull-up keeps the line idle.
        var txPullUp = Place(circuit, new Resistor(4.7e3), 1540, 230);
        txPullUp.RotationDegrees = 90;
        Tag(circuit, txPullUp.A, "VCC", 270);
        Tag(circuit, txPullUp.B, "TXD", 90);

        var terminal = Place(circuit, new SerialTerminal
        {
            BaudRate = 19200,
            Message = "",
            StartDelay = 1,
        }, 1260, -170);
        Tag(circuit, mfp.SerialOut, "TXD");
        Tag(circuit, terminal.Receive, "TXD");
        Tag(circuit, mfp.SerialIn, "RXD");
        Tag(circuit, terminal.Transmit, "RXD");
        Tag(circuit, terminal.Vcc, "VCC");
        Tag(circuit, terminal.Gnd, "GND");

        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(mfp.TimerOutputs[0], "TAO");
        vm.Scope.AddProbe(mfp.Gpip[0], "COUNT0");
        vm.Scope.AddProbe(mfp.Gpip[1], "COUNT1");
        vm.Scope.AddProbe(mfp.SerialOut, "TxD");
    }
}
