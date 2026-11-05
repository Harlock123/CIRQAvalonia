using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Processors;
using Cirq.Core.Digital;

namespace Cirq.Components.Tests;

/// <summary>
/// The 68681 on a 68000's bus at $C00001, every register and mode driven by a 68000 program: both
/// serial channels to a terminal and looped back, the baud-rate sets, errors and breaks, the
/// counter/timer as a timer on OP3 and as a counter, the output port commands and OPCR, the input
/// port and IPCR, and a vectored interrupt through /IACK.
/// </summary>
public class Duart68681Tests
{
    private const string Header = """
        MRA     equ $C00001
        SRA     equ $C00003
        CSRA    equ $C00003
        CRA     equ $C00005
        RHRA    equ $C00007
        THRA    equ $C00007
        IPCR    equ $C00009
        ACR     equ $C00009
        ISR     equ $C0000B
        IMR     equ $C0000B
        CUR     equ $C0000D
        CTUR    equ $C0000D
        CLR     equ $C0000F
        CTLR    equ $C0000F
        MRB     equ $C00011
        SRB     equ $C00013
        CSRB    equ $C00013
        CRB     equ $C00015
        RHRB    equ $C00017
        THRB    equ $C00017
        IVR     equ $C00019
        IP      equ $C0001B
        OPCR    equ $C0001B
        CTGO    equ $C0001D
        OPSET   equ $C0001D
        CTSTOP  equ $C0001F
        OPCLR   equ $C0001F
        RAM     equ $200000
        """;

    /// <summary>The board at 1 MHz with a DUART at $C00001: RS1-RS4 on A1-A4, D0-D7, /DTACK into the board's.</summary>
    private static (Cpu68000CircuitTests.Board Board, Duart68681 Duart) WithDuart(string body, string vectors = "")
    {
        var board = new Cpu68000CircuitTests.Board(Cpu68000CircuitTests.Board.Program(Header + "\n" + body, vectors), clock: 1e6);
        var circuit = board.Circuit;
        var duart = circuit.Add(new Duart68681());
        circuit.Connect(duart.Vcc, board.Rail.Positive);
        circuit.Connect(duart.Gnd, board.Ground.Pin);

        var cpu = board.Cpu;
        for (var i = 0; i < 8; i++) circuit.Connect(duart.Data[i], cpu.Data[i]);
        for (var i = 0; i < 4; i++) circuit.Connect(duart.RegisterSelect[i], cpu.Address[i]);
        circuit.Connect(duart.ChipSelect, board.IoSelect);
        circuit.Connect(duart.ReadWrite, cpu.ReadWrite);
        circuit.Connect(duart.Dtack, board.ExternalDtack);
        circuit.Connect(duart.Reset, cpu.Reset);
        board.PullUp(duart.Interrupt);
        board.PullUp(duart.InterruptAcknowledge);
        foreach (var ip in duart.InputPort) board.PullUp(ip);
        board.PullUp(duart.ReceiveA);
        board.PullUp(duart.ReceiveB);
        return (board, duart);
    }

    [Fact]
    public void ThePinoutIsTheDatasheets()
    {
        var duart = new Duart68681();
        string At(int pin) => duart.Terminals.Single(t => t.Id == $"p{pin}").Name;

        Assert.Equal(40, duart.Terminals.Count);
        Assert.Equal(["RS1", "IP3", "RS2", "IP1", "RS3", "RS4", "IP0", "RW", "DTACK", "RxDB", "TxDB"], Enumerable.Range(1, 11).Select(At));
        Assert.Equal(["D1", "D3", "D5", "D7", "GND", "INTR", "D6", "D4", "D2", "D0"], Enumerable.Range(16, 10).Select(At));
        Assert.Equal(["TxDA", "RxDA", "X1", "X2", "RESET", "CS", "IP2", "IACK", "IP5", "IP4", "VCC"], Enumerable.Range(30, 11).Select(At));
        Assert.Equal("/DTACK", duart.PinLabel(duart.Dtack));
    }

    /// <summary>Channel A at 38,400 baud to a terminal: a greeting out, and what the terminal sends read into RAM.</summary>
    [Fact]
    public void ChannelATalksToATerminal()
    {
        var (board, duart) = WithDuart("""
                    move.b #$10,CRA         ; MR pointer to MR1
                    move.b #$13,MRA         ; MR1: no parity, eight bits
                    move.b #$07,MRA         ; MR2: normal, one stop bit
                    move.b #$CC,CSRA        ; 38,400 both ways (set 1)
                    move.b #$05,CRA         ; transmitter and receiver on
                    lea text(pc),a0
            send:   move.b (a0)+,d0
                    beq.s listen
            wait:   btst #2,SRA             ; TxRDY
                    beq.s wait
                    move.b d0,THRA
                    bra.s send
            listen: lea RAM,a1
            poll:   btst #0,SRA             ; RxRDY
                    beq.s poll
                    move.b RHRA,(a1)+
                    bra.s poll
            text:   dc.b "Hi!",0
            """);
        var terminal = board.Circuit.Add(new SerialTerminal { BaudRate = 38400, Message = "ok", StartDelay = 0.3e-3 });
        board.Circuit.Connect(terminal.Vcc, board.Rail.Positive);
        board.Circuit.Connect(terminal.Gnd, board.Ground.Pin);
        board.Circuit.Connect(duart.TransmitA, terminal.Receive);
        board.Circuit.Connect(terminal.Transmit, duart.ReceiveA);

        board.Simulator.Run(1.2e-3);

        Assert.Equal("Hi!", terminal.ReceivedText);
        Assert.Equal(0, terminal.FramingErrors);
        Assert.Equal("Hi!", duart.TransmittedTextA);
        Assert.Equal((byte)'o', board.RamByte(0x200000));
        Assert.Equal((byte)'k', board.RamByte(0x200001));
        Assert.Equal(38400, duart.BaudRateA);
        Assert.Equal(0, board.Cpu.UndefinedReads);
    }

    /// <summary>
    /// Channel B in local loopback, seven bits with even parity and two stop bits, at set 2's 19,200:
    /// what goes out comes straight back, and the FIFO holds three before overrunning.
    /// </summary>
    [Fact]
    public void ChannelBLoopsBackLocallyAndOverruns()
    {
        var (board, duart) = WithDuart("""
                    move.b #$80,ACR         ; baud set 2
                    move.b #$10,CRB
                    move.b #$02,MRB         ; MR1: with parity, even, seven bits
                    move.b #$8F,MRB         ; MR2: local loopback, two stop bits
                    move.b #$CC,CSRB        ; 19,200 (set 2)
                    move.b #$05,CRB
                    moveq #'A',d0
                    moveq #3,d1             ; four characters, one more than the FIFO holds
            send:   btst #2,SRB
                    beq.s send
                    move.b d0,THRB
                    addq.b #1,d0
                    dbra d1,send
            drain:  btst #3,SRB             ; TxEMT: all gone
                    beq.s drain
                    move.w #200,d2
            delay:  dbra d2,delay
                    move.b SRB,RAM+8
                    lea RAM,a1
            read:   btst #0,SRB
                    beq.s done
                    move.b RHRB,(a1)+
                    bra.s read
            done:   move.b SRB,RAM+9
                    move.b #$40,CRB         ; reset error status
                    move.b SRB,RAM+10
            idle:   bra.s idle
            """);
        board.Simulator.Run(5e-3);

        Assert.Equal(19200, duart.BaudRateB);
        Assert.Equal("ABCD", System.Text.Encoding.ASCII.GetString([.. duart.TransmittedB]));
        Assert.True(duart.TransmitB is not null);
        Assert.Equal([(byte)'A', (byte)'B', (byte)'C'], new[] { board.RamByte(0x200000), board.RamByte(0x200001), board.RamByte(0x200002) });
        Assert.Equal(0x1F, board.RamByte(0x200008) & 0x1F);      // overrun, TxEMT, TxRDY, FIFO full, RxRDY
        Assert.Equal(0x1C, board.RamByte(0x200009) & 0x1F);      // still overrun, the FIFO empty
        Assert.Equal(0x0C, board.RamByte(0x20000A) & 0x1F);      // overrun cleared
    }

    /// <summary>The terminal at a quarter of the rate: its NUL is still low at the stop bit, a framing error and a break.</summary>
    [Fact]
    public void AMismatchedRateGivesFramingErrors()
    {
        var (board, duart) = WithDuart("""
                    move.b #$10,CRA
                    move.b #$13,MRA
                    move.b #$07,MRA
                    move.b #$BB,CSRA        ; 9,600
                    move.b #$01,CRA         ; receiver only
                    lea RAM,a1
            poll:   move.b SRA,d0
                    btst #0,d0
                    beq.s poll
                    move.b d0,(a1)+
                    move.b RHRA,d1
                    bra.s poll
            """);
        var terminal = board.Circuit.Add(new SerialTerminal { BaudRate = 2400, Message = "\0", StartDelay = 0.2e-3 });
        board.Circuit.Connect(terminal.Vcc, board.Rail.Positive);
        board.Circuit.Connect(terminal.Gnd, board.Ground.Pin);
        board.Circuit.Connect(terminal.Transmit, duart.ReceiveA);

        board.Simulator.Run(2.5e-3);

        Assert.NotEmpty(duart.ReceivedA);
        Assert.Equal(0x80, board.RamByte(0x200000) & 0xC0);      // received break: every bit low
        Assert.Equal(0, duart.ReceivedA[0]);
    }

    /// <summary>
    /// The counter/timer as a timer from the crystal ÷ 16: a square wave on OP3 of period
    /// 2 × preload, and the ready flag in ISR that the stop command clears.
    /// </summary>
    [Fact]
    public void TheTimerMakesASquareWaveOnOp3()
    {
        var (board, duart) = WithDuart("""
                    move.b #0,CTUR
                    move.b #12,CTLR         ; 12 counts of 3.6864 MHz / 16: 52 us
                    move.b #$04,OPCR        ; OP3 the counter/timer's output
                    move.b #$70,ACR         ; timer, crystal / 16
                    move.b CTGO,d0
            wait:   btst #3,ISR
                    beq.s wait
                    move.b ISR,RAM
                    move.b CTSTOP,d0          ; clears the ready flag
                    move.b ISR,RAM+1
            idle:   bra.s idle
            """);

        List<double> rises = [];
        board.Simulator.Scheduler.EventApplied += e =>
        {
            if (ReferenceEquals(e.Device, duart) && e.OutputIndex == 13 && e.NewState == LogicState.High) rises.Add(e.Timestamp);
        };
        board.Simulator.Run(0.8e-3);

        Assert.True(rises.Count > 5);
        var period = 2 * 12 * 16 / 3.6864e6;
        Assert.Equal(period, rises[^1] - rises[^2], 7);
        Assert.Equal(0x08, board.RamByte(0x200000) & 0x08);
        Assert.Equal(0, board.RamByte(0x200001) & 0x08);
        Assert.True(duart.CounterRunning);
    }

    /// <summary>The counter: started from the preload, read on the way down, stopped, and counting the transmitter's clock.</summary>
    [Fact]
    public void TheCounterCountsDownUntilStopped()
    {
        var (board, duart) = WithDuart("""
                    move.b #$FF,CTUR
                    move.b #$FF,CTLR
                    move.b #$30,ACR         ; counter, crystal / 16
                    move.b CTGO,d0
                    move.w #50,d1
            delay:  dbra d1,delay
                    move.b CUR,RAM
                    move.b CLR,RAM+1
                    move.b CTSTOP,d0
                    move.b CUR,RAM+2
                    move.b CLR,RAM+3
                    move.w #50,d1
            delay2: dbra d1,delay2
                    move.b CUR,RAM+4
                    move.b CLR,RAM+5
            idle:   bra.s idle
            """);
        board.Simulator.Run(1.5e-3);

        int Word(uint at) => (board.RamByte(at) << 8) | board.RamByte(at + 1);
        Assert.InRange(Word(0x200000), 0xFF00, 0xFFF8);       // a few hundred microseconds of 230 kHz counts gone
        Assert.Equal(Word(0x200002), Word(0x200004));          // stopped
        Assert.False(duart.CounterRunning);
    }

    /// <summary>The output port: set and reset commands drive the pins low and high again; OPCR gives OP6 to /TxRDYA.</summary>
    [Fact]
    public void TheOutputPortCommandsAndOpcr()
    {
        var (board, duart) = WithDuart("""
                    move.b #$A5,OPSET       ; OP0, OP2, OP5 and OP7 low
                    move.w #20,d1
            w1:     dbra d1,w1
                    move.b #$05,OPCLR       ; OP0 and OP2 high again
                    move.b #$40,OPCR        ; OP6 is /TxRDYA
                    move.b #$04,CRA         ; transmitter on: TxRDY, so OP6 low
            idle:   bra.s idle
            """);

        board.Simulator.Run(400e-6);

        LogicState Op(int i) => duart.GetOutputState(10 + i);
        Assert.Equal(0xA0, duart.OutputPortRegister);
        Assert.Equal(LogicState.High, Op(0));
        Assert.Equal(LogicState.High, Op(2));
        Assert.Equal(LogicState.Low, Op(5));
        Assert.Equal(LogicState.Low, Op(7));
        Assert.Equal(LogicState.Low, Op(6));
        Assert.Equal(LogicState.High, Op(1));
    }

    /// <summary>
    /// The input port read at its pins; IPCR's change flags, cleared by reading it; and IP1's change,
    /// enabled in ACR, as the interrupt that /INTR raises — taken by the 68000 as a vectored
    /// interrupt with the DUART answering /IACK with IVR.
    /// </summary>
    [Fact]
    public void AnInputChangeInterruptsWithTheVector()
    {
        var (board, duart) = WithDuart("""
                    move.b #$42,IVR         ; vector 66
                    move.b #$02,ACR         ; IP1's change can interrupt
                    move.b #$80,IMR         ; the input port change
                    move.b IPCR,d0          ; clear any flags
                    move #$2000,sr
                    move.b IP,RAM
            idle:   bra.s idle
            handler:
                    addq.l #1,d7
                    move.b IPCR,RAM+1       ; the flags, and clears them
                    move.b IP,RAM+2
                    rte
            """, "        org $108\n        dc.l handler");

        // /INTR on /IPL2 alone: level 4. /IACK from the 74138's Y7, which the acknowledge's address is in.
        board.Circuit.Connect(duart.Interrupt, board.Cpu.InterruptLevel[2]);
        board.Circuit.Connect(duart.InterruptAcknowledge, board.TopRegion);
        var ip1 = board.Toggle(duart.InputPort[1]);
        board.Simulator.Run(250e-6);
        Assert.Equal(0x3F, board.RamByte(0x200000) & 0x3F);
        Assert.Equal(0u, board.Cpu.Core.GetD(7));

        ip1.State = false;
        board.Simulator.Run(150e-6);

        Assert.Equal(1u, board.Cpu.Core.GetD(7));
        Assert.Equal(66, board.Cpu.Core.LastException);
        Assert.Equal(0x20, board.RamByte(0x200001) & 0x22);     // IP1 changed, and is low now
        Assert.Equal(0x3D, board.RamByte(0x200002) & 0x3F);
        Assert.Equal(0, duart.InterruptStatus & 0x80);
        Assert.Contains(board.Cpu.BusLog, c => c.Kind == M68000CycleKind.InterruptAcknowledge && c.Data == 0x42);
    }

    /// <summary>A break sent in local loopback is received as a null with the received-break bit, and sets the break-change interrupt.</summary>
    [Fact]
    public void ABreakIsReceived()
    {
        var (board, duart) = WithDuart("""
                    move.b #$10,CRA
                    move.b #$13,MRA
                    move.b #$87,MRA         ; local loopback
                    move.b #$CC,CSRA
                    move.b #$05,CRA
                    move.b #$60,CRA         ; start a break
                    move.w #60,d1
            w1:     dbra d1,w1
                    move.b #$70,CRA         ; stop it
            poll:   btst #0,SRA
                    beq.s poll
                    move.b SRA,RAM
                    move.b ISR,RAM+1
                    move.b #$50,CRA         ; reset the break-change interrupt
                    move.b ISR,RAM+2
            idle:   bra.s idle
            """);
        board.Simulator.Run(1e-3);

        Assert.Equal(0x80, board.RamByte(0x200000) & 0x80);
        Assert.Equal(0x04, board.RamByte(0x200001) & 0x04);
        Assert.Equal(0, board.RamByte(0x200002) & 0x04);
        Assert.Equal(0, duart.ReceivedA[0]);
    }
}
