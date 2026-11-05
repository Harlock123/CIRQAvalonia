using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Processors;
using Cirq.Core.Digital;

namespace Cirq.Components.Tests;

/// <summary>
/// The 68901 on a 68000's bus at $A00001, driven by 68000 programs: the GPIP lines and their edge
/// interrupts with the vector register in both end-of-interrupt modes, the four timers in delay,
/// event and pulse-width modes, the USART to a terminal clocked by timer D, and the daisy chain.
/// </summary>
public class Mfp68901Tests
{
    private const string Header = """
        GPDR    equ $A00001
        AER     equ $A00003
        DDR     equ $A00005
        IERA    equ $A00007
        IERB    equ $A00009
        IPRA    equ $A0000B
        IPRB    equ $A0000D
        ISRA    equ $A0000F
        ISRB    equ $A00011
        IMRA    equ $A00013
        IMRB    equ $A00015
        VR      equ $A00017
        TACR    equ $A00019
        TBCR    equ $A0001B
        TCDCR   equ $A0001D
        TADR    equ $A0001F
        TBDR    equ $A00021
        TCDR    equ $A00023
        TDDR    equ $A00025
        UCR     equ $A00029
        RSR     equ $A0002B
        TSR     equ $A0002D
        UDR     equ $A0002F
        RAM     equ $200000
        """;

    // Output indices: I0 = 8, TAO-TDO = 16-19, SO = 20, IEO = 22.
    private const int Gpip = 8, Tao = 16, Tbo = 17, Ieo = 22;

    private static (Cpu68000CircuitTests.Board Board, Mfp68901 Mfp) WithMfp(string body, string vectors = "", bool chainEnabled = true)
    {
        var board = new Cpu68000CircuitTests.Board(Cpu68000CircuitTests.Board.Program(Header + "\n" + body, vectors));
        var circuit = board.Circuit;
        var mfp = circuit.Add(new Mfp68901());
        circuit.Connect(mfp.Vcc, board.Rail.Positive);
        circuit.Connect(mfp.Gnd, board.Ground.Pin);

        var cpu = board.Cpu;
        for (var i = 0; i < 8; i++) circuit.Connect(mfp.Data[i], cpu.Data[i]);
        for (var i = 0; i < 5; i++) circuit.Connect(mfp.RegisterSelect[i], cpu.Address[i]);
        circuit.Connect(mfp.ChipSelect, board.Decoder.Outputs[5]);
        circuit.Connect(mfp.DataStrobe, cpu.LowerDataStrobe);
        circuit.Connect(mfp.ReadWrite, cpu.ReadWrite);
        circuit.Connect(mfp.Dtack, board.ExternalDtack);
        circuit.Connect(mfp.Reset, cpu.Reset);
        circuit.Connect(mfp.Clock, board.Clock.Output);
        circuit.Connect(mfp.InterruptAcknowledge, board.TopRegion);
        circuit.Connect(mfp.InterruptEnableIn, chainEnabled ? board.Ground.Pin : board.Rail.Positive);

        // /IRQ on /IPL1 and /IPL2: level 6, as the Atari ST has it.
        circuit.Connect(mfp.Irq, cpu.InterruptLevel[1]);
        circuit.Connect(mfp.Irq, cpu.InterruptLevel[2]);
        foreach (var pin in mfp.Gpip) board.PullUp(pin);
        board.PullUp(mfp.TimerAInput);
        board.PullUp(mfp.TimerBInput);
        board.PullUp(mfp.SerialIn);
        board.PullUp(mfp.SerialOut);    // high-impedance until the transmitter is set up
        return (board, mfp);
    }

    [Fact]
    public void ThePinoutIsTheDatasheets()
    {
        var mfp = new Mfp68901();
        string At(int pin) => mfp.Terminals.Single(t => t.Id == $"p{pin}").Name;

        Assert.Equal(48, mfp.Terminals.Count);
        Assert.Equal(["RW", "RS1", "RS2", "RS3", "RS4", "RS5", "TC", "SO", "SI", "RC", "VCC", "NC", "TAO", "TBO", "TCO", "TDO"],
            Enumerable.Range(1, 16).Select(At));
        Assert.Equal(["XTAL1", "XTAL2", "TAI", "TBI", "RESET", "I0"], Enumerable.Range(17, 6).Select(At));
        Assert.Equal(["I7", "TR", "RR", "IRQ", "IEO", "IEI", "CLK", "GND", "D0"], Enumerable.Range(29, 9).Select(At));
        Assert.Equal(["D7", "IACK", "DTACK", "DS", "CS"], Enumerable.Range(44, 5).Select(At));
    }

    /// <summary>
    /// GPIP: outputs drive GPDR, inputs read the pins; GPIP4 rising (its AER bit set) interrupts
    /// through vector $46 in software end-of-interrupt mode, the in-service bit held until the
    /// handler clears it.
    /// </summary>
    [Fact]
    public void GpipEdgesInterruptThroughTheVector()
    {
        var (board, mfp) = WithMfp("""
                    move.b #$0F,DDR         ; I0-I3 out, I4-I7 in
                    move.b #$05,GPDR
                    move.b #$10,AER         ; I4 on its rising edge
                    move.b #$40,IERB        ; channel 6, GPIP4
                    move.b #$40,IMRB
                    move.b #$48,VR          ; vectors $40-$4F, software end of interrupt
                    move.b GPDR,RAM
                    move #$2000,sr
            idle:   bra.s idle
            gpip4:  addq.l #1,d7
                    move.b ISRB,RAM+1       ; in service while the handler runs
                    move.b #$BF,ISRB        ; end of interrupt
                    move.b ISRB,RAM+2
                    rte
            """, "        org $118\n        dc.l gpip4");
        var i4 = board.Toggle(mfp.Gpip[4], false);
        board.Toggle(mfp.Gpip[6], false);
        board.Simulator.Run(150e-6);

        Assert.Equal(LogicState.High, mfp.GetOutputState(Gpip));
        Assert.Equal(LogicState.Low, mfp.GetOutputState(Gpip + 1));
        Assert.Equal(LogicState.High, mfp.GetOutputState(Gpip + 2));
        Assert.Equal(0xA5, board.RamByte(0x200000));        // I0-I3 the register, I4 and I6 low, I5 and I7 pulled up
        Assert.Equal(0u, board.Cpu.Core.GetD(7));

        i4.State = true;
        board.Simulator.Run(80e-6);
        Assert.Equal(1u, board.Cpu.Core.GetD(7));
        Assert.Equal(0x46, board.Cpu.Core.LastException);
        Assert.Equal(0x40, board.RamByte(0x200001));
        Assert.Equal(0x00, board.RamByte(0x200002));

        i4.State = false;      // the falling edge is not this line's
        board.Simulator.Run(40e-6);
        Assert.Equal(1u, board.Cpu.Core.GetD(7));
    }

    /// <summary>
    /// Timer A in delay mode, ÷ 4 from the 2.4576 MHz crystal, data 10: TAO toggles every 16.3 µs,
    /// and every time-out interrupts through vector $4D (automatic end of interrupt).
    /// </summary>
    [Fact]
    public void TimerADelayModeInterrupts()
    {
        var (board, mfp) = WithMfp("""
                    move.b #10,TADR
                    move.b #$20,IERA        ; channel 13, timer A
                    move.b #$20,IMRA
                    move.b #$40,VR          ; automatic end of interrupt
                    move.b #$01,TACR        ; delay, ÷ 4
                    move #$2000,sr
            idle:   bra.s idle
            timer:  addq.l #1,d7
                    rte
            """, "        org $134\n        dc.l timer");

        List<double> edges = [];
        board.Simulator.Scheduler.EventApplied += e =>
        {
            if (ReferenceEquals(e.Device, mfp) && e.OutputIndex == Tao) edges.Add(e.Timestamp);
        };
        board.Simulator.Run(300e-6);

        Assert.True(edges.Count > 8);
        Assert.Equal(40 / 2.4576e6, edges[^1] - edges[^2], 8);
        Assert.InRange(board.Cpu.Core.GetD(7), (uint)(edges.Count - 3), (uint)(edges.Count + 1));
        Assert.Equal(0, mfp.InterruptInService);
        Assert.Equal(0x4D, board.Cpu.Core.LastException);
    }

    /// <summary>Timer B counting falling edges on TBI: five of them run it out, toggling TBO and setting its pending bit.</summary>
    [Fact]
    public void TimerBCountsEvents()
    {
        var (board, mfp) = WithMfp("""
                    move.b #5,TBDR
                    move.b #$01,IERA        ; channel 8, timer B
                    move.b #$08,TBCR        ; event count
            idle:   bra.s idle
            """);
        var tbi = board.Toggle(mfp.TimerBInput);
        board.Simulator.Run(100e-6);

        for (var i = 0; i < 4; i++)
        {
            tbi.State = false;
            board.Simulator.Run(2e-6);
            tbi.State = true;
            board.Simulator.Run(2e-6);
        }

        Assert.Equal(1, mfp.TimerState(1).Count);
        Assert.Equal(LogicState.Low, mfp.GetOutputState(Tbo));
        tbi.State = false;
        board.Simulator.Run(2e-6);

        Assert.Equal(5, mfp.TimerState(1).Count);
        Assert.Equal(1, mfp.TimerState(1).Timeouts);
        Assert.Equal(LogicState.High, mfp.GetOutputState(Tbo));
        Assert.Equal(0x0100, mfp.InterruptPending);
    }

    /// <summary>Timer A in pulse-width mode: it counts the prescaled crystal only while TAI is high, and the pulse's end is GPIP4's event.</summary>
    [Fact]
    public void TimerAMeasuresAPulse()
    {
        var (board, mfp) = WithMfp("""
                    move.b #200,TADR
                    move.b #$40,IERB        ; channel 6, GPIP4: the end of the pulse
                    move.b #$0A,TACR        ; pulse width, ÷ 10
            idle:   bra.s idle
            """);
        var tai = board.Toggle(mfp.TimerAInput, false);
        board.Simulator.Run(100e-6);
        Assert.Equal(200, mfp.TimerState(0).Count);

        tai.State = true;
        board.Simulator.Run(40e-6);           // 40 us of 245.76 kHz: about 9.8 counts
        tai.State = false;
        board.Simulator.Run(40e-6);

        Assert.InRange(mfp.TimerState(0).Count, 189, 191);
        Assert.Equal(0x0040, mfp.InterruptPending);
    }

    /// <summary>
    /// The USART at 19,200 baud, ÷ 16 from TDO (timer D, ÷ 4, data 1) on TC and RC: two characters
    /// to a terminal, and one back, its arrival a vectored receive-buffer-full interrupt.
    /// </summary>
    [Fact]
    public void TheUsartTalksToATerminalOnTimerD()
    {
        var (board, mfp) = WithMfp("""
                    move.b #1,TDDR
                    move.b #$01,TCDCR       ; timer D: delay, ÷ 4
                    move.b #$88,UCR         ; ÷ 16, eight bits, one stop bit, no parity
                    move.b #$01,RSR         ; receiver on
                    move.b #$05,TSR         ; transmitter on, SO high when it is off
                    move.b #$10,IERA        ; channel 12, receive buffer full
                    move.b #$10,IMRA
                    move.b #$40,VR
                    move #$2000,sr
                    lea text(pc),a0
            send:   move.b (a0)+,d0
                    beq.s idle
            wait:   btst #7,TSR             ; buffer empty
                    beq.s wait
                    move.b d0,UDR
                    bra.s send
            idle:   bra.s idle
            receive: move.b RSR,RAM
                    move.b UDR,RAM+1
                    rte
            text:   dc.b "OK",0
            """, "        org $130\n        dc.l receive");
        var terminal = board.Circuit.Add(new SerialTerminal { BaudRate = 19200, Message = "x", StartDelay = 1.3e-3 });
        board.Circuit.Connect(terminal.Vcc, board.Rail.Positive);
        board.Circuit.Connect(terminal.Gnd, board.Ground.Pin);
        board.Circuit.Connect(mfp.SerialOut, terminal.Receive);
        board.Circuit.Connect(terminal.Transmit, mfp.SerialIn);
        board.Circuit.Connect(mfp.TimerOutputs[3], mfp.TransmitClock);
        board.Circuit.Connect(mfp.TimerOutputs[3], mfp.ReceiveClock);

        board.Simulator.Run(2e-3);

        Assert.Equal("OK", terminal.ReceivedText);
        Assert.Equal(0, terminal.FramingErrors);
        Assert.Equal(0x80, board.RamByte(0x200000) & 0xF0);     // buffer full, no errors
        Assert.Equal((byte)'x', board.RamByte(0x200001));
        Assert.Equal(0x4C, board.Cpu.Core.LastException);
    }

    /// <summary>With IEI high, an acknowledge is not this chip's: it passes it on by taking IEO low, and answers nothing.</summary>
    [Fact]
    public void AChipLaterInTheChainPassesTheAcknowledgeOn()
    {
        var (board, mfp) = WithMfp("""
                    move.b #$01,IERB        ; channel 0, GPIP0
                    move.b #$01,IMRB
                    move.b #$40,VR
                    move #$2000,sr
            idle:   bra.s idle
            """, chainEnabled: false);
        var i0 = board.Toggle(mfp.Gpip[0]);

        List<LogicState> ieo = [];
        board.Simulator.Scheduler.EventApplied += e =>
        {
            if (ReferenceEquals(e.Device, mfp) && e.OutputIndex == Ieo) ieo.Add(e.NewState);
        };
        board.Simulator.Run(100e-6);
        i0.State = false;
        board.Simulator.Run(20e-6);

        Assert.Contains(LogicState.Low, ieo);
        Assert.Equal(0x0001, mfp.InterruptPending);       // still pending: nobody answered for it
    }
}
