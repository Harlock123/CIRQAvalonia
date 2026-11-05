using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Processors;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Tests;

/// <summary>
/// The 6821 PIA (and the 6520) on a 6809's bus, and on a 68000's through /VPA: the direction and
/// control registers, port A's pull-ups and passive highs against port B's three-state drivers, CA1
/// interrupting on the edge it is told, CA2's handshake, pulse and manual outputs, CB2's write
/// strobe, and the flags a read of the data register clears.
/// </summary>
public class Pia6821Tests
{
    /// <summary>The 6809 board with a PIA at $C000 (RS0 = A0, RS1 = A1); /IRQA on /IRQ and /IRQB on /FIRQ.</summary>
    private sealed class Bench
    {
        public Cpu6809CircuitTests.Computer Computer { get; }

        public PeripheralInterfaceAdapter Pia { get; }

        public Bench(string source, PeripheralInterfaceAdapter? pia = null, params (int Vector, string Label)[] vectors)
        {
            Computer = new Cpu6809CircuitTests.Computer(Cpu6809CircuitTests.Rom(source, vectors));
            var c = Computer;
            Pia = c.Ic(pia ?? new Pia6821());
            for (var i = 0; i < 8; i++) c.Circuit.Connect(Pia.Data[i], c.Cpu.Data[i]);
            c.Circuit.Connect(Pia.RegisterSelect0, c.Cpu.Address[0]);
            c.Circuit.Connect(Pia.RegisterSelect1, c.Cpu.Address[1]);
            c.Circuit.Connect(Pia.ChipSelect0, c.Rail.Positive);
            c.Circuit.Connect(Pia.ChipSelect1, c.Rail.Positive);
            c.Circuit.Connect(Pia.ChipSelect2, c.SelectC000);
            c.Circuit.Connect(Pia.Enable, c.E);
            c.Circuit.Connect(Pia.ReadWrite, c.ReadWrite);
            c.Circuit.Connect(Pia.Reset, c.Cpu.Reset);
            c.Circuit.Connect(Pia.IrqA, c.IrqLine);
            c.Circuit.Connect(Pia.IrqB, c.FirqLine);
        }

        public LogicToggle Toggle(Terminal pin, bool state)
        {
            var toggle = Computer.Circuit.Add(new LogicToggle(state));
            Computer.Circuit.Connect(toggle.Out, pin);
            return toggle;
        }

        public Resistor ToGround(Terminal pin, double ohms)
        {
            var r = Computer.Circuit.Add(new Resistor(ohms));
            Computer.Circuit.Connect(r.A, pin);
            Computer.Circuit.Connect(r.B, Computer.Ground.Pin);
            return r;
        }

        public double Volts(Terminal pin) => Computer.Simulator.NodeVoltage(pin);
    }

    private const string PortSetup = """
        start:  clr $C001       ; CRA: the DDR
                lda #$0F
                sta $C000       ; PA0-PA3 outputs
                lda #$04
                sta $C001       ; CRA: the data register
                lda #$05
                sta $C000       ; ORA: PA0 and PA2 high, PA1 and PA3 low
                clr $C003
                lda #$FF
                sta $C002       ; port B all outputs
                lda #$04
                sta $C003
                lda #$3C
                sta $C002       ; ORB
                lda $C000
                sta $10         ; port A read back: the pins
                lda $C002
                sta $11         ; port B read back: the register
                lda $C001
                sta $12
        done:   bra done
        """;

    [Fact]
    public void ThePinoutIsTheDatasheets()
    {
        var pia = new Pia6821();
        string At(int pin) => pia.Terminals.Single(t => t.Id == $"p{pin}").Name;

        Assert.Equal(40, pia.Terminals.Count);
        Assert.Equal("VSS", At(1));
        Assert.Equal(["PA0", "PA7", "PB0", "PB7", "CB1", "CB2", "VCC", "RW", "CS0", "CS2", "CS1", "E"],
            new[] { 2, 9, 10, 17, 18, 19, 20, 21, 22, 23, 24, 25 }.Select(At));
        Assert.Equal(["D7", "D0", "RESET", "RS1", "RS0", "IRQB", "IRQA", "CA2", "CA1"], new[] { 26, 33, 34, 35, 36, 37, 38, 39, 40 }.Select(At));
        Assert.Equal("/IRQA", pia.PinLabel(pia.IrqA));
        Assert.Equal("PHI2", new Pia6520().Terminals.Single(t => t.Id == "p25").Name);
    }

    /// <summary>
    /// Port A reads its pins and its high is the pull-up's, so an output loaded down reads low; port
    /// B drives both ways and reads its register on output bits, however the pin is loaded.
    /// </summary>
    [Theory]
    [InlineData(typeof(Pia6821))]
    [InlineData(typeof(Pia6520))]
    public void PortAReadsItsPinsAndPortBItsRegister(Type type)
    {
        var bench = new Bench(PortSetup, (PeripheralInterfaceAdapter)Activator.CreateInstance(type)!);
        var pia = bench.Pia;
        bench.ToGround(pia.PortA[2], 330);      // an output high, loaded down
        bench.ToGround(pia.PortB[3], 10);       // an output high, shorted nearly to ground
        bench.Computer.Simulator.Run(150e-6);

        Assert.Equal(0x0F, pia.DirectionA);
        Assert.Equal(0x05, pia.OutputA);
        Assert.Equal(0x3C, pia.OutputB);
        Assert.True(bench.Volts(pia.PortA[0]) > 4.5);                 // the pull-up's high, unloaded
        Assert.True(bench.Volts(pia.PortA[1]) < 0.5);
        Assert.True(bench.Volts(pia.PortA[2]) < 0.8);                 // the pull-up loses to 330 ohms
        Assert.True(bench.Volts(pia.PortA[7]) > 4.5);                 // an input, pulled up inside
        Assert.True(bench.Volts(pia.PortB[2]) > 2.4);
        Assert.True(bench.Volts(pia.PortB[3]) < 1.0);

        var ram = bench.Computer.Ram;
        Assert.Equal(0xF1, ram.ReadByte(0x10));                       // PA2 reads as the low it really is
        Assert.Equal(0x3C, ram.ReadByte(0x11));                       // PB3 reads as written
        Assert.Equal(0x04, ram.ReadByte(0x12));
        Assert.Equal(0, bench.Computer.Cpu.UndefinedReads);
    }

    /// <summary>A port B input floats (no pull-up); a port A input reads high.</summary>
    [Fact]
    public void PortBInputsFloatAndPortAInputsPullUp()
    {
        var bench = new Bench("""
            start:  lda #$04
                    sta $C001
                    sta $C003       ; both ports' data registers, every line an input
            done:   bra done
            """);
        bench.Computer.Simulator.Run(20e-6);
        Assert.True(bench.Volts(bench.Pia.PortA[4]) > 4.5);
        Assert.InRange(bench.Volts(bench.Pia.PortB[4]), -0.1, 0.1);   // nothing drives it, nothing pulls it
    }

    /// <summary>CA1 on its rising edge sets the flag and interrupts; the handler's read of port A clears it.</summary>
    [Fact]
    public void Ca1InterruptsOnItsEdgeAndAReadClearsTheFlag()
    {
        var bench = new Bench("""
            start:  lds #$0800
                    clr $20
                    lda #$07        ; CA1 interrupt on, rising edge, the data register
                    sta $C001
                    andcc #$EF
            done:   bra done
            irq:    lda $C000       ; clears the flag, and so /IRQA
                    inc $20
                    rti
            """, null, (0xFFF8, "irq"));
        var ca1 = bench.Toggle(bench.Pia.Ca1, false);
        var sim = bench.Computer.Simulator;
        sim.Run(30e-6);

        for (var i = 0; i < 3; i++)
        {
            ca1.State = true;           // the active edge
            sim.Run(60e-6);
            ca1.State = false;          // not active
            sim.Run(60e-6);
        }

        Assert.Equal(3, bench.Computer.Ram.ReadByte(0x20));
        Assert.False(bench.Pia.IrqAAsserted);
        Assert.Equal(0, bench.Pia.ControlA & 0x80);
        Assert.Equal("IRQ", bench.Computer.Cpu.Core.LastInterrupt);
    }

    /// <summary>With the interrupt off the flag still sets, /IRQA stays high, and CB1's edge on /IRQB reaches /FIRQ when enabled.</summary>
    [Fact]
    public void FlagsSetWithTheInterruptOffAndIrqBIsSeparate()
    {
        var bench = new Bench("""
            start:  lds #$0800
                    lda #$04        ; CA1: falling edge, no interrupt
                    sta $C001
                    lda #$05        ; CB1: falling edge, interrupt on
                    sta $C003
                    andcc #$AF
            done:   bra done
            firq:   lda $C002
                    inc $21
                    rti
            """, null, (0xFFF6, "firq"));
        var ca1 = bench.Toggle(bench.Pia.Ca1, true);
        var cb1 = bench.Toggle(bench.Pia.Cb1, true);
        var sim = bench.Computer.Simulator;
        sim.Run(30e-6);

        ca1.State = false;
        cb1.State = false;
        sim.Run(40e-6);

        Assert.Equal(0x80, bench.Pia.ControlA & 0x80);
        Assert.False(bench.Pia.IrqAAsserted);
        Assert.Equal(1, bench.Computer.Ram.ReadByte(0x21));
        Assert.Equal("FIRQ", bench.Computer.Cpu.Core.LastInterrupt);
    }

    /// <summary>CA2 as a handshake: low as E falls after a read of port A, high again on CA1's next active edge.</summary>
    [Fact]
    public void Ca2HandshakesWithCa1()
    {
        var bench = new Bench("""
            start:  lda #$24        ; CA2 output, read strobe with CA1 restore; CA1 falling
                    sta $C001
                    nop
                    nop
                    lda $C000
            done:   bra done
            """);
        var ca1 = bench.Toggle(bench.Pia.Ca1, true);
        var sim = bench.Computer.Simulator;
        sim.Run(8e-6);
        Assert.True(bench.Volts(bench.Pia.Ca2) > 2.4);
        sim.Run(20e-6);
        Assert.True(bench.Volts(bench.Pia.Ca2) < 0.5);    // the read strobe, held
        ca1.State = false;
        sim.Run(5e-6);
        Assert.True(bench.Volts(bench.Pia.Ca2) > 2.4);    // restored by CA1
    }

    /// <summary>CA2 pulsed for one cycle after a read; CB2 for one cycle after a write, from the next rising edge of E; both set by hand.</summary>
    [Fact]
    public void Ca2AndCb2PulseAndCanBeSetByHand()
    {
        var bench = new Bench("""
            start:  lda #$2C        ; CA2: read strobe with E restore
                    sta $C001
                    lda #$2C        ; CB2: write strobe with E restore
                    sta $C003
                    lda $C000
                    sta $C002
                    lda #$34        ; CA2 held low by hand
                    sta $C001
                    lda #$3C        ; CB2 held high by hand
                    sta $C003
            done:   bra done
            """);
        var pia = bench.Pia;
        var sim = bench.Computer.Simulator;
        List<(double Time, int Index, LogicState State)> events = [];
        sim.Scheduler.EventApplied += x =>
        {
            if (ReferenceEquals(x.Device, pia) && x.OutputIndex is 24 or 25) events.Add((x.Timestamp, x.OutputIndex, x.NewState));
        };
        sim.Run(60e-6);

        double Low(int index, int nth) => events.Where(e => e.Index == index && e.State == LogicState.Low).ElementAt(nth).Time;
        double HighAfter(int index, double t) => events.First(e => e.Index == index && e.State == LogicState.High && e.Time > t).Time;

        var ca2Low = Low(24, 0);
        Assert.InRange(HighAfter(24, ca2Low) - ca2Low, 0.9e-6, 1.1e-6);
        var cb2Low = Low(25, 0);
        Assert.InRange(HighAfter(25, cb2Low) - cb2Low, 0.9e-6, 1.1e-6);
        Assert.True(cb2Low > ca2Low);

        Assert.True(bench.Volts(pia.Ca2) < 0.5);
        Assert.True(bench.Volts(pia.Cb2) > 2.4);
        Assert.Equal((false, true), pia.StrobeOutputs);
    }

    /// <summary>/RESET clears every register: the outputs let go.</summary>
    [Fact]
    public void ResetClearsEveryRegister()
    {
        var bench = new Bench(PortSetup);
        var reset = bench.Toggle(bench.Pia.Reset, true);
        bench.Computer.Simulator.Run(150e-6);
        Assert.Equal(0xFF, bench.Pia.DirectionB);

        reset.State = false;
        bench.Computer.Simulator.Run(5e-6);
        Assert.Equal((0, 0, 0, 0, 0, 0), (bench.Pia.DirectionA, bench.Pia.DirectionB, bench.Pia.OutputA, bench.Pia.OutputB, bench.Pia.ControlA, bench.Pia.ControlB));
    }

    /// <summary>
    /// A 68000 driving a 6821 as a 6800 peripheral: /VPA from the I/O region, the PIA selected while
    /// /VMA is low, clocked by the 68000's E, its registers on the odd bytes at $C00001-$C00007.
    /// </summary>
    [Fact]
    public void A68000DrivesItThroughVpaAndE()
    {
        var board = new Cpu68000CircuitTests.Board(Cpu68000CircuitTests.Board.Program("""
                    move.b #$00,$C00003     ; CRA: the DDR
                    move.b #$00,$C00001     ; port A inputs
                    move.b #$04,$C00003
                    move.b #$00,$C00007
                    move.b #$FF,$C00005     ; port B outputs
                    move.b #$04,$C00007
                    move.b #$A5,$C00005
                    move.b $C00001,d0       ; port A: the pull-ups
                    move.b d0,$200001
                    move.b $C00007,d1
                    move.b d1,$200003
            loop:   bra.s loop
            """));
        var c = board.Circuit;
        var pia = new Pia6821();
        c.Add(pia);
        c.Connect(pia.Vcc, board.Rail.Positive);
        c.Connect(pia.Gnd, board.Ground.Pin);
        var inverter = new Ic7404();
        c.Add(inverter);
        c.Connect(inverter.Vcc, board.Rail.Positive);
        c.Connect(inverter.Gnd, board.Ground.Pin);
        var (ia, iy) = inverter.Inverter(0);
        c.Connect(ia, board.IoSelect);
        for (var i = 1; i < 6; i++) c.Connect(inverter.Inverter(i).A, board.Ground.Pin);

        // /VPA from the region; CS1 the region and /LDS (inverted); /CS2 the processor's /VMA.
        c.Connect(board.IoRegion, board.Cpu.ValidPeripheralAddress);
        c.Connect(pia.ChipSelect0, board.Rail.Positive);
        c.Connect(pia.ChipSelect1, iy);
        c.Connect(pia.ChipSelect2, board.Cpu.ValidMemoryAddress);
        c.Connect(pia.Enable, board.Cpu.Enable);
        c.Connect(pia.ReadWrite, board.Cpu.ReadWrite);
        c.Connect(pia.RegisterSelect0, board.Cpu.Address[0]);
        c.Connect(pia.RegisterSelect1, board.Cpu.Address[1]);
        c.Connect(pia.Reset, board.Cpu.Reset);
        board.PullUp(pia.IrqA);
        board.PullUp(pia.IrqB);
        for (var i = 0; i < 8; i++) c.Connect(pia.Data[i], board.Cpu.Data[i]);

        board.Simulator.Run(150e-6);

        Assert.Equal(0xFF, pia.DirectionB);
        Assert.Equal(0xA5, pia.OutputB);
        Assert.True(board.Simulator.NodeVoltage(pia.PortB[0]) > 2.4);
        Assert.True(board.Simulator.NodeVoltage(pia.PortB[1]) < 0.5);
        Assert.Equal(0xFF, board.RamByte(0x200001));
        Assert.Equal(0x04, board.RamByte(0x200003));
        Assert.Equal(0, board.Cpu.UndefinedReads);
        Assert.Contains(board.Cpu.BusLog, x => x.Address == 0xC00005 && x.Kind == M68000CycleKind.Write && x.WaitStates >= 4);
    }
}
