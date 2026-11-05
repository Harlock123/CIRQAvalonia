using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Processors;
using Cirq.Core.Digital;
using Cirq.Core.Topology;
using Cirq.Engine.Decoding;

namespace Cirq.Components.Tests;

/// <summary>
/// The 6850 ACIA on a 6809's bus, its TxCLK and RxCLK on E — 1.8432 MHz from a 7.3728 MHz crystal,
/// ÷16 for 115,200 baud — and on a 68000's through /VPA: the master reset it waits for, the word
/// formats, sending to and receiving from the serial terminal, the receive interrupt, the documented
/// overrun and carrier-loss sequences, and /CTS holding TDRE low.
/// </summary>
public class Acia6850Tests
{
    /// <summary>The 6809 board at 1.8432 MHz with an ACIA at $A000 (RS = A0) and a serial terminal on it.</summary>
    private sealed class Bench
    {
        public Cpu6809CircuitTests.Computer Computer { get; }

        public Acia6850 Acia { get; }

        public SerialTerminal Terminal { get; }

        public Bench(string source, string message = "", double startDelay = 1, UartParity parity = UartParity.None, params (int, string)[] vectors)
        {
            Computer = new Cpu6809CircuitTests.Computer(Cpu6809CircuitTests.Rom(source, vectors), busClock: 1.8432e6);
            var c = Computer;
            Acia = c.Ic(new Acia6850());
            for (var i = 0; i < 8; i++) c.Circuit.Connect(Acia.Data[i], c.Cpu.Data[i]);
            c.Circuit.Connect(Acia.RegisterSelect, c.Cpu.Address[0]);
            c.Circuit.Connect(Acia.ChipSelect0, c.Rail.Positive);
            c.Circuit.Connect(Acia.ChipSelect1, c.Rail.Positive);
            c.Circuit.Connect(Acia.ChipSelect2, c.SelectA000);
            c.Circuit.Connect(Acia.Enable, c.E);
            c.Circuit.Connect(Acia.ReadWrite, c.ReadWrite);
            c.Circuit.Connect(Acia.TransmitClock, c.E);
            c.Circuit.Connect(Acia.ReceiveClock, c.E);
            c.Circuit.Connect(Acia.Irq, c.IrqLine);

            Terminal = c.Circuit.Add(new SerialTerminal { BaudRate = 115200, Message = message, StartDelay = startDelay, Parity = parity });
            c.Circuit.Connect(Terminal.Vcc, c.Rail.Positive);
            c.Circuit.Connect(Terminal.Gnd, c.Ground.Pin);
            c.Circuit.Connect(Acia.Transmit, Terminal.Receive);
            c.Circuit.Connect(Terminal.Transmit, Acia.Receive);
        }

        /// <summary>/CTS and /DCD, low unless a test says otherwise.</summary>
        public (LogicToggle Cts, LogicToggle Dcd) Modem()
        {
            var cts = Computer.Circuit.Add(new LogicToggle(false));
            var dcd = Computer.Circuit.Add(new LogicToggle(false));
            Computer.Circuit.Connect(cts.Out, Acia.ClearToSend);
            Computer.Circuit.Connect(dcd.Out, Acia.CarrierDetect);
            return (cts, dcd);
        }
    }

    private const string Send = """
        ; Sends A when the transmitter has room.
        send:   pshs b
        wait:   ldb $A000
                bitb #$02
                beq wait
                sta $A001
                puls b,pc
        """;

    [Fact]
    public void ThePinoutIsTheDatasheets()
    {
        var acia = new Acia6850();
        string At(int pin) => acia.Terminals.Single(t => t.Id == $"p{pin}").Name;

        Assert.Equal(24, acia.Terminals.Count);
        Assert.Equal(["VSS", "RxData", "RxCLK", "TxCLK", "RTS", "TxData", "IRQ", "CS0", "CS2", "CS1", "RS", "VCC", "RW", "E", "D7"],
            Enumerable.Range(1, 15).Select(At));
        Assert.Equal(["D0", "DCD", "CTS"], Enumerable.Range(22, 3).Select(At));
        Assert.Equal("/CS2", acia.PinLabel(acia.ChipSelect2));
    }

    /// <summary>A master reset, then 8N1 at ÷16 from E: the greeting reaches the terminal at 115,200 baud.</summary>
    [Fact]
    public void TheGreetingReachesTheTerminal()
    {
        var bench = new Bench($$"""
            start:  lds #$0800
                    lda #$03
                    sta $A000       ; master reset
                    lda #$15
                    sta $A000       ; ÷16, 8N1, /RTS low, no interrupts
                    ldx #text
            next:   lda ,x+
                    beq done
                    bsr send
                    bra next
            done:   bra done
            {{Send}}
            text:   fcc "Hi from the 6850"
                    fcb 13,10,0
            """);
        bench.Modem();

        // TxData sampled every 400 ns, as a scope probe would, for the UART decoder.
        var sim = bench.Computer.Simulator;
        List<Cirq.Core.Primitives.DataPoint> samples = [];
        while (sim.Time < 2.2e-3)
        {
            sim.Run(400e-9);
            samples.Add(new(sim.Time, sim.NodeVoltage(bench.Acia.Transmit)));
        }

        Assert.StartsWith("Hi from the 6850..", bench.Terminal.ReceivedText);
        Assert.Equal("Hi from the 6850..", bench.Acia.TransmittedText);
        Assert.Equal(0, bench.Terminal.FramingErrors);
        Assert.InRange(bench.Acia.TransmitBaud, 115000, 115400);
        Assert.Equal("8N1", bench.Acia.WordFormat);

        // The UART decoder reads the line as the terminal did.
        var decoded = UartDecoder.Decode(LogicTrace.From(samples), 115200);
        Assert.Equal("Hi", string.Concat(decoded.Spans.Where(x => x.Kind == SpanKind.Data).Take(2).Select(x => (char)Convert.ToInt32(x.Text[2..4], 16))));
        Assert.DoesNotContain(decoded.Spans, x => x.Kind == SpanKind.Error);
    }

    /// <summary>The receive interrupt: each byte the terminal sends is read in the handler and sent back.</summary>
    [Fact]
    public void TheReceiveInterruptEchoes()
    {
        var bench = new Bench($$"""
            start:  lds #$0800
                    lda #$03
                    sta $A000
                    lda #$95        ; receive interrupt on, ÷16, 8N1
                    sta $A000
                    andcc #$EF
            done:   bra done
            irq:    lda $A000
                    bita #$01
                    beq out
                    lda $A001
                    bsr send
            out:    rti
            {{Send}}
            """, "6809!", 0.1e-3, UartParity.None, (0xFFF8, "irq"));
        bench.Modem();
        bench.Computer.Simulator.Run(0.9e-3);

        Assert.Equal("6809!", bench.Terminal.ReceivedText);
        Assert.Equal("6809!", System.Text.Encoding.ASCII.GetString([.. bench.Acia.ReceivedBytes]));
        Assert.Equal("IRQ", bench.Computer.Cpu.Core.LastInterrupt);
    }

    /// <summary>
    /// An overrun, read out as the datasheet describes: two bytes arrive with nothing read; the status
    /// says RDRF only; reading the data gives the first byte and sets OVRN with RDRF still set;
    /// reading again clears both. The second byte is lost.
    /// </summary>
    [Fact]
    public void AnOverrunShowsAfterTheByteBeforeItIsRead()
    {
        var bench = new Bench("""
            start:  lda #$03
                    sta $A000
                    lda #$15
                    sta $A000
                    ldx #0
            delay:  leax 1,x
                    cmpx #200
                    bne delay
                    lda $A000
                    sta $40
                    lda $A001
                    sta $41
                    lda $A000
                    sta $42
                    lda $A001
                    sta $43
                    lda $A000
                    sta $44
            done:   bra done
            """, "XY", 0.1e-3);
        bench.Modem();
        bench.Computer.Simulator.Run(1.8e-3);

        var ram = bench.Computer.Ram;
        Assert.Equal(0x03, ram.ReadByte(0x40));     // RDRF, TDRE
        Assert.Equal((byte)'X', ram.ReadByte(0x41));
        Assert.Equal(0x23, ram.ReadByte(0x42));     // OVRN, RDRF, TDRE
        Assert.Equal((byte)'X', ram.ReadByte(0x43));
        Assert.Equal(0x02, ram.ReadByte(0x44));     // TDRE alone
        Assert.Equal("XY", System.Text.Encoding.ASCII.GetString([.. bench.Acia.ReceivedBytes]));
    }

    /// <summary>
    /// /DCD going high sets DCD and interrupts with the receive interrupt on; it stays while the
    /// line does; reading the status and then the data clears it once the line is low again.
    /// </summary>
    [Fact]
    public void CarrierLossLatchesUntilStatusThenData()
    {
        var bench = new Bench("""
            start:  lda #$03
                    sta $A000
                    lda #$95
                    sta $A000
            poll:   lda $A000
                    sta $40
                    ldb $A001
                    bra poll
            """);
        var (_, dcd) = bench.Modem();
        var sim = bench.Computer.Simulator;
        sim.Run(40e-6);
        Assert.Equal(0, bench.Acia.Status & 0x04);

        dcd.State = true;
        sim.Run(40e-6);
        Assert.Equal(0x84, bench.Acia.Status & 0x84);          // DCD and IRQ
        Assert.True(sim.NodeVoltage(bench.Acia.Irq) < 0.8);

        dcd.State = false;
        sim.Run(40e-6);
        Assert.Equal(0, bench.Acia.Status & 0x84);
        Assert.True(sim.NodeVoltage(bench.Acia.Irq) > 2.4);
    }

    /// <summary>/CTS high holds TDRE low in the status; /RTS follows the transmitter control bits.</summary>
    [Fact]
    public void CtsHoldsTdreLowAndRtsFollowsTheControlRegister()
    {
        var bench = new Bench("""
            start:  lda #$03
                    sta $A000
                    lda #$55        ; ÷16, 8N1, /RTS high
                    sta $A000
            done:   bra done
            """);
        var (cts, _) = bench.Modem();
        var sim = bench.Computer.Simulator;
        sim.Run(40e-6);
        Assert.Equal(0x02, bench.Acia.Status & 0x0A);
        Assert.True(sim.NodeVoltage(bench.Acia.RequestToSend) > 2.4);

        cts.State = true;
        sim.Run(5e-6);
        Assert.Equal(0x08, bench.Acia.Status & 0x0A);           // CTS set, TDRE held low
    }

    /// <summary>At power-on the chip waits for a master reset: before one, nothing is sent and /RTS is high.</summary>
    [Fact]
    public void NothingHappensBeforeAMasterReset()
    {
        var bench = new Bench("""
            start:  lda #$15
                    sta $A001       ; a byte, with no master reset first
            done:   bra done
            """);
        bench.Modem();
        bench.Computer.Simulator.Run(200e-6);
        Assert.Empty(bench.Acia.TransmittedBytes);
        Assert.True(bench.Acia.InReset);
        Assert.Equal(0x00, bench.Acia.Status);
        Assert.True(bench.Computer.Simulator.NodeVoltage(bench.Acia.RequestToSend) > 2.4);
    }

    /// <summary>8E1: the parity the terminal expects.</summary>
    [Fact]
    public void EightBitsEvenParity()
    {
        var bench = new Bench($$"""
            start:  lds #$0800
                    lda #$03
                    sta $A000
                    lda #$19        ; ÷16, 8E1
                    sta $A000
                    lda #'C'
                    bsr send
                    lda #'G'
                    bsr send
            done:   bra done
            {{Send}}
            """, parity: UartParity.Even);
        bench.Modem();
        bench.Computer.Simulator.Run(400e-6);
        Assert.Equal("CG", bench.Terminal.ReceivedText);
        Assert.Equal(0, bench.Terminal.ParityErrors);
        Assert.Equal("8E1", bench.Acia.WordFormat);
    }

    /// <summary>A 68000 sending through a 6850: /VPA from the region, /CS2 on /VMA, E for the bus and both baud clocks (÷16 of 400 kHz, 25,000 baud).</summary>
    [Fact]
    public void A68000SendsThroughIt()
    {
        var board = new Cpu68000CircuitTests.Board(Cpu68000CircuitTests.Board.Program("""
                    move.b #$03,$C00001     ; master reset
                    move.b #$15,$C00001     ; ÷16, 8N1
                    lea text(pc),a0
            next:   move.b (a0)+,d0
                    beq.s loop
            wait:   btst #1,$C00001
                    beq.s wait
                    move.b d0,$C00003
                    bra.s next
            loop:   bra.s loop
            text:   dc.b "68K",0
            """));
        var c = board.Circuit;
        var acia = new Acia6850();
        c.Add(acia);
        c.Connect(acia.Vcc, board.Rail.Positive);
        c.Connect(acia.Gnd, board.Ground.Pin);
        var inverter = new Ic7404();
        c.Add(inverter);
        c.Connect(inverter.Vcc, board.Rail.Positive);
        c.Connect(inverter.Gnd, board.Ground.Pin);
        var (ia, iy) = inverter.Inverter(0);
        c.Connect(ia, board.IoSelect);
        for (var i = 1; i < 6; i++) c.Connect(inverter.Inverter(i).A, board.Ground.Pin);

        c.Connect(board.IoRegion, board.Cpu.ValidPeripheralAddress);
        c.Connect(acia.ChipSelect0, board.Rail.Positive);
        c.Connect(acia.ChipSelect1, iy);
        c.Connect(acia.ChipSelect2, board.Cpu.ValidMemoryAddress);
        c.Connect(acia.Enable, board.Cpu.Enable);
        c.Connect(acia.TransmitClock, board.Cpu.Enable);
        c.Connect(acia.ReceiveClock, board.Cpu.Enable);
        c.Connect(acia.ReadWrite, board.Cpu.ReadWrite);
        c.Connect(acia.RegisterSelect, board.Cpu.Address[0]);
        c.Connect(acia.ClearToSend, board.Ground.Pin);
        c.Connect(acia.CarrierDetect, board.Ground.Pin);
        board.PullUp(acia.Irq);
        for (var i = 0; i < 8; i++) c.Connect(acia.Data[i], board.Cpu.Data[i]);

        var terminal = c.Add(new SerialTerminal { BaudRate = 25000, Message = "", StartDelay = 1 });
        c.Connect(terminal.Vcc, board.Rail.Positive);
        c.Connect(terminal.Gnd, board.Ground.Pin);
        c.Connect(acia.Transmit, terminal.Receive);
        c.Connect(terminal.Transmit, acia.Receive);

        board.Simulator.Run(1.6e-3);

        Assert.Equal("68K", terminal.ReceivedText);
        Assert.Equal(0, terminal.FramingErrors);
        Assert.InRange(acia.TransmitBaud, 24900, 25100);
        Assert.Equal(0, board.Cpu.UndefinedReads);
    }
}
