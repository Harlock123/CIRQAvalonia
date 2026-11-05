using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Processors;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// A 6809 on a schematic: clocked through EXTAL (or, as a 6809E, from a 7474 making E and Q), a ROM
/// and a RAM on its buses with a 74138 decoding the address, and programs running from the ROM for
/// real — with E and Q, MRDY, SYNC, /HALT, TSC, LIC and AVMA watched on the pins.
/// </summary>
public class Cpu6809CircuitTests
{
    /// <summary>
    /// A 6809 board: 32K of RAM (a 62256) at $0000-$7FFF selected by A15 low, an 8K 2764 at
    /// $E000-$FFFF, and a 74138 on A13-A15 whose Y5 ($A000) and Y6 ($C000) are there for
    /// peripherals. A 7400 makes /RD = NAND(E, R/W) for the memories' /OE and /WR = NAND(E, NOT R/W)
    /// for the RAM's /WE, so both act only while E is high. Every control input is pulled up.
    /// </summary>
    internal sealed class Computer
    {
        public Circuit Circuit { get; } = new();

        public Processor6809 Cpu { get; }

        public Eprom2764 Rom { get; }

        public Sram62256 Ram { get; }

        public Ic74138 Decoder { get; }

        public Ic7400 Nand { get; }

        public OscillatorModule Clock { get; }

        /// <summary>The 7474 making E and Q for a 6809E; null for a 6809.</summary>
        public Ic7474? Johnson { get; }

        /// <summary>A switch on a 6809E's TSC, low; null for a 6809.</summary>
        public LogicToggle? Tsc { get; }

        public DcVoltageSource Rail { get; }

        public Ground Ground { get; }

        /// <summary>The bus clock: the 6809's E output, or the 7474's for a 6809E.</summary>
        public Terminal E { get; }

        public Terminal ReadWrite => Cpu.ReadWrite;

        /// <summary>The /IRQ line, pulled up: a peripheral's open-drain request goes here.</summary>
        public Terminal IrqLine => Cpu.Irq;

        public Terminal FirqLine => Cpu.Firq;

        /// <summary>Low for $A000-$BFFF.</summary>
        public Terminal SelectA000 => Decoder.Outputs[5];

        /// <summary>Low for $C000-$DFFF.</summary>
        public Terminal SelectC000 => Decoder.Outputs[6];

        public T Ic<T>(T part) where T : DigitalIc
        {
            Circuit.Add(part);
            Circuit.Connect(part.Vcc, Rail.Positive);
            Circuit.Connect(part.Gnd, Ground.Pin);
            return part;
        }

        public Resistor PullUp(Terminal pin, double ohms = 4.7e3)
        {
            var pull = Circuit.Add(new Resistor(ohms));
            Circuit.Connect(pull.A, Rail.Positive);
            Circuit.Connect(pull.B, pin);
            return pull;
        }

        public Computer(byte[] rom, Processor6809? cpu = null, double busClock = 1e6)
        {
            Rail = Circuit.Add(new DcVoltageSource(5.0));
            Ground = Circuit.Add(new Ground());
            Circuit.Connect(Rail.Negative, Ground.Pin);

            Cpu = Ic(cpu ?? new Cpu6809());
            Rom = Ic(new Eprom2764());
            Ram = Ic(new Sram62256());
            Decoder = Ic(new Ic74138());
            Nand = Ic(new Ic7400());
            Clock = Ic(new OscillatorModule(busClock * 4));
            Rom.Load(MemoryImage.FromBinary(rom));

            if (Cpu.Extal is { } extal)
            {
                Circuit.Connect(Clock.Output, extal);
                E = Cpu.E;
            }
            else
            {
                // A 6809E's E and Q from a two-stage Johnson counter on the 4x clock: Q leads E by a quarter.
                Johnson = Ic(new Ic7474());
                var (clr1, d1, clk1, pre1, q1, _) = Johnson.Section(0);
                var (clr2, d2, clk2, pre2, q2, q2Not) = Johnson.Section(1);
                foreach (var pin in new[] { clr1, pre1, clr2, pre2 }) Circuit.Connect(pin, Rail.Positive);
                Circuit.Connect(clk1, Clock.Output);
                Circuit.Connect(clk2, Clock.Output);
                Circuit.Connect(d1, q2Not);
                Circuit.Connect(d2, q1);
                Circuit.Connect(q1, Cpu.Q);
                Circuit.Connect(q2, Cpu.E);
                E = Cpu.E;
                Tsc = Circuit.Add(new LogicToggle(false));
                Circuit.Connect(Tsc.Out, Cpu.ThreeStateControl!);
            }

            foreach (var pin in new[] { Cpu.Reset, Cpu.Halt, Cpu.Nmi, Cpu.Irq, Cpu.Firq, Cpu.DmaRequest, Cpu.MemoryReady })
                if (pin is not null) PullUp(pin);

            for (var i = 0; i < 15; i++) Circuit.Connect(Cpu.Address[i], Ram.Address[i]);
            for (var i = 0; i < 13; i++) Circuit.Connect(Cpu.Address[i], Rom.Address[i]);
            for (var i = 0; i < 8; i++)
            {
                Circuit.Connect(Cpu.Data[i], Rom.Data[i]);
                Circuit.Connect(Cpu.Data[i], Ram.Data[i]);
            }

            // The 74138 on A13-A15: Y7 is the ROM.
            Circuit.Connect(Decoder.SelectA, Cpu.Address[13]);
            Circuit.Connect(Decoder.SelectB, Cpu.Address[14]);
            Circuit.Connect(Decoder.SelectC, Cpu.Address[15]);
            Circuit.Connect(Decoder.Enable1, Ground.Pin);
            Circuit.Connect(Decoder.Enable2, Ground.Pin);
            Circuit.Connect(Decoder.Enable3, Rail.Positive);
            Circuit.Connect(Decoder.Outputs[7], Rom.ChipEnable);
            Circuit.Connect(Rom.OutputEnable, Ground.Pin);
            foreach (var t in Rom.Terminals.Where(t => t.Name is "VPP" or "PGM")) Circuit.Connect(t, Rail.Positive);

            // The RAM: /CE = A15; /OE = /RD; /WE = /WR.
            Circuit.Connect(Ram.ChipEnable, Cpu.Address[15]);
            var (a0, b0, notRw) = Nand.Gate(0);
            Circuit.Connect(a0, Cpu.ReadWrite);
            Circuit.Connect(b0, Cpu.ReadWrite);
            var (a1, b1, rd) = Nand.Gate(1);
            Circuit.Connect(a1, E);
            Circuit.Connect(b1, Cpu.ReadWrite);
            var (a2, b2, wr) = Nand.Gate(2);
            Circuit.Connect(a2, E);
            Circuit.Connect(b2, notRw);
            Circuit.Connect(Ram.OutputEnable, rd);
            Circuit.Connect(Ram.WriteEnable, wr);
            var (a3, b3, _) = Nand.Gate(3);
            Circuit.Connect(a3, Ground.Pin);
            Circuit.Connect(b3, Ground.Pin);
        }

        private CircuitSimulator? _simulator;

        public CircuitSimulator Simulator
        {
            get
            {
                if (_simulator is not null) return _simulator;
                _simulator = new CircuitSimulator(Circuit, new() { TimeStep = 1e-6, MaxTimeStep = 1e-5, UseInitialConditions = true });
                _simulator.Reset();
                _simulator.SolveOperatingPoint();
                return _simulator;
            }
        }
    }

    /// <summary>A program for the ROM at $E000, its reset vector pointing at <c>start</c>.</summary>
    internal static byte[] Rom(string source, params (int Vector, string Label)[] vectors)
    {
        var text = "        org $E000\n" + source + "\n        org $FFFE\n        fdb start\n";
        foreach (var (vector, label) in vectors) text += $"        org ${vector:X4}\n        fdb {label}\n";
        return Assembler6809.Assemble(text).Image(0xE000, 0x2000);
    }

    /// <summary>The self-check program from the core tests, from ROM, through a real bus.</summary>
    [Theory]
    [InlineData(typeof(Cpu6809))]
    [InlineData(typeof(Cpu6809E))]
    public void TheSelfCheckRunsFromRomInACircuit(Type type)
    {
        var cpu = (Processor6809)Activator.CreateInstance(type)!;
        var computer = new Computer(Assembler6809.Assemble(Cpu6809CoreTests.SelfCheck.Replace("org $1000", "org $E000", StringComparison.Ordinal)
                                                           + "\n        org $FFFE\n        fdb start\n").Image(0xE000, 0x2000), cpu);
        computer.Simulator.Run(1.5e-3);

        Assert.Equal(1, computer.Ram.ReadByte(0x100));
        Assert.Equal(0, cpu.UndefinedReads);
        Assert.Contains(cpu.BusLog, c => c.Kind == M6809CycleKind.Dead);
        Assert.Empty(cpu.Violations);
    }

    /// <summary>E and Q at a quarter of EXTAL, in quadrature: Q rises a quarter cycle before E.</summary>
    [Fact]
    public void EAndQAreInQuadratureAtAQuarterOfTheClock()
    {
        var computer = new Computer(Rom("start:  bra start"));
        var cpu = computer.Cpu;
        var sim = computer.Simulator;

        double? eRise = null, qRise = null, eRiseAgain = null;
        var lastE = false;
        var lastQ = false;
        for (var i = 0; i < 400 && eRiseAgain is null; i++)
        {
            sim.Run(25e-9);
            var e = cpu.GetOutputState(cpu.OutputTerminals.ToList().IndexOf(cpu.E)) == LogicState.High;
            var q = cpu.GetOutputState(cpu.OutputTerminals.ToList().IndexOf(cpu.Q)) == LogicState.High;
            if (q && !lastQ && qRise is null && sim.Time > 2e-6) qRise = sim.Time;
            if (e && !lastE && qRise is not null)
            {
                if (eRise is null) eRise = sim.Time;
                else eRiseAgain = sim.Time;
            }

            lastE = e;
            lastQ = q;
        }

        Assert.NotNull(eRiseAgain);
        Assert.InRange(eRiseAgain!.Value - eRise!.Value, 0.95e-6, 1.05e-6);   // E at 1 MHz from 4 MHz
        Assert.InRange(eRise.Value - qRise!.Value, 0.2e-6, 0.3e-6);            // a quarter cycle
    }

    /// <summary>MRDY held low stretches E high a quarter at a time; the program runs on, slower.</summary>
    [Fact]
    public void MrdyStretchesTheCycle()
    {
        var computer = new Computer(Rom("""
            start:  ldx #0
            loop:   leax 1,x
                    stx $0010
                    bra loop
            """));
        var stretch = computer.Circuit.Add(new ClockSource(150e3, 0.5));
        computer.Circuit.Connect(stretch.Out, computer.Cpu.MemoryReady!);
        computer.Simulator.Run(200e-6);

        var cpu = computer.Cpu;
        Assert.True(cpu.StretchedQuarters > 50, $"{cpu.StretchedQuarters} quarters");
        Assert.InRange(cpu.Core.Cycles, 50, 195);
        var count = (computer.Ram.ReadByte(0x10) << 8) | computer.Ram.ReadByte(0x11);
        Assert.True(count > 3);
    }

    /// <summary>SYNC lets the buses float with BA high and BS low until /IRQ is pulled low (masked, so it just carries on).</summary>
    [Fact]
    public void SyncFloatsTheBusUntilAnInterruptLine()
    {
        var computer = new Computer(Rom("""
            start:  orcc #$50
                    sync
                    lda #$5A
                    sta $0020
            done:   bra done
            """));
        var irq = computer.Circuit.Add(new LogicToggle(true));
        computer.Circuit.Connect(irq.Out, computer.Cpu.Irq);
        var cpu = computer.Cpu;
        var sim = computer.Simulator;
        sim.Run(30e-6);

        Assert.True(cpu.Core.IsSyncing);
        int Index(Terminal t) => cpu.OutputTerminals.ToList().IndexOf(t);
        Assert.Equal(LogicState.High, cpu.GetOutputState(Index(cpu.BusAvailable)));
        Assert.Equal(LogicState.Low, cpu.GetOutputState(Index(cpu.BusStatus)));
        Assert.Equal(LogicState.HighImpedance, cpu.GetOutputState(Index(cpu.Address[0])));
        Assert.Equal(LogicState.HighImpedance, cpu.GetOutputState(Index(cpu.ReadWrite)));

        irq.State = false;
        sim.Run(20e-6);
        Assert.False(cpu.Core.IsSyncing);
        Assert.Equal(0x5A, computer.Ram.ReadByte(0x20));
    }

    /// <summary>/HALT low: the processor stops at the end of the instruction with BA and BS high and the bus let go.</summary>
    [Fact]
    public void HaltLetsGoOfTheBus()
    {
        var computer = new Computer(Rom("""
            start:  ldx #0
            loop:   leax 1,x
                    bra loop
            """));
        var halt = computer.Circuit.Add(new LogicToggle(true));
        computer.Circuit.Connect(halt.Out, computer.Cpu.Halt);
        var cpu = computer.Cpu;
        var sim = computer.Simulator;
        sim.Run(40e-6);

        halt.State = false;
        sim.Run(20e-6);
        var x = cpu.Core.X;
        sim.Run(20e-6);
        Assert.True(cpu.Core.IsHalted);
        Assert.Equal(x, cpu.Core.X);
        int Index(Terminal t) => cpu.OutputTerminals.ToList().IndexOf(t);
        Assert.Equal(LogicState.High, cpu.GetOutputState(Index(cpu.BusAvailable)));
        Assert.Equal(LogicState.High, cpu.GetOutputState(Index(cpu.BusStatus)));
        Assert.Equal(LogicState.HighImpedance, cpu.GetOutputState(Index(cpu.Data[0])));

        halt.State = true;
        sim.Run(20e-6);
        Assert.True(cpu.Core.X > x);
    }

    /// <summary>
    /// The 6809E on E and Q from a 7474: LIC high once an instruction in its last cycle, AVMA low in
    /// the cycle before each dead one, BUSY on a 16-bit store's first byte, and TSC floating the bus.
    /// </summary>
    [Fact]
    public void The6809EsStatusPinsLookACycleAhead()
    {
        var computer = new Computer(Rom("""
            start:  ldx #$1234
            loop:   stx $0010
                    bra loop
            """), new Cpu6809E());
        var cpu = computer.Cpu;
        var sim = computer.Simulator;
        sim.Run(10e-6);

        int Index(string name) => cpu.OutputTerminals.ToList().FindIndex(t => t.Name == name);
        List<(M6809CycleKind Kind, bool Lic, bool Avma, bool Busy, ushort Address)> seen = [];
        var lastE = true;
        for (var i = 0; i < 4000 && seen.Count < 40; i++)
        {
            sim.Run(20e-9);
            var e = sim.Time > 0 && computer.Johnson!.QState(1) == LogicState.High;
            if (e && !lastE)
            {
                seen.Add((cpu.Core.Kind, cpu.GetOutputState(Index("LIC")) == LogicState.High, cpu.GetOutputState(Index("AVMA")) == LogicState.High,
                    cpu.GetOutputState(Index("BUSY")) == LogicState.High, cpu.Core.Address));
            }

            lastE = e;
        }

        Assert.Equal(40, seen.Count);
        for (var k = 0; k + 1 < seen.Count; k++)
        {
            Assert.Equal(seen[k + 1].Kind == M6809CycleKind.Fetch, seen[k].Lic);
            Assert.Equal(seen[k + 1].Kind != M6809CycleKind.Dead, seen[k].Avma);
        }

        Assert.Contains(seen, s => s.Busy && s.Address == 0x0010);
        Assert.DoesNotContain(seen, s => s.Busy && s.Address == 0x0011);

        Assert.Equal(0x12, computer.Ram.ReadByte(0x10));
        Assert.Equal(0x34, computer.Ram.ReadByte(0x11));

        // TSC high floats the address bus, R/W and the data bus; low gives them back.
        computer.Tsc!.State = true;
        sim.Run(2e-6);
        Assert.Equal(LogicState.HighImpedance, cpu.GetOutputState(Index("A0")));
        Assert.Equal(LogicState.HighImpedance, cpu.GetOutputState(Index("RW")));
        computer.Tsc.State = false;
        sim.Run(2e-6);
        Assert.NotEqual(LogicState.HighImpedance, cpu.GetOutputState(Index("A0")));
    }

    /// <summary>The bus decoder: instructions where they start, dead cycles marked, the reset vector read with BS.</summary>
    [Fact]
    public void TheBusDecoderMarksDeadCyclesAndVectors()
    {
        var computer = new Computer(Rom("""
            start:  lds #$0800
                    lda #$41
                    sta <$20
            done:   bra done
            """));
        computer.Simulator.Run(30e-6);

        var result = Cpu6809BusDecoder.Decode(computer.Cpu.BusLog);
        var texts = result.Spans.Select(s => s.Text).ToList();
        Assert.Contains("V $FFFE E0", texts);
        Assert.Contains("$E000 LDS #$0800", texts);
        Assert.Contains("$E004 LDA #$41", texts);
        Assert.Contains("$E006 STA <$20", texts);
        Assert.Contains("W $0020 41", texts);
        Assert.Contains("dead $FFFF", texts);
        Assert.Contains("dead", result.Summary);
        Assert.Equal("$E008 BRA $E008", computer.Cpu.CurrentInstruction);
    }
}
