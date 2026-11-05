using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Processors;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The 68000 on a schematic: clocked through CLK, a pair of 28C256s and a pair of 62256s as its
/// even and odd bytes, a 74138 decoding A21-A23 under /AS, a 74164 counting clocks into /DTACK, and
/// a program running from the ROMs through the analog solve — with its wait states, bus errors,
/// 6800 cycles, interrupts, bus arbitration, /HALT and /RESET on the pins.
/// </summary>
public class Cpu68000CircuitTests
{
    /// <summary>
    /// A 68000 with ROM at $000000 and RAM at $200000, each a pair of byte-wide chips — the even
    /// bytes on D8-D15 selected by /UDS, the odd on D0-D7 by /LDS — and I/O selects at $C00000
    /// (IO_CS) and $A00000 (IO2_CS), gated by /LDS for chips on the low byte. Memory gets /DTACK from
    /// a 74164 shifting ones in on CLK while it is selected: tap QA for no wait states, QB for one.
    /// Peripherals pull <see cref="ExternalDtack"/> (pulled up) low for theirs.
    /// </summary>
    internal sealed class Board
    {
        public Circuit Circuit { get; } = new();

        public Cpu68000 Cpu { get; }

        public Eeprom28C256 RomEven { get; }

        public Eeprom28C256 RomOdd { get; }

        public Sram62256 RamEven { get; }

        public Sram62256 RamOdd { get; }

        public OscillatorModule Clock { get; }

        public DcVoltageSource Rail { get; }

        public Ground Ground { get; }

        public Ic74138 Decoder { get; }

        public Ic74164 DtackCounter { get; }

        /// <summary>Low for a chip on D0-D7 at $C00000: the 74138's Y6 ORed with /LDS.</summary>
        public Terminal IoSelect { get; }

        /// <summary>Low for a chip on D0-D7 at $A00000.</summary>
        public Terminal Io2Select { get; }

        /// <summary>The 74138's Y6 alone: $C00000-$DFFFFF while /AS is low.</summary>
        public Terminal IoRegion { get; }

        /// <summary>The 74138's Y7: $E00000-$FFFFFF while /AS is low — which the interrupt acknowledge's address is in.</summary>
        public Terminal TopRegion { get; }

        /// <summary>An open-drain /DTACK for peripherals, pulled up, ANDed with memory's into the processor's.</summary>
        public Terminal ExternalDtack { get; }

        private readonly Dictionary<Terminal, Resistor> _pullUps = [];

        public Board(byte[] image, double clock = 4e6, int dtackTap = 0, bool haltOnReset = true)
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

            Cpu = Ic(new Cpu68000());
            Circuit.Connect(Cpu.Terminals.Single(t => t.Name == "VCC2"), Rail.Positive);
            Circuit.Connect(Cpu.Terminals.Single(t => t.Name == "GND2"), Ground.Pin);
            RomEven = Ic(new Eeprom28C256());
            RomOdd = Ic(new Eeprom28C256());
            RamEven = Ic(new Sram62256());
            RamOdd = Ic(new Sram62256());
            Decoder = Ic(new Ic74138());
            var or = Ic(new Ic7432());
            var or2 = Ic(new Ic7432());
            var and = Ic(new Ic7408());
            var not = Ic(new Ic7404());
            DtackCounter = Ic(new Ic74164());
            Clock = Ic(new OscillatorModule(clock));

            RomEven.Load(MemoryImage.FromBinary(image).Lane(ByteLane.Even));
            RomOdd.Load(MemoryImage.FromBinary(image).Lane(ByteLane.Odd));

            Circuit.Connect(Clock.Output, Cpu.Clock);

            // Every control input tied off through a pull-up, /RESET and /HALT on one net.
            foreach (var pin in (Terminal[])[Cpu.Reset, Cpu.BusError, Cpu.ValidPeripheralAddress, Cpu.BusRequest, Cpu.BusGrantAcknowledge, .. Cpu.InterruptLevel])
                PullUp(pin);
            if (haltOnReset) Circuit.Connect(Cpu.Halt, Cpu.Reset);
            else PullUp(Cpu.Halt);

            foreach (var memory in new ParallelMemory[] { RomEven, RomOdd, RamEven, RamOdd })
            {
                for (var i = 0; i < 15; i++) Circuit.Connect(memory.Address[i], Cpu.Address[i]);
                var high = memory == RomEven || memory == RamEven;
                for (var i = 0; i < 8; i++) Circuit.Connect(memory.Data[i], Cpu.Data[high ? 8 + i : i]);
                Circuit.Connect(memory.OutputEnable, Ground.Pin);
            }

            Circuit.Connect(RomEven.WriteEnable, Rail.Positive);
            Circuit.Connect(RomOdd.WriteEnable, Rail.Positive);
            Circuit.Connect(RamEven.WriteEnable, Cpu.ReadWrite);
            Circuit.Connect(RamOdd.WriteEnable, Cpu.ReadWrite);

            // The 74138: A21-A23 pick a 2 MB region while /AS is low.
            Circuit.Connect(Decoder.SelectA, Cpu.Address[20]);
            Circuit.Connect(Decoder.SelectB, Cpu.Address[21]);
            Circuit.Connect(Decoder.SelectC, Cpu.Address[22]);
            Circuit.Connect(Decoder.Enable1, Cpu.AddressStrobe);
            Circuit.Connect(Decoder.Enable2, Ground.Pin);
            Circuit.Connect(Decoder.Enable3, Rail.Positive);
            var romSelect = Decoder.Outputs[0];
            var ramSelect = Decoder.Outputs[1];
            IoRegion = Decoder.Outputs[6];
            TopRegion = Decoder.Outputs[7];

            // Each chip's /CE: its region's select ORed with its byte's strobe — both active low.
            void Or(Ic7432 chip, int gate, Terminal a, Terminal b, Terminal y)
            {
                var (ga, gb, gy) = chip.Gate(gate);
                Circuit.Connect(ga, a);
                Circuit.Connect(gb, b);
                Circuit.Connect(gy, y);
            }

            Or(or, 0, romSelect, Cpu.UpperDataStrobe, RomEven.ChipEnable);
            Or(or, 1, romSelect, Cpu.LowerDataStrobe, RomOdd.ChipEnable);
            Or(or, 2, ramSelect, Cpu.UpperDataStrobe, RamEven.ChipEnable);
            Or(or, 3, ramSelect, Cpu.LowerDataStrobe, RamOdd.ChipEnable);

            var (o4a, o4b, o4y) = or2.Gate(0);
            Circuit.Connect(o4a, IoRegion);
            Circuit.Connect(o4b, Cpu.LowerDataStrobe);
            IoSelect = o4y;
            var (o5a, o5b, o5y) = or2.Gate(1);
            Circuit.Connect(o5a, Decoder.Outputs[5]);
            Circuit.Connect(o5b, Cpu.LowerDataStrobe);
            Io2Select = o5y;
            foreach (var g in new[] { 2, 3 })
            {
                var (a, b, _) = or2.Gate(g);
                Circuit.Connect(a, Ground.Pin);
                Circuit.Connect(b, Ground.Pin);
            }

            // The DTACK generator: memory selected (either select low) lets the 74164 shift ones in.
            var (m1, m2, memorySelected) = and.Gate(0);
            Circuit.Connect(m1, romSelect);
            Circuit.Connect(m2, ramSelect);
            var (n0, n0y) = not.Inverter(0);
            Circuit.Connect(n0, memorySelected);
            Circuit.Connect(DtackCounter.Clear, n0y);
            Circuit.Connect(DtackCounter.Clock, Clock.Output);
            Circuit.Connect(DtackCounter.SerialA, Rail.Positive);
            Circuit.Connect(DtackCounter.SerialB, Rail.Positive);
            var (n1, memoryDtack) = not.Inverter(1);
            Circuit.Connect(n1, DtackCounter.Outputs[dtackTap]);

            var (d1, d2, dy) = and.Gate(1);
            Circuit.Connect(d1, memoryDtack);
            ExternalDtack = d2;
            PullUp(d2);
            Circuit.Connect(dy, Cpu.DataAcknowledge);

            foreach (var g in new[] { 2, 3 })
            {
                var (a, b, _) = and.Gate(g);
                Circuit.Connect(a, Ground.Pin);
                Circuit.Connect(b, Ground.Pin);
            }

            for (var i = 2; i < 6; i++) Circuit.Connect(not.Inverter(i).A, Ground.Pin);
        }

        /// <summary>A 4.7 kΩ pull-up on a pin, the board's way of tying an input high.</summary>
        public Resistor PullUp(Terminal pin)
        {
            var r = Circuit.Add(new Resistor(4.7e3));
            Circuit.Connect(r.A, Rail.Positive);
            Circuit.Connect(r.B, pin);
            _pullUps[pin] = r;
            return r;
        }

        /// <summary>A logic toggle on a net — a bench switch overriding the pull-up there.</summary>
        public LogicToggle Toggle(Terminal pin, bool state = true)
        {
            var toggle = Circuit.Add(new LogicToggle(state));
            Circuit.Connect(toggle.Out, pin);
            return toggle;
        }

        private CircuitSimulator? _simulator;

        public CircuitSimulator Simulator
        {
            get
            {
                if (_simulator is not null) return _simulator;
                _simulator = new CircuitSimulator(Circuit, new() { TimeStep = 1e-7, MaxTimeStep = 1e-6, UseInitialConditions = true });
                _simulator.Reset();
                _simulator.SolveOperatingPoint();
                return _simulator;
            }
        }

        /// <summary>The byte at a RAM address ($200000 up), from whichever chip holds it.</summary>
        public byte RamByte(uint address)
        {
            var offset = (int)((address - 0x200000) >> 1);
            return (address & 1) == 0 ? RamEven.ReadByte(offset) : RamOdd.ReadByte(offset);
        }

        /// <summary>A program assembled with its vectors: SSP at the top of RAM, the reset vector at start.</summary>
        public static byte[] Program(string body, string vectors = "")
        {
            var output = Assembler68000.Assemble($"""
                        org 0
                        dc.l $210000
                        dc.l start
                {vectors}
                        org $400
                start:
                {body}
                """);
            return output.Image(0, 0x10000);
        }
    }

    [Fact]
    public void ThePinoutIsTheDatasheets()
    {
        var cpu = new Cpu68000();
        string At(int pin) => cpu.Terminals.Single(t => t.Id == $"p{pin}").Name;

        Assert.Equal(64, cpu.Terminals.Count);
        Assert.Equal(["D4", "D3", "D2", "D1", "D0", "AS", "UDS", "LDS", "RW", "DTACK", "BG", "BGACK", "BR", "VCC", "CLK", "GND"],
            Enumerable.Range(1, 16).Select(At));
        Assert.Equal(["HALT", "RESET", "VMA", "E", "VPA", "BERR", "IPL2", "IPL1", "IPL0", "FC2", "FC1", "FC0", "A1"],
            Enumerable.Range(17, 13).Select(At));
        Assert.Equal("A20", At(48));
        Assert.Equal("VCC2", At(49));
        Assert.Equal(["A21", "A22", "A23", "GND2", "D15", "D14"], Enumerable.Range(50, 6).Select(At));
        Assert.Equal("D5", At(64));
        Assert.Equal("/DTACK", cpu.PinLabel(cpu.DataAcknowledge));
        Assert.Equal("R/W", cpu.PinLabel(cpu.ReadWrite));
    }

    /// <summary>A program from the ROM pair writes words and bytes into the RAM pair, each byte into its own chip.</summary>
    [Fact]
    public void AProgramRunsFromTheRomPairIntoTheRamPair()
    {
        var board = new Board(Board.Program("""
                    lea $200000,a0
                    move.w #$1234,(a0)+
                    move.b #$56,(a0)+
                    move.b #$78,(a0)+
                    move.l #$9ABCDEF0,(a0)
                    moveq #0,d0
                    move.w $200000,d0
                    move.l d0,$200010
            loop:   bra.s loop
            """));
        board.Simulator.Run(40e-6);

        Assert.Equal(0x12, board.RamEven.ReadByte(0));
        Assert.Equal(0x34, board.RamOdd.ReadByte(0));
        Assert.Equal(0x56, board.RamEven.ReadByte(1));
        Assert.Equal(0x78, board.RamOdd.ReadByte(1));
        Assert.Equal([0x9A, 0xBC, 0xDE, 0xF0], new[] { board.RamByte(0x200004), board.RamByte(0x200005), board.RamByte(0x200006), board.RamByte(0x200007) });
        Assert.Equal(0x1234u, board.Cpu.Core.GetD(0));
        Assert.Equal(0x34, board.RamByte(0x200013));
        Assert.Equal(0, board.Cpu.UndefinedReads);
        Assert.Equal(0, board.Cpu.WaitStates);
    }

    /// <summary>
    /// The bus cycle on the pins: FC and R/W in S0, the address in S1, /AS and the read strobes in
    /// S2, the data taken as CLK falls into S7 — four clocks with /DTACK from QA, five from QB.
    /// </summary>
    [Theory]
    [InlineData(0, 4)]
    [InlineData(1, 5)]
    [InlineData(2, 6)]
    public void DtackLateMeansWaitStates(int tap, int clocks)
    {
        var board = new Board(Board.Program("loop:   bra.s loop"), dtackTap: tap);
        board.Simulator.Run(30e-6);

        var cycles = board.Cpu.BusLog.Where(c => c.Kind != M68000CycleKind.Instruction).ToList();
        Assert.True(cycles.Count > 10);
        Assert.All(cycles.Skip(2), c => Assert.Equal(clocks * 0.25e-6, c.End - c.Start, 9));
        Assert.All(cycles.Skip(2), c => Assert.Equal(tap, c.WaitStates));
        Assert.Contains(cycles, c => c.Address == 0x000004 && c.Data == 0x0000 && c.FunctionCode == 6);
        Assert.Equal("BRA.S $000400", board.Cpu.CurrentInstruction[8..]);
    }

    [Fact]
    public void WithNoDtackTheProcessorWaitsForever()
    {
        var board = new Board(Board.Program("""
                    move.b $C00001,d0
                    moveq #1,d1
            loop:   bra.s loop
            """));
        board.Simulator.Run(40e-6);

        Assert.Equal(0u, board.Cpu.Core.GetD(1));
        Assert.Equal(0xC00001u, board.Cpu.Core.Address);
        Assert.True(board.Cpu.WaitStates > 100);
        Assert.Equal(LogicState.Low, board.Cpu.GetOutputState(42));      // /AS still low, waiting
    }

    /// <summary>/BERR from a watchdog on the unanswered region: the access ends with a bus error, and the handler runs.</summary>
    [Fact]
    public void BerrEndsTheCycleWithABusError()
    {
        var board = new Board(Board.Program("""
                    move.b $C00001,d0
                    moveq #1,d1
            loop:   bra.s loop
            handler: moveq #2,d1
                    bra.s loop
            """, "        org 8\n        dc.l handler"));
        board.Circuit.Connect(board.IoRegion, board.Cpu.BusError);
        board.Simulator.Run(40e-6);

        Assert.Equal(2u, board.Cpu.Core.GetD(1));
        Assert.Equal(2, board.Cpu.Core.LastException);
    }

    /// <summary>
    /// A 6800 peripheral's cycle: /VPA instead of /DTACK, /VMA once E is low, and the data taken as
    /// E falls, so the access stretches to E's ten-clock period.
    /// </summary>
    [Fact]
    public void VpaMakesASynchronousCycleOnE()
    {
        var board = new Board(Board.Program("""
                    move.b $C00001,d0
                    move.b $C00001,d0
            loop:   bra.s loop
            """));
        board.Circuit.Connect(board.IoRegion, board.Cpu.ValidPeripheralAddress);

        List<(double, LogicState)> e = [];
        List<(double, LogicState)> vma = [];
        board.Simulator.Scheduler.EventApplied += x =>
        {
            if (!ReferenceEquals(x.Device, board.Cpu)) return;
            if (x.OutputIndex == 47) e.Add((x.Timestamp, x.NewState));
            if (x.OutputIndex == 48) vma.Add((x.Timestamp, x.NewState));
        };
        board.Simulator.Run(40e-6);

        var peripheral = board.Cpu.BusLog.Where(c => c.Address == 0xC00001).ToList();
        Assert.Equal(2, peripheral.Count);
        Assert.All(peripheral, c => Assert.True(c.WaitStates >= 4));

        // E: high four clocks, low six.
        var rises = e.Where(x => x.Item2 == LogicState.High).Select(x => x.Item1).ToList();
        var falls = e.Where(x => x.Item2 == LogicState.Low).Select(x => x.Item1).ToList();
        Assert.Equal(2.5e-6, rises[2] - rises[1], 9);
        Assert.Equal(1e-6, falls.First(f => f > rises[1]) - rises[1], 9);

        // /VMA went low before E rose, and each cycle ended as E fell.
        var vmaLow = vma.First(x => x.Item2 == LogicState.Low).Item1;
        Assert.Contains(rises, r => r > vmaLow && r - vmaLow < 2.5e-6);
        Assert.All(peripheral, c => Assert.Contains(falls, f => c.End - f is > 0 and < 0.2e-6));
    }

    /// <summary>
    /// An interrupt on /IPL with /VPA during the acknowledge: the autovector. The handler counts, and
    /// the acknowledge cycle has FC = 111 and the level on A1-A3.
    /// </summary>
    [Fact]
    public void AnAutovectoredInterruptRunsItsHandler()
    {
        var board = new Board(Board.Program("""
                    move #$2000,sr
            loop:   bra.s loop
            level3: addq.l #1,d7
                    rte
            """, "        org $6C\n        dc.l level3"));

        // /VPA for the acknowledge: the 74138's Y7 holds A21-A23 high, which only it does here.
        board.Circuit.Connect(board.TopRegion, board.Cpu.ValidPeripheralAddress);
        var ipl0 = board.Toggle(board.Cpu.InterruptLevel[0]);
        var ipl1 = board.Toggle(board.Cpu.InterruptLevel[1]);
        board.Simulator.Run(20e-6);
        Assert.Equal(0u, board.Cpu.Core.GetD(7));

        // Level 3 is /IPL0 and /IPL1 low.
        ipl0.State = false;
        ipl1.State = false;
        board.Simulator.Run(10e-6);
        ipl0.State = true;
        ipl1.State = true;
        board.Simulator.Run(20e-6);

        Assert.True(board.Cpu.Core.GetD(7) >= 1);
        var acknowledge = board.Cpu.BusLog.First(c => c.Kind == M68000CycleKind.InterruptAcknowledge);
        Assert.Equal(7, acknowledge.FunctionCode);
        Assert.Equal(0xFFFFF7u, acknowledge.Address);
        Assert.True(acknowledge.LowerByte && !acknowledge.UpperByte);
        Assert.Equal(27, board.Cpu.Core.LastException);
    }

    /// <summary>/BR: /BG answers between cycles, the processor lets go of the bus, and takes it back when /BR and /BGACK are high.</summary>
    [Fact]
    public void ABusRequestIsGrantedAndTheBusReleased()
    {
        var board = new Board(Board.Program("""
            loop:   addq.l #1,d0
                    bra.s loop
            """));
        var request = board.Toggle(board.Cpu.BusRequest);
        board.Simulator.Run(10e-6);
        var before = board.Cpu.BusCycles;

        request.State = false;
        board.Simulator.Run(10e-6);
        Assert.Equal(LogicState.Low, board.Cpu.GetOutputState(46));                 // /BG
        Assert.Equal(LogicState.HighImpedance, board.Cpu.GetOutputState(0));        // A1 let go
        Assert.Equal(LogicState.HighImpedance, board.Cpu.GetOutputState(42));       // /AS let go
        var during = board.Cpu.BusCycles;
        board.Simulator.Run(10e-6);
        Assert.Equal(during, board.Cpu.BusCycles);

        request.State = true;
        board.Simulator.Run(20e-6);
        Assert.Equal(LogicState.High, board.Cpu.GetOutputState(46));
        Assert.True(board.Cpu.BusCycles > during + 5);
        Assert.True(during >= before);
    }

    /// <summary>The RESET instruction drives /RESET low for 124 clocks; /RESET and /HALT held low together restart it.</summary>
    [Fact]
    public void ResetInstructionAndExternalReset()
    {
        var board = new Board(Board.Program("""
                    addq.l #1,d6
                    reset
            loop:   addq.l #1,d0
                    bra.s loop
            """));

        // A reset button: /RESET and /HALT on one net, pulled low when pressed.
        var button = board.Circuit.Add(new PushButton());
        board.Circuit.Connect(button.A, board.Cpu.Reset);
        board.Circuit.Connect(button.B, board.Ground.Pin);

        List<(double, LogicState)> reset = [];
        board.Simulator.Scheduler.EventApplied += x =>
        {
            if (ReferenceEquals(x.Device, board.Cpu) && x.OutputIndex == 50) reset.Add((x.Timestamp, x.NewState));
        };
        board.Simulator.Run(60e-6);

        var low = reset.First(x => x.Item2 == LogicState.Low).Item1;
        var high = reset.First(x => x.Item2 == LogicState.HighImpedance && x.Item1 > low).Item1;
        Assert.Equal(124 * 0.25e-6, high - low, 9);
        Assert.Equal(1u, board.Cpu.Core.GetD(6));       // the processor itself was not reset by it

        button.IsPressed = true;
        board.Simulator.Run(5e-6);
        var heldAt = board.Cpu.BusCycles;
        board.Simulator.Run(5e-6);
        Assert.Equal(heldAt, board.Cpu.BusCycles);
        button.IsPressed = false;
        board.Simulator.Run(60e-6);
        Assert.Equal(2u, board.Cpu.Core.GetD(6));       // the program ran again from the vector
    }

    /// <summary>/HALT alone stops the processor between cycles; letting it go lets it carry on.</summary>
    [Fact]
    public void HaltStopsBetweenCycles()
    {
        var board = new Board(Board.Program("""
            loop:   addq.l #1,d0
                    bra.s loop
            """), haltOnReset: false);
        var halt = board.Toggle(board.Cpu.Halt);
        board.Simulator.Run(10e-6);

        halt.State = false;
        board.Simulator.Run(2e-6);
        var stoppedAt = board.Cpu.BusCycles;
        board.Simulator.Run(10e-6);
        Assert.Equal(stoppedAt, board.Cpu.BusCycles);

        halt.State = true;
        board.Simulator.Run(20e-6);
        Assert.True(board.Cpu.BusCycles > stoppedAt + 5);
    }

    /// <summary>The bus decoder lists the cycles, with each instruction disassembled where it starts.</summary>
    [Fact]
    public void TheBusDecoderReadsTheCycles()
    {
        var board = new Board(Board.Program("""
                    lea $200000,a0
                    move.w #$1234,(a0)
            loop:   bra.s loop
            """));
        board.Simulator.Run(20e-6);

        var decoded = Cpu68000BusDecoder.Decode(board.Cpu.BusLog);
        Assert.Contains(decoded.Spans, s => s.Text == "$000400 LEA $200000,A0");
        Assert.Contains(decoded.Spans, s => s.Text == "$000406 MOVE.W #$1234,(A0)");
        Assert.Contains(decoded.Spans, s => s.Text == "W.W $200000 1234 (SD)");
        Assert.Contains(decoded.Spans, s => s.Text == "R.W $000004 0000 (SP)");
        Assert.Contains("instructions", decoded.Summary);
    }
}
