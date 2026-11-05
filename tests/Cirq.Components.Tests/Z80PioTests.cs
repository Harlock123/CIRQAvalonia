using Cirq.Components.Digital;
using Cirq.Components.Processors;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Tests;

/// <summary>
/// The Z80 PIO on a Z80's bus, driven by Z80 programs: modes 0, 1, 2 and 3 with their handshakes,
/// the bit-control interrupt logic (OR and AND, high and low, the mask), each port's vector in
/// interrupt mode 2, the enable and disable words, RETI ending a service, and the /M1 reset.
/// </summary>
public class Z80PioTests
{
    /// <summary>The Z80 board with a PIO at $C0: A data $C0, B data $C1, A control $C2, B control $C3 (B/A = A0, C/D = A1).</summary>
    private sealed class Bench
    {
        public CpuZ80CircuitTests.Computer Computer { get; }

        public Z80Pio Pio { get; }

        /// <summary>A toggle on the PIO's /M1 in place of the processor's, when asked for.</summary>
        public LogicToggle? M1 { get; }

        public Bench(string source, bool ownM1 = false)
        {
            Computer = new CpuZ80CircuitTests.Computer(CpuZ80CircuitTests.Rom(Prelude + "\n" + source));
            var c = Computer;
            if (ownM1) M1 = c.Circuit.Add(new LogicToggle(true));
            Pio = c.Peripheral(new Z80Pio(), c.SelectC0, m1: M1?.Out);
            c.Circuit.Connect(Pio.PortSelect, c.Cpu.Address[0]);
            c.Circuit.Connect(Pio.ControlData, c.Cpu.Address[1]);
        }

        public LogicToggle Toggle(Terminal pin, bool state) => Computer.Toggle(pin, state);

        public void Run(double seconds) => Computer.Simulator.Run(seconds);

        public byte Ram(int address) => Computer.Ram.ReadByte(address - 0x8000);

        public LogicState Pin(Terminal pin) => Pio.GetOutputState(Pio.OutputTerminals.ToList().IndexOf(pin));

        public int PortPins(IReadOnlyList<Terminal> port)
        {
            var value = 0;
            for (var i = 0; i < 8; i++) if (Pin(port[i]) == LogicState.High) value |= 1 << i;
            return value;
        }
    }

    /// <summary>
    /// Mode 2 interrupts with I = 0: port A's vector $40 and port B's $42 point at the handlers
    /// <c>isra</c> and <c>isrb</c>, which each program supplies; each counts in RAM ($8010 and $8011)
    /// and ends with RETI.
    /// </summary>
    private const string Prelude = """
                    jp start
                    org $0040
                    dw isra,isrb
                    org $0100
            start:  ld sp,$0000
                    xor a
                    ld i,a
                    im 2
            """;

    private const string Counters = """
            isra:   push af
                    ld a,($8010)
                    inc a
                    ld ($8010),a
                    pop af
                    ei
                    reti
            isrb:   push af
                    ld a,($8011)
                    inc a
                    ld ($8011),a
                    in a,($C1)
                    ld ($8012),a
                    pop af
                    ei
                    reti
            """;

    [Fact]
    public void ThePinoutIsTheDatasheets()
    {
        var pio = new Z80Pio();
        string At(int pin) => pio.Terminals.Single(t => t.Id == $"p{pin}").Name;

        Assert.Equal(40, pio.Terminals.Count);
        Assert.Equal(["D2", "D7", "D6", "CE", "CD", "BA", "PA7", "PA4", "GND", "PA3", "PA0", "ASTB", "BSTB", "ARDY", "D0", "D1", "BRDY", "IEO", "INT", "IEI"],
            new[] { 1, 2, 3, 4, 5, 6, 7, 10, 11, 12, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24 }.Select(At));
        Assert.Equal(["CLK", "VCC", "PB7", "PB0", "RD", "IORQ", "M1", "D3", "D4", "D5"], new[] { 25, 26, 27, 34, 35, 36, 37, 38, 39, 40 }.Select(At));
        Assert.Equal("/ASTB", pio.PinLabel(pio.StrobeA));
        Assert.Equal("B/A", pio.PinLabel(pio.PortSelect));
    }

    /// <summary>Mode 0: a write puts the byte on port A and raises ARDY; /ASTB's pulse drops ARDY and interrupts through port A's vector.</summary>
    [Fact]
    public void OutputModeHandshakesAndInterrupts()
    {
        var bench = new Bench("""
                    ld a,$40
                    out ($C2),a     ; port A's vector
                    ld a,$0F
                    out ($C2),a     ; mode 0
                    ld a,$87
                    out ($C2),a     ; interrupts on
                    ei
                    ld a,$5A
                    out ($C0),a
            done:   jr done
            """ + "\n" + Counters);
        var strobe = bench.Toggle(bench.Pio.StrobeA, true);
        bench.Run(40e-6);

        Assert.Equal(0x5A, bench.PortPins(bench.Pio.PortA));
        Assert.Equal(LogicState.High, bench.Pin(bench.Pio.ReadyA));
        Assert.Equal(0, bench.Ram(0x8010));

        strobe.State = false;
        bench.Run(1e-6);
        strobe.State = true;
        bench.Run(40e-6);
        Assert.Equal(LogicState.Low, bench.Pin(bench.Pio.ReadyA));
        Assert.Equal(1, bench.Ram(0x8010));
        Assert.Equal(1, bench.Pio.Acknowledges);
        Assert.Equal(1, bench.Pio.Returns);
        Assert.Equal(0x40, bench.Computer.Cpu.Core.LastAcknowledgeByte);
        Assert.Equal(0, bench.Computer.Cpu.UndefinedReads);
    }

    /// <summary>Mode 1: a read raises BRDY; /BSTB latches port B's lines, drops BRDY and interrupts; the handler's read gets the byte and raises BRDY again.</summary>
    [Fact]
    public void InputModeLatchesOnTheStrobe()
    {
        var bench = new Bench("""
                    ld a,$42
                    out ($C3),a     ; port B's vector
                    ld a,$4F
                    out ($C3),a     ; mode 1
                    ld a,$87
                    out ($C3),a
                    in a,($C1)      ; the first read raises BRDY
                    ei
            done:   jr done
            """ + "\n" + Counters);
        var strobe = bench.Toggle(bench.Pio.StrobeB, true);
        var lines = Enumerable.Range(0, 8).Select(i => bench.Toggle(bench.Pio.PortB[i], (0xA5 & (1 << i)) != 0)).ToList();
        bench.Run(30e-6);
        Assert.Equal(LogicState.High, bench.Pin(bench.Pio.ReadyB));

        strobe.State = false;
        bench.Run(1e-6);
        foreach (var line in lines) line.State = false;      // the byte was latched while /BSTB was low
        strobe.State = true;
        bench.Run(1e-6);
        Assert.Equal(LogicState.Low, bench.Pin(bench.Pio.ReadyB));
        bench.Run(25e-6);
        Assert.Equal(1, bench.Ram(0x8011));
        Assert.Equal(0xA5, bench.Ram(0x8012));
        Assert.Equal(LogicState.High, bench.Pin(bench.Pio.ReadyB));
        Assert.Equal(0x42, bench.Computer.Cpu.Core.LastAcknowledgeByte);
    }

    /// <summary>Mode 3 with OR, active low, PB7 monitored: outputs on PB0-PB3 drive, inputs read back, and a low on PB7 interrupts.</summary>
    [Fact]
    public void BitControlOrInterruptsOnAnActiveLine()
    {
        var bench = new Bench("""
                    ld a,$42
                    out ($C3),a
                    ld a,$CF
                    out ($C3),a     ; mode 3
                    ld a,$F0
                    out ($C3),a     ; PB4-PB7 inputs, PB0-PB3 outputs
                    ld a,$97
                    out ($C3),a     ; interrupts on, OR, active low, mask follows
                    ld a,$7F
                    out ($C3),a     ; only PB7 monitored
                    ld a,$05
                    out ($C1),a
                    in a,($C1)
                    ld ($8013),a
                    ei
            done:   jr done
            """ + "\n" + Counters);
        var pb7 = bench.Toggle(bench.Pio.PortB[7], true);
        var pb6 = bench.Toggle(bench.Pio.PortB[6], false);
        bench.Toggle(bench.Pio.PortB[5], true);
        bench.Toggle(bench.Pio.PortB[4], false);
        bench.Run(60e-6);

        Assert.Equal(0x05, bench.PortPins(bench.Pio.PortB) & 0x0F);
        Assert.Equal(0xA5, bench.Ram(0x8013));          // PB7 and PB5 high, PB4 and PB6 low, and the outputs' register
        Assert.Equal(0, bench.Ram(0x8011));

        pb6.State = true;                                // masked: nothing
        bench.Run(10e-6);
        Assert.Equal(0, bench.Ram(0x8011));

        pb7.State = false;
        bench.Run(30e-6);
        Assert.Equal(1, bench.Ram(0x8011));
        Assert.Equal(3, bench.Pio.Describe(true).Mode);
        Assert.Equal(0xF0, bench.Pio.Describe(true).Direction);
    }

    /// <summary>Mode 3 with AND, active high, PB4 and PB5 monitored: one alone does nothing; both together interrupt.</summary>
    [Fact]
    public void BitControlAndWantsEveryMonitoredLine()
    {
        var bench = new Bench("""
                    ld a,$42
                    out ($C3),a
                    ld a,$CF
                    out ($C3),a
                    ld a,$FF
                    out ($C3),a     ; all inputs
                    ld a,$F7
                    out ($C3),a     ; on, AND, active high, mask follows
                    ld a,$CF
                    out ($C3),a     ; PB4 and PB5 monitored
                    ei
            done:   jr done
            """ + "\n" + Counters);
        var pb4 = bench.Toggle(bench.Pio.PortB[4], false);
        var pb5 = bench.Toggle(bench.Pio.PortB[5], false);
        bench.Run(40e-6);

        pb4.State = true;
        bench.Run(15e-6);
        Assert.Equal(0, bench.Ram(0x8011));
        pb5.State = true;
        bench.Run(30e-6);
        Assert.Equal(1, bench.Ram(0x8011));
        Assert.True(bench.Pio.Describe(true).And);
        Assert.True(bench.Pio.Describe(true).ActiveHigh);
    }

    /// <summary>The disable word turns a port's interrupt off and on again without touching the rest of its control.</summary>
    [Fact]
    public void TheDisableWordHoldsInterruptsOff()
    {
        var bench = new Bench("""
                    ld a,$42
                    out ($C3),a
                    ld a,$CF
                    out ($C3),a
                    ld a,$FF
                    out ($C3),a
                    ld a,$B7
                    out ($C3),a     ; on, OR, active high, mask follows
                    ld a,$FE
                    out ($C3),a     ; PB0 monitored
                    ld a,$03
                    out ($C3),a     ; disable
                    ei
            wait:   in a,($C1)
                    and $02
                    jr z,wait       ; PB1 high: turn interrupts back on
                    ld a,$83
                    out ($C3),a
            done:   jr done
            """ + "\n" + Counters);
        var pb0 = bench.Toggle(bench.Pio.PortB[0], false);
        var pb1 = bench.Toggle(bench.Pio.PortB[1], false);
        bench.Run(40e-6);

        pb0.State = true;
        bench.Run(15e-6);
        Assert.Equal(0, bench.Ram(0x8011));
        Assert.False(bench.Pio.Describe(true).InterruptEnabled);

        pb0.State = false;
        pb1.State = true;
        bench.Run(20e-6);
        Assert.True(bench.Pio.Describe(true).InterruptEnabled);
        pb0.State = true;
        bench.Run(30e-6);
        Assert.Equal(1, bench.Ram(0x8011));
    }

    /// <summary>Mode 2's output half: port A floats until /ASTB comes, drives while it is low, and its rise drops ARDY and interrupts.</summary>
    [Fact]
    public void BidirectionalOutputDrivesOnlyWhileAstbIsLow()
    {
        var bench = new Bench("""
                    ld a,$CF
                    out ($C3),a     ; port B in mode 3, as mode 2 on port A needs
                    ld a,$FF
                    out ($C3),a
                    ld a,$40
                    out ($C2),a
                    ld a,$8F
                    out ($C2),a     ; port A in mode 2
                    ld a,$87
                    out ($C2),a
                    ei
                    ld a,$3C
                    out ($C0),a
            done:   jr done
            """ + "\n" + Counters);
        var astb = bench.Toggle(bench.Pio.StrobeA, true);
        bench.Toggle(bench.Pio.StrobeB, true);
        bench.Run(40e-6);

        Assert.Equal(LogicState.HighImpedance, bench.Pin(bench.Pio.PortA[2]));
        Assert.Equal(LogicState.High, bench.Pin(bench.Pio.ReadyA));

        astb.State = false;
        bench.Run(1e-6);
        Assert.Equal(0x3C, bench.PortPins(bench.Pio.PortA));
        astb.State = true;
        bench.Run(25e-6);
        Assert.Equal(LogicState.HighImpedance, bench.Pin(bench.Pio.PortA[2]));
        Assert.Equal(LogicState.Low, bench.Pin(bench.Pio.ReadyA));
        Assert.Equal(1, bench.Ram(0x8010));
    }

    /// <summary>Mode 2's input half: /BSTB latches port A's lines, drops BRDY and interrupts through port A's vector; a read raises BRDY.</summary>
    [Fact]
    public void BidirectionalInputLatchesOnBstb()
    {
        var bench = new Bench("""
                    ld a,$CF
                    out ($C3),a
                    ld a,$FF
                    out ($C3),a
                    ld a,$40
                    out ($C2),a
                    ld a,$8F
                    out ($C2),a
                    ld a,$87
                    out ($C2),a
                    in a,($C0)      ; BRDY up
                    ei
            done:   jr done
            isra:   push af
                    in a,($C0)
                    ld ($8014),a
                    pop af
                    ei
                    reti
            isrb:   reti
            """);
        bench.Toggle(bench.Pio.StrobeA, true);
        var bstb = bench.Toggle(bench.Pio.StrobeB, true);
        for (var i = 0; i < 8; i++) bench.Toggle(bench.Pio.PortA[i], (0x96 & (1 << i)) != 0);
        bench.Run(40e-6);
        Assert.Equal(LogicState.High, bench.Pin(bench.Pio.ReadyB));

        bstb.State = false;
        bench.Run(1e-6);
        bstb.State = true;
        bench.Run(1e-6);
        Assert.Equal(LogicState.Low, bench.Pin(bench.Pio.ReadyB));
        bench.Run(25e-6);
        Assert.Equal(0x96, bench.Ram(0x8014));
        Assert.Equal(LogicState.High, bench.Pin(bench.Pio.ReadyB));
    }

    /// <summary>Port A outranks port B: both pending, A is served first, and B only after A's RETI.</summary>
    [Fact]
    public void PortAOutranksPortB()
    {
        var bench = new Bench("""
                    ld a,$40
                    out ($C2),a
                    ld a,$42
                    out ($C3),a
                    ld a,$CF
                    out ($C2),a
                    ld a,$FF
                    out ($C2),a
                    ld a,$B7
                    out ($C2),a
                    ld a,$FE
                    out ($C2),a     ; port A: PA0 high interrupts
                    ld a,$CF
                    out ($C3),a
                    ld a,$FF
                    out ($C3),a
                    ld a,$B7
                    out ($C3),a
                    ld a,$FE
                    out ($C3),a     ; port B: PB0 high interrupts
                    ld a,1
                    ld ($8020),a
                    ei
            done:   jr done
            isra:   push af
                    ld a,($8020)
                    ld ($8021),a    ; the order: A sees 1
                    inc a
                    ld ($8020),a
                    pop af
                    ei
                    reti
            isrb:   push af
                    ld a,($8020)
                    ld ($8022),a    ; B sees 2
                    inc a
                    ld ($8020),a
                    pop af
                    ei
                    reti
            """);
        var pa0 = bench.Toggle(bench.Pio.PortA[0], false);
        var pb0 = bench.Toggle(bench.Pio.PortB[0], false);
        bench.Run(60e-6);

        pb0.State = true;
        pa0.State = true;
        bench.Run(80e-6);
        Assert.Equal(1, bench.Ram(0x8021));
        Assert.Equal(2, bench.Ram(0x8022));
        Assert.Equal(2, bench.Pio.Returns);
    }

    /// <summary>/M1 held low with neither /RD nor /IORQ resets the PIO: both ports back to mode 1, their outputs let go.</summary>
    [Fact]
    public void M1AloneResetsThePio()
    {
        var bench = new Bench("""
                    ld a,$0F
                    out ($C2),a
                    ld a,$81
                    out ($C0),a
            done:   jr done
            """ + "\n" + Counters, ownM1: true);
        bench.Run(30e-6);
        Assert.Equal(0, bench.Pio.Describe(false).Mode);
        Assert.Equal(LogicState.High, bench.Pin(bench.Pio.PortA[0]));

        // A reset circuit on /M1, standing in for the processor's: low for a few clocks with no /RD or /IORQ.
        bench.M1!.State = false;
        bench.Run(2e-6);
        Assert.Equal(1, bench.Pio.Describe(false).Mode);
        Assert.Equal(LogicState.HighImpedance, bench.Pin(bench.Pio.PortA[0]));
        Assert.False(bench.Pio.Describe(false).InterruptEnabled);
    }
}
