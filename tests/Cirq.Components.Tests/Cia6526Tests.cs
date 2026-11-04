using Cirq.Components.Digital;
using Cirq.Components.Processors;
using Cirq.Core.Digital;

namespace Cirq.Components.Tests;

/// <summary>
/// The 6526 on a 6502's bus at $4000, every register and mode driven by a program: the ports and
/// their pull-ups, /PC, both timers in each of their modes, PB6 and PB7, the time-of-day clock with
/// its latch, its stop, its rollovers and its alarm, the serial port both ways, /FLAG, the interrupt
/// control register and /IRQ.
/// </summary>
public class Cia6526Tests
{
    private const int Pb6 = 14, Pb7 = 15, PcIndex = 24, SpIndex = 25, CntIndex = 26, IrqIndex = 27;

    private const string Header = """
        PRA    = $4000
        PRB    = $4001
        DDRA   = $4002
        DDRB   = $4003
        TALO   = $4004
        TAHI   = $4005
        TBLO   = $4006
        TBHI   = $4007
        TOD10  = $4008
        TODSEC = $4009
        TODMIN = $400A
        TODHR  = $400B
        SDR    = $400C
        ICR    = $400D
        CRA    = $400E
        CRB    = $400F
                .org $8000
        reset:  ldx #$FF
                txs
        """;

    private const string Vectors = """
                .org $FFFC
                .word reset
                .word irq
        """;

    /// <summary>The computer with a 6526 at $4000-$7FFF: /CS the I/O select, RS0-RS3 on A0-A3.</summary>
    private static (Cpu6502CircuitTests.Computer Computer, Cia6526 Cia) WithCia(string body)
    {
        var rom = Assembler6502.Assemble(Header + "\n" + body + "\n" + Vectors).Rom32K();
        var computer = new Cpu6502CircuitTests.Computer(rom);
        var circuit = computer.Circuit;
        var cia = circuit.Add(new Cia6526());
        circuit.Connect(cia.Vcc, computer.Rail.Positive);
        circuit.Connect(cia.Gnd, computer.Ground.Pin);

        var cpu = computer.Cpu;
        for (var i = 0; i < 8; i++) circuit.Connect(cia.Data[i], cpu.Data[i]);
        for (var i = 0; i < 4; i++) circuit.Connect(cia.RegisterSelect[i], cpu.Address[i]);
        circuit.Connect(computer.IoSelect, cia.ChipSelect);
        circuit.Connect(cia.Clock, computer.Clock.Output);
        circuit.Connect(cia.ReadWrite, cpu.ReadWrite);
        circuit.Connect(cia.Reset, cpu.Reset);
        circuit.Connect(cia.Irq, cpu.Irq);
        return (computer, cia);
    }

    /// <summary>The times an output of the CIA goes to a level from now on.</summary>
    private static List<double> Edges(Cpu6502CircuitTests.Computer computer, Cia6526 cia, int output, LogicState level)
    {
        List<double> edges = [];
        computer.Simulator.Scheduler.EventApplied += e =>
        {
            if (ReferenceEquals(e.Device, cia) && e.OutputIndex == output && e.NewState == level) edges.Add(e.Timestamp);
        };
        return edges;
    }

    [Fact]
    public void ThePinoutIsTheDatasheets()
    {
        var cia = new Cia6526();
        string Name(int pin) => cia.Terminals.Single(t => t.Id == $"p{pin}").Name;

        Assert.Equal(40, cia.Terminals.Count);
        Assert.Equal("VSS", Name(1));
        Assert.Equal("PA0", Name(2));
        Assert.Equal("PA7", Name(9));
        Assert.Equal("PB0", Name(10));
        Assert.Equal("PB7", Name(17));
        Assert.Equal(["PC", "TOD", "VCC", "IRQ", "RW", "CS", "FLAG", "PHI2"], Enumerable.Range(18, 8).Select(Name));
        Assert.Equal("D7", Name(26));
        Assert.Equal("D0", Name(33));
        Assert.Equal(["RES", "RS3", "RS2", "RS1", "RS0", "SP", "CNT"], Enumerable.Range(34, 7).Select(Name));
        Assert.Equal("/FLAG", cia.PinLabel(cia.Flag));
        Assert.Equal("R/W", cia.PinLabel(cia.ReadWrite));
    }

    /// <summary>
    /// DDRA makes the top nibble outputs; a read of PRA gives the pins — the outputs, the inputs held
    /// low, and the open ones high on the pull-ups. Port B, all inputs, reads $FF with nothing on it.
    /// </summary>
    [Fact]
    public void ThePortsDriveTheirOutputsAndReadThePinsThroughThePullUps()
    {
        var (computer, cia) = WithCia("""
                lda #$F0
                sta DDRA
                lda #$A5
                sta PRA
                lda PRA
                sta $0200
                lda PRB
                sta $0201
                lda DDRA
                sta $0202
        done:   jmp done
        irq:    rti
        """);
        computer.Circuit.Connect(cia.PortA[0], computer.Ground.Pin);
        computer.Circuit.Connect(cia.PortA[2], computer.Ground.Pin);
        var sim = computer.Simulator;
        sim.Run(60e-6);

        Assert.Equal(0xF0, cia.DirectionA);
        Assert.Equal(0xA5, cia.OutputA);
        Assert.Equal(0xAA, computer.Ram.ReadByte(0x200));   // $A from the outputs, 1010 from the pins
        Assert.Equal(0xFF, computer.Ram.ReadByte(0x201));
        Assert.Equal(0xF0, computer.Ram.ReadByte(0x202));
        Assert.True(sim.NodeVoltage(cia.PortA[1]) > 4.5, "an open input sits at the pull-up");
        Assert.True(sim.NodeVoltage(cia.PortA[6]) < 0.8);
        Assert.Equal(LogicState.HighImpedance, cia.GetOutputState(0));
    }

    /// <summary>A read or a write of port B takes /PC low for the one cycle after it; port A does not.</summary>
    [Fact]
    public void PortControlPulsesForACycleAfterEachPortBAccess()
    {
        var (computer, cia) = WithCia("""
                lda PRA
                lda PRB
                sta PRB
        done:   jmp done
        irq:    rti
        """);
        var lows = Edges(computer, cia, PcIndex, LogicState.Low);
        var highs = Edges(computer, cia, PcIndex, LogicState.High);
        computer.Simulator.Run(40e-6);

        Assert.Equal(2, lows.Count);
        Assert.All(lows.Zip(highs.Skip(highs.Count - lows.Count)), p => Assert.Equal(1e-6, p.Second - p.First, 8));
    }

    /// <summary>
    /// Timer A continuous from a latch of 99: an underflow every 100 cycles, PB6 toggling at each so a
    /// 200 µs square wave, and the interrupt taken each time by a handler that reads ICR to clear it.
    /// </summary>
    [Fact]
    public void TimerARunsContinuouslyTogglesPb6AndInterrupts()
    {
        var (computer, cia) = WithCia("""
                lda #99
                sta TALO
                lda #0
                sta TAHI
                stz $10
                lda #$81        ; enable the timer A interrupt
                sta ICR
                lda #%00010111  ; force load, toggle PB6, PB6 on, start
                sta CRA
                cli
        done:   jmp done
        irq:    pha
                lda ICR         ; clears the flag and lets /IRQ go
                sta $11
                inc $10
                pla
                rti
        """);
        var sim = computer.Simulator;
        sim.Run(5e-6);
        var rises = Edges(computer, cia, Pb6, LogicState.High);
        sim.Run(1.2e-3);

        var periods = rises.Zip(rises.Skip(1), (a, b) => b - a).ToList();
        Assert.True(periods.Count >= 4);
        Assert.All(periods, p => Assert.Equal(200e-6, p, 8));
        Assert.InRange(computer.Ram.ReadByte(0x10), 11, 12);
        Assert.Equal(0x81, computer.Ram.ReadByte(0x11));      // IR and the timer A flag
    }

    /// <summary>
    /// The original 6526 pulls /IRQ the cycle after the flag is set — PB7's one-cycle pulse marks
    /// the underflow — and in pulse mode PB7 is high for exactly one cycle per underflow.
    /// </summary>
    [Fact]
    public void TimerBPulsesPb7AndIrqFollowsTheFlagByACycle()
    {
        var (computer, cia) = WithCia("""
                lda #49
                sta TBLO
                lda #0
                sta TBHI
                lda #$82
                sta ICR
                lda #%00010011  ; force load, pulse PB7, PB7 on, start
                sta CRB
        done:   jmp done
        irq:    rti
        """);
        var sim = computer.Simulator;
        var rises = Edges(computer, cia, Pb7, LogicState.High);
        var falls = Edges(computer, cia, Pb7, LogicState.Low);
        var irq = Edges(computer, cia, IrqIndex, LogicState.Low);
        sim.Run(200e-6);

        Assert.True(rises.Count >= 3);
        Assert.Equal(50e-6, rises[2] - rises[1], 8);
        Assert.All(rises.Take(3), r => Assert.Contains(falls, f => Math.Abs(f - r - 1e-6) < 1e-9));
        Assert.Equal(rises[0] + 1e-6, irq[0], 9);
        Assert.Equal(0x82, cia.InterruptFlags);
    }

    /// <summary>
    /// One-shot: writing the high byte with the timer stopped loads it and starts it; it underflows
    /// once, stops, and clears its start bit. Reading ICR gives the flag with bit 7 only when enabled,
    /// and clears it.
    /// </summary>
    [Fact]
    public void AOneShotStartsOnTheHighByteAndStopsAfterOneUnderflow()
    {
        var (computer, cia) = WithCia("""
                lda #%00001000  ; one-shot, stopped
                sta CRA
                lda #$20
                sta TALO
                lda #$00
                sta TAHI        ; loads and starts
        wait:   lda ICR
                beq wait
                sta $0200       ; the flag without IR: not enabled
                lda ICR
                sta $0201       ; cleared by the read
                lda CRA
                sta $0202
        done:   jmp done
        irq:    rti
        """);
        computer.Simulator.Run(150e-6);

        Assert.Equal(Cia6526.FlagTimerA, computer.Ram.ReadByte(0x200));
        Assert.Equal(0, computer.Ram.ReadByte(0x201));
        Assert.Equal(0x08, computer.Ram.ReadByte(0x202));
        Assert.Equal(0x20, cia.TimerA);
        Assert.Equal(0x0020, cia.TimerALatch);
    }

    /// <summary>The force-load strobe puts the latch in the counter at once, and reads back as zero.</summary>
    [Fact]
    public void ForceLoadReloadsARunningTimer()
    {
        var (computer, cia) = WithCia("""
                lda #$F0
                sta TALO
                stz TAHI        ; latch $00F0, loaded: the timer is stopped
                lda #%00000001
                sta CRA         ; start
                ldx #20
        spin:   dex
                bne spin
                lda TALO
                sta $0200       ; a hundred-odd cycles down
                lda #%00010001
                sta CRA         ; force load while running
                lda TALO
                sta $0201       ; back near the top
                lda CRA
                sta $0202
        done:   jmp done
        irq:    rti
        """);
        computer.Simulator.Run(200e-6);

        Assert.InRange(computer.Ram.ReadByte(0x200), 0x60, 0x9F);
        Assert.InRange(computer.Ram.ReadByte(0x201), 0xE8, 0xF0);
        Assert.Equal(0x01, computer.Ram.ReadByte(0x202));
        Assert.Equal(0x00F0, cia.TimerALatch);
    }

    /// <summary>Timer B counting timer A's underflows: ten cycles a count, five counts an underflow.</summary>
    [Fact]
    public void TimerBCountsTimerAUnderflows()
    {
        var (computer, cia) = WithCia("""
                lda #9
                sta TALO
                stz TAHI
                lda #4
                sta TBLO
                stz TBHI
                lda #%01010001  ; timer B: count A's underflows, force load, start
                sta CRB
                lda #%00010001
                sta CRA
        done:   jmp done
        irq:    rti
        """);
        var sim = computer.Simulator;
        var pb7 = Edges(computer, cia, Pb7, LogicState.High);
        sim.Run(40e-6);

        // Sampled every microsecond for a hundred: one count every ten, 4, 3, 2, 1, 0 and round again.
        List<ushort> changes = [];
        var last = cia.TimerB;
        for (var i = 0; i < 100; i++)
        {
            sim.Run(1e-6);
            if (cia.TimerB == last) continue;
            Assert.Equal((last + 4) % 5, cia.TimerB);
            changes.Add(last = cia.TimerB);
        }

        Assert.InRange(changes.Count, 9, 11);
        Assert.NotEqual(0, cia.InterruptFlags & Cia6526.FlagTimerB);
        Assert.Empty(pb7);                             // PB7 is the port's while PBON is off
    }

    /// <summary>CNT as a count input: timer A on its rising edges, and timer B on A's underflows only while CNT is high.</summary>
    [Fact]
    public void CntCountsForTheTimersAndGatesTimerB()
    {
        var (computer, cia) = WithCia("""
                lda #2
                sta TALO
                stz TAHI
                lda #$FF
                sta TBLO
                sta TBHI
                lda #%01110001  ; timer B: A's underflows while CNT is high
                sta CRB
                lda #%00110001  ; timer A: CNT's rising edges, force load, start
                sta CRA
        done:   jmp done
        irq:    rti
        """);
        var cnt = computer.Circuit.Add(new LogicToggle { State = true });
        computer.Circuit.Connect(cnt.Out, cia.Count);
        var sim = computer.Simulator;
        sim.Run(80e-6);
        Assert.Equal(2, cia.TimerA);

        void Pulse()
        {
            cnt.State = false;
            sim.Run(3e-6);
            cnt.State = true;
            sim.Run(3e-6);
        }

        Pulse();
        Assert.Equal(1, cia.TimerA);
        Pulse();
        Assert.Equal(0, cia.TimerA);
        Assert.Equal(0xFFFF, cia.TimerB);
        Pulse();                         // the underflow, with CNT high as the cycle ends: B counts
        Assert.Equal(2, cia.TimerA);
        Assert.Equal(0xFFFE, cia.TimerB);
        Assert.NotEqual(0, cia.InterruptFlags & Cia6526.FlagTimerA);
    }

    /// <summary>Timer B counting CNT's rising edges, and flagging on the underflow after its latch's worth.</summary>
    [Fact]
    public void TimerBCountsCntEdges()
    {
        var (computer, cia) = WithCia("""
                lda #3
                sta TBLO
                stz TBHI
                lda #%00110001  ; CNT edges, force load, start
                sta CRB
        done:   jmp done
        irq:    rti
        """);
        var clock = computer.Circuit.Add(new ClockSource(50e3));
        computer.Circuit.Connect(clock.Out, cia.Count);
        var sim = computer.Simulator;
        sim.Run(30e-6);
        Assert.Equal(0, cia.InterruptFlags & Cia6526.FlagTimerB);
        sim.Run(80e-6);
        Assert.NotEqual(0, cia.InterruptFlags & Cia6526.FlagTimerB);
    }

    /// <summary>The time-of-day clock from a fast TOD input: set hours first (which stops it), read with the latch, and roll 11:59:59.9 AM over to 12 PM.</summary>
    [Fact]
    public void TheTimeOfDayClockSetsStopsLatchesAndRollsOver()
    {
        var (computer, cia) = WithCia("""
                lda #%10000000  ; 50 Hz: five TOD edges a tenth
                sta CRA
                lda #$11        ; 11 AM: the clock stops
                sta TODHR
                ldx #100
        hold:   dex
                bne hold
                lda #$59
                sta TODMIN
                sta TODSEC
                lda #$08
                sta TOD10       ; and starts on the tenth
                ldx #60
        run:    dex
                bne run
                lda TODHR       ; latches all four
                sta $0200
                ldx #30
        later:  dex
                bne later
                lda TODMIN
                sta $0201
                lda TODSEC
                sta $0202
                lda TOD10       ; the latched tenths, and the latch lets go
                sta $0203
                lda TOD10
                sta $0204       ; the clock as it now is
        done:   jmp done
        irq:    rti
        """);
        var tod = computer.Circuit.Add(new ClockSource(100e3));    // a tenth every 50 µs
        computer.Circuit.Connect(tod.Out, cia.TimeOfDayInput);
        var sim = computer.Simulator;

        sim.Run(200e-6);
        Assert.True(cia.IsTimeOfDayStopped);
        Assert.Equal((byte)0, cia.TimeOfDay.Tenths);
        sim.Run(1.2e-3);

        Assert.False(cia.IsTimeOfDayStopped);
        Assert.Equal(0x92, computer.Ram.ReadByte(0x200));   // 12 PM
        Assert.Equal(0x00, computer.Ram.ReadByte(0x201));
        Assert.Equal(0x00, computer.Ram.ReadByte(0x202));
        var latched = computer.Ram.ReadByte(0x203);
        Assert.True(computer.Ram.ReadByte(0x204) > latched, "reading the tenths let the latch go");
        Assert.StartsWith("12:00:0", cia.TimeOfDayText);
        Assert.EndsWith("PM", cia.TimeOfDayText);
    }

    /// <summary>12:59:59.9 goes round to 1:00:00.0 with PM kept; and the alarm, written with CRB bit 7, flags and interrupts.</summary>
    [Fact]
    public void TheAlarmInterruptsAndTwelveGoesRoundToOne()
    {
        var (computer, cia) = WithCia("""
                lda #%10000000  ; alarm writes
                sta CRB
                lda #$81
                sta TODHR
                stz TODMIN
                stz TODSEC
                lda #$02
                sta TOD10       ; alarm 1:00:00.2 PM
                stz CRB         ; time writes
                lda #%10000000
                sta CRA
                lda #$92
                sta TODHR
                lda #$59
                sta TODMIN
                sta TODSEC
                lda #$09
                sta TOD10       ; 12:59:59.9 PM
                lda #$84
                sta ICR         ; the alarm interrupt
                cli
        done:   jmp done
        irq:    lda ICR
                sta $0200
                lda TODHR
                sta $0201
                lda TOD10
                sta $0202
                rti
        """);
        var tod = computer.Circuit.Add(new ClockSource(100e3));
        computer.Circuit.Connect(tod.Out, cia.TimeOfDayInput);
        computer.Simulator.Run(400e-6);

        Assert.Equal(0x84, computer.Ram.ReadByte(0x200));
        Assert.Equal(0x81, computer.Ram.ReadByte(0x201));
        Assert.Equal(0x02, computer.Ram.ReadByte(0x202));
        Assert.Equal(((byte)0x02, (byte)0, (byte)0, (byte)0x81), cia.Alarm);
    }

    /// <summary>
    /// Serial output: two bytes written back to back go out on SP most significant bit first, a bit
    /// per two timer A underflows, each good as CNT rises — and the SP flag sets after each.
    /// </summary>
    [Fact]
    public void TheSerialPortShiftsBytesOutMsbFirstClockedByTimerA()
    {
        var (computer, cia) = WithCia("""
                lda #3
                sta TALO
                stz TAHI
                lda #%01010001  ; serial out, force load, continuous, start
                sta CRA
                lda #$A5
                sta SDR
                lda #$3C
                sta SDR         ; queued behind the first
        wait:   lda ICR
                and #%00001000
                beq wait
                inc $0200
                jmp wait
        irq:    rti
        """);
        var sim = computer.Simulator;
        List<int> bits = [];
        List<double> clocks = [];
        var cnt = LogicState.HighImpedance;
        sim.Scheduler.EventApplied += e =>
        {
            if (!ReferenceEquals(e.Device, cia) || e.OutputIndex != CntIndex) return;

            // A rising edge of the shift clock, not CNT being taken over as an output in the first place.
            if (cnt == LogicState.Low && e.NewState == LogicState.High)
            {
                bits.Add(cia.GetOutputState(SpIndex) == LogicState.High ? 1 : 0);
                clocks.Add(e.Timestamp);
            }

            cnt = e.NewState;
        };
        sim.Run(250e-6);

        Assert.Equal(16, bits.Count);
        Assert.Equal(0xA5, bits.Take(8).Aggregate(0, (v, b) => (v << 1) | b));
        Assert.Equal(0x3C, bits.Skip(8).Aggregate(0, (v, b) => (v << 1) | b));
        Assert.All(clocks.Zip(clocks.Skip(1), (a, b) => b - a), gap => Assert.Equal(8e-6, gap, 8));
        Assert.Equal(2, computer.Ram.ReadByte(0x200));
        Assert.True(sim.NodeVoltage(cia.Count) > 2.4, "CNT idles high");
    }

    /// <summary>Serial input: SP shifted in on CNT's rising edges, the eighth putting the byte in SDR and setting the flag.</summary>
    [Fact]
    public void TheSerialPortShiftsBytesInOnCnt()
    {
        var (computer, cia) = WithCia("""
        wait:   lda ICR
                and #%00001000
                beq wait
                lda SDR
                sta $0200
        done:   jmp done
        irq:    rti
        """);
        var cnt = computer.Circuit.Add(new LogicToggle { State = true });
        var sp = computer.Circuit.Add(new LogicToggle());
        computer.Circuit.Connect(cnt.Out, cia.Count);
        computer.Circuit.Connect(sp.Out, cia.SerialPort);
        var sim = computer.Simulator;
        sim.Run(20e-6);

        foreach (var bit in new[] { 1, 1, 0, 0, 1, 0, 1, 0 })
        {
            sp.State = bit == 1;
            cnt.State = false;
            sim.Run(4e-6);
            cnt.State = true;
            sim.Run(4e-6);
        }

        sim.Run(20e-6);
        Assert.Equal(0xCA, cia.SerialData);
        Assert.Equal(0xCA, computer.Ram.ReadByte(0x200));
    }

    /// <summary>/FLAG's falling edge sets its flag; enabling it pulls /IRQ, and the handler's ICR read lets it go.</summary>
    [Fact]
    public void FlagInterruptsOnItsFallingEdge()
    {
        var (computer, cia) = WithCia("""
                lda #$90
                sta ICR
                cli
        done:   jmp done
        irq:    lda ICR
                sta $0200
                inc $0201
                rti
        """);
        var flag = computer.Circuit.Add(new LogicToggle { State = true });
        computer.Circuit.Connect(flag.Out, cia.Flag);
        var sim = computer.Simulator;
        sim.Run(30e-6);
        Assert.Equal(0, computer.Ram.ReadByte(0x201));

        flag.State = false;
        sim.Run(30e-6);
        flag.State = true;
        sim.Run(10e-6);

        Assert.Equal(0x90, computer.Ram.ReadByte(0x200));
        Assert.Equal(1, computer.Ram.ReadByte(0x201));
        Assert.Equal(LogicState.HighImpedance, cia.GetOutputState(IrqIndex));
    }

    /// <summary>ICR writes: bit 7 set adds the bits given to the mask, clear takes them away.</summary>
    [Fact]
    public void TheMaskIsSetAndClearedByBitSeven()
    {
        var (computer, cia) = WithCia("""
                lda #$9F
                sta ICR
                lda #$05
                sta ICR
        done:   jmp done
        irq:    rti
        """);
        computer.Simulator.Run(30e-6);
        Assert.Equal(0x1A, cia.InterruptMask);
    }

    /// <summary>/RES clears the registers and sets the timer latches to all ones; the ports become inputs.</summary>
    [Fact]
    public void ResetClearsTheRegistersAndSetsTheLatches()
    {
        var (computer, cia) = WithCia("""
                lda #$FF
                sta DDRB
                stz TALO
                stz TAHI
        done:   jmp done
        irq:    rti
        """);
        var button = computer.Circuit.Add(new Cirq.Components.Passive.PushButton());
        computer.Circuit.Connect(button.A, cia.Reset);
        computer.Circuit.Connect(button.B, computer.Ground.Pin);
        var sim = computer.Simulator;
        sim.Run(40e-6);
        Assert.Equal(0xFF, cia.DirectionB);
        Assert.Equal(0, cia.TimerALatch);

        button.IsPressed = true;
        sim.Run(3e-6);
        Assert.Equal(0, cia.DirectionB);
        Assert.Equal(0xFFFF, cia.TimerALatch);
        Assert.Equal(0xFFFF, cia.TimerBLatch);
        Assert.Equal(LogicState.HighImpedance, cia.GetOutputState(8));
    }
}
