using Cirq.Components.Digital;
using Cirq.Components.Nonlinear;
using Cirq.Components.Ics;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
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
        vm.Scope.Layout = ScopeLayout.Stacked;
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
        vm.Scope.Layout = ScopeLayout.Stacked;
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
        vm.Scope.Layout = ScopeLayout.Stacked;
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
        vm.Scope.Layout = ScopeLayout.Stacked;
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

    private static T Place<T>(Circuit circuit, T component, double x, double y) where T : CircuitComponent
    {
        component.X = x;
        component.Y = y;
        circuit.Add(component);
        return component;
    }
}
