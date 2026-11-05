using Cirq.Components.Digital;
using Cirq.Components.Processors;
using Cirq.Core.Digital;

namespace Cirq.Components.Tests;

/// <summary>
/// The 68230 on a 68000's bus at $A00001, driven by 68000 programs: bit I/O, double-buffered input
/// and output with their handshakes, the 16-bit mode, port C, the timer's square wave and its
/// vectored interrupt, and a vectored port interrupt.
/// </summary>
public class Pit68230Tests
{
    private const string Header = """
        PGCR    equ $A00001
        PSRR    equ $A00003
        PADDR   equ $A00005
        PBDDR   equ $A00007
        PCDDR   equ $A00009
        PIVR    equ $A0000B
        PACR    equ $A0000D
        PBCR    equ $A0000F
        PADR    equ $A00011
        PBDR    equ $A00013
        PAAR    equ $A00015
        PBAR    equ $A00017
        PCDR    equ $A00019
        PSR     equ $A0001B
        TCR     equ $A00021
        TIVR    equ $A00023
        CPRH    equ $A00027
        CPRM    equ $A00029
        CPRL    equ $A0002B
        CNTRL   equ $A00033
        TSR     equ $A00035
        RAM     equ $200000
        """;

    // Output indices on the part: PA0 = 8, PB0 = 16, PC0 = 24, H2 = 32, H4 = 33.
    private const int Pa = 8, Pb = 16, Pc = 24, H2 = 32, H4 = 33;

    private static (Cpu68000CircuitTests.Board Board, Pit68230 Pit) WithPit(string body, string vectors = "", double clock = 4e6)
    {
        var board = new Cpu68000CircuitTests.Board(Cpu68000CircuitTests.Board.Program(Header + "\n" + body, vectors), clock: clock);
        var circuit = board.Circuit;
        var pit = circuit.Add(new Pit68230());
        circuit.Connect(pit.Vcc, board.Rail.Positive);
        circuit.Connect(pit.Gnd, board.Ground.Pin);

        var cpu = board.Cpu;
        for (var i = 0; i < 8; i++) circuit.Connect(pit.Data[i], cpu.Data[i]);
        for (var i = 0; i < 5; i++) circuit.Connect(pit.RegisterSelect[i], cpu.Address[i]);
        circuit.Connect(pit.ChipSelect, board.Io2Select);
        circuit.Connect(pit.ReadWrite, cpu.ReadWrite);
        circuit.Connect(pit.Dtack, board.ExternalDtack);
        circuit.Connect(pit.Reset, cpu.Reset);
        circuit.Connect(pit.Clock, board.Clock.Output);
        foreach (var pin in pit.PortC) board.PullUp(pin);
        return (board, pit);
    }

    private static int Byte(Pit68230 pit, int first)
    {
        var value = 0;
        for (var i = 0; i < 8; i++)
            if (pit.GetOutputState(first + i) == LogicState.High) value |= 1 << i;
        return value;
    }

    private static List<LogicToggle> Drive(Cpu68000CircuitTests.Board board, IReadOnlyList<Cirq.Core.Topology.Terminal> pins, int value) =>
        [.. pins.Select((p, i) => board.Toggle(p, (value & (1 << i)) != 0))];

    private static void Set(List<LogicToggle> toggles, int value)
    {
        for (var i = 0; i < toggles.Count; i++) toggles[i].State = (value & (1 << i)) != 0;
    }

    [Fact]
    public void ThePinoutIsTheDatasheets()
    {
        var pit = new Pit68230();
        string At(int pin) => pit.Terminals.Single(t => t.Id == $"p{pin}").Name;

        Assert.Equal(48, pit.Terminals.Count);
        Assert.Equal(["D5", "D6", "D7", "PA0"], Enumerable.Range(1, 4).Select(At));
        Assert.Equal(["PA7", "VCC", "H1", "H2", "H3", "H4", "PB0"], Enumerable.Range(11, 7).Select(At));
        Assert.Equal(["PB7", "RS5", "RS4", "RS3", "RS2", "RS1", "PC0", "PC1", "PC2/TIN", "PC3/TOUT"], Enumerable.Range(24, 10).Select(At));
        Assert.Equal(["PC7/TIACK", "GND", "RESET", "CLK", "CS", "DTACK", "RW", "D0", "D1", "D2", "D3", "D4"], Enumerable.Range(37, 12).Select(At));
    }

    /// <summary>Bit I/O: port A out, port B in, the alternate registers at the pins, H1's edge as a status that a written 1 clears.</summary>
    [Fact]
    public void BitIoAndEdgeStatus()
    {
        var (board, pit) = WithPit("""
                    move.b #$10,PGCR        ; H12 on, every H line active low
                    move.b #$80,PACR        ; port A: bit I/O
                    move.b #$80,PBCR
                    move.b #$FF,PADDR
                    move.b #$00,PBDDR
                    move.b #$5A,PADR
                    move.b PBDR,RAM
                    move.b PAAR,RAM+1
            wait:   btst #0,PSR
                    beq.s wait
                    move.b PSR,RAM+2
                    move.b #$01,PSR
                    move.b PSR,RAM+3
            idle:   bra.s idle
            """);
        Drive(board, pit.PortB, 0xC3);
        var h1 = board.Toggle(pit.Handshake[0]);
        board.Simulator.Run(200e-6);

        Assert.Equal(0x5A, Byte(pit, Pa));
        Assert.Equal(0xC3, board.RamByte(0x200000));
        Assert.Equal(0x5A, board.RamByte(0x200001));

        h1.State = false;
        board.Simulator.Run(100e-6);
        Assert.Equal(0x11, board.RamByte(0x200002) & 0x11);     // H1 asserted, H1S
        Assert.Equal(0x00, board.RamByte(0x200003) & 0x01);     // cleared
    }

    /// <summary>
    /// Double-buffered input with the interlocked handshake: H1 latches each byte, H2 says ready
    /// until both buffers are full, and the 68000 reads the two in order.
    /// </summary>
    [Fact]
    public void DoubleBufferedInputWithTheInterlockedHandshake()
    {
        var (board, pit) = WithPit("""
                    move.b #$10,PGCR
                    move.b #$30,PACR        ; submode 00, H2 interlocked
                    move.b #$00,PADDR
                    move.b #$00,RAM+4
            go:     tst.b RAM+4             ; the test says when to read
                    beq.s go
                    move.b PADR,RAM
                    move.b PADR,RAM+1
            idle:   bra.s idle
            """);
        var pa = Drive(board, pit.PortA, 0x3C);
        var h1 = board.Toggle(pit.Handshake[0]);
        board.Simulator.Run(100e-6);
        Assert.Equal(LogicState.Low, pit.GetOutputState(H2));    // ready, active low

        h1.State = false;
        board.Simulator.Run(5e-6);
        h1.State = true;
        Set(pa, 0xC3);
        board.Simulator.Run(5e-6);
        h1.State = false;
        board.Simulator.Run(5e-6);
        h1.State = true;
        board.Simulator.Run(5e-6);
        Assert.Equal(LogicState.High, pit.GetOutputState(H2));   // both buffers full
        Assert.Equal([0x3C, 0xC3], pit.LatchedA);

        board.RamOdd.WriteByte(2, 1);       // $200005
        board.RamEven.WriteByte(2, 1);      // $200004
        board.Simulator.Run(100e-6);
        Assert.Equal(0x3C, board.RamByte(0x200000));
        Assert.Equal(0xC3, board.RamByte(0x200001));
        Assert.Equal(LogicState.Low, pit.GetOutputState(H2));    // room again
    }

    /// <summary>Double-buffered output: two bytes queued, each shown on the pins until H1 acknowledges it, H2 asserted while one is there.</summary>
    [Fact]
    public void DoubleBufferedOutputWithTheInterlockedHandshake()
    {
        var (board, pit) = WithPit("""
                    move.b #$10,PGCR
                    move.b #$70,PACR        ; submode 01, H2 interlocked
                    move.b #$FF,PADDR
                    move.b #$11,PADR
                    move.b #$22,PADR
                    move.b PSR,RAM          ; H1S: no room now
            idle:   bra.s idle
            """);
        var h1 = board.Toggle(pit.Handshake[0]);
        board.Simulator.Run(150e-6);

        Assert.Equal(0x11, Byte(pit, Pa));
        Assert.Equal(LogicState.Low, pit.GetOutputState(H2));
        Assert.Equal(0, board.RamByte(0x200000) & 0x01);

        h1.State = false;
        board.Simulator.Run(5e-6);
        h1.State = true;
        board.Simulator.Run(5e-6);
        Assert.Equal(0x22, Byte(pit, Pa));

        h1.State = false;
        board.Simulator.Run(5e-6);
        Assert.Equal(LogicState.High, pit.GetOutputState(H2));   // nothing more to send
        Assert.Equal(0x01, pit.PortStatus & 0x01);
    }

    /// <summary>Mode 1: ports A and B one 16-bit port, A the high byte, the write to PBDR starting port B's handshake on H4.</summary>
    [Fact]
    public void SixteenBitModeUsesPortBsHandshake()
    {
        var (board, pit) = WithPit("""
                    move.b #$60,PGCR        ; mode 1, H34 and H12 on
                    move.b #$70,PBCR        ; output, H4 interlocked
                    move.b #$FF,PADDR
                    move.b #$FF,PBDDR
                    move.b #$12,PADR
                    move.b #$34,PBDR
            idle:   bra.s idle
            """);
        var h3 = board.Toggle(pit.Handshake[2]);
        board.Simulator.Run(150e-6);

        Assert.Equal(0x12, Byte(pit, Pa));
        Assert.Equal(0x34, Byte(pit, Pb));
        Assert.Equal(LogicState.Low, pit.GetOutputState(H4));
        h3.State = false;
        board.Simulator.Run(5e-6);
        Assert.Equal(LogicState.High, pit.GetOutputState(H4));
    }

    [Fact]
    public void PortCIsGeneralBits()
    {
        var (board, pit) = WithPit("""
                    move.b #$03,PCDDR       ; PC0 and PC1 out
                    move.b #$02,PCDR
                    move.b PCDR,RAM
            idle:   bra.s idle
            """);
        board.Toggle(pit.PortC[4], false);
        board.Simulator.Run(100e-6);

        Assert.Equal(LogicState.Low, pit.GetOutputState(Pc));
        Assert.Equal(LogicState.High, pit.GetOutputState(Pc + 1));
        Assert.Equal(0xEE, board.RamByte(0x200000));    // PC1 the register, PC4 low, the rest pulled up
    }

    /// <summary>The timer from CLK ÷ 32: TOUT a square wave toggling every preload × 32 clocks, the count read back, ZDS set.</summary>
    [Fact]
    public void TheTimerMakesASquareWaveOnTout()
    {
        var (board, pit) = WithPit("""
                    move.b #0,CPRH
                    move.b #0,CPRM
                    move.b #10,CPRL
                    move.b #$41,TCR         ; TOUT a square wave, CLK / 32, on
            idle:   bra.s idle
            """, clock: 4e6);

        List<double> edges = [];
        board.Simulator.Scheduler.EventApplied += e =>
        {
            if (ReferenceEquals(e.Device, pit) && e.OutputIndex == Pc + 3) edges.Add(e.Timestamp);
        };
        board.Simulator.Run(500e-6);

        Assert.True(edges.Count > 4);
        Assert.Equal(10 * 32 * 0.25e-6, edges[^1] - edges[^2], 8);
        Assert.True(pit.ZeroDetected);
        Assert.InRange(pit.TimerCount, 1, 10);
    }

    /// <summary>
    /// The timer's interrupt on TOUT as /TIRQ, acknowledged on /TIACK with TIVR's vector; the handler
    /// clears ZDS. And H1 as a port interrupt on /PIRQ, acknowledged on /PIACK with PIVR and source 00.
    /// </summary>
    [Fact]
    public void TimerAndPortInterruptsAreVectored()
    {
        var (board, pit) = WithPit("""
                    move.b #$48,TIVR        ; vector 72
                    move.b #$50,PIVR        ; vectors 80-83
                    move.b #$18,PSRR        ; PC5 /PIRQ, PC6 /PIACK
                    move.b #$10,PGCR
                    move.b #$82,PACR        ; bit I/O, H1 interrupts
                    move.b #0,CPRH
                    move.b #0,CPRM
                    move.b #20,CPRL
                    move.b #$A1,TCR         ; /TIRQ and /TIACK, interrupt on, CLK / 32, on
                    move #$2000,sr
            idle:   bra.s idle
            timer:  addq.l #1,d7
                    move.b #1,TSR
                    rte
            port:   addq.l #1,d6
                    move.b #1,PSR
                    rte
            """, "        org $120\n        dc.l timer\n        org $140\n        dc.l port", clock: 4e6);

        // /TIRQ and /PIRQ (open drain) on /IPL2 and /IPL1: levels 4 and 2. Both acknowledges from Y7.
        board.Circuit.Connect(pit.PortC[3], board.Cpu.InterruptLevel[2]);
        board.Circuit.Connect(pit.PortC[5], board.Cpu.InterruptLevel[1]);
        board.Circuit.Connect(pit.PortC[7], board.TopRegion);
        board.Circuit.Connect(pit.PortC[6], board.TopRegion);
        var h1 = board.Toggle(pit.Handshake[0]);
        board.Simulator.Run(600e-6);

        Assert.True(board.Cpu.Core.GetD(7) >= 2);
        Assert.Contains(board.Cpu.BusLog, c => c.Kind == M68000CycleKind.InterruptAcknowledge && c.Data == 0x48);

        h1.State = false;
        board.Simulator.Run(100e-6);
        Assert.Equal(1u, board.Cpu.Core.GetD(6));
        Assert.Contains(board.Cpu.BusLog, c => c.Kind == M68000CycleKind.InterruptAcknowledge && c.Data == 0x50);
    }
}
