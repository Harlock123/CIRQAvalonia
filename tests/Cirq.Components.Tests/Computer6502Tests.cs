using System.Diagnostics;
using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Processors;
using Cirq.Core.Digital;
using Cirq.Core.Topology;
using Xunit.Abstractions;

namespace Cirq.Components.Tests;

/// <summary>
/// The whole 6502 computer: processor, ROM, RAM, the 7400's decoding, a 6522 driving an HD44780,
/// or a 6551 talking to a serial terminal — each running its program from ROM through the solve.
/// </summary>
public class Computer6502Tests(ITestOutputHelper output)
{
    /// <summary>The hello-world the Ben Eater example runs, as the guide shows it.</summary>
    internal const string HelloWorld = """
        ; Hello world for the 6502 computer: a 65C02, a 28C256 at $8000-$FFFF,
        ; a 62256 at $0000-$3FFF, and a 6522 at $6000 driving an HD44780 in
        ; eight-bit mode: data on port B, E, RW and RS on PA7, PA6 and PA5.

        PORTB   = $6000         ; the 6522's registers
        PORTA   = $6001
        DDRB    = $6002
        DDRA    = $6003

        E       = %10000000     ; the LCD's control lines, on port A
        RW      = %01000000
        RS      = %00100000

                .org $8000
        reset:  ldx #$FF        ; the stack at the top of page one
                txs
                lda #%11100000  ; PA5-PA7 drive the LCD's control lines
                sta DDRA
                ldx #0
        setup:  lda init,x      ; the set-up commands, in order
                beq hello
                jsr command
                inx
                bne setup
        hello:  ldx #0
        next:   lda text,x      ; then the text, a character at a time
                beq idle
                jsr write
                inx
                bne next
        idle:   jmp idle

        ; Sends A to the LCD as a command (RS low) or a character (RS high).
        command: ldy #0
                beq send
        write:  ldy #RS
        send:   jsr wait
                sta PORTB       ; the byte onto the LCD's data lines
                sty PORTA       ; RS as wanted, E low
                tya
                ora #E
                sta PORTA       ; E high...
                sty PORTA       ; ...and low: the LCD takes the byte as E falls
                rts

        ; Waits while the LCD is busy, reading its busy flag through port B.
        wait:   pha
                stz DDRB        ; port B in
        busy:   lda #RW
                sta PORTA       ; RW high, E low
                lda #RW+E
                sta PORTA       ; E high: the LCD drives its busy flag onto D7
                bit PORTB       ; N = the busy flag
                bmi busy        ; still busy: drop E and ask again
                lda #RW
                sta PORTA
                lda #$FF
                sta DDRB        ; port B out again
                pla
                rts

        init:   .byte %00111000 ; eight bits, two lines, 5x8 font
                .byte %00001110 ; display on, cursor on
                .byte %00000110 ; move the cursor right after each character
                .byte %00000001 ; clear
                .byte 0
        text:   .asciiz "Hello, world!"

                .org $FFFC
                .word reset     ; the reset vector
                .word reset     ; IRQ/BRK, unused
        """;

    /// <summary>The computer with a 6522 at $6000 and an LCD on it: CS1 = A13, CS2B = NAND(NOT A15, A14).</summary>
    internal static (Cpu6502CircuitTests.Computer Computer, Via6522 Via, CharacterLcd Lcd) WithLcd(byte[] rom)
    {
        var computer = new Cpu6502CircuitTests.Computer(rom);
        var circuit = computer.Circuit;
        var via = circuit.Add(new Via6522());
        var lcd = circuit.Add(new CharacterLcd());

        foreach (var ic in new DigitalIc[] { via, lcd })
        {
            circuit.Connect(ic.Vcc, computer.Rail.Positive);
            circuit.Connect(ic.Gnd, computer.Ground.Pin);
        }

        var cpu = computer.Cpu;
        for (var i = 0; i < 8; i++) circuit.Connect(via.Data[i], cpu.Data[i]);
        for (var i = 0; i < 4; i++) circuit.Connect(via.RegisterSelect[i], cpu.Address[i]);
        circuit.Connect(via.ChipSelect1, cpu.Address[13]);

        circuit.Connect(computer.IoSelect, via.ChipSelect2);

        circuit.Connect(via.Clock, computer.Clock.Output);
        circuit.Connect(via.ReadWrite, cpu.ReadWrite);
        circuit.Connect(via.Reset, cpu.Reset);
        circuit.Connect(via.Irq, cpu.Irq);
        foreach (var pin in new[] { via.Ca1, via.Cb1 }) circuit.Connect(pin, computer.Ground.Pin);

        for (var i = 0; i < 8; i++) circuit.Connect(lcd.Data[i], via.PortB[i]);
        circuit.Connect(lcd.Enable, via.PortA[7]);
        circuit.Connect(lcd.ReadWrite, via.PortA[6]);
        circuit.Connect(lcd.RegisterSelect, via.PortA[5]);

        return (computer, via, lcd);
    }

    [Fact]
    public void HelloWorldAppearsOnTheLcd()
    {
        var rom = Assembler6502.Assemble(HelloWorld).Rom32K();
        var (computer, _, lcd) = WithLcd(rom);
        var sim = computer.Simulator;

        var clock = Stopwatch.StartNew();
        sim.Run(3.5e-3);
        output.WriteLine($"3.5 ms of a 1 MHz 6502 in {clock.Elapsed.TotalSeconds:0.00} s: {3.5e-3 / clock.Elapsed.TotalSeconds * 1e3:0.00} ms simulated per second; {computer.Cpu.Core.Cycles} cycles");

        Assert.Equal("Hello, world!", lcd.Line(0).TrimEnd());
        Assert.True(lcd.DisplayOn);
        Assert.Equal(0, computer.Cpu.UndefinedReads);
    }
}

/// <summary>The 6522 on its own: ports, handshakes, both timers and the interrupt logic, driven by programs.</summary>
public class Via6522Tests
{
    /// <summary>The computer with a 6522 at $6000 and nothing on its ports.</summary>
    private static (Cpu6502CircuitTests.Computer Computer, Via6522 Via) WithVia(string source)
    {
        var rom = Assembler6502.Assemble(source).Rom32K();
        var computer = new Cpu6502CircuitTests.Computer(rom);
        var circuit = computer.Circuit;
        var via = circuit.Add(new Via6522());
        circuit.Connect(via.Vcc, computer.Rail.Positive);
        circuit.Connect(via.Gnd, computer.Ground.Pin);

        var cpu = computer.Cpu;
        for (var i = 0; i < 8; i++) circuit.Connect(via.Data[i], cpu.Data[i]);
        for (var i = 0; i < 4; i++) circuit.Connect(via.RegisterSelect[i], cpu.Address[i]);
        circuit.Connect(via.ChipSelect1, cpu.Address[13]);
        circuit.Connect(computer.IoSelect, via.ChipSelect2);
        circuit.Connect(via.Clock, computer.Clock.Output);
        circuit.Connect(via.ReadWrite, cpu.ReadWrite);
        circuit.Connect(via.Reset, cpu.Reset);
        circuit.Connect(via.Irq, cpu.Irq);
        return (computer, via);
    }

    private const string Header = """
        PORTB = $6000
        PORTA = $6001
        DDRB  = $6002
        DDRA  = $6003
        T1CL  = $6004
        T1CH  = $6005
        T2CL  = $6008
        T2CH  = $6009
        ACR   = $600B
        PCR   = $600C
        IFR   = $600D
        IER   = $600E
                .org $8000
        reset:  ldx #$FF
                txs
        """;

    private const string Vectors = """
                .org $FFFC
                .word reset
                .word irq
        """;

    [Fact]
    public void ThePortsDriveWhatTheirDirectionRegistersSayAndReadThePins()
    {
        var (computer, via) = WithVia(Header + "\n" + """
                lda #$F0        ; port A: high nibble out
                sta DDRA
                lda #$A5
                sta PORTA
                lda PORTA       ; reads the pins: A on top, and the low nibble as wired
                sta $0200
        done:   jmp done
        irq:    rti
        """ + "\n" + Vectors);

        for (var i = 0; i < 4; i++) computer.Circuit.Connect(via.PortA[i], i % 2 == 0 ? computer.Rail.Positive : computer.Ground.Pin);
        computer.Simulator.Run(60e-6);

        Assert.Equal(0xF0, via.DirectionA);
        Assert.Equal(0xA0, Enumerable.Range(4, 4).Sum(i => computer.Simulator.NodeVoltage(via.PortA[i]) > 2.5 ? 1 << i : 0));
        Assert.Equal(LogicState.HighImpedance, via.GetOutputState(0));
        Assert.Equal(0xA5, computer.Ram.ReadByte(0x200));
    }

    /// <summary>
    /// Timer 1 free-running with PB7 on: a square wave with nothing but the timer running it, its
    /// period 2 × (N + 2) cycles — and its interrupt, enabled, counted by a handler in RAM.
    /// </summary>
    [Fact]
    public void Timer1FreeRunsASquareWaveOnPb7AndInterrupts()
    {
        var (computer, via) = WithVia(Header + "\n" + """
                lda #%11000000  ; T1 free-run, PB7 output
                sta ACR
                lda #%11000000  ; enable the T1 interrupt
                sta IER
                lda #48         ; N = 48: a period of 2 x 50 cycles
                sta T1CL
                lda #0
                sta T1CH
                stz $10
                cli
        done:   jmp done
        irq:    pha
                lda T1CL        ; clears the flag
                inc $10
                pla
                rti
        """ + "\n" + Vectors);

        var sim = computer.Simulator;
        List<double> edges = [];
        sim.Run(5e-6);
        sim.Scheduler.EventApplied += e =>
        {
            if (ReferenceEquals(e.Device, via) && e.OutputIndex == 15 && e.NewState == LogicState.High) edges.Add(e.Timestamp);
        };

        sim.Run(1.2e-3);

        var periods = edges.Zip(edges.Skip(1), (a, b) => b - a).Skip(1).ToList();
        Assert.True(periods.Count > 5);
        Assert.All(periods, p => Assert.Equal(100e-6, p, 7));

        // Twenty-odd time-outs in 1.2 ms at one every 50 µs, each taken and counted.
        Assert.InRange(computer.Ram.ReadByte(0x10), 20, 25);
    }

    [Fact]
    public void Timer2CountsPulsesOnPb6()
    {
        var (computer, via) = WithVia(Header + "\n" + """
                lda #%00100000  ; T2 counts PB6 pulses
                sta ACR
                lda #5
                sta T2CL
                lda #0
                sta T2CH
        wait:   lda IFR
                and #%00100000
                beq wait
                lda #$AA
                sta $0200
        done:   jmp done
        irq:    rti
        """ + "\n" + Vectors);

        var clock = computer.Circuit.Add(new ClockSource(50e3));
        computer.Circuit.Connect(clock.Out, via.PortB[6]);

        computer.Simulator.Run(60e-6);
        Assert.Equal(0, computer.Ram.ReadByte(0x200));

        // Five falling edges at 50 kHz is a hundred microseconds.
        computer.Simulator.Run(150e-6);
        Assert.Equal(0xAA, computer.Ram.ReadByte(0x200));
    }

    /// <summary>
    /// CA1's active edge sets its flag and, enabled, pulls IRQB low; reading port A clears it. CA2 in
    /// handshake mode goes low on that read and back high on the next CA1 edge.
    /// </summary>
    [Fact]
    public void Ca1InterruptsAndCa2Handshakes()
    {
        var (computer, via) = WithVia(Header + "\n" + """
                lda #%00001001  ; CA1 rising edge, CA2 handshake output
                sta PCR
                lda #%10000010  ; enable CA1
                sta IER
        wait:   lda IFR
                and #%00000010
                beq wait
                lda PORTA       ; clears CA1, takes CA2 low
                sta $0200
        done:   jmp done
        irq:    rti
        """ + "\n" + Vectors);

        var ca1 = computer.Circuit.Add(new LogicToggle());
        computer.Circuit.Connect(ca1.Out, via.Ca1);
        var sim = computer.Simulator;

        sim.Run(40e-6);
        Assert.Equal(0, via.InterruptFlags & 0x02);
        Assert.True(sim.NodeVoltage(via.Ca2) > 2.5);

        ca1.State = true;
        sim.Run(2e-6);
        Assert.NotEqual(0, via.InterruptFlags & 0x82);
        Assert.Equal(LogicState.Low, via.GetOutputState(26));

        sim.Run(20e-6);
        Assert.Equal(0, via.InterruptFlags & 0x02);
        Assert.Equal(LogicState.HighImpedance, via.GetOutputState(26));
        Assert.True(sim.NodeVoltage(via.Ca2) < 0.8);

        ca1.State = false;
        sim.Run(5e-6);
        ca1.State = true;
        sim.Run(5e-6);
        Assert.True(sim.NodeVoltage(via.Ca2) > 2.5);
    }

    [Fact]
    public void ThePinoutIsTheDatasheets()
    {
        var via = new Via6522();
        Assert.Equal("PA0", via.Terminals.Single(t => t.Id == "p2").Name);
        Assert.Equal("PB7", via.Terminals.Single(t => t.Id == "p17").Name);
        Assert.Equal("IRQB", via.Terminals.Single(t => t.Id == "p21").Name);
        Assert.Equal("PHI2", via.Terminals.Single(t => t.Id == "p25").Name);
        Assert.Equal("D0", via.Terminals.Single(t => t.Id == "p33").Name);
        Assert.Equal("RS0", via.Terminals.Single(t => t.Id == "p38").Name);
        Assert.Equal("CA1", via.Terminals.Single(t => t.Id == "p40").Name);
    }
}

/// <summary>The 6551 talking to a serial terminal: a greeting out, and an echo of what comes in.</summary>
public class Acia6551Tests
{
    internal const string Echo = """
        ; Serial hello and echo for the 6502 computer: a 6551 at $5000 at 19,200
        ; baud, eight bits, no parity, polled rather than interrupt-driven.

        DATA    = $5000         ; the 6551's registers
        STATUS  = $5001
        COMMAND = $5002
        CONTROL = $5003

                .org $8000
        reset:  ldx #$FF
                txs
                sta STATUS      ; a programmed reset
                lda #%00011111  ; 19,200 baud from the crystal, eight bits, one stop bit
                sta CONTROL
                lda #%00001011  ; no parity, no echo, no interrupts, receiver on
                sta COMMAND
                ldx #0
        greet:  lda text,x      ; the greeting, a character at a time
                beq echo
                jsr send
                inx
                bne greet
        echo:   lda STATUS      ; then wait for a character...
                and #%00001000
                beq echo
                lda DATA
                jsr send        ; ...and send it straight back
                jmp echo

        ; Sends A when the transmitter has room for it.
        send:   pha
        wait:   lda STATUS
                and #%00010000
                beq wait
                pla
                sta DATA
                rts

        text:   .byte "Hello, 6502!", 13, 10, 0

                .org $FFFC
                .word reset
                .word reset
        """;

    internal static (Cpu6502CircuitTests.Computer Computer, Acia6551 Acia, Buses.SerialTerminal Terminal) WithAcia(byte[] rom)
    {
        var computer = new Cpu6502CircuitTests.Computer(rom);
        var circuit = computer.Circuit;
        var acia = circuit.Add(new Acia6551());
        circuit.Connect(acia.Vcc, computer.Rail.Positive);
        circuit.Connect(acia.Gnd, computer.Ground.Pin);

        var cpu = computer.Cpu;
        for (var i = 0; i < 8; i++) circuit.Connect(acia.Data[i], cpu.Data[i]);
        circuit.Connect(acia.RegisterSelect[0], cpu.Address[0]);
        circuit.Connect(acia.RegisterSelect[1], cpu.Address[1]);
        circuit.Connect(acia.ChipSelect0, cpu.Address[12]);
        circuit.Connect(computer.IoSelect, acia.ChipSelect1);
        circuit.Connect(acia.Clock, computer.Clock.Output);
        circuit.Connect(acia.ReadWrite, cpu.ReadWrite);
        circuit.Connect(acia.Reset, cpu.Reset);
        circuit.Connect(acia.Irq, cpu.Irq);
        foreach (var pin in new[] { acia.ClearToSend, acia.CarrierDetect, acia.DataSetReady }) circuit.Connect(pin, computer.Ground.Pin);

        var terminal = circuit.Add(new Buses.SerialTerminal { BaudRate = 19200, Message = "hi", StartDelay = 7.5e-3 });
        circuit.Connect(terminal.Vcc, computer.Rail.Positive);
        circuit.Connect(terminal.Gnd, computer.Ground.Pin);
        circuit.Connect(acia.Transmit, terminal.Receive);
        circuit.Connect(terminal.Transmit, acia.Receive);
        return (computer, acia, terminal);
    }

    [Fact]
    public void TheGreetingReachesTheTerminalAndWhatItTypesComesBack()
    {
        var rom = Assembler6502.Assemble(Echo).Rom32K();
        var (computer, acia, terminal) = WithAcia(rom);

        computer.Simulator.Run(10e-3);

        Assert.Equal("Hello, 6502!..hi", terminal.ReceivedText);
        Assert.Equal("hi", string.Concat(acia.ReceivedBytes.Select(b => (char)b)));
        Assert.Equal(0, terminal.FramingErrors);
        Assert.Equal(19200, acia.BaudRate, 6);
    }

    [Theory]
    [InlineData(0x1E, 9600)]
    [InlineData(0x18, 1200)]
    [InlineData(0x11, 50)]
    [InlineData(0x10, 115200)]
    public void TheBaudRatesComeFromTheCrystal(int control, double baud)
    {
        var acia = new Acia6551();
        typeof(Acia6551).GetField("_control", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(acia, (byte)control);
        Assert.Equal(baud, acia.BaudRate, 6);
    }

    [Fact]
    public void ThePinoutIsTheDatasheets()
    {
        var acia = new Acia6551();
        Assert.Equal(28, acia.PinCount);
        Assert.Equal("TxD", acia.Terminals.Single(t => t.Id == "p10").Name);
        Assert.Equal("RxD", acia.Terminals.Single(t => t.Id == "p12").Name);
        Assert.Equal("D0", acia.Terminals.Single(t => t.Id == "p18").Name);
        Assert.Equal("IRQB", acia.Terminals.Single(t => t.Id == "p26").Name);
        Assert.Equal("PHI2", acia.Terminals.Single(t => t.Id == "p27").Name);
        Assert.Equal("RWB", acia.Terminals.Single(t => t.Id == "p28").Name);
    }
}
