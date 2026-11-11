using System.Text.RegularExpressions;
using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Components.Processors;
using Cirq.Components.Tests;
using Cirq.UI.Tests.Support;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>The 1.3.0 examples, each held to what it is there to show.</summary>
public class Parts130ExampleTests
{
    private static string GuideSource(string marker)
    {
        var guide = File.ReadAllText(Path.Combine(DocPlot.ImageDirectory, "..", "USER_GUIDE.md"));
        return Regex.Matches(guide, "```asm\n(.*?)```", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .Single(code => code.Contains(marker, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("every fifteenth toggles PB0", nameof(Examples.Attiny85BlinkFirmware))]
    [InlineData("on an AY-3-8910's channel A", nameof(Examples.UnoAyToneFirmware))]
    public void The_avr_firmware_is_the_source_the_guide_prints(string marker, string firmware)
    {
        var bytes = (byte[])typeof(Examples).GetField(firmware)!.GetValue(null)!;
        Assert.Equal(bytes, AssemblerAvr.Assemble(GuideSource(marker)));
    }

    [Fact]
    public void The_8051_firmware_is_the_source_the_guide_prints() =>
        Assert.Equal(Examples.At89c2051HelloFirmware, Assembler8051.Assemble(GuideSource("AT89C2051 greeting")));

    private static MainWindowViewModel Run(string example, double seconds)
    {
        var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        vm.Scope.ClearProbes();
        Examples.All.Single(e => e.Name == example).Build(vm);
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);
        vm.Simulation.Simulator!.Run(seconds);
        return vm;
    }

    private static T Part<T>(MainWindowViewModel vm) => vm.Circuit.Components.OfType<T>().Single();

    [Fact]
    public void The_rgb_led_shows_its_mix()
    {
        using var vm = Run("RGB LED Mixing", 0.2);
        var rgb = Part<RgbLed>(vm);
        Assert.True(rgb.Brightness[0] > rgb.Brightness[1]);
        Assert.True(rgb.Brightness[2] > rgb.Brightness[1]);
        Assert.True(Part<BicolourLed>(vm).Brightness.Max() > 0.3);
    }

    [Fact]
    public void The_xr2206_runs_at_a_kilohertz()
    {
        using var vm = Run("XR2206 Generator", 2e-3);
        var chip = Part<Xr2206>(vm);
        var sim = vm.Simulation.Simulator!;
        var rises = new List<double>();
        var was = sim.NodeVoltage(chip.Sync) > 6;
        while (sim.Time < 8e-3)
        {
            sim.Step();
            var high = sim.NodeVoltage(chip.Sync) > 6;
            if (high && !was) rises.Add(sim.Time);
            was = high;
        }

        Assert.True(rises.Count >= 4, string.Join(", ", rises));
        Assert.Equal(1e-3, (rises[^1] - rises[0]) / (rises.Count - 1), 4);
    }

    [Fact]
    public void The_tone_decoder_locks_and_lights_its_led()
    {
        using var vm = Run("NE567 Tone Decoder", 30e-3);
        var decoder = Part<Ne567>(vm);
        Assert.True(decoder.Locked, $"f0 {decoder.CentreFrequency}, input {decoder.InputFrequency}");
        Assert.True(vm.Circuit.Components.OfType<Led>().Single().Brightness > 0.2);
    }

    [Fact]
    public void The_556_sends_its_tone_in_bursts()
    {
        using var vm = Run("556 Tone Bursts", 5e-3);
        var timer = Part<Ne556>(vm);
        var sim = vm.Simulation.Simulator!;
        var rises = new List<double>();
        var was = false;
        while (sim.Time < 35e-3)
        {
            sim.Step();
            if (timer.IsOutputHigh(1) && !was) rises.Add(sim.Time);
            was = timer.IsOutputHigh(1);
        }

        // Within a burst the rises come every 146 µs; between bursts there's a gap of several milliseconds.
        var gaps = rises.Zip(rises.Skip(1), (a, b) => b - a).ToList();
        Assert.Contains(gaps, g => g < 200e-6);
        Assert.Contains(gaps, g => g > 2e-3);
    }

    [Fact]
    public void The_bar_graph_lights_up_to_its_pot()
    {
        using var vm = Run("LED Bar Graph", 1e-3);
        var chip = Part<Lm3914>(vm);
        Assert.True(chip.Lit.Count(l => l) == 4, $"{chip.Lit.Count(l => l)} lit, SIG {vm.Simulation.Simulator!.NodeVoltage(chip.Signal)}");   // 0.45 of the way down from 1.25 V: four of ten
        Assert.True(chip.Lit[0] && !chip.Lit[4]);
    }

    [Fact]
    public void The_nixie_counts()   // the clock's rises at 0, 0.5 s and 1 s
    {
        using var vm = Run("Nixie Counter", 1.3);
        var tube = Part<NixieTube>(vm);
        var counter = Part<Ic7490>(vm);
        Assert.True(tube.Digit == 3, $"digit {tube.Digit}, glow {string.Join(",", Enumerable.Range(0, 10).Select(d => tube.Glow(d).ToString("F2")))}, anode {vm.Simulation.Simulator!.NodeVoltage(tube.Anode)}");
    }

    [Fact]
    public void The_oled_shows_its_frame_and_word()
    {
        using var vm = Run("OLED Display", 20e-3);
        var oled = Part<Ssd1306>(vm);
        Assert.True(oled.DisplayOn);
        Assert.True(oled.Pixel(0, 0) && oled.Pixel(127, 63));
        Assert.True(oled.Pixel(0x34, 25));        // the C's left side, page 3
        Assert.False(oled.Pixel(64, 40));
    }

    [Fact]
    public void The_matrix_shows_its_face()
    {
        using var vm = Run("LED Matrix", 1e-3);
        var matrix = Part<Max7219Matrix>(vm);
        Assert.False(matrix.Shutdown);
        Assert.Equal([0x3C, 0x42, 0xA5, 0x81, 0xA5, 0x99, 0x42, 0x3C], matrix.Rows.Select(r => (int)r));
    }

    [Fact]
    public void The_tm1637_shows_twelve_thirty_four()
    {
        using var vm = Run("TM1637 Display", 3e-3);
        var display = Part<Tm1637Display>(vm);
        Assert.True(display.On);
        Assert.Equal([0x06, 0xDB, 0x4F, 0x66], Enumerable.Range(0, 4).Select(i => (int)display.Segments(i)));
    }

    [Fact]
    public void The_valve_stage_has_a_gain_of_about_sixty()
    {
        using var vm = Run("Valve Preamp", 40e-3);
        var sim = vm.Simulation.Simulator!;
        var output = vm.Circuit.Components.OfType<Cirq.Components.Passive.Resistor>().Single(r => r.Resistance == 1e6 && r.X > 0);   // the load, not the grid leak
        double low = double.MaxValue, high = double.MinValue;
        while (sim.Time < 45e-3)
        {
            sim.Step();
            var v = sim.NodeVoltage(output.A);
            (low, high) = (Math.Min(low, v), Math.Max(high, v));
        }

        Assert.InRange((high - low) / 0.2, 40, 75);
    }

    [Fact]
    public void The_uno_plays_440_hz_on_the_ay()
    {
        using var vm = Run("AY-3-8910 Tone", 2e-3);
        var ay = Part<Ay38910>(vm);
        Assert.Equal(142, ay.Registers[0]);
        Assert.Equal(440.1, ay.ToneFrequency(0), 1);
        var sim = vm.Simulation.Simulator!;
        var rises = new List<double>();
        var was = sim.NodeVoltage(ay.ChannelA) > 0.2;
        while (sim.Time < 20e-3)
        {
            sim.Step();
            var high = sim.NodeVoltage(ay.ChannelA) > 0.2;
            if (high && !was) rises.Add(sim.Time);
            was = high;
        }

        var rate = (rises.Count - 1) / (rises[^1] - rises[0]);
        Assert.True(Math.Abs(rate - 440) < 2, $"{rate} Hz from {rises.Count} rises, first {rises.FirstOrDefault()}");
    }

    [Fact]
    public void The_ir_receiver_sends_its_frame()
    {
        using var vm = Run("IR Remote Frame", 0.1);
        Assert.Equal(1, Part<IrReceiver>(vm).FramesSent);
    }

    [Fact]
    public void The_dds_makes_a_kilohertz_sine()
    {
        using var vm = Run("DDS Generator", 1e-3);
        var dds = Part<Ad9833>(vm);
        Assert.False(dds.InReset);
        Assert.Equal(1000, dds.Frequency, 0);
    }

    [Fact]
    public void The_attiny_blinks_twice_a_second()
    {
        using var vm = Run("ATtiny85 Blink", 0.05);
        var tiny = Part<Attiny85>(vm);
        Assert.Null(tiny.FirmwareError);
        var pb0 = tiny.TerminalByName("PB0");
        var sim = vm.Simulation.Simulator!;
        var edges = new List<double>();
        var was = sim.NodeVoltage(pb0) > 2.5;
        while (sim.Time < 1.6)
        {
            sim.Step();
            var high = sim.NodeVoltage(pb0) > 2.5;
            if (high != was) edges.Add(sim.Time);
            was = high;
        }

        Assert.True(edges.Count >= 3, string.Join(", ", edges));
        Assert.Equal(15 * 32.768e-3, edges[2] - edges[1], 3);
    }

    [Fact]
    public void The_8051_says_hello()
    {
        using var vm = Run("AT89C2051 Serial", 15e-3);
        var terminal = Part<SerialTerminal>(vm);
        Assert.Equal("HELLO 8051..", terminal.ReceivedText);
        Assert.Equal(0, terminal.FramingErrors);
        Assert.True(vm.Circuit.Components.OfType<Led>().Single().Brightness > 0.2);
    }
}
