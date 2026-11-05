using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Processors;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Tests;

/// <summary>
/// The Z80 SIO/0 and DART on a Z80's bus, driven by Z80 programs: characters both ways with the
/// serial terminal, every receive interrupt mode, the transmit and external/status interrupts,
/// status affects vector, the special receive conditions (parity, overrun), breaks both ways,
/// the auto enables, /W/RDY as a ready and as a wait on the Z80's /WAIT, and the DART's /RI.
/// </summary>
public class Z80SioTests
{
    /// <summary>
    /// The Z80 board at 4 MHz with a SIO (or DART) at $80: A data $80, B data $81, A control $82,
    /// B control $83 (B/A = A0, C/D = A1). A 1.8432 MHz oscillator clocks /RxCA, /TxCA and /RxTxCB,
    /// so x16 is 115,200 baud; a serial terminal is on channel A.
    /// </summary>
    private sealed class Bench
    {
        public CpuZ80CircuitTests.Computer Computer { get; }

        public Z80SerialController Sio { get; }

        public SerialTerminal Terminal { get; }

        public OscillatorModule SerialClock { get; }

        public Bench(string source, Z80SerialController? sio = null, string message = "", double delay = 1, UartParity parity = UartParity.None)
        {
            Computer = new CpuZ80CircuitTests.Computer(CpuZ80CircuitTests.Rom(Prelude + "\n" + source));
            var c = Computer;
            Sio = c.Peripheral(sio ?? new Z80Sio(), c.Select80);
            c.Circuit.Connect(Sio.ChannelSelect, c.Cpu.Address[0]);
            c.Circuit.Connect(Sio.ControlData, c.Cpu.Address[1]);
            c.Circuit.Connect(Sio.Reset, c.Rail.Positive);
            SerialClock = c.Ic(new OscillatorModule(1.8432e6));
            foreach (var pin in new[] { Sio.ReceiveClockA, Sio.TransmitClockA, Sio.ClockB }) c.Circuit.Connect(pin, SerialClock.Output);

            Terminal = c.Circuit.Add(new SerialTerminal { BaudRate = 115200, Message = message, StartDelay = delay, Parity = parity });
            c.Circuit.Connect(Terminal.Vcc, c.Rail.Positive);
            c.Circuit.Connect(Terminal.Gnd, c.Ground.Pin);
            c.Circuit.Connect(Sio.TransmitA, Terminal.Receive);
            c.Circuit.Connect(Terminal.Transmit, Sio.ReceiveA);

            Cts = c.Toggle(Sio.ClearToSendA, false);
            Dcd = c.Toggle(Sio.CarrierDetectA, false);
            Sync = c.Toggle(Sio.SyncA, true);
            foreach (var pin in new[] { Sio.ClearToSendB, Sio.CarrierDetectB, Sio.SyncB }) c.Circuit.Connect(pin, c.Ground.Pin);
            ReceiveB = c.Toggle(Sio.ReceiveB, true);
        }

        public LogicToggle Cts { get; }

        public LogicToggle Dcd { get; }

        public LogicToggle Sync { get; }

        public LogicToggle ReceiveB { get; }

        public void Run(double seconds) => Computer.Simulator.Run(seconds);

        public byte Ram(int address) => Computer.Ram.ReadByte(address - 0x8000);

        public LogicState Pin(Terminal pin) => Sio.GetOutputState(Sio.OutputTerminals.ToList().IndexOf(pin));
    }

    /// <summary>
    /// Mode 2 with I = 0 and the SIO's vector $20: with status affects vector, $20-$2E are the eight
    /// sources (B transmit, B external, B receive, B special, then A's), each calling the handler the
    /// program names — <c>btx bext brx bsp atx aext arx asp</c>, with the ones it does not use
    /// falling to <c>none</c>. Then channel A is set up for 115,200 baud 8N1 (x16) and channel B the same.
    /// </summary>
    private const string Prelude = """
                    jp start
                    org $0020
                    dw btx,bext,brx,bsp,atx,aext,arx,asp
                    org $0100
            start:  ld sp,$0000
                    xor a
                    ld i,a
                    im 2
                    ld c,$82
                    ld hl,setup
                    ld b,setupend-setup
                    otir            ; channel A
                    ld c,$83
                    ld hl,setup
                    ld b,setupend-setup
                    otir            ; channel B
                    ld a,2
                    out ($83),a
                    ld a,$20
                    out ($83),a     ; WR2 (channel B): the vector
                    jp main
            setup:  db $18          ; channel reset
                    db 4,$44        ; WR4: x16, one stop bit, no parity
                    db 3,$C1        ; WR3: 8 bits, receiver on
                    db 5,$EA        ; WR5: DTR, 8 bits, transmitter on, RTS
            setupend:
            """;

    private const string Unused = """
            btx:
            bext:
            brx:
            bsp:
            atx:
            aext:
            arx:
            asp:
            none:   ei
                    reti
            """;

    [Fact]
    public void ThePinoutIsTheDatasheets()
    {
        var sio = new Z80Sio();
        string At(int pin) => sio.Terminals.Single(t => t.Id == $"p{pin}").Name;

        Assert.Equal(40, sio.Terminals.Count);
        Assert.Equal(["D1", "D3", "D5", "D7", "INT", "IEI", "IEO", "M1", "VCC", "WRDYA", "SYNCA", "RxDA", "RxCA", "TxCA", "TxDA", "DTRA", "RTSA", "CTSA", "DCDA", "CLK"],
            Enumerable.Range(1, 20).Select(At));
        Assert.Equal(["RESET", "DCDB", "CTSB", "RTSB", "DTRB", "TxDB", "RxTxCB", "RxDB", "SYNCB", "WRDYB", "GND", "RD", "CD", "BA", "CE", "IORQ", "D6", "D4", "D2", "D0"],
            Enumerable.Range(21, 20).Select(At));
        Assert.Equal("/W/RDYA", sio.PinLabel(sio.WaitReadyA));
        Assert.Equal("Z80 SIO/0", sio.PartNumber);

        var dart = new Z80Dart();
        Assert.Equal("RIA", dart.Terminals.Single(t => t.Id == "p11").Name);
        Assert.Equal("/RIB", dart.PinLabel(dart.SyncB));
    }

    /// <summary>Polled transmit: RR0's buffer-empty bit, the bytes out on TxDA at 115,200 baud, and /RTS and /DTR low.</summary>
    [Fact]
    public void ItSendsToTheTerminal()
    {
        var bench = new Bench("""
            main:   ld hl,text
            next:   ld a,(hl)
                    or a
                    jr z,done
            wait:   in a,($82)
                    bit 2,a
                    jr z,wait
                    ld a,(hl)
                    out ($80),a
                    inc hl
                    jr next
            done:   jr done
            text:   db "Z80 SIO",0
            """ + "\n" + Unused);
        bench.Run(900e-6);

        Assert.Equal("Z80 SIO", bench.Terminal.ReceivedText);
        Assert.Equal(0, bench.Terminal.FramingErrors);
        Assert.Equal("Z80 SIO", bench.Sio.TransmittedText(false));
        Assert.InRange(bench.Sio.Baud(false).Transmit, 115000, 115400);
        Assert.Equal("8N1", bench.Sio.Format(false));
        Assert.Equal(LogicState.Low, bench.Pin(bench.Sio.RequestToSendA));
        Assert.Equal(LogicState.Low, bench.Pin(bench.Sio.DataTerminalReadyA));
        Assert.Equal(0x01, bench.Sio.ReadRegisterValue(false, 1) & 0x01);       // all sent
    }

    /// <summary>
    /// Receive interrupts on every character, status affects vector: channel A's receive comes as
    /// $2C, the handler takes the byte, and a break on channel B is an external/status interrupt, $22.
    /// </summary>
    [Fact]
    public void ReceiveInterruptsCarryTheirStatusInTheVector()
    {
        var bench = new Bench("""
            main:   ld a,1
                    out ($82),a
                    ld a,$10        ; WR1 (A): receive interrupts on every character, parity special
                    out ($82),a
                    ld a,1
                    out ($83),a
                    ld a,$05        ; WR1 (B): status affects vector, external/status interrupts
                    out ($83),a
                    ld hl,$8040
                    ei
            done:   jr done
            arx:    push af
                    in a,($80)
                    ld (hl),a
                    inc hl
                    pop af
                    ei
                    reti
            bext:   push af
                    ld a,$10
                    out ($83),a     ; reset external/status interrupts
                    in a,($83)
                    ld ($8050),a    ; RR0 as held
                    ld a,($8051)
                    inc a
                    ld ($8051),a
                    pop af
                    ei
                    reti
            btx:
            brx:
            bsp:
            atx:
            aext:
            asp:    ei
                    reti
            """, message: "OK", delay: 100e-6);
        bench.Run(400e-6);

        Assert.Equal((byte)'O', bench.Ram(0x8040));
        Assert.Equal((byte)'K', bench.Ram(0x8041));
        Assert.Equal(0x2C, bench.Computer.Cpu.Core.LastAcknowledgeByte);

        // A break on RxDB: the line low for well over a character.
        bench.ReceiveB.State = false;
        bench.Run(250e-6);
        Assert.Equal(1, bench.Ram(0x8051));
        Assert.Equal(0x22, bench.Computer.Cpu.Core.LastAcknowledgeByte);
        Assert.Equal(0x80, bench.Ram(0x8050) & 0x80);
        Assert.Equal(1, bench.Sio.Waiting(true));                                // the null a break leaves
        bench.ReceiveB.State = true;
        bench.Run(60e-6);
        Assert.Equal(2, bench.Ram(0x8051));                                     // the break's end
        Assert.Equal(0, bench.Sio.ReadRegisterValue(true, 0) & 0x80);
    }

    /// <summary>The transmit interrupt comes as the buffer empties into the shift register ($28); the last one is answered with "reset Tx int pending".</summary>
    [Fact]
    public void TransmitInterruptsFeedTheBuffer()
    {
        var bench = new Bench("""
            main:   ld a,1
                    out ($82),a
                    ld a,$02        ; WR1 (A): transmit interrupts
                    out ($82),a
                    ld a,1
                    out ($83),a
                    ld a,$04        ; WR1 (B): status affects vector
                    out ($83),a
                    ld hl,text
                    ei
                    ld a,(hl)
                    inc hl
                    out ($80),a     ; the first by hand: the interrupt comes as it moves on
            done:   jr done
            atx:    push af
                    ld a,(hl)
                    or a
                    jr z,last
                    out ($80),a
                    inc hl
                    jr out
            last:   ld a,$28        ; reset Tx int pending
                    out ($82),a
            out:    pop af
                    ei
                    reti
            text:   db "IRQ",0
            btx:
            bext:
            brx:
            bsp:
            aext:
            arx:
            asp:    ei
                    reti
            """);
        bench.Run(500e-6);

        Assert.Equal("IRQ", bench.Terminal.ReceivedText);
        Assert.Equal(0x28, bench.Computer.Cpu.Core.LastAcknowledgeByte);
        Assert.False(bench.Sio.PendingSources[1]);
    }

    /// <summary>
    /// /CTS and /DCD changes are external/status interrupts ($2A for channel A), with RR0 holding the
    /// new levels until the reset; and with auto enables, /CTS high holds the transmitter.
    /// </summary>
    [Fact]
    public void ExternalStatusAndAutoEnables()
    {
        var bench = new Bench("""
            main:   ld a,1
                    out ($82),a
                    ld a,$01        ; WR1 (A): external/status interrupts
                    out ($82),a
                    ld a,1
                    out ($83),a
                    ld a,$04
                    out ($83),a
                    ld a,3
                    out ($82),a
                    ld a,$E1        ; WR3 (A): auto enables
                    out ($82),a
                    ei
                    ld a,'!'
                    out ($80),a
            done:   jr done
            aext:   push af
                    in a,($82)
                    ld ($8050),a    ; RR0, held
                    ld a,$10
                    out ($82),a
                    ld a,($8051)
                    inc a
                    ld ($8051),a
                    pop af
                    ei
                    reti
            btx:
            bext:
            brx:
            bsp:
            atx:
            arx:
            asp:    ei
                    reti
            """);
        bench.Cts.State = true;         // /CTS high from the start: not clear to send
        bench.Run(200e-6);
        Assert.Equal(string.Empty, bench.Terminal.ReceivedText);
        Assert.Equal(0, bench.Ram(0x8051));

        bench.Cts.State = false;
        bench.Run(150e-6);
        Assert.Equal("!", bench.Terminal.ReceivedText);
        Assert.Equal(1, bench.Ram(0x8051));
        Assert.Equal(0x2A, bench.Computer.Cpu.Core.LastAcknowledgeByte);
        Assert.Equal(0x20, bench.Ram(0x8050) & 0x20);         // CTS, its pin now low

        bench.Dcd.State = true;
        bench.Run(40e-6);
        Assert.Equal(2, bench.Ram(0x8051));
        Assert.Equal(0, bench.Ram(0x8050) & 0x08);              // DCD gone
    }

    /// <summary>
    /// Even parity against a terminal sending 8N1: 'A' arrives with its stop bit where the parity
    /// should be, a parity error — with receive interrupt mode 10, a special receive condition
    /// ($2E); RR1 shows it until an error reset.
    /// </summary>
    [Fact]
    public void AParityErrorIsASpecialReceiveCondition()
    {
        var bench = new Bench("""
            main:   ld a,4
                    out ($82),a
                    ld a,$47        ; WR4 (A): x16, one stop bit, even parity
                    out ($82),a
                    ld a,1
                    out ($82),a
                    ld a,$10        ; WR1 (A): every character, parity special
                    out ($82),a
                    ld a,1
                    out ($83),a
                    ld a,$04
                    out ($83),a
                    ei
            done:   jr done
            asp:    push af
                    ld a,1
                    out ($82),a
                    in a,($82)
                    ld ($8050),a    ; RR1
                    in a,($80)
                    ld ($8052),a
                    ld a,$30
                    out ($82),a     ; error reset
                    ld a,1
                    out ($82),a
                    in a,($82)
                    ld ($8051),a    ; RR1 after
                    pop af
                    ei
                    reti
            arx:    push af
                    in a,($80)
                    ld ($8053),a
                    pop af
                    ei
                    reti
            btx:
            bext:
            brx:
            bsp:
            atx:
            aext:   ei
                    reti
            """, message: "A", delay: 100e-6);
        bench.Run(300e-6);

        Assert.Equal(0x10, bench.Ram(0x8050) & 0x10);
        Assert.Equal(0x00, bench.Ram(0x8051) & 0x10);
        Assert.Equal((byte)'A', bench.Ram(0x8052));
        Assert.Equal(0, bench.Ram(0x8053));
        Assert.Equal(0x2E, bench.Computer.Cpu.Core.LastAcknowledgeByte);
        Assert.Equal("8E1", bench.Sio.Format(false));
    }

    /// <summary>
    /// Five characters with none read: the FIFO keeps three, the third overwritten twice; the one on
    /// top when it is reached carries the overrun into RR1, which holds it until an error reset.
    /// </summary>
    [Fact]
    public void AFourthCharacterOverrunsTheFifo()
    {
        var bench = new Bench("""
            main:   ld b,0
            delay:  djnz delay      ; 832 µs: all five arrive
                    ld hl,$8040
                    in a,($80)
                    ld (hl),a
                    inc hl
                    in a,($80)
                    ld (hl),a
                    inc hl
                    ld a,1
                    out ($82),a
                    in a,($82)
                    ld ($8050),a    ; RR1, the overrun character on top
                    in a,($80)
                    ld (hl),a
                    ld a,$30
                    out ($82),a     ; error reset
                    ld a,1
                    out ($82),a
                    in a,($82)
                    ld ($8051),a
            done:   jr done
            """ + "\n" + Unused, message: "12345", delay: 50e-6);
        bench.Run(1.1e-3);

        Assert.Equal((byte)'1', bench.Ram(0x8040));
        Assert.Equal((byte)'2', bench.Ram(0x8041));
        Assert.Equal((byte)'5', bench.Ram(0x8042));
        Assert.Equal(0x20, bench.Ram(0x8050) & 0x20);
        Assert.Equal(0, bench.Ram(0x8051) & 0x20);
        Assert.Equal("12345", System.Text.Encoding.ASCII.GetString([.. bench.Sio.ReceivedBytes(false)]));
    }

    /// <summary>Receive interrupt mode 01: only the first character interrupts, until "enable interrupt on next character".</summary>
    [Fact]
    public void FirstCharacterModeInterruptsOnce()
    {
        var bench = new Bench("""
            main:   ld a,1
                    out ($82),a
                    ld a,$08        ; WR1 (A): interrupt on the first character
                    out ($82),a
                    ld a,1
                    out ($83),a
                    ld a,$04        ; WR1 (B): status affects vector
                    out ($83),a
                    ei
            done:   jr done
            arx:    push af
                    ld a,($8051)
                    inc a
                    ld ($8051),a
                    pop af
                    ei
                    reti
            btx:
            bext:
            brx:
            bsp:
            atx:
            aext:
            asp:    ei
                    reti
            """, message: "XYZ", delay: 50e-6);
        bench.Run(400e-6);
        Assert.Equal(1, bench.Ram(0x8051));
        Assert.Equal(3, bench.Sio.Waiting(false));
    }

    /// <summary>A break sent: WR5 bit 4 holds TxDA low; cleared, the line marks again.</summary>
    [Fact]
    public void ABreakHoldsTheLineLow()
    {
        var bench = new Bench("""
            main:   ld a,5
                    out ($82),a
                    ld a,$FA        ; WR5: DTR, 8 bits, send break, transmitter on, RTS
                    out ($82),a
                    ld b,0
            hold:   djnz hold
                    ld a,5
                    out ($82),a
                    ld a,$EA
                    out ($82),a
            done:   jr done
            """ + "\n" + Unused);
        bench.Run(200e-6);
        Assert.Equal(LogicState.Low, bench.Pin(bench.Sio.TransmitA));
        bench.Run(800e-6);
        Assert.Equal(LogicState.High, bench.Pin(bench.Sio.TransmitA));
        Assert.True(bench.Terminal.FramingErrors > 0);
    }

    /// <summary>/W/RDYA as a wait on the Z80's /WAIT: a write to a full buffer stalls the processor until the buffer frees, so no polling is needed.</summary>
    [Fact]
    public void WaitHoldsTheProcessorUntilTheBufferFrees()
    {
        var bench = new Bench("""
            main:   ld a,1
                    out ($82),a
                    ld a,$80        ; WR1 (A): /W/RDY on, as a wait, on transmit
                    out ($82),a
                    ld hl,text
                    ld b,4
                    ld c,$80
                    otir            ; four bytes back to back
            done:   jr done
            text:   db "WAIT"
            """ + "\n" + Unused);
        var c = bench.Computer;
        // /W/RDYA onto /WAIT, which the board pulls up.
        c.Circuit.Connect(bench.Sio.WaitReadyA, c.Cpu.Wait);
        bench.Run(600e-6);

        Assert.Equal("WAIT", bench.Terminal.ReceivedText);
        Assert.True(c.Cpu.Core.WaitStates > 100, $"{c.Cpu.Core.WaitStates}");
    }

    /// <summary>/W/RDYB as a ready on transmit: low while the buffer is empty.</summary>
    [Fact]
    public void ReadyShowsTheBufferEmpty()
    {
        var bench = new Bench("""
            main:   ld a,1
                    out ($83),a
                    ld a,$C0        ; WR1 (B): /W/RDY on, as a ready, on transmit
                    out ($83),a
            done:   jr done
            """ + "\n" + Unused);
        bench.Run(200e-6);
        Assert.Equal(LogicState.Low, bench.Pin(bench.Sio.WaitReadyB));
        Assert.Equal(LogicState.HighImpedance, bench.Pin(bench.Sio.WaitReadyA));
    }

    /// <summary>The DART: the same channels, its /RI in RR0's bit 4 and a change of it an external/status interrupt.</summary>
    [Fact]
    public void TheDartsRingIndicator()
    {
        var bench = new Bench("""
            main:   ld a,1
                    out ($82),a
                    ld a,$01
                    out ($82),a
                    ld a,1
                    out ($83),a
                    ld a,$04
                    out ($83),a
                    ld a,6
                    out ($82),a
                    ld a,$55        ; WR6: the DART has none
                    out ($82),a
                    ei
                    ld a,'D'
                    out ($80),a
            done:   jr done
            aext:   push af
                    in a,($82)
                    ld ($8050),a
                    ld a,$10
                    out ($82),a
                    pop af
                    ei
                    reti
            btx:
            bext:
            brx:
            bsp:
            atx:
            arx:
            asp:    ei
                    reti
            """, new Z80Dart());
        bench.Run(400e-6);
        Assert.Equal("D", bench.Terminal.ReceivedText);

        bench.Sync.State = false;       // /RIA low: ringing
        bench.Run(40e-6);
        Assert.Equal(0x10, bench.Ram(0x8050) & 0x10);
        Assert.Equal(0x2A, bench.Computer.Cpu.Core.LastAcknowledgeByte);
        Assert.Equal(0, bench.Sio.WriteRegisterValue(false, 6));
    }
}
