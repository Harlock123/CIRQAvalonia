using Cirq.Components.Boards;
using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Electromechanical;
using Cirq.Components.Nonlinear;
using Cirq.Components.Ics;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.UI.ViewModels;

/// <summary>A ready-made circuit offered from the Examples menu.</summary>
public sealed record ExampleCircuit(string Name, string Description, Action<MainWindowViewModel> Build);

/// <summary>
/// Pre-wired circuits that exercise each part of the engine, so the application has something
/// running the moment it opens.
/// </summary>
public static class Examples
{
    public static IReadOnlyList<ExampleCircuit> All { get; } =
    [
        new("RC Low-Pass", "Step response of a first-order filter", LoadRcLowPass),
        new("555 Astable", "Free-running multivibrator at about 480 Hz", Load555Astable),
        new("Inverting Amplifier", "LM741 with a gain of -10", LoadInvertingAmplifier),
        new("Half-Wave Rectifier", "Diode and smoothing capacitor", LoadRectifier),
        new("Regulated Supply", "7805 holding 5 V from a 12 V rail", LoadRegulatedSupply),
        new("NAND Latch", "Cross-coupled 7400 gates", LoadNandLatch),
        new("Decade Counter", "7490 counting in BCD", LoadDecadeCounter),
        new("Ring Oscillator", "Three inverters with unequal delays", LoadRingOscillator),
        new("Digit Counter", "7490 into a 7447 driving a seven-segment display", LoadDigitCounter),
        new("Transistor Switch", "NPN driving an LED from a push button", LoadTransistorSwitch),
        new("MOSFET Driver", "Logic-level MOSFET switching an inductive load", LoadMosfetDriver),
        new("Comparator Trigger", "LM311 squaring up a sine wave", LoadComparatorTrigger),
        new("Running Light", "74164 shift register walking a bit across eight LEDs", LoadRunningLight),
        new("Window Detector", "LM339 outputs wired together to flag an out-of-range voltage",
            LoadWindowDetector),
        new("Raspberry Pi GPIO", "Pi driving two LEDs and reading a button on an internal pull-up",
            LoadRaspberryPiGpio),
        new("Linear Power Supply", "Mains secondary through a bridge and reservoir into a 7812",
            LoadLinearPowerSupply),
        new("Lamp Dimmer", "Triac and diac phase control — move the firing angle and the lamp dims",
            LoadLampDimmer),
        new("SCR Latch", "A push button fires it and only interrupting the anode turns it off",
            LoadScrLatch),
        new("LED Chaser", "4017 walking a lit output along ten LEDs", LoadLedChaser),
        new("4060 Timer", "A 4060 clocking itself from one resistor and one capacitor",
            Load4060Timer),
        new("Staircase Generator", "4040 addressing a 4051 to step through a resistor ladder",
            LoadStaircase),
        new("JFET Amplifier", "2N3819 common-source stage with self-bias", LoadJfetAmplifier),
        new("Buck Converter", "MC34063 stepping 12 V down to 5 V without turning the difference into heat",
            LoadBuckConverter),
        new("I2C EEPROM", "Three bytes written over two wires and read back again", LoadI2cEeprom),
        new("SPI Shift Register", "An SPI master clocking a byte into a 74595 and onto eight LEDs",
            LoadSpiShiftRegister),
        new("Full-Wave Rectifier", "A centre-tapped secondary and two diodes — one drop, not a bridge's two",
            LoadFullWaveRectifier),
        new("I2C Clock", "A DS1307 read over two wires, its registers in BCD and moving on their own",
            LoadI2cClock),
        new("Ultrasonic Ranger", "An HC-SR04 triggered on a timer — the distance is the echo's width",
            LoadUltrasonicRanger),
        new("Serial Link", "A UART talking to a module — change one baud rate and watch it break",
            LoadSerialLink),
        new("Light Meter", "A phototransistor into an I2C ADC — light, to current, to volts, to a number",
            LoadLightMeter),
        new("Motor Reversing", "An H-bridge running a motor both ways — forward, brake, reverse, coast",
            LoadMotorReversing),
        new("Noise and Hysteresis", "A comparator chattering on a noisy ramp — add hysteresis and it stops",
            LoadNoiseAndHysteresis),
        new("Reflections", "A fast edge down a metre of coax — close SW1 to terminate it and the ringing stops",
            LoadReflections),
        new("Ferrite Bead", "Two hundred megahertz of rubbish on a rail, and the bead that turns it into heat",
            LoadFerriteBead),
        new("Gate Driver", "The same clock into two MOSFETs, one straight from the pin and one through a driver",
            LoadGateDriver),
        new("1-Wire Thermometer", "A DS18B20 read over a single wire — reset, convert, wait, read",
            LoadOneWireThermometer),
        new("DAC and ADC", "A voltage made by one chip and measured by another — move the Code slider",
            LoadDacAndAdc),
        new("Reed Switch Bounce", "One magnet passing, counted several times — which is what debouncing is for",
            LoadReedSwitchBounce),
        new("Motion Light", "A PIR holding a lamp on long after you stop moving",
            LoadMotionLight),
    ];

    public static void LoadRcLowPass(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "RC low-pass filter";

        var generator = Place(circuit, new FunctionGenerator(Waveform.Square, 500, 4.0) { DcOffset = 2.0 }, -220, 0);
        var resistor = Place(circuit, new Resistor(10e3), -80, -80);
        var capacitor = Place(circuit, new Capacitor(100e-9), 40, 0);
        var ground = Place(circuit, new Ground(), -220, 120);
        var ground2 = Place(circuit, new Ground(), 40, 120);

        resistor.RotationDegrees = 0;
        capacitor.RotationDegrees = 90;

        circuit.Connect(generator.Output, resistor.A);
        circuit.Connect(resistor.B, capacitor.A);
        circuit.Connect(capacitor.B, ground2.Pin);
        circuit.Connect(generator.Return, ground.Pin);

        vm.Scope.TimebasePerDivision = 200e-6;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.AddProbe(resistor.A, "Input");
        vm.Scope.AddProbe(capacitor.A, "Output");
    }

    public static void Load555Astable(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "NE555 astable multivibrator";

        var supply = Place(circuit, new DcVoltageSource(9.0), -300, 40);
        var timer = Place(circuit, new Ne555(), 40, 0);
        var r1 = Place(circuit, new Resistor(10e3), -120, -120);
        var r2 = Place(circuit, new Resistor(10e3), -120, -20);
        var timing = Place(circuit, new Capacitor(100e-9), -200, 80);
        var control = Place(circuit, new Capacitor(10e-9), 180, 60);
        var ground = Place(circuit, new Ground(), -300, 160);
        var ground2 = Place(circuit, new Ground(), -200, 160);
        var ground3 = Place(circuit, new Ground(), 180, 160);

        r1.RotationDegrees = 90;
        r2.RotationDegrees = 90;
        timing.RotationDegrees = 90;
        control.RotationDegrees = 90;

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, timer.Vcc);
        circuit.Connect(supply.Positive, timer.Reset);
        circuit.Connect(timer.Gnd, ground.Pin);

        circuit.Connect(supply.Positive, r1.A);
        circuit.Connect(r1.B, timer.Discharge);
        circuit.Connect(timer.Discharge, r2.A);
        circuit.Connect(r2.B, timing.A);
        circuit.Connect(timing.B, ground2.Pin);

        circuit.Connect(timing.A, timer.Threshold);
        circuit.Connect(timing.A, timer.Trigger);

        circuit.Connect(timer.Control, control.A);
        circuit.Connect(control.B, ground3.Pin);

        vm.Scope.TimebasePerDivision = 500e-6;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.AddProbe(timing.A, "Capacitor");
        vm.Scope.AddProbe(timer.Out, "Output");
    }

    public static void LoadInvertingAmplifier(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "LM741 inverting amplifier";

        var positive = Place(circuit, new DcVoltageSource(15.0), 60, -180);
        var negative = Place(circuit, new DcVoltageSource(-15.0), 60, 180);
        var opamp = Place(circuit, new OpAmp741(), 60, 0);
        var generator = Place(circuit, new FunctionGenerator(Waveform.Sine, 1e3, 1.0), -300, 0);
        var rin = Place(circuit, new Resistor(10e3), -160, -40);
        var rf = Place(circuit, new Resistor(100e3), 0, -140);
        var ground = Place(circuit, new Ground(), -300, 120);
        var ground2 = Place(circuit, new Ground(), -20, 120);
        var ground3 = Place(circuit, new Ground(), 160, -180);
        var ground4 = Place(circuit, new Ground(), 160, 180);

        circuit.Connect(generator.Return, ground.Pin);
        circuit.Connect(generator.Output, rin.A);
        circuit.Connect(rin.B, opamp.Inverting);
        circuit.Connect(opamp.Inverting, rf.A);
        circuit.Connect(rf.B, opamp.Output);
        circuit.Connect(opamp.NonInverting, ground2.Pin);

        circuit.Connect(positive.Positive, opamp.PositiveSupply);
        circuit.Connect(positive.Negative, ground3.Pin);
        circuit.Connect(negative.Positive, opamp.NegativeSupply);
        circuit.Connect(negative.Negative, ground4.Pin);

        vm.Scope.TimebasePerDivision = 200e-6;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.AddProbe(rin.A, "Input");
        vm.Scope.AddProbe(opamp.Output, "Output");
    }

    public static void LoadRectifier(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Half-wave rectifier with smoothing";

        var generator = Place(circuit, new FunctionGenerator(Waveform.Sine, 50, 24.0), -260, 0);
        var diode = Place(circuit, new Diode(DiodeModel.D1N4001), -100, -80);
        var smoothing = Place(circuit, new Capacitor(100e-6), 40, 0);
        var load = Place(circuit, new Resistor(1e3), 160, 0);
        var ground = Place(circuit, new Ground(), -260, 140);
        var ground2 = Place(circuit, new Ground(), 40, 140);
        var ground3 = Place(circuit, new Ground(), 160, 140);

        smoothing.RotationDegrees = 90;
        load.RotationDegrees = 90;

        circuit.Connect(generator.Output, diode.Anode);
        circuit.Connect(diode.Cathode, smoothing.A);
        circuit.Connect(smoothing.A, load.A);
        circuit.Connect(smoothing.B, ground2.Pin);
        circuit.Connect(load.B, ground3.Pin);
        circuit.Connect(generator.Return, ground.Pin);

        vm.Scope.TimebasePerDivision = 5e-3;
        vm.Scope.VoltsPerDivision = 5.0;
        vm.Scope.AddProbe(diode.Anode, "AC in");
        vm.Scope.AddProbe(load.A, "DC out");
    }

    public static void LoadRegulatedSupply(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "7805 regulated supply";

        var supply = Place(circuit, new DcVoltageSource(12.0), -260, 40);
        var regulator = Place(circuit, new VoltageRegulator(RegulatorModel.Lm7805), -60, -40);
        var input = Place(circuit, new Capacitor(10e-6), -160, 60);
        var output = Place(circuit, new Capacitor(1e-6), 60, 60);
        var load = Place(circuit, new Resistor(100), 180, 60);
        var ground = Place(circuit, new Ground(), -260, 180);
        var ground2 = Place(circuit, new Ground(), -60, 180);

        input.RotationDegrees = 90;
        output.RotationDegrees = 90;
        load.RotationDegrees = 90;

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, regulator.Input);
        circuit.Connect(regulator.Input, input.A);
        circuit.Connect(input.B, ground2.Pin);
        circuit.Connect(regulator.Common, ground2.Pin);
        circuit.Connect(regulator.Output, output.A);
        circuit.Connect(output.B, ground2.Pin);
        circuit.Connect(regulator.Output, load.A);
        circuit.Connect(load.B, ground2.Pin);

        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.AddProbe(regulator.Input, "Unregulated");
        vm.Scope.AddProbe(regulator.Output, "Regulated");
    }

    public static void LoadNandLatch(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "7400 NAND latch";

        var supply = Place(circuit, new DcVoltageSource(5.0), -300, 140);
        var ic = Place(circuit, new Ic7400(), 40, 0);
        var setSwitch = Place(circuit, new LogicToggle(true), -180, -100);
        var resetSwitch = Place(circuit, new LogicToggle(true), -180, 100);
        var ground = Place(circuit, new Ground(), -300, 260);

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, ic.Vcc);
        circuit.Connect(ic.Gnd, ground.Pin);

        var (a1, b1, y1) = ic.Gate(0);
        var (a2, b2, y2) = ic.Gate(1);

        circuit.Connect(setSwitch.Out, a1);
        circuit.Connect(resetSwitch.Out, a2);
        circuit.Connect(y1, b2);   // Cross-coupled: each output feeds the other gate.
        circuit.Connect(y2, b1);

        vm.Scope.TimebasePerDivision = 1e-6;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.AddProbe(y1, "Q");
        vm.Scope.AddProbe(y2, "Q'");
    }

    public static void LoadDecadeCounter(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "7490 decade counter";

        var supply = Place(circuit, new DcVoltageSource(5.0), -300, 160);
        var counter = Place(circuit, new Ic7490(), 60, 0);
        var clock = Place(circuit, new ClockSource(10e3), -180, -60);
        var ground = Place(circuit, new Ground(), -300, 280);
        var ground2 = Place(circuit, new Ground(), -60, 200);

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, counter.Vcc);
        circuit.Connect(counter.Gnd, ground.Pin);

        circuit.Connect(clock.Out, counter.ClockA);
        circuit.Connect(counter.Qa, counter.ClockB);   // BCD sequence.
        circuit.Connect(counter.Reset0A, ground2.Pin);
        circuit.Connect(counter.Reset0B, ground2.Pin);
        circuit.Connect(counter.Reset9A, ground2.Pin);
        circuit.Connect(counter.Reset9B, ground2.Pin);

        vm.Scope.TimebasePerDivision = 100e-6;
        vm.Scope.VoltsPerDivision = 2.0;
        // Tiled, not stacked: these are unipolar 0-3.4V logic traces, and stacked mode centres
        // each lane on its offset, which puts the top of the swing outside the lane. Tiled gives
        // every trace its own auto-ranged axes, which is what logic wants.
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(counter.Qa, "QA");
        vm.Scope.AddProbe(counter.Qb, "QB");
        vm.Scope.AddProbe(counter.Qc, "QC");
        vm.Scope.AddProbe(counter.Qd, "QD");
    }

    public static void LoadRingOscillator(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Three-stage ring oscillator";

        // Unequal delays break the symmetric lockstep mode a perfectly matched ring would sit in.
        double[] delays = [15e-9, 20e-9, 25e-9];
        var inverters = new LogicGate[3];
        for (var i = 0; i < 3; i++)
            inverters[i] = Place(circuit, new LogicGate(GateFunction.Not) { PropagationDelay = delays[i] },
                -200 + i * 160, 0);

        Place(circuit, new Ground(), -200, 160);

        for (var i = 0; i < 3; i++)
            circuit.Connect(inverters[i].Out, inverters[(i + 1) % 3].InputTerminals[0]);

        vm.Scope.TimebasePerDivision = 50e-9;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        for (var i = 0; i < 3; i++)
            vm.Scope.AddProbe(inverters[i].Out, $"Stage {i + 1}");
    }


    public static void LoadDigitCounter(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Counter driving a seven-segment display";

        var supply = Place(circuit, new DcVoltageSource(5.0), -460, 120);
        var gnd = Place(circuit, new Ground(), -460, 260);
        var rail = Place(circuit, new Ground(), -180, 300);

        var clock = Place(circuit, new ClockSource(4.0), -340, -80);
        var counter = Place(circuit, new Ic7490(), -160, 0);
        var decoder = Place(circuit, new Ic7447(), 60, 0);
        var display = Place(circuit, new SevenSegmentDisplay(commonAnode: true), 340, 0);

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(counter.Vcc, supply.Positive);
        circuit.Connect(decoder.Vcc, supply.Positive);
        circuit.Connect(counter.Gnd, gnd.Pin);
        circuit.Connect(decoder.Gnd, gnd.Pin);

        // Counter in BCD mode: QA feeds the divide-by-five section.
        circuit.Connect(clock.Out, counter.ClockA);
        circuit.Connect(counter.Qa, counter.ClockB);
        foreach (var reset in new[] { counter.Reset0A, counter.Reset0B, counter.Reset9A, counter.Reset9B })
            circuit.Connect(reset, rail.Pin);

        circuit.Connect(counter.Qa, decoder.InputA);
        circuit.Connect(counter.Qb, decoder.InputB);
        circuit.Connect(counter.Qc, decoder.InputC);
        circuit.Connect(counter.Qd, decoder.InputD);

        // Control pins released.
        circuit.Connect(decoder.LampTest, supply.Positive);
        circuit.Connect(decoder.BlankingInput, supply.Positive);
        circuit.Connect(decoder.RippleBlankingInput, supply.Positive);

        // Common anode to the rail, one current-limiting resistor per segment.
        circuit.Connect(display.Common, supply.Positive);
        for (var i = 0; i < 7; i++)
        {
            var resistor = Place(circuit, new Resistor(330), 200, -90 + i * 30);
            circuit.Connect(decoder.Segments[i], resistor.A);
            circuit.Connect(resistor.B, display.Segments[i]);
        }

        vm.Scope.TimebasePerDivision = 50e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(counter.Qa, "QA");
        vm.Scope.AddProbe(counter.Qd, "QD");
    }

    public static void LoadTransistorSwitch(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "NPN low-side LED switch";

        var supply = Place(circuit, new DcVoltageSource(5.0), -300, 60);
        var button = Place(circuit, new PushButton(), -160, -120);
        var baseResistor = Place(circuit, new Resistor(4.7e3), -40, -120);
        var q = Place(circuit, new BipolarTransistor(BjtModel.N2N3904), 120, 40);
        var limit = Place(circuit, new Resistor(220), 140, -120);
        var led = Place(circuit, Led.OfColour("Red"), 140, -40);
        var gnd = Place(circuit, new Ground(), -300, 200);
        var gnd2 = Place(circuit, new Ground(), 140, 180);

        limit.RotationDegrees = 90;
        led.RotationDegrees = 90;

        circuit.Connect(supply.Negative, gnd.Pin);

        // Button pulls the base up through the resistor when pressed.
        circuit.Connect(supply.Positive, button.A);
        circuit.Connect(button.B, baseResistor.A);
        circuit.Connect(baseResistor.B, q.Base);

        circuit.Connect(supply.Positive, limit.A);
        circuit.Connect(limit.B, led.Anode);
        circuit.Connect(led.Cathode, q.Collector);
        circuit.Connect(q.Emitter, gnd2.Pin);

        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.AddProbe(q.Base, "Base");
        vm.Scope.AddProbe(q.Collector, "Collector");
    }

    public static void LoadMosfetDriver(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "MOSFET driving an inductive load";

        var supply = Place(circuit, new DcVoltageSource(12.0), -320, 60);
        var drive = Place(circuit, new FunctionGenerator(Waveform.Square, 500, 10.0) { DcOffset = 5.0 }, -320, -120);
        var coil = Place(circuit, new Inductor(10e-3) { SeriesResistance = 8.0 }, 100, -120);
        var flyback = Place(circuit, new Diode(DiodeModel.D1N4001), 240, -60);
        var m = Place(circuit, new Mosfet(MosfetModel.IrlZ44N), 100, 60);
        var gnd = Place(circuit, new Ground(), -320, 220);
        var gnd2 = Place(circuit, new Ground(), 100, 220);

        coil.RotationDegrees = 90;
        flyback.RotationDegrees = 90;

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(drive.Return, gnd.Pin);
        circuit.Connect(drive.Output, m.Gate);

        circuit.Connect(supply.Positive, coil.A);
        circuit.Connect(coil.B, m.Drain);
        circuit.Connect(m.Source, gnd2.Pin);

        // Flyback diode across the coil: anode at the drain, cathode at the supply.
        circuit.Connect(flyback.Anode, m.Drain);
        circuit.Connect(flyback.Cathode, supply.Positive);

        vm.Scope.TimebasePerDivision = 200e-6;
        vm.Scope.VoltsPerDivision = 5.0;
        vm.Scope.AddProbe(m.Gate, "Gate");
        vm.Scope.AddProbe(m.Drain, "Drain");
    }

    public static void LoadComparatorTrigger(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "LM311 comparator squaring a sine";

        var supply = Place(circuit, new DcVoltageSource(5.0), -340, 120);
        var generator = Place(circuit, new FunctionGenerator(Waveform.Sine, 1e3, 4.0) { DcOffset = 2.5 }, -340, -80);
        var u = Place(circuit, new Comparator(ComparatorModel.Lm311), 40, 0);
        var top = Place(circuit, new Resistor(10e3), -140, 120);
        var bottom = Place(circuit, new Resistor(10e3), -140, 220);
        var pullUp = Place(circuit, new Resistor(4.7e3), 200, -100);
        var gnd = Place(circuit, new Ground(), -340, 280);
        var gnd2 = Place(circuit, new Ground(), -140, 300);

        top.RotationDegrees = 90;
        bottom.RotationDegrees = 90;
        pullUp.RotationDegrees = 90;

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(generator.Return, gnd.Pin);
        circuit.Connect(u.PositiveSupply, supply.Positive);
        circuit.Connect(u.NegativeSupply, gnd.Pin);

        // Mid-rail reference from a divider.
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, gnd2.Pin);
        circuit.Connect(top.B, u.Inverting);

        circuit.Connect(generator.Output, u.NonInverting);

        // Open-collector output needs the pull-up to go high.
        circuit.Connect(pullUp.A, supply.Positive);
        circuit.Connect(pullUp.B, u.Output);

        vm.Scope.TimebasePerDivision = 200e-6;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.AddProbe(generator.Output, "Sine in");
        vm.Scope.AddProbe(u.Output, "Squared");
    }


    public static void LoadRunningLight(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "74164 shift register running light";

        var supply = Place(circuit, new DcVoltageSource(5.0), -420, 140);
        var gnd = Place(circuit, new Ground(), -420, 280);
        var ledGround = Place(circuit, new Ground(), 320, 300);

        var clock = Place(circuit, new ClockSource(8.0), -300, -60);
        var seed = Place(circuit, new LogicToggle(true), -300, 60);
        var register = Place(circuit, new Ic74164(), -100, 0);

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(register.Vcc, supply.Positive);
        circuit.Connect(register.Gnd, gnd.Pin);

        circuit.Connect(clock.Out, register.Clock);
        circuit.Connect(seed.Out, register.SerialA);
        circuit.Connect(register.SerialB, supply.Positive);
        circuit.Connect(register.Clear, supply.Positive);

        // Eight LEDs, each on its own output through a current-limiting resistor.
        string[] colours = ["Red", "Amber", "Yellow", "Green", "Red", "Amber", "Yellow", "Green"];
        for (var i = 0; i < 8; i++)
        {
            var y = -110 + i * 32;
            var resistor = Place(circuit, new Resistor(330), 120, y);
            var led = Place(circuit, Led.OfColour(colours[i]), 260, y);

            circuit.Connect(register.Outputs[i], resistor.A);
            circuit.Connect(resistor.B, led.Anode);
            circuit.Connect(led.Cathode, ledGround.Pin);
        }

        vm.Scope.TimebasePerDivision = 50e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(register.Outputs[0], "QA");
        vm.Scope.AddProbe(register.Outputs[3], "QD");
        vm.Scope.AddProbe(register.Outputs[7], "QH");
    }

    public static void LoadWindowDetector(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "LM339 window detector";

        var supply = Place(circuit, new DcVoltageSource(5.0), -420, 160);
        var gnd = Place(circuit, new Ground(), -420, 300);
        var dividerGround = Place(circuit, new Ground(), -180, 320);

        var signal = Place(circuit, new FunctionGenerator(Waveform.Triangle, 200, 5.0) { DcOffset = 2.5 },
            -420, -100);
        var ic = Place(circuit, new QuadComparator(), 60, 0);

        // Reference ladder: 3.5 V upper limit, 1.5 V lower limit.
        var top = Place(circuit, new Resistor(15e3), -180, 40);
        var middle = Place(circuit, new Resistor(20e3), -180, 140);
        var bottom = Place(circuit, new Resistor(15e3), -180, 240);

        top.RotationDegrees = 90;
        middle.RotationDegrees = 90;
        bottom.RotationDegrees = 90;

        var pullUp = Place(circuit, new Resistor(4.7e3), 260, -120);
        pullUp.RotationDegrees = 90;

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(signal.Return, gnd.Pin);
        circuit.Connect(ic.Vcc, supply.Positive);
        circuit.Connect(ic.Gnd, gnd.Pin);

        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, middle.A);
        circuit.Connect(middle.B, bottom.A);
        circuit.Connect(bottom.B, dividerGround.Pin);

        var (plus0, minus0, out0) = ic.Channel(0);
        var (plus1, minus1, out1) = ic.Channel(1);

        // Channel 0 trips when the signal rises above the upper limit.
        circuit.Connect(signal.Output, plus0);
        circuit.Connect(minus0, top.B);

        // Channel 1 trips when it falls below the lower limit.
        circuit.Connect(signal.Output, minus1);
        circuit.Connect(plus1, middle.B);

        // Both open-collector outputs share one node: in range means neither is pulling.
        circuit.Connect(out0, out1);
        circuit.Connect(pullUp.A, supply.Positive);
        circuit.Connect(pullUp.B, out0);

        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.AddProbe(signal.Output, "Signal");
        vm.Scope.AddProbe(out0, "In range");
    }

    /// <summary>
    /// A complete linear supply: transformer secondary, bridge, reservoir capacitor, regulator.
    /// <para>
    /// The pair of probes is the point of the example. The reservoir rail sags and recharges twice
    /// per mains cycle — that sawtooth is the ripple a capacitor alone cannot remove — while the
    /// regulated rail beside it is flat. Each stage only works because the one before it left
    /// enough headroom: the regulator needs its dropout voltage above 12 V at the *bottom* of the
    /// ripple, not on average.
    /// </para>
    /// <para>
    /// This is the one example that sets the simulation speed. At the usual 1/1000 a single 50 Hz
    /// cycle would take fifty seconds of wall time and the reservoir would spend minutes charging,
    /// so it asks for real time and the solver keeps up as best it can.
    /// </para>
    /// </summary>
    public static void LoadLinearPowerSupply(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "12 V linear power supply";

        // About 14 V RMS off the transformer: enough that the ripple troughs still clear the
        // 7812's dropout, without wasting the headroom as heat in the regulator.
        var secondary = Place(circuit, new FunctionGenerator
        {
            Shape = Waveform.Sine,
            Frequency = 50,
            AmplitudePeakToPeak = 40,
            OutputResistance = 1.0,          // transformer winding resistance, and it tames the inrush
        }, -420, 0);

        var bridge = Place(circuit, new BridgeRectifier(), -240, 0);
        var reservoir = Place(circuit, new ElectrolyticCapacitor(1000e-6) { VoltageRating = 35 }, -80, 80);
        var regulator = Place(circuit, new VoltageRegulator(RegulatorModel.Lm7812), 90, -40);
        var output = Place(circuit, new ElectrolyticCapacitor(10e-6) { VoltageRating = 25 }, 240, 80);
        var load = Place(circuit, new Resistor(120), 360, 80);
        var ground = Place(circuit, new Ground(), -240, 210);

        reservoir.RotationDegrees = 90;
        output.RotationDegrees = 90;
        load.RotationDegrees = 90;

        circuit.Connect(secondary.Output, bridge.Ac1);
        circuit.Connect(secondary.Return, bridge.Ac2);
        circuit.Connect(bridge.Negative, ground.Pin);

        circuit.Connect(bridge.Positive, reservoir.A);
        circuit.Connect(reservoir.B, ground.Pin);

        circuit.Connect(bridge.Positive, regulator.Input);
        circuit.Connect(regulator.Common, ground.Pin);

        circuit.Connect(regulator.Output, output.A);
        circuit.Connect(output.B, ground.Pin);
        circuit.Connect(regulator.Output, load.A);
        circuit.Connect(load.B, ground.Pin);

        vm.Scope.TimebasePerDivision = 10e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Unified;
        vm.Scope.AddProbe(bridge.Positive, "Reservoir");
        vm.Scope.AddProbe(regulator.Output, "Regulated");

        vm.Simulation.SpeedFactor = 1.0;
    }

    /// <summary>
    /// The canonical first Raspberry Pi circuit, with each of the three things a GPIO pin can do:
    /// one pin toggling, one playing a bit pattern, and one reading a button.
    /// <para>
    /// The button pin uses the Pi's internal pull-up, so the input idles high and the button pulls
    /// it down — which is why the button goes to ground rather than to 3.3V. Wiring it the other
    /// way needs an external pull-down and is the usual reason a first attempt reads garbage.
    /// </para>
    /// <para>
    /// The rates are far faster than you would blink a real LED, because the simulation runs at
    /// 1/1000 speed by default and a 2 Hz blink would take eight minutes of wall time per cycle.
    /// Set <b>Simulate &gt; Speed</b> to real time to see human-scale rates.
    /// </para>
    /// </summary>
    public static void LoadRaspberryPiGpio(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Raspberry Pi GPIO";

        var pi = Place(circuit, new RaspberryPiBoard(), -300, 0);

        // GPIO18 toggles, GPIO23 plays a pattern, GPIO25 reads. All three on the even-numbered
        // side of the header so the wiring runs out to the right rather than back across the body.
        pi.Pins = "GPIO18=clock@500Hz; GPIO23=seq@1kHz:1100_1010; GPIO25=in-pullup";

        // 330R on 3.3V gives about 4.5 mA through a red LED — comfortably inside the Pi's 16 mA
        // per-pin rating, which the board checks as it runs.
        var r1 = Place(circuit, new Resistor(330), -60, -180);
        var led1 = Place(circuit, Led.OfColour("Red"), 60, -180);
        var r2 = Place(circuit, new Resistor(330), -60, -80);
        var led2 = Place(circuit, Led.OfColour("Green"), 60, -80);
        var button = Place(circuit, new PushButton(), -60, 60);

        var gndLeds = Place(circuit, new Ground(), 180, 20);
        var gndButton = Place(circuit, new Ground(), 60, 140);
        var gndBoard = Place(circuit, new Ground(), -300, 300);

        // The board's own ground is what everything else is measured against.
        circuit.Connect(pi.GroundPins[0], gndBoard.Pin);

        circuit.Connect(pi.Pin("GPIO18"), r1.A);
        circuit.Connect(r1.B, led1.Anode);
        circuit.Connect(led1.Cathode, gndLeds.Pin);

        circuit.Connect(pi.Pin("GPIO23"), r2.A);
        circuit.Connect(r2.B, led2.Anode);
        circuit.Connect(led2.Cathode, gndLeds.Pin);

        // Double-click the button on the canvas to press it and watch the input fall.
        circuit.Connect(pi.Pin("GPIO25"), button.A);
        circuit.Connect(button.B, gndButton.Pin);

        // Tiled rather than stacked: these are 0-3.3V logic traces, and stacking centres each
        // lane on its offset, which pushes a unipolar trace off the top of its lane.
        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(pi.Pin("GPIO18"), "Clock");
        vm.Scope.AddProbe(pi.Pin("GPIO23"), "Pattern");
        vm.Scope.AddProbe(pi.Pin("GPIO25"), "Button");
    }

    /// <summary>
    /// An I²C bus with one device on it, writing three bytes and reading them back.
    /// <para>
    /// The two pull-up resistors are not optional decoration. Nothing on an I²C bus ever drives a
    /// line high — every device can only pull down or let go — so without them there is no high
    /// level to make and nothing works at all. Delete one and watch.
    /// </para>
    /// </summary>
    public static void LoadI2cEeprom(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "I2C EEPROM";

        var rail = Place(circuit, new DcVoltageSource(3.3), -420, 120);
        var master = Place(circuit, new I2cMaster
        {
            Transactions = "w 50 00 00 48 49 21; w 50 00 00; r 50 3",
            ClockFrequency = 100e3,
        }, -180, -40);
        var eeprom = Place(circuit, new I2cEeprom(), 220, -40);

        var sdaPull = Place(circuit, new Resistor(4.7e3), 20, -180);
        var sclPull = Place(circuit, new Resistor(4.7e3), 140, -180);

        var gnd = Place(circuit, new Ground(), -420, 260);
        var gnd2 = Place(circuit, new Ground(), 220, 160);

        sdaPull.RotationDegrees = 90;
        sclPull.RotationDegrees = 90;

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(master.Vcc, rail.Positive);
        circuit.Connect(master.Gnd, gnd.Pin);
        circuit.Connect(eeprom.Vcc, rail.Positive);
        circuit.Connect(eeprom.Gnd, gnd2.Pin);

        circuit.Connect(master.Sda, eeprom.Sda);
        circuit.Connect(master.Scl, eeprom.Scl);

        circuit.Connect(rail.Positive, sdaPull.A);
        circuit.Connect(sdaPull.B, master.Sda);
        circuit.Connect(rail.Positive, sclPull.A);
        circuit.Connect(sclPull.B, master.Scl);

        vm.Scope.TimebasePerDivision = 100e-6;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(master.Scl, "SCL");
        vm.Scope.AddProbe(master.Sda, "SDA");
    }

    /// <summary>
    /// An SPI master clocking a byte into a 74595 and out onto eight LEDs.
    /// <para>
    /// Three wires become eight outputs, which is what shift registers are for. The select line
    /// doubles as the latch here: the outputs update when the transfer ends, so the LEDs never
    /// show the intermediate patterns the bits make on their way through.
    /// </para>
    /// </summary>
    public static void LoadSpiShiftRegister(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "SPI shift register";

        var rail = Place(circuit, new DcVoltageSource(5.0), -460, 120);
        var master = Place(circuit, new SpiMaster
        {
            Transactions = "5A",
            ClockFrequency = 200e3,
        }, -240, -40);
        var register = Place(circuit, new Ic74595(), 40, -40);

        var gnd = Place(circuit, new Ground(), -460, 260);
        var gnd2 = Place(circuit, new Ground(), 40, 220);
        var ledGround = Place(circuit, new Ground(), 420, 300);

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(master.Vcc, rail.Positive);
        circuit.Connect(master.Gnd, gnd.Pin);
        circuit.Connect(register.Vcc, rail.Positive);
        circuit.Connect(register.Gnd, gnd2.Pin);

        circuit.Connect(master.Clock, register.ShiftClock);
        circuit.Connect(master.MasterOut, register.SerialIn);
        circuit.Connect(master.ChipSelect, register.LatchClock);

        circuit.Connect(register.Clear, rail.Positive);
        circuit.Connect(register.OutputEnable, gnd2.Pin);

        string[] colours = ["Red", "Amber", "Yellow", "Green", "Red", "Amber", "Yellow", "Green"];

        for (var i = 0; i < 8; i++)
        {
            var y = -150 + (i * 40);
            var resistor = Place(circuit, new Resistor(330), 260, y);
            var led = Place(circuit, Led.OfColour(colours[i]), 400, y);

            circuit.Connect(register.Outputs[i], resistor.A);
            circuit.Connect(resistor.B, led.Anode);
            circuit.Connect(led.Cathode, ledGround.Pin);
        }

        vm.Scope.TimebasePerDivision = 10e-6;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(master.Clock, "SCK");
        vm.Scope.AddProbe(master.MasterOut, "MOSI");
        vm.Scope.AddProbe(register.Outputs[0], "QA");
    }

    /// <summary>
    /// A step-down switching converter, and the counterpart to the linear supply example. The
    /// 7805 there drops seven volts across itself and turns them into heat; this one chops the
    /// input instead, and the inductor and diode carry the energy across between chops.
    /// <para>
    /// Nothing in the chip knows it is a buck converter. It brings an oscillator, a comparator
    /// against 1.25 V and a switch; the topology is the wiring around it, and the divider is what
    /// decides the output.
    /// </para>
    /// </summary>
    public static void LoadBuckConverter(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "MC34063 buck converter";

        var input = Place(circuit, new DcVoltageSource(12.0), -460, 60);
        var regulator = Place(circuit, new SwitchingRegulator(), -220, 0);
        var sense = Place(circuit, new Resistor(1.0), -340, -140);
        var coil = Place(circuit, new Inductor(1e-3), 40, -140);
        var catchDiode = Place(circuit, new Diode(DiodeModel.D1N5817), -60, -20);
        var reservoir = Place(circuit, new Capacitor(100e-6) { InitialVoltage = 0 }, 200, -20);
        var timing = Place(circuit, new Capacitor(1e-9) { InitialVoltage = 0 }, -340, 160);
        var top = Place(circuit, new Resistor(3e3), 340, -60);
        var bottom = Place(circuit, new Resistor(1e3), 340, 80);
        var load = Place(circuit, new Resistor(100), 460, -20);

        var gnd = Place(circuit, new Ground(), -460, 220);
        var gnd2 = Place(circuit, new Ground(), -60, 120);
        var gnd3 = Place(circuit, new Ground(), 200, 120);
        var gnd4 = Place(circuit, new Ground(), 340, 200);
        var gnd5 = Place(circuit, new Ground(), 460, 120);

        catchDiode.RotationDegrees = 90;
        coil.RotationDegrees = 0;
        reservoir.RotationDegrees = 90;
        timing.RotationDegrees = 90;
        top.RotationDegrees = 90;
        bottom.RotationDegrees = 90;
        load.RotationDegrees = 90;

        circuit.Connect(input.Negative, gnd.Pin);
        circuit.Connect(input.Positive, regulator.Supply);
        circuit.Connect(regulator.Ground, gnd.Pin);
        circuit.Connect(regulator.DriveCollector, regulator.Supply);

        // The sense resistor is how the chip sees the switch current: its own limit trips at
        // 300 mV across it, which is what stops the inductor current running away.
        circuit.Connect(regulator.Supply, sense.A);
        circuit.Connect(sense.B, regulator.CurrentSense);
        circuit.Connect(regulator.CurrentSense, regulator.SwitchCollector);

        // Switch node: the inductor one way, the catch diode the other. Without the diode the
        // inductor has nowhere to send its current when the switch opens.
        circuit.Connect(regulator.SwitchEmitter, coil.A);
        circuit.Connect(catchDiode.Cathode, regulator.SwitchEmitter);
        circuit.Connect(catchDiode.Anode, gnd2.Pin);

        circuit.Connect(coil.B, reservoir.A);
        circuit.Connect(reservoir.B, gnd3.Pin);
        circuit.Connect(coil.B, load.A);
        circuit.Connect(load.B, gnd5.Pin);

        // 1.25 V × (1 + 3k/1k) = 5 V. Change the divider and the output follows it.
        circuit.Connect(coil.B, top.A);
        circuit.Connect(top.B, regulator.Feedback);
        circuit.Connect(regulator.Feedback, bottom.A);
        circuit.Connect(bottom.B, gnd4.Pin);

        circuit.Connect(regulator.Timing, timing.A);
        circuit.Connect(timing.B, gnd.Pin);

        vm.Scope.TimebasePerDivision = 20e-6;
        vm.Scope.VoltsPerDivision = 5.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(regulator.SwitchEmitter, "Switch");
        vm.Scope.AddProbe(coil.B, "Output");

        var current = vm.Scope.AddProbe(coil.A, "Coil I");
        current.Kind = Cirq.Core.Probing.ProbeKind.Current;
    }

    /// <summary>
    /// Triac phase control, which is what is inside a lamp dimmer. The RC charges from the mains
    /// through each half cycle; when it reaches the diac's breakover the diac dumps it into the
    /// triac gate, and the triac conducts for whatever is left of that half. It then turns itself
    /// off at the zero crossing, because the current falls below its holding current, and the
    /// whole thing starts again. Raise the resistor and it fires later and the lamp dims.
    /// </summary>
    public static void LoadLampDimmer(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Triac lamp dimmer";

        var mains = Place(circuit, new FunctionGenerator(Waveform.Sine, 50, 320), -360, 40);
        var lamp = Place(circuit, new Resistor(240), -140, -100);
        var triac = Place(circuit, new Triac(), 80, 40);
        var gateResistor = Place(circuit, new Resistor(47e3), 80, -100);
        var timing = Place(circuit, new Capacitor(100e-9), 240, 40);
        var diac = Place(circuit, new Diac(), 240, -40);
        var gnd = Place(circuit, new Ground(), -360, 200);
        var gnd2 = Place(circuit, new Ground(), 240, 200);

        lamp.RotationDegrees = 0;
        timing.RotationDegrees = 90;

        circuit.Connect(mains.Output, lamp.A);
        circuit.Connect(lamp.B, triac.MainTerminal2);
        circuit.Connect(triac.MainTerminal1, gnd.Pin);
        circuit.Connect(mains.Return, gnd.Pin);

        // The phase-shift network, taken across the triac so it charges while the triac blocks.
        circuit.Connect(triac.MainTerminal2, gateResistor.A);
        circuit.Connect(gateResistor.B, timing.A);
        circuit.Connect(timing.B, gnd2.Pin);
        circuit.Connect(timing.A, diac.A);
        circuit.Connect(diac.B, triac.Gate);

        vm.Scope.TimebasePerDivision = 5e-3;
        vm.Scope.VoltsPerDivision = 100.0;
        vm.Scope.AddProbe(mains.Output, "Mains");
        vm.Scope.AddProbe(lamp.B, "Lamp");
    }

    /// <summary>
    /// What makes a thyristor different from everything else here: it remembers. Double-click the
    /// push button and the SCR fires; let go and it stays on, because the gate has no further say.
    /// The only way to put it out is to take the anode current away, which is what the switch in
    /// series with the lamp is for.
    /// </summary>
    public static void LoadScrLatch(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "SCR latch";

        var supply = Place(circuit, new DcVoltageSource(12.0), -360, 60);
        var interrupter = Place(circuit, new ToggleSwitch(closed: true), -200, -80);
        var lamp = Place(circuit, new Resistor(120), -20, -80);
        var scr = Place(circuit, new SiliconControlledRectifier(), 160, 60);
        var trigger = Place(circuit, new PushButton(), -20, 120);
        var gateResistor = Place(circuit, new Resistor(2.2e3), 160, 180);
        var gnd = Place(circuit, new Ground(), -360, 220);
        var gnd2 = Place(circuit, new Ground(), 340, 160);

        scr.RotationDegrees = 90;

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, interrupter.A);
        circuit.Connect(interrupter.B, lamp.A);
        circuit.Connect(lamp.B, scr.Anode);
        circuit.Connect(scr.Cathode, gnd2.Pin);

        // Gate drive: a momentary pulse from the rail is all it takes.
        circuit.Connect(supply.Positive, trigger.A);
        circuit.Connect(trigger.B, gateResistor.A);
        circuit.Connect(gateResistor.B, scr.Gate);

        vm.Scope.TimebasePerDivision = 50e-3;
        vm.Scope.VoltsPerDivision = 5.0;
        vm.Scope.AddProbe(lamp.B, "Anode");
        vm.Scope.AddProbe(scr.Gate, "Gate");
    }

    /// <summary>
    /// The archetypal 4000-series circuit. A 4017 decodes for you, so exactly one output is high
    /// and it walks along them — ten LEDs, ten pins, and no decoder anywhere.
    /// </summary>
    public static void LoadLedChaser(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "4017 LED chaser";

        var supply = Place(circuit, new DcVoltageSource(5.0), -420, 140);
        var gnd = Place(circuit, new Ground(), -420, 280);
        var ledGround = Place(circuit, new Ground(), 340, 340);

        // CMOS wants a rail-to-rail input, so the clock is set to the same family as the counter.
        var clock = Place(circuit, new ClockSource(6.0) { Levels = LogicLevels.Cmos5V }, -300, -40);
        var counter = Place(circuit, new Ic4017(), -100, 20);

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(counter.Vcc, supply.Positive);
        circuit.Connect(counter.Gnd, gnd.Pin);

        circuit.Connect(clock.Out, counter.Clock);
        circuit.Connect(counter.ClockInhibit, gnd.Pin);
        circuit.Connect(counter.Reset, gnd.Pin);

        string[] colours = ["Red", "Amber", "Yellow", "Green", "Blue",
                            "Red", "Amber", "Yellow", "Green", "Blue"];

        for (var i = 0; i < 10; i++)
        {
            var y = -160 + (i * 36);
            var resistor = Place(circuit, new Resistor(330), 140, y);
            var led = Place(circuit, Led.OfColour(colours[i]), 300, y);

            circuit.Connect(counter.Outputs[i], resistor.A);
            circuit.Connect(resistor.B, led.Anode);
            circuit.Connect(led.Cathode, ledGround.Pin);
        }

        vm.Scope.TimebasePerDivision = 100e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(counter.Outputs[0], "Q0");
        vm.Scope.AddProbe(counter.Outputs[5], "Q5");
        vm.Scope.AddProbe(counter.CarryOut, "Carry");
    }

    /// <summary>
    /// The 4060 clocking itself. Rt from REXT, Ct from CEXT and Rs from RS all meet at one node,
    /// and that node plus the two inverters inside the chip is the whole oscillator — the scope
    /// shows it charging and being thrown past the rail on every flip. The counter then divides it
    /// down, which is why one chip and three passives make a timer.
    /// </summary>
    public static void Load4060Timer(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "4060 self-clocking timer";

        var supply = Place(circuit, new DcVoltageSource(5.0), -420, 120);
        var gnd = Place(circuit, new Ground(), -420, 260);
        var timingGround = Place(circuit, new Ground(), 60, 340);
        var ledGround = Place(circuit, new Ground(), 400, 260);

        var counter = Place(circuit, new Ic4060(), -120, 0);
        var rt = Place(circuit, new Resistor(10e3), 60, -80);
        var ct = Place(circuit, new Capacitor(100e-9), 60, 0);
        var rs = Place(circuit, new Resistor(47e3), 60, 80);

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(counter.Vcc, supply.Positive);
        circuit.Connect(counter.Gnd, gnd.Pin);
        circuit.Connect(counter.MasterReset, gnd.Pin);

        // The three timing components meet at one node, which is the timing node itself.
        circuit.Connect(counter.ResistorPin, rt.A);
        circuit.Connect(counter.CapacitorPin, ct.A);
        circuit.Connect(counter.ClockOscillator, rs.A);
        circuit.Connect(rt.B, ct.B);
        circuit.Connect(ct.B, rs.B);

        var limiter = Place(circuit, new Resistor(330), 240, 160);
        var led = Place(circuit, Led.OfColour("Green"), 400, 160);
        // Q6: the oscillator divided by 64, which is slow enough to watch and fast enough to see
        // change inside one screen of the scope.
        circuit.Connect(counter.Outputs[2], limiter.A);
        circuit.Connect(limiter.B, led.Anode);
        circuit.Connect(led.Cathode, ledGround.Pin);

        // The timing node needs a ground reference for the scope to make sense of it.
        circuit.Connect(timingGround.Pin, gnd.Pin);

        vm.Scope.TimebasePerDivision = 20e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(counter.ClockOscillator, "Osc");
        vm.Scope.AddProbe(counter.Outputs[0], "Q4");
        vm.Scope.AddProbe(counter.Outputs[2], "Q6");
    }

    /// <summary>
    /// A 4051 is not a logic part: its channels carry whatever analog voltage you put on them. Here
    /// a 4040 counts on its three low stages, the 4051 walks the taps of a resistor ladder, and
    /// the common pin produces a staircase — one chip doing what eight switches would.
    /// </summary>
    public static void LoadStaircase(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "4040 and 4051 staircase generator";

        var supply = Place(circuit, new DcVoltageSource(5.0), -460, 160);
        var gnd = Place(circuit, new Ground(), -460, 300);
        var ladderGround = Place(circuit, new Ground(), 300, 380);

        var clock = Place(circuit, new ClockSource(400.0) { Levels = LogicLevels.Cmos5V }, -340, -60);
        var counter = Place(circuit, new Ic4040(), -160, 0);
        var mux = Place(circuit, new Ic4051(), 80, 0);
        var load = Place(circuit, new Resistor(100e3), 260, -180);

        circuit.Connect(supply.Negative, gnd.Pin);
        foreach (var vcc in new[] { counter.Vcc, mux.Vcc }) circuit.Connect(vcc, supply.Positive);
        foreach (var pin in new[] { counter.Gnd, mux.Gnd, counter.Reset, mux.Inhibit, mux.NegativeSupply })
            circuit.Connect(pin, gnd.Pin);

        circuit.Connect(clock.Out, counter.Clock);

        // Three stages of the counter are three address bits, so it sweeps the eight channels.
        circuit.Connect(counter.Outputs[0], mux.A);
        circuit.Connect(counter.Outputs[1], mux.B);
        circuit.Connect(counter.Outputs[2], mux.C);

        // A ladder of eight equal resistors from the rail to ground, tapped at every junction.
        var previous = supply.Positive;
        for (var i = 7; i >= 0; i--)
        {
            var rung = Place(circuit, new Resistor(1e3), 420, -180 + ((7 - i) * 50));
            circuit.Connect(previous, rung.A);
            circuit.Connect(rung.A, mux.Channel(i));
            previous = rung.B;
        }

        circuit.Connect(previous, ladderGround.Pin);

        circuit.Connect(mux.Common, load.A);
        circuit.Connect(load.B, gnd.Pin);

        vm.Scope.TimebasePerDivision = 5e-3;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.AddProbe(mux.Common, "Staircase");
        vm.Scope.AddProbe(counter.Outputs[0], "Q1");
    }

    /// <summary>
    /// A JFET is a depletion device, so it conducts with the gate at zero and biases itself: the
    /// source resistor lifts the source above the gate, which is the same as pulling the gate
    /// negative, and the drain current settles where the two agree. No divider, no coupling to the
    /// supply — which is most of why the part is still around.
    /// </summary>
    public static void LoadJfetAmplifier(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "2N3819 common-source amplifier";

        var supply = Place(circuit, new DcVoltageSource(15.0), -420, 60);
        var signal = Place(circuit, new FunctionGenerator(Waveform.Sine, 1e3, 0.4), -420, -160);
        var coupling = Place(circuit, new Capacitor(100e-9), -240, -160);
        var gateResistor = Place(circuit, new Resistor(1e6), -140, -40);
        var jfet = Place(circuit, new JunctionFet(JfetModel.J2N3819), 40, -80);
        var drainResistor = Place(circuit, new Resistor(2.2e3), 40, -240);
        var sourceResistor = Place(circuit, new Resistor(470), 40, 60);
        var bypass = Place(circuit, new Capacitor(10e-6), 200, 60);

        var gnd = Place(circuit, new Ground(), -420, 200);
        var gateGround = Place(circuit, new Ground(), -140, 80);
        var sourceGround = Place(circuit, new Ground(), 120, 200);

        gateResistor.RotationDegrees = 90;
        drainResistor.RotationDegrees = 90;
        sourceResistor.RotationDegrees = 90;
        bypass.RotationDegrees = 90;

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(signal.Return, gnd.Pin);

        circuit.Connect(signal.Output, coupling.A);
        circuit.Connect(coupling.B, jfet.Gate);
        circuit.Connect(jfet.Gate, gateResistor.A);
        circuit.Connect(gateResistor.B, gateGround.Pin);

        circuit.Connect(supply.Positive, drainResistor.A);
        circuit.Connect(drainResistor.B, jfet.Drain);

        // Self-bias, with the source resistor bypassed so the gain is not degenerated away.
        circuit.Connect(jfet.Source, sourceResistor.A);
        circuit.Connect(sourceResistor.B, sourceGround.Pin);
        circuit.Connect(jfet.Source, bypass.A);
        circuit.Connect(bypass.B, sourceGround.Pin);

        vm.Scope.TimebasePerDivision = 500e-6;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.AddProbe(jfet.Gate, "In");
        vm.Scope.AddProbe(jfet.Drain, "Out");
    }

    public static void LoadFullWaveRectifier(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Full-wave rectifier";

        // A 1:1 transformer against a 40 V peak-to-peak supply gives about 14 V RMS on the whole
        // secondary, so each half swings about ten volts either side of the tap.
        var mains = Place(circuit, new FunctionGenerator
        {
            Shape = Waveform.Sine,
            Frequency = 50,
            AmplitudePeakToPeak = 40,
        }, -440, 0);

        var transformer = Place(circuit, new CentreTappedTransformer(1.0, 1.0, 0.999), -240, 0);
        var upper = Place(circuit, new Diode(DiodeModel.D1N4001), -60, -120);
        var lower = Place(circuit, new Diode(DiodeModel.D1N4001), -60, 120);
        var reservoir = Place(circuit, new ElectrolyticCapacitor(470e-6) { VoltageRating = 35 }, 160, 140);
        var load = Place(circuit, new Resistor(470), 340, 140);
        var ground = Place(circuit, new Ground(), -240, 320);
        var rail = Place(circuit, new Ground(), 250, 300);

        reservoir.RotationDegrees = 90;
        load.RotationDegrees = 90;

        circuit.Connect(mains.Return, ground.Pin);
        circuit.Connect(mains.Output, transformer.P1);
        circuit.Connect(transformer.P2, ground.Pin);

        // The tap is the zero volt line. Each end takes its turn at being the positive one, which
        // is what makes two diodes enough.
        circuit.Connect(transformer.CentreTap, ground.Pin);
        circuit.Connect(transformer.S1, upper.Anode);
        circuit.Connect(transformer.S2, lower.Anode);
        circuit.Connect(upper.Cathode, reservoir.A);
        circuit.Connect(lower.Cathode, reservoir.A);
        circuit.Connect(reservoir.B, rail.Pin);
        circuit.Connect(reservoir.A, load.A);
        circuit.Connect(load.B, rail.Pin);

        vm.Scope.TimebasePerDivision = 5e-3;
        vm.Scope.VoltsPerDivision = 5.0;
        vm.Scope.Layout = ScopeLayout.Unified;
        vm.Scope.AddProbe(transformer.S1, "Half A");
        vm.Scope.AddProbe(transformer.S2, "Half B");
        vm.Scope.AddProbe(load.A, "Rectified");

        vm.Simulation.SpeedFactor = 1.0;
    }

    public static void LoadI2cClock(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "I2C real-time clock";

        var rail = Place(circuit, new DcVoltageSource(3.3), -420, 120);

        // Point at register zero, then read three bytes: seconds, minutes and hours, all in BCD.
        var master = Place(circuit, new I2cMaster
        {
            Transactions = "w 68 00; r 68 3",
            ClockFrequency = 100e3,
        }, -180, -40);

        var clock = Place(circuit, new Ds1307 { StartHour = 11, StartMinute = 59, StartSecond = 50 },
            220, -40);

        var sdaPull = Place(circuit, new Resistor(4.7e3), 20, -180);
        var sclPull = Place(circuit, new Resistor(4.7e3), 140, -180);

        var gnd = Place(circuit, new Ground(), -420, 260);
        var gnd2 = Place(circuit, new Ground(), 220, 160);

        sdaPull.RotationDegrees = 90;
        sclPull.RotationDegrees = 90;

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(master.Vcc, rail.Positive);
        circuit.Connect(master.Gnd, gnd.Pin);
        circuit.Connect(clock.Vcc, rail.Positive);
        circuit.Connect(clock.Gnd, gnd2.Pin);

        circuit.Connect(master.Sda, clock.Sda);
        circuit.Connect(master.Scl, clock.Scl);

        circuit.Connect(rail.Positive, sdaPull.A);
        circuit.Connect(sdaPull.B, master.Sda);
        circuit.Connect(rail.Positive, sclPull.A);
        circuit.Connect(sclPull.B, master.Scl);

        vm.Scope.TimebasePerDivision = 100e-6;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(master.Scl, "SCL");
        vm.Scope.AddProbe(master.Sda, "SDA");
    }

    public static void LoadUltrasonicRanger(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Ultrasonic ranger";

        var rail = Place(circuit, new DcVoltageSource(5.0), -420, 60);

        // A hundred pings a second, each trigger fifty microseconds wide — comfortably over the ten
        // the part insists on, and far enough apart that a fifty centimetre echo of just under
        // three milliseconds finishes well before the next one goes out.
        var trigger = Place(circuit, new ClockSource(100) { DutyCycle = 0.005 }, -220, -80);
        var ranger = Place(circuit, new UltrasonicRanger { DistanceCentimetres = 50 }, 160, 0);
        var load = Place(circuit, new Resistor(10e3), 420, 120);
        var gnd = Place(circuit, new Ground(), -420, 200);
        var gnd2 = Place(circuit, new Ground(), 160, 260);

        load.RotationDegrees = 90;

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(ranger.Vcc, rail.Positive);
        circuit.Connect(ranger.Gnd, gnd2.Pin);
        circuit.Connect(trigger.Out, ranger.Trigger);
        circuit.Connect(ranger.Echo, load.A);
        circuit.Connect(load.B, gnd2.Pin);

        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(ranger.Trigger, "TRIG");
        vm.Scope.AddProbe(ranger.Echo, "ECHO");

        // The interesting part is milliseconds wide, so the default thousandth of real time leaves
        // you waiting a quarter of a minute between pings.
        vm.Simulation.SpeedFactor = 0.01;
    }

    public static void LoadSerialLink(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Serial link";

        var rail = Place(circuit, new DcVoltageSource(3.3), -420, 140);

        var terminal = Place(circuit, new SerialTerminal
        {
            BaudRate = 9600,
            Message = "hi\r\n",
            StartDelay = 500e-6,

            // Every five milliseconds, so there is always traffic on the scope rather than one
            // burst that has scrolled away by the time you look.
            RepeatInterval = 5e-3,
        }, -180, 0);

        var device = Place(circuit, new SerialDevice
        {
            BaudRate = 9600,
            Greeting = "READY\r\n",
        }, 220, 0);

        var gnd = Place(circuit, new Ground(), -420, 300);
        var gnd2 = Place(circuit, new Ground(), 220, 200);

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(terminal.Vcc, rail.Positive);
        circuit.Connect(terminal.Gnd, gnd.Pin);
        circuit.Connect(device.Vcc, rail.Positive);
        circuit.Connect(device.Gnd, gnd2.Pin);

        // Crossed over: each end's transmit goes to the other end's receive. Joining TX to TX is
        // the mistake everybody makes once, and it is completely silent.
        circuit.Connect(terminal.Transmit, device.Receive);
        circuit.Connect(device.Transmit, terminal.Receive);

        vm.Scope.TimebasePerDivision = 200e-6;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(terminal.Transmit, "Terminal TX");
        vm.Scope.AddProbe(device.Transmit, "Device TX");

        // A bit is a hundred microseconds at this rate, so real time would be a blur.
        vm.Simulation.SpeedFactor = 0.01;
    }

    public static void LoadLightMeter(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Light meter";

        var rail = Place(circuit, new DcVoltageSource(3.3), -460, 140);

        // Collector to the rail, emitter into a load: more light, more current, more volts across
        // the resistor. A phototransistor gives you a current, and this is what turns it into
        // something an ADC can read.
        var sensor = Place(circuit, new Phototransistor { Illuminance = 300, AlternateIlluminance = 5 },
            -220, -40);
        var load = Place(circuit, new Resistor(4.7e3), -220, 120);

        var master = Place(circuit, new I2cMaster
        {
            Transactions = "w 48 00; r 48 2",
            ClockFrequency = 100e3,
            StartDelay = 2e-3,
        }, 60, -240);

        var adc = Place(circuit, new Ads1115(), 380, -40);

        var sdaPull = Place(circuit, new Resistor(4.7e3), 200, -400);
        var sclPull = Place(circuit, new Resistor(4.7e3), 320, -400);

        var gnd = Place(circuit, new Ground(), -460, 300);
        var gnd2 = Place(circuit, new Ground(), -220, 260);
        var gnd3 = Place(circuit, new Ground(), 380, 200);

        load.RotationDegrees = 90;
        sdaPull.RotationDegrees = 90;
        sclPull.RotationDegrees = 90;

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, sensor.Collector);
        circuit.Connect(sensor.Emitter, load.A);
        circuit.Connect(load.B, gnd2.Pin);

        // The node between the two is what gets measured.
        circuit.Connect(sensor.Emitter, adc.Input(0));

        circuit.Connect(master.Vcc, rail.Positive);
        circuit.Connect(master.Gnd, gnd.Pin);
        circuit.Connect(adc.Vcc, rail.Positive);
        circuit.Connect(adc.Gnd, gnd3.Pin);

        circuit.Connect(master.Sda, adc.Sda);
        circuit.Connect(master.Scl, adc.Scl);

        circuit.Connect(rail.Positive, sdaPull.A);
        circuit.Connect(sdaPull.B, master.Sda);
        circuit.Connect(rail.Positive, sclPull.A);
        circuit.Connect(sclPull.B, master.Scl);

        vm.Scope.TimebasePerDivision = 200e-6;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(sensor.Emitter, "Sensor");
        vm.Scope.AddProbe(master.Sda, "SDA");
    }

    public static void LoadMotorReversing(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Motor reversing";

        var logic = Place(circuit, new DcVoltageSource(5.0), -520, -40);
        var supply = Place(circuit, new DcVoltageSource(12.0), -520, 130);

        // Enable is held on; the two inputs are switches you can flip while it runs. Both the
        // same is a brake, one of each drives, and dropping enable lets it coast.
        var enable = Place(circuit, new ToggleSwitch { IsClosed = true }, -250, -190);
        var in1 = Place(circuit, new ToggleSwitch { IsClosed = true }, -250, -110);
        var in2 = Place(circuit, new ToggleSwitch(), -250, -30);

        var bridge = Place(circuit, new HBridge(), 90, -60);
        var motor = Place(circuit, new DcMotor(), 430, -60);

        var gnd = Place(circuit, new Ground(), -520, 270);
        var gnd2 = Place(circuit, new Ground(), 90, 150);

        circuit.Connect(logic.Negative, gnd.Pin);
        circuit.Connect(supply.Negative, gnd.Pin);

        circuit.Connect(bridge.LogicSupply, logic.Positive);
        circuit.Connect(bridge.MotorSupply, supply.Positive);
        circuit.Connect(bridge.Gnd, gnd2.Pin);

        foreach (var (contact, pin) in new[]
                 {
                     (enable, bridge.Enable),
                     (in1, bridge.Input1),
                     (in2, bridge.Input2),
                 })
        {
            circuit.Connect(logic.Positive, contact.A);
            circuit.Connect(contact.B, pin);
        }

        circuit.Connect(bridge.Output1, motor.A);
        circuit.Connect(motor.B, bridge.Output2);

        vm.Scope.TimebasePerDivision = 50e-3;
        vm.Scope.VoltsPerDivision = 5.0;
        vm.Scope.Layout = ScopeLayout.Unified;
        vm.Scope.AddProbe(bridge.Output1, "OUT1");
        vm.Scope.AddProbe(bridge.Output2, "OUT2");

        vm.Simulation.SpeedFactor = 1.0;
    }

    public static void LoadNoiseAndHysteresis(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Noise and hysteresis";

        var rail = Place(circuit, new DcVoltageSource(5.0), -520, 180);

        // A slow triangle creeping through the threshold, with noise riding on it. Either alone
        // is well behaved; together they are what makes a bare comparator useless.
        var ramp = Place(circuit, new FunctionGenerator
        {
            Shape = Waveform.Triangle,
            Frequency = 20,
            AmplitudePeakToPeak = 2.0,
            DcOffset = 2.5,
        }, -520, -160);

        var noise = Place(circuit, new NoiseSource
        {
            RmsVoltage = 12e-3,
            Bandwidth = 50e3,
        }, -280, -160);

        var comparator = Place(circuit, new Comparator(ComparatorModel.Lm393), 60, -140);
        var reference = Place(circuit, new DcVoltageSource(2.5), -280, 40);

        // The pull-up an open-collector comparator cannot work without.
        var pullUp = Place(circuit, new Resistor(4.7e3), 300, -260);

        // The feedback resistor that turns one threshold into two. Large against the source
        // impedance, so it moves the reference by tens of millivolts rather than volts — which is
        // all it takes to outrun the noise.
        var hysteresis = Place(circuit, new Resistor(470e3), 60, 40);
        var source = Place(circuit, new Resistor(10e3), -100, -160);

        var gnd = Place(circuit, new Ground(), -520, 340);
        var gnd2 = Place(circuit, new Ground(), 60, 180);

        pullUp.RotationDegrees = 90;

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(ramp.Return, gnd.Pin);
        circuit.Connect(reference.Negative, gnd.Pin);

        circuit.Connect(comparator.PositiveSupply, rail.Positive);
        circuit.Connect(comparator.NegativeSupply, gnd2.Pin);

        // Signal, then noise in series with it, then a resistor into the non-inverting input.
        // That resistor is what the feedback works against: hysteresis is the divider between the
        // two, and without it the feedback fights the generator's own fifty ohms and moves the
        // threshold by a millivolt, which is nothing against the noise.
        circuit.Connect(ramp.Output, noise.A);
        circuit.Connect(noise.B, source.A);
        circuit.Connect(source.B, comparator.NonInverting);

        circuit.Connect(reference.Positive, comparator.Inverting);

        circuit.Connect(rail.Positive, pullUp.A);
        circuit.Connect(pullUp.B, comparator.Output);

        // Output back to the non-inverting input: that is the hysteresis. Delete this resistor
        // and the chatter comes back.
        circuit.Connect(comparator.Output, hysteresis.A);
        circuit.Connect(hysteresis.B, comparator.NonInverting);

        vm.Scope.TimebasePerDivision = 5e-3;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(comparator.NonInverting, "Noisy ramp");
        vm.Scope.AddProbe(comparator.Output, "Output");

        vm.Simulation.SpeedFactor = 0.05;
    }

    /// <summary>
    /// A metre of coax driven by a fast edge, with a terminator that can be switched in while it
    /// runs. Open, the far end doubles the wave and the near end knows nothing about it for
    /// another whole delay; terminated, the wave is absorbed and one clean edge arrives late.
    /// </summary>
    public static void LoadReflections(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Reflections on a transmission line";

        // Matched source: 50 Ω out, so whatever comes home is absorbed rather than sent out again.
        var driver = Place(circuit, new FunctionGenerator(Waveform.Square, 20e6, 10.0)
        {
            DcOffset = 5.0, EdgeTime = 100e-12, OutputResistance = 50.0,
        }, -420, 0);

        var line = Place(circuit, new TransmissionLine
        {
            CharacteristicImpedance = 50, Length = 1.0, VelocityFactor = 0.66,
        }, -80, 0);

        var terminator = Place(circuit, new Resistor(50.0), 260, 100);
        var switchIn = Place(circuit, new ToggleSwitch(), 260, -60);

        // Something has to reference the far end while the terminator is switched out.
        var leak = Place(circuit, new Resistor(1e6), 400, 100);

        var gnd = Place(circuit, new Ground(), -420, 200);
        var gnd2 = Place(circuit, new Ground(), -80, 200);
        var gnd3 = Place(circuit, new Ground(), 260, 240);
        var gnd4 = Place(circuit, new Ground(), 400, 240);

        terminator.RotationDegrees = 90;
        switchIn.RotationDegrees = 90;
        leak.RotationDegrees = 90;

        circuit.Connect(driver.Return, gnd.Pin);
        circuit.Connect(driver.Output, line.NearPlus);
        circuit.Connect(line.NearMinus, gnd2.Pin);
        circuit.Connect(line.FarMinus, gnd2.Pin);

        circuit.Connect(line.FarPlus, switchIn.A);
        circuit.Connect(switchIn.B, terminator.A);
        circuit.Connect(terminator.B, gnd3.Pin);

        circuit.Connect(line.FarPlus, leak.A);
        circuit.Connect(leak.B, gnd4.Pin);

        // Five nanoseconds a division, because the whole story is over in twenty.
        vm.Scope.TimebasePerDivision = 5e-9;
        vm.Scope.VoltsPerDivision = 5.0;
        vm.Scope.AddProbe(line.NearPlus, "Near");
        vm.Scope.AddProbe(line.FarPlus, "Far");
    }

    /// <summary>
    /// A bead where one actually belongs: between a rail carrying a couple of hundred megahertz of
    /// rubbish and the supply pin of a chip, whose few tens of picofarads are the only capacitance
    /// up there that matters. Both sides are probed, so the attenuation is the gap between the
    /// traces — and turning the noise bandwidth down in the CONTROLS panel takes it away again,
    /// because a bead only works inside its band.
    /// </summary>
    public static void LoadFerriteBead(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Ferrite bead feeding a chip";

        var rail = Place(circuit, new DcVoltageSource(5.0), -460, 60);

        // The noise the rest of the board is putting back into the rail, up where a bead works.
        var noise = Place(circuit, new NoiseSource
        {
            RmsVoltage = 0.2, Bandwidth = 200e6, SeriesResistance = 5.0,
        }, -300, -120);

        var bead = Place(circuit, new FerriteBead(600) { PeakFrequency = 100e6 }, -60, -120);

        // The chip's own pin capacitance, which at these frequencies is the whole of the load's
        // reactance. A hundred nanofarads down here would resonate with the bead at half a
        // megahertz, where the bead is still an inductor and no help at all.
        var decoupling = Place(circuit, new Capacitor(47e-12), 120, -20);
        var load = Place(circuit, new Resistor(100.0), 300, -20);

        var gnd = Place(circuit, new Ground(), -460, 200);
        var gnd2 = Place(circuit, new Ground(), 120, 140);
        var gnd3 = Place(circuit, new Ground(), 300, 140);

        decoupling.RotationDegrees = 90;
        load.RotationDegrees = 90;

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, noise.A);
        circuit.Connect(noise.B, bead.A);
        circuit.Connect(bead.B, decoupling.A);
        circuit.Connect(decoupling.B, gnd2.Pin);
        circuit.Connect(bead.B, load.A);
        circuit.Connect(load.B, gnd3.Pin);

        vm.Scope.TimebasePerDivision = 20e-9;
        vm.Scope.VoltsPerDivision = 0.2;
        vm.Scope.AddProbe(bead.A, "Before");
        vm.Scope.AddProbe(bead.B, "After");
    }

    /// <summary>
    /// The same clock into two identical MOSFETs — one gate straight off the logic pin, the other
    /// through a driver. Both gates are probed, and the difference is the point.
    /// </summary>
    public static void LoadGateDriver(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Driving a MOSFET gate";

        var rail = Place(circuit, new DcVoltageSource(12.0), -560, 380);
        var logic = Place(circuit, new ClockSource(20e3) { Levels = LogicLevels.Cmos33V }, -560, -120);

        var driver = Place(circuit, new GateDriver(), -220, -300);

        // The two halves well apart, each with its own load above it and its own ground below,
        // so that neither column has anything of the other's in it.
        var drivenFet = Place(circuit, new Mosfet(MosfetModel.IrlZ44N), 200, -300);
        var directFet = Place(circuit, new Mosfet(MosfetModel.IrlZ44N), 200, 220);

        var drivenLoad = Place(circuit, new Resistor(24.0), 200, -480);
        var directLoad = Place(circuit, new Resistor(24.0), 200, 40);

        var gnd = Place(circuit, new Ground(), -560, 500);
        var gnd2 = Place(circuit, new Ground(), -220, -100);
        var gnd3 = Place(circuit, new Ground(), 360, -180);
        var gnd4 = Place(circuit, new Ground(), 360, 340);

        drivenLoad.RotationDegrees = 90;
        directLoad.RotationDegrees = 90;

        circuit.Connect(rail.Negative, gnd.Pin);

        circuit.Connect(driver.Vcc, rail.Positive);
        circuit.Connect(driver.Gnd, gnd2.Pin);
        circuit.Connect(logic.Out, driver.Input);
        circuit.Connect(driver.Output, drivenFet.Gate);

        // And the same pin straight onto the other gate, which is how it is done the first time.
        circuit.Connect(logic.Out, directFet.Gate);

        circuit.Connect(rail.Positive, drivenLoad.A);
        circuit.Connect(drivenLoad.B, drivenFet.Drain);
        circuit.Connect(drivenFet.Source, gnd3.Pin);

        circuit.Connect(rail.Positive, directLoad.A);
        circuit.Connect(directLoad.B, directFet.Drain);
        circuit.Connect(directFet.Source, gnd4.Pin);

        vm.Scope.TimebasePerDivision = 2e-6;
        vm.Scope.VoltsPerDivision = 5.0;
        vm.Scope.AddProbe(drivenFet.Gate, "Driven");
        vm.Scope.AddProbe(directFet.Gate, "Direct");
    }

    /// <summary>
    /// The whole DS18B20 exchange on one wire: reset, skip addressing, convert, wait, read the
    /// scratchpad. Nine bits rather than twelve, so the conversion is ninety milliseconds instead
    /// of three quarters of a second and the run is watchable.
    /// </summary>
    public static void LoadOneWireThermometer(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "DS18B20 on one wire";

        var rail = Place(circuit, new DcVoltageSource(5.0), -460, 120);

        var master = Place(circuit, new OneWireMaster
        {
            // Set nine-bit resolution first, so the conversion is quick enough to watch.
            Operations = "reset; w CC 4E 4B 46 1F; reset; w CC 44; d 95000; reset; w CC BE; r 9",
            StartDelay = 1e-3,
        }, -160, 0);

        var pullUp = Place(circuit, new Resistor(4.7e3), 80, -200);
        var sensor = Place(circuit, new Ds18b20 { Temperature = 22.0, AlternateTemperature = 36.0 }, 340, 0);

        var gnd = Place(circuit, new Ground(), -460, 260);
        var gnd2 = Place(circuit, new Ground(), -160, 200);
        var gnd3 = Place(circuit, new Ground(), 340, 200);

        pullUp.RotationDegrees = 90;

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(master.Vcc, rail.Positive);
        circuit.Connect(master.Gnd, gnd2.Pin);

        // Externally powered rather than parasitic, which is the arrangement that simply works.
        circuit.Connect(sensor.Vdd, rail.Positive);
        circuit.Connect(sensor.Gnd, gnd3.Pin);

        circuit.Connect(pullUp.A, rail.Positive);
        circuit.Connect(pullUp.B, master.Data);
        circuit.Connect(master.Data, sensor.Data);

        vm.Scope.TimebasePerDivision = 5e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.AddProbe(master.Data, "DQ");
    }

    /// <summary>
    /// A number turned into a voltage and measured back again. The DAC's code is a live control,
    /// so moving the slider moves the voltage and the ADC follows it.
    /// </summary>
    public static void LoadDacAndAdc(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "DAC out, ADC back";

        var rail = Place(circuit, new DcVoltageSource(5.0), -520, 160);

        var dac = Place(circuit, new Mcp4725 { Code = 2048 }, -220, 0);

        // A follower, because the DAC cannot drive anything: take the load off it directly and the
        // voltage sags away from the number you asked for.
        var buffer = Place(circuit, new OperationalAmplifier(OpAmpModel.Mcp6002), 120, 60);
        var load = Place(circuit, new Resistor(2.2e3), 300, 200);
        var adc = Place(circuit, new Ads1115(), 480, -40);

        var sdaPull = Place(circuit, new Resistor(4.7e3), -60, -300);
        var sclPull = Place(circuit, new Resistor(4.7e3), 60, -300);

        var gnd = Place(circuit, new Ground(), -520, 320);
        var gnd2 = Place(circuit, new Ground(), -220, 200);
        var gnd3 = Place(circuit, new Ground(), 300, 340);
        var gnd4 = Place(circuit, new Ground(), 480, 260);

        load.RotationDegrees = 90;
        sdaPull.RotationDegrees = 90;
        sclPull.RotationDegrees = 90;

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(dac.Vcc, rail.Positive);
        circuit.Connect(dac.Gnd, gnd2.Pin);
        circuit.Connect(adc.Vcc, rail.Positive);
        circuit.Connect(adc.Gnd, gnd4.Pin);

        circuit.Connect(buffer.PositiveSupply, rail.Positive);
        circuit.Connect(buffer.NegativeSupply, gnd2.Pin);
        circuit.Connect(dac.Output, buffer.NonInverting);
        circuit.Connect(buffer.Output, buffer.Inverting);
        circuit.Connect(buffer.Output, load.A);
        circuit.Connect(load.B, gnd3.Pin);

        // The ADC measures what the buffer actually delivered, not what the DAC intended.
        circuit.Connect(buffer.Output, adc.Input(0));

        circuit.Connect(sdaPull.A, rail.Positive);
        circuit.Connect(sdaPull.B, dac.Sda);
        circuit.Connect(sclPull.A, rail.Positive);
        circuit.Connect(sclPull.B, dac.Scl);
        circuit.Connect(dac.Sda, adc.Sda);
        circuit.Connect(dac.Scl, adc.Scl);

        vm.Scope.TimebasePerDivision = 20e-3;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.AddProbe(dac.Output, "DAC");
        vm.Scope.AddProbe(buffer.Output, "Buffered");
    }

    /// <summary>
    /// A reed switch clocking a counter. Bring the magnet up once — the CONTROLS panel has the
    /// field — and the counter moves several places, because the blades bounce on the way shut.
    /// </summary>
    public static void LoadReedSwitchBounce(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Reed switch bounce";

        var rail = Place(circuit, new DcVoltageSource(5.0), -460, 140);
        var reed = Place(circuit, new ReedSwitch(), -200, -60);
        var pullUp = Place(circuit, new Resistor(10e3), -200, -240);
        var counter = Place(circuit, new Ic7490(), 180, 0);

        var gnd = Place(circuit, new Ground(), -460, 300);
        var gnd2 = Place(circuit, new Ground(), -200, 120);
        var gnd3 = Place(circuit, new Ground(), 180, 260);

        pullUp.RotationDegrees = 90;

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(pullUp.A, rail.Positive);
        circuit.Connect(pullUp.B, reed.A);
        circuit.Connect(reed.B, gnd2.Pin);

        circuit.Connect(counter.Vcc, rail.Positive);
        circuit.Connect(counter.Gnd, gnd3.Pin);
        circuit.Connect(counter.ClockA, reed.A);

        circuit.Connect(counter.Qa, counter.ClockB);

        // Resets tied low so it just counts.
        circuit.Connect(counter.Reset0A, gnd3.Pin);
        circuit.Connect(counter.Reset0B, gnd3.Pin);
        circuit.Connect(counter.Reset9A, gnd3.Pin);
        circuit.Connect(counter.Reset9B, gnd3.Pin);

        vm.Scope.TimebasePerDivision = 200e-6;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(reed.A, "Contact");
        vm.Scope.AddProbe(counter.Qa, "QA");
        vm.Scope.AddProbe(counter.Qb, "QB");
    }

    /// <summary>
    /// A PIR holding a lamp on. Turn Movement on in the CONTROLS panel, turn it straight off
    /// again, and the lamp stays lit for the whole of the hold.
    /// </summary>
    public static void LoadMotionLight(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "PIR motion light";

        var rail = Place(circuit, new DcVoltageSource(5.0), -420, 140);

        // Short warm-up, so the example does something in the first half minute.
        var pir = Place(circuit, new PirSensor
        {
            HoldSeconds = 4.0, WarmUpSeconds = 1.0, IsRetriggerable = true,
        }, -120, 0);

        var resistor = Place(circuit, new Resistor(220.0), 200, -60);
        var lamp = Place(circuit, Led.OfColour("Amber"), 200, 100);

        var gnd = Place(circuit, new Ground(), -420, 300);
        var gnd2 = Place(circuit, new Ground(), -120, 200);
        var gnd3 = Place(circuit, new Ground(), 200, 240);

        resistor.RotationDegrees = 90;
        lamp.RotationDegrees = 90;

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(pir.Vcc, rail.Positive);
        circuit.Connect(pir.Gnd, gnd2.Pin);

        circuit.Connect(pir.Output, resistor.A);
        circuit.Connect(resistor.B, lamp.Anode);
        circuit.Connect(lamp.Cathode, gnd3.Pin);

        vm.Scope.TimebasePerDivision = 1.0;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.AddProbe(pir.Output, "PIR out");
    }

    private static T Place<T>(Circuit circuit, T component, double x, double y) where T : CircuitComponent
    {
        component.X = x;
        component.Y = y;
        circuit.Add(component);
        return component;
    }
}
