using Cirq.Components.Digital;
using Cirq.Components.Processors;
using Cirq.Core.Digital;

namespace Cirq.Components.Tests;

/// <summary>
/// A CTC, a SIO and a PIO in one interrupt daisy chain on a Z80, in that order of priority — the
/// CTC's IEI tied high, its IEO to the SIO's IEI, the SIO's IEO to the PIO's — with interrupt mode 2:
/// requests at once served by priority, a higher one breaking into a lower one's handler, a lower
/// one waiting for the higher one's RETI, and each chip's service ended by the RETI the chain lets
/// it see.
/// </summary>
public class Z80DaisyChainTests
{
    /// <summary>
    /// The board with the CTC at $A0, the SIO at $80 and the PIO at $C0. The CTC's channel 0 counts
    /// rising edges on CLK/TRG0 with a time constant of 1, so one edge interrupts; the SIO raises an
    /// external/status interrupt on channel A when /CTSA changes; the PIO's port B, in bit control,
    /// interrupts when PB0 goes high.
    /// </summary>
    private sealed class Bench
    {
        public CpuZ80CircuitTests.Computer Computer { get; }

        public Z80Ctc Ctc { get; }

        public Z80Sio Sio { get; }

        public Z80Pio Pio { get; }

        public LogicToggle Trigger { get; }

        public LogicToggle Cts { get; }

        public LogicToggle Pb0 { get; }

        public Bench(string handlers)
        {
            Computer = new CpuZ80CircuitTests.Computer(CpuZ80CircuitTests.Rom(Setup + "\n" + handlers + "\n" + Note));
            var c = Computer;
            Ctc = c.Peripheral(new Z80Ctc(), c.SelectA0);
            Sio = c.Peripheral(new Z80Sio(), c.Select80, Ctc.InterruptEnableOut);
            Pio = c.Peripheral(new Z80Pio(), c.SelectC0, Sio.InterruptEnableOut);
            c.Circuit.Connect(Ctc.ChannelSelect0, c.Cpu.Address[0]);
            c.Circuit.Connect(Ctc.ChannelSelect1, c.Cpu.Address[1]);
            c.Circuit.Connect(Ctc.Reset, c.Rail.Positive);
            foreach (var pin in Ctc.Triggers.Skip(1)) c.Circuit.Connect(pin, c.Ground.Pin);
            foreach (var chip in new Z80Peripheral[] { Sio, Pio })
            {
                var (ba, cd) = chip is Z80Sio s ? (s.ChannelSelect, s.ControlData) : (((Z80Pio)chip).PortSelect, ((Z80Pio)chip).ControlData);
                c.Circuit.Connect(ba, c.Cpu.Address[0]);
                c.Circuit.Connect(cd, c.Cpu.Address[1]);
            }

            c.Circuit.Connect(Sio.Reset, c.Rail.Positive);
            foreach (var pin in new[] { Sio.ReceiveA, Sio.ReceiveB, Sio.ReceiveClockA, Sio.TransmitClockA, Sio.ClockB, Sio.CarrierDetectA, Sio.CarrierDetectB, Sio.ClearToSendB, Sio.SyncA, Sio.SyncB })
                c.Circuit.Connect(pin, c.Ground.Pin);
            c.Circuit.Connect(Pio.StrobeA, c.Rail.Positive);
            c.Circuit.Connect(Pio.StrobeB, c.Rail.Positive);

            Trigger = c.Toggle(Ctc.Triggers[0], false);
            Cts = c.Toggle(Sio.ClearToSendA, true);
            Pb0 = c.Toggle(Pio.PortB[0], false);
        }

        public void Run(double seconds) => Computer.Simulator.Run(seconds);

        /// <summary>The letters the handlers have noted, in order.</summary>
        public string Log
        {
            get
            {
                var text = new System.Text.StringBuilder();
                for (var a = 0x40; a < 0x60 && Computer.Ram.ReadByte(a) != 0; a++) text.Append((char)Computer.Ram.ReadByte(a));
                return text.ToString();
            }
        }
    }

    /// <summary>
    /// The vectors: the CTC's $40 (channel 0), the SIO's $50, the PIO's port B $60, through a table on
    /// page 0 (I = 0). Each chip is set up, then the program waits with interrupts on. Each handler
    /// notes a letter with <c>note</c>, which appends it to the list at $8040.
    /// </summary>
    private const string Setup = """
                    jp start
                    org $0040
                    dw ctc0
                    org $0050
                    dw sio
                    org $0060
                    dw piob
                    org $0100
            start:  ld sp,$0000
                    ld hl,$8040
                    ld ($8030),hl
                    xor a
                    ld ($8032),a
                    ld i,a
                    im 2
                    ld a,$40
                    out ($A0),a     ; CTC vector
                    ld a,$D7
                    out ($A0),a     ; channel 0: interrupts, counter, rising, a time constant follows, reset
                    ld a,1
                    out ($A0),a
                    ld a,$18
                    out ($82),a     ; SIO channel A reset
                    ld a,1
                    out ($82),a
                    ld a,$01
                    out ($82),a     ; WR1 (A): external/status interrupts
                    ld a,2
                    out ($83),a
                    ld a,$50
                    out ($83),a     ; WR2: the vector, status not affecting it
                    ld a,$60
                    out ($C3),a     ; PIO port B vector
                    ld a,$CF
                    out ($C3),a     ; mode 3
                    ld a,$FF
                    out ($C3),a     ; all inputs
                    ld a,$B7
                    out ($C3),a     ; interrupts, OR, active high, mask follows
                    ld a,$FE
                    out ($C3),a     ; PB0 alone
                    ei
            idle:   jr idle
            """;

    private const string Note = """
            note:   push hl
                    ld hl,($8030)
                    ld (hl),a
                    inc hl
                    ld ($8030),hl
                    pop hl
                    ret
            """;

    /// <summary>The SIO's handler resets its external/status interrupt, or it would come straight back.</summary>
    private const string SioHandler = """
            sio:    push af
                    ld a,'S'
                    call note
                    ld a,$10
                    out ($82),a
                    ld a,1
                    ld ($8032),a
                    pop af
                    ei
                    reti
            """;

    [Fact]
    public void RequestsAtOnceAreServedByPriority()
    {
        var bench = new Bench("""
            ctc0:   push af
                    ld a,'C'
                    call note
                    pop af
                    ei
                    reti
            piob:   push af
                    ld a,'P'
                    call note
                    pop af
                    ei
                    reti
            """ + "\n" + SioHandler);
        bench.Run(120e-6);
        Assert.Equal(string.Empty, bench.Log);

        // All three at the same instant: the PIO, then the SIO, then the CTC asked — the chain decides.
        bench.Pb0.State = true;
        bench.Cts.State = false;
        bench.Trigger.State = true;
        bench.Run(150e-6);

        Assert.Equal("CSP", bench.Log);
        Assert.Equal(1, bench.Ctc.Returns);
        Assert.Equal(1, bench.Sio.Returns);
        Assert.Equal(1, bench.Pio.Returns);
        Assert.All(new Z80Peripheral[] { bench.Ctc, bench.Sio, bench.Pio }, p => Assert.DoesNotContain(true, p.InService));
        Assert.Equal(0, bench.Computer.Cpu.UndefinedReads);
    }

    /// <summary>The PIO's handler, with interrupts back on, is broken into by the SIO above it; it carries on after the SIO's RETI.</summary>
    [Fact]
    public void AHigherChipBreaksIntoALowerOnesHandler()
    {
        var bench = new Bench("""
            ctc0:   ei
                    reti
            piob:   push af
                    ld a,'P'
                    call note
                    ei
            spin:   ld a,($8032)
                    or a
                    jr z,spin       ; until the SIO's handler has run
                    ld a,'p'
                    call note
                    pop af
                    ei
                    reti
            """ + "\n" + SioHandler);
        bench.Run(120e-6);

        bench.Pb0.State = true;
        bench.Run(40e-6);
        Assert.Equal("P", bench.Log);
        Assert.True(bench.Pio.InService[1]);
        Assert.Equal(LogicState.High, bench.Sio.GetOutputState(bench.Sio.OutputTerminals.ToList().IndexOf(bench.Sio.InterruptEnableOut)));

        bench.Cts.State = false;
        bench.Run(150e-6);
        Assert.Equal("PSp", bench.Log);
        Assert.Equal(1, bench.Sio.Returns);
        Assert.Equal(1, bench.Pio.Returns);
    }

    /// <summary>
    /// The CTC's handler, with interrupts back on, holds the PIO below it off: the CTC's IEO is low
    /// while it is in service, so the PIO waits for the CTC's RETI, which only the CTC acts on.
    /// </summary>
    [Fact]
    public void ALowerChipWaitsForTheHighersReti()
    {
        var bench = new Bench("""
            ctc0:   push af
                    push bc
                    ld a,'C'
                    call note
                    ei
                    ld b,40
            delay:  djnz delay      ; 130 µs with interrupts on
                    ld a,'c'
                    call note
                    pop bc
                    pop af
                    reti
            piob:   push af
                    ld a,'P'
                    call note
                    pop af
                    ei
                    reti
            """ + "\n" + SioHandler);
        bench.Run(120e-6);

        bench.Trigger.State = true;
        bench.Run(30e-6);
        Assert.Equal("C", bench.Log);
        Assert.True(bench.Ctc.InService[0]);
        Assert.Equal(LogicState.Low, bench.Ctc.GetOutputState(bench.Ctc.OutputTerminals.ToList().IndexOf(bench.Ctc.InterruptEnableOut)));

        bench.Pb0.State = true;
        bench.Run(60e-6);
        Assert.Equal("C", bench.Log);           // held off: the PIO's IEI is low
        Assert.False(bench.Pio.Interrupting);
        Assert.True(bench.Pio.PendingSources[1]);

        bench.Run(150e-6);
        Assert.Equal("CcP", bench.Log);
        Assert.Equal(1, bench.Ctc.Returns);
        Assert.Equal(1, bench.Pio.Returns);
        Assert.Equal(0, bench.Sio.Returns);
    }
}
