using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Processors;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// A 6502 on a schematic: clocked through its PHI2 pin, a ROM and a RAM on its buses with the
/// address decoding Ben Eater's computer uses, and the program in the ROM running for real through
/// the analog solve.
/// </summary>
public class Cpu6502CircuitTests
{
    /// <summary>
    /// The computer's core: a 65C02 or 6502, a 32K ROM at $8000-$FFFF, a 62256 RAM at $0000-$3FFF,
    /// and a 7400 doing the decoding — ROM /CE = NOT A15, RAM /CE = NAND(PHI2, NOT A15), RAM /OE = A14.
    /// </summary>
    internal sealed class Computer
    {
        public Circuit Circuit { get; } = new();

        public Processor6502 Cpu { get; }

        public Eeprom28C256 Rom { get; }

        public Sram62256 Ram { get; }

        public Ic7400 Nand { get; }

        public OscillatorModule Clock { get; }

        public DcVoltageSource Rail { get; }

        /// <summary>Low while the address is in $4000-$7FFF: where an I/O chip's active-low select goes.</summary>
        public Terminal IoSelect { get; }

        public Ground Ground { get; }

        public Computer(byte[] rom, Processor6502? cpu = null, double clock = 1e6)
        {
            Rail = Circuit.Add(new DcVoltageSource(5.0));
            Ground = Circuit.Add(new Ground());
            Circuit.Connect(Rail.Negative, Ground.Pin);

            T Ic<T>(T part) where T : DigitalIc
            {
                Circuit.Add(part);
                Circuit.Connect(part.Vcc, Rail.Positive);
                Circuit.Connect(part.Gnd, Ground.Pin);
                return part;
            }

            Cpu = Ic(cpu ?? new Cpu65C02());
            Rom = Ic(new Eeprom28C256());
            Ram = Ic(new Sram62256());
            Nand = Ic(new Ic7400());
            Clock = Ic(new OscillatorModule(clock));
            Rom.Load(MemoryImage.FromBinary(rom));

            Circuit.Connect(Clock.Output, Cpu.Clock);

            // Every control input the processor has, tied off through pull-ups as on the board.
            foreach (var pin in new[] { Cpu.Reset, Cpu.Irq, Cpu.Nmi, Cpu.Ready, Cpu.SetOverflow, Cpu.BusEnable })
            {
                if (pin is null) continue;
                var pull = Circuit.Add(new Resistor(1e3));
                Circuit.Connect(pull.A, Rail.Positive);
                Circuit.Connect(pull.B, pin);
            }

            for (var i = 0; i < 15; i++)
            {
                Circuit.Connect(Cpu.Address[i], Rom.Address[i]);
                Circuit.Connect(Cpu.Address[i], Ram.Address[i]);
            }

            for (var i = 0; i < 8; i++)
            {
                Circuit.Connect(Cpu.Data[i], Rom.Data[i]);
                Circuit.Connect(Cpu.Data[i], Ram.Data[i]);
            }

            // Gate 0: NOT A15, the ROM's chip enable.
            var (a0, b0, y0) = Nand.Gate(0);
            Circuit.Connect(a0, Cpu.Address[15]);
            Circuit.Connect(b0, Cpu.Address[15]);
            Circuit.Connect(y0, Rom.ChipEnable);
            Circuit.Connect(Rom.OutputEnable, Ground.Pin);
            Circuit.Connect(Rom.WriteEnable, Rail.Positive);

            // Gate 1: NAND(PHI2, NOT A15), the RAM's chip enable: selected in the second half of a
            // cycle with A15 low, so a write lands while the data is steady.
            var (a1, b1, y1) = Nand.Gate(1);
            Circuit.Connect(a1, Clock.Output);
            Circuit.Connect(b1, y0);
            Circuit.Connect(y1, Ram.ChipEnable);
            Circuit.Connect(Ram.OutputEnable, Cpu.Address[14]);
            Circuit.Connect(Ram.WriteEnable, Cpu.ReadWrite);

            // Gate 2: NAND(NOT A15, A14), low for $4000-$7FFF — the I/O chips' select.
            var (a2, b2, y2) = Nand.Gate(2);
            Circuit.Connect(a2, y0);
            Circuit.Connect(b2, Cpu.Address[14]);
            IoSelect = y2;

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

    [Theory]
    [InlineData(typeof(Cpu65C02))]
    [InlineData(typeof(Cpu6502))]
    [InlineData(typeof(Cpu6510))]
    public void TheSelfCheckRunsFromRomInACircuit(Type type)
    {
        var cpu = (Processor6502)Activator.CreateInstance(type)!;
        var program = Assembler6502.Assemble(Cpu6502CoreTests.SelfCheck, cpu.Variant);
        var computer = new Computer(program.Rom32K(), cpu);

        // About 900 cycles to the end of the program at 1 MHz.
        computer.Simulator.Run(1.5e-3);

        Assert.Equal(0xAA, computer.Ram.ReadByte(0x200));
        Assert.Equal((byte)'H', computer.Ram.ReadByte(0x300));
        Assert.Equal(program.Labels["done"], computer.Cpu.Core.InstructionAddress);
        Assert.Equal(0, computer.Cpu.UndefinedReads);
        Assert.Empty(computer.Cpu.Violations);
    }

    /// <summary>The pins carry the cycles: SYNC high on fetches, R/W low on writes, the address at its setup time.</summary>
    [Fact]
    public void TheBusFollowsTheClock()
    {
        var program = Assembler6502.Assemble("""
                    .org $8000
            reset:  lda #$42
                    sta $0010
            loop:   jmp loop
                    .org $FFFC
                    .word reset
            """);
        var computer = new Computer(program.Rom32K());
        var sim = computer.Simulator;

        sim.Run(40e-6);
        var log = computer.Cpu.BusLog;

        Assert.Contains(log, c => !c.IsRead && c.Address == 0x0010 && c.Data == 0x42);
        Assert.Contains(log, c => c.Sync && c.Address == 0x8000 && c.Data == 0xA9);
        Assert.Equal(0x42, computer.Ram.ReadByte(0x10));

        // Cycles end on falling clock edges a microsecond apart.
        var gaps = log.Zip(log.Skip(1), (a, b) => b.Time - a.Time).ToList();
        Assert.All(gaps, g => Assert.Equal(1e-6, g, 9));

        // The disassembly on the bus matches the program.
        var decoded = Cpu6502BusDecoder.Decode(Cpu6502Variant.Wdc65C02, log);
        Assert.Contains(decoded.Spans, s => s.Text == "$8000 LDA #$42");
        Assert.Contains(decoded.Spans, s => s.Text == "$8002 STA $10");
        Assert.Contains(decoded.Spans, s => s.Text == "W $0010 42");
    }

    /// <summary>Holding RESB low stops the processor; letting it go starts it again from the vector.</summary>
    [Fact]
    public void ResetHoldsAndRestarts()
    {
        var program = Assembler6502.Assemble("""
                    .org $8000
            reset:  inc $0020
            loop:   jmp loop
                    .org $FFFC
                    .word reset
            """);
        var computer = new Computer(program.Rom32K());
        var button = computer.Circuit.Add(new PushButton());
        computer.Circuit.Connect(button.A, computer.Cpu.Reset);
        computer.Circuit.Connect(button.B, computer.Ground.Pin);
        var sim = computer.Simulator;

        sim.Run(30e-6);
        Assert.Equal(1, computer.Ram.ReadByte(0x20));

        button.IsPressed = true;
        sim.Run(20e-6);
        Assert.Equal(1, computer.Ram.ReadByte(0x20));

        button.IsPressed = false;
        sim.Run(30e-6);
        Assert.Equal(2, computer.Ram.ReadByte(0x20));
    }

    /// <summary>A clock too fast for the ROM's 150 ns: the data is sampled before it has arrived, and the program goes wrong.</summary>
    [Fact]
    public void AMemoryTooSlowForTheClockGivesTheWrongBytes()
    {
        var program = Assembler6502.Assemble(Cpu6502CoreTests.SelfCheck);
        var computer = new Computer(program.Rom32K(), clock: 6e6);

        computer.Simulator.Run(200e-6);

        Assert.NotEqual(0xAA, computer.Ram.ReadByte(0x200));
    }

    [Fact]
    public void ThePinoutsAreTheDatasheets()
    {
        var c = new Cpu65C02();
        Assert.Equal("VPB", c.Terminals.Single(t => t.Id == "p1").Name);
        Assert.Equal("PHI2", c.Clock.Name);
        Assert.Equal("p37", c.Clock.Id);
        Assert.Equal("p40", c.Reset.Id);
        Assert.Equal("p34", c.ReadWrite.Id);
        Assert.Equal("p9", c.Address[0].Id);
        Assert.Equal("p25", c.Address[15].Id);
        Assert.Equal("p33", c.Data[0].Id);
        Assert.Equal("p26", c.Data[7].Id);
        Assert.Equal("p36", c.BusEnable!.Id);

        var n = new Cpu6502();
        Assert.Equal("PHI0", n.Clock.Name);
        Assert.Equal("p37", n.Clock.Id);
        Assert.Equal("PHI2", n.Terminals.Single(t => t.Id == "p39").Name);
        Assert.Null(n.BusEnable);
        Assert.DoesNotContain(n.Terminals, t => t.Id is "p5" or "p35" or "p36");
    }
}
