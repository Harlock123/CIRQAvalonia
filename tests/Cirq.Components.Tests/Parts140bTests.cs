using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Processors;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using static Cirq.Components.Tests.Parts140Tests;

namespace Cirq.Components.Tests;

/// <summary>More of 1.4.0's parts: converters, displays, valves, sound chips and the 1970s.</summary>
public class Parts140bTests
{
    private static T Powered<T>(Circuit circuit, DcVoltageSource supply, Ground ground, T chip) where T : DigitalIc
    {
        circuit.Add(chip);
        circuit.Connect(chip.Vcc, supply.Terminals[0]);
        circuit.Connect(chip.Gnd, ground.Terminals[0]);
        return chip;
    }

    [Fact]
    public void The_adc0804_converts_on_a_write_and_puts_the_result_on_a_read()
    {
        var (circuit, supply, ground) = Bench(5);
        var adc = Powered(circuit, supply, ground, new Adc0804());
        circuit.Connect(adc.AnalogGround, ground.Terminals[0]);
        circuit.Connect(adc.InputMinus, ground.Terminals[0]);
        circuit.Connect(adc.ChipSelect, ground.Terminals[0]);
        Level(circuit, ground, adc.InputPlus, 3.0);
        // /WR pulses low then high at 20 µs; /RD goes low at 300 µs.
        var bus = circuit.Add(new PatternGenerator { Pattern = "1011111111111111\n1111111111111110", StepRate = 50e3, Repeats = false, StartDelay = 0 });
        circuit.Connect(bus.Terminals[0], adc.Write);
        circuit.Connect(bus.Terminals[1], adc.Read);
        for (var i = 0; i < 8; i++) Wire(circuit, adc.Data[i], ground.Terminals[0], 100e3);
        var simulator = Simulate(circuit, 250e-6, 1e-7, 2e-6);
        Assert.Equal(154, adc.Result);           // 256 · 3 / 5
        Assert.True(simulator.NodeVoltage(adc.InterruptOut) < 1);
        Assert.True(simulator.NodeVoltage(adc.Data[0]) < 1, "three-state before /RD");
        simulator.Run(80e-6);
        var value = Enumerable.Range(0, 8).Sum(i => simulator.NodeVoltage(adc.Data[i]) > 2.5 ? 1 << i : 0);
        Assert.Equal(154, value);
        Assert.True(simulator.NodeVoltage(adc.InterruptOut) > 2.5);
    }

    [Fact]
    public void The_dac0808_sinks_its_reference_current_scaled_by_the_code()
    {
        var (circuit, supply, ground) = Bench(5);
        var negative = circuit.Add(new DcVoltageSource(-15));
        circuit.Connect(negative.Terminals[1], ground.Terminals[0]);
        var dac = circuit.Add(new Dac0808());
        circuit.Connect(dac.Vcc, supply.Terminals[0]);
        circuit.Connect(dac.Gnd, ground.Terminals[0]);
        circuit.Connect(dac.Vee, negative.Terminals[0]);
        var reference = Level(circuit, ground, circuit.Add(new Resistor(1)).Terminals[0], 10);
        Wire(circuit, reference.Terminals[0], dac.ReferencePlus, 5e3);       // 2 mA
        Wire(circuit, dac.ReferenceMinus, ground.Terminals[0], 5e3);
        for (var i = 0; i < 8; i++) circuit.Connect(dac.Inputs[i], (0xC0 & (0x80 >> i)) != 0 ? supply.Terminals[0] : ground.Terminals[0]);

        // An op-amp turns IOUT into a voltage: 5 kΩ of feedback from a virtual ground.
        var amp = circuit.Add(new OperationalAmplifier(OpAmpModel.Tl081));
        var plus15 = Level(circuit, ground, amp.PositiveSupply, 15);
        circuit.Connect(amp.NegativeSupply, negative.Terminals[0]);
        circuit.Connect(amp.NonInverting, ground.Terminals[0]);
        circuit.Connect(amp.Inverting, dac.Output);
        Wire(circuit, amp.Inverting, amp.Output, 5e3);
        var simulator = Simulate(circuit, 1e-5);
        Assert.Equal(0xC0, dac.Code);
        Assert.Equal(2e-3 * 192 / 256 * 5e3, simulator.NodeVoltage(amp.Output), 1);
        Assert.NotNull(plus15);
    }

    [Fact]
    public void The_bar_graph_and_the_four_digit_display_light_what_they_are_given()
    {
        var (circuit, supply, ground) = Bench(5);
        var bar = circuit.Add(new LedBarGraph { Colour = "Green" });
        for (var n = 1; n <= 10; n++)
        {
            if (n <= 3) Wire(circuit, supply.Terminals[0], bar.Anode(n), 220);
            circuit.Connect(bar.Cathode(n), ground.Terminals[0]);
        }

        // Digit 2 shows a 7 (A, B, C) held on; the others are off.
        var quad = circuit.Add(new QuadSevenSegment());
        foreach (var s in new[] { "A", "B", "C" }) Wire(circuit, supply.Terminals[0], quad.Segment(s), 220);
        circuit.Connect(quad.Digit(1), ground.Terminals[0]);
        Simulate(circuit, 60e-3, 1e-5, 1e-3);
        Assert.True(bar.Brightness.Take(3).All(b => b > 0.3) && bar.Brightness.Skip(3).All(b => b < 0.01));
        Assert.Equal(0x07, quad.Pattern(1));
        Assert.Equal(0, quad.Pattern(0));
    }

    [Fact]
    public void The_nokia_5110_takes_its_commands_and_draws_a_column()
    {
        var (circuit, supply, ground) = Bench(3.3);
        var lcd = Powered(circuit, supply, ground, new Pcd8544());
        circuit.Connect(lcd.Reset, supply.Terminals[0]);
        var master = circuit.Add(new SpiMaster { Transactions = "21 BF 04 14 20 0C 42 85; wait 30us; FF 81 3C", StartDelay = 10e-6 });
        circuit.Connect(master.Vcc, supply.Terminals[0]);
        circuit.Connect(master.Gnd, ground.Terminals[0]);
        circuit.Connect(master.ChipSelect, lcd.ChipEnable);
        circuit.Connect(master.Clock, lcd.Clock);
        circuit.Connect(master.MasterOut, lcd.DataIn);
        var dc = circuit.Add(new PatternGenerator { Pattern = "0111111111", StepRate = 10e3, Repeats = false });
        circuit.Connect(dc.Terminals[0], lcd.DataCommand);
        Simulate(circuit, 0.3e-3, 1e-7, 1e-6);
        Assert.False(lcd.PoweredDown);
        Assert.Equal(2, lcd.DisplayMode);
        Assert.Equal(0x3F, lcd.Contrast);
        Assert.Equal(0xFF, lcd.Ram[(2 * 84) + 5]);
        Assert.Equal(0x81, lcd.Ram[(2 * 84) + 6]);
        Assert.True(lcd.Pixel(5, 16) && lcd.Pixel(6, 16) && !lcd.Pixel(6, 17));
    }

    [Fact]
    public void A_ujt_and_a_put_make_relaxation_oscillators()
    {
        var (circuit, supply, ground) = Bench(12);
        var ujt = circuit.Add(new Unijunction());
        Wire(circuit, supply.Terminals[0], ujt.Base2, 470);
        var b1 = Wire(circuit, ujt.Base1, ground.Terminals[0], 47);
        Wire(circuit, supply.Terminals[0], ujt.Emitter, 47e3);
        var c = circuit.Add(new Capacitor(100e-9));
        circuit.Connect(ujt.Emitter, c.Terminals[0]);
        circuit.Connect(c.Terminals[1], ground.Terminals[0]);
        var simulator = Simulate(circuit, 0, 1e-6, 2e-5);
        var pulses = Edges(simulator, ujt.Base1, 40e-3, 1.5).Where(e => e.High).Select(e => e.T).ToList();
        Assert.True(pulses.Count >= 4, $"{pulses.Count} pulses");
        var period = (pulses[^1] - pulses[1]) / (pulses.Count - 2);
        var expected = 47e3 * 100e-9 * Math.Log(1 / (1 - 0.65));
        Assert.InRange(period, expected * 0.6, expected * 1.4);
        Assert.NotNull(b1);

        // The PUT, its gate at half of 12 V through two 10 kΩ.
        var (c2, s2, g2) = Bench(12);
        var put = c2.Add(new ProgrammableUnijunction());
        Wire(c2, s2.Terminals[0], put.Gate, 10e3);
        Wire(c2, put.Gate, g2.Terminals[0], 10e3);
        Wire(c2, s2.Terminals[0], put.Anode, 470e3);       // little enough current that it drops below the valley and lets go
        var cap = c2.Add(new Capacitor(22e-9));
        c2.Connect(put.Anode, cap.Terminals[0]);
        c2.Connect(cap.Terminals[1], g2.Terminals[0]);
        Wire(c2, put.Cathode, g2.Terminals[0], 47);
        var sim2 = Simulate(c2, 0, 1e-6, 2e-5);
        var firings = Edges(sim2, put.Cathode, 60e-3, 1.0).Where(e => e.High).Select(e => e.T).ToList();
        Assert.True(firings.Count >= 3, $"{firings.Count} firings");
        var peak = 6.6;
        var putPeriod = 470e3 * 22e-9 * Math.Log((12 - 1) / (12 - peak));
        Assert.InRange((firings[^1] - firings[1]) / (firings.Count - 2), putPeriod * 0.6, putPeriod * 1.5);
    }

    [Fact]
    public void The_el84_sits_at_its_datasheet_point_and_the_5y3_drops_sixty_volts()
    {
        var (circuit, supply, ground) = Bench(250);
        var valve = circuit.Add(new Pentode());
        circuit.Connect(valve.Plate, supply.Terminals[0]);
        circuit.Connect(valve.Screen, supply.Terminals[0]);
        circuit.Connect(valve.Cathode, ground.Terminals[0]);
        Level(circuit, ground, valve.Grid, -7.3);
        Simulate(circuit, 1e-6);
        Assert.InRange(valve.PlateCurrent, 45e-3, 51e-3);
        Assert.InRange(valve.ScreenCurrent, 4.5e-3, 6.5e-3);

        var (c2, s2, g2) = Bench(300);
        var rect = c2.Add(new ValveRectifier());
        c2.Connect(rect.Plate1, s2.Terminals[0]);
        c2.Connect(rect.Plate2, s2.Terminals[0]);
        var load = Wire(c2, rect.Cathode, g2.Terminals[0], 1920);
        var sim2 = Simulate(c2, 0);
        var drop = 300 - sim2.NodeVoltage(rect.Cathode);
        Assert.InRange(drop, 25, 45);       // two plates sharing 125 mA: about 39 V each at 62 mA
        Assert.NotNull(load);
        Assert.Equal("12AU7", new Triode { Mu = 21.5 }.ValueLabel);
    }

    [Fact]
    public void The_sn76489_plays_a_tone_its_bytes_ask_for()
    {
        var (circuit, supply, ground) = Bench(5);
        var chip = Powered(circuit, supply, ground, new Sn76489 { ClockFrequency = 4e6 });
        // Tone 0 = 0x11C (284): latch 1000 1100, then data 0001 0001; then attenuation 0: 1001 0000.
        byte[] bytes = [0x8C, 0x11, 0x90];
        var lines = Enumerable.Range(0, 9).Select(_ => new System.Text.StringBuilder()).ToArray();
        foreach (var b in bytes)
            foreach (var we in new[] { '1', '0', '0', '1' })
            {
                for (var i = 0; i < 8; i++) lines[i].Append(((b >> (7 - i)) & 1) != 0 ? '1' : '0');   // D0 is the top bit
                lines[8].Append(we);
            }

        var bus = circuit.Add(new PatternGenerator(9) { Pattern = string.Join("\n", lines.Select(l => l.ToString())), StepRate = 200e3, Repeats = false });
        for (var i = 0; i < 8; i++) circuit.Connect(bus.Terminals[i], chip.Data[i]);
        circuit.Connect(bus.Terminals[8], chip.WriteEnable);
        circuit.Connect(chip.ChipEnable, ground.Terminals[0]);
        Wire(circuit, chip.Audio, ground.Terminals[0], 10e3);
        var simulator = Simulate(circuit, 1e-4, 1e-6, 2e-5);
        Assert.Equal(284, chip.TonePeriod(0));
        Assert.Equal(0, chip.Attenuation(0));
        var rises = Edges(simulator, chip.Audio, 20e-3, 0.4).Where(e => e.High).Select(e => e.T).ToList();
        var measured = (rises.Count - 2) / (rises[^1] - rises[1]);
        Assert.Equal(4e6 / (32 * 284), measured, 0);
    }

    [Fact]
    public void The_msgeq7_puts_a_1_khz_tone_in_its_fourth_band()
    {
        var (circuit, supply, ground) = Bench(5);
        var eq = circuit.Add(new Msgeq7());
        circuit.Connect(eq.Vdd, supply.Terminals[0]);
        circuit.Connect(eq.Vss, ground.Terminals[0]);
        var tone = circuit.Add(new FunctionGenerator(Waveform.Sine, 1e3, 0.2));
        circuit.Connect(tone.Return, ground.Terminals[0]);
        var c = circuit.Add(new Capacitor(1e-6));
        circuit.Connect(tone.Output, c.Terminals[0]);
        circuit.Connect(c.Terminals[1], eq.Input);
        Simulate(circuit, 30e-3, 2e-6, 4e-6);
        var peaks = Enumerable.Range(0, 7).Select(eq.Peak).ToList();
        Assert.Equal(3, peaks.IndexOf(peaks.Max()));
        Assert.InRange(peaks[3], 0.08, 0.12);     // the generator's 0.2 is peak to peak
        Assert.True(peaks[0] < peaks[3] / 4 && peaks[6] < peaks[3] / 4);
    }

    [Fact]
    public void The_icl7107_reads_its_input_against_its_reference()
    {
        var (circuit, supply, ground) = Bench(5);
        var negative = circuit.Add(new DcVoltageSource(-5));
        circuit.Connect(negative.Terminals[1], ground.Terminals[0]);
        var meter = circuit.Add(new Icl7107());
        circuit.Connect(meter.Pin("V+"), supply.Terminals[0]);
        circuit.Connect(meter.Pin("V-"), negative.Terminals[0]);
        circuit.Connect(meter.Pin("GND"), ground.Terminals[0]);
        circuit.Connect(meter.Pin("REF LO"), ground.Terminals[0]);
        circuit.Connect(meter.Pin("IN LO"), ground.Terminals[0]);
        Level(circuit, ground, meter.Pin("REF HI"), 1.0);
        Level(circuit, ground, meter.Pin("IN HI"), -1.234);
        var units = circuit.Add(new SevenSegmentDisplay(commonAnode: true));
        Simulate(circuit, 1e-3);
        Assert.Equal(-1234, meter.Reading);
        Assert.Equal("ICL7107 -1234", meter.ValueLabel);
        Assert.NotNull(units);
    }

    [Fact]
    public void The_6532_counts_its_timer_down_and_keeps_its_ram()
    {
        foreach (var (rs, address, value) in new[] { (true, 0x15, 0x10), (false, 0x2A, 0x5A) })
        {
            var (circuit, supply, ground) = Bench(5);
            var riot = Powered(circuit, supply, ground, new Riot6532());
            var clock = circuit.Add(new ClockSource(1e6));
            circuit.Connect(clock.Out, riot.Clock);
            circuit.Connect(riot.Reset, supply.Terminals[0]);
            circuit.Connect(riot.ReadWrite, ground.Terminals[0]);
            circuit.Connect(riot.ChipSelect2, ground.Terminals[0]);
            circuit.Connect(riot.RamSelect, rs ? supply.Terminals[0] : ground.Terminals[0]);
            for (var i = 0; i < 7; i++) circuit.Connect(riot.Address[i], (address & (1 << i)) != 0 ? supply.Terminals[0] : ground.Terminals[0]);
            for (var i = 0; i < 8; i++) circuit.Connect(riot.Data[i], (value & (1 << i)) != 0 ? supply.Terminals[0] : ground.Terminals[0]);
            var select = circuit.Add(new PatternGenerator { Pattern = "100000000", StepRate = 1e6, Repeats = false, StartDelay = 0.25e-6 });
            circuit.Connect(select.Terminals[0], riot.ChipSelect1);
            Simulate(circuit, 41.6e-6, 2e-8, 1e-7);
            if (rs)
                Assert.Equal(0x10 - 5, riot.Timer);   // written at 0.5 µs, then 40 more cycles at one count in eight
            else
                Assert.Equal(0x5A, riot.Ram[0x2A]);
        }
    }

    [Fact]
    public void The_2114_writes_a_nibble_and_reads_it_back()
    {
        var (circuit, supply, ground) = Bench(5);
        var ram = Powered(circuit, supply, ground, new Sram2114());
        foreach (var a in ram.Address) circuit.Connect(a, ground.Terminals[0]);
        circuit.Connect(ram.ChipSelect, ground.Terminals[0]);
        // IO1-IO4 drive 0xA while /WE is low, then let go.
        var bus = circuit.Add(new PatternGenerator(5) { Pattern = "0zzz\n1zzz\n0zzz\n1zzz\n0111", StepRate = 100e3, Repeats = false });
        for (var i = 0; i < 4; i++)
        {
            circuit.Connect(bus.Terminals[i], ram.Data[i]);
            Wire(circuit, ram.Data[i], ground.Terminals[0], 100e3);
        }

        circuit.Connect(bus.Terminals[4], ram.WriteEnable);
        var simulator = Simulate(circuit, 30e-6, 1e-7, 1e-6);
        Assert.Equal(0xA, ram.Read(0));
        Assert.Equal([false, true, false, true], ram.Data.Select(d => simulator.NodeVoltage(d) > 2.5));
    }

    [Fact]
    public void The_4116_forgets_a_row_left_unrefreshed()
    {
        var (circuit, supply, ground) = Bench(5);
        var dram = Powered(circuit, supply, ground, new Dram4116());
        Level(circuit, ground, dram.Vdd, 12);
        Level(circuit, ground, dram.Vbb, -5);
        foreach (var a in dram.Address) circuit.Connect(a, ground.Terminals[0]);
        circuit.Connect(dram.DataIn, supply.Terminals[0]);
        Wire(circuit, dram.DataOut, ground.Terminals[0], 100e3);
        // Write a 1 at 0: RAS, CAS, WE. Read at 1 ms (DOUT high), and again at 4 ms, unrefreshed (DOUT low).
        string Cycle(bool write) => write ? "0011" : "0011";
        var ras = "1" + "00" + "1" + new string('1', 96) + "00" + "1" + new string('1', 296) + "00" + "11";
        var cas = "1" + "10" + "1" + new string('1', 96) + "10" + "1" + new string('1', 296) + "10" + "11";
        var we = "1" + "00" + "1" + new string('1', 96) + "11" + "1" + new string('1', 296) + "11" + "11";
        var bus = circuit.Add(new PatternGenerator(3) { Pattern = $"{ras}\n{cas}\n{we}", StepRate = 1e5, Repeats = false });
        circuit.Connect(bus.Terminals[0], dram.RowStrobe);
        circuit.Connect(bus.Terminals[1], dram.ColumnStrobe);
        circuit.Connect(bus.Terminals[2], dram.WriteEnable);
        _ = Cycle(true);
        var simulator = Simulate(circuit, 1.015e-3, 1e-6, 5e-6);
        Assert.True(simulator.NodeVoltage(dram.DataOut) > 2.5, "read straight after the write");
        simulator.Run(2.99e-3);
        Assert.True(simulator.NodeVoltage(dram.DataOut) < 1, "read after 3 ms with no refresh");
    }

    [Fact]
    public void The_til311_shows_its_latched_hex_digit()
    {
        var (circuit, supply, ground) = Bench(5);
        var hex = Powered(circuit, supply, ground, new Til311());
        circuit.Connect(hex.LedSupply, supply.Terminals[0]);
        for (var i = 0; i < 4; i++) circuit.Connect(hex.Data[i], (0xB & (1 << i)) != 0 ? supply.Terminals[0] : ground.Terminals[0]);
        circuit.Connect(hex.Strobe, ground.Terminals[0]);
        circuit.Connect(hex.Blank, ground.Terminals[0]);
        Simulate(circuit, 1e-6);
        Assert.Equal(0xB, hex.Shown);
        Assert.Equal("TIL311 B", hex.ValueLabel);
    }

    [Fact]
    public void The_mc14500b_runs_its_instructions_on_the_clock()
    {
        var (circuit, supply, ground) = Bench(5);
        var icu = Powered(circuit, supply, ground, new Mc14500());
        circuit.Connect(icu.Reset, ground.Terminals[0]);
        Wire(circuit, supply.Terminals[0], icu.Data, 10e3);
        // IEN, OEN, LD (data high: RR = 1), STOC (drives DATA low, WRITE high), NOPF.
        int[] program = [0xA, 0xB, 0x1, 0x9, 0xF];
        var clockLine = new System.Text.StringBuilder("1");
        var bits = Enumerable.Range(0, 4).Select(_ => new System.Text.StringBuilder("0")).ToArray();
        foreach (var op in program)
            foreach (var clk in new[] { '1', '0' })
            {
                clockLine.Append(clk);
                for (var i = 0; i < 4; i++) bits[i].Append(((op >> i) & 1) != 0 ? '1' : '0');
            }

        // Each 1 released ('z') to a pull-up, so it reaches 5 V: the generator's TTL high is short of CMOS's 3.5 V.
        var pattern = string.Join("\n", [clockLine.ToString(), .. bits.Select(b => b.ToString())]).Replace('1', 'z');
        var bus = circuit.Add(new PatternGenerator(5) { Pattern = pattern, StepRate = 10e3, Repeats = false });
        circuit.Connect(bus.Terminals[0], icu.Clock);
        for (var i = 0; i < 4; i++) circuit.Connect(bus.Terminals[1 + i], icu.Instruction[i]);
        for (var i = 0; i < 5; i++) Wire(circuit, supply.Terminals[0], bus.Terminals[i], 4.7e3);
        var simulator = Simulate(circuit, 0.85e-3, 1e-6, 5e-6);    // in STOC's low half
        Assert.True(icu.ResultRegister && icu.InputEnable && icu.OutputEnable, $"{icu.ValueLabel} clk {simulator.NodeVoltage(icu.Clock)} i0 {simulator.NodeVoltage(icu.Instruction[0])} data {simulator.NodeVoltage(icu.Data)}");
        Assert.True(simulator.NodeVoltage(icu.Data) < 1, "STOC drives the complement of RR");
        Assert.True(simulator.NodeVoltage(icu.Write) > 2.5);
        simulator.Run(0.2e-3);
        Assert.True(simulator.NodeVoltage(icu.FlagF) > 2.5);
    }

    [Fact]
    public void A_gal16v8_runs_its_equations_combinational_and_registered()
    {
        var (circuit, supply, ground) = Bench(5);
        var gal = Powered(circuit, supply, ground, new Gal16v8());
        Assert.Null(gal.EquationError);
        circuit.Connect(gal.Terminals[1], supply.Terminals[0]);   // A (pin 2) high
        circuit.Connect(gal.Terminals[2], ground.Terminals[0]);   // B (pin 3) low
        var simulator = Simulate(circuit, 1e-6);
        Assert.True(simulator.NodeVoltage(gal.Terminals[18]) < 1);        // AND on 19
        Assert.True(simulator.NodeVoltage(gal.Terminals[17]) > 2.5);      // XOR on 18

        var (c2, s2, g2) = Bench(5);
        var counter = Powered(c2, s2, g2, new Gal16v8 { Equations = "pin 19 = Q0\npin 18 = Q1\nQ0.R = !Q0\nQ1.R = Q1 ^ Q0" });
        Assert.True(counter.RegisteredMode);
        var clock = c2.Add(new ClockSource(10e3));
        c2.Connect(clock.Out, counter.Terminals[0]);
        c2.Connect(counter.Terminals[10], g2.Terminals[0]);     // /OE low
        foreach (var p in new[] { 1, 2, 3, 4, 5, 6, 7, 8 }) c2.Connect(counter.Terminals[p], g2.Terminals[0]);
        Simulate(c2, 0.32e-3, 1e-7, 1e-5);
        Assert.Equal(3 % 4, (counter.Register(19) ? 1 : 0) | (counter.Register(18) ? 2 : 0));
        Assert.Contains("product terms", new Gal16v8 { Equations = "pin 19 = Y\nY = P2|P3|P4|P5|P6|P7|P8|P9|P11" }.EquationError, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ne602_mixes_its_signal_with_its_oscillator()
    {
        var (circuit, supply, ground) = Bench(6);
        var mixer = circuit.Add(new Ne602());
        circuit.Connect(mixer.Vcc, supply.Terminals[0]);
        circuit.Connect(mixer.Gnd, ground.Terminals[0]);
        var rf = circuit.Add(new FunctionGenerator(Waveform.Sine, 110e3, 0.01));
        circuit.Connect(rf.Return, ground.Terminals[0]);
        var c1 = circuit.Add(new Capacitor(100e-9));
        circuit.Connect(rf.Output, c1.Terminals[0]);
        circuit.Connect(c1.Terminals[1], mixer.InputA);
        var c2 = circuit.Add(new Capacitor(100e-9));
        circuit.Connect(mixer.InputB, c2.Terminals[0]);
        circuit.Connect(c2.Terminals[1], ground.Terminals[0]);
        var lo = circuit.Add(new FunctionGenerator(Waveform.Sine, 100e3, 0.3));
        circuit.Connect(lo.Return, ground.Terminals[0]);
        var c3 = circuit.Add(new Capacitor(10e-9));
        circuit.Connect(lo.Output, c3.Terminals[0]);
        circuit.Connect(c3.Terminals[1], mixer.OscillatorBase);
        var simulator = Simulate(circuit, 2e-3, 0.2e-6, 0.2e-6, adaptive: false);

        // The 10 kHz difference, found by correlating the differential output over a millisecond.
        double re = 0, im = 0;
        var n = 0;
        while (simulator.Time < 3e-3)
        {
            simulator.Step();
            var v = simulator.NodeVoltage(mixer.OutputA) - simulator.NodeVoltage(mixer.OutputB);
            re += v * Math.Cos(2 * Math.PI * 10e3 * simulator.Time);
            im += v * Math.Sin(2 * Math.PI * 10e3 * simulator.Time);
            n++;
        }

        var amplitude = 2 * Math.Sqrt((re * re) + (im * im)) / n;
        var expected = mixer.Gain * 0.005 * (2 / Math.PI);    // the generator's 0.01 is peak to peak
        Assert.InRange(amplitude, expected * 0.5, expected * 1.3);
        Assert.InRange(simulator.NodeVoltage(mixer.OutputA), 3.5, 5);
    }

    [Fact]
    public void The_mc1496s_output_current_is_the_signal_over_re_times_the_carriers_sign()
    {
        foreach (var carrier in new[] { 0.3, -0.3 })
        {
            var (circuit, supply, ground) = Bench(12);
            var negative = circuit.Add(new DcVoltageSource(-8));
            circuit.Connect(negative.Terminals[1], ground.Terminals[0]);
            var mod = circuit.Add(new Mc1496());
            circuit.Connect(mod.NegativeSupply, negative.Terminals[0]);
            Wire(circuit, ground.Terminals[0], mod.Bias, 6.8e3);       // about 1 mA
            Level(circuit, ground, mod.SignalPlus, 0.1);
            circuit.Connect(mod.SignalMinus, ground.Terminals[0]);
            Wire(circuit, mod.GainA, mod.GainB, 1e3);
            Level(circuit, ground, mod.CarrierPlus, carrier);
            circuit.Connect(mod.CarrierMinus, ground.Terminals[0]);
            Wire(circuit, supply.Terminals[0], mod.OutputPlus, 3.9e3);
            Wire(circuit, supply.Terminals[0], mod.OutputMinus, 3.9e3);
            var simulator = Simulate(circuit, 1e-5);
            var difference = (simulator.NodeVoltage(mod.OutputMinus) - simulator.NodeVoltage(mod.OutputPlus)) / 3.9e3;
            Assert.Equal(Math.Sign(carrier) * 2 * 0.1 / 1e3, difference, 5);   // 2·Vs / RE
        }
    }
}
