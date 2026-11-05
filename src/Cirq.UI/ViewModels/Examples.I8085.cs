using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Processors;
using Cirq.Components.Sources;
using Cirq.Components.Nonlinear;
using Cirq.Core.Topology;

namespace Cirq.UI.ViewModels;

/// <summary>An 8085 single-board computer: a 74373 on ALE, a 2764, an 8156, a 74138, and Intel's peripherals for the 8085.</summary>
public static partial class Examples
{
    /// <summary>
    /// The 8085 monitor, assembled from the source in the guide (a test checks these are its bytes):
    /// a banner over the 8251A at 19,200 baud, a prompt, and a D command that dumps eight bytes in
    /// hex. Runs of bytes and where each starts.
    /// </summary>
    internal static readonly (int Address, byte[] Bytes)[] Monitor8085Program =
    [
        (0x0000, [
            0x31, 0x00, 0x81, 0x3E, 0x06, 0xD3, 0x84, 0x3E, 0x40, 0xD3, 0x85, 0x3E, 0xC0, 0xD3, 0x80, 0xAF,
            0xD3, 0x01, 0xD3, 0x01, 0xD3, 0x01, 0x3E, 0x40, 0xD3, 0x01, 0x3E, 0x4E, 0xD3, 0x01, 0x3E, 0x37,
            0xD3, 0x01, 0x21, 0x0B, 0x01, 0xCD, 0x9B, 0x00, 0x21, 0x1F, 0x01, 0xCD, 0x9B, 0x00, 0x21, 0x00,
            0x80, 0xCD, 0x91, 0x00, 0xFE, 0x0D, 0xCA, 0x41, 0x00, 0xCD, 0x85, 0x00, 0x77, 0x23, 0xC3, 0x31,
            0x00, 0x36, 0x00, 0xCD, 0xA5, 0x00, 0x21, 0x00, 0x80, 0x7E, 0xE6, 0xDF, 0xFE, 0x44, 0xC2, 0x7C,
            0x00, 0x23, 0xCD, 0xC6, 0x00, 0xDA, 0x7C, 0x00, 0xEB, 0x7C, 0xCD, 0xAF, 0x00, 0x7D, 0xCD, 0xAF,
            0x00, 0x3E, 0x3A, 0xCD, 0x85, 0x00, 0x06, 0x08, 0x3E, 0x20, 0xCD, 0x85, 0x00, 0x7E, 0xCD, 0xAF,
            0x00, 0x23, 0x05, 0xC2, 0x68, 0x00, 0xCD, 0xA5, 0x00, 0xC3, 0x28, 0x00, 0x21, 0x22, 0x01, 0xCD,
            0x9B, 0x00, 0xC3, 0x28, 0x00, 0xF5, 0xDB, 0x01, 0xE6, 0x01, 0xCA, 0x86, 0x00, 0xF1, 0xD3, 0x00,
            0xC9, 0xDB, 0x01, 0xE6, 0x02, 0xCA, 0x91, 0x00, 0xDB, 0x00, 0xC9, 0x7E, 0xB7, 0xC8, 0xCD, 0x85,
            0x00, 0x23, 0xC3, 0x9B, 0x00, 0x3E, 0x0D, 0xCD, 0x85, 0x00, 0x3E, 0x0A, 0xC3, 0x85, 0x00, 0xF5,
            0x0F, 0x0F, 0x0F, 0x0F, 0xCD, 0xB8, 0x00, 0xF1, 0xE6, 0x0F, 0xC6, 0x30, 0xFE, 0x3A, 0xDA, 0x85,
            0x00, 0xC6, 0x07, 0xC3, 0x85, 0x00, 0x11, 0x00, 0x00, 0x06, 0x00, 0x7E, 0xFE, 0x20, 0xC2, 0xD5,
            0x00, 0x23, 0xC3, 0xCB, 0x00, 0x7E, 0xCD, 0xED, 0x00, 0xDA, 0xE9, 0x00, 0xEB, 0x29, 0x29, 0x29,
            0x29, 0xB5, 0x6F, 0xEB, 0x23, 0x04, 0xC3, 0xD5, 0x00, 0x78, 0xD6, 0x01, 0xC9, 0xFE, 0x30, 0xDA,
            0x09, 0x01, 0xFE, 0x3A, 0xDA, 0x06, 0x01, 0xE6, 0xDF, 0xFE, 0x41, 0xDA, 0x09, 0x01, 0xFE, 0x47,
            0xD2, 0x09, 0x01, 0xD6, 0x37, 0xC9, 0xD6, 0x30, 0xC9, 0x37, 0xC9, 0x43, 0x49, 0x52, 0x51, 0x20,
            0x38, 0x30, 0x38, 0x35, 0x20, 0x6D, 0x6F, 0x6E, 0x69, 0x74, 0x6F, 0x72, 0x0D, 0x0A, 0x00, 0x3E,
            0x20, 0x00, 0x3F, 0x0D, 0x0A, 0x00,
        ]),
    ];

    /// <summary>
    /// The timer interrupts example's program, assembled from the guide's source: the 8253's OUT0
    /// interrupting through the 8259A a thousand times a second to move a count on the 8255A's LEDs,
    /// and a button on RST 7.5 turning it round.
    /// </summary>
    internal static readonly (int Address, byte[] Bytes)[] Counter8085Program =
    [
        (0x0000, [
            0xC3, 0x60, 0x00,
        ]),
        (0x003C, [
            0xC3, 0xA7, 0x00,
        ]),
        (0x0040, [
            0xC3, 0x91, 0x00,
        ]),
        (0x0060, [
            0x31, 0x00, 0x81, 0xAF, 0x32, 0x00, 0x80, 0x3C, 0x32, 0x01, 0x80, 0x3E, 0x80, 0xD3, 0x43, 0xAF,
            0xD3, 0x40, 0x3E, 0x56, 0xD3, 0x60, 0xAF, 0xD3, 0x61, 0x3E, 0xFE, 0xD3, 0x61, 0x3E, 0x36, 0xD3,
            0x23, 0x3E, 0x33, 0xD3, 0x20, 0x3E, 0x07, 0xD3, 0x20, 0x3E, 0x1B, 0x30, 0xFB, 0x76, 0xC3, 0x8D,
            0x00, 0xF5, 0xE5, 0x21, 0x01, 0x80, 0x3A, 0x00, 0x80, 0x86, 0x32, 0x00, 0x80, 0xD3, 0x40, 0x3E,
            0x20, 0xD3, 0x60, 0xE1, 0xF1, 0xFB, 0xC9, 0xF5, 0x3A, 0x01, 0x80, 0x2F, 0x3C, 0x32, 0x01, 0x80,
            0xF1, 0xFB, 0xC9,
        ]),
    ];

    /// <summary>A program as the 8K image of the 2764 at $0000, unused bytes zero.</summary>
    internal static byte[] Image8085((int Address, byte[] Bytes)[] program)
    {
        var image = new byte[0x2000];
        foreach (var (address, bytes) in program) bytes.CopyTo(image, address);
        return image;
    }

    /// <summary>The parts every 8085 example shares.</summary>
    private sealed record Computer8085(Circuit Circuit, Cpu8085 Cpu, Ic74373 Latch, Eprom2764 Rom, Ram8156 Ram, Ic74138 Decoder);

    /// <summary>
    /// An 8085 at 1.8432 MHz — a 3.6864 MHz oscillator module on X1, X2 left open — in the style of
    /// Intel's SDK-85: a 74373 takes A0-A7 off AD0-AD7 as ALE falls, a 2764 EPROM sits at
    /// $0000-$1FFF, and an 8156 with CE on A15 gives 256 bytes of RAM at $8000 (and ports at $80)
    /// through its own latch. One 74138 decodes the rest from A13, A14 and IO/M, enabled while A15 is
    /// low: Y0 is ROM_SEL (memory $0000-$1FFF), Y4 UART_SEL (ports $00-$1F), Y5 PIT_SEL ($20), Y6
    /// PPI_SEL ($40) and Y7 PIC_SEL ($60). /RESET IN has a pull-up and a button; RESET OUT resets the
    /// peripherals. READY is tied high, HOLD, TRAP, RST 6.5, RST 5.5 and SID low; INTR and RST 7.5
    /// are each example's own.
    /// </summary>
    private static Computer8085 Build8085(MainWindowViewModel vm, byte[] image, string intr, string rst75)
    {
        var circuit = vm.Circuit;

        var supply = Place(circuit, new DcVoltageSource(5.0), -560, 440);
        var ground = Place(circuit, new Ground(), -560, 560);
        supply.RotationDegrees = 90;
        circuit.Connect(supply.Negative, ground.Pin);
        Tag(circuit, supply.Positive, "VCC", 270);
        Tag(circuit, ground.Pin, "GND", 180);

        var crystal = Place(circuit, new OscillatorModule(3.6864e6), -560, -250);
        Tag(circuit, crystal.Vcc, "VCC", 270);
        Tag(circuit, crystal.Gnd, "GND");
        Tag(circuit, crystal.Output, "X1");

        // /RESET IN pulled up, and a button to pull it down.
        var pullUp = Place(circuit, new Resistor(4.7e3), -560, -100);
        var reset = Place(circuit, new PushButton(), -560, 20);
        pullUp.RotationDegrees = 90;
        reset.RotationDegrees = 90;
        Tag(circuit, pullUp.A, "VCC", 270);
        circuit.Connect(pullUp.B, reset.A);
        Tag(circuit, pullUp.B, "RESET", 360);
        Tag(circuit, reset.B, "GND", 90);

        var cpu = Place(circuit, new Cpu8085(), -200, 0);
        for (var i = 0; i < 8; i++) Tag(circuit, cpu.AddressData[i], $"AD{i}");
        for (var i = 0; i < 8; i++) Tag(circuit, cpu.AddressHigh[i], $"A{8 + i}");
        Tag(circuit, cpu.X1, "X1");
        Tag(circuit, cpu.ResetIn, "RESET");
        Tag(circuit, cpu.Ready, "VCC");
        foreach (var pin in new[] { cpu.Hold, cpu.Trap, cpu.Rst65, cpu.Rst55, cpu.Sid }) Tag(circuit, pin, "GND");
        Tag(circuit, cpu.Intr, intr);
        Tag(circuit, cpu.Rst75, rst75);
        Tag(circuit, cpu.AddressLatchEnable, "ALE");
        Tag(circuit, cpu.ReadStrobe, "RD");
        Tag(circuit, cpu.WriteStrobe, "WR");
        Tag(circuit, cpu.IoMemory, "IOM");
        Tag(circuit, cpu.ClockOut, "CLK");
        Tag(circuit, cpu.ResetOut, "RESET_OUT");
        Tag(circuit, cpu.InterruptAcknowledge, "INTA");
        Tag(circuit, cpu.Vcc, "VCC");
        Tag(circuit, cpu.Gnd, "GND");

        // The address latch: open while ALE is high, A0-A7 out.
        var latch = Place(circuit, new Ic74373(), 180, -380);
        for (var i = 0; i < 8; i++)
        {
            Tag(circuit, latch.Data[i], $"AD{i}");
            Tag(circuit, latch.Outputs[i], $"A{i}");
        }

        Tag(circuit, latch.LatchEnable, "ALE");
        Tag(circuit, latch.OutputEnable, "GND");
        Tag(circuit, latch.Vcc, "VCC");
        Tag(circuit, latch.Gnd, "GND");

        var rom = Place(circuit, new Eprom2764(), 180, -60);
        rom.Load(MemoryImage.FromBinary(image));
        for (var i = 0; i < 13; i++) Tag(circuit, rom.Address[i], $"A{i}");
        for (var i = 0; i < 8; i++) Tag(circuit, rom.Data[i], $"AD{i}");
        Tag(circuit, rom.ChipEnable, "ROM_SEL");
        Tag(circuit, rom.OutputEnable, "RD");
        foreach (var pin in rom.Terminals.Where(t => t.Name is "VPP" or "PGM")) Tag(circuit, pin, "VCC");
        Tag(circuit, rom.Vcc, "VCC");
        Tag(circuit, rom.Gnd, "GND");

        // The 8156: RAM at $8000 and its ports at $80, selected by A15 with no gate.
        var ram = Place(circuit, new Ram8156(), 520, -60);
        for (var i = 0; i < 8; i++) Tag(circuit, ram.AddressData[i], $"AD{i}");
        Tag(circuit, ram.AddressLatchEnable, "ALE");
        Tag(circuit, ram.IoMemory, "IOM");
        Tag(circuit, ram.ChipEnable, "A15");
        Tag(circuit, ram.ReadStrobe, "RD");
        Tag(circuit, ram.WriteStrobe, "WR");
        Tag(circuit, ram.Reset, "RESET_OUT");
        Tag(circuit, ram.TimerIn, "CLK");
        Tag(circuit, ram.Vcc, "VCC");
        Tag(circuit, ram.Gnd, "GND");

        // Everything else: A13, A14 and IO/M, while A15 is low.
        var decoder = Place(circuit, new Ic74138(), 180, 360);
        Tag(circuit, decoder.Vcc, "VCC");
        Tag(circuit, decoder.Gnd, "GND");
        Tag(circuit, decoder.SelectA, "A13");
        Tag(circuit, decoder.SelectB, "A14");
        Tag(circuit, decoder.SelectC, "IOM");
        Tag(circuit, decoder.Enable1, "GND");
        Tag(circuit, decoder.Enable2, "A15");
        Tag(circuit, decoder.Enable3, "VCC");
        Tag(circuit, decoder.Outputs[0], "ROM_SEL");
        Tag(circuit, decoder.Outputs[4], "UART_SEL");
        Tag(circuit, decoder.Outputs[5], "PIT_SEL");
        Tag(circuit, decoder.Outputs[6], "PPI_SEL");
        Tag(circuit, decoder.Outputs[7], "PIC_SEL");

        return new Computer8085(circuit, cpu, latch, rom, ram, decoder);
    }

    /// <summary>An Intel peripheral's bus pins: the data lines on AD0-AD7, /CS, /RD and /WR.</summary>
    private static void Bus8085(Circuit circuit, IntelPeripheral chip, string select)
    {
        for (var i = 0; i < 8; i++) Tag(circuit, chip.Data[i], $"AD{i}");
        Tag(circuit, chip.ChipSelect, select);
        Tag(circuit, chip.ReadStrobe, "RD");
        Tag(circuit, chip.WriteStrobe, "WR");
        Tag(circuit, chip.Vcc, "VCC");
        Tag(circuit, chip.Gnd, "GND");
    }

    /// <summary>
    /// The 8085 computer with an 8251A at ports $00-$01 (/CS = UART_SEL, C/D = A0) whose /TxC and
    /// /RxC come from the 8156's timer, CLK OUT ÷ 6 — 307.2 kHz, x16 for 19,200 baud — talking to the
    /// serial terminal: a monitor that prints a banner and dumps memory on request.
    /// </summary>
    public static void Load8085Monitor(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "An 8085 single-board computer";

        var board = Build8085(vm, Image8085(Monitor8085Program), "GND", "GND");
        Tag(circuit, board.Ram.TimerOut, "BAUD");

        var uart = Place(circuit, new Usart8251(), 880, -60);
        Bus8085(circuit, uart, "UART_SEL");
        Tag(circuit, uart.ControlData, "A0");
        Tag(circuit, uart.Clock, "CLK");
        Tag(circuit, uart.Reset, "RESET_OUT");
        Tag(circuit, uart.TransmitClock, "BAUD");
        Tag(circuit, uart.ReceiveClock, "BAUD");
        Tag(circuit, uart.ClearToSend, "GND");
        Tag(circuit, uart.DataSetReady, "GND");

        var terminal = Place(circuit, new SerialTerminal
        {
            BaudRate = 19200,
            Message = "D 0000\r",
            StartDelay = 11.5e-3,
        }, 1260, -60);
        Tag(circuit, uart.Transmit, "TXD");
        Tag(circuit, terminal.Receive, "TXD");
        Tag(circuit, uart.Receive, "RXD");
        Tag(circuit, terminal.Transmit, "RXD");
        Tag(circuit, terminal.Vcc, "VCC");
        Tag(circuit, terminal.Gnd, "GND");

        vm.Scope.TimebasePerDivision = 5e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(uart.Transmit, "TxD");
        vm.Scope.AddProbe(uart.Receive, "RxD");
    }

    /// <summary>
    /// The 8085 computer with an 8253 at $20 (counter 0 on CLK, its OUT0 the 8259A's IR0), an 8259A
    /// at $60 in 8085 mode answering INTR, and an 8255A at $40 whose port A drives eight LEDs; a
    /// button with a pull-down on RST 7.5 turns the count round.
    /// </summary>
    public static void Load8085Counter(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Timer interrupts on an 8085, through an 8259A";

        Build8085(vm, Image8085(Counter8085Program), "INTR", "BUTTON");

        var pit = Place(circuit, new Pit8253(), 880, -60);
        Bus8085(circuit, pit, "PIT_SEL");
        Tag(circuit, pit.A0, "A0");
        Tag(circuit, pit.A1, "A1");
        Tag(circuit, pit.Clocks[0], "CLK");
        Tag(circuit, pit.Gates[0], "VCC");
        Tag(circuit, pit.Outputs[0], "TICK");
        foreach (var pin in new[] { pit.Clocks[1], pit.Clocks[2], pit.Gates[1], pit.Gates[2] }) Tag(circuit, pin, "GND");

        var pic = Place(circuit, new Pic8259(), 520, 400);
        Bus8085(circuit, pic, "PIC_SEL");
        Tag(circuit, pic.Address0, "A0");
        Tag(circuit, pic.Interrupt, "INTR");
        Tag(circuit, pic.InterruptAcknowledge, "INTA");
        Tag(circuit, pic.SlaveProgram, "VCC");
        Tag(circuit, pic.Requests[0], "TICK");
        for (var i = 1; i < 8; i++) Tag(circuit, pic.Requests[i], "GND");

        var ppi = Place(circuit, new Ppi8255(), 880, 400);
        Bus8085(circuit, ppi, "PPI_SEL");
        Tag(circuit, ppi.A0, "A0");
        Tag(circuit, ppi.A1, "A1");
        Tag(circuit, ppi.Reset, "RESET_OUT");

        // Port A's eight LEDs, each through 330 ohms to ground.
        string[] colours = ["Red", "Red", "Amber", "Amber", "Yellow", "Yellow", "Green", "Green"];
        for (var i = 0; i < 8; i++)
        {
            var y = 200 + (i * 64);
            var resistor = Place(circuit, new Resistor(330), 1220, y);
            var led = Place(circuit, Led.OfColour(colours[i]), 1360, y);
            Tag(circuit, ppi.PortA[i], $"PA{i}");
            Tag(circuit, resistor.A, $"PA{i}", 180);
            circuit.Connect(resistor.B, led.Anode);
            Tag(circuit, led.Cathode, "GND");
        }

        // The button on RST 7.5, pulled down: a press is a rising edge, which the 8085 latches.
        var pullDown = Place(circuit, new Resistor(4.7e3), 1260, 20);
        var button = Place(circuit, new PushButton(), 1260, -100);
        pullDown.RotationDegrees = 90;
        button.RotationDegrees = 90;
        Tag(circuit, button.A, "VCC", 270);
        circuit.Connect(button.B, pullDown.A);
        Tag(circuit, button.B, "BUTTON", 360);
        Tag(circuit, pullDown.B, "GND", 90);

        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(pit.Outputs[0], "OUT0");
        vm.Scope.AddProbe(pic.Interrupt, "INTR");
        vm.Scope.AddProbe(ppi.PortA[0], "PA0");
    }
}
