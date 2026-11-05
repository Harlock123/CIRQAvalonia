using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Processors;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// A Z80 on a schematic: clocked from an oscillator, a 27C256 and a 62256 on its buses with a 74138
/// decoding memory from /MREQ and another decoding I/O from /IORQ and /M1, and programs running from
/// the ROM for real — with /MREQ, /RD, /M1, /RFSH, /WAIT, /INT, /HALT, /BUSREQ and /RESET watched on
/// the pins.
/// </summary>
public class CpuZ80CircuitTests
{
    /// <summary>
    /// A Z80 board: 32K of ROM (a 27C256) at $0000-$7FFF and 32K of RAM (a 62256) at $8000-$FFFF,
    /// selected by a 74138 with A15 on its A input and /MREQ on /E1, so a refresh or an I/O cycle
    /// selects neither; /RD is both memories' /OE and /WR the RAM's /WE. A second 74138 decodes I/O:
    /// A5-A7 on its inputs, /IORQ on /E1 and /M1 on E3, so an interrupt acknowledge (/M1 with /IORQ)
    /// selects nothing; its Y4 is $80-$9F, Y5 $A0-$BF, Y6 $C0-$DF and Y7 $E0-$FF. Every control input
    /// is pulled up.
    /// </summary>
    internal sealed class Computer
    {
        public Circuit Circuit { get; } = new();

        public CpuZ80 Cpu { get; }

        public Eprom27C256 Rom { get; }

        public Sram62256 Ram { get; }

        public Ic74138 MemoryDecoder { get; }

        public Ic74138 IoDecoder { get; }

        public OscillatorModule Clock { get; }

        public DcVoltageSource Rail { get; }

        public Ground Ground { get; }

        /// <summary>Low for ports $80-$9F.</summary>
        public Terminal Select80 => IoDecoder.Outputs[4];

        /// <summary>Low for ports $A0-$BF.</summary>
        public Terminal SelectA0 => IoDecoder.Outputs[5];

        /// <summary>Low for ports $C0-$DF.</summary>
        public Terminal SelectC0 => IoDecoder.Outputs[6];

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

        public Computer(byte[] rom, double clock = 4e6)
        {
            Rail = Circuit.Add(new DcVoltageSource(5.0));
            Ground = Circuit.Add(new Ground());
            Circuit.Connect(Rail.Negative, Ground.Pin);

            Cpu = Ic(new CpuZ80());
            Rom = Ic(new Eprom27C256());
            Ram = Ic(new Sram62256());
            MemoryDecoder = Ic(new Ic74138());
            IoDecoder = Ic(new Ic74138());
            Clock = Ic(new OscillatorModule(clock));
            Rom.Load(MemoryImage.FromBinary(rom));
            Circuit.Connect(Clock.Output, Cpu.Clock);

            foreach (var pin in new[] { Cpu.Reset, Cpu.Int, Cpu.Nmi, Cpu.Wait, Cpu.BusRequest }) PullUp(pin);

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

            // Memory: A15 picks Y0 (the ROM) or Y1 (the RAM), only while /MREQ is low.
            Circuit.Connect(MemoryDecoder.SelectA, Cpu.Address[15]);
            Circuit.Connect(MemoryDecoder.SelectB, Ground.Pin);
            Circuit.Connect(MemoryDecoder.SelectC, Ground.Pin);
            Circuit.Connect(MemoryDecoder.Enable1, Cpu.MemoryRequest);
            Circuit.Connect(MemoryDecoder.Enable2, Ground.Pin);
            Circuit.Connect(MemoryDecoder.Enable3, Rail.Positive);
            Circuit.Connect(MemoryDecoder.Outputs[0], Rom.ChipEnable);
            Circuit.Connect(MemoryDecoder.Outputs[1], Ram.ChipEnable);
            Circuit.Connect(Rom.OutputEnable, Cpu.ReadStrobe);
            Circuit.Connect(Ram.OutputEnable, Cpu.ReadStrobe);
            Circuit.Connect(Ram.WriteEnable, Cpu.WriteStrobe);
            foreach (var t in Rom.Terminals.Where(t => t.Name is "VPP")) Circuit.Connect(t, Rail.Positive);

            // I/O: A5-A7, enabled by /IORQ low and /M1 high.
            Circuit.Connect(IoDecoder.SelectA, Cpu.Address[5]);
            Circuit.Connect(IoDecoder.SelectB, Cpu.Address[6]);
            Circuit.Connect(IoDecoder.SelectC, Cpu.Address[7]);
            Circuit.Connect(IoDecoder.Enable1, Cpu.IoRequest);
            Circuit.Connect(IoDecoder.Enable2, Ground.Pin);
            Circuit.Connect(IoDecoder.Enable3, Cpu.M1);
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
    }

    /// <summary>A program for the ROM at $0000.</summary>
    internal static byte[] Rom(string source) =>
        AssemblerZ80.Assemble("        org $0000\n" + source + "\n").Image(0x0000, 0x8000);

    /// <summary>The self-check program from the core tests, from ROM, through a real bus.</summary>
    [Fact]
    public void TheSelfCheckRunsFromRomInACircuit()
    {
        var computer = new Computer(AssemblerZ80.Assemble(CpuZ80CoreTests.SelfCheck).Image(0, 0x8000));
        computer.Simulator.Run(2.5e-3);

        Assert.Equal(1, computer.Ram.ReadByte(0x0000));
        Assert.Equal(0, computer.Cpu.UndefinedReads);
        Assert.Empty(computer.Cpu.Violations);
        Assert.Contains(computer.Cpu.BusLog, c => c.IsRefresh);
    }

    /// <summary>
    /// An M1 on the pins, watched every 25 ns at 4 MHz: /M1 and the PC from T1, /MREQ and /RD from
    /// the middle of T1, then /RFSH low with I and R on the address bus, /MREQ strobing again for
    /// the refresh.
    /// </summary>
    [Fact]
    public void AnM1PutsTheRefreshAddressOnTheBus()
    {
        var computer = new Computer(Rom("""
                    ld a,$5A
                    ld i,a
            loop:   nop
                    jr loop
            """));
        var cpu = computer.Cpu;
        var sim = computer.Simulator;
        sim.Run(20e-6);

        List<(bool M1, bool Mreq, bool Rd, bool Rfsh, int Address)> seen = [];
        for (var i = 0; i < 80; i++)
        {
            sim.Run(25e-9);
            var address = 0;
            for (var b = 0; b < 16; b++) if (computer.Pin(cpu.Address[b]) == LogicState.High) address |= 1 << b;
            seen.Add((computer.Pin(cpu.M1) == LogicState.Low, computer.Pin(cpu.MemoryRequest) == LogicState.Low,
                computer.Pin(cpu.ReadStrobe) == LogicState.Low, computer.Pin(cpu.RefreshStrobe) == LogicState.Low, address));
        }

        Assert.Contains(seen, s => s.M1 && s.Mreq && s.Rd && !s.Rfsh && s.Address is 0x0003 or 0x0004);
        Assert.Contains(seen, s => s.Rfsh && s.Mreq && !s.Rd && !s.M1 && (s.Address >> 8) == 0x5A);
        Assert.DoesNotContain(seen, s => s.M1 && s.Rfsh);
        Assert.DoesNotContain(seen, s => s.Rfsh && s.Rd);
    }

    /// <summary>/WAIT held low stalls the processor in wait states, its address steady; let go, it carries on.</summary>
    [Fact]
    public void WaitStretchesTheCycle()
    {
        var computer = new Computer(Rom("""
                    ld hl,$8000
            loop:   inc (hl)
                    jr loop
            """));
        var wait = computer.Toggle(computer.Cpu.Wait, true);
        var cpu = computer.Cpu;
        var sim = computer.Simulator;
        sim.Run(30e-6);
        var count = computer.Ram.ReadByte(0);
        Assert.True(count > 2);

        wait.State = false;
        sim.Run(5e-6);
        var waits = cpu.Core.WaitStates;
        var address = cpu.Core.Address;
        sim.Run(10e-6);
        Assert.True(cpu.Core.WaitStates > waits + 30);
        Assert.Equal(address, cpu.Core.Address);
        Assert.Equal(Z80TState.Tw, cpu.Core.State);

        wait.State = true;
        sim.Run(20e-6);
        Assert.True(computer.Ram.ReadByte(0) != count);
        var decoded = CpuZ80BusDecoder.Decode(cpu.BusLog);
        Assert.Contains(decoded.Spans, s => s.Text.EndsWith("w", StringComparison.Ordinal) && s.Text.Contains('+'));
        Assert.Contains("wait states", decoded.Summary);
    }

    /// <summary>
    /// /INT in mode 1: the acknowledge on the pins is /M1 with /IORQ and no /RD, two wait states in,
    /// the handler at $0038 counting in RAM, and RETI putting the program back.
    /// </summary>
    [Fact]
    public void AnInterruptIsAcknowledgedWithM1AndIorq()
    {
        var computer = new Computer(Rom("""
                    jp start
                    org $0038
                    push af
                    ld a,($8000)
                    inc a
                    ld ($8000),a
                    pop af
                    ei
                    reti
            start:  ld sp,$0000
                    im 1
                    ei
            loop:   jr loop
            """));
        var irq = computer.Toggle(computer.Cpu.Int, true);
        var cpu = computer.Cpu;
        var sim = computer.Simulator;
        sim.Run(15e-6);

        irq.State = false;
        var acknowledged = false;
        for (var i = 0; i < 400 && !acknowledged; i++)
        {
            sim.Run(25e-9);
            acknowledged = computer.Pin(cpu.M1) == LogicState.Low && computer.Pin(cpu.IoRequest) == LogicState.Low;
            if (acknowledged) Assert.Equal(LogicState.High, computer.Pin(cpu.ReadStrobe));
        }

        Assert.True(acknowledged);
        irq.State = true;
        sim.Run(20e-6);
        Assert.Equal(1, computer.Ram.ReadByte(0));
        Assert.Equal("INT (IM 1)", cpu.Core.LastInterrupt);
        Assert.Contains(cpu.BusLog, c => c.Kind == Z80CycleKind.Acknowledge);

        // Nothing drives the bus in the acknowledge, which mode 1 ignores: not a fault.
        Assert.Empty(cpu.Violations);
    }

    /// <summary>HALT takes /HALT low and runs NOPs; an NMI takes it out, to $0066.</summary>
    [Fact]
    public void HaltWaitsForAnNmi()
    {
        var computer = new Computer(Rom("""
                    jp start
                    org $0066
                    ld a,$77
                    ld ($8001),a
                    retn
            start:  ld sp,$0000
                    halt
                    ld a,$11
                    ld ($8002),a
            done:   jr done
            """));
        var nmi = computer.Toggle(computer.Cpu.Nmi, true);
        var cpu = computer.Cpu;
        var sim = computer.Simulator;
        sim.Run(15e-6);
        Assert.True(cpu.Core.Halted);
        Assert.Equal(LogicState.Low, computer.Pin(cpu.HaltOut));
        Assert.Contains(cpu.BusLog, c => c.Halted);

        nmi.State = false;
        sim.Run(1e-6);
        nmi.State = true;
        sim.Run(30e-6);
        Assert.False(cpu.Core.Halted);
        Assert.Equal(LogicState.High, computer.Pin(cpu.HaltOut));
        Assert.Equal(0x77, computer.Ram.ReadByte(1));
        Assert.Equal(0x11, computer.Ram.ReadByte(2));
        Assert.Equal("NMI", cpu.Core.LastInterrupt);
    }

    /// <summary>/BUSREQ low: the address bus, the data bus and the strobes float, /BUSACK low; high again, the program carries on.</summary>
    [Fact]
    public void BusRequestFloatsTheBus()
    {
        var computer = new Computer(Rom("""
                    ld hl,$8000
            loop:   inc (hl)
                    jr loop
            """));
        var request = computer.Toggle(computer.Cpu.BusRequest, true);
        var cpu = computer.Cpu;
        var sim = computer.Simulator;
        sim.Run(20e-6);

        request.State = false;
        sim.Run(5e-6);
        var count = computer.Ram.ReadByte(0);
        sim.Run(10e-6);
        Assert.Equal(count, computer.Ram.ReadByte(0));
        Assert.Equal(LogicState.Low, computer.Pin(cpu.BusAcknowledge));
        Assert.Equal(LogicState.HighImpedance, computer.Pin(cpu.Address[0]));
        Assert.Equal(LogicState.HighImpedance, computer.Pin(cpu.MemoryRequest));
        Assert.Equal(LogicState.HighImpedance, computer.Pin(cpu.ReadStrobe));

        request.State = true;
        sim.Run(10e-6);
        Assert.Equal(LogicState.High, computer.Pin(cpu.BusAcknowledge));
        Assert.NotEqual(count, computer.Ram.ReadByte(0));
    }

    /// <summary>/RESET low floats the buses; let go, the program starts again from $0000.</summary>
    [Fact]
    public void ResetStartsAgainFromZero()
    {
        var computer = new Computer(Rom("""
                    ld hl,$8000
                    inc (hl)
            done:   jr done
            """));
        var reset = computer.Toggle(computer.Cpu.Reset, true);
        var cpu = computer.Cpu;
        var sim = computer.Simulator;
        sim.Run(10e-6);
        Assert.Equal(1, computer.Ram.ReadByte(0));

        reset.State = false;
        sim.Run(2e-6);
        Assert.Equal(Z80CycleKind.Reset, cpu.Core.Kind);
        Assert.Equal(LogicState.HighImpedance, computer.Pin(cpu.Address[3]));
        Assert.Equal(LogicState.High, computer.Pin(cpu.M1));

        reset.State = true;
        sim.Run(10e-6);
        Assert.Equal(2, computer.Ram.ReadByte(0));
        Assert.Equal("RESET", cpu.Core.LastInterrupt);
    }

    /// <summary>The bus decoder: instructions where they start, refreshes, reads and writes, I/O, and the latest instruction.</summary>
    [Fact]
    public void TheBusDecoderReadsTheZ80()
    {
        var computer = new Computer(Rom("""
                    ld sp,$0000
                    ld a,$41
                    ld ($8010),a
                    out ($81),a
                    ld ix,$8000
                    set 3,(ix+16)
            done:   jr done
            """));
        computer.Simulator.Run(30e-6);

        var result = CpuZ80BusDecoder.Decode(computer.Cpu.BusLog);
        var texts = result.Spans.Select(s => s.Text).ToList();
        Assert.Contains("$0000 LD SP,$0000", texts);
        Assert.Contains("$0003 LD A,$41", texts);
        Assert.Contains("$0005 LD ($8010),A", texts);
        Assert.Contains("W $8010 41", texts);
        Assert.Contains("OUT $81 41", texts);
        Assert.Contains("$000E SET 3,(IX+$10)", texts);
        Assert.Contains("M1 $000F CB", texts);
        Assert.Contains("W $8010 49", texts);
        Assert.Contains(texts, t => t.StartsWith("RFSH $00", StringComparison.Ordinal));
        Assert.Contains("refreshes", result.Summary);
        Assert.Equal("$0012 JR $0012", computer.Cpu.CurrentInstruction);
    }
}
