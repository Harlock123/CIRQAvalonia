using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Electromechanical;
using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Processors;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>The parts of 1.3.0, each held to what its datasheet says it does.</summary>
public class NewPartsTests
{
    private static CircuitSimulator Simulate(Circuit circuit, double seconds, double step = 1e-6, double max = 1e-4, bool adaptive = true)
    {
        var simulator = new CircuitSimulator(circuit, new Cirq.Core.Simulation.SimulationSettings { TimeStep = step, MaxTimeStep = max, AdaptiveTimeStep = adaptive });
        simulator.Reset();
        simulator.SolveOperatingPoint();
        if (seconds > 0) simulator.Run(seconds);
        return simulator;
    }

    private static (Circuit Circuit, DcVoltageSource Supply, Ground Ground) Bench(double volts)
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(volts));
        var ground = circuit.Add(new Ground());
        circuit.Connect(supply.Terminals[1], ground.Terminals[0]);
        return (circuit, supply, ground);
    }

    private static Resistor Wire(Circuit circuit, Terminal a, Terminal b, double ohms)
    {
        var r = circuit.Add(new Resistor(ohms));
        circuit.Connect(a, r.Terminals[0]);
        circuit.Connect(r.Terminals[1], b);
        return r;
    }

    // ───── presets, regulators, the buck module ─────

    [Fact]
    public void The_new_transistors_are_in_the_library()
    {
        foreach (var name in new[] { "BD139", "BD140", "2N3055" })
            Assert.Contains(typeof(BjtModel).GetFields().Select(f => f.GetValue(null)).OfType<BjtModel>(), m => m.Name == name);
        Assert.Equal(BjtPolarity.Pnp, BjtModel.Bd140.Polarity);
        Assert.Equal("BS170", MosfetModel.Bs170.Name);
        Assert.Equal("IRF540N", MosfetModel.Irf540N.Name);
    }

    [Fact]
    public void An_lm337_makes_minus_five_volts_and_a_78l05_plus_five()
    {
        var (circuit, supply, ground) = Bench(12);
        var negative = circuit.Add(new DcVoltageSource(-12));
        circuit.Connect(negative.Terminals[1], ground.Terminals[0]);
        var lm337 = circuit.Add(new VoltageRegulator(RegulatorModel.Lm337));
        circuit.Connect(negative.Terminals[0], lm337.Input);
        Wire(circuit, lm337.Output, lm337.Common, 240);
        Wire(circuit, lm337.Common, ground.Terminals[0], 720);
        Wire(circuit, lm337.Output, ground.Terminals[0], 1e3);

        var small = circuit.Add(new VoltageRegulator(RegulatorModel.Lm78L05));
        circuit.Connect(supply.Terminals[0], small.Input);
        circuit.Connect(small.Common, ground.Terminals[0]);
        Wire(circuit, small.Output, ground.Terminals[0], 100);

        var simulator = Simulate(circuit, 0);
        Assert.Equal(-5.0, simulator.NodeVoltage(lm337.Output), 1);
        Assert.Equal(5.0, simulator.NodeVoltage(small.Output), 1);
    }

    [Fact]
    public void A_buck_module_holds_its_output_and_takes_less_current_than_it_gives()
    {
        var (circuit, supply, ground) = Bench(12);
        var buck = circuit.Add(new BuckModule { OutputVoltage = 5 });
        circuit.Connect(supply.Terminals[0], buck.Input);
        circuit.Connect(buck.Ground, ground.Terminals[0]);
        Wire(circuit, buck.Output, ground.Terminals[0], 10);
        var simulator = Simulate(circuit, 1e-4);

        Assert.Equal(5.0, simulator.NodeVoltage(buck.Output), 1);
        Assert.Equal(0.5, buck.OutputCurrent, 2);
        Assert.Equal((5 * 0.5 / (0.88 * 12)) + 5e-3, buck.InputCurrent, 2);

        // Below the dropout it follows the input down.
        supply.Voltage = 5.5;
        simulator = Simulate(circuit, 1e-4);
        Assert.InRange(simulator.NodeVoltage(buck.Output), 3.8, 4.2);
    }

    // ───── LEDs and the IR receiver ─────

    [Fact]
    public void An_rgb_led_mixes_its_three_and_a_bicolour_led_lights_by_direction()
    {
        var (circuit, supply, ground) = Bench(5);
        var rgb = circuit.Add(new RgbLed());
        circuit.Connect(rgb.Terminals[3], ground.Terminals[0]);
        Wire(circuit, supply.Terminals[0], rgb.Terminals[0], 150);
        Wire(circuit, supply.Terminals[0], rgb.Terminals[2], 150);   // red and blue: magenta

        var bicolour = circuit.Add(new BicolourLed());
        Wire(circuit, supply.Terminals[0], bicolour.Terminals[0], 220);
        circuit.Connect(bicolour.Terminals[1], ground.Terminals[0]);

        Simulate(circuit, 2e-5);
        Assert.True(rgb.Brightness[0] > 0.5 && rgb.Brightness[2] > 0.3);
        Assert.Equal(0, rgb.Brightness[1], 3);
        var (r, g, b, _) = rgb.Mix();
        Assert.True(r > 0.3 && b > 0.3 && g < Math.Min(r, b));
        Assert.True(bicolour.Brightness[0] > 0.5);
        Assert.Equal(0, bicolour.Brightness[1], 3);
        Assert.Contains(Led.Colours, c => c.Name == "Infrared");
    }

    [Fact]
    public void The_ir_receiver_puts_out_a_whole_nec_frame_for_a_press()
    {
        var (circuit, supply, ground) = Bench(5);
        var receiver = circuit.Add(new IrReceiver { Address = 0x00, Command = 0x46, SendAt = 1e-3 });
        circuit.Connect(receiver.Vcc, supply.Terminals[0]);
        circuit.Connect(receiver.Gnd, ground.Terminals[0]);
        var simulator = Simulate(circuit, 0, 1e-5, 1e-3);
        var edges = new List<(double T, bool High)>();
        var was = true;
        while (simulator.Time < 0.08)
        {
            simulator.Step();
            var high = simulator.NodeVoltage(receiver.Out) > 2.5;
            if (high != was) edges.Add((simulator.Time, high));
            was = high;
        }

        // 9 ms low, 4.5 ms high, then 32 bits from the spaces after each 562 µs burst.
        Assert.Equal(1e-3, edges[0].T, 4);
        Assert.Equal(9e-3, edges[1].T - edges[0].T, 4);
        Assert.Equal(4.5e-3, edges[2].T - edges[1].T, 4);
        var bits = 0L;
        for (var i = 0; i < 32; i++)
        {
            var space = edges[4 + (2 * i)].T - edges[3 + (2 * i)].T;
            if (space > 1.1e-3) bits |= 1L << i;
        }

        Assert.Equal(0x00, (int)(bits & 0xFF));
        Assert.Equal(0xFF, (int)((bits >> 8) & 0xFF));
        Assert.Equal(0x46, (int)((bits >> 16) & 0xFF));
        Assert.Equal(0xB9, (int)((bits >> 24) & 0xFF));
        Assert.Equal(1, receiver.FramesSent);
    }

    // ───── the DHT22, answering a host ─────

    [Fact]
    public void The_dht22_answers_a_start_pulse_with_its_reading()
    {
        var (circuit, supply, ground) = Bench(5);
        var dht = circuit.Add(new DhtSensor { Temperature = -3.5, Humidity = 61.3 });
        circuit.Connect(dht.Vcc, supply.Terminals[0]);
        circuit.Connect(dht.Gnd, ground.Terminals[0]);
        Wire(circuit, supply.Terminals[0], dht.Data, 10e3);

        // The host: an ATmega holding PD2 low for 1.2 ms, then letting go.
        var chip = circuit.Add(new Atmega328p());
        circuit.Connect(supply.Terminals[0], chip.Vcc);
        circuit.Connect(supply.Terminals[0], chip.Avcc);
        circuit.Connect(chip.Gnd, ground.Terminals[0]);
        circuit.Connect(chip.Gnd2, ground.Terminals[0]);
        circuit.Connect(chip.TerminalByName("PD2/INT0"), dht.Data);
        chip.Load(MemoryImage.FromBinary(AssemblerAvr.Assemble("""
            sbi 0x0A, 2            ; PD2 low (PORTD is 0)
            ldi r24, 0x00
            ldi r25, 0x13          ; 4864 times round four cycles: about 1.2 ms
            wait:
            sbiw r24, 1
            brne wait
            cbi 0x0A, 2            ; let go
            done:
            rjmp done
            """)));

        var simulator = Simulate(circuit, 0, 1e-6, 2e-5);
        var edges = new List<(double T, bool High)>();
        var was = true;
        while (simulator.Time < 8e-3)
        {
            simulator.Step();
            var high = simulator.NodeVoltage(dht.Data) > 2.5;
            if (high != was) edges.Add((simulator.Time, high));
            was = high;
        }

        // The host's low, then the sensor's 80 µs low and 80 µs high, then 40 bits, each a high of 27 or 70 µs.
        Assert.Equal(1, dht.Answers);
        var highs = new List<double>();
        for (var i = 0; i + 1 < edges.Count; i++)
            if (edges[i].High) highs.Add(edges[i + 1].T - edges[i].T);
        var bits = highs.Skip(highs.Count - 40).Select(h => h > 50e-6 ? 1 : 0).ToList();
        var bytes = Enumerable.Range(0, 5).Select(b => bits.Skip(b * 8).Take(8).Aggregate(0, (v, bit) => (v << 1) | bit)).ToArray();
        Assert.Equal(dht.Bytes().Select(b => (int)b), bytes);
        Assert.Equal(613, (bytes[0] << 8) | bytes[1]);
        Assert.Equal(0x8000 | 35, (bytes[2] << 8) | bytes[3]);
        Assert.Equal((bytes[0] + bytes[1] + bytes[2] + bytes[3]) & 0xFF, bytes[4]);
    }

    // ───── I2C modules ─────

    private static (Circuit Circuit, I2cMaster Master, Ground Ground, DcVoltageSource Supply) I2cBench(string script)
    {
        var (circuit, supply, ground) = Bench(3.3);
        var master = circuit.Add(new I2cMaster { Transactions = script, ClockFrequency = 400e3, StartDelay = 20e-6 });
        circuit.Connect(master.Vcc, supply.Terminals[0]);
        circuit.Connect(master.Gnd, ground.Terminals[0]);
        Wire(circuit, supply.Terminals[0], master.Sda, 4.7e3);
        Wire(circuit, supply.Terminals[0], master.Scl, 4.7e3);
        return (circuit, master, ground, supply);
    }

    private static void Attach(Circuit circuit, I2cDevice device, I2cMaster master, DcVoltageSource supply, Ground ground)
    {
        circuit.Connect(device.Vcc, supply.Terminals[0]);
        circuit.Connect(device.Gnd, ground.Terminals[0]);
        circuit.Connect(device.Sda, master.Sda);
        circuit.Connect(device.Scl, master.Scl);
    }

    [Fact]
    public void The_oled_takes_its_commands_and_draws_what_it_is_sent()
    {
        // Display on, horizontal addressing, the whole screen; then data: a byte down each of the first two columns.
        var (circuit, master, ground, supply) = I2cBench("w 3C 00 AF 20 00 21 00 7F 22 00 07; w 3C 40 FF 81; w 3C 00 A7");
        var oled = circuit.Add(new Ssd1306());
        Attach(circuit, oled, master, supply, ground);
        Simulate(circuit, 1.5e-3, 2e-7, 2e-6);

        Assert.True(master.LastTransferAcknowledged);
        Assert.True(oled.DisplayOn);
        Assert.Equal(0xFF, oled.Ram[0]);
        Assert.Equal(0x81, oled.Ram[1]);
        Assert.True(oled.Inverted);
        Assert.False(oled.Pixel(0, 0));      // lit, then inverted
        Assert.True(oled.Pixel(1, 3));
        Assert.True(oled.Pixel(5, 40));
    }

    [Fact]
    public void The_rtc_and_the_accelerometer_answer_their_registers()
    {
        var (circuit, master, ground, supply) = I2cBench("w 68 00; r 68 3; w 69 6B 00; w 69 3B; r 69 6; w 69 75; r 69 1");
        var rtc = circuit.Add(new Ds3231 { StartTime = "2026-11-11 09:41:30" });
        var imu = circuit.Add(new Mpu6050 { AddressHigh = true, Pitch = 30 });
        Attach(circuit, rtc, master, supply, ground);
        Attach(circuit, imu, master, supply, ground);
        Simulate(circuit, 2e-3, 2e-7, 2e-6);

        var read = master.ReceivedBytes;
        Assert.Equal([0x30, 0x41, 0x09], read.Take(3));            // 09:41:30 in BCD, seconds first
        Assert.False(imu.Asleep);
        var ax = (short)((read[3] << 8) | read[4]);
        var az = (short)((read[7] << 8) | read[8]);
        Assert.Equal(-Math.Sin(Math.PI / 6) * 16384, ax, 0);
        Assert.Equal(Math.Cos(Math.PI / 6) * 16384, az, 0);
        Assert.Equal(0x68, read[9]);
    }

    // ───── SPI parts ─────

    private static (Circuit Circuit, SpiMaster Master, Ground Ground, DcVoltageSource Supply) SpiBench(string script)
    {
        var (circuit, supply, ground) = Bench(5);
        var master = circuit.Add(new SpiMaster { Transactions = script, ClockFrequency = 1e6 });
        circuit.Connect(master.Vcc, supply.Terminals[0]);
        circuit.Connect(master.Gnd, ground.Terminals[0]);
        return (circuit, master, ground, supply);
    }

    private static void Spi(Circuit circuit, SpiMaster master, DigitalIc chip, Terminal cs, Terminal sck, Terminal mosi, Terminal? miso, DcVoltageSource supply, Ground ground)
    {
        circuit.Connect(chip.Vcc, supply.Terminals[0]);
        circuit.Connect(chip.Gnd, ground.Terminals[0]);
        circuit.Connect(master.ChipSelect, cs);
        circuit.Connect(master.Clock, sck);
        circuit.Connect(master.MasterOut, mosi);
        if (miso is not null) circuit.Connect(master.MasterIn, miso);
    }

    [Fact]
    public void The_flash_identifies_itself_programs_a_page_and_reads_it_back()
    {
        var (circuit, master, ground, supply) = SpiBench("9F 00 00 00; 06; 02 00 01 00 CA FE; 03 00 01 00 00 00; 05 00");
        var flash = circuit.Add(new W25qFlash());
        Spi(circuit, master, flash, flash.ChipSelect, flash.Clock, flash.Mosi, flash.Miso, supply, ground);
        Simulate(circuit, 2e-3, 1e-7, 1e-6);

        Assert.Equal([0xEF, 0x40, 0x16], master.ReceivedBytes.Skip(1).Take(3));
        Assert.Equal(0xCA, flash.Read(0x100));
        Assert.Equal(0xFE, flash.Read(0x101));
        Assert.Equal(0xFF, flash.Read(0x102));
    }

    [Fact]
    public void The_sd_card_initialises_and_writes_and_reads_a_block()
    {
        var block = string.Join(" ", Enumerable.Range(0, 512).Select(i => ((byte)i).ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
        var script = string.Join(";",
            "40 00 00 00 00 95 FF FF",                  // CMD0
            "48 00 00 01 AA 87 FF FF FF FF FF FF",       // CMD8
            "77 00 00 00 00 01 FF FF; 69 40 00 00 00 01 FF FF",
            "77 00 00 00 00 01 FF FF; 69 40 00 00 00 01 FF FF",
            $"58 00 00 00 05 01 FF FF FE {block} FF FF FF FF FF FF FF",   // CMD24 block 5
            "51 00 00 00 05 01 FF FF FF FF FF FF");      // CMD17 block 5, the start of it
        var (circuit, master, ground, supply) = SpiBench(script);
        master.ClockFrequency = 4e6;
        var card = circuit.Add(new SdCard());
        Spi(circuit, master, card, card.ChipSelect, card.Clock, card.Mosi, card.Miso, supply, ground);
        Simulate(circuit, 4e-3, 5e-8, 2e-7);

        Assert.Equal(1, card.Block(5)[1]);
        Assert.Equal(255, card.Block(5)[255]);
        Assert.Contains("ready", card.ValueLabel);
        var got = master.ReceivedBytes;
        Assert.Contains(0xFE, got.Skip(got.Count - 12));    // the read's data token
    }

    [Fact]
    public void The_digital_pot_sets_its_wiper_and_the_dds_its_frequency()
    {
        var (circuit, master, ground, supply) = SpiBench("00 20; 04; 04");
        var pot = circuit.Add(new Mcp4131());
        Spi(circuit, master, pot, pot.ChipSelect, pot.Clock, pot.Mosi, null, supply, ground);
        circuit.Connect(pot.TerminalA, supply.Terminals[0]);
        circuit.Connect(pot.TerminalB, ground.Terminals[0]);
        var simulator = Simulate(circuit, 2e-4, 1e-7, 1e-6);
        Assert.Equal(0x22, pot.Step);
        Assert.Equal(5.0 * (pot.Step / 128.0), simulator.NodeVoltage(pot.Wiper), 1);

        // The AD9833: reset with B28, 1 kHz in FREQ0 (10,737 = 1 kHz × 2^28 / 25 MHz), then out of reset, sine.
        var (c2, m2, g2, s2) = SpiBench("21 00; 69 F1; 40 00; 00 00");
        var dds = c2.Add(new Ad9833());
        c2.Connect(dds.Vcc, s2.Terminals[0]);
        c2.Connect(dds.Gnd, g2.Terminals[0]);
        c2.Connect(dds.AnalogGround, g2.Terminals[0]);
        c2.Connect(m2.ChipSelect, dds.Sync);
        c2.Connect(m2.Clock, dds.Clock);
        c2.Connect(m2.MasterOut, dds.Data);
        Wire(c2, dds.Output, g2.Terminals[0], 10e3);
        Simulate(c2, 1e-4, 1e-7, 1e-6);
        Assert.False(dds.InReset);
        Assert.Equal(1000, dds.Frequency, 0);
        Assert.Equal("sine", dds.Shape);
        Assert.InRange(dds.VoltageAt(0.25e-3 + 1e-4, 5), 0.0, 0.7);
    }

    [Fact]
    public void The_matrix_lights_the_rows_it_is_sent_and_the_tm1637_its_digits()
    {
        var (circuit, master, ground, supply) = SpiBench("0C 01; 0B 07; 01 81; 08 3C");
        var matrix = circuit.Add(new Max7219Matrix());
        Spi(circuit, master, matrix, matrix.Load, matrix.Clock, matrix.DataIn, null, supply, ground);
        Simulate(circuit, 2e-4, 1e-7, 1e-6);
        Assert.False(matrix.Shutdown);
        Assert.True(matrix.Lit(0, 0) && matrix.Lit(0, 7) && !matrix.Lit(0, 3));
        Assert.True(matrix.Lit(7, 2) && !matrix.Lit(7, 0));

        // The TM1637's two-wire bus, LSB first, from a pattern generator: data mode, the four digits from C0, display on.
        var (c2, s2, g2) = Bench(5);
        var bus = c2.Add(new PatternGenerator(2) { Pattern = Tm1637Display.PatternFor([0x40], [0xC0, 0x06, 0x5B, 0x4F, 0x66], [0x8F]), StepRate = 200e3, Repeats = false, StartDelay = 10e-6 });
        var tm = c2.Add(new Tm1637Display());
        c2.Connect(tm.Vcc, s2.Terminals[0]);
        c2.Connect(tm.Gnd, g2.Terminals[0]);
        c2.Connect(tm.Clock, bus.Terminals[0]);
        c2.Connect(tm.Dio, bus.Terminals[1]);
        Wire(c2, s2.Terminals[0], tm.Dio, 10e3);
        Simulate(c2, 1e-3, 2e-7, 2e-6);
        Assert.True(tm.On);
        Assert.Equal(7, tm.Brightness);
        Assert.Equal([0x06, 0x5B, 0x4F, 0x66], Enumerable.Range(0, 4).Select(i => (int)tm.Segments(i)));   // 1 2 3 4
    }

    // ───── motor drivers ─────

    [Fact]
    public void The_l293d_and_l298n_drive_their_outputs_as_their_inputs_say()
    {
        var (circuit, supply, ground) = Bench(12);
        var logic = circuit.Add(new DcVoltageSource(5));
        circuit.Connect(logic.Terminals[1], ground.Terminals[0]);
        var chip = circuit.Add(new L293d());
        circuit.Connect(chip.TerminalByName("VCC2"), supply.Terminals[0]);
        circuit.Connect(chip.LogicSupply, logic.Terminals[0]);
        foreach (var gnd in chip.Terminals.Where(t => t.Name == "GND")) circuit.Connect(gnd, ground.Terminals[0]);
        circuit.Connect(chip.TerminalByName("EN1,2"), logic.Terminals[0]);
        circuit.Connect(chip.TerminalByName("1A"), logic.Terminals[0]);
        circuit.Connect(chip.TerminalByName("2A"), ground.Terminals[0]);
        var motor = Wire(circuit, chip.TerminalByName("1Y"), chip.TerminalByName("2Y"), 24);

        var module = circuit.Add(new L298nModule());
        circuit.Connect(module.TerminalByName("+12V"), supply.Terminals[0]);
        circuit.Connect(module.TerminalByName("GND"), ground.Terminals[0]);
        circuit.Connect(module.TerminalByName("ENA"), logic.Terminals[0]);
        circuit.Connect(module.TerminalByName("IN1"), ground.Terminals[0]);
        circuit.Connect(module.TerminalByName("IN2"), logic.Terminals[0]);
        Wire(circuit, module.TerminalByName("OUT1"), module.TerminalByName("OUT2"), 24);

        var simulator = Simulate(circuit, 0);
        var across = simulator.NodeVoltage(chip.TerminalByName("1Y")) - simulator.NodeVoltage(chip.TerminalByName("2Y"));
        Assert.InRange(across, 10.5, 12);
        Assert.Equal(["high", "low", "off", "off"], chip.Outputs);
        var reverse = simulator.NodeVoltage(module.TerminalByName("OUT1")) - simulator.NodeVoltage(module.TerminalByName("OUT2"));
        Assert.InRange(reverse, -12, -10.5);
        Assert.NotNull(motor);

        var drv = new Drv8825();
        Assert.Equal("M0", drv.Ms1.Name);
        Assert.Contains("DRV8825", drv.ValueLabel + " " + new Drv8825().GetType().Name);
    }

    // ───── analog chips ─────

    [Fact]
    public void The_lm3914_lights_a_bar_up_to_the_signal_at_ten_times_its_reference_current()
    {
        foreach (var bar in new[] { true, false })
        {
        var (circuit, supply, ground) = Bench(5);
        var chip = circuit.Add(new Lm3914());
        circuit.Connect(chip.Positive, supply.Terminals[0]);
        circuit.Connect(chip.Negative, ground.Terminals[0]);
        circuit.Connect(chip.Low, ground.Terminals[0]);
        circuit.Connect(chip.ReferenceAdjust, ground.Terminals[0]);
        circuit.Connect(chip.High, chip.ReferenceOut);
        Wire(circuit, chip.ReferenceOut, ground.Terminals[0], 1.25e3);
        if (bar) circuit.Connect(chip.Mode, supply.Terminals[0]);
        var signal = circuit.Add(new DcVoltageSource(0.6));
        circuit.Connect(signal.Terminals[1], ground.Terminals[0]);
        circuit.Connect(signal.Terminals[0], chip.Signal);
        var leds = new List<Led>();
        foreach (var pin in chip.Leds)
        {
            var led = circuit.Add(Led.OfColour("Green"));
            circuit.Connect(supply.Terminals[0], led.Terminals[0]);
            circuit.Connect(led.Terminals[1], pin);
            leds.Add(led);
        }

        Simulate(circuit, 1e-4);
        if (bar)
        {
            Assert.Equal([true, true, true, true, false, false, false, false, false, false], chip.Lit);
            Assert.InRange(leds[0].Current, 8e-3, 12e-3);
            Assert.True(leds[5].Current < 1e-5);
        }
        else
        {
            // Dot mode: only the top one.
            Assert.Equal(1, chip.Lit.Count(l => l));
            Assert.True(chip.Lit[3]);
        }
        }
    }

    [Fact]
    public void The_ne567_locks_on_a_tone_at_its_r_and_c_and_not_off_it()
    {
        foreach (var (frequency, locks) in new[] { (909.0, true), (1500.0, false) })
        {
            var (circuit, supply, ground) = Bench(5);
            var chip = circuit.Add(new Ne567());
            circuit.Connect(chip.Vcc, supply.Terminals[0]);
            circuit.Connect(chip.Gnd, ground.Terminals[0]);
            Wire(circuit, chip.TimingR, chip.TimingC, 10e3);
            var c1 = circuit.Add(new Capacitor(100e-9));
            circuit.Connect(chip.TimingC, c1.Terminals[0]);
            circuit.Connect(c1.Terminals[1], ground.Terminals[0]);
            foreach (var (pin, farads) in new[] { (chip.LoopFilter, 1e-6), (chip.OutputFilter, 2.2e-6) })
            {
                var c = circuit.Add(new Capacitor(farads));
                circuit.Connect(pin, c.Terminals[0]);
                circuit.Connect(c.Terminals[1], ground.Terminals[0]);
            }

            var tone = circuit.Add(new FunctionGenerator(Waveform.Sine, frequency, 0.3));
            circuit.Connect(tone.Return, ground.Terminals[0]);
            var coupling = circuit.Add(new Capacitor(1e-6));
            circuit.Connect(tone.Output, coupling.Terminals[0]);
            circuit.Connect(coupling.Terminals[1], chip.Input);
            Wire(circuit, supply.Terminals[0], chip.Output, 10e3);

            var simulator = Simulate(circuit, 25e-3, 2e-6, 5e-6);
            Assert.InRange(chip.CentreFrequency, 900, 920);
            Assert.Equal(locks, chip.Locked);
            Assert.Equal(locks, simulator.NodeVoltage(chip.Output) < 1);
        }
    }

    [Fact]
    public void The_xr2206_runs_at_one_over_r_c_and_the_556_and_7555_oscillate()
    {
        var (circuit, supply, ground) = Bench(12);
        var gen = circuit.Add(new Xr2206());
        circuit.Connect(gen.Vcc, supply.Terminals[0]);
        circuit.Connect(gen.Gnd, ground.Terminals[0]);
        Wire(circuit, gen.TimingResistor, ground.Terminals[0], 10e3);
        var c = circuit.Add(new Capacitor(100e-9));
        circuit.Connect(gen.Capacitor1, c.Terminals[0]);
        circuit.Connect(c.Terminals[1], gen.Capacitor2);
        Wire(circuit, supply.Terminals[0], gen.Sync, 10e3);
        var simulator = Simulate(circuit, 0, 1e-6, 4e-6);
        var rises = new List<double>();
        var was = false;
        while (simulator.Time < 6e-3)
        {
            simulator.Step();
            var high = simulator.NodeVoltage(gen.Sync) > 6;
            if (high && !was) rises.Add(simulator.Time);
            was = high;
        }

        var period = (rises[^1] - rises[1]) / (rises.Count - 2);
        Assert.Equal(1e-3, period, 4);

        // The 7555 and the 556 in the classic astable: 1k, 10k, 100 nF is 686 Hz.
        foreach (var dual in new[] { false, true })
        {
            var (c2, s2, g2) = Bench(5);
            Terminal disch, thres, trig, outPin;
            Func<bool> outHigh;
            if (dual)
            {
                var timer = c2.Add(new Ne556());
                c2.Connect(timer.Vcc, s2.Terminals[0]);
                c2.Connect(timer.Gnd, g2.Terminals[0]);
                c2.Connect(timer.TerminalByName("RESET1"), s2.Terminals[0]);
                (disch, thres, trig, outPin) = (timer.TerminalByName("DISCH1"), timer.TerminalByName("THRES1"), timer.TerminalByName("TRIG1"), timer.TerminalByName("OUT1"));
                outHigh = () => timer.IsOutputHigh(0);
            }
            else
            {
                var timer = c2.Add(new Cmos7555());
                c2.Connect(timer.Vcc, s2.Terminals[0]);
                c2.Connect(timer.Gnd, g2.Terminals[0]);
                c2.Connect(timer.Reset, s2.Terminals[0]);
                (disch, thres, trig, outPin) = (timer.Discharge, timer.Threshold, timer.Trigger, timer.Out);
                outHigh = () => timer.IsOutputHigh;
            }

            Wire(c2, s2.Terminals[0], disch, 1e3);
            Wire(c2, disch, thres, 10e3);
            c2.Connect(thres, trig);
            var timing = c2.Add(new Capacitor(100e-9));
            c2.Connect(thres, timing.Terminals[0]);
            c2.Connect(timing.Terminals[1], g2.Terminals[0]);
            var sim2 = Simulate(c2, 0, 1e-6, 5e-6);
            var edges = new List<double>();
            var before = false;
            var top = 0.0;
            while (sim2.Time < 10e-3)
            {
                sim2.Step();
                if (outHigh() && !before) edges.Add(sim2.Time);
                before = outHigh();
                top = Math.Max(top, sim2.NodeVoltage(outPin));
            }

            Assert.Equal(1 / Ne555.AstableFrequency(1e3, 10e3, 100e-9), (edges[^1] - edges[1]) / (edges.Count - 2), 4);
            if (!dual) Assert.True(top > 4.9);
        }
    }

    [Fact]
    public void A_74141_lights_the_nixie_digit_its_bcd_input_counts()
    {
        var (circuit, supply, ground) = Bench(5);
        var high = circuit.Add(new DcVoltageSource(180));
        circuit.Connect(high.Terminals[1], ground.Terminals[0]);
        var driver = circuit.Add(new Ic74141());
        circuit.Connect(driver.Vcc, supply.Terminals[0]);
        circuit.Connect(driver.Gnd, ground.Terminals[0]);
        circuit.Connect(driver.A, supply.Terminals[0]);   // 5 = A and C
        circuit.Connect(driver.B, ground.Terminals[0]);
        circuit.Connect(driver.C, supply.Terminals[0]);
        circuit.Connect(driver.D, ground.Terminals[0]);
        var tube = circuit.Add(new NixieTube());
        Wire(circuit, high.Terminals[0], tube.Anode, 22e3);
        for (var d = 0; d < 10; d++) circuit.Connect(tube.Cathode(d), driver.Outputs[d]);
        Simulate(circuit, 2e-4, 1e-6, 2e-5);

        Assert.Equal(5, tube.Digit);
        Assert.True(tube.Glow(5) > 0.3);
        Assert.Equal(0, tube.Glow(3), 3);
    }

    [Fact]
    public void A_12ax7_stage_biases_and_amplifies_as_a_valve_does()
    {
        var (circuit, supply, ground) = Bench(250);
        var valve = circuit.Add(new Triode());
        Wire(circuit, supply.Terminals[0], valve.Plate, 100e3);
        Wire(circuit, valve.Cathode, ground.Terminals[0], 1.5e3);
        var bypass = circuit.Add(new Capacitor(22e-6));
        circuit.Connect(valve.Cathode, bypass.Terminals[0]);
        circuit.Connect(bypass.Terminals[1], ground.Terminals[0]);
        Wire(circuit, valve.Grid, ground.Terminals[0], 1e6);
        var input = circuit.Add(new FunctionGenerator(Waveform.Sine, 1e3, 0.2));
        circuit.Connect(input.Return, ground.Terminals[0]);
        var coupling = circuit.Add(new Capacitor(100e-9));
        circuit.Connect(input.Output, coupling.Terminals[0]);
        circuit.Connect(coupling.Terminals[1], valve.Grid);

        var simulator = Simulate(circuit, 0, 5e-6, 5e-6, adaptive: false);
        Assert.InRange(simulator.NodeVoltage(valve.Cathode), 0.8, 2.2);
        Assert.InRange(simulator.NodeVoltage(valve.Plate), 100, 200);

        simulator.Run(20e-3);
        double low = double.MaxValue, top = double.MinValue;
        while (simulator.Time < 25e-3)
        {
            simulator.Step();
            var v = simulator.NodeVoltage(valve.Plate);
            (low, top) = (Math.Min(low, v), Math.Max(top, v));
        }

        var gain = (top - low) / 0.2;
        Assert.InRange(gain, 45, 75);
    }

    // ───── the AY-3-8910, the ATtiny85, the AT89C2051 ─────

    /// <summary>A Uno-style ATmega writing the AY's registers: data on PORTD, BC1 on PC0, BDIR on PC1, BC2 held high.</summary>
    public const string AyTone = """
        ldi r16, 0xFF
        out 0x0A, r16          ; DDRD: the data bus
        ldi r16, 0x03
        out 0x07, r16          ; DDRC: BC1, BDIR
        ldi r20, 0
        ldi r21, 142           ; channel A at 1 MHz / (16 × 142) = 440 Hz
        rcall write
        ldi r20, 1
        ldi r21, 0
        rcall write
        ldi r20, 7
        ldi r21, 0x3E          ; the mixer: tone A only
        rcall write
        ldi r20, 8
        ldi r21, 15            ; channel A at full level
        rcall write
        done:
        rjmp done
        write:
        out 0x0B, r20          ; the register's address
        ldi r16, 0x03
        out 0x08, r16          ; BDIR, BC1: latch it
        clr r16
        out 0x08, r16
        out 0x0B, r21          ; the value
        ldi r16, 0x02
        out 0x08, r16          ; BDIR alone: write it
        clr r16
        out 0x08, r16
        ret
        """;

    [Fact]
    public void An_ay_3_8910_plays_the_tone_its_registers_ask_for()
    {
        var (circuit, supply, ground) = Bench(5);
        var chip = circuit.Add(new Atmega328p());
        circuit.Connect(supply.Terminals[0], chip.Vcc);
        circuit.Connect(supply.Terminals[0], chip.Avcc);
        circuit.Connect(chip.Gnd, ground.Terminals[0]);
        circuit.Connect(chip.Gnd2, ground.Terminals[0]);
        chip.Load(MemoryImage.FromBinary(AssemblerAvr.Assemble("ldi r16, 0x08\nout 0x3E, r16\nldi r16, 0xFF\nout 0x3D, r16\n" + AyTone)));
        var ay = circuit.Add(new Ay38910());
        circuit.Connect(ay.Vcc, supply.Terminals[0]);
        circuit.Connect(ay.Gnd, ground.Terminals[0]);
        circuit.Connect(ay.Bc2, supply.Terminals[0]);
        circuit.Connect(ay.Reset, supply.Terminals[0]);
        circuit.Connect(ay.Bc1, chip.TerminalByName("PC0/ADC0"));
        circuit.Connect(ay.Bdir, chip.TerminalByName("PC1/ADC1"));
        var portD = new[] { "PD0/RXD", "PD1/TXD", "PD2/INT0", "PD3/INT1", "PD4", "PD5", "PD6", "PD7" };
        for (var i = 0; i < 8; i++) circuit.Connect(ay.Data[i], chip.TerminalByName(portD[i]));
        Wire(circuit, ay.ChannelA, ground.Terminals[0], 1e3);

        var simulator = Simulate(circuit, 0, 2e-6, 2e-5);
        var rises = new List<double>();
        var was = false;
        while (simulator.Time < 20e-3)
        {
            simulator.Step();
            var high = simulator.NodeVoltage(ay.ChannelA) > 0.2;
            if (high && !was) rises.Add(simulator.Time);
            was = high;
        }

        Assert.Equal(142, ay.Registers[0]);
        Assert.Equal(0x3E, ay.Registers[7]);
        Assert.Equal(440.1, ay.ToneFrequency(0), 1);
        var measured = (rises.Count - 2) / (rises[^1] - rises[1]);
        Assert.Equal(440.1, measured, 0);
    }

    /// <summary>An ATtiny85 blink: Timer0's overflow at clk/1024 toggling PB0 by writing PINB.</summary>
    public const string TinyBlink = """
        rjmp start
        .org 5                 ; TIMER0_OVF, a one-word vector on the tiny
        rjmp overflow
        start:
        sbi 0x17, 0            ; DDRB: PB0 an output
        ldi r16, 0x05
        out 0x33, r16          ; TCCR0B: clk/1024
        ldi r16, 0x02
        out 0x39, r16          ; TIMSK: TOIE0
        sei
        loop:
        rjmp loop
        overflow:
        sbi 0x16, 0            ; writing PINB0 toggles PB0
        reti
        """;

    [Fact]
    public void An_attiny85_runs_its_own_registers_and_vectors()
    {
        var (circuit, supply, ground) = Bench(5);
        var tiny = circuit.Add(new Attiny85());
        circuit.Connect(tiny.Vcc, supply.Terminals[0]);
        circuit.Connect(tiny.Gnd, ground.Terminals[0]);
        Wire(circuit, tiny.TerminalByName("PB0"), ground.Terminals[0], 10e3);
        tiny.Load(MemoryImage.FromBinary(AssemblerAvr.Assemble(TinyBlink)));
        Assert.Equal(8e6, tiny.ClockFrequency);
        var simulator = Simulate(circuit, 0, 2e-5, 2e-4);
        var edges = new List<double>();
        var was = false;
        while (simulator.Time < 0.12)
        {
            simulator.Step();
            var high = simulator.NodeVoltage(tiny.TerminalByName("PB0")) > 2.5;
            if (high != was) edges.Add(simulator.Time);
            was = high;
        }

        // 1024 × 256 / 8 MHz = 32.768 ms between toggles.
        Assert.True(edges.Count >= 3, string.Join(", ", edges));
        Assert.Equal(32.768e-3, edges[2] - edges[1], 5);
        Assert.InRange(tiny.Core.StackPointer, 0x25D, 0x25F);   // the top of the tiny's 512 bytes of SRAM
    }

    /// <summary>The AT89C2051 sending "OK" at 9600 baud from an 11.0592 MHz crystal, and toggling P1.0.</summary>
    public const string Uart8051 = """
        ORG 0
        MOV TMOD, #20H         ; Timer 1 in mode 2, auto-reload
        MOV TH1, #0FDH         ; 9600 baud at 11.0592 MHz
        MOV SCON, #50H         ; mode 1, receiver on
        SETB TR1
        MOV R0, #0
        SEND: MOV A, R0
        MOV DPTR, #TEXT
        MOVC A, @A+DPTR
        JZ DONE
        MOV SBUF, A
        WAIT: JNB TI, WAIT
        CLR TI
        INC R0
        SJMP SEND
        DONE: CPL P1.0
        SJMP DONE
        TEXT: DB "OK", 0
        """;

    [Fact]
    public void An_at89c2051_sends_text_at_9600_baud()
    {
        var (circuit, supply, ground) = Bench(5);
        var mcu = circuit.Add(new At89c2051());
        circuit.Connect(mcu.Vcc, supply.Terminals[0]);
        circuit.Connect(mcu.Gnd, ground.Terminals[0]);
        circuit.Connect(mcu.Reset, ground.Terminals[0]);
        var terminal = circuit.Add(new SerialTerminal { BaudRate = 9600, Message = "" });
        circuit.Connect(terminal.Vcc, supply.Terminals[0]);
        circuit.Connect(terminal.Gnd, ground.Terminals[0]);
        circuit.Connect(terminal.Receive, mcu.Port3[1]);
        mcu.Load(MemoryImage.FromBinary(Assembler8051.Assemble(Uart8051)));
        Assert.Null(mcu.FirmwareError);
        Simulate(circuit, 4e-3, 2e-6, 2e-5);
        Assert.Equal(96, mcu.Core.BitCycles);
        Assert.Equal("OK", terminal.ReceivedText);
        Assert.Equal(0, terminal.FramingErrors);
    }

    [Fact]
    public void The_8051_core_does_its_arithmetic_and_flags()
    {
        var core = new I8051Core();
        core.LoadCode([(0, Assembler8051.Assemble("""
            MOV A, #0F0H
            ADD A, #20H            ; 0x110: carry, A = 0x10
            MOV R1, A
            MOV A, #99H
            ADD A, #01H
            DA A                   ; BCD 99 + 1 = 00, carry
            MOV 30H, #7
            MOV B, #6
            MOV A, 30H
            MUL AB                 ; 42
            MOV R2, A
            MOV A, #100
            MOV B, #7
            DIV AB                 ; 14 r 2
            MOV R3, A
            MOV R4, B
            SETB 20H.3
            MOV C, 20H.3
            MOV R5, #3
            LOOP: DJNZ R5, LOOP
            HERE: SJMP HERE
            """))]);
        core.Reset();
        for (var i = 0; i < 200; i++) core.Step();
        Assert.Equal(0x10, (int)core[1]);
        Assert.Equal(42, (int)core[2]);
        Assert.Equal(14, (int)core[3]);
        Assert.Equal(2, (int)core[4]);
        Assert.Equal(0, (int)core[5]);
        Assert.Equal(0x08, (int)core[0x20]);
        Assert.Equal(0x80, core.StatusWord & 0x80);    // C from bit 20H.3
    }
}
