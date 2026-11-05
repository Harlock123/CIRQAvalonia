using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Processors;
using Cirq.Core.Digital;
using Cirq.Core.Topology;
using Computer = Cirq.Components.Tests.Cpu8085CircuitTests.Computer;

namespace Cirq.Components.Tests;

/// <summary>
/// The 8255A driven by 8085 programs on the circuit tests' board, at ports $40-$43: mode 0 out and
/// in, bit set/reset, mode 1 strobed input interrupting the processor on RST 5.5, mode 1 output with
/// its /OBF and /ACK handshake, mode 2 both ways on port A, the port C status word, reading the
/// control register back, and RESET.
/// </summary>
public class Ppi8255Tests
{
    /// <summary>The board with an 8255A at $40 (/CS = Y2, A0 and A1 from the latch) and RESET on a toggle, low.</summary>
    private static (Computer Computer, Ppi8255 Ppi, LogicToggle Reset) Board(string source)
    {
        var computer = new Computer(Cpu8085CircuitTests.Rom(source));
        var ppi = computer.Ic(new Ppi8255());
        var cpu = computer.Cpu;
        for (var i = 0; i < 8; i++) computer.Circuit.Connect(ppi.Data[i], cpu.AddressData[i]);
        computer.Circuit.Connect(ppi.A0, computer.LowAddress[0]);
        computer.Circuit.Connect(ppi.A1, computer.LowAddress[1]);
        computer.Circuit.Connect(ppi.ChipSelect, computer.Select40);
        computer.Circuit.Connect(ppi.ReadStrobe, cpu.ReadStrobe);
        computer.Circuit.Connect(ppi.WriteStrobe, cpu.WriteStrobe);
        var reset = computer.Toggle(ppi.Reset, false);
        return (computer, ppi, reset);
    }

    private static LogicState Pin(Ppi8255 ppi, Terminal pin) => ppi.GetOutputState(ppi.OutputTerminals.ToList().IndexOf(pin));

    /// <summary>The byte a port's pins carry as the chip drives them; -1 where any floats.</summary>
    private static int Driven(Ppi8255 ppi, IReadOnlyList<Terminal> port)
    {
        var value = 0;
        for (var i = 0; i < 8; i++)
        {
            var level = Pin(ppi, port[i]);
            if (level == LogicState.HighImpedance) return -1;
            if (level == LogicState.High) value |= 1 << i;
        }

        return value;
    }

    /// <summary>Toggles on a port, holding <paramref name="value"/>.</summary>
    private static LogicToggle[] Toggles(Computer computer, IReadOnlyList<Terminal> port, int value) =>
        [.. Enumerable.Range(0, 8).Select(i => computer.Toggle(port[i], ((value >> i) & 1) != 0))];

    /// <summary>Resistors to the rail or ground on a port: a pattern the chip can drive over.</summary>
    private static void Pulls(Computer computer, IReadOnlyList<Terminal> port, int value)
    {
        for (var i = 0; i < 8; i++)
        {
            if (((value >> i) & 1) != 0) computer.PullUp(port[i], 10e3);
            else computer.PullDown(port[i], 10e3);
        }
    }

    [Fact]
    public void ModeZeroDrivesAndReadsThePorts()
    {
        var (computer, ppi, _) = Board("""
                    mvi a,$82       ; mode 0: A out, B in, C out
                    out $43
                    mvi a,$5A
                    out $40
                    in $41
                    sta $8000
                    mvi a,$A5
                    out $42
                    in $43          ; the control register reads back
                    sta $8001
                    in $40          ; an output reads its latch
                    sta $8002
            done:   jmp done
            """);
        Toggles(computer, ppi.PortB, 0x3C);
        computer.Simulator.Run(60e-6);

        Assert.Equal(0x5A, Driven(ppi, ppi.PortA));
        Assert.Equal(0xA5, Driven(ppi, ppi.PortC));
        Assert.Equal(-1, Driven(ppi, ppi.PortB));
        Assert.Equal(0x3C, computer.Ram.ReadByte(0));
        Assert.Equal(0x82, computer.Ram.ReadByte(1));
        Assert.Equal(0x5A, computer.Ram.ReadByte(2));
        Assert.Equal(0, ppi.Describe().ModeA);
        Assert.Equal(0, computer.Cpu.UndefinedReads);
    }

    [Fact]
    public void ModeZeroInputsReadTheirPinsAsTheyStand()
    {
        var (computer, ppi, _) = Board("""
                    mvi a,$9B       ; everything in
                    out $43
            loop:   in $40
                    sta $8000
                    in $42
                    sta $8001
                    jmp loop
            """);
        var a = Toggles(computer, ppi.PortA, 0x11);
        Toggles(computer, ppi.PortC, 0xC3);
        var sim = computer.Simulator;
        sim.Run(30e-6);
        Assert.Equal(0x11, computer.Ram.ReadByte(0));
        Assert.Equal(0xC3, computer.Ram.ReadByte(1));
        a[7].State = true;
        sim.Run(20e-6);
        Assert.Equal(0x91, computer.Ram.ReadByte(0));
    }

    /// <summary>Bit set/reset writes one bit of port C at a time.</summary>
    [Fact]
    public void BitSetResetWritesPortCOneBitAtATime()
    {
        var (computer, ppi, _) = Board("""
                    mvi a,$80       ; mode 0, all out
                    out $43
                    mvi a,$0F       ; PC7 set
                    out $43
                    mvi a,$05       ; PC2 set
                    out $43
                    mvi a,$0E       ; PC7 reset
                    out $43
                    mvi a,$03       ; PC1 set
                    out $43
                    in $42
                    sta $8000
            done:   jmp done
            """);
        computer.Simulator.Run(60e-6);
        Assert.Equal(0x06, Driven(ppi, ppi.PortC));
        Assert.Equal(0x06, computer.Ram.ReadByte(0));
    }

    /// <summary>
    /// Mode 1 input on port A: /STB (PC4) low latches the pins and raises IBF (PC5); as it rises
    /// INTR A (PC3) goes high and interrupts the 8085 on RST 5.5; the handler's read drops INTR and
    /// then IBF, and the status word shows INTE A in PC4's place.
    /// </summary>
    [Fact]
    public void ModeOneStrobedInputInterrupts()
    {
        var (computer, ppi, _) = Board("""
                    jmp start
                    org $002C
                    in $40
                    sta $8000
                    in $42          ; the status word
                    sta $8001
                    lda $8002
                    inr a
                    sta $8002
                    ei
                    ret
            start:  lxi sp,$0000
                    mvi a,$B0       ; group A mode 1, A in; PC6-PC7 out; group B mode 0
                    out $43
                    mvi a,$09       ; INTE A (PC4)
                    out $43
                    mvi a,$0E       ; RST 5.5 unmasked
                    sim
                    ei
            loop:   jmp loop
            """);
        var cpu = computer.Cpu;
        computer.Circuit.Connect(ppi.PortC[3], cpu.Rst55);
        var data = Toggles(computer, ppi.PortA, 0x77);
        var strobe = computer.Toggle(ppi.PortC[4], true);
        var sim = computer.Simulator;
        sim.Run(40e-6);
        Assert.Equal(LogicState.Low, Pin(ppi, ppi.PortC[3]));
        Assert.Equal(LogicState.Low, Pin(ppi, ppi.PortC[5]));

        strobe.State = false;
        sim.Run(1e-6);
        Assert.Equal(LogicState.High, Pin(ppi, ppi.PortC[5]));     // IBF
        Assert.Equal(LogicState.Low, Pin(ppi, ppi.PortC[3]));      // no INTR until /STB rises
        data[0].State = false;                                     // the latch follows while /STB is low
        sim.Run(0.5e-6);
        strobe.State = true;
        data[1].State = false;                                     // ... and holds after
        sim.Run(1e-6);
        Assert.True(ppi.Describe().IbfA);
        sim.Run(40e-6);

        Assert.Equal(1, computer.Ram.ReadByte(2));
        Assert.Equal(0x76, computer.Ram.ReadByte(0));
        Assert.Equal(0x10, computer.Ram.ReadByte(1) & 0x38);       // IBF and INTR clear, INTE A set
        Assert.Equal(LogicState.Low, Pin(ppi, ppi.PortC[3]));
        Assert.Equal(LogicState.Low, Pin(ppi, ppi.PortC[5]));
        Assert.Equal("RST 5.5", cpu.Core.LastInterrupt);
    }

    /// <summary>
    /// Mode 1 output on port A: setting INTE A (PC6) with /OBF high raises INTR at once; a write drops
    /// it and takes /OBF (PC7) low; /ACK low raises /OBF, and /ACK rising raises INTR again.
    /// </summary>
    [Fact]
    public void ModeOneOutputHandshakes()
    {
        var (computer, ppi, _) = Board("""
                    mvi a,$A0       ; group A mode 1, A out
                    out $43
                    mvi a,$0D       ; INTE A (PC6)
                    out $43
                    in $42
                    sta $8000       ; the status before the write
            wait:   lda $8003
                    ora a
                    jz wait         ; until the test says go
                    mvi a,$C3
                    out $40
            done:   jmp done
            """);
        var ack = computer.Toggle(ppi.PortC[6], true);
        var sim = computer.Simulator;
        sim.Run(40e-6);
        Assert.Equal(0x88 | 0x40, computer.Ram.ReadByte(0) & 0xC8);   // /OBF high, INTE A, INTR
        Assert.Equal(LogicState.High, Pin(ppi, ppi.PortC[3]));

        computer.Ram.WriteByte(3, 1);
        sim.Run(20e-6);
        Assert.Equal(0xC3, Driven(ppi, ppi.PortA));
        Assert.Equal(LogicState.Low, Pin(ppi, ppi.PortC[7]));         // /OBF
        Assert.Equal(LogicState.Low, Pin(ppi, ppi.PortC[3]));         // INTR dropped by the write

        ack.State = false;
        sim.Run(1e-6);
        Assert.Equal(LogicState.High, Pin(ppi, ppi.PortC[7]));
        Assert.Equal(LogicState.Low, Pin(ppi, ppi.PortC[3]));
        ack.State = true;
        sim.Run(1e-6);
        Assert.Equal(LogicState.High, Pin(ppi, ppi.PortC[3]));
        Assert.True(ppi.Describe().IntrA);
    }

    /// <summary>Mode 1 on port B: /STB B on PC2, IBF B on PC1, INTR B on PC0.</summary>
    [Fact]
    public void ModeOneWorksOnPortBToo()
    {
        var (computer, ppi, _) = Board("""
                    mvi a,$86       ; group B mode 1, B in
                    out $43
                    mvi a,$05       ; INTE B (PC2)
                    out $43
            wait:   in $42
                    ani $01         ; INTR B
                    jz wait
                    in $41
                    sta $8000
            done:   jmp done
            """);
        Toggles(computer, ppi.PortB, 0x42);
        var strobe = computer.Toggle(ppi.PortC[2], true);
        var sim = computer.Simulator;
        sim.Run(30e-6);
        strobe.State = false;
        sim.Run(1e-6);
        Assert.Equal(LogicState.High, Pin(ppi, ppi.PortC[1]));
        strobe.State = true;
        sim.Run(30e-6);
        Assert.Equal(0x42, computer.Ram.ReadByte(0));
        Assert.Equal(LogicState.Low, Pin(ppi, ppi.PortC[1]));
        Assert.Equal(LogicState.Low, Pin(ppi, ppi.PortC[0]));
    }

    /// <summary>
    /// Mode 2: a write takes /OBF low but port A is driven only while /ACK is low; /STB latches what
    /// the pins carry with the chip off them, IBF rises, and the program reads it.
    /// </summary>
    [Fact]
    public void ModeTwoGoesBothWays()
    {
        var (computer, ppi, _) = Board("""
                    mvi a,$C0       ; group A mode 2
                    out $43
                    mvi a,$0D       ; INTE 1 (PC6)
                    out $43
                    mvi a,$09       ; INTE 2 (PC4)
                    out $43
                    mvi a,$3C
                    out $40
            loop:   in $42
                    ani $20         ; IBF A
                    jz loop
                    in $40
                    sta $8000
            done:   jmp done
            """);
        Pulls(computer, ppi.PortA, 0xA5);
        var ack = computer.Toggle(ppi.PortC[6], true);
        var strobe = computer.Toggle(ppi.PortC[4], true);
        var sim = computer.Simulator;
        sim.Run(40e-6);
        Assert.Equal(-1, Driven(ppi, ppi.PortA));
        Assert.Equal(LogicState.Low, Pin(ppi, ppi.PortC[7]));
        Assert.Equal(LogicState.Low, Pin(ppi, ppi.PortC[3]));

        ack.State = false;
        sim.Run(1e-6);
        Assert.Equal(0x3C, Driven(ppi, ppi.PortA));
        Assert.Equal(LogicState.High, Pin(ppi, ppi.PortC[7]));
        ack.State = true;
        sim.Run(1e-6);
        Assert.Equal(-1, Driven(ppi, ppi.PortA));
        Assert.Equal(LogicState.High, Pin(ppi, ppi.PortC[3]));       // the output's INTR (INTE 1)

        strobe.State = false;
        sim.Run(1e-6);
        strobe.State = true;
        sim.Run(30e-6);
        Assert.Equal(0xA5, computer.Ram.ReadByte(0));
        Assert.False(ppi.Describe().IbfA);
        Assert.Equal(2, ppi.Describe().ModeA);
    }

    /// <summary>RESET high: every port an input in mode 0, the latches cleared.</summary>
    [Fact]
    public void ResetMakesEveryPortAnInput()
    {
        var (computer, ppi, reset) = Board("""
                    mvi a,$80
                    out $43
                    mvi a,$FF
                    out $40
                    out $41
                    out $42
            done:   jmp done
            """);
        var sim = computer.Simulator;
        sim.Run(40e-6);
        Assert.Equal(0xFF, Driven(ppi, ppi.PortB));
        reset.State = true;
        sim.Run(1e-6);
        Assert.Equal(-1, Driven(ppi, ppi.PortA));
        Assert.Equal(-1, Driven(ppi, ppi.PortB));
        Assert.Equal(-1, Driven(ppi, ppi.PortC));
        reset.State = false;
        sim.Run(1e-6);
        var state = ppi.Describe();
        Assert.Equal(0x9B, state.Control);
        Assert.Equal(0, state.OutputA);
        Assert.True(state.AInput && state.BInput && state.CUpperInput && state.CLowerInput);
    }
}
