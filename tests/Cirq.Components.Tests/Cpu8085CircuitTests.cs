using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Processors;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// An 8085 on a schematic: X1 driven at twice its clock, a 74373 taking the address's low byte off
/// AD0-AD7 on ALE, a 27C256 and a 62256 behind it with a 74138 decoding memory from A15 and IO/M,
/// another decoding I/O from A13-A15 — and programs running from the ROM for real, with ALE, /RD,
/// /WR, IO/M, S0, S1, READY, HOLD, HLDA, INTR, /INTA, RST 7.5, SID, SOD, RESET IN and RESET OUT
/// watched on the pins.
/// </summary>
public class Cpu8085CircuitTests
{
    /// <summary>
    /// An 8085 board: 32K of ROM (a 27C256) at $0000-$7FFF and 32K of RAM (a 62256) at $8000-$FFFF.
    /// A 74373 latches A0-A7 from AD0-AD7 while ALE is high; A8-A14 come straight from the
    /// processor. A 74138 with A15 on its A input and IO/M on /E1 selects the ROM (Y0) or the RAM
    /// (Y1) only for memory cycles; /RD is both memories' /OE and /WR the RAM's /WE. A second 74138
    /// decodes I/O: A13-A15 — the port number's top bits, which the 8085 also puts on A8-A15 — on its
    /// inputs and IO/M on E3, so Y0 is ports $00-$1F, Y1 $20-$3F, Y2 $40-$5F and so on; an INTR
    /// acknowledge (IO/M high, no /RD or /WR) selects a port but strobes none. READY and /RESET IN
    /// are pulled up; HOLD, SID and the interrupt inputs pulled down.
    /// </summary>
    internal sealed class Computer
    {
        public Circuit Circuit { get; } = new();

        public Cpu8085 Cpu { get; }

        public Ic74373 Latch { get; }

        public Eprom27C256 Rom { get; }

        public Sram62256 Ram { get; }

        public Ic74138 MemoryDecoder { get; }

        public Ic74138 IoDecoder { get; }

        public OscillatorModule Clock { get; }

        public DcVoltageSource Rail { get; }

        public Ground Ground { get; }

        /// <summary>A0-A7 as the latch holds them.</summary>
        public IReadOnlyList<Terminal> LowAddress => Latch.Outputs;

        /// <summary>Low for ports $00-$1F.</summary>
        public Terminal Select00 => IoDecoder.Outputs[0];

        /// <summary>Low for ports $20-$3F.</summary>
        public Terminal Select20 => IoDecoder.Outputs[1];

        /// <summary>Low for ports $40-$5F.</summary>
        public Terminal Select40 => IoDecoder.Outputs[2];

        /// <summary>Low for ports $60-$7F.</summary>
        public Terminal Select60 => IoDecoder.Outputs[3];

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

        public Resistor PullDown(Terminal pin, double ohms = 4.7e3)
        {
            var pull = Circuit.Add(new Resistor(ohms));
            Circuit.Connect(pull.A, Ground.Pin);
            Circuit.Connect(pull.B, pin);
            return pull;
        }

        /// <param name="rom">The ROM's image, from $0000.</param>
        /// <param name="crystal">X1's frequency: twice the processor's clock.</param>
        /// <param name="pullUpTheBus">Pull AD0-AD7 up, so an INTR acknowledge nothing answers reads $FF, an RST 7.</param>
        public Computer(byte[] rom, double crystal = 6.144e6, bool pullUpTheBus = false)
        {
            Rail = Circuit.Add(new DcVoltageSource(5.0));
            Ground = Circuit.Add(new Ground());
            Circuit.Connect(Rail.Negative, Ground.Pin);

            Cpu = Ic(new Cpu8085());
            Latch = Ic(new Ic74373());
            Rom = Ic(new Eprom27C256());
            Ram = Ic(new Sram62256());
            MemoryDecoder = Ic(new Ic74138());
            IoDecoder = Ic(new Ic74138());
            Clock = Ic(new OscillatorModule(crystal));
            Rom.Load(MemoryImage.FromBinary(rom));
            Circuit.Connect(Clock.Output, Cpu.X1);

            foreach (var pin in new[] { Cpu.Ready, Cpu.ResetIn }) PullUp(pin);
            foreach (var pin in new[] { Cpu.Hold, Cpu.Sid, Cpu.Trap, Cpu.Rst75, Cpu.Rst65, Cpu.Rst55, Cpu.Intr }) PullDown(pin);

            // The latch: AD0-AD7 in, A0-A7 out, open while ALE is high.
            Circuit.Connect(Latch.LatchEnable, Cpu.AddressLatchEnable);
            Circuit.Connect(Latch.OutputEnable, Ground.Pin);
            for (var i = 0; i < 8; i++)
            {
                Circuit.Connect(Latch.Data[i], Cpu.AddressData[i]);
                Circuit.Connect(Latch.Outputs[i], Rom.Address[i]);
                Circuit.Connect(Latch.Outputs[i], Ram.Address[i]);
                Circuit.Connect(Cpu.AddressData[i], Rom.Data[i]);
                Circuit.Connect(Cpu.AddressData[i], Ram.Data[i]);
                if (pullUpTheBus) PullUp(Cpu.AddressData[i], 10e3);
            }

            for (var i = 8; i < 15; i++)
            {
                Circuit.Connect(Cpu.AddressHigh[i - 8], Rom.Address[i]);
                Circuit.Connect(Cpu.AddressHigh[i - 8], Ram.Address[i]);
            }

            // Memory: A15 picks Y0 (the ROM) or Y1 (the RAM), only while IO/M is low.
            Circuit.Connect(MemoryDecoder.SelectA, Cpu.AddressHigh[7]);
            Circuit.Connect(MemoryDecoder.SelectB, Ground.Pin);
            Circuit.Connect(MemoryDecoder.SelectC, Ground.Pin);
            Circuit.Connect(MemoryDecoder.Enable1, Cpu.IoMemory);
            Circuit.Connect(MemoryDecoder.Enable2, Ground.Pin);
            Circuit.Connect(MemoryDecoder.Enable3, Rail.Positive);
            Circuit.Connect(MemoryDecoder.Outputs[0], Rom.ChipEnable);
            Circuit.Connect(MemoryDecoder.Outputs[1], Ram.ChipEnable);
            Circuit.Connect(Rom.OutputEnable, Cpu.ReadStrobe);
            Circuit.Connect(Ram.OutputEnable, Cpu.ReadStrobe);
            Circuit.Connect(Ram.WriteEnable, Cpu.WriteStrobe);
            foreach (var t in Rom.Terminals.Where(t => t.Name is "VPP")) Circuit.Connect(t, Rail.Positive);

            // I/O: A13-A15, enabled by IO/M high.
            Circuit.Connect(IoDecoder.SelectA, Cpu.AddressHigh[5]);
            Circuit.Connect(IoDecoder.SelectB, Cpu.AddressHigh[6]);
            Circuit.Connect(IoDecoder.SelectC, Cpu.AddressHigh[7]);
            Circuit.Connect(IoDecoder.Enable1, Ground.Pin);
            Circuit.Connect(IoDecoder.Enable2, Ground.Pin);
            Circuit.Connect(IoDecoder.Enable3, Cpu.IoMemory);
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

        public LogicToggle Toggle(Terminal pin, bool state)
        {
            var toggle = Circuit.Add(new LogicToggle(state));
            Circuit.Connect(toggle.Out, pin);
            return toggle;
        }

        public LogicState Pin(Terminal output) => Cpu.GetOutputState(Cpu.OutputTerminals.ToList().IndexOf(output));

        /// <summary>The address the processor is driving: A8-A15 and AD0-AD7 as they stand, -1 where any line floats.</summary>
        public int DrivenAddress()
        {
            var address = 0;
            for (var b = 0; b < 8; b++)
            {
                var high = Pin(Cpu.AddressHigh[b]);
                var low = Pin(Cpu.AddressData[b]);
                if (high == LogicState.HighImpedance || low == LogicState.HighImpedance) return -1;
                if (high == LogicState.High) address |= 0x100 << b;
                if (low == LogicState.High) address |= 1 << b;
            }

            return address;
        }
    }

    /// <summary>A program for the ROM at $0000.</summary>
    internal static byte[] Rom(string source) =>
        Assembler8085.Assemble("        org $0000\n" + source + "\n").Image(0x0000, 0x8000);

    /// <summary>The self-check program from the core tests, from ROM, through the latch and a real bus.</summary>
    [Fact]
    public void TheSelfCheckRunsFromRomInACircuit()
    {
        var computer = new Computer(Assembler8085.Assemble(Cpu8085CoreTests.SelfCheck).Image(0, 0x8000));
        computer.Simulator.Run(0.6e-3);

        Assert.Equal(1, computer.Ram.ReadByte(0x0000));
        Assert.Equal(0, computer.Cpu.UndefinedReads);
        Assert.Empty(computer.Cpu.Violations);
        Assert.Contains(computer.Cpu.BusLog, c => c.Kind == I8085CycleKind.BusIdle);
    }

    /// <summary>CLK OUT is X1 halved: a T-state per cycle.</summary>
    [Fact]
    public void ClockOutIsHalfTheCrystal()
    {
        var computer = new Computer(Rom("loop: jmp loop"));
        var sim = computer.Simulator;
        sim.Run(10e-6);
        var before = computer.Cpu.Core.TStates;
        sim.Run(100e-6);
        Assert.InRange(computer.Cpu.Core.TStates - before, 305, 309);     // 100 µs at 3.072 MHz
    }

    /// <summary>
    /// The multiplexed bus, watched every 20 ns: ALE high with the address on AD0-AD7, then /RD low
    /// with the ROM driving them; the status lines for a fetch (IO/M 0, S1 1, S0 1) and a memory
    /// write (0, 0, 1); /WR low with the processor's byte on the bus.
    /// </summary>
    [Fact]
    public void AleThenTheStrobesShareTheBus()
    {
        var computer = new Computer(Rom("""
                    lxi h,$8005
                    mvi a,$5A
            loop:   mov m,a
                    jmp loop
            """));
        var cpu = computer.Cpu;
        var sim = computer.Simulator;
        sim.Run(10e-6);

        List<(bool Ale, bool Rd, bool Wr, bool IoM, bool S1, bool S0, int Address, int Ad)> seen = [];
        for (var i = 0; i < 300; i++)
        {
            sim.Run(20e-9);
            var ad = 0;
            for (var b = 0; b < 8; b++) if (computer.Pin(cpu.AddressData[b]) == LogicState.High) ad |= 1 << b;
            seen.Add((computer.Pin(cpu.AddressLatchEnable) == LogicState.High, computer.Pin(cpu.ReadStrobe) == LogicState.Low,
                computer.Pin(cpu.WriteStrobe) == LogicState.Low, computer.Pin(cpu.IoMemory) == LogicState.High,
                computer.Pin(cpu.S1) == LogicState.High, computer.Pin(cpu.S0) == LogicState.High, computer.DrivenAddress(), ad));
        }

        // ALE with the fetch's address out; never with a strobe.
        Assert.Contains(seen, s => s.Ale && !s.IoM && s.S1 && s.S0 && s.Address is 0x0005 or 0x0006);
        Assert.DoesNotContain(seen, s => s.Ale && (s.Rd || s.Wr));
        // The write: status 0 0 1, /WR low, $5A on AD0-AD7 and $80 on A8-A15.
        Assert.Contains(seen, s => s.Wr && !s.IoM && !s.S1 && s.S0 && s.Ad == 0x5A && s.Address == 0x805A);
        Assert.Equal(0x5A, computer.Ram.ReadByte(0x0005));
        // While /RD is low the processor has let go of AD0-AD7.
        Assert.All(seen.Where(s => s.Rd), s => Assert.Equal(-1, s.Address));
    }

    /// <summary>READY held low stalls the processor in wait states, its address and /RD steady; let go, it carries on.</summary>
    [Fact]
    public void ReadyStretchesTheCycle()
    {
        var computer = new Computer(Rom("""
                    lxi h,$8000
            loop:   inr m
                    jmp loop
            """));
        var ready = computer.Toggle(computer.Cpu.Ready, true);
        var cpu = computer.Cpu;
        var sim = computer.Simulator;
        sim.Run(40e-6);
        var count = computer.Ram.ReadByte(0);
        Assert.True(count > 2);

        ready.State = false;
        sim.Run(5e-6);
        var waits = cpu.Core.WaitStates;
        var address = cpu.Core.Address;
        sim.Run(10e-6);
        Assert.True(cpu.Core.WaitStates > waits + 25);
        Assert.Equal(address, cpu.Core.Address);
        Assert.Equal(I8085TState.Tw, cpu.Core.State);

        ready.State = true;
        sim.Run(20e-6);
        Assert.NotEqual(count, computer.Ram.ReadByte(0));
        var decoded = Cpu8085BusDecoder.Decode(cpu.BusLog);
        Assert.Contains(decoded.Spans, s => s.Text.EndsWith("w", StringComparison.Ordinal) && s.Text.Contains('+'));
        Assert.Contains("wait states", decoded.Summary);
    }

    /// <summary>
    /// INTR answered by the bus's pull-ups: /INTA low in place of /RD with IO/M high, $FF read — an
    /// RST 7 — and the handler at $0038 counting in RAM, EI and RET putting the program back.
    /// </summary>
    [Fact]
    public void IntrIsAcknowledgedWithInta()
    {
        var computer = new Computer(Rom("""
                    jmp start
                    org $0038
                    push psw
                    lda $8000
                    inr a
                    sta $8000
                    pop psw
                    ei
                    ret
            start:  lxi sp,$0000
                    ei
            loop:   jmp loop
            """), pullUpTheBus: true);
        var intr = computer.Toggle(computer.Cpu.Intr, false);
        var cpu = computer.Cpu;
        var sim = computer.Simulator;
        sim.Run(15e-6);

        intr.State = true;
        var acknowledged = false;
        for (var i = 0; i < 600 && !acknowledged; i++)
        {
            sim.Run(20e-9);
            acknowledged = computer.Pin(cpu.InterruptAcknowledge) == LogicState.Low;
            if (acknowledged)
            {
                Assert.Equal(LogicState.High, computer.Pin(cpu.ReadStrobe));
                Assert.Equal(LogicState.High, computer.Pin(cpu.IoMemory));
            }
        }

        Assert.True(acknowledged);
        intr.State = false;
        sim.Run(40e-6);
        Assert.Equal(1, computer.Ram.ReadByte(0));
        Assert.Equal("INTR", cpu.Core.LastInterrupt);
        Assert.Equal([0xFF], cpu.Core.LastAcknowledgeBytes);
        Assert.Contains(cpu.BusLog, c => c.Kind == I8085CycleKind.InterruptAcknowledge && c.Data == 0xFF);
        Assert.Empty(cpu.Violations);
    }

    /// <summary>HLT floats the bus; a pulse on RST 7.5, latched, takes it out to $003C.</summary>
    [Fact]
    public void HaltWaitsForAnRst75Pulse()
    {
        var computer = new Computer(Rom("""
                    jmp start
                    org $003C
                    mvi a,$77
                    sta $8001
                    ret
            start:  lxi sp,$0000
                    mvi a,$0B       ; unmask RST 7.5 only
                    sim
                    ei
                    hlt
                    mvi a,$11
                    sta $8002
            done:   jmp done
            """));
        var rst = computer.Toggle(computer.Cpu.Rst75, false);
        var cpu = computer.Cpu;
        var sim = computer.Simulator;
        sim.Run(20e-6);
        Assert.True(cpu.Core.Halted);
        Assert.Equal(LogicState.HighImpedance, computer.Pin(cpu.AddressHigh[0]));
        Assert.Equal(LogicState.HighImpedance, computer.Pin(cpu.ReadStrobe));
        Assert.Equal(LogicState.HighImpedance, computer.Pin(cpu.IoMemory));
        Assert.Equal(LogicState.Low, computer.Pin(cpu.S0));
        Assert.Equal(LogicState.Low, computer.Pin(cpu.S1));
        Assert.Contains(cpu.BusLog, c => c.Kind == I8085CycleKind.Halt);

        rst.State = true;
        sim.Run(0.1e-6);
        rst.State = false;                  // a short pulse: the flip-flop keeps it
        sim.Run(40e-6);
        Assert.False(cpu.Core.Halted);
        Assert.Equal(0x77, computer.Ram.ReadByte(1));
        Assert.Equal(0x11, computer.Ram.ReadByte(2));
        Assert.Equal("RST 7.5", cpu.Core.LastInterrupt);
        Assert.Contains(cpu.BusLog, c => c.Kind == I8085CycleKind.RestartAcknowledge);
    }

    /// <summary>HOLD high: HLDA high, the address and data buses, /RD, /WR and IO/M float; low again, the program carries on.</summary>
    [Fact]
    public void HoldFloatsTheBus()
    {
        var computer = new Computer(Rom("""
                    lxi h,$8000
            loop:   inr m
                    jmp loop
            """));
        var hold = computer.Toggle(computer.Cpu.Hold, false);
        var cpu = computer.Cpu;
        var sim = computer.Simulator;
        sim.Run(20e-6);

        hold.State = true;
        sim.Run(5e-6);
        var count = computer.Ram.ReadByte(0);
        sim.Run(10e-6);
        Assert.Equal(count, computer.Ram.ReadByte(0));
        Assert.Equal(LogicState.High, computer.Pin(cpu.HoldAcknowledge));
        Assert.Equal(LogicState.HighImpedance, computer.Pin(cpu.AddressHigh[0]));
        Assert.Equal(LogicState.HighImpedance, computer.Pin(cpu.AddressData[0]));
        Assert.Equal(LogicState.HighImpedance, computer.Pin(cpu.ReadStrobe));
        Assert.Equal(LogicState.HighImpedance, computer.Pin(cpu.WriteStrobe));
        Assert.Equal(LogicState.HighImpedance, computer.Pin(cpu.IoMemory));
        Assert.Contains(cpu.BusLog, c => c.Kind == I8085CycleKind.Hold);

        hold.State = false;
        sim.Run(10e-6);
        Assert.Equal(LogicState.Low, computer.Pin(cpu.HoldAcknowledge));
        Assert.NotEqual(count, computer.Ram.ReadByte(0));
    }

    /// <summary>RESET IN low floats the bus and raises RESET OUT; let go, the program starts again from $0000.</summary>
    [Fact]
    public void ResetStartsAgainFromZero()
    {
        var computer = new Computer(Rom("""
                    lxi h,$8000
                    inr m
            done:   jmp done
            """));
        var reset = computer.Toggle(computer.Cpu.ResetIn, true);
        var cpu = computer.Cpu;
        var sim = computer.Simulator;
        sim.Run(10e-6);
        Assert.Equal(1, computer.Ram.ReadByte(0));
        Assert.Equal(LogicState.Low, computer.Pin(cpu.ResetOut));

        reset.State = false;
        sim.Run(2e-6);
        Assert.Equal(I8085CycleKind.Reset, cpu.Core.Kind);
        Assert.Equal(LogicState.High, computer.Pin(cpu.ResetOut));
        Assert.Equal(LogicState.HighImpedance, computer.Pin(cpu.AddressHigh[3]));
        Assert.Equal(LogicState.HighImpedance, computer.Pin(cpu.WriteStrobe));

        reset.State = true;
        sim.Run(10e-6);
        Assert.Equal(LogicState.Low, computer.Pin(cpu.ResetOut));
        Assert.Equal(2, computer.Ram.ReadByte(0));
        Assert.Equal("RESET", cpu.Core.LastInterrupt);
    }

    /// <summary>SOD follows SIM; RIM reads SID into bit 7.</summary>
    [Fact]
    public void SodAndSidAreAOneBitPort()
    {
        var computer = new Computer(Rom("""
                    mvi a,$C0       ; SOD high
                    sim
            wait:   rim
                    ora a
                    jp wait         ; until SID is high
                    mvi a,$40       ; SOD low
                    sim
            done:   jmp done
            """));
        var sid = computer.Toggle(computer.Cpu.Sid, false);
        var cpu = computer.Cpu;
        var sim = computer.Simulator;
        sim.Run(15e-6);
        Assert.Equal(LogicState.High, computer.Pin(cpu.Sod));
        sid.State = true;
        sim.Run(15e-6);
        Assert.Equal(LogicState.Low, computer.Pin(cpu.Sod));
    }

    /// <summary>The bus decoder: instructions where they start, reads and writes, I/O on both halves, bus idle cycles, and the latest instruction.</summary>
    [Fact]
    public void TheBusDecoderReadsThe8085()
    {
        var computer = new Computer(Rom("""
                    lxi sp,$0000
                    mvi a,$41
                    sta $8010
                    out $81
                    lxi h,$8000
                    dad h
                    jz $0000
            done:   jmp done
            """));
        computer.Simulator.Run(30e-6);

        var result = Cpu8085BusDecoder.Decode(computer.Cpu.BusLog);
        var texts = result.Spans.Select(s => s.Text).ToList();
        Assert.Contains("$0000 LXI SP,$0000", texts);
        Assert.Contains("$0003 MVI A,$41", texts);
        Assert.Contains("$0005 STA $8010", texts);
        Assert.Contains("W $8010 41", texts);
        Assert.Contains("OUT $81 41", texts);
        Assert.Contains("$000D DAD H", texts);
        Assert.Contains("BI", texts);
        Assert.Contains("$000E JZ (not taken)", texts);
        Assert.Contains("bus idle", result.Summary);
        Assert.Equal("$0011 JMP $0011", computer.Cpu.CurrentInstruction);
    }
}
