using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Processors;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Tests;

/// <summary>
/// The 8251A USART on an 8085's bus, driven by 8085 programs: the internal reset sequence, the
/// mode and command instructions, 8N1 at x16 to and from the serial terminal, x1 and x64, 7E2
/// looped back, parity and framing errors, overrun and error reset, a break sent and detected,
/// the TxRDY and TxEMPTY pins with /CTS, DSR in the status, /DTR and /RTS, and RESET.
/// </summary>
public class Usart8251Tests
{
    /// <summary>The 8085 board with an 8251A at ports $40 (data) and $41 (control): /CS from ports $40-$5F, C/D on the latched A0.</summary>
    private sealed class Bench
    {
        public Cpu8085CircuitTests.Computer Computer { get; }

        public Usart8251 Usart { get; }

        public SerialTerminal? Terminal { get; }

        public LogicToggle Cts { get; }

        public LogicToggle Dsr { get; }

        public LogicToggle Reset { get; }

        /// <param name="source">The program.</param>
        /// <param name="serialClock">The /TxC and /RxC clock: 1.8432 MHz is 115,200 baud at x16.</param>
        /// <param name="terminalBaud">A terminal on TxD and RxD at this rate; 0 loops TxD back to RxD instead.</param>
        public Bench(string source, double serialClock = 1.8432e6, double terminalBaud = 115200, string message = "", double startDelay = 1,
            UartParity parity = UartParity.None, bool ctsHigh = false)
        {
            Computer = new Cpu8085CircuitTests.Computer(Cpu8085CircuitTests.Rom(source));
            var c = Computer;
            Usart = c.Ic(new Usart8251());
            for (var i = 0; i < 8; i++) c.Circuit.Connect(Usart.Data[i], c.Cpu.AddressData[i]);
            c.Circuit.Connect(Usart.ControlData, c.LowAddress[0]);
            c.Circuit.Connect(Usart.ChipSelect, c.Select40);
            c.Circuit.Connect(Usart.ReadStrobe, c.Cpu.ReadStrobe);
            c.Circuit.Connect(Usart.WriteStrobe, c.Cpu.WriteStrobe);
            c.Circuit.Connect(Usart.Clock, c.Cpu.ClockOut);

            var clock = c.Ic(new OscillatorModule(serialClock));
            c.Circuit.Connect(clock.Output, Usart.TransmitClock);
            c.Circuit.Connect(clock.Output, Usart.ReceiveClock);

            Cts = c.Toggle(Usart.ClearToSend, ctsHigh);
            Dsr = c.Toggle(Usart.DataSetReady, true);
            Reset = c.Toggle(Usart.Reset, false);

            if (terminalBaud > 0)
            {
                Terminal = c.Circuit.Add(new SerialTerminal { BaudRate = terminalBaud, Message = message, StartDelay = startDelay, Parity = parity });
                c.Circuit.Connect(Terminal.Vcc, c.Rail.Positive);
                c.Circuit.Connect(Terminal.Gnd, c.Ground.Pin);
                c.Circuit.Connect(Usart.Transmit, Terminal.Receive);
                c.Circuit.Connect(Terminal.Transmit, Usart.Receive);
            }
            else
            {
                c.Circuit.Connect(Usart.Transmit, Usart.Receive);
            }
        }

        public LogicState Pin(Terminal output) => Usart.GetOutputState(Usart.OutputTerminals.ToList().IndexOf(output));

        public void Run(double seconds) => Computer.Simulator.Run(seconds);
    }

    /// <summary>Intel's safe start: three $00s and an internal reset, then the mode and the command.</summary>
    private static string Init(int mode, int command = 0x37) => $"""
                lxi sp,$0000
                xra a
                out $41
                out $41
                out $41
                mvi a,$40
                out $41
                mvi a,${mode:X2}
                out $41
                mvi a,${command:X2}
                out $41

        """;

    /// <summary>Sends the zero-terminated string at HL, waiting for TxRDY in the status before each character.</summary>
    private const string Puts = """
        puts:   mov a,m
                ora a
                rz
        txwait: in $41
                ani $01
                jz txwait
                mov a,m
                out $40
                inx h
                jmp puts

        """;

    [Fact]
    public void ThePinoutIsTheDatasheets()
    {
        var usart = new Usart8251();
        string At(int pin) => usart.Terminals.Single(t => t.Id == $"p{pin}").Name;

        Assert.Equal(28, usart.Terminals.Count);
        Assert.Equal(["D2", "D3", "RxD", "GND", "D4", "D5", "D6", "D7", "TxC", "WR", "CS", "CD", "RD", "RxRDY"], Enumerable.Range(1, 14).Select(At));
        Assert.Equal(["TxRDY", "SYNDET", "CTS", "TxEMPTY", "TxD", "CLK", "RESET", "DSR", "RTS", "DTR", "RxC", "VCC", "D0", "D1"],
            Enumerable.Range(15, 14).Select(At));
        Assert.Equal("/CS", usart.PinLabel(usart.ChipSelect));
        Assert.Equal("C/D", usart.PinLabel(usart.ControlData));
    }

    /// <summary>The reset idiom, 8N1 at x16, and text to the terminal at 115,200 baud; /DTR and /RTS low as the command asks.</summary>
    [Fact]
    public void TheResetSequenceThenTextToTheTerminal()
    {
        var b = new Bench(Init(0x4E) + """
                    lxi h,text
                    call puts
            done:   jmp done
            text:   db "8085!",0

            """ + Puts);
        b.Run(0.7e-3);

        Assert.Equal("8085!", b.Terminal!.ReceivedText);
        Assert.Equal(0, b.Terminal.FramingErrors);
        Assert.Equal("8085!", b.Usart.TransmittedText);
        Assert.Equal("command", b.Usart.Expects);
        Assert.Equal("8N1", b.Usart.Format);
        Assert.Equal(0x4E, b.Usart.Mode);
        Assert.Equal(0x27, b.Usart.Command);           // ER acts once and is not kept
        Assert.InRange(b.Usart.TransmitBaud, 115000, 115400);
        Assert.Equal(LogicState.Low, b.Pin(b.Usart.DataTerminalReady));
        Assert.Equal(LogicState.Low, b.Pin(b.Usart.RequestToSend));
        Assert.Equal(LogicState.High, b.Pin(b.Usart.TransmitterEmpty));
        Assert.Equal(LogicState.High, b.Pin(b.Usart.Transmit));
    }

    /// <summary>Characters from the terminal: RxRDY on the pin and in the status, each read taking it down.</summary>
    [Fact]
    public void CharactersFromTheTerminalAreRead()
    {
        var b = new Bench(Init(0x4E) + """
                    lxi h,$8000
            rxwait: in $41
                    ani $02
                    jz rxwait
                    in $40
                    mov m,a
                    inx h
                    jmp rxwait
            """, message: "OK", startDelay: 60e-6);
        b.Run(0.4e-3);

        Assert.Equal((byte)'O', b.Computer.Ram.ReadByte(0));
        Assert.Equal((byte)'K', b.Computer.Ram.ReadByte(1));
        Assert.Equal(LogicState.Low, b.Pin(b.Usart.ReceiverReady));
        Assert.Equal(0, b.Usart.Status & 0x38);
    }

    /// <summary>A second character before the first is read: it replaces it and sets OE; ER clears it, and the read clears RxRDY.</summary>
    [Fact]
    public void AnOverrunIsFlaggedUntilAnErrorReset()
    {
        var b = new Bench(Init(0x4E) + """
            wait:   in $41
                    ani $10         ; OE
                    jz wait
                    in $41
                    sta $8001       ; the status with the overrun
                    in $40
                    sta $8000       ; the character that replaced the first
                    in $41
                    sta $8002       ; after the read: RxRDY gone, OE still there
                    mvi a,$37       ; ER
                    out $41
                    in $41
                    sta $8003
            done:   jmp done
            """, message: "AB", startDelay: 60e-6);
        Assert.Equal(LogicState.Low, b.Pin(b.Usart.ReceiverReady));
        b.Run(0.4e-3);

        Assert.Equal((byte)'B', b.Computer.Ram.ReadByte(0));
        Assert.Equal(0x12, b.Computer.Ram.ReadByte(1) & 0x12);
        Assert.Equal(0x10, b.Computer.Ram.ReadByte(2) & 0x12);
        Assert.Equal(0x00, b.Computer.Ram.ReadByte(3) & 0x12);
        Assert.Equal("AB", System.Text.Encoding.ASCII.GetString([.. b.Usart.ReceivedBytes]));
    }

    /// <summary>x1 with a 115,200 Hz clock and x64 with 1.8432 MHz (28,800 baud): the factor divides the clock.</summary>
    [Theory]
    [InlineData(0x4D, 115200.0, 115200.0)]
    [InlineData(0x4F, 1.8432e6, 28800.0)]
    public void TheBaudFactorDividesTheClock(int mode, double clock, double baud)
    {
        var b = new Bench(Init(mode) + """
                    lxi h,text
                    call puts
            done:   jmp done
            text:   db "Hi",0

            """ + Puts, serialClock: clock, terminalBaud: baud);
        b.Run(baud < 50000 ? 1.2e-3 : 0.4e-3);

        Assert.Equal("Hi", b.Terminal!.ReceivedText);
        Assert.InRange(b.Usart.TransmitBaud, baud * 0.99, baud * 1.01);
    }

    /// <summary>A terminal at x64 sends to it: the receiver samples the middle of each bit at the slower rate.</summary>
    [Fact]
    public void X64Receives()
    {
        var b = new Bench(Init(0x4F) + """
            rxwait: in $41
                    ani $02
                    jz rxwait
                    in $40
                    sta $8000
            done:   jmp done
            """, terminalBaud: 28800, message: "Z", startDelay: 60e-6);
        b.Run(0.6e-3);
        Assert.Equal((byte)'Z', b.Computer.Ram.ReadByte(0));
    }

    /// <summary>7E2 looped back: seven bits, even parity and two stop bits sent, the same received with no error, the eighth bit read as 0.</summary>
    [Fact]
    public void SevenEvenTwoLoopsBack()
    {
        var b = new Bench(Init(0xFA) + """
                    lxi h,$8000
                    mvi a,$C3       ; bit 7 is dropped: seven bits
                    out $40
            rxwait: in $41
                    ani $02
                    jz rxwait
                    in $40
                    sta $8000
                    in $41
                    sta $8001
            done:   jmp done
            """, terminalBaud: 0);
        b.Run(0.4e-3);

        Assert.Equal("7E2", b.Usart.Format);
        Assert.Equal(0x43, b.Computer.Ram.ReadByte(0));
        Assert.Equal(0, b.Computer.Ram.ReadByte(1) & 0x38);
        Assert.Equal([0x43], b.Usart.TransmittedBytes);
    }

    /// <summary>A parity bit that does not match sets PE: 'C' (three ones) sent 8N1 into a 7E1 receiver, its bit 7 taken as the parity.</summary>
    [Fact]
    public void AWrongParityBitSetsPe()
    {
        var b = new Bench(Init(0x7A) + """
            rxwait: in $41
                    ani $02
                    jz rxwait
                    in $41
                    sta $8001
                    in $40
                    sta $8000
            done:   jmp done
            """, message: "C", startDelay: 60e-6);
        b.Run(0.3e-3);

        Assert.Equal(0x43, b.Computer.Ram.ReadByte(0));
        Assert.Equal(0x08, b.Computer.Ram.ReadByte(1) & 0x38);
    }

    /// <summary>A stop bit found low sets FE: 'A' sent 8E1 (its parity bit 0) into an 8N1 receiver.</summary>
    [Fact]
    public void ALowStopBitSetsFe()
    {
        var b = new Bench(Init(0x4E) + """
            rxwait: in $41
                    ani $02
                    jz rxwait
                    in $41
                    sta $8001
            done:   jmp done
            """, message: "A", startDelay: 60e-6, parity: UartParity.Even);
        b.Run(0.3e-3);

        Assert.Equal(0x20, b.Computer.Ram.ReadByte(1) & 0x38);
    }

    /// <summary>SBRK holds TxD low; looped back, BRKDET goes high after two characters' time, and low as the line comes back.</summary>
    [Fact]
    public void ABreakIsSentAndDetected()
    {
        var b = new Bench(Init(0x4E, 0x3F) + """
                    lxi d,60        ; a while in the break
            delay:  dcx d
                    jnk delay
                    in $41
                    sta $8000       ; the status during the break
                    mvi a,$37       ; SBRK off, ER
                    out $41
                    in $41
                    sta $8001
            done:   jmp done
            """, terminalBaud: 0);
        b.Run(0.25e-3);
        Assert.Equal(LogicState.Low, b.Pin(b.Usart.Transmit));
        Assert.Equal(LogicState.High, b.Pin(b.Usart.BreakDetect));
        Assert.True(b.Usart.BreakDetected);

        b.Run(0.4e-3);
        Assert.Equal(0x40, b.Computer.Ram.ReadByte(0) & 0x40);
        Assert.Equal(0x00, b.Computer.Ram.ReadByte(1) & 0x40);
        Assert.Equal(LogicState.High, b.Pin(b.Usart.Transmit));
        Assert.Equal(LogicState.Low, b.Pin(b.Usart.BreakDetect));
    }

    /// <summary>
    /// /CTS high holds the character in the buffer: the TxRDY pin low (and the status bit too, the
    /// buffer being full), TxEMPTY low. /CTS low, it goes, and both pins come back high.
    /// </summary>
    [Fact]
    public void ClearToSendGatesTheTransmitter()
    {
        var b = new Bench(Init(0x4E) + """
                    mvi a,'X'
                    out $40
            done:   jmp done
            """, ctsHigh: true);
        b.Run(60e-6);
        Assert.Equal(LogicState.Low, b.Pin(b.Usart.TransmitterReady));
        Assert.Equal(LogicState.Low, b.Pin(b.Usart.TransmitterEmpty));
        Assert.Equal(0, b.Usart.Status & 0x01);
        Assert.Empty(b.Usart.TransmittedBytes);
        b.Run(100e-6);
        Assert.Empty(b.Usart.TransmittedBytes);

        b.Cts.State = false;
        b.Run(20e-6);
        Assert.Equal(LogicState.High, b.Pin(b.Usart.TransmitterReady));
        Assert.Equal(LogicState.Low, b.Pin(b.Usart.TransmitterEmpty));    // shifting out
        b.Run(100e-6);
        Assert.Equal(LogicState.High, b.Pin(b.Usart.TransmitterEmpty));
        Assert.Equal("X", b.Terminal!.ReceivedText);
    }

    /// <summary>The TxRDY status bit is the buffer alone: TxEN off keeps the pin low but the bit says empty.</summary>
    [Fact]
    public void TheTxRdyStatusBitIgnoresTxEn()
    {
        var b = new Bench(Init(0x4E, 0x00) + """
            done:   jmp done
            """);
        b.Run(30e-6);
        Assert.Equal(1, b.Usart.Status & 0x01);
        Assert.Equal(LogicState.Low, b.Pin(b.Usart.TransmitterReady));
        Assert.Equal(LogicState.High, b.Pin(b.Usart.DataTerminalReady));
        Assert.Equal(LogicState.High, b.Pin(b.Usart.RequestToSend));
    }

    /// <summary>/DSR low reads as status bit 7.</summary>
    [Fact]
    public void DsrIsStatusBitSeven()
    {
        var b = new Bench(Init(0x4E) + """
            loop:   in $41
                    sta $8000
                    jmp loop
            """);
        b.Run(30e-6);
        Assert.Equal(0, b.Computer.Ram.ReadByte(0) & 0x80);
        b.Dsr.State = false;
        b.Run(10e-6);
        Assert.Equal(0x80, b.Computer.Ram.ReadByte(0) & 0x80);
    }

    /// <summary>RESET high: back to the mode state, the command cleared, /DTR and /RTS high, TxD marking.</summary>
    [Fact]
    public void ResetWaitsForANewMode()
    {
        var b = new Bench(Init(0x4E) + """
            done:   jmp done
            """);
        b.Run(30e-6);
        Assert.Equal("command", b.Usart.Expects);
        b.Reset.State = true;
        b.Run(2e-6);
        b.Reset.State = false;
        b.Run(2e-6);
        Assert.Equal("mode", b.Usart.Expects);
        Assert.Equal(0, b.Usart.Command);
        Assert.Equal(LogicState.High, b.Pin(b.Usart.DataTerminalReady));
        Assert.Equal(LogicState.High, b.Pin(b.Usart.RequestToSend));
        Assert.Equal(LogicState.High, b.Pin(b.Usart.Transmit));
        Assert.Equal("awaiting mode", b.Usart.ValueLabel);
    }

    /// <summary>A synchronous mode takes its two sync characters before the command, and leaves both halves idle.</summary>
    [Fact]
    public void ASynchronousModeTakesItsSyncCharacters()
    {
        var b = new Bench("""
                    mvi a,$40
                    out $41         ; whatever state: an internal reset (or a sync mode taking more)
                    mvi a,$40
                    out $41
                    mvi a,$40
                    out $41
                    mvi a,$40
                    out $41
                    mvi a,$0C       ; sync, 8 bits, two sync characters
                    out $41
                    mvi a,$16
                    out $41
                    mvi a,$16
                    out $41
                    mvi a,$37
                    out $41
                    mvi a,'S'
                    out $40
            done:   jmp done
            """);
        b.Run(60e-6);
        Assert.Equal("command", b.Usart.Expects);
        Assert.True(b.Usart.Synchronous);
        Assert.Equal("sync", b.Usart.Format);
        Assert.Empty(b.Usart.TransmittedBytes);
        Assert.Equal(LogicState.High, b.Pin(b.Usart.Transmit));
    }
}
