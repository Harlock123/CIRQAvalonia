using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Processors;
using Cirq.Components.Sources;
using Cirq.Components.Nonlinear;
using Cirq.Core.Topology;

namespace Cirq.UI.ViewModels;

/// <summary>A Z80 single-board computer: ROM, RAM, two 74138s decoding memory and I/O, and the Z80 family's peripherals.</summary>
public static partial class Examples
{
    /// <summary>
    /// The Z80 monitor, assembled from the source in the guide (a test checks these are its bytes):
    /// a banner over the SIO at 115,200 baud, a prompt, and a D command that dumps eight bytes in
    /// hex. Runs of bytes and where each starts.
    /// </summary>
    internal static readonly (int Address, byte[] Bytes)[] MonitorZ80Program =
    [
        (0x0000, [
            0x31, 0x00, 0x00, 0x21, 0xDF, 0x00, 0x06, 0x07, 0x0E, 0x82, 0xED, 0xB3, 0x21, 0xE6, 0x00, 0xCD,
            0x7B, 0x00, 0x21, 0xF9, 0x00, 0xCD, 0x7B, 0x00, 0x21, 0x00, 0x80, 0xCD, 0x72, 0x00, 0xFE, 0x0D,
            0x28, 0x07, 0xCD, 0x67, 0x00, 0x77, 0x23, 0x18, 0xF2, 0x36, 0x00, 0xCD, 0x84, 0x00, 0x21, 0x00,
            0x80, 0x7E, 0xE6, 0xDF, 0xFE, 0x44, 0x20, 0x27, 0x23, 0xCD, 0xA2, 0x00, 0x38, 0x21, 0xEB, 0x7C,
            0xCD, 0x8D, 0x00, 0x7D, 0xCD, 0x8D, 0x00, 0x3E, 0x3A, 0xCD, 0x67, 0x00, 0x06, 0x08, 0x3E, 0x20,
            0xCD, 0x67, 0x00, 0x7E, 0xCD, 0x8D, 0x00, 0x23, 0x10, 0xF4, 0xCD, 0x84, 0x00, 0x18, 0xB3, 0x21,
            0xFC, 0x00, 0xCD, 0x7B, 0x00, 0x18, 0xAB, 0xF5, 0xDB, 0x82, 0xCB, 0x57, 0x28, 0xFA, 0xF1, 0xD3,
            0x80, 0xC9, 0xDB, 0x82, 0xCB, 0x47, 0x28, 0xFA, 0xDB, 0x80, 0xC9, 0x7E, 0xB7, 0xC8, 0xCD, 0x67,
            0x00, 0x23, 0x18, 0xF7, 0x3E, 0x0D, 0xCD, 0x67, 0x00, 0x3E, 0x0A, 0x18, 0xDA, 0xF5, 0x0F, 0x0F,
            0x0F, 0x0F, 0xCD, 0x96, 0x00, 0xF1, 0xE6, 0x0F, 0xC6, 0x30, 0xFE, 0x3A, 0x38, 0xC9, 0xC6, 0x07,
            0x18, 0xC5, 0x11, 0x00, 0x00, 0x06, 0x00, 0x7E, 0xFE, 0x20, 0x20, 0x03, 0x23, 0x18, 0xF8, 0x7E,
            0xCD, 0xC5, 0x00, 0x38, 0x0C, 0xEB, 0x29, 0x29, 0x29, 0x29, 0xB5, 0x6F, 0xEB, 0x23, 0x04, 0x18,
            0xEE, 0x78, 0xD6, 0x01, 0xC9, 0xFE, 0x30, 0x38, 0x14, 0xFE, 0x3A, 0x38, 0x0D, 0xE6, 0xDF, 0xFE,
            0x41, 0x38, 0x0A, 0xFE, 0x47, 0x30, 0x06, 0xD6, 0x37, 0xC9, 0xD6, 0x30, 0xC9, 0x37, 0xC9, 0x18,
            0x04, 0x84, 0x03, 0xC1, 0x05, 0xEA, 0x43, 0x49, 0x52, 0x51, 0x20, 0x5A, 0x38, 0x30, 0x20, 0x6D,
            0x6F, 0x6E, 0x69, 0x74, 0x6F, 0x72, 0x0D, 0x0A, 0x00, 0x3E, 0x20, 0x00, 0x3F, 0x0D, 0x0A, 0x00,
        ]),
    ];

    /// <summary>
    /// The CTC and PIO example's program, assembled from the guide's source: the CTC's channel 1
    /// interrupting a thousand times a second to move a count on port A's LEDs, and a button on
    /// PB0 turning it round through the PIO.
    /// </summary>
    internal static readonly (int Address, byte[] Bytes)[] CounterZ80Program =
    [
        (0x0000, [
            0xC3, 0x1A, 0x00,
        ]),
        (0x0010, [
            0x5C, 0x00, 0x5C, 0x00, 0x5C, 0x00, 0x5C, 0x00, 0x6F, 0x00, 0x31, 0x00, 0x00, 0xAF, 0x32, 0x00,
            0x80, 0xED, 0x47, 0x3C, 0x32, 0x01, 0x80, 0xED, 0x5E, 0x3E, 0x0F, 0xD3, 0xC2, 0xAF, 0xD3, 0xC0,
            0x3E, 0x18, 0xD3, 0xC3, 0x3E, 0xCF, 0xD3, 0xC3, 0x3E, 0xFF, 0xD3, 0xC3, 0x3E, 0x97, 0xD3, 0xC3,
            0x3E, 0xFE, 0xD3, 0xC3, 0x3E, 0x10, 0xD3, 0xA0, 0x3E, 0x07, 0xD3, 0xA0, 0x3E, 0x17, 0xD3, 0xA0,
            0x3E, 0xD7, 0xD3, 0xA1, 0x3E, 0x0A, 0xD3, 0xA1, 0xFB, 0x76, 0x18, 0xFD, 0xF5, 0xE5, 0x21, 0x01,
            0x80, 0x3A, 0x00, 0x80, 0x86, 0x32, 0x00, 0x80, 0xD3, 0xC0, 0xE1, 0xF1, 0xFB, 0xED, 0x4D, 0xF5,
            0x3A, 0x01, 0x80, 0xED, 0x44, 0x32, 0x01, 0x80, 0xF1, 0xFB, 0xED, 0x4D,
        ]),
    ];

    /// <summary>A program as the 32K image of the 27C256 at $0000, unused bytes zero.</summary>
    internal static byte[] ImageZ80((int Address, byte[] Bytes)[] program)
    {
        var image = new byte[0x8000];
        foreach (var (address, bytes) in program) bytes.CopyTo(image, address);
        return image;
    }

    /// <summary>The parts every Z80 example shares.</summary>
    private sealed record ComputerZ80(Circuit Circuit, CpuZ80 Cpu, Eprom27C256 Rom, Sram62256 Ram, Ic74138 MemoryDecoder, Ic74138 IoDecoder);

    /// <summary>
    /// A Z80 at 3.6864 MHz from an oscillator module, a 27C256 at $0000-$7FFF and a 62256 at
    /// $8000-$FFFF, in the style of the RC2014 Mini and Grant Searle's simple Z80. One 74138 decodes
    /// memory: A15 on its A input and /MREQ on /E1, so Y0 is ROM_SEL and Y1 RAM_SEL, and a refresh or
    /// an I/O cycle selects neither; /RD is both memories' /OE and /WR the RAM's /WE. The other decodes
    /// I/O: A5-A7 on its inputs, /IORQ on /E1 and /M1 on E3, so an interrupt acknowledge selects no
    /// port; Y4 is SIO_SEL ($80), Y5 CTC_SEL ($A0), Y6 PIO_SEL ($C0). /INT is pulled up for the
    /// peripherals' open-drain requests; /RESET has a button; /NMI, /WAIT and /BUSREQ are tied high.
    /// 3.6864 MHz ÷ 32 is 115,200 baud for the SIO.
    /// </summary>
    private static ComputerZ80 BuildZ80(MainWindowViewModel vm, byte[] image)
    {
        var circuit = vm.Circuit;

        var supply = Place(circuit, new DcVoltageSource(5.0), -560, 440);
        var ground = Place(circuit, new Ground(), -560, 560);
        supply.RotationDegrees = 90;
        circuit.Connect(supply.Negative, ground.Pin);
        Tag(circuit, supply.Positive, "VCC", 270);
        Tag(circuit, ground.Pin, "GND", 180);

        var clock = Place(circuit, new OscillatorModule(3.6864e6), -560, -250);
        Tag(circuit, clock.Vcc, "VCC", 270);
        Tag(circuit, clock.Gnd, "GND");
        Tag(circuit, clock.Output, "CLK");

        // /RESET pulled up, and a button to pull it down.
        var pullUp = Place(circuit, new Resistor(4.7e3), -560, -100);
        var reset = Place(circuit, new PushButton(), -560, 20);
        pullUp.RotationDegrees = 90;
        reset.RotationDegrees = 90;
        Tag(circuit, pullUp.A, "VCC", 270);
        circuit.Connect(pullUp.B, reset.A);
        Tag(circuit, pullUp.B, "RESET", 360);
        Tag(circuit, reset.B, "GND", 90);

        // /INT pulled up, so the peripherals' open-drain outputs only pull it down.
        var intPullUp = Place(circuit, new Resistor(4.7e3), -560, 200);
        intPullUp.RotationDegrees = 90;
        Tag(circuit, intPullUp.A, "VCC", 270);
        Tag(circuit, intPullUp.B, "INT", 360);

        var cpu = Place(circuit, new CpuZ80(), -200, 0);
        for (var i = 0; i < 16; i++) Tag(circuit, cpu.Address[i], $"A{i}");
        for (var i = 0; i < 8; i++) Tag(circuit, cpu.Data[i], $"D{i}");
        Tag(circuit, cpu.Clock, "CLK");
        Tag(circuit, cpu.Reset, "RESET");
        Tag(circuit, cpu.Int, "INT");
        foreach (var pin in new[] { cpu.Nmi, cpu.Wait, cpu.BusRequest }) Tag(circuit, pin, "VCC");
        Tag(circuit, cpu.M1, "M1");
        Tag(circuit, cpu.MemoryRequest, "MREQ");
        Tag(circuit, cpu.IoRequest, "IORQ");
        Tag(circuit, cpu.ReadStrobe, "RD");
        Tag(circuit, cpu.WriteStrobe, "WR");
        Tag(circuit, cpu.RefreshStrobe, "RFSH");
        Tag(circuit, cpu.HaltOut, "HALT");
        Tag(circuit, cpu.Vcc, "VCC");
        Tag(circuit, cpu.Gnd, "GND");

        var rom = Place(circuit, new Eprom27C256(), 180, -60);
        rom.Load(MemoryImage.FromBinary(image));
        var ram = Place(circuit, new Sram62256(), 520, -60);

        foreach (var memory in new ParallelMemory[] { rom, ram })
        {
            for (var i = 0; i < 15; i++) Tag(circuit, memory.Address[i], $"A{i}");
            for (var i = 0; i < 8; i++) Tag(circuit, memory.Data[i], $"D{i}");
            Tag(circuit, memory.OutputEnable, "RD");
            Tag(circuit, memory.Vcc, "VCC");
            Tag(circuit, memory.Gnd, "GND");
        }

        Tag(circuit, rom.ChipEnable, "ROM_SEL");
        foreach (var pin in rom.Terminals.Where(t => t.Name is "VPP")) Tag(circuit, pin, "VCC");
        Tag(circuit, ram.ChipEnable, "RAM_SEL");
        Tag(circuit, ram.WriteEnable, "WR");

        // Memory: A15 and /MREQ.
        var memoryDecoder = Place(circuit, new Ic74138(), 180, 360);
        Tag(circuit, memoryDecoder.Vcc, "VCC");
        Tag(circuit, memoryDecoder.Gnd, "GND");
        Tag(circuit, memoryDecoder.SelectA, "A15");
        Tag(circuit, memoryDecoder.SelectB, "GND");
        Tag(circuit, memoryDecoder.SelectC, "GND");
        Tag(circuit, memoryDecoder.Enable1, "MREQ");
        Tag(circuit, memoryDecoder.Enable2, "GND");
        Tag(circuit, memoryDecoder.Enable3, "VCC");
        Tag(circuit, memoryDecoder.Outputs[0], "ROM_SEL");
        Tag(circuit, memoryDecoder.Outputs[1], "RAM_SEL");

        // I/O: A5-A7, /IORQ, and /M1 high.
        var ioDecoder = Place(circuit, new Ic74138(), 520, 360);
        Tag(circuit, ioDecoder.Vcc, "VCC");
        Tag(circuit, ioDecoder.Gnd, "GND");
        Tag(circuit, ioDecoder.SelectA, "A5");
        Tag(circuit, ioDecoder.SelectB, "A6");
        Tag(circuit, ioDecoder.SelectC, "A7");
        Tag(circuit, ioDecoder.Enable1, "IORQ");
        Tag(circuit, ioDecoder.Enable2, "GND");
        Tag(circuit, ioDecoder.Enable3, "M1");
        Tag(circuit, ioDecoder.Outputs[4], "SIO_SEL");
        Tag(circuit, ioDecoder.Outputs[5], "CTC_SEL");
        Tag(circuit, ioDecoder.Outputs[6], "PIO_SEL");

        return new ComputerZ80(circuit, cpu, rom, ram, memoryDecoder, ioDecoder);
    }

    /// <summary>A Z80 peripheral's bus pins: the data lines, /M1, /IORQ, /RD, CLK and /INT, its select, and IEI on <paramref name="enableIn"/>.</summary>
    private static void BusZ80(Circuit circuit, Z80Peripheral chip, string select, string enableIn)
    {
        for (var i = 0; i < 8; i++) Tag(circuit, chip.Data[i], $"D{i}");
        Tag(circuit, chip.ChipEnable, select);
        Tag(circuit, chip.M1, "M1");
        Tag(circuit, chip.IoRequest, "IORQ");
        Tag(circuit, chip.ReadStrobe, "RD");
        Tag(circuit, chip.Clock, "CLK");
        Tag(circuit, chip.Interrupt, "INT");
        Tag(circuit, chip.InterruptEnableIn, enableIn);
        Tag(circuit, chip.Vcc, "VCC");
        Tag(circuit, chip.Gnd, "GND");
    }

    /// <summary>
    /// The Z80 computer with a SIO/0 at $80 (/CE = SIO_SEL, B/A = A0, C/D = A1) whose channel A
    /// clocks are CLK, ÷ 32 for 115,200 baud, talking to the serial terminal: a monitor that prints a
    /// banner and dumps memory on request — the terminal asks for the ROM at $0000.
    /// </summary>
    public static void LoadZ80Monitor(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "A Z80 single-board computer";

        BuildZ80(vm, ImageZ80(MonitorZ80Program));

        var sio = Place(circuit, new Z80Sio(), 880, -60);
        BusZ80(circuit, sio, "SIO_SEL", "VCC");
        Tag(circuit, sio.ChannelSelect, "A0");
        Tag(circuit, sio.ControlData, "A1");
        Tag(circuit, sio.Reset, "RESET");
        foreach (var pin in new[] { sio.ReceiveClockA, sio.TransmitClockA, sio.ClockB }) Tag(circuit, pin, "CLK");
        foreach (var pin in new[] { sio.ClearToSendA, sio.CarrierDetectA, sio.ClearToSendB, sio.CarrierDetectB }) Tag(circuit, pin, "GND");
        foreach (var pin in new[] { sio.SyncA, sio.SyncB, sio.ReceiveB }) Tag(circuit, pin, "VCC");

        var terminal = Place(circuit, new SerialTerminal
        {
            BaudRate = 115200,
            Message = "D 0000\r",
            StartDelay = 2.5e-3,
        }, 1260, -60);
        Tag(circuit, sio.TransmitA, "TXD");
        Tag(circuit, terminal.Receive, "TXD");
        Tag(circuit, sio.ReceiveA, "RXD");
        Tag(circuit, terminal.Transmit, "RXD");
        Tag(circuit, terminal.Vcc, "VCC");
        Tag(circuit, terminal.Gnd, "GND");

        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(sio.TransmitA, "TxDA");
        vm.Scope.AddProbe(sio.ReceiveA, "RxDA");
    }

    /// <summary>
    /// The Z80 computer with a CTC at $A0 (CS0 = A0, CS1 = A1) and a PIO at $C0 (B/A = A0, C/D = A1),
    /// in that order in the daisy chain — the CTC's IEO on the PIO's IEI. CTC channel 0's ZC/TO0 is
    /// channel 1's CLK/TRG, eight LEDs hang on port A, and a button on PB0, pulled up, interrupts
    /// through the PIO.
    /// </summary>
    public static void LoadZ80Counter(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "A CTC and a PIO in a Z80's daisy chain";

        BuildZ80(vm, ImageZ80(CounterZ80Program));

        var ctc = Place(circuit, new Z80Ctc(), 880, -60);
        BusZ80(circuit, ctc, "CTC_SEL", "VCC");
        Tag(circuit, ctc.ChannelSelect0, "A0");
        Tag(circuit, ctc.ChannelSelect1, "A1");
        Tag(circuit, ctc.Reset, "RESET");
        Tag(circuit, ctc.InterruptEnableOut, "IEO_CTC");
        Tag(circuit, ctc.ZeroCount[0], "TRG1");
        Tag(circuit, ctc.Triggers[1], "TRG1");
        foreach (var pin in new[] { ctc.Triggers[0], ctc.Triggers[2], ctc.Triggers[3] }) Tag(circuit, pin, "GND");

        var pio = Place(circuit, new Z80Pio(), 880, 380);
        BusZ80(circuit, pio, "PIO_SEL", "IEO_CTC");
        Tag(circuit, pio.PortSelect, "A0");
        Tag(circuit, pio.ControlData, "A1");
        Tag(circuit, pio.StrobeA, "VCC");
        Tag(circuit, pio.StrobeB, "VCC");
        Tag(circuit, pio.PortB[0], "BUTTON");
        for (var i = 1; i < 8; i++) Tag(circuit, pio.PortB[i], "GND");

        // Port A's eight LEDs, each through 330 ohms to ground.
        string[] colours = ["Red", "Red", "Amber", "Amber", "Yellow", "Yellow", "Green", "Green"];
        for (var i = 0; i < 8; i++)
        {
            var y = 200 + (i * 64);
            var resistor = Place(circuit, new Resistor(330), 1220, y);
            var led = Place(circuit, Led.OfColour(colours[i]), 1360, y);
            Tag(circuit, pio.PortA[i], $"PA{i}");
            Tag(circuit, resistor.A, $"PA{i}", 180);
            circuit.Connect(resistor.B, led.Anode);
            Tag(circuit, led.Cathode, "GND");
        }

        // The button on PB0, pulled up: a press is a low.
        var buttonPullUp = Place(circuit, new Resistor(4.7e3), 1260, -100);
        var button = Place(circuit, new PushButton(), 1260, 20);
        buttonPullUp.RotationDegrees = 90;
        button.RotationDegrees = 90;
        Tag(circuit, buttonPullUp.A, "VCC", 270);
        circuit.Connect(buttonPullUp.B, button.A);
        Tag(circuit, buttonPullUp.B, "BUTTON", 360);
        Tag(circuit, button.B, "GND", 90);

        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(ctc.Interrupt, "/INT");
        vm.Scope.AddProbe(pio.PortA[0], "PA0");
        vm.Scope.AddProbe(pio.PortB[0], "PB0");
    }
}
