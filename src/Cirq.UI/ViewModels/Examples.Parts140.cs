using System.Globalization;
using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Electromechanical;
using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Processors;
using Cirq.Components.Sources;
using Cirq.Core.Topology;

namespace Cirq.UI.ViewModels;

/// <summary>The examples for the parts that came in 1.4.0.</summary>
public static partial class Examples
{
    /// <summary>The Uno's SN76489 program as the guide prints it assembled: 440 Hz on tone channel 0.</summary>
    public static readonly byte[] UnoSn76489Firmware =
    [
        0x08, 0xE0, 0x0E, 0xBF, 0x0F, 0xEF, 0x0D, 0xBF, 0x0F, 0xEF, 0x0A, 0xB9, 0x01, 0xE0, 0x07, 0xB9, 0x08, 0xB9, 0x4E, 0xE8,
        0x0B, 0xD0, 0x4F, 0xE0, 0x09, 0xD0, 0x40, 0xE9, 0x07, 0xD0, 0x4F, 0xEB, 0x05, 0xD0, 0x4F, 0xED, 0x03, 0xD0, 0x4F, 0xEF,
        0x01, 0xD0, 0xFF, 0xCF, 0x4B, 0xB9, 0x40, 0x98, 0x00, 0x00, 0x00, 0x00, 0x40, 0x9A, 0x08, 0x95,
    ];

    private static (DcVoltageSource Supply, Ground Ground) Rail(Circuit circuit, double volts, double x, double y)
    {
        var supply = Place(circuit, new DcVoltageSource(volts), x, y);
        var ground = Place(circuit, new Ground(), x, y + 140);
        circuit.Connect(supply.Negative, ground.Pin);
        return (supply, ground);
    }

    private static Resistor Between(Circuit circuit, Terminal a, Terminal b, double ohms, double x, double y, bool upright = true)
    {
        var r = Place(circuit, new Resistor(ohms), x, y);
        if (upright) r.RotationDegrees = 90;
        circuit.Connect(a, r.A);
        circuit.Connect(r.B, b);
        return r;
    }

    private static Capacitor Cap(Circuit circuit, Terminal a, Terminal b, double farads, double x, double y)
    {
        var c = Place(circuit, new Capacitor(farads), x, y);
        c.RotationDegrees = 90;
        circuit.Connect(a, c.Terminals[0]);
        circuit.Connect(c.Terminals[1], b);
        return c;
    }

    private static void Bus(Circuit circuit, Terminal supply, Terminal ground, I2cDevice device)
    {
        circuit.Connect(supply, device.Vcc);
        circuit.Connect(device.Gnd, ground);
    }

    // ───── Timers & Oscillators ─────

    /// <summary>A 2N2646 relaxation oscillator: 47 kΩ charging 100 nF until the emitter fires, a pulse across B1's 47 Ω.</summary>
    public static void LoadUjtOscillator(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "UJT relaxation oscillator";
        var (supply, ground) = Rail(circuit, 12.0, -300, 0);
        var ujt = Place(circuit, new Unijunction(), 60, 0);
        var g2 = Place(circuit, new Ground(), 60, 240);
        Between(circuit, supply.Positive, ujt.Base2, 470, 70, -140);
        Between(circuit, supply.Positive, ujt.Emitter, 47e3, -100, -100);
        Cap(circuit, ujt.Emitter, g2.Pin, 100e-9, -100, 120);
        Between(circuit, ujt.Base1, g2.Pin, 47, 70, 140);
        _ = ground;
        vm.Scope.TimebasePerDivision = 5e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(ujt.Emitter, "Emitter");
        vm.Scope.AddProbe(ujt.Base1, "B1");
    }

    /// <summary>A 2N6027 oscillator: its gate held at half the supply by two 10 kΩ, 470 kΩ charging 22 nF on the anode.</summary>
    public static void LoadPutOscillator(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "PUT relaxation oscillator";
        var (supply, ground) = Rail(circuit, 12.0, -320, 0);
        var put = Place(circuit, new ProgrammableUnijunction(), 60, 0);
        var g2 = Place(circuit, new Ground(), 60, 240);
        Between(circuit, supply.Positive, put.Gate, 10e3, -80, -120);
        Between(circuit, put.Gate, g2.Pin, 10e3, -80, 120);
        Between(circuit, supply.Positive, put.Anode, 470e3, 60, -140);
        Cap(circuit, put.Anode, g2.Pin, 22e-9, 180, 60);
        Between(circuit, put.Cathode, g2.Pin, 47, 60, 140);
        _ = ground;
        vm.Scope.TimebasePerDivision = 5e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(put.Anode, "Anode");
        vm.Scope.AddProbe(put.Cathode, "Cathode");
    }

    /// <summary>One 40106 gate, 10 kΩ from its output back to its input and 100 nF to ground: about 1 kHz.</summary>
    public static void LoadSchmittOscillator(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "40106 oscillator";
        var (supply, ground) = Rail(circuit, 5.0, -300, 0);
        var chip = Place(circuit, new Ic40106(), 0, 0);
        circuit.Connect(supply.Positive, chip.Vcc);
        circuit.Connect(chip.Gnd, ground.Pin);
        var (a, y) = chip.Inverter(0);
        Between(circuit, y, a, 10e3, -120, -140, upright: false);
        Cap(circuit, a, ground.Pin, 100e-9, -160, 120);
        for (var i = 1; i < 6; i++) circuit.Connect(chip.Inverter(i).A, ground.Pin);
        vm.Scope.TimebasePerDivision = 0.5e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(a, "Capacitor");
        vm.Scope.AddProbe(y, "Output");
    }

    // ───── Audio ─────

    /// <summary>An LM13700 as a VCA: a 100 mV tone through 10 kΩ / 220 Ω into the inputs, the gain set by the bias current a pot drives.</summary>
    public static void LoadOtaAmplifier(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "LM13700 voltage-controlled amplifier";
        var (plus, ground) = Rail(circuit, 15.0, -520, -60);
        var minus = Place(circuit, new DcVoltageSource(-15.0), -520, 220);
        circuit.Connect(minus.Negative, ground.Pin);
        var ota = Place(circuit, new Lm13700(), 0, 0);
        circuit.Connect(ota.PositiveSupply, plus.Positive);
        circuit.Connect(ota.NegativeSupply, minus.Positive);
        var h = ota.Half(0);
        var tone = Place(circuit, new FunctionGenerator(Waveform.Sine, 1e3, 1.0), -400, -260);
        var g2 = Place(circuit, new Ground(), -300, 360);
        circuit.Connect(tone.Return, g2.Pin);
        Between(circuit, tone.Output, h.NonInverting, 10e3, -240, -200, upright: false);
        Between(circuit, h.NonInverting, g2.Pin, 220, -200, 120);
        Between(circuit, h.Inverting, g2.Pin, 220, -140, 120);

        // The control: a pot from +15 V to ground into AMP BIAS through 47 kΩ, up to about 0.5 mA.
        var pot = Place(circuit, new Potentiometer(10e3, 0.6), -260, -40);
        circuit.Connect(pot.A, plus.Positive);
        circuit.Connect(pot.B, g2.Pin);
        Between(circuit, pot.Wiper, h.AmpBias, 47e3, -160, -80, upright: false);
        Between(circuit, h.Output, g2.Pin, 10e3, 240, 120);
        circuit.Connect(h.BufferIn, h.Output);
        Between(circuit, h.BufferOut, minus.Positive, 10e3, 320, 120);
        foreach (var t in new[] { ota.Half(1).NonInverting, ota.Half(1).Inverting, ota.Half(1).AmpBias, ota.Half(1).BufferIn })
            circuit.Connect(t, g2.Pin);

        vm.Scope.TimebasePerDivision = 0.5e-3;
        vm.Scope.VoltsPerDivision = 0.5;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(h.NonInverting, "In");
        vm.Scope.AddProbe(h.BufferOut, "Out");
    }

    /// <summary>A single-ended valve amplifier: a 12AX7 stage driving an EL84 into a 5 kΩ : 8 Ω output transformer and a speaker.</summary>
    public static void LoadValveAmplifier(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Single-ended valve amplifier";
        var (b, ground) = Rail(circuit, 250.0, 520, -420);
        var g2 = Place(circuit, new Ground(), 0, 320);
        var input = Place(circuit, new FunctionGenerator(Waveform.Sine, 1e3, 0.4), -560, 40);
        circuit.Connect(input.Return, g2.Pin);
        var c1 = Place(circuit, new Capacitor(100e-9), -420, 0);
        circuit.Connect(input.Output, c1.Terminals[0]);
        var pre = Place(circuit, new Triode(), -260, 0);
        circuit.Connect(c1.Terminals[1], pre.Grid);
        Between(circuit, pre.Grid, g2.Pin, 1e6, -340, 140);
        Between(circuit, b.Positive, pre.Plate, 100e3, -260, -160);
        Between(circuit, pre.Cathode, g2.Pin, 1.5e3, -300, 160);
        var bypass1 = Place(circuit, new ElectrolyticCapacitor(22e-6), -220, 160);
        bypass1.RotationDegrees = 90;
        circuit.Connect(pre.Cathode, bypass1.Terminals[0]);
        circuit.Connect(bypass1.Terminals[1], g2.Pin);

        var c2 = Place(circuit, new Capacitor(22e-9), -120, -60);
        circuit.Connect(pre.Plate, c2.Terminals[0]);
        var output = Place(circuit, new Pentode(), 60, 0);
        circuit.Connect(c2.Terminals[1], output.Grid);
        Between(circuit, output.Grid, g2.Pin, 470e3, -20, 140);
        Between(circuit, output.Cathode, g2.Pin, 150, 40, 180);
        var bypass2 = Place(circuit, new ElectrolyticCapacitor(100e-6), 120, 180);
        bypass2.RotationDegrees = 90;
        circuit.Connect(output.Cathode, bypass2.Terminals[0]);
        circuit.Connect(bypass2.Terminals[1], g2.Pin);
        circuit.Connect(output.Screen, b.Positive);

        var transformer = Place(circuit, new Transformer(10, 10.0 / 625, 0.999), 260, -100);
        circuit.Connect(b.Positive, transformer.Terminals[0]);
        circuit.Connect(transformer.Terminals[1], output.Plate);
        var speaker = Place(circuit, new Speaker(), 420, -80);
        circuit.Connect(transformer.Terminals[2], speaker.Terminals[0]);
        circuit.Connect(transformer.Terminals[3], speaker.Terminals[1]);
        circuit.Connect(speaker.Terminals[1], g2.Pin);
        _ = ground;

        vm.Scope.TimebasePerDivision = 0.5e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(input.Output, "In");
        vm.Scope.AddProbe(speaker.Terminals[0], "Speaker");
    }

    /// <summary>The Fuzz Face: two AC128 germanium PNPs on a negative-ground 9 V supply, a 100 mV note clipped into a fuzz.</summary>
    public static void LoadFuzzFace(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Fuzz Face";
        // PNP and positive ground: the circuit on −9 V.
        var battery = Place(circuit, new DcVoltageSource(-9.0), 360, -300);
        var ground = Place(circuit, new Ground(), 360, -160);
        circuit.Connect(battery.Negative, ground.Pin);
        var rail = battery.Positive;

        var guitar = Place(circuit, new FunctionGenerator(Waveform.Sine, 330, 0.2), -460, 60);
        circuit.Connect(guitar.Return, ground.Pin);
        var input = Place(circuit, new ElectrolyticCapacitor(2.2e-6), -320, 20);
        circuit.Connect(guitar.Output, input.Terminals[0]);
        var q1 = Place(circuit, new BipolarTransistor(BjtModel.Ac128), -160, 60);
        var q2 = Place(circuit, new BipolarTransistor(BjtModel.Ac128), 80, 0);
        circuit.Connect(input.Terminals[1], q1.Base);
        circuit.Connect(q1.Emitter, ground.Pin);
        circuit.Connect(q1.Collector, q2.Base);
        Between(circuit, rail, q1.Collector, 33e3, -160, -140);
        Between(circuit, q2.Emitter, q1.Base, 100e3, -40, 160, upright: false);

        // Q2's emitter: 1 kΩ to ground, its bypass through the fuzz control.
        Between(circuit, q2.Emitter, ground.Pin, 1e3, 120, 160);
        var fuzz = Place(circuit, new Potentiometer(1e3, 1.0), 220, 160);
        circuit.Connect(q2.Emitter, fuzz.A);
        var bypass = Place(circuit, new ElectrolyticCapacitor(22e-6), 300, 220);
        bypass.RotationDegrees = 90;
        circuit.Connect(fuzz.Wiper, bypass.Terminals[0]);
        circuit.Connect(bypass.Terminals[1], ground.Pin);
        Between(circuit, rail, q2.Collector, 470, 60, -160);
        Between(circuit, q2.Collector, ground.Pin, 8.2e3, 200, -100);
        var output = Place(circuit, new Capacitor(10e-9), 280, -40);
        circuit.Connect(q2.Collector, output.Terminals[0]);
        Between(circuit, output.Terminals[1], ground.Pin, 500e3, 360, 40);

        vm.Scope.TimebasePerDivision = 2e-3;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(guitar.Output, "Guitar");
        vm.Scope.AddProbe(output.Terminals[1], "Fuzz");
    }

    /// <summary>An MSGEQ7 hearing two tones, 160 Hz and 2.5 kHz; a pattern generator strobes OUT through the seven bands.</summary>
    public static void LoadSpectrumAnalyser(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "MSGEQ7 spectrum analyser";
        var (supply, ground) = Rail(circuit, 5.0, -460, -60);
        var eq = Place(circuit, new Msgeq7(), 60, 0);
        circuit.Connect(eq.Vdd, supply.Positive);
        circuit.Connect(eq.Vss, ground.Pin);
        var low = Place(circuit, new FunctionGenerator(Waveform.Sine, 160, 0.4), -460, 200);
        var high = Place(circuit, new FunctionGenerator(Waveform.Sine, 2.5e3, 0.2), -300, 200);
        circuit.Connect(low.Return, ground.Pin);
        circuit.Connect(high.Return, low.Output);
        var c = Place(circuit, new Capacitor(100e-9), -120, 60);
        circuit.Connect(high.Output, c.Terminals[0]);
        circuit.Connect(c.Terminals[1], eq.Input);

        // RESET, then seven strobes 1 ms apart, over and over.
        var strobe = string.Concat(Enumerable.Repeat("10", 7)) + "11";
        var reset = "00" + new string('0', 12) + "10";
        var bus = Place(circuit, new PatternGenerator { Pattern = $"{strobe}\n{reset}", StepRate = 2e3, Repeats = true, StartDelay = 40e-3 }, -200, -200);
        circuit.Connect(bus.Terminals[0], eq.Strobe);
        circuit.Connect(bus.Terminals[1], eq.Reset);
        Between(circuit, eq.Output, ground.Pin, 100e3, 220, 120);

        vm.Scope.TimebasePerDivision = 2e-3;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(eq.Strobe, "STROBE");
        vm.Scope.AddProbe(eq.Output, "OUT");
    }

    /// <summary>An Arduino Uno writing an SN76489: 440 Hz on its first tone channel.</summary>
    public static void LoadUnoSn76489(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Arduino Uno and SN76489";
        var uno = Place(circuit, new ArduinoUnoFirmware(), -260, 0);
        uno.Load(MemoryImage.FromBinary(UnoSn76489Firmware));
        var chip = Place(circuit, new Sn76489(), 200, 0);
        var ground = Place(circuit, new Ground(), 200, 260);
        var five = uno.TerminalByName("5V");
        circuit.Connect(uno.TerminalByName("GND"), ground.Pin);
        circuit.Connect(five, chip.Vcc);
        circuit.Connect(chip.Gnd, ground.Pin);
        circuit.Connect(chip.ChipEnable, ground.Pin);
        circuit.Connect(uno.TerminalByName("A0"), chip.WriteEnable);

        // TI's D0 is the top bit: the Uno's D7 goes to it, D0 to TI's D7.
        for (var i = 0; i < 8; i++)
        {
            var name = $"D{7 - i}";
            circuit.Connect(uno.Terminals.First(t => t.Name == name || t.Name.StartsWith(name + "/", StringComparison.Ordinal)), chip.Data[i]);
        }

        var clock = Place(circuit, new OscillatorModule { Frequency = 3.579545e6 }, 420, -160);
        circuit.Connect(clock.TerminalByName("VCC"), five);
        circuit.Connect(clock.TerminalByName("GND"), ground.Pin);
        circuit.Connect(clock.TerminalByName("EN"), five);
        circuit.Connect(clock.TerminalByName("OUT"), chip.Clock);
        Between(circuit, chip.Audio, ground.Pin, 10e3, 420, 140);

        vm.Scope.TimebasePerDivision = 2e-3;
        vm.Scope.VoltsPerDivision = 0.5;
        vm.Scope.AddProbe(chip.Audio, "AUDIO");
    }

    // ───── Measurement ─────

    /// <summary>An ICL7107 panel meter: a pot on IN HI against a 1.000 V reference from COMMON, four common-anode displays.</summary>
    public static void LoadDigitalVoltmeter(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "ICL7107 voltmeter";
        var (plus, ground) = Rail(circuit, 5.0, -560, -200);
        var minus = Place(circuit, new DcVoltageSource(-5.0), -560, 100);
        circuit.Connect(minus.Negative, ground.Pin);
        var meter = Place(circuit, new Icl7107(), -200, 0);
        circuit.Connect(meter.Pin("V+"), plus.Positive);
        circuit.Connect(meter.Pin("V-"), minus.Positive);
        circuit.Connect(meter.Pin("GND"), ground.Pin);
        circuit.Connect(meter.Pin("IN LO"), ground.Pin);
        circuit.Connect(meter.Pin("REF LO"), ground.Pin);

        // 1.000 V for REF HI from a divider on the supply; the input from a pot across 2 V.
        Between(circuit, plus.Positive, meter.Pin("REF HI"), 4e3, -420, -100);
        Between(circuit, meter.Pin("REF HI"), ground.Pin, 1e3, -420, 60);
        var pot = Place(circuit, new Potentiometer(10e3, 0.62), -420, 260);
        Between(circuit, plus.Positive, pot.A, 15e3, -500, 200);
        circuit.Connect(pot.B, ground.Pin);
        circuit.Connect(pot.Wiper, meter.Pin("IN HI"));

        // Units, tens and hundreds: each display's segments a-g on its seven pins, its anode on V+.
        string[][] digits = [["A1", "B1", "C1", "D1", "E1", "F1", "G1"], ["A2", "B2", "C2", "D2", "E2", "F2", "G2"], ["A3", "B3", "C3", "D3", "E3", "F3", "G3"]];
        for (var d = 0; d < 3; d++)
        {
            var display = Place(circuit, new SevenSegmentDisplay(commonAnode: true), 420 - (d * 140), 0);
            circuit.Connect(display.TerminalByName("COM"), plus.Positive);
            for (var s = 0; s < 7; s++) circuit.Connect(display.TerminalByName(((char)('a' + s)).ToString()), meter.Pin(digits[d][s]));
        }

        var thousands = Place(circuit, new SevenSegmentDisplay(commonAnode: true), 0, -260);
        circuit.Connect(thousands.TerminalByName("COM"), plus.Positive);
        circuit.Connect(thousands.TerminalByName("b"), meter.Pin("AB4"));
        circuit.Connect(thousands.TerminalByName("c"), meter.Pin("AB4"));
        circuit.Connect(thousands.TerminalByName("g"), meter.Pin("POL"));

        vm.Scope.VoltsPerDivision = 0.5;
        vm.Scope.AddProbe(meter.Pin("IN HI"), "IN HI");
    }

    /// <summary>An ACS712 in the 12 V feed to a 6 Ω load: OUT at 2.5 V + 2 A × 185 mV.</summary>
    public static void LoadCurrentMonitor(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "ACS712 current monitor";
        var (logic, ground) = Rail(circuit, 5.0, -360, 140);
        var (twelve, g2) = Rail(circuit, 12.0, -360, -200);
        var sensor = Place(circuit, new Acs712(), 0, 0);
        circuit.Connect(sensor.Vcc, logic.Positive);
        circuit.Connect(sensor.Gnd, ground.Pin);
        circuit.Connect(twelve.Positive, sensor.CurrentIn);
        var lamp = Place(circuit, new IncandescentLamp(), 0, -200);
        circuit.Connect(sensor.CurrentOut, lamp.Terminals[0]);
        circuit.Connect(lamp.Terminals[1], g2.Pin);
        vm.Scope.TimebasePerDivision = 20e-3;
        vm.Scope.VoltsPerDivision = 0.5;
        vm.Scope.AddProbe(sensor.Output, "OUT");
    }

    /// <summary>An ADC0804 converting a pot's voltage, the result on eight LEDs: a /WR pulse starts it, /RD shows it.</summary>
    public static void LoadAdc0804(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "ADC0804 converter";
        var (supply, ground) = Rail(circuit, 5.0, -420, 0);
        var adc = Place(circuit, new Adc0804(), 0, 0);
        circuit.Connect(supply.Positive, adc.Vcc);
        circuit.Connect(adc.Gnd, ground.Pin);
        circuit.Connect(adc.AnalogGround, ground.Pin);
        circuit.Connect(adc.InputMinus, ground.Pin);
        circuit.Connect(adc.ChipSelect, ground.Pin);
        circuit.Connect(adc.Read, ground.Pin);
        var pot = Place(circuit, new Potentiometer(10e3, 0.3), -260, -60);
        circuit.Connect(pot.A, supply.Positive);
        circuit.Connect(pot.B, ground.Pin);
        circuit.Connect(pot.Wiper, adc.InputPlus);

        // /WR pulses ten times a second: each pulse starts a conversion, and with /RD held low the result shows.
        var start = Place(circuit, new PatternGenerator { Pattern = "0111111111", StepRate = 100, Repeats = true }, -260, -220);
        circuit.Connect(start.Terminals[0], adc.Write);
        for (var i = 0; i < 8; i++)
        {
            var led = Place(circuit, Led.OfColour("Red"), 260, -160 + (i * 40));
            var r = Place(circuit, new Resistor(1e3), 180, -160 + (i * 40));
            circuit.Connect(adc.Data[i], r.A);
            circuit.Connect(r.B, led.A);
            circuit.Connect(led.B, ground.Pin);
        }

        vm.Scope.TimebasePerDivision = 20e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(adc.Write, "/WR");
        vm.Scope.AddProbe(adc.InterruptOut, "/INTR");
        vm.Simulation.SpeedFactor = 1.0;
    }

    // ───── Signal Integrity & RF ─────

    /// <summary>A crystal radio's back end: an AM signal, a tuned LC tank, a 1N34A germanium detector and a high-impedance earpiece.</summary>
    public static void LoadCrystalRadio(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Crystal radio";
        var ground = Place(circuit, new Ground(), -200, 260);

        // The station: a 200 kHz carrier, 1 V, modulated 50 % at 1 kHz.
        var station = Place(circuit, new BehaviouralSource { Expression = "(1 + 0.5*sin(2*pi*1000*t)) * sin(2*pi*200000*t)" }, -440, 60);
        circuit.Connect(station.OutputNegative, ground.Pin);
        foreach (var input in new[] { station.A, station.B, station.C }) circuit.Connect(input, ground.Pin);
        var aerial = Place(circuit, new Resistor(1e3), -300, 0);
        circuit.Connect(station.OutputPositive, aerial.A);
        var coil = Place(circuit, new Inductor(633e-6), -160, 120);
        coil.RotationDegrees = 90;
        var tune = Place(circuit, new Capacitor(1e-9), -80, 120);
        tune.RotationDegrees = 90;
        circuit.Connect(aerial.B, coil.Terminals[0]);
        circuit.Connect(coil.Terminals[1], ground.Pin);
        circuit.Connect(aerial.B, tune.Terminals[0]);
        circuit.Connect(tune.Terminals[1], ground.Pin);
        var detector = Place(circuit, new Diode(DiodeModel.D1N34A), 40, 0);
        circuit.Connect(aerial.B, detector.Terminals[0]);
        Cap(circuit, detector.Terminals[1], ground.Pin, 2.2e-9, 160, 120);
        Between(circuit, detector.Terminals[1], ground.Pin, 47e3, 260, 120);

        vm.Scope.TimebasePerDivision = 0.5e-3;
        vm.Scope.VoltsPerDivision = 0.5;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(aerial.B, "Tank");
        vm.Scope.AddProbe(detector.Terminals[1], "Earpiece");
    }

    /// <summary>An NE602 mixing a 110 kHz signal with a 100 kHz oscillator; an RC filter on its output keeps the 10 kHz difference.</summary>
    public static void LoadSuperhetMixer(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "NE602 mixer";
        var (supply, ground) = Rail(circuit, 6.0, -460, -200);
        var mixer = Place(circuit, new Ne602(), 0, 0);
        circuit.Connect(mixer.Vcc, supply.Positive);
        circuit.Connect(mixer.Gnd, ground.Pin);
        var rf = Place(circuit, new FunctionGenerator(Waveform.Sine, 110e3, 0.02), -420, 100);
        circuit.Connect(rf.Return, ground.Pin);
        var c1 = Place(circuit, new Capacitor(100e-9), -260, 0);
        circuit.Connect(rf.Output, c1.Terminals[0]);
        circuit.Connect(c1.Terminals[1], mixer.InputA);
        Cap(circuit, mixer.InputB, ground.Pin, 100e-9, -200, 120);
        var lo = Place(circuit, new FunctionGenerator(Waveform.Sine, 100e3, 0.3), -420, 260);
        circuit.Connect(lo.Return, ground.Pin);
        var c3 = Place(circuit, new Capacitor(10e-9), -200, 220);
        circuit.Connect(lo.Output, c3.Terminals[0]);
        circuit.Connect(c3.Terminals[1], mixer.OscillatorBase);

        // A 16 kHz low-pass on OUT A: the 10 kHz difference through, the 210 kHz sum stopped.
        var r = Place(circuit, new Resistor(1e3), 220, -40);
        circuit.Connect(mixer.OutputA, r.A);
        Cap(circuit, r.B, ground.Pin, 10e-9, 320, 60);

        vm.Scope.TimebasePerDivision = 0.1e-3;
        vm.Scope.VoltsPerDivision = 0.05;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(mixer.InputA, "RF");
        vm.Scope.AddProbe(r.B, "IF");
    }

    /// <summary>An MC1496 balanced modulator: a 1 kHz tone times a 50 kHz carrier, double sideband with the carrier suppressed.</summary>
    public static void LoadBalancedModulator(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "MC1496 balanced modulator";
        var (plus, ground) = Rail(circuit, 12.0, -480, -300);
        var minus = Place(circuit, new DcVoltageSource(-8.0), -480, 160);
        circuit.Connect(minus.Negative, ground.Pin);
        var mod = Place(circuit, new Mc1496(), 0, 0);
        circuit.Connect(mod.NegativeSupply, minus.Positive);
        Between(circuit, ground.Pin, mod.Bias, 6.8e3, -100, 200);
        var tone = Place(circuit, new FunctionGenerator(Waveform.Sine, 1e3, 0.2), -340, 0);
        circuit.Connect(tone.Return, ground.Pin);
        circuit.Connect(tone.Output, mod.SignalPlus);
        circuit.Connect(mod.SignalMinus, ground.Pin);
        Between(circuit, mod.GainA, mod.GainB, 1e3, -120, -40, upright: false);
        var carrier = Place(circuit, new FunctionGenerator(Waveform.Sine, 50e3, 0.6), -340, 260);
        circuit.Connect(carrier.Return, ground.Pin);
        circuit.Connect(carrier.Output, mod.CarrierPlus);
        circuit.Connect(mod.CarrierMinus, ground.Pin);
        Between(circuit, plus.Positive, mod.OutputPlus, 3.9e3, 200, -140);
        Between(circuit, plus.Positive, mod.OutputMinus, 3.9e3, 280, -140);

        vm.Scope.TimebasePerDivision = 0.5e-3;
        vm.Scope.VoltsPerDivision = 0.5;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(tone.Output, "Signal");
        vm.Scope.AddProbe(mod.OutputMinus, "OUT-");
    }

    // ───── Digital Logic ─────

    /// <summary>A 74148 encoding eight buttons: the highest-numbered one pressed comes out, inverted, on three LEDs.</summary>
    public static void LoadPriorityEncoder(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "74148 priority encoder";
        var (supply, ground) = Rail(circuit, 5.0, -460, 0);
        var enc = Place(circuit, new Ic74148(), 0, 0);
        circuit.Connect(supply.Positive, enc.Vcc);
        circuit.Connect(enc.Gnd, ground.Pin);
        circuit.Connect(enc.Pin("EI"), ground.Pin);
        for (var i = 0; i < 8; i++)
        {
            var name = i.ToString(CultureInfo.InvariantCulture);
            Between(circuit, supply.Positive, enc.Pin(name), 10e3, -300 + (i * 30), -240);
            var button = Place(circuit, new PushButton { IsPressed = i is 2 or 5 }, -300 + (i * 30), 200);
            button.RotationDegrees = 90;
            circuit.Connect(enc.Pin(name), button.Terminals[0]);
            circuit.Connect(button.Terminals[1], ground.Pin);
        }

        foreach (var (pin, y) in new[] { ("A0", -60), ("A1", 0), ("A2", 60), ("GS", 120) })
        {
            var led = Place(circuit, Led.OfColour("Green"), 320, y);
            Between(circuit, supply.Positive, led.A, 470, 220, y, upright: false);
            circuit.Connect(led.B, enc.Pin(pin));
        }

        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(enc.Pin("A0"), "A0");
        vm.Scope.AddProbe(enc.Pin("A2"), "A2");
    }

    /// <summary>A 74688 address decoder: the byte on eight switches compared with A7-A0 from a counter, P=Q lighting an LED at 0x5A.</summary>
    public static void LoadAddressDecoder(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "74688 address decoder";
        var (supply, ground) = Rail(circuit, 5.0, -520, 0);
        var cmp = Place(circuit, new Ic74688(), 140, 0);
        circuit.Connect(supply.Positive, cmp.Vcc);
        circuit.Connect(cmp.Gnd, ground.Pin);
        circuit.Connect(cmp.Pin("G"), ground.Pin);
        var counter = Place(circuit, new Ic74393(), -200, 0);
        circuit.Connect(supply.Positive, counter.Vcc);
        circuit.Connect(counter.Gnd, ground.Pin);
        var clock = Place(circuit, new ClockSource(1e3), -420, -160);
        circuit.Connect(clock.Out, counter.TerminalByName("1CLK"));
        circuit.Connect(counter.TerminalByName("1QD"), counter.TerminalByName("2CLK"));
        circuit.Connect(counter.TerminalByName("1CLR"), ground.Pin);
        circuit.Connect(counter.TerminalByName("2CLR"), ground.Pin);
        string[] q = ["1QA", "1QB", "1QC", "1QD", "2QA", "2QB", "2QC", "2QD"];
        for (var i = 0; i < 8; i++)
        {
            circuit.Connect(counter.TerminalByName(q[i]), cmp.Pin($"P{i}"));
            circuit.Connect(cmp.Pin($"Q{i}"), (0x5A & (1 << i)) != 0 ? supply.Positive : ground.Pin);
        }

        var led = Place(circuit, Led.OfColour("Red"), 400, 0);
        Between(circuit, supply.Positive, led.A, 470, 320, -100);
        circuit.Connect(led.B, cmp.Pin("P=Q"));
        vm.Scope.TimebasePerDivision = 50e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.AddProbe(cmp.Pin("P=Q"), "/P=Q");
    }

    // ───── Counting & Arithmetic ─────

    /// <summary>A 74279 debouncing a bouncing changeover switch: the latch's Q steps once however much the contact chatters.</summary>
    public static void LoadDebouncedSwitch(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "74279 switch debouncer";
        var (supply, ground) = Rail(circuit, 5.0, -460, 0);
        var latch = Place(circuit, new Ic74279(), 0, 0);
        circuit.Connect(supply.Positive, latch.Vcc);
        circuit.Connect(latch.Gnd, ground.Pin);

        // The contact: resting on R (/R low), across (both high), bouncing onto S, resting there, and back, bouncing onto R.
        const string s = "1111 11 0101 0000 0000 11 1111 1111";
        const string r = "0000 11 1111 1111 1111 11 0101 0000";
        var contact = Place(circuit, new PatternGenerator { Pattern = $"{s}\n{r}", StepRate = 2e3, Repeats = true }, -300, -100);
        circuit.Connect(contact.Terminals[0], latch.Pin("2S"));
        circuit.Connect(contact.Terminals[1], latch.Pin("2R"));
        foreach (var p in new[] { "1R", "1S1", "1S2", "3R", "3S1", "3S2", "4R", "4S" }) circuit.Connect(latch.Pin(p), supply.Positive);
        vm.Scope.TimebasePerDivision = 2e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(latch.Pin("2S"), "/S");
        vm.Scope.AddProbe(latch.Pin("2R"), "/R");
        vm.Scope.AddProbe(latch.Pin("2Q"), "Q");
    }

    /// <summary>A GAL16V8 programmed as a 2-bit counter and its carry, from equations: registered outputs on pin 1's clock.</summary>
    public static void LoadGalCounter(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "GAL16V8 counter";
        var (supply, ground) = Rail(circuit, 5.0, -420, 0);
        var gal = Place(circuit, new Gal16v8
        {
            Equations = "; A 2-bit counter, and a carry while it's at 3\npin 2 = EN\npin 19 = Q0\npin 18 = Q1\npin 17 = CARRY\nQ0.R = Q0 ^ EN\nQ1.R = Q1 ^ (Q0 & EN)\nCARRY = Q0 & Q1",
        }, 0, 0);
        circuit.Connect(supply.Positive, gal.Vcc);
        circuit.Connect(gal.Gnd, ground.Pin);
        var clock = Place(circuit, new ClockSource(1e3), -260, -140);
        circuit.Connect(clock.Out, gal.Terminals[0]);
        circuit.Connect(gal.Terminals[1], supply.Positive);           // EN
        circuit.Connect(gal.Terminals[10], ground.Pin);               // /OE
        foreach (var p in new[] { 2, 3, 4, 5, 6, 7, 8 }) circuit.Connect(gal.Terminals[p], ground.Pin);
        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(gal.Terminals[18], "Q0");
        vm.Scope.AddProbe(gal.Terminals[17], "Q1");
        vm.Scope.AddProbe(gal.Terminals[16], "CARRY");
    }

    // ───── Displays ─────

    /// <summary>The Nokia 5110 written over SPI: set up, then a word's columns on bank 2, with a pattern generator on D/C.</summary>
    public static void LoadNokia5110(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Nokia 5110 LCD";
        var (supply, ground) = Rail(circuit, 3.3, -400, 0);
        var lcd = Place(circuit, new Pcd8544(), 160, 40);
        circuit.Connect(lcd.Vcc, supply.Positive);
        circuit.Connect(lcd.Gnd, ground.Pin);
        circuit.Connect(lcd.Reset, supply.Positive);
        // Extended set, contrast, bias; basic set, normal display; Y = 2, X = 30; then "CIRQ".
        var master = Place(circuit, new SpiMaster { Transactions = "21 BF 04 14 20 0C 42 9E; wait 30us; " + Hex(OledWord), StartDelay = 10e-6 }, -260, -200);
        circuit.Connect(master.Vcc, supply.Positive);
        circuit.Connect(master.Gnd, ground.Pin);
        circuit.Connect(master.ChipSelect, lcd.ChipEnable);
        circuit.Connect(master.Clock, lcd.Clock);
        circuit.Connect(master.MasterOut, lcd.DataIn);
        var dc = Place(circuit, new PatternGenerator { Pattern = "0111111111", StepRate = 10e3, Repeats = false }, -260, 120);
        circuit.Connect(dc.Terminals[0], lcd.DataCommand);
        vm.Scope.TimebasePerDivision = 50e-6;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(lcd.DataCommand, "D/C");
        vm.Scope.AddProbe(lcd.DataIn, "DIN");
    }

    /// <summary>A TIL311 showing the count of a 74193 driven by a 2 Hz clock: 0 to F.</summary>
    public static void LoadHexDisplay(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "TIL311 hex display";
        var (supply, ground) = Rail(circuit, 5.0, -400, 0);
        var counter = Place(circuit, new Ic74193(), -100, 0);
        circuit.Connect(supply.Positive, counter.Vcc);
        circuit.Connect(counter.Gnd, ground.Pin);
        var clock = Place(circuit, new ClockSource(2.0), -320, -160);
        circuit.Connect(clock.Out, counter.Up);
        circuit.Connect(counter.Down, supply.Positive);
        circuit.Connect(counter.Load, supply.Positive);
        circuit.Connect(counter.Clear, ground.Pin);
        foreach (var d in counter.Data) circuit.Connect(d, ground.Pin);
        var hex = Place(circuit, new Til311(), 160, 0);
        circuit.Connect(hex.Vcc, supply.Positive);
        circuit.Connect(hex.LedSupply, supply.Positive);
        circuit.Connect(hex.Gnd, ground.Pin);
        circuit.Connect(hex.Strobe, ground.Pin);
        circuit.Connect(hex.Blank, ground.Pin);
        for (var i = 0; i < 4; i++) circuit.Connect(counter.Outputs[i], hex.Data[i]);
        vm.Scope.TimebasePerDivision = 1.0;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(counter.Outputs[0], "A");
        vm.Scope.AddProbe(counter.Outputs[3], "D");
        vm.Simulation.SpeedFactor = 1.0;
    }

    /// <summary>
    /// A four-digit multiplexed display showing 1234: three pattern generators, two for the segments and one for a 74139's
    /// select, step through the digits at 2 kHz, the 74139's active-low outputs sinking each digit's common cathode in turn.
    /// </summary>
    public static void LoadMultiplexedDisplay(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Multiplexed display";
        var (supply, ground) = Rail(circuit, 5.0, -520, 0);
        var display = Place(circuit, new QuadSevenSegment(), 260, 0);
        var decoder = Place(circuit, new Ic74139(), 0, 220);
        circuit.Connect(supply.Positive, decoder.Vcc);
        circuit.Connect(decoder.Gnd, ground.Pin);
        circuit.Connect(decoder.TerminalByName("1G"), ground.Pin);
        circuit.Connect(decoder.TerminalByName("2G"), supply.Positive);
        circuit.Connect(decoder.TerminalByName("2A"), ground.Pin);
        circuit.Connect(decoder.TerminalByName("2B"), ground.Pin);

        // The segments for 1, 2, 3, 4 (A in bit 0), a digit to a step; the select counts 0-3 alongside.
        int[] glyphs = [0x06, 0x5B, 0x4F, 0x66];
        string Line(int bit) => string.Concat(glyphs.Select(g => ((g >> bit) & 1) != 0 ? '1' : '0'));
        var low = Place(circuit, new PatternGenerator { Pattern = string.Join("\n", Enumerable.Range(0, 4).Select(Line)), StepRate = 2e3 }, -300, -200);
        var high = Place(circuit, new PatternGenerator { Pattern = string.Join("\n", Enumerable.Range(4, 3).Select(Line)) + "\n0000", StepRate = 2e3 }, -300, -40);
        var select = Place(circuit, new PatternGenerator { Pattern = "0101\n0011", StepRate = 2e3 }, -300, 200);
        string[] segments = ["A", "B", "C", "D", "E", "F", "G", "DP"];
        for (var s = 0; s < 8; s++)
        {
            var source = s < 4 ? low.Terminals[s] : high.Terminals[s - 4];
            Between(circuit, source, display.Segment(segments[s]), 220, 60, -260 + (s * 30), upright: false);
        }

        circuit.Connect(select.Terminals[0], decoder.TerminalByName("1A"));
        circuit.Connect(select.Terminals[1], decoder.TerminalByName("1B"));
        for (var d = 0; d < 4; d++) circuit.Connect(decoder.TerminalByName($"1Y{d}"), display.Digit(d));
        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(display.Digit(0), "D1");
        vm.Scope.AddProbe(display.Digit(1), "D2");
    }

    // ───── Modules ─────

    /// <summary>A BME280 read by the I2C master: its ID, a forced measurement, the raw pressure, temperature and humidity.</summary>
    public static void LoadWeatherStation(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "BME280 weather station";
        var (supply, ground) = Rail(circuit, 3.3, -400, 0);
        var master = Place(circuit, new I2cMaster { Transactions = "w 76 D0; r 76 1; w 76 F2 01 F4 25; wait 10ms; w 76 F7; r 76 8", ClockFrequency = 100e3, StartDelay = 50e-6 }, -300, -200);
        var sensor = Place(circuit, new Bme280 { Temperature = 18.5, Pressure = 100_890, Humidity = 72 }, 120, 0);
        Bus(circuit, supply.Positive, ground.Pin, master);
        Bus(circuit, supply.Positive, ground.Pin, sensor);
        circuit.Connect(master.Sda, sensor.Sda);
        circuit.Connect(master.Scl, sensor.Scl);
        Between(circuit, supply.Positive, master.Sda, 4.7e3, -100, -140);
        Between(circuit, supply.Positive, master.Scl, 4.7e3, -40, -140);
        vm.Scope.TimebasePerDivision = 2e-3;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(master.Scl, "SCL");
        vm.Scope.AddProbe(master.Sda, "SDA");
    }

    /// <summary>A PCA9685 at 50 Hz moving a servo: asleep for PRE_SCALE 121, awake, then channel 0 high for 307 of 4096 counts (1.5 ms).</summary>
    public static void LoadServoDriver(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "PCA9685 servo driver";
        var (supply, ground) = Rail(circuit, 5.0, -400, 0);
        var master = Place(circuit, new I2cMaster { Transactions = "w 40 00 10; w 40 FE 79; w 40 00 20; w 40 06 00 00 33 01", ClockFrequency = 400e3, StartDelay = 50e-6 }, -300, -220);
        var pwm = Place(circuit, new Pca9685(), 60, 0);
        Bus(circuit, supply.Positive, ground.Pin, master);
        Bus(circuit, supply.Positive, ground.Pin, pwm);
        circuit.Connect(master.Sda, pwm.Sda);
        circuit.Connect(master.Scl, pwm.Scl);
        circuit.Connect(pwm.OutputEnable, ground.Pin);
        Between(circuit, supply.Positive, master.Sda, 4.7e3, -100, -160);
        Between(circuit, supply.Positive, master.Scl, 4.7e3, -40, -160);
        var servo = Place(circuit, new Servo(), 300, -100);
        circuit.Connect(servo.TerminalByName("SIG"), pwm.Outputs[0]);
        circuit.Connect(servo.TerminalByName("V+"), supply.Positive);
        circuit.Connect(servo.TerminalByName("GND"), ground.Pin);
        vm.Scope.TimebasePerDivision = 5e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.AddProbe(pwm.Outputs[0], "PWM0");
    }

    /// <summary>An MCP23017: port A as outputs lighting four LEDs with 0x05, port B read back with its pull-ups and a switch on GPB0.</summary>
    public static void LoadIoExpander(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "MCP23017 I/O expander";
        var (supply, ground) = Rail(circuit, 5.0, -420, 0);
        var master = Place(circuit, new I2cMaster { Transactions = "w 20 00 F0; w 20 14 05; w 20 0D FF; w 20 13; r 20 1", ClockFrequency = 400e3, StartDelay = 50e-6 }, -300, -240);
        var io = Place(circuit, new Mcp23017(), 60, 0);
        Bus(circuit, supply.Positive, ground.Pin, master);
        Bus(circuit, supply.Positive, ground.Pin, io);
        circuit.Connect(master.Sda, io.Sda);
        circuit.Connect(master.Scl, io.Scl);
        circuit.Connect(io.Reset, supply.Positive);
        Between(circuit, supply.Positive, master.Sda, 4.7e3, -100, -180);
        Between(circuit, supply.Positive, master.Scl, 4.7e3, -40, -180);
        for (var i = 0; i < 4; i++)
        {
            var led = Place(circuit, Led.OfColour("Yellow"), 300, -120 + (i * 40));
            Between(circuit, io.PortA[i], led.A, 330, 220, -120 + (i * 40), upright: false);
            circuit.Connect(led.B, ground.Pin);
        }

        var button = Place(circuit, new PushButton { IsPressed = true }, -160, 160);
        button.RotationDegrees = 90;
        circuit.Connect(io.PortB[0], button.Terminals[0]);
        circuit.Connect(button.Terminals[1], ground.Pin);
        vm.Scope.TimebasePerDivision = 0.2e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(master.Sda, "SDA");
        vm.Scope.AddProbe(io.PortA[0], "GPA0");
    }

    /// <summary>Two MCP2515s on a CAN bus at 125 kbit/s: one sends 0x123 with three bytes, the other acknowledges and stores it.</summary>
    public static void LoadCanNetwork(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "MCP2515 CAN network";
        var (supply, ground) = Rail(circuit, 5.0, -560, 0);
        const string setup = "C0; wait 50us; 02 28 05 B1 01; 02 2B 01; 02 60 60; 02 0F 00";
        Mcp2515 Node(string script, double y)
        {
            var chip = Place(circuit, new Mcp2515(), -100, y);
            var master = Place(circuit, new SpiMaster { Transactions = script }, -380, y);
            var transceiver = Place(circuit, new CanTransceiver(), 180, y);
            foreach (var (vcc, gnd) in new[] { (chip.Vcc, chip.Gnd), (master.Vcc, master.Gnd), (transceiver.Vcc, transceiver.Gnd) })
            {
                circuit.Connect(vcc, supply.Positive);
                circuit.Connect(gnd, ground.Pin);
            }

            circuit.Connect(chip.Reset, supply.Positive);
            circuit.Connect(master.ChipSelect, chip.ChipSelect);
            circuit.Connect(master.Clock, chip.Clock);
            circuit.Connect(master.MasterOut, chip.Mosi);
            circuit.Connect(master.MasterIn, chip.Miso);
            circuit.Connect(chip.TxCan, transceiver.TransmitIn);
            circuit.Connect(transceiver.ReceiveOut, chip.RxCan);
            return chip;
        }

        var sender = Node(setup + "; 40 24 60 00 00 03 DE AD 42; 81", -220);
        var receiver = Node(setup, 220);
        var a = circuit.Components.OfType<CanTransceiver>().ToList();
        circuit.Connect(a[0].High, a[1].High);
        circuit.Connect(a[0].Low, a[1].Low);
        Between(circuit, a[0].High, a[0].Low, 120, 360, -220);
        Between(circuit, a[1].High, a[1].Low, 120, 360, 220);
        vm.Scope.TimebasePerDivision = 0.2e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(sender.TxCan, "TX (sender)");
        vm.Scope.AddProbe(receiver.TxCan, "TX (receiver: its ACK)");
    }

    /// <summary>A MAX6675 read over SPI, its type-K thermocouple at 300 °C: one 16-bit word, the temperature in quarter degrees.</summary>
    public static void LoadThermocoupleMeter(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "MAX6675 thermocouple";
        var (supply, ground) = Rail(circuit, 5.0, -400, 0);
        var tc = Place(circuit, new Thermocouple { Temperature = 300, ColdJunctionTemperature = 25 }, -100, 160);
        var chip = Place(circuit, new Max6675(), 60, 0);
        circuit.Connect(chip.Vcc, supply.Positive);
        circuit.Connect(chip.Gnd, ground.Pin);
        circuit.Connect(tc.Terminals[0], chip.ThermocouplePlus);
        circuit.Connect(tc.Terminals[1], chip.ThermocoupleMinus);
        circuit.Connect(chip.ThermocoupleMinus, ground.Pin);
        var master = Place(circuit, new SpiMaster { Transactions = "00 00; wait 200us; 00 00" }, -260, -200);
        circuit.Connect(master.Vcc, supply.Positive);
        circuit.Connect(master.Gnd, ground.Pin);
        circuit.Connect(master.ChipSelect, chip.ChipSelect);
        circuit.Connect(master.Clock, chip.Clock);
        circuit.Connect(master.MasterIn, chip.SerialData);
        vm.Scope.TimebasePerDivision = 20e-6;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(chip.Clock, "SCK");
        vm.Scope.AddProbe(chip.SerialData, "SO");
    }

    /// <summary>A relay module switching a 12 V lamp, its IN driven low by a 2 Hz clock: the click comes 10 ms after the pin.</summary>
    public static void LoadRelayModule(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Relay module";
        var (logic, ground) = Rail(circuit, 5.0, -400, 0);
        var (twelve, g2) = Rail(circuit, 12.0, 360, -260);
        var relay = Place(circuit, new RelayModule(), 0, 0);
        circuit.Connect(relay.Vcc, logic.Positive);
        circuit.Connect(relay.Gnd, ground.Pin);
        var clock = Place(circuit, new ClockSource(2.0), -260, -140);
        circuit.Connect(clock.Out, relay.Input);
        circuit.Connect(relay.Common, twelve.Positive);
        var lamp = Place(circuit, new IncandescentLamp(), 240, 0);
        circuit.Connect(relay.NormallyOpen, lamp.Terminals[0]);
        circuit.Connect(lamp.Terminals[1], g2.Pin);
        vm.Scope.TimebasePerDivision = 0.1;
        vm.Scope.VoltsPerDivision = 5.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(relay.Input, "IN");
        vm.Scope.AddProbe(relay.NormallyOpen, "NO");
        vm.Simulation.SpeedFactor = 1.0;
    }

    /// <summary>An MT3608 boost module making 12 V at 200 mA from 5 V, and taking half an amp to do it.</summary>
    public static void LoadBoostConverter(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "MT3608 boost module";
        var (supply, ground) = Rail(circuit, 5.0, -320, 0);
        var boost = Place(circuit, new BoostModule { OutputVoltage = 12 }, 0, 0);
        circuit.Connect(supply.Positive, boost.Input);
        circuit.Connect(boost.Ground, ground.Pin);
        Between(circuit, boost.Output, ground.Pin, 60, 160, 80);
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.AddProbe(boost.Output, "VOUT");
    }

    /// <summary>A TB6612FNG running a motor forwards at a 75 % PWM duty from a pattern generator, then braking it.</summary>
    public static void LoadTb6612Motor(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "TB6612FNG motor driver";
        var (logic, ground) = Rail(circuit, 5.0, -460, 0);
        var (vm9, g2) = Rail(circuit, 9.0, 360, -300);
        var drv = Place(circuit, new Tb6612Module(), 0, 0);
        var t = drv.Terminals;
        circuit.Connect(t[0], vm9.Positive);
        circuit.Connect(t[1], logic.Positive);
        circuit.Connect(t[2], ground.Pin);
        circuit.Connect(g2.Pin, ground.Pin);
        circuit.Connect(t[10], logic.Positive);         // STBY
        circuit.Connect(t[9], logic.Positive);          // AIN1
        circuit.Connect(t[8], ground.Pin);              // AIN2
        var pwm = Place(circuit, new PatternGenerator { Pattern = "1110", StepRate = 20e3 }, -260, -100);
        circuit.Connect(pwm.Terminals[0], t[7]);
        foreach (var i in new[] { 11, 12, 13 }) circuit.Connect(t[i], ground.Pin);
        var motor = Place(circuit, new DcMotor(), 220, -40);
        circuit.Connect(t[3], motor.Terminals[0]);
        circuit.Connect(t[4], motor.Terminals[1]);
        vm.Scope.TimebasePerDivision = 50e-6;
        vm.Scope.VoltsPerDivision = 5.0;
        vm.Scope.AddProbe(t[3], "AO1");
    }

    /// <summary>A DAC0808 driven by a 74393's count, its IOUT into an op-amp's virtual ground: a 256-step staircase from 0 to 10 V.</summary>
    public static void LoadDacStaircase(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "DAC0808 staircase";
        var (five, ground) = Rail(circuit, 5.0, -600, -200);
        var plus = Place(circuit, new DcVoltageSource(15.0), -600, 100);
        var minus = Place(circuit, new DcVoltageSource(-15.0), -600, 300);
        circuit.Connect(plus.Negative, ground.Pin);
        circuit.Connect(minus.Negative, ground.Pin);
        var counter = Place(circuit, new Ic74393(), -300, 0);
        circuit.Connect(five.Positive, counter.Vcc);
        circuit.Connect(counter.Gnd, ground.Pin);
        var clock = Place(circuit, new ClockSource(25.6e3), -460, -180);
        circuit.Connect(clock.Out, counter.TerminalByName("1CLK"));
        circuit.Connect(counter.TerminalByName("1QD"), counter.TerminalByName("2CLK"));
        circuit.Connect(counter.TerminalByName("1CLR"), ground.Pin);
        circuit.Connect(counter.TerminalByName("2CLR"), ground.Pin);
        var dac = Place(circuit, new Dac0808(), 0, 0);
        circuit.Connect(dac.Vcc, five.Positive);
        circuit.Connect(dac.Gnd, ground.Pin);
        circuit.Connect(dac.Vee, minus.Positive);
        string[] q = ["2QD", "2QC", "2QB", "2QA", "1QD", "1QC", "1QB", "1QA"];
        for (var i = 0; i < 8; i++) circuit.Connect(counter.TerminalByName(q[i]), dac.Inputs[i]);
        var reference = Place(circuit, new DcVoltageSource(10.0), -160, -260);
        circuit.Connect(reference.Negative, ground.Pin);
        Between(circuit, reference.Positive, dac.ReferencePlus, 5e3, -40, -200, upright: false);
        Between(circuit, dac.ReferenceMinus, ground.Pin, 5e3, 60, -160);
        var amp = Place(circuit, new OperationalAmplifier(OpAmpModel.Tl081), 240, 40);
        circuit.Connect(amp.PositiveSupply, plus.Positive);
        circuit.Connect(amp.NegativeSupply, minus.Positive);
        circuit.Connect(amp.NonInverting, ground.Pin);
        circuit.Connect(amp.Inverting, dac.Output);
        Between(circuit, amp.Inverting, amp.Output, 5e3, 240, -80, upright: false);
        vm.Scope.TimebasePerDivision = 2e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.AddProbe(amp.Output, "Staircase");
    }
}
