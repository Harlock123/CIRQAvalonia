using Cirq.Components.Digital;
using Cirq.Components.Processors;
using Cirq.Core.Digital;
using Computer = Cirq.Components.Tests.Cpu8085CircuitTests.Computer;

namespace Cirq.Components.Tests;

/// <summary>
/// The 8253 and 8254 on an 8085's bus at ports $20-$23 (/CS from the I/O decoder's $20-$3F, A0 and
/// A1 from the address latch), their counters clocked from CLK OUT: every mode's OUT on the pin,
/// counted in CLK pulses, with GATE enabling, stopping and triggering; binary and BCD, the three
/// ways of writing and reading a count, the counter latch command, the 8254's read-back and status,
/// a counter dividing CLK OUT for a baud clock, and OUT interrupting the processor on RST 7.5.
/// </summary>
public class Pit8253Tests
{
    /// <summary>The board with a timer, its GATEs on toggles (high) and its CLKs on CLK OUT, running <paramref name="program"/>.</summary>
    private static (Computer Computer, IntervalTimer Pit, LogicToggle[] Gates) Board(string program, bool is8254 = true)
    {
        var computer = new Computer(Cpu8085CircuitTests.Rom(program));
        var cpu = computer.Cpu;
        IntervalTimer pit = is8254 ? new Pit8254() : new Pit8253();
        computer.Ic(pit);
        var circuit = computer.Circuit;
        for (var i = 0; i < 8; i++) circuit.Connect(pit.Data[i], cpu.AddressData[i]);
        circuit.Connect(pit.A0, computer.LowAddress[0]);
        circuit.Connect(pit.A1, computer.LowAddress[1]);
        circuit.Connect(pit.ChipSelect, computer.Select20);
        circuit.Connect(pit.ReadStrobe, cpu.ReadStrobe);
        circuit.Connect(pit.WriteStrobe, cpu.WriteStrobe);
        foreach (var clock in pit.Clocks) circuit.Connect(clock, cpu.ClockOut);
        var gates = pit.Gates.Select(g => computer.Toggle(g, true)).ToArray();
        return (computer, pit, gates);
    }

    private static bool Out(IntervalTimer pit, int counter) => pit.GetOutputState(8 + counter) == LogicState.High;

    /// <summary>OUT's changes over <paramref name="seconds"/>, each with the processor's T-state count — one per CLK pulse.</summary>
    private static List<(long T, bool High)> Watch(Computer computer, IntervalTimer pit, int counter, double seconds)
    {
        List<(long, bool)> changes = [];
        var sim = computer.Simulator;
        var last = Out(pit, counter);
        var end = sim.Time + seconds;
        while (sim.Time < end)
        {
            sim.Run(80e-9);
            var now = Out(pit, counter);
            if (now != last) changes.Add((computer.Cpu.Core.TStates, now));
            last = now;
        }

        return changes;
    }

    /// <summary>The lengths of the high and low spans between the changes, in CLK pulses.</summary>
    private static List<(bool High, long Pulses)> Spans(List<(long T, bool High)> changes) =>
        [.. changes.Zip(changes.Skip(1), (a, b) => (a.High, b.T - a.T))];

    private const string Setup = """
                    lxi sp,$0000
            """;

    /// <summary>Mode 3 dividing CLK OUT by 2 — a baud-rate clock — and by 5: high for 3 pulses and low for 2.</summary>
    [Fact]
    public void Mode3IsASquareWave()
    {
        var (computer, pit, _) = Board("""
                    mvi a,$36       ; counter 0: LSB then MSB, mode 3, binary
                    out $23
                    mvi a,2
                    out $20
                    xra a
                    out $20
                    mvi a,$76       ; counter 1: mode 3
                    out $23
                    mvi a,5
                    out $21
                    xra a
                    out $21
            done:   jmp done
            """);
        computer.Simulator.Run(20e-6);
        var spans = Spans(Watch(computer, pit, 0, 5e-6));
        Assert.True(spans.Count > 10);
        Assert.All(spans, s => Assert.Equal(1, s.Pulses));

        var five = Spans(Watch(computer, pit, 1, 10e-6));
        Assert.All(five, s => Assert.Equal(s.High ? 3 : 2, s.Pulses));
        Assert.Equal(3, pit.Describe(1).Mode);
    }

    /// <summary>Mode 2 divides by N with OUT low for one pulse of each N; GATE low holds OUT high and stops it, GATE high reloads.</summary>
    [Fact]
    public void Mode2IsARateGenerator()
    {
        var (computer, pit, gates) = Board("""
                    mvi a,$34       ; counter 0: mode 2
                    out $23
                    mvi a,10
                    out $20
                    xra a
                    out $20
            done:   jmp done
            """);
        computer.Simulator.Run(20e-6);
        var spans = Spans(Watch(computer, pit, 0, 15e-6));
        Assert.True(spans.Count > 6);
        Assert.All(spans, s => Assert.Equal(s.High ? 9 : 1, s.Pulses));

        gates[0].State = false;
        computer.Simulator.Run(1e-6);
        Assert.True(Out(pit, 0));
        Assert.Empty(Watch(computer, pit, 0, 10e-6));

        gates[0].State = true;
        var changes = Watch(computer, pit, 0, 10e-6);
        Assert.True(changes.Count >= 4);
        Assert.All(Spans(changes), s => Assert.Equal(s.High ? 9 : 1, s.Pulses));
    }

    /// <summary>Mode 0: OUT low from the control word, high N + 1 pulses after the count; GATE low pauses it.</summary>
    [Fact]
    public void Mode0InterruptsOnTerminalCount()
    {
        var (computer, pit, gates) = Board("""
                    mvi a,$30       ; counter 0: mode 0
                    out $23
                    mvi a,40
                    out $20
                    xra a
                    out $20
            done:   jmp done
            """);
        var sim = computer.Simulator;
        sim.Run(3e-6);
        Assert.False(Out(pit, 0));
        while (!pit.Describe(0).Running || pit.Describe(0).NullCount) sim.Run(80e-9);
        var loaded = computer.Cpu.Core.TStates;     // the pulse after the count: loaded, not counted

        var high = Watch(computer, pit, 0, 20e-6);
        Assert.Single(high);
        Assert.True(high[0].High);
        Assert.Equal(40, high[0].T - loaded);
        Assert.Equal(1, pit.Describe(0).Expirations);

        // Again, paused half way by GATE.
        var (paused, timer, pausedGates) = Board("""
                    mvi a,$30
                    out $23
                    mvi a,100
                    out $20
                    xra a
                    out $20
            done:   jmp done
            """);
        paused.Simulator.Run(20e-6);
        pausedGates[0].State = false;
        paused.Simulator.Run(0.5e-6);
        var held = timer.Describe(0).CountingElement;
        paused.Simulator.Run(20e-6);
        Assert.Equal(held, timer.Describe(0).CountingElement);
        Assert.False(Out(timer, 0));
        pausedGates[0].State = true;
        paused.Simulator.Run(40e-6);
        Assert.True(Out(timer, 0));
    }

    /// <summary>Mode 1: a GATE rising edge takes OUT low for N pulses; a second edge on the way starts it again.</summary>
    [Fact]
    public void Mode1IsARetriggerableOneShot()
    {
        var (computer, pit, gates) = Board("""
                    mvi a,$32       ; counter 0: mode 1
                    out $23
                    mvi a,20
                    out $20
                    xra a
                    out $20
            done:   jmp done
            """);
        gates[0].State = false;
        var sim = computer.Simulator;
        sim.Run(20e-6);
        Assert.True(Out(pit, 0));

        gates[0].State = true;
        var once = Watch(computer, pit, 0, 15e-6);
        Assert.Equal(2, once.Count);
        Assert.Equal(20, once[1].T - once[0].T);

        // Retriggered after 10 pulses: low for 10 + 20.
        gates[0].State = false;
        sim.Run(1e-6);
        gates[0].State = true;
        sim.Run(10 / 3.072e6);
        gates[0].State = false;
        sim.Run(0.2e-6);
        gates[0].State = true;
        var twice = Watch(computer, pit, 0, 15e-6);
        Assert.Single(twice);
        Assert.True(twice[0].High);
        Assert.True(pit.Describe(0).Expirations >= 2);
    }

    /// <summary>Mode 4 strobes OUT low once, N + 1 pulses after the count; mode 5 the same after a GATE edge.</summary>
    [Fact]
    public void Modes4And5Strobe()
    {
        var (computer, pit, gates) = Board("""
                    mvi a,$38       ; counter 0: mode 4
                    out $23
                    mvi a,$7A       ; counter 1: mode 5, LSB then MSB
                    out $23
                    mvi a,30
                    out $21
                    xra a
                    out $21
                    mvi a,30
                    out $20
                    xra a
                    out $20
            done:   jmp done
            """);
        gates[1].State = false;
        computer.Simulator.Run(8e-6);
        var changes = Watch(computer, pit, 0, 40e-6);
        Assert.Equal(2, changes.Count);
        Assert.False(changes[0].High);
        Assert.Equal(1, changes[1].T - changes[0].T);
        Assert.Empty(Watch(computer, pit, 0, 30e-6));     // once only

        Assert.Empty(Watch(computer, pit, 1, 2e-6));
        gates[1].State = true;
        var strobe = Watch(computer, pit, 1, 20e-6);
        Assert.Equal(2, strobe.Count);
        Assert.Equal(1, strobe[1].T - strobe[0].T);
        Assert.Equal(5, pit.Describe(1).Mode);
    }

    /// <summary>BCD: a count of $0010 divides by ten, and the count reads back in BCD; LSB only and MSB only.</summary>
    [Fact]
    public void BcdAndTheByteOrders()
    {
        var (computer, pit, _) = Board("""
                    mvi a,$35       ; counter 0: mode 2, BCD
                    out $23
                    mvi a,$10
                    out $20
                    xra a
                    out $20
                    mvi a,$54       ; counter 1: LSB only, mode 2
                    out $23
                    mvi a,8
                    out $21
                    mvi a,$A4       ; counter 2: MSB only, mode 2
                    out $23
                    mvi a,1         ; 256
                    out $22
            done:   jmp done
            """);
        computer.Simulator.Run(20e-6);
        Assert.All(Spans(Watch(computer, pit, 0, 12e-6)), s => Assert.Equal(s.High ? 9 : 1, s.Pulses));
        Assert.All(Spans(Watch(computer, pit, 1, 12e-6)), s => Assert.Equal(s.High ? 7 : 1, s.Pulses));
        var slow = Spans(Watch(computer, pit, 2, 200e-6));
        Assert.Contains(slow, s => s.High && s.Pulses == 255);
        Assert.True(pit.Describe(0).Bcd);
        Assert.Equal(0x0010, pit.Describe(0).CountRegister);
        Assert.InRange(pit.Describe(0).CountingElement, 1, 0x10);
        Assert.InRange(pit.Describe(0).CountingElement & 0x0F, 0, 9);      // a BCD digit
        Assert.Equal(0x0100, pit.Describe(2).CountRegister);
    }

    /// <summary>
    /// Reads: a counter paused by GATE reads its count low byte then high; the counter latch command
    /// holds a running counter's count while it is read; a second latch before then is ignored.
    /// </summary>
    [Fact]
    public void CountsReadBackWithAndWithoutTheLatch()
    {
        var (computer, pit, gates) = Board("""
                    lxi sp,$0000
                    mvi a,$70       ; counter 1: mode 0, LSB then MSB
                    out $23
                    mvi a,$34
                    out $21
                    mvi a,$12
                    out $21
                    nop
                    nop
                    in $21          ; paused by GATE: the count as loaded
                    sta $8000
                    in $21
                    sta $8001
                    mvi a,$34       ; counter 0: mode 2, a long count, running
                    out $23
                    xra a
                    out $20
                    out $20
                    nop
                    mvi a,$00       ; latch counter 0
                    out $23
                    in $20
                    sta $8002
                    mvi a,$00       ; ignored: a count is already latched
                    out $23
                    mvi b,20
            delay:  dcr b
                    jnz delay
                    in $20
                    sta $8003
                    mvi a,$00       ; latch again
                    out $23
                    in $20
                    sta $8004
                    in $20
                    sta $8005
            done:   jmp done
            """);
        gates[1].State = false;
        computer.Simulator.Run(300e-6);
        var ram = computer.Ram;
        Assert.Equal(0x34, ram.ReadByte(0));
        Assert.Equal(0x12, ram.ReadByte(1));
        var first = ram.ReadByte(2) | (ram.ReadByte(3) << 8);
        var second = ram.ReadByte(4) | (ram.ReadByte(5) << 8);
        Assert.InRange(first, 0xFF00, 0xFFFF);            // counting down from 65536
        Assert.True(second < first && first - second > 100, $"{first:X4} then {second:X4}: the latch held while the delay ran");
        Assert.False(pit.Describe(0).CountLatched);
        Assert.Equal(0, computer.Cpu.UndefinedReads);
    }

    /// <summary>The 8254's read-back: status bytes (OUT, NULL COUNT, RW, mode, BCD) before the counts, two counters at once; the 8253 ignores it.</summary>
    [Fact]
    public void The8254ReadsBackStatusAndCounts()
    {
        const string program = """
                    mvi a,$B0       ; counter 2: mode 0, LSB then MSB
                    out $23
                    mvi a,$E8       ; read-back: status of counter 2 (NULL COUNT, no count yet)
                    out $23
                    in $22
                    sta $8000
                    mvi a,$00
                    out $22
                    mvi a,$10       ; count $1000
                    out $22
                    nop
                    nop
                    mvi a,$C8       ; read-back: count and status of counter 2
                    out $23
                    in $22
                    sta $8001
                    in $22
                    sta $8002
                    in $22
                    sta $8003
            done:   jmp done
            """;
        var (computer, pit, gates) = Board(program);
        gates[2].State = false;     // paused, so the count is exactly as loaded
        computer.Simulator.Run(60e-6);
        var ram = computer.Ram;
        Assert.Equal(0x40 | 0x30, ram.ReadByte(0));     // OUT low, NULL COUNT, RW 11, mode 0, binary
        Assert.Equal(0x30, ram.ReadByte(1));            // loaded: NULL COUNT clear
        Assert.Equal(0x00, ram.ReadByte(2));
        Assert.Equal(0x10, ram.ReadByte(3));
        Assert.Equal(0x30, pit.StatusByte(2));

        var (old, timer, oldGates) = Board(program, is8254: false);
        oldGates[2].State = false;
        old.Simulator.Run(60e-6);
        Assert.NotEqual(0x30, old.Ram.ReadByte(1));     // no status: a byte of the count
        Assert.False(timer.Describe(2).StatusLatched);
        Assert.Equal("8253", timer.PartNumber);
    }

    /// <summary>OUT0 dividing CLK OUT by 307 into RST 7.5: an interrupt every 100 µs, counted by the handler while the processor halts.</summary>
    [Fact]
    public void OutInterruptsTheProcessor()
    {
        var (computer, pit, _) = Board("""
                    jmp start
                    org $003C
                    push psw
                    lda $8000
                    inr a
                    sta $8000
                    pop psw
                    ei
                    ret
            start:  lxi sp,$0000
                    mvi a,$34       ; counter 0: mode 2, ÷ 307: an RST 7.5 edge every 100 µs
                    out $23
                    mvi a,$33
                    out $20
                    mvi a,$01
                    out $20
                    mvi a,$0B       ; unmask RST 7.5
                    sim
                    ei
            idle:   hlt
                    jmp idle
            """);
        computer.Circuit.Connect(pit.Outputs[0], computer.Cpu.Rst75);
        computer.Simulator.Run(1.05e-3);
        Assert.InRange(computer.Ram.ReadByte(0), 9, 11);
        Assert.Equal("RST 7.5", computer.Cpu.Core.LastInterrupt);
        Assert.InRange(pit.Describe(0).Expirations, 9, 11);
    }
}
