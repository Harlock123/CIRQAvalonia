using Cirq.Components.Digital;
using Cirq.Components.Processors;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Tests;

/// <summary>
/// The 8155 and 8156 on an 8085's multiplexed bus, driven by real programs: the RAM through its own
/// address latch, the ports as outputs and inputs, port C's strobed modes with BF and INTR (into RST
/// 6.5), the timer's four modes on /TIMER OUT from CLK OUT, its stop and start commands and the
/// status register's TIMER bit, and RESET from the processor's RESET OUT.
/// </summary>
public class Ram8155Tests
{
    /// <summary>
    /// The 8085 board with its 62256 taken out and the chip in its place at $8000-$FFFF (mirrored
    /// every 256 bytes) and on ports $80-$FF (every eight): an 8156 with CE on A15, or an 8155 with
    /// /CE from A15 through a 7404. TIMER IN is CLK OUT (3.072 MHz) and RESET the processor's RESET OUT.
    /// </summary>
    private sealed class Board
    {
        public Cpu8085CircuitTests.Computer Computer { get; }

        public RamIoTimer8155 Chip { get; }

        public Board(string source, bool use8155 = false)
        {
            Computer = new Cpu8085CircuitTests.Computer(Cpu8085CircuitTests.Rom(source));
            var c = Computer.Circuit;
            var cpu = Computer.Cpu;
            c.Remove(Computer.Ram);

            Chip = Computer.Ic<RamIoTimer8155>(use8155 ? new Ram8155() : new Ram8156());
            for (var i = 0; i < 8; i++) c.Connect(Chip.AddressData[i], cpu.AddressData[i]);
            c.Connect(Chip.AddressLatchEnable, cpu.AddressLatchEnable);
            c.Connect(Chip.IoMemory, cpu.IoMemory);
            c.Connect(Chip.ReadStrobe, cpu.ReadStrobe);
            c.Connect(Chip.WriteStrobe, cpu.WriteStrobe);
            c.Connect(Chip.Reset, cpu.ResetOut);
            c.Connect(Chip.TimerIn, cpu.ClockOut);

            if (use8155)
            {
                var inverter = Computer.Ic(new Ic7404());
                var (a, y) = inverter.Inverter(0);
                c.Connect(a, cpu.AddressHigh[7]);
                c.Connect(y, Chip.ChipEnable);
            }
            else
            {
                c.Connect(Chip.ChipEnable, cpu.AddressHigh[7]);
            }
        }

        public LogicState Out(Terminal pin) => Chip.GetOutputState(Chip.OutputTerminals.ToList().IndexOf(pin));

        /// <summary>/TIMER OUT's level changes over <paramref name="seconds"/>, sampled every 20 ns: (time, level) pairs.</summary>
        public List<(double Time, bool High)> TimerEdges(double seconds)
        {
            var sim = Computer.Simulator;
            List<(double, bool)> edges = [];
            var was = Out(Chip.TimerOut) == LogicState.High;
            for (double t = 0; t < seconds; t += 20e-9)
            {
                sim.Run(20e-9);
                var now = Out(Chip.TimerOut) == LogicState.High;
                if (now != was) edges.Add((sim.Time, now));
                was = now;
            }

            return edges;
        }
    }

    private const double ClockPeriod = 1 / 3.072e6;

    /// <summary>The RAM, through the chip's own latch: a pattern written over all 256 bytes, read back and summed, its mirrors the same bytes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheRamIsOnTheMultiplexedBus(bool use8155)
    {
        var board = new Board("""
                    lxi h,$80F0     ; the top sixteen bytes
            fill:   mov a,l
                    xri $5A
                    mov m,a
                    inr l
                    jnz fill
                    lxi h,$93F0     ; the same bytes, mirrored
                    mvi b,0
            sum:    mov a,m
                    add b
                    mov b,a
                    inr l
                    jnz sum
                    mov a,b
                    sta $8100       ; a mirror of $8000
            done:   jmp done
            """, use8155);
        board.Computer.Simulator.Run(0.5e-3);

        var sum = Enumerable.Range(0xF0, 16).Sum(i => i ^ 0x5A) & 0xFF;
        Assert.Equal(sum, board.Chip.ReadByte(0));
        Assert.Equal(0xF7 ^ 0x5A, board.Chip.ReadByte(0xF7));
        Assert.Equal(0, board.Computer.Cpu.UndefinedReads);
        Assert.Empty(board.Computer.Cpu.Violations);
    }

    /// <summary>Ports A and B as outputs drive their pins and read back their latches; port C in ALT2 too; turned to inputs they float and read the pins.</summary>
    [Fact]
    public void PortsDriveAndReadTheirPins()
    {
        var board = new Board("""
                    mvi a,$0F       ; A and B out, port C ALT2: all outputs
                    out $80
                    mvi a,$5A
                    out $81
                    mvi a,$A5
                    out $82
                    mvi a,$2A
                    out $83
                    in $81
                    sta $8010
                    in $83
                    sta $8011
            done:   jmp done
            """);
        board.Computer.Simulator.Run(40e-6);
        var chip = board.Chip;
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(((0x5A >> i) & 1) != 0 ? LogicState.High : LogicState.Low, board.Out(chip.PortA[i]));
            Assert.Equal(((0xA5 >> i) & 1) != 0 ? LogicState.High : LogicState.Low, board.Out(chip.PortB[i]));
        }

        for (var i = 0; i < 6; i++) Assert.Equal(((0x2A >> i) & 1) != 0 ? LogicState.High : LogicState.Low, board.Out(chip.PortC[i]));
        Assert.Equal(0x5A, chip.ReadByte(0x10));
        Assert.Equal(0x2A, chip.ReadByte(0x11));
        Assert.Equal(2, chip.PortCAlternative);
    }

    [Fact]
    public void InputPortsReadThePinsAndClearTheirLatches()
    {
        var board = new Board("""
                    mvi a,$01       ; A out
                    out $80
                    mvi a,$FF
                    out $81
                    mvi a,$00       ; A in: its latch cleared
                    out $80
                    in $81
                    sta $8010
                    mvi a,$FF       ; written while an input: ignored
                    out $81
                    mvi a,$01       ; A out again: low
                    out $80
            done:   jmp done
            """);
        var c = board.Computer;
        var toggles = Enumerable.Range(0, 8).Select(i => c.Toggle(board.Chip.PortA[i], ((0xC3 >> i) & 1) != 0)).ToList();
        c.Simulator.Run(60e-6);
        Assert.Equal(0xC3, board.Chip.ReadByte(0x10));
        foreach (var t in toggles) c.Circuit.Remove(t);
        c.Simulator.Run(1e-9);
        Assert.Equal(0, board.Chip.OutputLatches.A);
    }

    /// <summary>A continuous square wave: an even count splits evenly, an odd count's high half one TIMER IN cycle longer.</summary>
    [Theory]
    [InlineData(10, 5, 5)]
    [InlineData(9, 5, 4)]
    public void TheSquareWaveHasTheCountsPeriod(int count, int high, int low)
    {
        var board = new Board($"""
                    mvi a,{count}
                    out $84
                    mvi a,$40       ; continuous square wave
                    out $85
                    mvi a,$C0       ; start
                    out $80
            done:   jmp done
            """);
        board.Computer.Simulator.Run(40e-6);
        Assert.True(board.Chip.TimerRunning);
        var edges = board.TimerEdges(count * 4 * ClockPeriod);
        Assert.True(edges.Count >= 5);
        for (var i = 1; i < edges.Count; i++)
        {
            var cycles = (edges[i].Time - edges[i - 1].Time) / ClockPeriod;
            Assert.Equal(edges[i - 1].High ? high : low, cycles, 0.2);
        }
    }

    /// <summary>Continuous pulses: /TIMER OUT low for one TIMER IN cycle at each terminal count.</summary>
    [Fact]
    public void ContinuousPulsesComeAtEachTerminalCount()
    {
        var board = new Board("""
                    mvi a,20
                    out $84
                    mvi a,$C0       ; continuous pulses
                    out $85
                    mvi a,$C0
                    out $80
            done:   jmp done
            """);
        board.Computer.Simulator.Run(40e-6);
        var edges = board.TimerEdges(80 * ClockPeriod);
        var falls = edges.Where(e => !e.High).Select(e => e.Time).ToList();
        Assert.True(falls.Count >= 3);
        Assert.Equal(20, (falls[2] - falls[1]) / ClockPeriod, 0.2);
        var rise = edges.First(e => e.High && e.Time > falls[1]).Time;
        Assert.Equal(1, (rise - falls[1]) / ClockPeriod, 0.2);
        Assert.Equal(3, board.Chip.TimerMode);
    }

    /// <summary>
    /// A single square wave runs once and stops, the status's TIMER bit set at the terminal count and
    /// cleared by reading it; a single pulse likewise.
    /// </summary>
    [Fact]
    public void SingleModesRunOnceAndSetTheTimerBit()
    {
        var board = new Board("""
                    mvi a,100
                    out $84
                    mvi a,$00       ; single square wave
                    out $85
                    mvi a,$C0
                    out $80
            wait:   in $80
                    ani $40
                    jz wait         ; until TIMER
                    in $80          ; read again: cleared
                    sta $8020
                    mvi a,50
                    out $84
                    mvi a,$80       ; single pulse
                    out $85
                    mvi a,$C0
                    out $80
            done:   jmp done
            """);
        var sim = board.Computer.Simulator;
        sim.Run(40e-6);
        Assert.True(board.Chip.TimerRunning);
        sim.Run(60e-6);
        Assert.Equal(0, board.Chip.ReadByte(0x20) & 0x40);
        Assert.True(board.Chip.TerminalCounts >= 1);
        sim.Run(30e-6);
        Assert.False(board.Chip.TimerRunning);
        Assert.Equal(2, board.Chip.TerminalCounts);
        Assert.Equal(LogicState.High, board.Out(board.Chip.TimerOut));
        Assert.Equal(0x40, board.Chip.Status & 0x40);
    }

    /// <summary>STOP stops at once; STOP AFTER TC at the next terminal count; START while running takes the new length at the next one.</summary>
    [Fact]
    public void TheTimerCommandsStopAndRestartIt()
    {
        var board = new Board("""
                    mvi a,$00
                    out $84
                    mvi a,$41       ; 256, continuous square wave
                    out $85
                    mvi a,$C0
                    out $80
                    mvi a,$40       ; stop
                    out $80
                    in $84
                    sta $8030       ; the count kept: stopped, the register as written
                    mvi a,$C0       ; start again
                    out $80
                    mvi a,$80       ; stop after TC
                    out $80
            done:   jmp done
            """);
        var sim = board.Computer.Simulator;
        sim.Run(60e-6);
        Assert.Equal(0x00, board.Chip.ReadByte(0x30));
        Assert.True(board.Chip.TimerRunning);
        Assert.Equal(0, board.Chip.TerminalCounts);
        sim.Run(100e-6);
        Assert.False(board.Chip.TimerRunning);
        Assert.Equal(1, board.Chip.TerminalCounts);

        var restart = new Board("""
                    mvi a,$00
                    out $84
                    mvi a,$42       ; 512, continuous square wave
                    out $85
                    mvi a,$C0
                    out $80
                    mvi a,10        ; 10 from the next terminal count
                    out $84
                    mvi a,$40
                    out $85
                    mvi a,$C0
                    out $80
            done:   jmp done
            """);
        restart.Computer.Simulator.Run(40e-6);
        Assert.Equal(512, restart.Chip.TimerLength);
        restart.Computer.Simulator.Run(150e-6);
        Assert.Equal(10, restart.Chip.TimerLength);
    }

    /// <summary>
    /// Strobed input (ALT3) with port A's interrupt into RST 6.5: /STB loads the latch and raises BF,
    /// its rising edge raises A INTR, the handler's read drops INTR and BF.
    /// </summary>
    [Fact]
    public void StrobedInputInterruptsThroughRst65()
    {
        var board = new Board("""
                    jmp start
                    org $0034
                    in $81
                    sta $8040
                    lda $8041
                    inr a
                    sta $8041
                    ei
                    ret
            start:  lxi sp,$0000
                    xra a
                    sta $8041
                    mvi a,$14       ; A in, ALT3, A interrupt enabled
                    out $80
                    mvi a,$0D       ; RST 6.5 only
                    sim
                    ei
            idle:   hlt
                    jmp idle
            """);
        var c = board.Computer;
        var chip = board.Chip;
        c.Circuit.Connect(chip.PortC[0], c.Cpu.Rst65);
        var strobe = c.Toggle(chip.PortC[2], true);
        for (var i = 0; i < 8; i++) c.Toggle(chip.PortA[i], ((0x3C >> i) & 1) != 0);
        var sim = c.Simulator;
        sim.Run(20e-6);
        Assert.Equal(3, chip.PortCAlternative);
        Assert.Equal(LogicState.Low, board.Out(chip.PortC[1]));

        strobe.State = false;
        sim.Run(1e-6);
        Assert.Equal(LogicState.High, board.Out(chip.PortC[1]));      // BF
        Assert.Equal(LogicState.Low, board.Out(chip.PortC[0]));       // no INTR yet
        strobe.State = true;
        sim.Run(30e-6);
        Assert.Equal(0x3C, chip.ReadByte(0x40));
        Assert.Equal(1, chip.ReadByte(0x41));
        Assert.Equal(LogicState.Low, board.Out(chip.PortC[0]));
        Assert.Equal(LogicState.Low, board.Out(chip.PortC[1]));
        Assert.Equal("RST 6.5", c.Cpu.Core.LastInterrupt);
    }

    /// <summary>Strobed output (ALT4, both ports out): a write raises BF and drops INTR; the peripheral's /STB drops BF and raises INTR.</summary>
    [Fact]
    public void StrobedOutputHandshakes()
    {
        var board = new Board("""
                    mvi a,$3B       ; A and B out, ALT4, both interrupts enabled
                    out $80
                    mvi a,$77
                    out $82
            done:   jmp done
            """);
        var c = board.Computer;
        var chip = board.Chip;
        var strobeA = c.Toggle(chip.PortC[2], true);
        c.Toggle(chip.PortC[5], true);
        var sim = c.Simulator;
        sim.Run(8e-6);
        Assert.Equal(4, chip.PortCAlternative);
        Assert.Equal(LogicState.High, board.Out(chip.PortC[0]));      // A INTR high on entering output mode
        sim.Run(20e-6);
        Assert.Equal(LogicState.High, board.Out(chip.PortC[4]));      // B BF after the write
        Assert.Equal(LogicState.Low, board.Out(chip.PortC[3]));       // B INTR dropped by it
        Assert.Equal(0x77, chip.OutputLatches.B);
        Assert.Equal(0x10 | 0x20 | 0x01 | 0x04, chip.Status & 0x3F);

        strobeA.State = false;
        sim.Run(1e-6);
        Assert.Equal(LogicState.Low, board.Out(chip.PortC[1]));
        strobeA.State = true;
        sim.Run(1e-6);
        Assert.Equal(LogicState.High, board.Out(chip.PortC[0]));
    }

    /// <summary>RESET (the 8085's RESET OUT) turns the ports to inputs, clears the status and stops the timer, keeping the count length.</summary>
    [Fact]
    public void ResetReturnsThePortsToInputs()
    {
        var board = new Board("""
                    mvi a,10
                    out $84
                    mvi a,$40
                    out $85
                    mvi a,$C3       ; A and B out, start
                    out $80
                    mvi a,$FF
                    out $81
            done:   jmp done
            """);
        var c = board.Computer;
        var reset = c.Toggle(c.Cpu.ResetIn, true);
        c.Simulator.Run(40e-6);
        Assert.True(board.Chip.TimerRunning);
        Assert.Equal(LogicState.High, board.Out(board.Chip.PortA[0]));

        reset.State = false;
        c.Simulator.Run(2e-6);
        Assert.False(board.Chip.TimerRunning);
        Assert.Equal(0, board.Chip.Command);
        Assert.Equal(LogicState.HighImpedance, board.Out(board.Chip.PortA[0]));
        Assert.Equal(LogicState.High, board.Out(board.Chip.TimerOut));
        Assert.Equal(0x400A, board.Chip.CountRegister);
    }

    [Fact]
    public void RunStateCarriesTheRamAndTimer()
    {
        var chip = new Ram8155();
        chip.WriteByte(0x12, 0x34);
        var state = chip.CaptureRunState();
        var other = new Ram8156();
        other.AdoptRunState(state);
        Assert.Equal(0x34, other.ReadByte(0x12));
        Assert.Equal("/CE", chip.PinLabel(chip.ChipEnable));
        Assert.Equal("CE", other.PinLabel(other.ChipEnable));
    }
}
