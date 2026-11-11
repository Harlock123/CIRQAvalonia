using System.Text.RegularExpressions;
using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Electromechanical;
using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Components.Processors;
using Cirq.Components.Tests;
using Cirq.UI.Tests.Support;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>The 1.4.0 examples, each held to what it is there to show.</summary>
public class Parts140ExampleTests
{
    private static MainWindowViewModel Run(string example, double seconds)
    {
        var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        vm.Scope.ClearProbes();
        Examples.All.Single(e => e.Name == example).Build(vm);
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);
        if (seconds > 0) vm.Simulation.Simulator!.Run(seconds);
        return vm;
    }

    private static T Part<T>(MainWindowViewModel vm) => vm.Circuit.Components.OfType<T>().First();

    private static List<double> Rises(MainWindowViewModel vm, Cirq.Core.Topology.Terminal node, double until, double threshold)
    {
        var sim = vm.Simulation.Simulator!;
        var rises = new List<double>();
        var was = sim.NodeVoltage(node) > threshold;
        while (sim.Time < until)
        {
            sim.Step();
            var high = sim.NodeVoltage(node) > threshold;
            if (high && !was) rises.Add(sim.Time);
            was = high;
        }

        return rises;
    }

    private static (double Low, double High) Swing(MainWindowViewModel vm, Cirq.Core.Topology.Terminal node, double until)
    {
        var sim = vm.Simulation.Simulator!;
        double low = double.MaxValue, high = double.MinValue;
        while (sim.Time < until)
        {
            sim.Step();
            var v = sim.NodeVoltage(node);
            (low, high) = (Math.Min(low, v), Math.Max(high, v));
        }

        return (low, high);
    }

    [Fact]
    public void The_sn76489_firmware_is_the_source_the_guide_prints()
    {
        var guide = File.ReadAllText(Path.Combine(DocPlot.ImageDirectory, "..", "USER_GUIDE.md"));
        var source = Regex.Matches(guide, "```asm\n(.*?)```", RegexOptions.Singleline).Select(m => m.Groups[1].Value)
            .Single(code => code.Contains("on an SN76489's first tone channel", StringComparison.Ordinal));
        Assert.Equal(Examples.UnoSn76489Firmware, AssemblerAvr.Assemble(source));
    }

    [Fact]
    public void The_ujt_and_put_oscillators_fire_regularly()
    {
        using (var vm = Run("UJT Oscillator", 2e-3))
        {
            var ujt = Part<Unijunction>(vm);
            var pulses = Rises(vm, ujt.Base1, 40e-3, 1.5);
            Assert.True(pulses.Count >= 4, $"{pulses.Count} pulses");
        }

        using (var vm = Run("PUT Oscillator", 2e-3))
        {
            var put = Part<ProgrammableUnijunction>(vm);
            var firings = Rises(vm, put.Cathode, 60e-3, 1.0);
            Assert.True(firings.Count >= 3, $"{firings.Count} firings");
        }
    }

    [Fact]
    public void The_schmitt_oscillator_runs_near_a_kilohertz()
    {
        using var vm = Run("Schmitt Oscillator", 1e-3);
        var rises = Rises(vm, Part<Ic40106>(vm).Inverter(0).Y, 10e-3, 2.5);
        var period = (rises[^1] - rises[0]) / (rises.Count - 1);
        Assert.InRange(period, 0.4e-3, 2.5e-3);
    }

    [Fact]
    public void The_ota_amplifies_by_its_bias()
    {
        using var vm = Run("OTA Amplifier", 2e-3);
        var ota = Part<Lm13700>(vm);
        Assert.InRange(ota.AmplifierBias(0), 50e-6, 400e-6);
        var (low, high) = Swing(vm, ota.Half(0).Output, 5e-3);
        Assert.InRange(high - low, 0.2, 4.0);
    }

    [Fact]
    public void The_valve_amplifier_drives_its_speaker()
    {
        using var vm = Run("Valve Amplifier", 10e-3);
        var el84 = Part<Pentode>(vm);
        Assert.InRange(el84.PlateCurrent, 20e-3, 80e-3);
        var speaker = vm.Circuit.Components.OfType<Cirq.Components.Passive.Speaker>().Single();
        var (low, high) = Swing(vm, speaker.Terminals[0], 14e-3);
        Assert.True(high - low > 0.5, $"{high - low} V at the speaker");
    }

    [Fact]
    public void The_fuzz_face_squares_off_its_note()
    {
        using var vm = Run("Fuzz Face", 20e-3);
        var output = vm.Circuit.Components.OfType<Cirq.Components.Passive.Capacitor>().Single(c => c.Capacitance == 10e-9).Terminals[1];
        var (low, high) = Swing(vm, output, 30e-3);
        Assert.True(high - low > 0.5, $"{high - low} V of fuzz");
    }

    [Fact]
    public void The_spectrum_analyser_shows_its_two_tones()
    {
        using var vm = Run("Spectrum Analyser", 120e-3);
        var eq = Part<Msgeq7>(vm);
        var peaks = Enumerable.Range(0, 7).Select(eq.Peak).ToList();
        var top = peaks.Select((p, i) => (p, i)).OrderByDescending(x => x.p).Take(2).Select(x => x.i).Order().ToList();
        Assert.True(top.SequenceEqual([1, 4]), string.Join(", ", peaks.Select(p => p.ToString("F3"))));   // 160 Hz and 2.5 kHz
    }

    [Fact]
    public void The_uno_plays_440_hz_on_the_sn76489()
    {
        using var vm = Run("SN76489 Tone", 2e-3);
        var chip = Part<Sn76489>(vm);
        Assert.Equal(254, chip.TonePeriod(0));
        Assert.Equal(0, chip.Attenuation(0));
        Assert.Equal(15, chip.Attenuation(1));
        Assert.Equal(440.4, chip.ToneFrequency(0), 0);
    }

    [Fact]
    public void The_voltmeter_reads_its_pot()
    {
        using var vm = Run("Digital Voltmeter", 0.4);
        var meter = Part<Icl7107>(vm);
        var vin = vm.Simulation.Simulator!.NodeVoltage(meter.Pin("IN HI"));
        var vref = vm.Simulation.Simulator!.NodeVoltage(meter.Pin("REF HI"));
        Assert.Equal(1.0, vref, 2);
        Assert.InRange(meter.Reading, (int)(1000 * vin / vref) - 2, (int)(1000 * vin / vref) + 2);
        Assert.InRange(meter.Reading, 500, 1999);
    }

    [Fact]
    public void The_current_monitor_reads_the_lamps_current()
    {
        using var vm = Run("Current Monitor", 50e-3);
        var sensor = Part<Acs712>(vm);
        Assert.InRange(sensor.Current, 0.2, 3);
        Assert.Equal(2.5 + (sensor.Current * 0.185), vm.Simulation.Simulator!.NodeVoltage(sensor.Output), 2);
    }

    [Fact]
    public void The_adc0804_shows_the_pots_voltage()
    {
        using var vm = Run("ADC0804 Converter", 15e-3);
        Assert.Equal(179, Part<Adc0804>(vm).Result);        // 0.7 of 256
    }

    [Fact]
    public void The_crystal_radio_detects_the_tone()
    {
        using var vm = Run("Crystal Radio", 2e-3);
        var earpiece = vm.Circuit.Components.OfType<Diode>().Single().Terminals[1];
        var (low, high) = Swing(vm, earpiece, 4e-3);
        Assert.True(high > 0.1 && high - low > 0.05, $"{low}..{high}");
    }

    [Fact]
    public void The_mixer_and_the_modulator_make_their_products()
    {
        using (var vm = Run("Superhet Mixer", 1e-3))
        {
            var ifFilter = vm.Circuit.Components.OfType<Cirq.Components.Passive.Resistor>().Single(r => r.Resistance == 1e3).B;
            var (low, high) = Swing(vm, ifFilter, 1.3e-3);
            Assert.True(high - low > 0.01, $"{high - low} V of IF");
        }

        using (var vm = Run("Balanced Modulator", 0.2e-3))
        {
            var (low, high) = Swing(vm, Part<Mc1496>(vm).OutputMinus, 1.2e-3);
            Assert.True(high - low > 0.1, $"{high - low} V of sidebands");
        }
    }

    [Fact]
    public void The_encoder_and_decoder_do_their_jobs()
    {
        using (var vm = Run("Priority Encoder", 1e-5))
        {
            var enc = Part<Ic74148>(vm);
            var sim = vm.Simulation.Simulator!;
            var code = (sim.NodeVoltage(enc.Pin("A0")) > 2.5 ? 1 : 0) | (sim.NodeVoltage(enc.Pin("A1")) > 2.5 ? 2 : 0) | (sim.NodeVoltage(enc.Pin("A2")) > 2.5 ? 4 : 0);
            Assert.Equal(~5 & 7, code);
        }

        using (var vm = Run("Address Decoder", 0))
        {
            var cmp = Part<Ic74688>(vm);
            var sim = vm.Simulation.Simulator!;
            var lows = new List<double>();
            var was = true;
            while (sim.Time < 0.3)
            {
                sim.Step();
                var high = sim.NodeVoltage(cmp.Pin("P=Q")) > 2.5;
                if (!high && was) lows.Add(sim.Time);
                was = high;
            }

            Assert.True(lows.Count >= 1, "P=Q never went low");
            Assert.Equal(0x5A, Math.Round(lows[0] * 1e3) % 256, 0);
        }
    }

    [Fact]
    public void The_debouncer_and_the_gal_count_clean_edges()
    {
        using (var vm = Run("Debounced Switch", 0))
        {
            var q = Part<Ic74279>(vm).Pin("2Q");
            Assert.Equal(2, Rises(vm, q, 26e-3, 2.5).Count);     // once a 13 ms cycle, however it bounces
        }

        using (var vm = Run("GAL Counter", 3.2e-3))
        {
            var gal = Part<Gal16v8>(vm);
            Assert.Null(gal.EquationError);
            var count = (gal.Register(19) ? 1 : 0) | (gal.Register(18) ? 2 : 0);
            Assert.Equal(3 % 4, count);
        }
    }

    [Fact]
    public void The_displays_show_what_they_are_sent()
    {
        using (var vm = Run("Nokia 5110", 0.3e-3))
        {
            var lcd = Part<Pcd8544>(vm);
            Assert.False(lcd.PoweredDown);
            Assert.Equal(0x3E, lcd.Ram[(2 * 84) + 30]);     // the C's first column
        }

        using (var vm = Run("TIL311 Hex Display", 1.3))
            Assert.Equal(2, Part<Til311>(vm).Shown);           // the 74193 counts the rises at 0.5 and 1 s

        using (var vm = Run("Multiplexed Display", 40e-3))
        {
            var display = Part<QuadSevenSegment>(vm);
            Assert.Equal([0x06, 0x5B, 0x4F, 0x66], Enumerable.Range(0, 4).Select(d => display.Pattern(d) & 0x7F));
        }
    }

    [Fact]
    public void The_bus_modules_answer()
    {
        using (var vm = Run("Weather Station", 14e-3))
        {
            var master = Part<I2cMaster>(vm);
            var r = master.ReceivedBytes;
            Assert.Equal(0x60, r[0]);
            var adcT = (r[4] << 12) | (r[5] << 4) | (r[6] >> 4);
            Assert.Equal(18.5, Bme280.CompensateTemperature(adcT) / 100.0, 1);
        }

        using (var vm = Run("Servo Driver", 1e-3))
        {
            var pwm = Part<Pca9685>(vm);
            Assert.False(pwm.Asleep);
            Assert.Equal(50.0, pwm.Frequency, 0);
        }

        using (var vm = Run("I/O Expander", 2e-3))
        {
            var io = Part<Mcp23017>(vm);
            Assert.True(vm.Simulation.Simulator!.NodeVoltage(io.PortA[0]) > 2.5 && vm.Simulation.Simulator!.NodeVoltage(io.PortA[1]) < 1);
            Assert.Equal(0xFE, Part<I2cMaster>(vm).ReceivedBytes[0]);
        }

        using (var vm = Run("CAN Network", 1.6e-3))
        {
            var chips = vm.Circuit.Components.OfType<Mcp2515>().ToList();
            Assert.Equal(0x123, chips[1].ReceiveBuffer(0)!.Value.Id);
        }

        using (var vm = Run("Thermocouple Meter", 0.1e-3))
            Assert.InRange(Part<Max6675>(vm).Temperature!.Value, 296, 300.5);
    }

    [Fact]
    public void The_power_and_motor_modules_work()
    {
        using (var vm = Run("Relay Module", 0.3))
            Assert.True(Part<RelayModule>(vm).ContactClosed || !Part<RelayModule>(vm).IsEnergised);

        using (var vm = Run("Boost Converter", 1e-4))
            Assert.Equal(12.0, vm.Simulation.Simulator!.NodeVoltage(Part<BoostModule>(vm).Output), 1);

        using (var vm = Run("TB6612 Motor Driver", 50e-3))
            Assert.True(Part<DcMotor>(vm).AngularVelocity > 0, "the motor turns");

        using (var vm = Run("DAC Staircase", 1e-3))
        {
            var amp = Part<OperationalAmplifier>(vm);
            var (low, high) = Swing(vm, amp.Output, 12e-3);
            Assert.InRange(high, 8, 10.1);
            Assert.InRange(low, -1.0, 0.5);    // a little under, as the op-amp catches the wrap from 255 to 0
        }
    }
}
