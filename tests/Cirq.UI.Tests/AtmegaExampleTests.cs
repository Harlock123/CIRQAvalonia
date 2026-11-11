using System.Text.RegularExpressions;
using Cirq.Components.Processors;
using Cirq.Components.Tests;
using Cirq.UI.Tests.Support;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>The ATmega328P example: its firmware the guide's source assembled, blinking the LED twice a second.</summary>
public class AtmegaExampleTests
{
    [Theory]
    [InlineData("TIMER1_COMPA", nameof(Examples.AtmegaBlinkFirmware))]
    [InlineData("through a 74595, by SPI", nameof(Examples.UnoSpiCounterFirmware))]
    [InlineData("written to a 24LC256 over I2C", nameof(Examples.UnoI2cEepromFirmware))]
    public void The_firmware_is_the_source_the_guide_prints(string marker, string firmware)
    {
        var guide = File.ReadAllText(Path.Combine(DocPlot.ImageDirectory, "..", "USER_GUIDE.md"));
        var source = Regex.Matches(guide, "```asm\n(.*?)```", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .Single(code => code.Contains(marker, StringComparison.Ordinal));
        var bytes = (byte[])typeof(Examples).GetField(firmware)!.GetValue(null)!;
        Assert.Equal(bytes, AssemblerAvr.Assemble(source));
    }

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

    [Fact]
    public void The_uno_counts_on_the_leds_through_the_shift_register()
    {
        using var vm = Run("Arduino Uno SPI Counter", 0.35);
        var register = vm.Circuit.Components.OfType<Cirq.Components.Digital.Ic74595>().Single();
        Assert.InRange(register.Latched, 3, 4);
        var uno = vm.Circuit.Components.OfType<ArduinoUnoFirmware>().Single();
        Assert.True(uno.Core.SpiMaster);
        Assert.Null(uno.FirmwareError);
    }

    [Fact]
    public void The_uno_prints_what_it_read_back_from_the_eeprom()
    {
        using var vm = Run("Arduino Uno I2C EEPROM", 12e-3);
        var eeprom = vm.Circuit.Components.OfType<Cirq.Components.Buses.I2cEeprom>().Single();
        Assert.Equal("HI!", new string([.. Enumerable.Range(0, 3).Select(i => (char)eeprom.Read(i))]));
        var terminal = vm.Circuit.Components.OfType<Cirq.Components.Buses.SerialTerminal>().Single();
        Assert.Equal("HI!..", terminal.ReceivedText);   // CR and LF show as dots
        Assert.Equal(0, terminal.FramingErrors);
    }

    [Fact]
    public void The_example_blinks_its_led_every_half_second()
    {
        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        vm.Scope.ClearProbes();
        Examples.All.Single(e => e.Name == "ATmega328P Blink").Build(vm);
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);
        Assert.True(vm.Simulation.SteppingByEvents);

        var chip = vm.Circuit.Components.OfType<Atmega328p>().Single();
        Assert.Null(chip.FirmwareError);
        var pb5 = chip.TerminalByName("PB5");
        var sim = vm.Simulation.Simulator!;
        var edges = new List<double>();
        var was = sim.NodeVoltage(pb5) > 2.5;
        while (sim.Time < 1.2)
        {
            sim.Step();
            var high = sim.NodeVoltage(pb5) > 2.5;
            if (high != was) edges.Add(sim.Time);
            was = high;
        }

        Assert.Equal(2, edges.Count);
        Assert.Equal(0.5, edges[0], 0.01);
        Assert.Equal(1.0, edges[1], 0.01);
    }
}
