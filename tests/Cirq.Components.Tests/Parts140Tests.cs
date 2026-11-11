using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Electromechanical;
using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>The parts of 1.4.0, each held to its datasheet.</summary>
public class Parts140Tests
{
    internal static CircuitSimulator Simulate(Circuit circuit, double seconds, double step = 1e-6, double max = 1e-4, bool adaptive = true)
    {
        var simulator = new CircuitSimulator(circuit, new Cirq.Core.Simulation.SimulationSettings { TimeStep = step, MaxTimeStep = max, AdaptiveTimeStep = adaptive });
        simulator.Reset();
        simulator.SolveOperatingPoint();
        if (seconds > 0) simulator.Run(seconds);
        return simulator;
    }

    internal static (Circuit Circuit, DcVoltageSource Supply, Ground Ground) Bench(double volts)
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(volts));
        var ground = circuit.Add(new Ground());
        circuit.Connect(supply.Terminals[1], ground.Terminals[0]);
        return (circuit, supply, ground);
    }

    internal static Resistor Wire(Circuit circuit, Terminal a, Terminal b, double ohms)
    {
        var r = circuit.Add(new Resistor(ohms));
        circuit.Connect(a, r.Terminals[0]);
        circuit.Connect(r.Terminals[1], b);
        return r;
    }

    internal static DcVoltageSource Level(Circuit circuit, Ground ground, Terminal pin, double volts)
    {
        var s = circuit.Add(new DcVoltageSource(volts));
        circuit.Connect(s.Terminals[1], ground.Terminals[0]);
        circuit.Connect(s.Terminals[0], pin);
        return s;
    }

    /// <summary>Steps on, recording each time a node crosses half the given level.</summary>
    internal static List<(double T, bool High)> Edges(CircuitSimulator simulator, Terminal node, double until, double threshold)
    {
        var edges = new List<(double, bool)>();
        var was = simulator.NodeVoltage(node) > threshold;
        while (simulator.Time < until)
        {
            simulator.Step();
            var high = simulator.NodeVoltage(node) > threshold;
            if (high != was) edges.Add((simulator.Time, high));
            was = high;
        }

        return edges;
    }

    [Fact]
    public void The_new_models_are_in_the_libraries()
    {
        foreach (var name in new[] { "BC337", "BC327", "2N5551", "2N5401", "TIP42C", "AC128", "OC71" })
            Assert.Contains(BjtModel.Library, m => m.Name == name);
        Assert.Equal(BjtPolarity.Pnp, BjtModel.Tip42C.Polarity);
        Assert.Contains(MosfetModel.Library, m => m.Name == "IRF4905" && m.Channel == MosfetChannel.PChannel);
        Assert.Contains(DiodeModel.Library, m => m.Name == "1N34A");
        foreach (var name in new[] { "TL072", "TL074", "NE5532", "OP07" }) Assert.Contains(OpAmpModel.Library, m => m.Name == name);
    }

    [Fact]
    public void A_germanium_junction_turns_on_at_a_third_of_silicons_voltage()
    {
        foreach (var (model, low, high) in new[] { (DiodeModel.D1N34A, 0.15, 0.4), (DiodeModel.D1N4148, 0.55, 0.75) })
        {
            var (circuit, supply, ground) = Bench(5);
            var diode = circuit.Add(new Diode(model));
            var r = Wire(circuit, supply.Terminals[0], diode.Terminals[0], 4.3e3);
            circuit.Connect(diode.Terminals[1], ground.Terminals[0]);
            var simulator = Simulate(circuit, 0);
            Assert.InRange(simulator.NodeVoltage(diode.Terminals[0]), low, high);
            Assert.NotNull(r);
        }

        // An AC128's base turns on near 0.2 V.
        var (c2, s2, g2) = Bench(-9);
        var ge = c2.Add(new BipolarTransistor(BjtModel.Ac128));
        circuit2(c2, s2, g2, ge);
        var sim2 = Simulate(c2, 0);
        Assert.InRange(sim2.NodeVoltage(ge.Emitter) - sim2.NodeVoltage(ge.Base), 0.1, 0.3);
    }

    private static void circuit2(Circuit c, DcVoltageSource s, Ground g, BipolarTransistor t)
    {
        c.Connect(t.Emitter, g.Terminals[0]);
        Wire(c, s.Terminals[0], t.Base, 100e3);
        Wire(c, s.Terminals[0], t.Collector, 1e3);
    }

    [Fact]
    public void A_dual_op_amp_runs_two_followers_and_a_quad_takes_the_tl074()
    {
        var (circuit, supply, ground) = Bench(12);
        var negative = circuit.Add(new DcVoltageSource(-12));
        circuit.Connect(negative.Terminals[1], ground.Terminals[0]);
        var dual = circuit.Add(new DualOpAmp(OpAmpModel.Ne5532));
        circuit.Connect(dual.PositiveSupply, supply.Terminals[0]);
        circuit.Connect(dual.NegativeSupply, negative.Terminals[0]);
        for (var i = 0; i < 2; i++)
        {
            var (plus, minus, output) = dual.Channel(i);
            circuit.Connect(minus, output);
            Level(circuit, ground, plus, i == 0 ? 1.5 : -2.5);
            Wire(circuit, output, ground.Terminals[0], 600);
        }

        var simulator = Simulate(circuit, 0);
        Assert.Equal(1.5, simulator.NodeVoltage(dual.Channel(0).Output), 2);
        Assert.Equal(-2.5, simulator.NodeVoltage(dual.Channel(1).Output), 2);
        Assert.Equal("dual NE5532", dual.ValueLabel);
        Assert.Equal("TL074", new QuadOpAmp(OpAmpModel.Tl074).Model.Name);
    }

    [Fact]
    public void The_lm13700s_output_current_is_its_bias_times_the_input_over_2vt()
    {
        var (circuit, supply, ground) = Bench(15);
        var negative = circuit.Add(new DcVoltageSource(-15));
        circuit.Connect(negative.Terminals[1], ground.Terminals[0]);
        var ota = circuit.Add(new Lm13700());
        circuit.Connect(ota.PositiveSupply, supply.Terminals[0]);
        circuit.Connect(ota.NegativeSupply, negative.Terminals[0]);
        var h = ota.Half(0);
        // 100 µA of bias: (15 − (−15 + 1.2)) / 288 kΩ.
        Wire(circuit, supply.Terminals[0], h.AmpBias, 288e3);
        Level(circuit, ground, h.NonInverting, 5e-3);
        circuit.Connect(h.Inverting, ground.Terminals[0]);
        Wire(circuit, h.Output, ground.Terminals[0], 1e3);
        // The buffer: BUF IN on OUT, 10 kΩ to V−.
        circuit.Connect(h.BufferIn, h.Output);
        Wire(circuit, h.BufferOut, negative.Terminals[0], 10e3);
        var simulator = Simulate(circuit, 1e-5);

        Assert.Equal(100e-6, ota.AmplifierBias(0), 6);
        var expected = 100e-6 * Math.Tanh(5e-3 / (2 * 0.025852)) * 1e3;
        Assert.InRange(simulator.NodeVoltage(h.Output), expected - 0.3e-3, expected + 0.05e-3);   // less the buffer's input current
        Assert.InRange(simulator.NodeVoltage(h.Output) - simulator.NodeVoltage(h.BufferOut), 1.4, 1.5);   // 1.4 V and its 50 Ω

        // Large signal: it saturates at the bias current itself.
        circuit.Components.OfType<DcVoltageSource>().Single(s => s.Voltage == 5e-3).Voltage = 0.5;
        simulator = Simulate(circuit, 1e-5);
        Assert.Equal(0.1, simulator.NodeVoltage(h.Output), 3);
    }

    [Fact]
    public void A_40106_gate_oscillates_with_a_resistor_and_a_capacitor()
    {
        var (circuit, supply, ground) = Bench(5);
        var chip = circuit.Add(new Ic40106());
        circuit.Connect(chip.Vcc, supply.Terminals[0]);
        circuit.Connect(chip.Gnd, ground.Terminals[0]);
        var (a, y) = chip.Inverter(0);
        Wire(circuit, y, a, 10e3);
        var c = circuit.Add(new Capacitor(100e-9));
        circuit.Connect(a, c.Terminals[0]);
        circuit.Connect(c.Terminals[1], ground.Terminals[0]);
        var simulator = Simulate(circuit, 0, 1e-6, 1e-5);
        var rises = Edges(simulator, y, 10e-3, 2.5).Where(e => e.High).ToList();
        Assert.True(rises.Count >= 3, $"{rises.Count} rises");
        var period = (rises[^1].T - rises[0].T) / (rises.Count - 1);
        Assert.InRange(period, 0.4e-3, 2.5e-3);
        Assert.Equal("40106", chip.PartNumber);
    }

    private static (Circuit Circuit, DcVoltageSource Supply, Ground Ground, T Chip) Chip<T>(T chip) where T : DigitalIc
    {
        var (circuit, supply, ground) = Bench(5);
        circuit.Add(chip);
        circuit.Connect(chip.Vcc, supply.Terminals[0]);
        circuit.Connect(chip.Gnd, ground.Terminals[0]);
        return (circuit, supply, ground, chip);
    }

    private static void Tie(Circuit circuit, DcVoltageSource supply, Ground ground, Terminal pin, bool high) =>
        circuit.Connect(pin, high ? supply.Terminals[0] : ground.Terminals[0]);

    [Fact]
    public void The_74148_encodes_the_highest_input_held_low()
    {
        var (circuit, supply, ground, enc) = Chip(new Ic74148());
        for (var i = 0; i < 8; i++) Tie(circuit, supply, ground, enc.Pin(i.ToString(System.Globalization.CultureInfo.InvariantCulture)), i is not (2 or 5));
        Tie(circuit, supply, ground, enc.Pin("EI"), false);
        var simulator = Simulate(circuit, 1e-6);
        bool V(string pin) => simulator.NodeVoltage(enc.Pin(pin)) > 2.5;
        // 5 is the highest held low: A = ~5 = 010.
        Assert.Equal([false, true, false], new[] { V("A0"), V("A1"), V("A2") });
        Assert.False(V("GS"));
        Assert.True(V("EO"));
    }

    [Fact]
    public void The_74688_matches_its_two_bytes_and_the_74259_latches_an_addressed_bit()
    {
        var (circuit, supply, ground, cmp) = Chip(new Ic74688());
        for (var i = 0; i < 8; i++)
        {
            Tie(circuit, supply, ground, cmp.Pin($"P{i}"), (0xA5 & (1 << i)) != 0);
            Tie(circuit, supply, ground, cmp.Pin($"Q{i}"), (0xA5 & (1 << i)) != 0);
        }

        Tie(circuit, supply, ground, cmp.Pin("G"), false);
        Assert.True(Simulate(circuit, 1e-6).NodeVoltage(cmp.Pin("P=Q")) < 1);

        var (c2, s2, g2, latch) = Chip(new Ic74259());
        Tie(c2, s2, g2, latch.Pin("S0"), true);
        Tie(c2, s2, g2, latch.Pin("S1"), true);
        Tie(c2, s2, g2, latch.Pin("S2"), false);      // address 3
        Tie(c2, s2, g2, latch.Pin("D"), true);
        Tie(c2, s2, g2, latch.Pin("CLR"), true);
        Tie(c2, s2, g2, latch.Pin("E"), false);
        var sim2 = Simulate(c2, 1e-6);
        Assert.Equal(0x08, latch.Latched);
        Assert.True(sim2.NodeVoltage(latch.Pin("Q3")) > 2.5);
    }

    [Fact]
    public void The_74191_counts_down_on_one_clock_and_the_74279_latches_a_switch()
    {
        var (circuit, supply, ground, counter) = Chip(new Ic74191());
        var clock = circuit.Add(new ClockSource(10e3));
        circuit.Connect(clock.Out, counter.Pin("CLK"));
        foreach (var pin in new[] { "A", "B", "C", "D" }) Tie(circuit, supply, ground, counter.Pin(pin), pin == "B");
        Tie(circuit, supply, ground, counter.Pin("CTEN"), false);
        Tie(circuit, supply, ground, counter.Pin("D/U"), true);
        var load = circuit.Add(new PatternGenerator { Pattern = "0111111111", StepRate = 1e5, Repeats = false });
        circuit.Connect(load.Terminals[0], counter.Pin("LOAD"));
        Simulate(circuit, 0.35e-3, 1e-7, 1e-5);
        // Loaded 2, then down three times (rises at 0.1, 0.2 and 0.3 ms): 15.
        Assert.Equal(15, counter.Count);

        var (c2, s2, g2, sr) = Chip(new Ic74279());
        Tie(c2, s2, g2, sr.Pin("2S"), false);
        Tie(c2, s2, g2, sr.Pin("2R"), true);
        Tie(c2, s2, g2, sr.Pin("4S"), true);
        Tie(c2, s2, g2, sr.Pin("4R"), false);
        Simulate(c2, 1e-6);
        Assert.True(sr.Q(1));
        Assert.False(sr.Q(3));
    }

    [Fact]
    public void A_uln2803_sinks_all_eight()
    {
        var (circuit, supply, ground) = Bench(12);
        var logic = Level(circuit, ground, circuit.Add(new Resistor(1)).Terminals[0], 5);
        var chip = circuit.Add(new Uln2803());
        circuit.Connect(chip.Gnd, ground.Terminals[0]);
        circuit.Connect(chip.Common, supply.Terminals[0]);
        for (var i = 0; i < 8; i++)
        {
            circuit.Connect(chip.Inputs[i], i % 2 == 0 ? logic.Terminals[0] : ground.Terminals[0]);
            Wire(circuit, supply.Terminals[0], chip.Outputs[i], 120);
        }

        var simulator = Simulate(circuit, 1e-4);
        Assert.Equal(8, chip.Inputs.Count);
        Assert.Equal("ULN2803", chip.PartNumber);
        for (var i = 0; i < 8; i++) Assert.Equal(i % 2 == 0, simulator.NodeVoltage(chip.Outputs[i]) < 2);
    }

    [Fact]
    public void The_acs712_reads_185_mv_an_amp_about_half_its_supply()
    {
        var (circuit, supply, ground) = Bench(5);
        var load = Bench(12);
        var sensor = circuit.Add(new Acs712());
        circuit.Connect(sensor.Vcc, supply.Terminals[0]);
        circuit.Connect(sensor.Gnd, ground.Terminals[0]);
        var twelve = Level(circuit, ground, sensor.CurrentIn, 12);
        Wire(circuit, sensor.CurrentOut, ground.Terminals[0], 6);     // 2 A
        var simulator = Simulate(circuit, 1e-5);
        Assert.Equal(2.0, sensor.Current, 2);
        Assert.Equal(2.5 + (2 * 0.185), simulator.NodeVoltage(sensor.Output), 2);
        Assert.NotNull(load.Circuit);
        Assert.NotNull(twelve);
    }

    [Fact]
    public void The_hx711_reads_a_load_cell_through_its_clocked_serial_line()
    {
        var (circuit, supply, ground) = Bench(5);
        var amp = circuit.Add(new Hx711 { FastRate = true });
        circuit.Connect(amp.Vcc, supply.Terminals[0]);
        circuit.Connect(amp.Gnd, ground.Terminals[0]);
        var cell = circuit.Add(new LoadCell());
        circuit.Connect(cell.ExcitationPositive, amp.ExcitationPlus);
        circuit.Connect(cell.ExcitationNegative, amp.ExcitationMinus);
        circuit.Connect(cell.SignalPositive, amp.APlus);
        circuit.Connect(cell.SignalNegative, amp.AMinus);

        // 25 clocks at 50 kHz, starting once the first reading is ready (12.5 ms at 80 a second).
        var pattern = "0" + string.Concat(Enumerable.Repeat("10", 25)) + "0";
        var clock = circuit.Add(new PatternGenerator { Pattern = pattern, StepRate = 100e3, Repeats = false, StartDelay = 14e-3 });
        circuit.Connect(clock.Terminals[0], amp.Clock);

        var simulator = Simulate(circuit, 13.5e-3, 1e-6, 1e-4);
        Assert.True(simulator.NodeVoltage(amp.Data) < 1, "DOUT low: a reading is ready");
        var bits = new List<int>();
        var was = false;
        while (simulator.Time < 14.7e-3)
        {
            simulator.Step();
            var sck = simulator.NodeVoltage(amp.Clock) > 2.5;
            if (!sck && was) bits.Add(simulator.NodeVoltage(amp.Data) > 2.5 ? 1 : 0);   // read before the clock falls, as a host does
            was = sck;
        }

        Assert.True(bits.Count >= 24, $"{bits.Count} bits");
        var raw = bits.Take(24).Aggregate(0, (v, b) => (v << 1) | b);
        var value = (raw << 8) >> 8;
        Assert.Equal(amp.Reading, value);
        var vdiff = cell.SignalPositive is { } ? simulator.NodeVoltage(amp.APlus) - simulator.NodeVoltage(amp.AMinus) : 0;
        var avdd = simulator.NodeVoltage(amp.ExcitationPlus);
        Assert.InRange(value - (vdiff * 128 / (0.5 * avdd) * 8388608), -500, 500);
        Assert.Equal(4.3, avdd, 1);
    }

    [Fact]
    public void The_max6675_reports_a_thermocouples_temperature_in_quarter_degrees()
    {
        var (circuit, supply, ground) = Bench(5);
        var tc = circuit.Add(new Thermocouple { Temperature = 300, ColdJunctionTemperature = 25 });
        var chip = circuit.Add(new Max6675 { ColdJunctionTemperature = 25 });
        circuit.Connect(chip.Vcc, supply.Terminals[0]);
        circuit.Connect(chip.Gnd, ground.Terminals[0]);
        circuit.Connect(tc.Terminals[0], chip.ThermocouplePlus);
        circuit.Connect(tc.Terminals[1], chip.ThermocoupleMinus);
        circuit.Connect(chip.ThermocoupleMinus, ground.Terminals[0]);
        var master = circuit.Add(new SpiMaster { Transactions = "00 00" });
        circuit.Connect(master.Vcc, supply.Terminals[0]);
        circuit.Connect(master.Gnd, ground.Terminals[0]);
        circuit.Connect(master.ChipSelect, chip.ChipSelect);
        circuit.Connect(master.Clock, chip.Clock);
        circuit.Connect(master.MasterIn, chip.SerialData);
        Simulate(circuit, 1e-4, 1e-7, 1e-6);
        var word = (master.ReceivedBytes[0] << 8) | master.ReceivedBytes[1];
        Assert.Equal(0, word & 0x04);
        var celsius = (word >> 3) / 4.0;
        Assert.InRange(celsius, 296, 300.5);
        Assert.Equal(celsius, chip.Temperature!.Value, 2);
    }

    [Fact]
    public void The_bme280s_compensation_is_boschs_and_its_raw_readings_come_back_as_set()
    {
        // The BMP280 datasheet's worked example.
        Assert.Equal(2508, Bme280.CompensateTemperature(519888));
        Assert.Equal(128422, Bme280.TFine(519888));
        Assert.Equal(100653.27, Bme280.CompensatePressure(415148, 128422) / 256.0, 1);

        var (circuit, supply, ground) = Bench(3.3);
        var master = circuit.Add(new I2cMaster { Transactions = "w 76 D0; r 76 1; w 76 F2 01 F4 27; w 76 F7; r 76 8", ClockFrequency = 400e3, StartDelay = 20e-6 });
        var sensor = circuit.Add(new Bme280 { Temperature = 23.5, Pressure = 98_750, Humidity = 62 });
        foreach (var d in new I2cDevice[] { master, sensor })
        {
            circuit.Connect(d.Vcc, supply.Terminals[0]);
            circuit.Connect(d.Gnd, ground.Terminals[0]);
        }

        circuit.Connect(master.Sda, sensor.Sda);
        circuit.Connect(master.Scl, sensor.Scl);
        Wire(circuit, supply.Terminals[0], master.Sda, 4.7e3);
        Wire(circuit, supply.Terminals[0], master.Scl, 4.7e3);
        Simulate(circuit, 1.5e-3, 2e-7, 2e-6);

        var r = master.ReceivedBytes;
        Assert.Equal(0x60, r[0]);
        var adcP = (r[1] << 12) | (r[2] << 4) | (r[3] >> 4);
        var adcT = (r[4] << 12) | (r[5] << 4) | (r[6] >> 4);
        var adcH = (r[7] << 8) | r[8];
        Assert.Equal(23.5, Bme280.CompensateTemperature(adcT) / 100.0, 1);
        var tFine = Bme280.TFine(adcT);
        Assert.InRange(Bme280.CompensatePressure(adcP, tFine) / 256.0, 98_745, 98_755);
        Assert.Equal(62, Bme280.CompensateHumidity(adcH, tFine) / 1024.0, 0);
        Assert.Equal(0x58, new Bme280 { Bmp280 = true }.Registers[0xD0]);
    }

    [Fact]
    public void The_pca9685_makes_a_servo_pulse_at_50_hz()
    {
        var (circuit, supply, ground) = Bench(5);
        var master = circuit.Add(new I2cMaster { Transactions = "w 40 00 10; w 40 FE 79; w 40 00 20; w 40 06 00 00 33 01", ClockFrequency = 400e3, StartDelay = 20e-6 });
        var pwm = circuit.Add(new Pca9685());
        foreach (var d in new I2cDevice[] { master, pwm })
        {
            circuit.Connect(d.Vcc, supply.Terminals[0]);
            circuit.Connect(d.Gnd, ground.Terminals[0]);
        }

        circuit.Connect(master.Sda, pwm.Sda);
        circuit.Connect(master.Scl, pwm.Scl);
        circuit.Connect(pwm.OutputEnable, ground.Terminals[0]);
        Wire(circuit, supply.Terminals[0], master.Sda, 4.7e3);
        Wire(circuit, supply.Terminals[0], master.Scl, 4.7e3);
        Wire(circuit, pwm.Outputs[0], ground.Terminals[0], 10e3);
        var simulator = Simulate(circuit, 1e-3, 2e-7, 1e-5);
        Assert.False(pwm.Asleep);
        Assert.Equal(25e6 / (4096 * 122), pwm.Frequency, 3);
        var edges = Edges(simulator, pwm.Outputs[0], 65e-3, 2.5);
        var rises = edges.Where(e => e.High).Select(e => e.T).ToList();
        var falls = edges.Where(e => !e.High).Select(e => e.T).ToList();
        Assert.True(rises.Count >= 3);
        Assert.Equal(1 / pwm.Frequency, rises[2] - rises[1], 5);
        var width = falls.First(f => f > rises[1]) - rises[1];
        Assert.Equal(307 / 4096.0 / pwm.Frequency, width, 5);
    }

    [Fact]
    public void The_mcp23017_drives_port_a_and_reads_port_b_through_its_pull_ups()
    {
        var (circuit, supply, ground) = Bench(5);
        var master = circuit.Add(new I2cMaster { Transactions = "w 20 00 00; w 20 14 A5; w 20 0D FF; w 20 13; r 20 1", ClockFrequency = 400e3, StartDelay = 20e-6 });
        var io = circuit.Add(new Mcp23017());
        foreach (var d in new I2cDevice[] { master, io })
        {
            circuit.Connect(d.Vcc, supply.Terminals[0]);
            circuit.Connect(d.Gnd, ground.Terminals[0]);
        }

        circuit.Connect(master.Sda, io.Sda);
        circuit.Connect(master.Scl, io.Scl);
        circuit.Connect(io.Reset, supply.Terminals[0]);
        Wire(circuit, supply.Terminals[0], master.Sda, 4.7e3);
        Wire(circuit, supply.Terminals[0], master.Scl, 4.7e3);
        circuit.Connect(io.PortB[3], ground.Terminals[0]);
        var simulator = Simulate(circuit, 1.5e-3, 2e-7, 2e-6);
        for (var i = 0; i < 8; i++) Assert.Equal((0xA5 & (1 << i)) != 0, simulator.NodeVoltage(io.PortA[i]) > 2.5);
        Assert.Equal(0xF7, master.ReceivedBytes[0]);
    }

    [Fact]
    public void The_tb6612_drives_brakes_and_coasts_as_its_table_says()
    {
        var (circuit, supply, ground) = Bench(12);
        var drv = circuit.Add(new Tb6612Module());
        var t = drv.Terminals;
        circuit.Connect(t[0], supply.Terminals[0]);
        circuit.Connect(t[2], ground.Terminals[0]);
        Level(circuit, ground, t[1], 5);
        Level(circuit, ground, t[10], 5);       // STBY
        Level(circuit, ground, t[7], 5);        // PWMA
        Level(circuit, ground, t[9], 5);        // AIN1
        circuit.Connect(t[8], ground.Terminals[0]);      // AIN2
        Level(circuit, ground, t[13], 0);       // PWMB low: brake
        Level(circuit, ground, t[11], 5);
        circuit.Connect(t[12], ground.Terminals[0]);
        Wire(circuit, t[3], t[4], 24);
        Wire(circuit, t[5], t[6], 24);
        var simulator = Simulate(circuit, 0);
        Assert.InRange(simulator.NodeVoltage(t[3]) - simulator.NodeVoltage(t[4]), 11.0, 12.0);
        Assert.Equal(["high", "low", "low", "low"], drv.Outputs);
    }

    [Fact]
    public void The_relay_module_closes_10_ms_after_its_input_goes_low()
    {
        var (circuit, supply, ground) = Bench(5);
        var relay = circuit.Add(new RelayModule());
        circuit.Connect(relay.Vcc, supply.Terminals[0]);
        circuit.Connect(relay.Gnd, ground.Terminals[0]);
        var drive = circuit.Add(new PatternGenerator { Pattern = "10", StepRate = 1e3, Repeats = false });
        circuit.Connect(drive.Terminals[0], relay.Input);
        var simulator = Simulate(circuit, 5e-3);
        Assert.False(relay.ContactClosed);
        simulator.Run(5e-3);
        Assert.True(relay.IsEnergised);
        Assert.False(relay.ContactClosed);
        simulator.Run(7e-3);
        Assert.True(relay.ContactClosed);
    }

    [Fact]
    public void The_boost_module_steps_five_volts_up_to_twelve()
    {
        var (circuit, supply, ground) = Bench(5);
        var boost = circuit.Add(new BoostModule { OutputVoltage = 12 });
        circuit.Connect(supply.Terminals[0], boost.Input);
        circuit.Connect(boost.Ground, ground.Terminals[0]);
        Wire(circuit, boost.Output, ground.Terminals[0], 60);
        var simulator = Simulate(circuit, 1e-4);
        Assert.Equal(12.0, simulator.NodeVoltage(boost.Output), 1);
        Assert.Equal((12 * 0.2 / (0.93 * 5)) + 2e-3, boost.InputCurrent, 2);

        // Set below the input, it passes the input through its diode.
        boost.OutputVoltage = 3;
        Assert.Equal(4.5, Simulate(circuit, 1e-4).NodeVoltage(boost.Output), 1);
    }

    private static (Mcp2515 Chip, SpiMaster Master, CanTransceiver Transceiver) CanNode(Circuit circuit, DcVoltageSource supply, Ground ground, string script)
    {
        var chip = circuit.Add(new Mcp2515());
        var master = circuit.Add(new SpiMaster { Transactions = script, ClockFrequency = 1e6 });
        var transceiver = circuit.Add(new CanTransceiver());
        foreach (var (vcc, gnd) in new[] { (chip.Vcc, chip.Gnd), (master.Vcc, master.Gnd), (transceiver.Vcc, transceiver.Gnd) })
        {
            circuit.Connect(vcc, supply.Terminals[0]);
            circuit.Connect(gnd, ground.Terminals[0]);
        }

        circuit.Connect(chip.Reset, supply.Terminals[0]);
        circuit.Connect(master.ChipSelect, chip.ChipSelect);
        circuit.Connect(master.Clock, chip.Clock);
        circuit.Connect(master.MasterOut, chip.Mosi);
        circuit.Connect(master.MasterIn, chip.Miso);
        circuit.Connect(chip.TxCan, transceiver.TransmitIn);
        circuit.Connect(transceiver.ReceiveOut, chip.RxCan);
        return (chip, master, transceiver);
    }

    // 125 kbit/s from 8 MHz: CNF3 05, CNF2 B1, CNF1 01 at 0x28 onwards; then normal mode.
    private const string CanSetup = "C0; wait 50us; 02 28 05 B1 01; 02 2B 01; 02 60 60; 02 0F 00";

    [Fact]
    public void Two_mcp2515s_send_a_frame_across_the_bus_and_the_receiver_acknowledges_it()
    {
        var (circuit, supply, ground) = Bench(5);
        var sender = CanNode(circuit, supply, ground, CanSetup + "; 40 24 60 00 00 03 DE AD 42; 81");
        var receiver = CanNode(circuit, supply, ground, CanSetup + "; wait 2ms; 90 00 00 00 00 00 00 00 00");
        circuit.Connect(sender.Transceiver.High, receiver.Transceiver.High);
        circuit.Connect(sender.Transceiver.Low, receiver.Transceiver.Low);
        Wire(circuit, sender.Transceiver.High, sender.Transceiver.Low, 60);

        var simulator = Simulate(circuit, 1.5e-3, 1e-7, 2e-6);
        Assert.Equal("normal", sender.Chip.Mode);
        Assert.Equal(125e3, sender.Chip.BitRate, 0);
        var frame = receiver.Chip.ReceiveBuffer(0);
        Assert.NotNull(frame);
        Assert.Equal(0x123, frame!.Value.Id);
        Assert.Equal([0xDE, 0xAD, 0x42], frame.Value.Data);
        Assert.Equal(0x04, sender.Chip.Registers[0x2C] & 0x04);     // TX0IF: acknowledged
        Assert.Equal(0, sender.Chip.Registers[0x30] & 0x08);        // TXREQ cleared
        Assert.True(simulator.NodeVoltage(receiver.Chip.Interrupt) < 1);

        // The host reads it out with READ RX BUFFER, which clears RX0IF.
        simulator.Run(1.5e-3);
        var bytes = receiver.Master.ReceivedBytes;
        Assert.Equal([0x24, 0x60, 0x00, 0x00, 0x03, 0xDE, 0xAD, 0x42], bytes.Skip(bytes.Count - 8));
        Assert.Equal(0, receiver.Chip.Registers[0x2C] & 1);
    }

    [Fact]
    public void Two_mcp2515s_sending_at_once_settle_it_by_arbitration_and_loopback_needs_no_bus()
    {
        var (circuit, supply, ground) = Bench(5);
        // 0x100 against 0x0FF: the lower identifier wins.
        var a = CanNode(circuit, supply, ground, CanSetup + "; 40 20 00 00 00 01 AA; 81");
        var b = CanNode(circuit, supply, ground, CanSetup + "; 40 1F E0 00 00 01 BB; 81");
        circuit.Connect(a.Transceiver.High, b.Transceiver.High);
        circuit.Connect(a.Transceiver.Low, b.Transceiver.Low);
        Wire(circuit, a.Transceiver.High, a.Transceiver.Low, 60);
        Simulate(circuit, 4e-3, 1e-7, 2e-6);

        // B (0x0FF) won and was received by A; then A sent its own, received by B.
        Assert.Equal(0x0FF, a.Chip.ReceiveBuffer(0)!.Value.Id);
        Assert.Equal(0x100, b.Chip.ReceiveBuffer(0)!.Value.Id);
        Assert.Equal(0x04, a.Chip.Registers[0x2C] & 0x04);

        var (c2, s2, g2) = Bench(5);
        var solo = CanNode(c2, s2, g2, "C0; wait 50us; 02 28 05 B1 01; 02 60 60; 02 0F 40; 40 0A A0 00 00 02 12 34; 81");
        var sim2 = Simulate(c2, 2e-3, 1e-7, 2e-6);
        Assert.Equal("loopback", solo.Chip.Mode);
        Assert.Equal(0x055, solo.Chip.ReceiveBuffer(0)!.Value.Id);
        Assert.True(sim2.NodeVoltage(solo.Chip.TxCan) > 2.5);
    }
}
