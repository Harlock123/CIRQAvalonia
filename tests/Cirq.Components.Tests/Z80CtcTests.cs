using Cirq.Components.Digital;
using Cirq.Components.Processors;
using Cirq.Core.Digital;

namespace Cirq.Components.Tests;

/// <summary>
/// The Z80 CTC on a Z80's bus, driven by Z80 programs: timers with both prescalers, counters on
/// either edge, a timer started by its trigger, the down-counter read back, ZC/TO's pulse, a time
/// constant changed on the run, the software reset and the /RESET pin, and the vector with the
/// channel in bits 2-1 and the channels' priority in interrupt mode 2.
/// </summary>
public class Z80CtcTests
{
    /// <summary>The Z80 board at 4 MHz with a CTC at $A0: channels 0-3 at $A0-$A3 (CS0 = A0, CS1 = A1).</summary>
    private sealed class Bench
    {
        public CpuZ80CircuitTests.Computer Computer { get; }

        public Z80Ctc Ctc { get; }

        public Bench(string source)
        {
            Computer = new CpuZ80CircuitTests.Computer(CpuZ80CircuitTests.Rom(Prelude + "\n" + source));
            var c = Computer;
            Ctc = c.Peripheral(new Z80Ctc(), c.SelectA0);
            c.Circuit.Connect(Ctc.ChannelSelect0, c.Cpu.Address[0]);
            c.Circuit.Connect(Ctc.ChannelSelect1, c.Cpu.Address[1]);
            ResetToggle = c.Toggle(Ctc.Reset, true);
            foreach (var trigger in Ctc.Triggers) Triggers.Add(c.Toggle(trigger, false));
        }

        /// <summary>A switch on /RESET, high.</summary>
        public LogicToggle ResetToggle { get; }

        /// <summary>Switches on CLK/TRG0-3, low.</summary>
        public List<LogicToggle> Triggers { get; } = [];

        public void Run(double seconds) => Computer.Simulator.Run(seconds);

        public byte Ram(int address) => Computer.Ram.ReadByte(address - 0x8000);

        public void Pulse(int channel)
        {
            Triggers[channel].State = true;
            Run(1e-6);
            Triggers[channel].State = false;
            Run(1e-6);
        }
    }

    /// <summary>Mode 2 with I = 0 and the CTC's vector $10: channels 0-3 call isr0-isr3, each counting in RAM at $8010 + channel.</summary>
    private const string Prelude = """
                    jp start
                    org $0010
                    dw isr0,isr1,isr2,isr3
                    org $0100
            start:  ld sp,$0000
                    xor a
                    ld i,a
                    im 2
                    ld a,$10
                    out ($A0),a     ; the vector, written to channel 0
            """;

    private const string Handlers = """
            isr0:   push hl
                    ld hl,$8010
                    jr count
            isr1:   push hl
                    ld hl,$8011
                    jr count
            isr2:   push hl
                    ld hl,$8012
                    jr count
            isr3:   push hl
                    ld hl,$8013
            count:  inc (hl)
                    ld a,(hl)
                    ld ($8020),a
                    pop hl
                    ei
                    reti
            """;

    [Fact]
    public void ThePinoutIsTheDatasheets()
    {
        var ctc = new Z80Ctc();
        string At(int pin) => ctc.Terminals.Single(t => t.Id == $"p{pin}").Name;

        Assert.Equal(28, ctc.Terminals.Count);
        Assert.Equal(["D4", "D7", "GND", "RD", "ZCTO0", "ZCTO2", "IORQ", "IEO", "INT", "IEI", "M1", "CLK", "CLKTRG3", "RESET"],
            new[] { 1, 4, 5, 6, 7, 9, 10, 11, 12, 13, 14, 15, 16, 17 }.Select(At));
        Assert.Equal(["CS1", "CS0", "CE", "CLKTRG2", "CLKTRG0", "VCC", "D0", "D3"], new[] { 18, 19, 20, 21, 23, 24, 25, 28 }.Select(At));
        Assert.Equal("ZC/TO1", ctc.PinLabel(ctc.ZeroCount[1]));
        Assert.Equal("CLK/TRG3", ctc.PinLabel(ctc.Triggers[3]));
    }

    /// <summary>A timer with prescaler 16 and time constant 10 counts to zero every 160 clocks — 40 µs at 4 MHz — and ZC/TO0 pulses for a clock.</summary>
    [Fact]
    public void TheTimerCountsTheClockThroughItsPrescaler()
    {
        var bench = new Bench("""
                    ld a,$07        ; timer, prescaler 16, starting at once, a time constant follows, reset
                    out ($A0),a
                    ld a,10
                    out ($A0),a
                    ld a,$27        ; prescaler 256 on channel 1
                    out ($A1),a
                    ld a,2
                    out ($A1),a
            done:   jr done
            """ + "\n" + Handlers);
        bench.Run(80e-6);
        var start = bench.Ctc.Describe(0).ZeroCounts;
        var highSince = double.NaN;
        var widths = new List<double>();
        var sim = bench.Computer.Simulator;
        var zc = bench.Ctc.ZeroCount[0];
        for (var i = 0; i < 16000; i++)
        {
            sim.Run(25e-9);
            var high = bench.Ctc.GetOutputState(bench.Ctc.OutputTerminals.ToList().IndexOf(zc)) == LogicState.High;
            if (high && double.IsNaN(highSince)) highSince = sim.Time;
            if (!high && !double.IsNaN(highSince))
            {
                widths.Add(sim.Time - highSince);
                highSince = double.NaN;
            }
        }

        // 400 µs: ten zero counts, each pulse one 250 ns clock long.
        Assert.InRange(bench.Ctc.Describe(0).ZeroCounts - start, 9, 11);
        Assert.All(widths, w => Assert.InRange(w, 200e-9, 300e-9));
        Assert.Equal(40e-6, bench.Ctc.TimerPeriod(0, 4e6), 9);
        Assert.InRange(bench.Ctc.Describe(1).ZeroCounts, 2, 4);    // 256 x 2 clocks: one every 128 µs
        Assert.Equal(0, bench.Ram(0x8010));                         // interrupts off
    }

    /// <summary>A counter counts CLK/TRG edges, the one the control word picks, and its down-counter reads back.</summary>
    [Fact]
    public void TheCounterCountsItsEdges()
    {
        var bench = new Bench("""
                    ld a,$57        ; counter, rising edges, a time constant follows, reset
                    out ($A1),a
                    ld a,5
                    out ($A1),a
                    ld a,$47        ; channel 2 counts falling edges
                    out ($A2),a
                    ld a,5
                    out ($A2),a
            loop:   in a,($A1)
                    ld ($8030),a
                    in a,($A2)
                    ld ($8031),a
                    jr loop
            """ + "\n" + Handlers);
        bench.Run(80e-6);

        bench.Pulse(1);
        bench.Pulse(1);
        bench.Triggers[2].State = true;
        bench.Run(30e-6);
        Assert.Equal(3, bench.Ram(0x8030));         // two rising edges counted from 5
        Assert.Equal(5, bench.Ram(0x8031));         // a rising edge is not channel 2's

        bench.Triggers[2].State = false;
        bench.Run(30e-6);
        Assert.Equal(4, bench.Ram(0x8031));

        for (var i = 0; i < 3; i++) bench.Pulse(1);
        bench.Run(30e-6);
        Assert.Equal(1, bench.Ctc.Describe(1).ZeroCounts);
        Assert.Equal(5, bench.Ram(0x8030));         // reloaded
    }

    /// <summary>With bit 3 set, a timer loaded with its time constant waits for its trigger's edge to start.</summary>
    [Fact]
    public void ATriggeredTimerWaitsForItsEdge()
    {
        var bench = new Bench("""
                    ld a,$1F        ; timer, rising trigger starts it, a time constant follows, reset
                    out ($A2),a
                    ld a,4
                    out ($A2),a
            done:   jr done
            """ + "\n" + Handlers);
        bench.Run(100e-6);
        Assert.True(bench.Ctc.Describe(2).AwaitingTrigger);
        Assert.Equal(0, bench.Ctc.Describe(2).ZeroCounts);

        bench.Pulse(2);
        bench.Run(100e-6);
        Assert.False(bench.Ctc.Describe(2).AwaitingTrigger);
        Assert.True(bench.Ctc.Describe(2).ZeroCounts >= 6);       // every 64 clocks, 16 µs
    }

    /// <summary>Channel 2's interrupt goes through the vector with 2 in bits 2-1: $14, to isr2.</summary>
    [Fact]
    public void TheVectorCarriesTheChannel()
    {
        var bench = new Bench("""
                    ld a,$87        ; interrupts on, timer, prescaler 16, a time constant follows, reset
                    out ($A2),a
                    ld a,25         ; 400 clocks: 100 µs
                    out ($A2),a
                    ei
            done:   jr done
            """ + "\n" + Handlers);
        bench.Run(450e-6);

        Assert.InRange(bench.Ram(0x8012), 3, 5);
        Assert.Equal(0, bench.Ram(0x8010));
        Assert.Equal(0x14, bench.Computer.Cpu.Core.LastAcknowledgeByte);
        Assert.Equal(0x10, bench.Ctc.Vector);
        Assert.Equal(bench.Ctc.Acknowledges, bench.Ctc.Returns + (bench.Ctc.InService.Any(s => s) ? 1 : 0));
    }

    /// <summary>Channels 0 and 3 reaching zero on the same edge: channel 0 is served first, channel 3 after its RETI.</summary>
    [Fact]
    public void ChannelZeroOutranksChannelThree()
    {
        var bench = new Bench("""
                    ld a,$D7        ; interrupts on, counter, rising, a time constant follows, reset
                    out ($A0),a
                    ld a,1
                    out ($A0),a
                    ld a,$D7
                    out ($A3),a
                    ld a,1
                    out ($A3),a
                    ei
            done:   jr done
            """ + "\n" + Handlers);
        bench.Run(80e-6);

        // Both triggers at once.
        bench.Triggers[0].State = true;
        bench.Triggers[3].State = true;
        bench.Run(60e-6);
        Assert.Equal(1, bench.Ram(0x8010));
        Assert.Equal(1, bench.Ram(0x8013));
        Assert.Equal(1, bench.Ram(0x8020));          // the last handler saw its count at 1: both ran
        Assert.Equal(2, bench.Ctc.Returns);
    }

    /// <summary>A time constant written while the channel runs is taken at the next zero; a software reset stops it; /RESET stops all and disables the interrupts.</summary>
    [Fact]
    public void TimeConstantsAndResets()
    {
        var bench = new Bench("""
                    ld a,$57
                    out ($A0),a
                    ld a,3
                    out ($A0),a
            wait:   in a,($A0)
                    cp 2
                    jr nz,wait
                    ld a,$55        ; counter, rising, a time constant follows, no reset
                    out ($A0),a
                    ld a,7
                    out ($A0),a
            done:   jr done
            """ + "\n" + Handlers);
        bench.Run(80e-6);
        bench.Pulse(0);
        bench.Run(20e-6);
        Assert.Equal(7, bench.Ctc.Describe(0).TimeConstant);
        Assert.Equal(2, bench.Ctc.Describe(0).Counter);              // still counting the old one
        bench.Pulse(0);
        bench.Pulse(0);
        Assert.Equal(7, bench.Ctc.Describe(0).Counter);              // reloaded with the new

        bench.ResetToggle.State = false;
        bench.Run(2e-6);
        bench.ResetToggle.State = true;
        Assert.False(bench.Ctc.Describe(0).Running);
        var counter = bench.Ctc.Describe(0).Counter;
        bench.Pulse(0);
        Assert.Equal(counter, bench.Ctc.Describe(0).Counter);
    }

    /// <summary>A control word with bit 1 set stops the channel until a new time constant.</summary>
    [Fact]
    public void ASoftwareResetStopsTheChannel()
    {
        var bench = new Bench("""
                    ld a,$07
                    out ($A1),a
                    ld a,4
                    out ($A1),a
                    ld b,0
            spin:   djnz spin       ; 256 x 13 T-states: 832 µs
                    ld a,$03        ; reset, no time constant
                    out ($A1),a
            done:   jr done
            """ + "\n" + Handlers);
        bench.Run(900e-6);
        var counts = bench.Ctc.Describe(1).ZeroCounts;
        Assert.True(counts > 40);
        Assert.False(bench.Ctc.Describe(1).Running);
        bench.Run(100e-6);
        Assert.Equal(counts, bench.Ctc.Describe(1).ZeroCounts);
    }
}
