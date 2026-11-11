using System.Globalization;
using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Processors;
using Cirq.Components.Sources;
using Cirq.Core.Topology;

namespace Cirq.UI.ViewModels;

/// <summary>The examples for the parts that came in 1.3.0.</summary>
public static partial class Examples
{
    /// <summary>The ATtiny85 blink as the guide prints it assembled: every fifteenth Timer0 overflow toggles PB0.</summary>
    public static readonly byte[] Attiny85BlinkFirmware =
    [
        0x05, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0xC0, 0x02, 0xE0, 0x0E, 0xBF, 0x0F, 0xE5, 0x0D, 0xBF,
        0xB8, 0x9A, 0x1F, 0xE0, 0x05, 0xE0, 0x03, 0xBF, 0x02, 0xE0, 0x09, 0xBF, 0x78, 0x94, 0xFF, 0xCF, 0x1A, 0x95, 0x11, 0xF4,
        0x1F, 0xE0, 0xB0, 0x9A, 0x18, 0x95,
    ];

    /// <summary>The AT89C2051's greeting as the guide prints it assembled: "HELLO 8051" at 9600 baud, then P1.0 low.</summary>
    public static readonly byte[] At89c2051HelloFirmware =
    [
        0x75, 0x81, 0x30, 0x75, 0x89, 0x20, 0x75, 0x8D, 0xFD, 0x75, 0x98, 0x50, 0xD2, 0x8E, 0x90, 0x00, 0x23, 0xE4, 0x93, 0x60,
        0x0A, 0xF5, 0x99, 0x30, 0x99, 0xFD, 0xC2, 0x99, 0xA3, 0x80, 0xF2, 0xC2, 0x90, 0x80, 0xFE, 0x48, 0x45, 0x4C, 0x4C, 0x4F,
        0x20, 0x38, 0x30, 0x35, 0x31, 0x0D, 0x0A, 0x00,
    ];

    /// <summary>The Uno's AY-3-8910 program as the guide prints it assembled: 440 Hz on channel A.</summary>
    public static readonly byte[] UnoAyToneFirmware =
    [
        0x08, 0xE0, 0x0E, 0xBF, 0x0F, 0xEF, 0x0D, 0xBF, 0x0F, 0xEF, 0x0A, 0xB9, 0x03, 0xE0, 0x07, 0xB9, 0x40, 0xE0, 0x5E, 0xE8,
        0x0A, 0xD0, 0x41, 0xE0, 0x50, 0xE0, 0x07, 0xD0, 0x47, 0xE0, 0x5E, 0xE3, 0x04, 0xD0, 0x48, 0xE0, 0x5F, 0xE0, 0x01, 0xD0,
        0xFF, 0xCF, 0x4B, 0xB9, 0x03, 0xE0, 0x08, 0xB9, 0x00, 0x27, 0x08, 0xB9, 0x5B, 0xB9, 0x02, 0xE0, 0x08, 0xB9, 0x00, 0x27,
        0x08, 0xB9, 0x08, 0x95,
    ];

    /// <summary>"CIRQ" in a 5 × 7 font, a byte to a column, bit 0 at the top: what the OLED example sends.</summary>
    private static readonly byte[] OledWord =
    [
        0x3E, 0x41, 0x41, 0x41, 0x22, 0x00, 0x00, 0x41, 0x7F, 0x41, 0x00, 0x00, 0x7F, 0x09, 0x19, 0x29, 0x46, 0x00, 0x3E, 0x41,
        0x51, 0x21, 0x5E,
    ];

    private static string Hex(IEnumerable<byte> bytes) =>
        string.Join(" ", bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    // ───── Fundamentals ─────

    /// <summary>An RGB LED mixed by three resistors, and a bicolour LED turned red and green by a square wave.</summary>
    public static void LoadRgbLedMixing(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "RGB LED mixing";
        var supply = Place(circuit, new DcVoltageSource(5.0), -320, 0);
        var ground = Place(circuit, new Ground(), -320, 140);
        var rgb = Place(circuit, new RgbLed(), 0, 0);
        var red = Place(circuit, new Resistor(150), -140, -20);
        var green = Place(circuit, new Resistor(2.2e3), -140, 0);
        var blue = Place(circuit, new Resistor(330), -140, 20);
        var ground2 = Place(circuit, new Ground(), 60, 100);
        circuit.Connect(supply.Negative, ground.Pin);
        foreach (var (resistor, pin) in new[] { (red, 0), (green, 1), (blue, 2) })
        {
            circuit.Connect(supply.Positive, resistor.A);
            circuit.Connect(resistor.B, rgb.Terminals[pin]);
        }

        circuit.Connect(rgb.Terminals[3], ground2.Pin);

        var square = Place(circuit, new FunctionGenerator(Waveform.Square, 1.0, 5.0), -320, 260);
        var feed = Place(circuit, new Resistor(220), -140, 260);
        var bicolour = Place(circuit, new BicolourLed(), 0, 260);
        var ground3 = Place(circuit, new Ground(), -320, 380);
        circuit.Connect(square.Return, ground3.Pin);
        circuit.Connect(square.Output, feed.A);
        circuit.Connect(feed.B, bicolour.Terminals[0]);
        circuit.Connect(bicolour.Terminals[1], ground3.Pin);

        vm.Scope.TimebasePerDivision = 0.2;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.AddProbe(feed.A, "Drive");
        vm.Simulation.SpeedFactor = 1.0;
    }

    // ───── Timers & Oscillators ─────

    /// <summary>An XR2206 at 1 kHz from 10 kΩ and 100 nF: the sine on STO and the square on SYNCO.</summary>
    public static void LoadXr2206Generator(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "XR2206 function generator";
        var supply = Place(circuit, new DcVoltageSource(12.0), -320, 0);
        var ground = Place(circuit, new Ground(), -320, 140);
        var chip = Place(circuit, new Xr2206(), 0, 0);
        var timingR = Place(circuit, new Resistor(10e3), -160, 160);
        var timingC = Place(circuit, new Capacitor(100e-9), -160, -100);
        var pullUp = Place(circuit, new Resistor(10e3), 160, -120);
        var load = Place(circuit, new Resistor(10e3), 200, 100);
        var ground2 = Place(circuit, new Ground(), 0, 220);
        timingR.RotationDegrees = 90;
        pullUp.RotationDegrees = 90;
        load.RotationDegrees = 90;

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, chip.Vcc);
        circuit.Connect(chip.Gnd, ground2.Pin);
        circuit.Connect(chip.TimingResistor, timingR.A);
        circuit.Connect(timingR.B, ground2.Pin);
        circuit.Connect(chip.Capacitor1, timingC.Terminals[0]);
        circuit.Connect(timingC.Terminals[1], chip.Capacitor2);
        circuit.Connect(supply.Positive, pullUp.A);
        circuit.Connect(pullUp.B, chip.Sync);
        circuit.Connect(chip.Output, load.A);
        circuit.Connect(load.B, ground2.Pin);

        vm.Scope.TimebasePerDivision = 0.5e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(chip.Output, "STO");
        vm.Scope.AddProbe(chip.Sync, "SYNCO");
    }

    /// <summary>An NE567 set to 909 Hz by 10 kΩ and 100 nF, hearing a tone at its frequency and lighting an LED.</summary>
    public static void LoadNe567ToneDecoder(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "NE567 tone decoder";
        var supply = Place(circuit, new DcVoltageSource(5.0), -380, -60);
        var ground = Place(circuit, new Ground(), -380, 80);
        var tone = Place(circuit, new FunctionGenerator(Waveform.Sine, 909, 0.3), -380, 200);
        var ground2 = Place(circuit, new Ground(), -380, 320);
        var coupling = Place(circuit, new Capacitor(100e-9), -220, 60);
        var chip = Place(circuit, new Ne567(), 0, 0);
        var timingR = Place(circuit, new Resistor(10e3), 160, -120);
        var timingC = Place(circuit, new Capacitor(100e-9), 200, 120);
        var loop = Place(circuit, new Capacitor(220e-9), -120, 160);
        var output = Place(circuit, new Capacitor(470e-9), -40, 160);
        var resistor = Place(circuit, new Resistor(1e3), 280, -120);
        var led = Place(circuit, Led.OfColour("Green"), 280, -40);
        var ground3 = Place(circuit, new Ground(), 0, 260);
        timingC.RotationDegrees = 90;
        loop.RotationDegrees = 90;
        output.RotationDegrees = 90;
        resistor.RotationDegrees = 90;
        led.RotationDegrees = 90;

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(tone.Return, ground2.Pin);
        circuit.Connect(tone.Output, coupling.Terminals[0]);
        circuit.Connect(coupling.Terminals[1], chip.Input);
        circuit.Connect(supply.Positive, chip.Vcc);
        circuit.Connect(chip.Gnd, ground3.Pin);
        circuit.Connect(chip.TimingR, timingR.A);
        circuit.Connect(timingR.B, chip.TimingC);
        circuit.Connect(chip.TimingC, timingC.Terminals[0]);
        circuit.Connect(timingC.Terminals[1], ground3.Pin);
        circuit.Connect(chip.LoopFilter, loop.Terminals[0]);
        circuit.Connect(loop.Terminals[1], ground3.Pin);
        circuit.Connect(chip.OutputFilter, output.Terminals[0]);
        circuit.Connect(output.Terminals[1], ground3.Pin);
        circuit.Connect(supply.Positive, resistor.A);
        circuit.Connect(resistor.B, led.A);
        circuit.Connect(led.B, chip.Output);

        vm.Scope.TimebasePerDivision = 5e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(chip.Input, "Tone");
        vm.Scope.AddProbe(chip.Output, "OUT");
    }

    /// <summary>A 556: its first timer at 69 Hz gating its second at 6.9 kHz through RESET2, so the tone comes in bursts.</summary>
    public static void LoadNe556Bursts(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "556 tone bursts";
        var supply = Place(circuit, new DcVoltageSource(5.0), -400, 0);
        var ground = Place(circuit, new Ground(), -400, 140);
        var timer = Place(circuit, new Ne556(), 0, 0);
        var ground2 = Place(circuit, new Ground(), 0, 200);
        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, timer.Vcc);
        circuit.Connect(timer.Gnd, ground2.Pin);
        circuit.Connect(supply.Positive, timer.TerminalByName("RESET1"));
        circuit.Connect(timer.TerminalByName("OUT1"), timer.TerminalByName("RESET2"));

        void Astable(string n, double x, double capacitance)
        {
            var ra = Place(circuit, new Resistor(1e3), x, -160);
            var rb = Place(circuit, new Resistor(10e3), x, -60);
            var c = Place(circuit, new Capacitor(capacitance), x, 120);
            ra.RotationDegrees = 90;
            rb.RotationDegrees = 90;
            c.RotationDegrees = 90;
            circuit.Connect(supply.Positive, ra.A);
            circuit.Connect(ra.B, timer.TerminalByName("DISCH" + n));
            circuit.Connect(ra.B, rb.A);
            circuit.Connect(rb.B, timer.TerminalByName("THRES" + n));
            circuit.Connect(timer.TerminalByName("THRES" + n), timer.TerminalByName("TRIG" + n));
            circuit.Connect(timer.TerminalByName("THRES" + n), c.Terminals[0]);
            circuit.Connect(c.Terminals[1], ground2.Pin);
        }

        Astable("1", -200, 1e-6);
        Astable("2", 200, 10e-9);
        var load = Place(circuit, new Resistor(1e3), 320, 120);
        load.RotationDegrees = 90;
        circuit.Connect(timer.TerminalByName("OUT2"), load.A);
        circuit.Connect(load.B, ground2.Pin);

        vm.Scope.TimebasePerDivision = 5e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(timer.TerminalByName("OUT1"), "OUT1");
        vm.Scope.AddProbe(timer.TerminalByName("OUT2"), "OUT2");
    }

    // ───── Displays ─────

    /// <summary>An LM3914 bar graph: ten LEDs at ten times REFOUT's current, the signal from a pot across the 1.25 V reference.</summary>
    public static void LoadLedBarGraph(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "LM3914 bar graph";
        var supply = Place(circuit, new DcVoltageSource(5.0), -400, 0);
        var ground = Place(circuit, new Ground(), -400, 140);
        var chip = Place(circuit, new Lm3914(), 0, 0);
        var reference = Place(circuit, new Resistor(1.2e3), -200, 160);
        var pot = Place(circuit, new Potentiometer(10e3, 0.55), -220, -20);
        var ground2 = Place(circuit, new Ground(), -120, 260);
        reference.RotationDegrees = 90;
        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, chip.Positive);
        circuit.Connect(chip.Negative, ground2.Pin);
        circuit.Connect(chip.Low, ground2.Pin);
        circuit.Connect(chip.ReferenceAdjust, ground2.Pin);
        circuit.Connect(chip.ReferenceOut, chip.High);
        circuit.Connect(chip.ReferenceOut, reference.A);
        circuit.Connect(reference.B, ground2.Pin);
        circuit.Connect(chip.ReferenceOut, pot.A);
        circuit.Connect(pot.B, ground2.Pin);
        circuit.Connect(pot.Wiper, chip.Signal);
        circuit.Connect(chip.Mode, supply.Positive);

        for (var i = 0; i < chip.Leds.Count; i++)
        {
            var led = Place(circuit, Led.OfColour(i < 6 ? "Green" : i < 8 ? "Yellow" : "Red"), 220, -180 + (i * 40));
            circuit.Connect(supply.Positive, led.A);
            circuit.Connect(led.B, chip.Leds[i]);
        }

        vm.Scope.VoltsPerDivision = 0.5;
        vm.Scope.AddProbe(chip.Signal, "SIG");
    }

    /// <summary>A Nixie counting: a 7490 counting a 2 Hz clock in BCD, a 74141 sinking the cathode it names, the tube on 180 V.</summary>
    public static void LoadNixieCounter(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Nixie counter";
        var logic = Place(circuit, new DcVoltageSource(5.0), -460, 0);
        var high = Place(circuit, new DcVoltageSource(180.0), 300, -260);
        var ground = Place(circuit, new Ground(), -460, 140);
        var ground2 = Place(circuit, new Ground(), 300, -120);
        var clock = Place(circuit, new ClockSource(2.0), -460, -160);
        var counter = Place(circuit, new Ic7490(), -240, 0);
        var driver = Place(circuit, new Ic74141(), 0, 0);
        var tube = Place(circuit, new NixieTube(), 320, 120);
        var anode = Place(circuit, new Resistor(22e3), 440, -60);
        var rail = Place(circuit, new Ground(), -240, 200);
        anode.RotationDegrees = 90;

        circuit.Connect(logic.Negative, ground.Pin);
        circuit.Connect(high.Negative, ground2.Pin);
        circuit.Connect(logic.Positive, counter.Vcc);
        circuit.Connect(logic.Positive, driver.Vcc);
        circuit.Connect(counter.Gnd, rail.Pin);
        circuit.Connect(driver.Gnd, rail.Pin);
        circuit.Connect(clock.Out, counter.ClockA);
        circuit.Connect(counter.Qa, counter.ClockB);
        foreach (var reset in new[] { counter.Reset0A, counter.Reset0B, counter.Reset9A, counter.Reset9B })
            circuit.Connect(reset, rail.Pin);
        circuit.Connect(counter.Qa, driver.A);
        circuit.Connect(counter.Qb, driver.B);
        circuit.Connect(counter.Qc, driver.C);
        circuit.Connect(counter.Qd, driver.D);
        for (var d = 0; d < 10; d++) circuit.Connect(driver.Outputs[d], tube.Cathode(d));
        circuit.Connect(high.Positive, anode.A);
        circuit.Connect(anode.B, tube.Anode);

        vm.Scope.TimebasePerDivision = 0.5;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(counter.Qa, "QA");
        vm.Scope.AddProbe(counter.Qd, "QD");
        vm.Simulation.SpeedFactor = 1.0;
    }

    /// <summary>An SSD1306 OLED written by the I2C master: set up, a frame round the edge and "CIRQ" in the middle.</summary>
    public static void LoadOledDisplay(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "SSD1306 OLED";
        var supply = Place(circuit, new DcVoltageSource(3.3), -360, 0);
        var ground = Place(circuit, new Ground(), -360, 140);
        var top = Enumerable.Repeat((byte)0x01, 128);
        var bottom = Enumerable.Repeat((byte)0x80, 128);
        var script = string.Join("; ",
            "w 3C 00 AE 20 00 8D 14 AF",                             // off, horizontal addressing, charge pump, on
            $"w 3C 00 21 00 7F 22 00 00; w 3C 40 {Hex(top)}",
            $"w 3C 00 21 00 7F 22 07 07; w 3C 40 {Hex(bottom)}",
            $"w 3C 00 21 34 4A 22 03 03; w 3C 40 {Hex(OledWord)}");
        var master = Place(circuit, new I2cMaster { Transactions = script, ClockFrequency = 400e3, StartDelay = 50e-6 }, -360, -200);
        var oled = Place(circuit, new Ssd1306(), 120, 40);
        var sdaPull = Place(circuit, new Resistor(4.7e3), -120, -160);
        var sclPull = Place(circuit, new Resistor(4.7e3), -60, -160);
        sdaPull.RotationDegrees = 90;
        sclPull.RotationDegrees = 90;
        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, master.Vcc);
        circuit.Connect(master.Gnd, ground.Pin);
        circuit.Connect(supply.Positive, oled.Vcc);
        circuit.Connect(oled.Gnd, ground.Pin);
        circuit.Connect(master.Sda, oled.Sda);
        circuit.Connect(master.Scl, oled.Scl);
        circuit.Connect(supply.Positive, sdaPull.A);
        circuit.Connect(sdaPull.B, master.Sda);
        circuit.Connect(supply.Positive, sclPull.A);
        circuit.Connect(sclPull.B, master.Scl);

        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(master.Scl, "SCL");
        vm.Scope.AddProbe(master.Sda, "SDA");
    }

    /// <summary>A MAX7219 8 × 8 matrix written by the SPI master: on, full scan, mid brightness, then a face a row at a time.</summary>
    public static void LoadLedMatrix(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "MAX7219 LED matrix";
        var supply = Place(circuit, new DcVoltageSource(5.0), -360, 0);
        var ground = Place(circuit, new Ground(), -360, 140);
        byte[] face = [0x3C, 0x42, 0xA5, 0x81, 0xA5, 0x99, 0x42, 0x3C];
        var rows = string.Join("; ", face.Select((b, i) => $"{i + 1:X2} {b:X2}"));
        var master = Place(circuit, new SpiMaster { Transactions = $"0F 00; 0C 01; 0B 07; 0A 08; {rows}" }, -300, -160);
        var matrix = Place(circuit, new Max7219Matrix(), 120, 0);
        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, master.Vcc);
        circuit.Connect(master.Gnd, ground.Pin);
        circuit.Connect(supply.Positive, matrix.Vcc);
        circuit.Connect(matrix.Gnd, ground.Pin);
        circuit.Connect(master.MasterOut, matrix.DataIn);
        circuit.Connect(master.Clock, matrix.Clock);
        circuit.Connect(master.ChipSelect, matrix.Load);

        vm.Scope.TimebasePerDivision = 50e-6;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(master.ChipSelect, "CS");
        vm.Scope.AddProbe(master.MasterOut, "DIN");
    }

    /// <summary>A TM1637 four-digit display showing 12:34, its two wires played by a pattern generator.</summary>
    public static void LoadTm1637Display(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "TM1637 display";
        var supply = Place(circuit, new DcVoltageSource(5.0), -360, 0);
        var ground = Place(circuit, new Ground(), -360, 140);

        // Data mode, then 1 2 3 4 from address C0 (the 2 with the colon), then the display on at full brightness.
        var pattern = Tm1637Display.PatternFor([0x40], [0xC0, 0x06, 0xDB, 0x4F, 0x66], [0x8F]);
        var bus = Place(circuit, new PatternGenerator { Pattern = pattern, StepRate = 100e3, Repeats = false, StartDelay = 50e-6 }, -260, -120);
        var display = Place(circuit, new Tm1637Display(), 120, 0);
        var pullUp = Place(circuit, new Resistor(10e3), -60, -160);
        pullUp.RotationDegrees = 90;
        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, display.Vcc);
        circuit.Connect(display.Gnd, ground.Pin);
        circuit.Connect(bus.Terminals[0], display.Clock);
        circuit.Connect(bus.Terminals[1], display.Dio);
        circuit.Connect(supply.Positive, pullUp.A);
        circuit.Connect(pullUp.B, display.Dio);

        vm.Scope.TimebasePerDivision = 0.2e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(display.Clock, "CLK");
        vm.Scope.AddProbe(display.Dio, "DIO");
    }

    // ───── Audio ─────

    /// <summary>Half a 12AX7 as a common-cathode stage: 250 V through 100 kΩ, 1.5 kΩ bypassed, a gain of about 60.</summary>
    public static void LoadValvePreamp(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "12AX7 preamp";
        var supply = Place(circuit, new DcVoltageSource(250.0), 260, -300);
        var ground = Place(circuit, new Ground(), 260, -160);
        var input = Place(circuit, new FunctionGenerator(Waveform.Sine, 1e3, 0.1), -400, 60);
        var ground2 = Place(circuit, new Ground(), -400, 200);
        var coupling = Place(circuit, new Capacitor(100e-9), -240, 0);
        var gridLeak = Place(circuit, new Resistor(1e6), -120, 120);
        var valve = Place(circuit, new Triode(), 0, 0);
        var plate = Place(circuit, new Resistor(100e3), 0, -140);
        var cathode = Place(circuit, new Resistor(1.5e3), -40, 140);
        var bypass = Place(circuit, new ElectrolyticCapacitor(22e-6), 60, 140);
        var output = Place(circuit, new Capacitor(100e-9), 140, -60);
        var load = Place(circuit, new Resistor(1e6), 240, 60);
        var ground3 = Place(circuit, new Ground(), 0, 260);
        foreach (var part in new CircuitComponent[] { gridLeak, plate, cathode, bypass, load }) part.RotationDegrees = 90;

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(input.Return, ground2.Pin);
        circuit.Connect(input.Output, coupling.Terminals[0]);
        circuit.Connect(coupling.Terminals[1], valve.Grid);
        circuit.Connect(valve.Grid, gridLeak.A);
        circuit.Connect(gridLeak.B, ground3.Pin);
        circuit.Connect(supply.Positive, plate.A);
        circuit.Connect(plate.B, valve.Plate);
        circuit.Connect(valve.Cathode, cathode.A);
        circuit.Connect(cathode.B, ground3.Pin);
        circuit.Connect(valve.Cathode, bypass.Terminals[0]);
        circuit.Connect(bypass.Terminals[1], ground3.Pin);
        circuit.Connect(valve.Plate, output.Terminals[0]);
        circuit.Connect(output.Terminals[1], load.A);
        circuit.Connect(load.B, ground3.Pin);

        vm.Scope.TimebasePerDivision = 0.5e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(input.Output, "In");
        vm.Scope.AddProbe(load.A, "Out");
    }

    /// <summary>An Arduino Uno writing an AY-3-8910's registers over its bus: 440 Hz on channel A.</summary>
    public static void LoadUnoAyTone(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Arduino Uno and AY-3-8910";
        var uno = Place(circuit, new ArduinoUnoFirmware(), -260, 0);
        uno.Load(MemoryImage.FromBinary(UnoAyToneFirmware));
        var ay = Place(circuit, new Ay38910(), 200, 0);
        var load = Place(circuit, new Resistor(1e3), 400, 160);
        var ground = Place(circuit, new Ground(), 200, 300);
        load.RotationDegrees = 90;
        var five = uno.TerminalByName("5V");
        circuit.Connect(uno.TerminalByName("GND"), ground.Pin);
        circuit.Connect(five, ay.Vcc);
        circuit.Connect(ay.Gnd, ground.Pin);
        circuit.Connect(five, ay.Bc2);
        circuit.Connect(five, ay.Reset);
        circuit.Connect(uno.TerminalByName("A0"), ay.Bc1);
        circuit.Connect(uno.TerminalByName("A1"), ay.Bdir);
        for (var i = 0; i < 8; i++)
        {
            var name = $"D{i}";
            circuit.Connect(uno.Terminals.First(t => t.Name == name || t.Name.StartsWith(name + "/", StringComparison.Ordinal)), ay.Data[i]);
        }
        circuit.Connect(ay.ChannelA, load.A);
        circuit.Connect(load.B, ground.Pin);

        vm.Scope.TimebasePerDivision = 2e-3;
        vm.Scope.VoltsPerDivision = 0.2;
        vm.Scope.AddProbe(ay.ChannelA, "CHA");
    }

    // ───── Measurement ─────

    /// <summary>A TSOP38238 putting out an NEC remote's frame: 9 ms low, 4.5 ms high, 32 bits in the spaces.</summary>
    public static void LoadIrRemoteFrame(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "IR remote frame";
        var supply = Place(circuit, new DcVoltageSource(5.0), -260, 0);
        var ground = Place(circuit, new Ground(), -260, 140);
        var receiver = Place(circuit, new IrReceiver { Address = 0x00, Command = 0x45, SendAt = 5e-3 }, 0, 0);
        var ground2 = Place(circuit, new Ground(), 0, 120);
        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, receiver.Vcc);
        circuit.Connect(receiver.Gnd, ground2.Pin);

        vm.Scope.TimebasePerDivision = 10e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.AddProbe(receiver.Out, "OUT");
    }

    /// <summary>An AD9833 told 1 kHz over SPI: B28 and reset, the two halves of FREQ0, then out of reset as a sine.</summary>
    public static void LoadDdsGenerator(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "AD9833 DDS generator";
        var supply = Place(circuit, new DcVoltageSource(5.0), -360, 0);
        var ground = Place(circuit, new Ground(), -360, 140);
        var master = Place(circuit, new SpiMaster { Transactions = "21 00; 69 F1; 40 00; 00 00" }, -300, -160);
        var dds = Place(circuit, new Ad9833(), 80, 0);
        var load = Place(circuit, new Resistor(10e3), 240, 100);
        var ground2 = Place(circuit, new Ground(), 80, 200);
        load.RotationDegrees = 90;
        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, master.Vcc);
        circuit.Connect(master.Gnd, ground.Pin);
        circuit.Connect(supply.Positive, dds.Vcc);
        circuit.Connect(dds.Gnd, ground2.Pin);
        circuit.Connect(dds.AnalogGround, ground2.Pin);
        circuit.Connect(master.ChipSelect, dds.Sync);
        circuit.Connect(master.Clock, dds.Clock);
        circuit.Connect(master.MasterOut, dds.Data);
        circuit.Connect(dds.Output, load.A);
        circuit.Connect(load.B, ground2.Pin);

        vm.Scope.TimebasePerDivision = 0.5e-3;
        vm.Scope.VoltsPerDivision = 0.2;
        vm.Scope.AddProbe(dds.Output, "VOUT");
    }

    // ───── Development Boards ─────

    /// <summary>An ATtiny85 on 5 V blinking an LED on PB0 (pin 5) twice a second, from its Timer0 overflow.</summary>
    public static void LoadAttiny85Blink(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "ATtiny85 blink";
        var supply = Place(circuit, new DcVoltageSource(5.0), -300, 0);
        var ground = Place(circuit, new Ground(), -300, 140);
        var tiny = Place(circuit, new Attiny85(), 0, 0);
        tiny.Load(MemoryImage.FromBinary(Attiny85BlinkFirmware));
        var resistor = Place(circuit, new Resistor(330), 200, 60);
        var led = Place(circuit, new Led(), 280, 140);
        var ground2 = Place(circuit, new Ground(), 280, 240);
        led.RotationDegrees = 90;
        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, tiny.Vcc);
        circuit.Connect(tiny.Gnd, ground.Pin);
        var pb0 = tiny.TerminalByName("PB0");
        circuit.Connect(pb0, resistor.A);
        circuit.Connect(resistor.B, led.A);
        circuit.Connect(led.B, ground2.Pin);

        vm.Scope.TimebasePerDivision = 0.5;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.AddProbe(pb0, "PB0");
        vm.Simulation.SpeedFactor = 1.0;
    }

    /// <summary>An AT89C2051 on an 11.0592 MHz crystal saying hello down its UART to the serial terminal, then lighting P1.0's LED.</summary>
    public static void LoadAt89c2051Hello(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "AT89C2051 serial";
        var supply = Place(circuit, new DcVoltageSource(5.0), -320, 0);
        var ground = Place(circuit, new Ground(), -320, 140);
        var mcu = Place(circuit, new At89c2051(), 0, 0);
        mcu.Load(MemoryImage.FromBinary(At89c2051HelloFirmware));
        var reset = Place(circuit, new Resistor(10e3), -140, -160);
        var terminal = Place(circuit, new SerialTerminal { BaudRate = 9600, Message = "" }, -300, -260);
        var resistor = Place(circuit, new Resistor(330), 200, -160);
        var led = Place(circuit, Led.OfColour("Green"), 200, -80);
        var ground2 = Place(circuit, new Ground(), 0, 200);
        reset.RotationDegrees = 90;
        resistor.RotationDegrees = 90;
        led.RotationDegrees = 90;
        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, mcu.Vcc);
        circuit.Connect(mcu.Gnd, ground2.Pin);
        circuit.Connect(mcu.Reset, reset.A);
        circuit.Connect(reset.B, ground2.Pin);
        circuit.Connect(supply.Positive, terminal.Vcc);
        circuit.Connect(terminal.Gnd, ground.Pin);
        circuit.Connect(mcu.Port3[1], terminal.Receive);
        circuit.Connect(supply.Positive, resistor.A);
        circuit.Connect(resistor.B, led.A);
        circuit.Connect(led.B, mcu.Port1[0]);

        vm.Scope.TimebasePerDivision = 2e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.AddProbe(mcu.Port3[1], "TXD");
    }
}
