using Cirq.Components.Digital;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Processors;
using Cirq.Components.Sources;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>The ATmega328P: its instruction set and flags, its cycle counts, its peripherals, and the chip in a circuit.</summary>
public class AtmegaTests
{
    /// <summary>A core with <paramref name="source"/> in its flash, run until it has executed <paramref name="instructions"/> or reached a SLEEP.</summary>
    private static AvrCore Run(string source, int instructions = 10_000)
    {
        var core = new AvrCore();
        core.LoadFlash([(0, AssemblerAvr.Assemble(source))]);
        core.Reset();
        for (var i = 0; i < instructions; i++)
        {
            var op = core.State.Flash[core.Pc];
            if (op == 0x9588) break;
            core.Step();
        }

        return core;
    }

    private static int Sreg(AvrCore core) => core.StatusRegister;

    [Fact]
    public void Arithmetic_sets_the_flags_the_instruction_set_manual_says()
    {
        // 0x7F + 1: overflow into the sign, a half carry, no carry.
        var add = Run("ldi r16, 0x7F\n ldi r17, 1\n add r16, r17\n sleep");
        Assert.Equal(0x80, add[16]);
        Assert.Equal(0b0010_1100, Sreg(add) & 0x3F); // H, V, N set; S = N^V = 0; Z, C clear

        // 0xFF + 1: zero, carry, half carry.
        var wrap = Run("ldi r16, 0xFF\n ldi r17, 1\n add r16, r17\n sleep");
        Assert.Equal(0, wrap[16]);
        Assert.Equal(0b0010_0011, Sreg(wrap) & 0x3F);

        // A 16-bit compare with CP and CPC: 0x1234 against 0x1235 is lower, and not equal.
        var compare = Run("ldi r24, 0x34\n ldi r25, 0x12\n ldi r22, 0x35\n ldi r23, 0x12\n cp r24, r22\n cpc r25, r23\n sleep");
        Assert.Equal(1, Sreg(compare) & 0x01);
        Assert.Equal(0, Sreg(compare) & 0x02);

        // SUBI/SBCI on a word: 0x0100 - 1 = 0x00FF, borrow through.
        var word = Run("ldi r24, 0x00\n ldi r25, 0x01\n subi r24, 1\n sbci r25, 0\n sleep");
        Assert.Equal((0xFF, 0x00), (word[24], word[25]));

        // ADIW and SBIW, MUL into R1:R0.
        var wide = Run("ldi r26, 0xFF\n ldi r27, 0x00\n adiw r26, 1\n ldi r28, 0\n ldi r29, 0\n sbiw r28, 1\n ldi r16, 200\n ldi r17, 100\n mul r16, r17\n sleep");
        Assert.Equal((0x00, 0x01), (wide[26], wide[27]));
        Assert.Equal((0xFF, 0xFF), (wide[28], wide[29]));
        Assert.Equal(20000, wide[0] | (wide[1] << 8));

        // Shifts: LSR to carry, ROR through it, ASR keeping the sign, SWAP, COM, NEG, INC, DEC.
        var shift = Run("ldi r16, 0x81\n lsr r16\n ldi r17, 0x00\n ror r17\n ldi r18, 0x82\n asr r18\n ldi r19, 0x12\n swap r19\n ldi r20, 0x0F\n com r20\n ldi r21, 1\n neg r21\n ldi r22, 0x7F\n inc r22\n ldi r23, 0\n dec r23\n sleep");
        Assert.Equal((0x40, 0x80, 0xC1, 0x21, 0xF0, 0xFF, 0x80, 0xFF), (shift[16], shift[17], shift[18], shift[19], shift[20], shift[21], shift[22], shift[23]));
    }

    [Fact]
    public void Memory_the_stack_and_flash_are_reached_every_way_the_chip_has()
    {
        var core = Run("""
            ldi r16, 0x08
            out 0x3E, r16
            ldi r16, 0xFF
            out 0x3D, r16
            ldi r26, 0x00      ; X = 0x0100
            ldi r27, 0x01
            ldi r16, 11
            st X+, r16
            ldi r16, 22
            st X+, r16
            ldi r28, 0x00      ; Y = 0x0100
            ldi r29, 0x01
            ldd r17, Y+1
            ld r18, -X
            ldi r16, 33
            std Y+5, r16
            sts 0x0200, r16
            lds r19, 0x0105
            ldi r30, lo8(table) ; Z = the table's byte address
            ldi r31, hi8(table)
            lsl r30
            rol r31
            lpm r20, Z+
            lpm r21, Z
            ldi r16, 0x5A
            push r16
            rcall sub
            pop r22
            sleep
            sub:
            ldi r23, 0x77
            ret
            table:
            .dw 0x2211
            """);
        Assert.Equal(11, core[0x100]);
        Assert.Equal(22, core[17]);
        Assert.Equal(22, core[18]);
        Assert.Equal(33, core[0x105]);
        Assert.Equal(33, core[0x200]);
        Assert.Equal(33, core[19]);
        Assert.Equal((0x11, 0x22), (core[20], core[21]));
        Assert.Equal(0x5A, core[22]);
        Assert.Equal(0x77, core[23]);
        Assert.Equal(0x08FF, core.StackPointer);
    }

    [Fact]
    public void Instructions_take_the_cycles_the_datasheet_gives()
    {
        static long Cycles(string body)
        {
            var core = Run(body + "\nsleep");
            return core.State.Cycles;
        }

        Assert.Equal(1, Cycles("ldi r16, 1"));
        Assert.Equal(2, Cycles("rjmp next\n next:"));
        Assert.Equal(1 + 2, Cycles("ldi r26, 0\n adiw r26, 1"));
        Assert.Equal(3 + 4, Cycles("rcall s\n rjmp done\n s:\n ret\n done:") - 2);
        Assert.Equal(4 + 4 + 3, Cycles("call s\n jmp done\n s:\n ret\n done:"));
        // A branch taken is 2, not taken 1; a skip over a two-word instruction is 3.
        Assert.Equal(1 + 2, Cycles("clr r16\n breq t\n t:"));
        Assert.Equal(1 + 1, Cycles("ldi r16, 1\n breq t\n t:"));
        Assert.Equal(1 + 3, Cycles("ldi r16, 1\n sbrc r16, 1\n lds r17, 0x100"));
    }

    [Fact]
    public void Timer0_overflows_at_its_prescale_and_its_interrupt_counts_them_as_millis_does()
    {
        // Timer0 in fast PWM at clk/64, as the Arduino core sets it, its overflow interrupt incrementing r24.
        var core = Run("""
            rjmp start
            .org 32
            rjmp overflow
            start:
            ldi r16, 0x08
            out 0x3E, r16
            ldi r16, 0xFF
            out 0x3D, r16
            ldi r16, 0x03
            out 0x24, r16      ; TCCR0A: WGM01 | WGM00
            out 0x25, r16      ; TCCR0B: CS01 | CS00, clk/64
            ldi r16, 0x01
            sts 0x6E, r16      ; TIMSK0: TOIE0
            sei
            loop:
            rjmp loop
            overflow:
            inc r24
            reti
            """, instructions: 0);
        while (core.State.Cycles < 16_000 * 10) core.Step();

        // 160,000 cycles at 64 × 256 a overflow: nine of them, with the tenth not yet.
        Assert.Equal(9, core[24]);
    }

    [Fact]
    public void The_usart_sends_frames_at_its_baud_rate()
    {
        var core = Run("""
            ldi r16, 103
            sts 0xC4, r16      ; UBRR0 = 103: 9600 baud at 16 MHz
            ldi r16, 0x08
            sts 0xC1, r16      ; UCSR0B: TXEN0
            ldi r16, 'H'
            sts 0xC6, r16
            wait:
            lds r17, 0xC0
            sbrs r17, 5        ; UDRE0
            rjmp wait
            ldi r16, 'i'
            sts 0xC6, r16
            done:
            rjmp done
            """, instructions: 0);
        var sent = new List<byte>();
        core.Transmitted += sent.Add;
        var line = new List<(long, bool)>();
        var last = true;
        while (core.State.Cycles < 16_000_000 / 9600 * 22)
        {
            core.Step();
            if (core.TxLevel != last) { last = core.TxLevel; line.Add((core.State.Cycles, last)); }
        }

        Assert.Equal("Hi"u8.ToArray(), sent);

        // The start bit of 'H' (0x48) lasts one bit, 1664 cycles; then 0 0 0 1 0 0 1 0 LSB first.
        Assert.False(line[0].Item2);
        Assert.InRange(line[1].Item1 - line[0].Item1, 1664 * 4 - 40, 1664 * 4 + 40);
    }

    [Fact]
    public void The_adc_converts_against_avcc_and_a_pin_change_flags_its_interrupt()
    {
        var core = Run("""
            ldi r16, 0x40
            sts 0x7C, r16      ; ADMUX: AVCC reference, ADC0
            ldi r16, 0xC7
            sts 0x7A, r16      ; ADCSRA: ADEN | ADSC, clk/128
            wait:
            lds r17, 0x7A
            sbrc r17, 6
            rjmp wait
            lds r18, 0x78
            lds r19, 0x79
            sleep
            """, instructions: 0);
        core.AvccVolts = 5.0;
        core.AnalogInputs[0] = 2.5;
        for (var i = 0; i < 20_000 && core.State.Flash[core.Pc] != 0x9588; i++) core.Step();
        Assert.Equal(512, core[18] | (core[19] << 8));

        // PCINT on PB0: an edge sets PCIF0.
        core[AvrCore.PCMSK0] = 0x01;
        core.PinInputs[0] = 0x01;
        core.SampleEdges();
        Assert.Equal(0x01, core[AvrCore.PCIFR] & 0x01);
    }

    /// <summary>A blink in a circuit: Timer1 in CTC at clk/256, its compare interrupt toggling PB5, an LED on the pin.</summary>
    public const string Blink = """
        rjmp start
        .org 22                ; TIMER1_COMPA
        rjmp toggle
        start:
        ldi r16, 0x08
        out 0x3E, r16          ; SP = 0x08FF
        ldi r16, 0xFF
        out 0x3D, r16
        sbi 0x04, 5            ; PB5 an output
        ldi r16, hi8(PERIOD)
        sts 0x89, r16          ; OCR1AH
        ldi r16, lo8(PERIOD)
        sts 0x88, r16          ; OCR1AL
        ldi r16, 0x0C
        sts 0x81, r16          ; TCCR1B: CTC, clk/256
        ldi r16, 0x02
        sts 0x6F, r16          ; TIMSK1: OCIE1A
        sei
        loop:
        rjmp loop
        toggle:
        sbi 0x03, 5            ; writing PINB5 toggles PORTB5
        reti
        """;

    [Fact]
    public void Firmware_runs_in_a_circuit_and_its_edges_land_where_its_timer_puts_them()
    {
        // 16 MHz / 256 / 625 = 100 Hz of compares: PB5 toggling every 10 ms.
        var bytes = AssemblerAvr.Assemble(".equ PERIOD = 624\n" + Blink);
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5));
        var chip = circuit.Add(new Atmega328p());
        var resistor = circuit.Add(new Resistor(330));
        var led = circuit.Add(new Led());
        var ground = circuit.Add(new Ground());
        circuit.Connect(supply.Terminals[1], ground.Terminals[0]);
        circuit.Connect(supply.Terminals[0], chip.Vcc);
        circuit.Connect(supply.Terminals[0], chip.Avcc);
        circuit.Connect(chip.Gnd, ground.Terminals[0]);
        circuit.Connect(chip.Gnd2, ground.Terminals[0]);
        var pb5 = chip.TerminalByName("PB5");
        circuit.Connect(pb5, resistor.Terminals[0]);
        circuit.Connect(resistor.Terminals[1], led.Terminals[0]);
        circuit.Connect(led.Terminals[1], ground.Terminals[0]);
        Assert.Equal("24 bytes of firmware".Split(' ')[1], chip.Load(Cirq.Components.Digital.MemoryImage.FromBinary(bytes)).Split(' ')[1]);
        Assert.Null(chip.FirmwareError);

        var probe = new SignalProbe("PB5", pb5, Cirq.Core.Primitives.Color.FromHex("#FFFF0000"));
        circuit.Probes.Add(probe);
        var simulator = new CircuitSimulator(circuit, new Cirq.Core.Simulation.SimulationSettings { TimeStep = 2e-5, MaxTimeStep = 2e-4, AdaptiveTimeStep = true });
        simulator.Reset();
        simulator.SolveOperatingPoint();
        var rises = new List<double>();
        var was = false;
        var lit = false;
        while (simulator.Time < 0.065)
        {
            simulator.Step();
            var high = simulator.NodeVoltage(pb5) > 2.5;
            if (high && !was) rises.Add(simulator.Time);
            was = high;
            if (high && led.Brightness > 0.1) lit = true;
        }

        // Rising every 20 ms (a toggle every 10), to well under a step.
        Assert.True(rises.Count >= 3, string.Join(", ", rises));
        for (var i = 1; i < rises.Count; i++) Assert.Equal(0.020, rises[i] - rises[i - 1], 4);
        Assert.True(lit);

        // The whole state goes with a run and comes back.
        var state = chip.CaptureRunState();
        var fresh = new Atmega328p();
        fresh.AdoptRunState(state);
        Assert.Equal(chip.Core.Pc, fresh.Core.Pc);
    }
}
