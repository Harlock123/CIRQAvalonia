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
    // ───────────────────────────── SPI and I2C ─────────────────────────────

    /// <summary>A circuit with an ATmega328P on 5 V running <paramref name="source"/>.</summary>
    private static (Circuit Circuit, Atmega328p Chip, DcVoltageSource Supply, Ground Ground) Board(string source)
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5));
        var chip = circuit.Add(new Atmega328p());
        var ground = circuit.Add(new Ground());
        circuit.Connect(supply.Terminals[1], ground.Terminals[0]);
        circuit.Connect(supply.Terminals[0], chip.Vcc);
        circuit.Connect(supply.Terminals[0], chip.Avcc);
        circuit.Connect(chip.Gnd, ground.Terminals[0]);
        circuit.Connect(chip.Gnd2, ground.Terminals[0]);
        chip.Load(Cirq.Components.Digital.MemoryImage.FromBinary(AssemblerAvr.Assemble(source)));
        Assert.Null(chip.FirmwareError);
        return (circuit, chip, supply, ground);
    }

    private static CircuitSimulator RunFor(Circuit circuit, double seconds)
    {
        var simulator = new CircuitSimulator(circuit, new Cirq.Core.Simulation.SimulationSettings { TimeStep = 1e-6, MaxTimeStep = 1e-4, AdaptiveTimeStep = true });
        simulator.Reset();
        simulator.SolveOperatingPoint();
        simulator.Run(seconds);
        return simulator;
    }

    /// <summary>
    /// SPI as a master into a 74595: 0xA5 shifted out MSB first at 1 MHz and latched onto the outputs, then 0x3C, the
    /// register's serial output wired back to MISO so the first byte comes back in.
    /// </summary>
    public const string SpiShiftOut = """
        ldi r16, 0x08
        out 0x3E, r16
        ldi r16, 0xFF
        out 0x3D, r16
        ldi r16, 0x2E          ; PB1 (the latch), SS, MOSI and SCK outputs
        out 0x04, r16
        ldi r16, 0x51          ; SPCR: SPE, MSTR, fosc/16
        out 0x2C, r16
        ldi r24, 0xA5
        rcall send
        ldi r24, 0x3C
        rcall send
        sts 0x0100, r25        ; what came back
        done:
        rjmp done
        send:
        out 0x2E, r24          ; SPDR
        wait:
        in r16, 0x2D           ; SPSR
        sbrs r16, 7
        rjmp wait
        in r25, 0x2E
        sbi 0x05, 1            ; latch
        cbi 0x05, 1
        ret
        """;

    [Fact]
    public void Spi_shifts_bytes_into_a_74595_and_reads_back_what_falls_out()
    {
        var (circuit, chip, supply, ground) = Board(SpiShiftOut);
        var register = circuit.Add(new Ic74595());
        circuit.Connect(register.Vcc, supply.Terminals[0]);
        circuit.Connect(register.Gnd, ground.Terminals[0]);
        circuit.Connect(register.TerminalByName("SRCLR"), supply.Terminals[0]);
        circuit.Connect(register.TerminalByName("OE"), ground.Terminals[0]);
        circuit.Connect(register.TerminalByName("SER"), chip.TerminalByName("PB3"));
        circuit.Connect(register.TerminalByName("SRCLK"), chip.TerminalByName("PB5"));
        circuit.Connect(register.TerminalByName("RCLK"), chip.TerminalByName("PB1"));
        circuit.Connect(register.TerminalByName("QH'"), chip.TerminalByName("PB4"));
        RunFor(circuit, 1e-3);

        // 0x3C latched onto the outputs, QA the last bit in; and 0xA5, the first byte, back through MISO.
        Assert.Equal(0x3C, register.Latched);
        Assert.Equal(0xA5, chip.Core[0x0100]);
        Assert.True(chip.Core.SpiMaster);
    }

    /// <summary>
    /// I2C as a master to a 24LC256: "HI!" written at address 0, then read back (a repeated START between) into SRAM,
    /// with the statuses kept.
    /// </summary>
    public const string I2cEepromWriteRead = """
        ldi r16, 0x08
        out 0x3E, r16
        ldi r16, 0xFF
        out 0x3D, r16
        sbi 0x08, 4            ; pull-ups on SDA and SCL
        sbi 0x08, 5
        ldi r16, 72            ; 100 kHz at 16 MHz
        sts 0xB8, r16
        rcall start
        sts 0x0110, r18
        ldi r24, 0xA0          ; the EEPROM, writing
        rcall send
        sts 0x0111, r18
        ldi r24, 0
        rcall send
        rcall send
        ldi r24, 'H'
        rcall send
        ldi r24, 'I'
        rcall send
        ldi r24, '!'
        rcall send
        sts 0x0112, r18
        rcall stop
        rcall start
        ldi r24, 0xA0
        rcall send
        ldi r24, 0
        rcall send
        rcall send
        rcall start            ; a repeated START
        sts 0x0113, r18
        ldi r24, 0xA1          ; reading
        rcall send
        sts 0x0114, r18
        ldi r16, 0xC4          ; a byte, acknowledged
        rcall go
        lds r24, 0xBB
        sts 0x0100, r24
        ldi r16, 0xC4
        rcall go
        lds r24, 0xBB
        sts 0x0101, r24
        ldi r16, 0x84          ; the last, not acknowledged
        rcall go
        lds r24, 0xBB
        sts 0x0102, r24
        sts 0x0115, r18
        rcall stop
        done:
        rjmp done
        start:
        ldi r16, 0xA4          ; TWINT, TWSTA, TWEN
        rjmp go
        send:
        sts 0xBB, r24
        ldi r16, 0x84          ; TWINT, TWEN
        go:
        sts 0xBC, r16
        wait:
        lds r16, 0xBC
        sbrs r16, 7
        rjmp wait
        lds r18, 0xB9
        andi r18, 0xF8
        ret
        stop:
        ldi r16, 0x94          ; TWINT, TWSTO, TWEN
        sts 0xBC, r16
        ldi r16, 200
        pause:
        dec r16
        brne pause
        ret
        """;

    [Fact]
    public void I2c_writes_to_an_eeprom_and_reads_it_back_with_the_datasheets_statuses()
    {
        var (circuit, chip, supply, ground) = Board(I2cEepromWriteRead);
        var eeprom = circuit.Add(new Cirq.Components.Buses.I2cEeprom());
        circuit.Connect(eeprom.Vcc, supply.Terminals[0]);
        circuit.Connect(eeprom.Gnd, ground.Terminals[0]);
        circuit.Connect(eeprom.Sda, chip.TerminalByName("PC4/ADC4"));
        circuit.Connect(eeprom.Scl, chip.TerminalByName("PC5/ADC5"));
        foreach (var line in new[] { eeprom.Sda, eeprom.Scl })
        {
            var pull = circuit.Add(new Resistor(4.7e3));
            circuit.Connect(pull.Terminals[0], supply.Terminals[0]);
            circuit.Connect(pull.Terminals[1], line);
        }

        RunFor(circuit, 3e-3);

        Assert.Equal("HI!", new string([.. Enumerable.Range(0, 3).Select(i => (char)eeprom.Read(i))]));
        Assert.Equal("HI!", new string([.. Enumerable.Range(0, 3).Select(i => (char)chip.Core[0x0100 + i])]));
        Assert.Equal([0x08, 0x18, 0x28, 0x10, 0x40, 0x58], Enumerable.Range(0, 6).Select(i => (int)chip.Core[0x0110 + i]).ToArray());
    }
    [Fact]
    public void An_arduino_uno_runs_the_blink_on_d13_and_feeds_the_circuit_from_its_rails()
    {
        var circuit = new Circuit();
        var uno = circuit.Add(new ArduinoUnoFirmware());
        var ground = circuit.Add(new Ground());
        var load = circuit.Add(new Resistor(1e3));
        var led = circuit.Add(new Resistor(330));
        circuit.Connect(uno.TerminalByName("GND"), ground.Terminals[0]);
        circuit.Connect(uno.TerminalByName("3V3"), load.Terminals[0]);
        circuit.Connect(load.Terminals[1], ground.Terminals[0]);
        var d13 = uno.TerminalByName("D13/LED");
        circuit.Connect(d13, led.Terminals[0]);
        circuit.Connect(led.Terminals[1], ground.Terminals[0]);
        Assert.Equal(["D0/RX", "D1/TX", "D2", "D3", "D4", "D5", "D6", "D7", "D8", "D9", "D10", "D11", "D12", "D13/LED", "A0", "A1", "A2", "A3", "A4", "A5"],
            uno.Gpio.Select(t => t.Name));
        Assert.Equal("Arduino Uno", uno.PartNumber);

        uno.Load(Cirq.Components.Digital.MemoryImage.FromBinary(AssemblerAvr.Assemble(".equ PERIOD = 624\n" + Blink)));
        var simulator = new CircuitSimulator(circuit, new Cirq.Core.Simulation.SimulationSettings { TimeStep = 2e-5, MaxTimeStep = 2e-4, AdaptiveTimeStep = true });
        simulator.Reset();
        simulator.SolveOperatingPoint();
        Assert.Equal(5.0, simulator.NodeVoltage(uno.Vcc), 3);
        Assert.Equal(3.3, simulator.NodeVoltage(load.Terminals[0]), 3);

        var lit = false;
        var dark = false;
        while (simulator.Time < 0.035)
        {
            simulator.Step();
            if (uno.LedLit) lit = true;
            else if (simulator.Time > 0.001) dark = true;
            Assert.Equal(uno.LedLit, simulator.NodeVoltage(d13) > 2.5);
        }

        Assert.True(lit && dark);
    }
    // ───────────────────────────── the debugger ─────────────────────────────

    [Theory]
    [InlineData(nameof(SpiShiftOut))]
    [InlineData(nameof(I2cEepromWriteRead))]
    [InlineData(nameof(Blink))]
    public void The_disassembler_reads_back_what_the_assembler_wrote(string program)
    {
        var source = (string)typeof(AtmegaTests).GetField(program)!.GetValue(null)!;
        if (program == nameof(Blink)) source = ".equ PERIOD = 624\n" + source;
        var bytes = AssemblerAvr.Assemble(source);
        var words = Enumerable.Range(0, AvrCore.FlashWords).Select(i => i * 2 + 1 < bytes.Length ? (ushort)(bytes[i * 2] | (bytes[(i * 2) + 1] << 8)) : (ushort)0).ToArray();
        for (var at = 0; at < bytes.Length / 2;)
        {
            var (text, length) = AvrDisassembler.Disassemble(words, at);
            Assert.DoesNotContain(".dw", text);
            var again = AssemblerAvr.Assemble($".org {at}\n{text}\n");
            Assert.Equal(bytes.Skip(at * 2).Take(length * 2), again.Skip(at * 2).Take(length * 2));
            at += length;
        }

        Assert.Equal(("ldi r16, 0x08", 1), AvrDisassembler.Disassemble(words.Length > 0 ? AssemblerAvr.Assemble("ldi r16, 8").Chunk(2).Select(b => (ushort)(b[0] | (b[1] << 8))).Concat(new ushort[15]).ToArray() : [], 0));
    }

    [Fact]
    public void A_breakpoint_stops_the_firmware_before_its_instruction_and_a_step_runs_one()
    {
        var (circuit, chip, _, _) = Board(SpiShiftOut);
        var send = Enumerable.Range(0, 64).First(i => AvrDisassembler.Disassemble(chip.Core.State.Flash, i).Text == "out 0x2E, r24");
        chip.Breakpoints.Add(send * 2);
        var simulator = new CircuitSimulator(circuit, new Cirq.Core.Simulation.SimulationSettings { TimeStep = 1e-6, MaxTimeStep = 1e-4, AdaptiveTimeStep = true });
        simulator.Reset();
        simulator.SolveOperatingPoint();
        while (!chip.Halted && simulator.Time < 1e-3) simulator.Step();

        Assert.True(chip.Halted);
        Assert.Equal("breakpoint", chip.HaltReason);
        Assert.Equal(send, chip.Core.Pc);
        Assert.Equal(0xA5, chip.Core[24]);
        Assert.Equal(0, chip.Core.State.SpiHalves);   // not yet sent

        // Halted, the circuit's time goes on but the firmware's doesn't.
        var cycles = chip.Core.State.Cycles;
        for (var i = 0; i < 20; i++) simulator.Step();
        Assert.Equal(cycles, chip.Core.State.Cycles);

        // One step: the OUT runs, and the transfer starts.
        chip.StepInstruction();
        while (!chip.Halted) simulator.Step();
        Assert.Equal("step", chip.HaltReason);
        Assert.Equal(send + 1, chip.Core.Pc);
        Assert.Equal(16, chip.Core.State.SpiHalves);

        // Resumed, it runs to the breakpoint again on the second byte.
        chip.Resume();
        var until = simulator.Time + 1e-3;
        while (!chip.Halted && simulator.Time < until) simulator.Step();
        Assert.True(chip.Halted);
        Assert.Equal(send, chip.Core.Pc);
        Assert.Equal(0x3C, chip.Core[24]);
    }
}
