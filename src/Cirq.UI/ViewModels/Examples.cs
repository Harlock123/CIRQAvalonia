using Cirq.Core.Probing;
using Cirq.Components.Boards;
using Cirq.Components.Bridges;
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
public sealed record ExampleCircuit(string Name, string Description, Action<MainWindowViewModel> Build)
{
    /// <summary>Which group it is filed under, filled in when the groups are flattened.</summary>
    public string Category { get; init; } = string.Empty;
}

/// <summary>A named group of examples, as the palette has for components.</summary>
/// <param name="Name">The heading.</param>
/// <param name="Items">What is in it.</param>
public sealed record ExampleCategory(string Name, IReadOnlyList<ExampleCircuit> Items);

/// <summary>
/// Pre-wired circuits that exercise each part of the engine, so the application has something
/// running the moment it opens.
/// </summary>
public static class Examples
{
    /// <summary>
    /// Every example, grouped the way the palette groups components.
    /// <para>
    /// There are enough of these now that a flat list was the wrong shape: a menu of forty-odd
    /// entries is something to be got through rather than something to browse, and it gets worse
    /// with each one added. Grouping them costs a category name per example and makes the set
    /// searchable instead.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ExampleCategory> Categories { get; } =
    [
        new("Fundamentals",
        [
            new("RC Low-Pass", "Step response of a first-order filter", LoadRcLowPass),
            new("Transistor Switch", "NPN driving an LED from a push button", LoadTransistorSwitch),
            new("Analog and Logic", "The two parts that live on the boundary, and why one has two thresholds",
                LoadAnalogBoundary),
            new("MOSFET Driver", "Logic-level MOSFET switching an inductive load", LoadMosfetDriver),
        ]),

        new("Analog",
        [
            new("Inverting Amplifier", "LM741 with a gain of -10", LoadInvertingAmplifier),
            new("Loop Stability", "A follower with a loop probe in it — open Simulate > Stability, then add the capacitive load and watch the phase margin go",
                LoadLoopStability),
            new("Crossover Distortion", "A class-B pair and the notch it puts in every waveform — open Simulate > Spectrum and read the THD",
                LoadCrossoverDistortion),
            new("Low-Noise Preamp", "The same gain from big resistors and from small ones — open Simulate > Noise and see which part is making it",
                LoadLowNoisePreamp),
            new("Comparator Trigger", "LM311 squaring up a sine wave", LoadComparatorTrigger),
            new("Window Detector", "LM339 outputs wired together to flag an out-of-range voltage",
                LoadWindowDetector),
            new("JFET Amplifier", "2N3819 common-source stage with self-bias", LoadJfetAmplifier),
            new("Rail to Rail", "The same follower three times — an LM741, an LM358 and an MCP6002 on one 5 V supply",
                LoadRailToRail),
            new("Analog Switch", "A 4066 picking one of four sources — and what closing two does",
                LoadAnalogSwitch),
            new("Noise and Hysteresis", "A comparator chattering on a noisy ramp — add hysteresis and it stops",
                LoadNoiseAndHysteresis),
            new("Quad Op-Amp", "One LM324 doing four jobs on a single supply — bias, buffer, gain and invert",
                LoadQuadOpAmpChain),
        ]),

        new("Power Supplies",
        [
            new("Half-Wave Rectifier", "Diode and smoothing capacitor", LoadRectifier),
            new("Full-Wave Rectifier", "A centre-tapped secondary and two diodes — one drop, not a bridge's two",
                LoadFullWaveRectifier),
            new("Regulated Supply", "7805 holding 5 V from a 12 V rail", LoadRegulatedSupply),
            new("Linear Power Supply", "Mains secondary through a bridge and reservoir into a 7812",
                LoadLinearPowerSupply),
            new("Adjustable Supply", "Transformer, bridge, reservoir and a 7805 made adjustable — turn RV1 and watch it move",
                LoadAdjustableSupply),
            new("Solar Panel", "A panel and an adjustable load — turn RV1 and find the maximum power point",
                LoadSolarPanel),
            new("Resettable Fuse", "A PPTC holding a short circuit at bay — close SW1, and it does not open, it heats",
                LoadResettableFuse),
            new("Lithium Charge Cycle", "Constant current to 4.2 V, then constant voltage tapering away",
                LoadLithiumCharge),
            new("Surge Protection", "A varistor takes the energy and a TVS clamps what gets past it",
                LoadSurgeProtection),
            new("Negative Rail", "An ICL7660 making -5 V from +5 V, and an amplifier that needs it",
                LoadNegativeRail),
            new("Battery Resistance", "Three cells, one load each — the number nobody reads off the packet",
                LoadBatteryInternalResistance),
            new("Buck Converter", "MC34063 stepping 12 V down to 5 V without turning the difference into heat",
                LoadBuckConverter),
        ]),

        new("Switching & Motors",
        [
            new("Gate Driver", "The same clock into two MOSFETs, one straight from the pin and one through a driver",
                LoadGateDriver),
            new("SCR Latch", "A push button fires it and only interrupting the anode turns it off",
                LoadScrLatch),
            new("Lamp Dimmer", "Triac and diac phase control — move the firing angle and the lamp dims",
                LoadLampDimmer),
            new("Relay Driver", "Logic to a coil through an optocoupler and a Darlington array",
                LoadRelayDriver),
            new("IGBT and MOSFET", "The same load switched by each — a voltage drop against a resistance",
                LoadIgbtAgainstMosfet),
            new("Curve Tracer", "A transistor set up for Simulate > DC Sweep — step the base current and get the datasheet fan",
                LoadCurveTracer),
            new("Servo Sweep", "The angle is the width of the pulse — turn Duty and watch it move",
                LoadServoSweep),
            new("Stepper Motor", "A 4017 walking four windings through a ULN2003",
                LoadStepper),
            new("Microstepping Drive", "An A4988 chopping a bipolar motor's current — step and direction in, a sine out",
                LoadMicrosteppingDrive),
            new("Zero-Crossing Switch", "A solid-state relay that refuses to fire until the mains passes through zero",
                LoadZeroCrossingSwitch),
            new("Motor Reversing", "An H-bridge running a motor both ways — forward, brake, reverse, coast",
                LoadMotorReversing),
        ]),

        new("Digital Logic",
        [
            new("NAND Latch", "Cross-coupled 7400 gates", LoadNandLatch),
            new("Decade Counter", "7490 counting in BCD", LoadDecadeCounter),
            new("Digit Counter", "7490 into a 7447 driving a seven-segment display", LoadDigitCounter),
            new("Running Light", "74164 shift register walking a bit across eight LEDs", LoadRunningLight),
            new("LED Chaser", "4017 walking a lit output along ten LEDs", LoadLedChaser),
            new("Staircase Generator", "4040 addressing a 4051 to step through a resistor ladder",
                LoadStaircase),
            new("Addressed Memory", "A counter walking a ROM, and a latch holding what came back",
                LoadAddressedMemory),
            new("Shared Bus", "Two transceivers taking turns on eight wires — the only tri-state in here",
                LoadSharedBus),
            new("Ring Oscillator", "Three inverters with unequal delays", LoadRingOscillator),
        ]),

        new("Timers & Oscillators",
        [
            new("555 Astable", "Free-running multivibrator at about 480 Hz", Load555Astable),
            new("4060 Timer", "A 4060 clocking itself from one resistor and one capacitor",
                Load4060Timer),
            new("Crystal Q", "A crystal and a tuned circuit at the same frequency — sweep them and compare",
                LoadCrystalQ),
            new("Phase-Locked Loop", "A 4046 dragging its oscillator onto an incoming signal and holding it",
                LoadPhaseLockedLoop),
        ]),

        new("Buses & Interfaces",
        [
            new("I2C EEPROM", "Three bytes written over two wires and read back again", LoadI2cEeprom),
            new("I2C Clock", "A DS1307 read over two wires, its registers in BCD and moving on their own",
                LoadI2cClock),
            new("SPI Shift Register", "An SPI master clocking a byte into a 74595 and onto eight LEDs",
                LoadSpiShiftRegister),
            new("Serial Link", "A UART talking to a module — change one baud rate and watch it break",
                LoadSerialLink),
            new("1-Wire Thermometer", "A DS18B20 read over a single wire — reset, convert, wait, read",
                LoadOneWireThermometer),
            new("DAC and ADC", "A voltage made by one chip and measured by another — move the Code slider",
                LoadDacAndAdc),
            new("Current Sensing", "An INA219 watching a load from the high side of the rail",
                LoadCurrentSensing),
            new("CAN Arbitration", "Two nodes talking at once, and why that is not a collision",
                LoadCanArbitration),
            new("SPI ADC", "An MCP3008 reading a knob — one transaction, answer and question overlapping",
                LoadSpiAdc),
            new("Level Shifting", "A 3.3 V part and a 5 V part on the same wires, both ways at once",
                LoadLevelShifting),
            new("RS-485 Link", "Two transceivers down fifty metres of cable — open SW1 and watch it ring",
                LoadRs485Link),
            new("RS-232 Link", "A MAX232 at each end, making ±8.5 V out of the 5 V rail and inverting on the way",
                LoadRs232Link),
        ]),

        new("Sensors",
        [
            new("Ultrasonic Ranger", "An HC-SR04 triggered on a timer — the distance is the echo's width",
                LoadUltrasonicRanger),
            new("Light Meter", "A phototransistor into an I2C ADC — light, to current, to volts, to a number",
                LoadLightMeter),
            new("Reed Switch Bounce", "One magnet passing, counted several times — which is what debouncing is for",
                LoadReedSwitchBounce),
            new("Motion Light", "A PIR holding a lamp on long after you stop moving",
                LoadMotionLight),
            new("Load Cell", "A strain-gauge bridge into an INA126 — millivolts on top of half a supply",
                LoadLoadCell),
            new("Rotary Encoder", "Quadrature, where the direction is in the phase and not in either output",
                LoadRotaryEncoder),
            new("Thermostat", "An NTC, a comparator and the feedback resistor that stops it chattering",
                LoadThermostat),
            new("Transimpedance Amp", "A photodiode held at zero volts, its current read as a voltage",
                LoadTransimpedance),
            new("Night Light", "An LDR against a TL431 reference, with a switch to override it",
                LoadNightLight),
            new("Thermocouple", "Microvolts per degree into an INA126 — and it measures a difference",
                LoadThermocouple),
            new("Hall Counter", "A Hall switch counting a magnet, cleanly, where a contact could not",
                LoadHallCounter),
        ]),

        // Circuits that exist to demonstrate a measurement rather than a design. Each is set up
        // for one of the analyses and needs nothing changed before it shows what it is for.
        new("Measurement",
        [
            new("Hysteresis Loop", "A comparator's hysteresis drawn as the loop it is — the scope is already in XY",
                LoadHysteresisLoop),
            new("Diode Thermometer", "A forward-biased diode is a thermometer. Run Simulate > DC Sweep over temperature",
                LoadDiodeThermometer),
            new("Resistor Bridge", "Four 5 % resistors that should read nothing. Run Simulate > Tolerance Analysis",
                LoadResistorBridge),
            new("High-Side Sensing", "Millivolts across a shunt at the top of a 24 V rail, read differentially — and the power it costs",
                LoadHighSideSensing),
        ]),

        new("Signal Integrity & RF",
        [
            new("Reflections", "A fast edge down a metre of coax — close SW1 to terminate it and the ringing stops",
                LoadReflections),
            new("Ferrite Bead", "Two hundred megahertz of rubbish on a rail, and the bead that turns it into heat",
                LoadFerriteBead),
            new("Amplitude Modulation", "A carrier multiplied by an audio tone — which is what AM is",
                LoadAmplitudeModulation),
            new("Common-Mode Choke", "It passes the signal and blocks the noise riding on both wires at once",
                LoadCommonModeFilter),
            new("Varactor Tuning", "An LC tank tuned by a voltage — move RV1 and sweep it in Frequency Response",
                LoadVaractorTuning),
        ]),

        new("Audio",
        [
            new("Audio Amplifier", "A microphone into an LM386 into a speaker, and the two capacitors that matter",
                LoadAudioAmplifier),
        ]),

        new("Displays",
        [
            new("Character LCD", "An Arduino driving an HD44780 in four-bit mode — the whole initialisation dance",
                LoadCharacterLcd),
            new("I2C LCD", "The same display on a PCF8574 backpack — two wires instead of six",
                LoadI2cLcd),
        ]),

        new("Development Boards",
        [
            new("Raspberry Pi GPIO", "Pi driving two LEDs and reading a button on an internal pull-up",
                LoadRaspberryPiGpio),
        ]),
    ];

    /// <summary>Every example, flattened — the order the groups above put them in.</summary>
    public static IReadOnlyList<ExampleCircuit> All { get; } =
        [.. Categories.SelectMany(c => c.Items.Select(e => e with { Category = c.Name }))];

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

    /// <summary>
    /// A follower with a break point in its feedback path, set up for
    /// <b>Simulate &gt; Stability</b>.
    /// <para>
    /// A follower is the hardest configuration to keep stable, which surprises people: it has the
    /// most feedback of any, so its loop gain is the highest and its crossover the furthest out —
    /// which is exactly where the second pole a capacitive load makes is waiting. The capacitor is
    /// on the drawing and switched out; close SW1 and watch ninety degrees of phase margin turn
    /// into something that rings.
    /// </para>
    /// </summary>
    public static void LoadLoopStability(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Loop gain and phase margin";

        var positive = Place(circuit, new DcVoltageSource(15.0), 60, -200);
        var negative = Place(circuit, new DcVoltageSource(-15.0), 60, 200);
        var opamp = Place(circuit, new OpAmp741(), 60, 0);
        var generator = Place(circuit, new FunctionGenerator(Waveform.Square, 5e3, 1.0), -280, -40);

        // The break, in the one place it belongs: between the output's return path and the input
        // it feeds. Looking forward is an op-amp input at two megohms; looking back is the output
        // at seventy-five. That ratio is what makes the injection honest.
        var probe = Place(circuit, new LoopProbe(), 60, 160);

        var load = Place(circuit, new Capacitor(100e-9), 320, 60);
        var switched = Place(circuit, new ToggleSwitch { IsClosed = false }, 320, -60);

        var ground = Place(circuit, new Ground(), -280, 100);
        var ground2 = Place(circuit, new Ground(), 320, 180);
        var ground3 = Place(circuit, new Ground(), 160, -200);
        var ground4 = Place(circuit, new Ground(), 160, 200);

        circuit.Connect(generator.Return, ground.Pin);
        circuit.Connect(generator.Output, opamp.NonInverting);

        // Output back round to the inverting input, through the probe.
        circuit.Connect(opamp.Output, probe.From);
        circuit.Connect(probe.To, opamp.Inverting);

        // The load, behind a switch so the effect can be turned on and off while it runs.
        circuit.Connect(opamp.Output, switched.A);
        circuit.Connect(switched.B, load.A);
        circuit.Connect(load.B, ground2.Pin);

        circuit.Connect(positive.Positive, opamp.PositiveSupply);
        circuit.Connect(positive.Negative, ground3.Pin);
        circuit.Connect(negative.Positive, opamp.NegativeSupply);
        circuit.Connect(negative.Negative, ground4.Pin);

        vm.Scope.TimebasePerDivision = 20e-6;
        vm.Scope.VoltsPerDivision = 0.5;
        vm.Scope.AddProbe(opamp.NonInverting, "Input");
        vm.Scope.AddProbe(opamp.Output, "Output");
    }

    /// <summary>
    /// A class-B output pair, which is the classic distortion to look at.
    /// <para>
    /// Two transistors, one for each half of the waveform, and neither conducts until its
    /// base-emitter junction is forward biased. So there is a band around zero — about 1.2 V wide,
    /// two junction drops — where <i>neither</i> is on and the output does not move at all. That
    /// is the crossover notch, and it is why every real class-B stage is biased slightly on.
    /// </para>
    /// <para>
    /// Worth doing with this one: run it, open <b>Simulate &gt; Spectrum</b>, and read the THD. The
    /// harmonics are the <i>odd</i> ones, because the notch is symmetric — the same distortion on
    /// both halves. Then look at the waveform: the notch is visible, which makes this the rare
    /// case where the distortion can be seen as well as measured.
    /// </para>
    /// </summary>
    public static void LoadCrossoverDistortion(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Class-B crossover distortion";

        var positive = Place(circuit, new DcVoltageSource(12.0), -180, -220);
        var negative = Place(circuit, new DcVoltageSource(-12.0), -180, 220);

        // Well above the two junction drops, so the notch is a fraction of the swing rather than
        // the whole of it — a stage that never leaves the notch is not distorting, it is off.
        // With a real output resistance, because a class-B pair driven from an ideal source is a
        // short through whichever base-emitter junction is forward biased — and the solver is
        // right to refuse it.
        var generator = Place(circuit, new FunctionGenerator(Waveform.Sine, 1e3, 8.0)
        {
            OutputResistance = 50.0,
        }, -360, 0);

        var drive = Place(circuit, new Resistor(220), -180, 0);

        var npn = Place(circuit, new BipolarTransistor(BjtModel.Bc547), 40, -90);
        var pnp = Place(circuit, new BipolarTransistor(BjtModel.Bc557), 40, 90);

        var load = Place(circuit, new Resistor(1e3), 260, 60);

        var ground = Place(circuit, new Ground(), -360, 140);
        var ground2 = Place(circuit, new Ground(), -60, -220);
        var ground3 = Place(circuit, new Ground(), -60, 220);
        var ground4 = Place(circuit, new Ground(), 260, 200);

        load.RotationDegrees = 90;

        circuit.Connect(generator.Return, ground.Pin);
        circuit.Connect(generator.Output, drive.A);
        circuit.Connect(drive.B, npn.Base);
        circuit.Connect(npn.Base, pnp.Base);

        circuit.Connect(positive.Positive, npn.Collector);
        circuit.Connect(positive.Negative, ground2.Pin);
        circuit.Connect(negative.Positive, pnp.Collector);
        circuit.Connect(negative.Negative, ground3.Pin);

        circuit.Connect(npn.Emitter, pnp.Emitter);
        circuit.Connect(npn.Emitter, load.A);
        circuit.Connect(load.B, ground4.Pin);

        vm.Scope.TimebasePerDivision = 200e-6;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.AddProbe(drive.A, "Input");
        vm.Scope.AddProbe(load.A, "Output");
    }

    /// <summary>
    /// The same amplifier twice, once from a high-impedance feedback network and once from a low
    /// one — set up for <b>Simulate &gt; Noise</b>.
    /// <para>
    /// Both stages have a gain of ten and both give the same answer to every other analysis here.
    /// They do not make the same noise: the one built from megohms is far worse, because a
    /// resistor's noise goes as the square root of its value and because the amplifier's own
    /// current noise has somewhere to develop across. Measure each in turn and read the ranking.
    /// </para>
    /// </summary>
    public static void LoadLowNoisePreamp(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "The same gain, from big resistors and from small";

        var positive = Place(circuit, new DcVoltageSource(15.0), -320, -260);
        var negative = Place(circuit, new DcVoltageSource(-15.0), -320, 260);

        var ground = Place(circuit, new Ground(), -180, -260);
        var ground2 = Place(circuit, new Ground(), -180, 260);

        // Two identical stages, differing only in the size of the feedback network.
        var quiet = Stage(circuit, "Quiet", -60, -140, 1e3, 9e3, positive, negative);
        var noisy = Stage(circuit, "Noisy", -60, 140, 100e3, 900e3, positive, negative);

        circuit.Connect(positive.Negative, ground.Pin);
        circuit.Connect(negative.Negative, ground2.Pin);

        vm.Scope.TimebasePerDivision = 200e-6;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.AddProbe(quiet, "Quiet out");
        vm.Scope.AddProbe(noisy, "Noisy out");
    }

    /// <summary>One non-inverting stage of gain ten, and the terminal its output comes from.</summary>
    private static Terminal Stage(
        Circuit circuit, string name, double x, double y, double lower, double upper,
        DcVoltageSource positive, DcVoltageSource negative)
    {
        var amp = Place(circuit, new OpAmp741 { Name = $"U{name}" }, x, y);
        var gain = Place(circuit, new Resistor(lower), x - 60, y + 90);
        var feedback = Place(circuit, new Resistor(upper), x + 20, y - 90);
        var ground = Place(circuit, new Ground(), x - 60, y + 180);
        var input = Place(circuit, new Ground(), x - 200, y + 40);

        gain.RotationDegrees = 90;

        circuit.Connect(amp.PositiveSupply, positive.Positive);
        circuit.Connect(amp.NegativeSupply, negative.Positive);

        // Driven from ground: a noise measurement is about what the circuit makes, not about what
        // it is being given, so there is nothing at the input on purpose.
        circuit.Connect(amp.NonInverting, input.Pin);

        circuit.Connect(amp.Output, feedback.A);
        circuit.Connect(feedback.B, amp.Inverting);
        circuit.Connect(amp.Inverting, gain.A);
        circuit.Connect(gain.B, ground.Pin);

        return amp.Output;
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

        // The reference goes to whichever input makes the output go *low* out of range. An open
        // collector says "out of range" by pulling the shared node down and says "in range" only
        // by letting go of it, so each channel has to pull when the signal leaves the window on
        // its own side.

        // Channel 0 pulls when the signal rises above the upper limit.
        circuit.Connect(plus0, top.B);
        circuit.Connect(signal.Output, minus0);

        // Channel 1 pulls when it falls below the lower limit.
        circuit.Connect(signal.Output, plus1);
        circuit.Connect(minus1, middle.B);

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

        // Half a millisecond a division, which is a five millisecond window — enough for the
        // reset pulse and the first several bytes, and fine enough to sample them.
        //
        // The sampling is the reason for the number. 1-Wire says its bits with pulse widths, and a
        // write-one slot is six microseconds wide; the scope takes four thousand samples across
        // its window, so a slower timebase steps straight over those pulses. The trace still looks
        // plausible and **Simulate > Decode Bus** cannot read it — which it says, rather than
        // producing bytes that are quietly wrong.
        vm.Scope.TimebasePerDivision = 500e-6;
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

    /// <summary>
    /// A 4046 closed round an RC loop filter. Watch the control voltage climb as the loop hunts
    /// for the signal and then go flat once it has it — that flat line is lock, and everything a
    /// PLL is for happens after it.
    /// </summary>
    public static void LoadPhaseLockedLoop(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "4046 phase-locked loop";

        var supply = Place(circuit, new DcVoltageSource(5.0), -560, 220);

        var signal = Place(circuit, new FunctionGenerator(Waveform.Square, 30e3, 5.0)
        {
            DcOffset = 2.5, EdgeTime = 20e-9,
        }, -560, -120);

        var pll = Place(circuit, new Ic4046
        {
            TimingCapacitance = 1e-9,
            SeriesResistance = 10e3,
            UsesComparatorTwo = true,
        }, -180, 0);

        // The loop filter, which is the part that is yours to choose and the part that decides
        // whether the thing works.
        var filter = Place(circuit, new Resistor(10e3), 180, -140);
        var hold = Place(circuit, new Capacitor(100e-9), 340, 40);

        // Somewhere for the control pin to sit while the comparator is in its third state. Very
        // large, or it would discharge the filter between corrections and drag the loop down.
        var bias = Place(circuit, new Resistor(100e6), 500, 40);

        var gnd = Place(circuit, new Ground(), -560, 360);
        var gnd2 = Place(circuit, new Ground(), -180, 220);
        var gnd3 = Place(circuit, new Ground(), 340, 220);
        var gnd4 = Place(circuit, new Ground(), 500, 220);

        hold.RotationDegrees = 90;
        bias.RotationDegrees = 90;

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(signal.Return, gnd.Pin);
        circuit.Connect(pll.Vcc, supply.Positive);
        circuit.Connect(pll.Gnd, gnd2.Pin);
        circuit.Connect(pll.Inhibit, gnd2.Pin);

        circuit.Connect(signal.Output, pll.SignalInput);

        // The VCO fed back to the comparator, which is what closes the loop.
        circuit.Connect(pll.VcoOut, pll.ComparatorInput);

        circuit.Connect(pll.ComparatorII, filter.A);
        circuit.Connect(filter.B, pll.ControlVoltage);
        circuit.Connect(pll.ControlVoltage, hold.A);
        circuit.Connect(hold.B, gnd3.Pin);
        circuit.Connect(pll.ControlVoltage, bias.A);
        circuit.Connect(bias.B, gnd4.Pin);

        vm.Scope.TimebasePerDivision = 2e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(pll.ControlVoltage, "Control");
        vm.Scope.AddProbe(pll.VcoOut, "VCO");
        vm.Scope.AddProbe(pll.SignalInput, "Signal");
    }

    /// <summary>
    /// A carrier multiplied by a tone. The output's envelope is the tone, which is not something
    /// done to the carrier — it is what multiplying them means.
    /// </summary>
    public static void LoadAmplitudeModulation(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Amplitude modulation";

        var pos = Place(circuit, new DcVoltageSource(15.0), -540, 240);
        var neg = Place(circuit, new DcVoltageSource(15.0), -540, 400);

        var carrier = Place(circuit, new FunctionGenerator(Waveform.Sine, 100e3, 20.0), -540, -160);

        // Offset so the modulation never crosses zero: through zero the carrier would invert,
        // which is a different thing again and not what a broadcast transmitter does.
        var tone = Place(circuit, new FunctionGenerator(Waveform.Sine, 2e3, 10.0) { DcOffset = 6.0 }, -540, 40);

        var u = Place(circuit, new AnalogMultiplier(), -120, -40);
        var load = Place(circuit, new Resistor(100e3), 200, 60);

        var gnd = Place(circuit, new Ground(), -540, 540);
        var gnd2 = Place(circuit, new Ground(), -120, 220);
        var gnd3 = Place(circuit, new Ground(), 200, 240);

        load.RotationDegrees = 90;

        circuit.Connect(pos.Negative, gnd.Pin);
        circuit.Connect(neg.Positive, gnd.Pin);
        circuit.Connect(carrier.Return, gnd.Pin);
        circuit.Connect(tone.Return, gnd.Pin);

        circuit.Connect(u.PositiveSupply, pos.Positive);
        circuit.Connect(u.NegativeSupply, neg.Negative);
        circuit.Connect(u.X1, carrier.Output);
        circuit.Connect(u.X2, gnd2.Pin);
        circuit.Connect(u.Y1, tone.Output);
        circuit.Connect(u.Y2, gnd2.Pin);
        circuit.Connect(u.Z, gnd2.Pin);
        circuit.Connect(u.Output, load.A);
        circuit.Connect(load.B, gnd3.Pin);

        vm.Scope.TimebasePerDivision = 100e-6;
        vm.Scope.VoltsPerDivision = 5.0;
        vm.Scope.AddProbe(tone.Output, "Modulation");
        vm.Scope.AddProbe(u.Output, "Modulated");
    }

    /// <summary>
    /// An INA219 in the high side of a rail, with a switch to change the load while it watches.
    /// The point of high-side sensing is what is <i>not</i> in the circuit: nothing between the
    /// load and ground, so every other measurement still means what it says.
    /// </summary>
    public static void LoadCurrentSensing(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "High-side current sensing";

        var rail = Place(circuit, new DcVoltageSource(12.0), -560, 180);
        var logic = Place(circuit, new DcVoltageSource(3.3), -560, 380);

        var sensor = Place(circuit, new Ina219 { ShuntResistance = 0.1 }, -180, 0);

        var load = Place(circuit, new Resistor(100.0), 180, 100);
        var extra = Place(circuit, new Resistor(47.0), 360, 100);
        var switchIn = Place(circuit, new ToggleSwitch(), 360, -60);

        var master = Place(circuit, new I2cMaster
        {
            Transactions = "w 40 01; r 40 2",
            ClockFrequency = 400e3,
            StartDelay = 1e-3,
        }, 140, -320);

        var sdaPull = Place(circuit, new Resistor(4.7e3), -40, -460);
        var sclPull = Place(circuit, new Resistor(4.7e3), 60, -460);

        var gnd = Place(circuit, new Ground(), -560, 520);
        var gnd2 = Place(circuit, new Ground(), -180, 200);
        var gnd3 = Place(circuit, new Ground(), 180, 280);
        var gnd4 = Place(circuit, new Ground(), 360, 280);
        var gnd5 = Place(circuit, new Ground(), 140, -180);

        load.RotationDegrees = 90;
        extra.RotationDegrees = 90;
        switchIn.RotationDegrees = 90;
        sdaPull.RotationDegrees = 90;
        sclPull.RotationDegrees = 90;

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(logic.Negative, gnd.Pin);

        // It runs from the logic rail and measures a different one, which is the whole idea.
        circuit.Connect(sensor.Vcc, logic.Positive);
        circuit.Connect(sensor.Gnd, gnd2.Pin);

        circuit.Connect(rail.Positive, sensor.ShuntPlus);
        circuit.Connect(sensor.ShuntMinus, load.A);
        circuit.Connect(load.B, gnd3.Pin);

        // A second load on a switch, so the reading can be made to change while it runs.
        circuit.Connect(sensor.ShuntMinus, switchIn.A);
        circuit.Connect(switchIn.B, extra.A);
        circuit.Connect(extra.B, gnd4.Pin);

        circuit.Connect(master.Vcc, logic.Positive);
        circuit.Connect(master.Gnd, gnd5.Pin);
        circuit.Connect(master.Sda, sensor.Sda);
        circuit.Connect(master.Scl, sensor.Scl);
        circuit.Connect(sdaPull.A, logic.Positive);
        circuit.Connect(sdaPull.B, sensor.Sda);
        circuit.Connect(sclPull.A, logic.Positive);
        circuit.Connect(sclPull.B, sensor.Scl);

        vm.Scope.TimebasePerDivision = 500e-6;
        vm.Scope.VoltsPerDivision = 5.0;
        vm.Scope.AddProbe(sensor.ShuntMinus, "Load");
        vm.Scope.AddProbe(sensor.Sda, "SDA");
    }

    /// <summary>
    /// A whole linear bench supply end to end: transformer, bridge, reservoir, and a 7805 made
    /// <b>adjustable</b> by a potentiometer — with a probe on each of the three voltages so the
    /// stages can be watched turning into one another.
    /// <para>
    /// The trick with the regulator is worth understanding, because a 7805 is a <i>fixed</i> five
    /// volt part and this one is not. All a 78xx does is hold its output five volts above its own
    /// GND pin. Ground that pin and you get five volts. Lift it — by putting R1 from the output to
    /// it, and the potentiometer from there to ground — and the output rises by however far the
    /// pin has been lifted. Turning RV1 up raises the pin, and the output goes with it.
    /// </para>
    /// <para>
    /// It also has the honest fault of the arrangement built into it. The regulator's own
    /// quiescent current leaves through that same GND pin and flows through RV1, adding its own
    /// few volts to the answer — which is why this is a fine way to get an adjustable rail and a
    /// poor way to get an accurate one. An LM317 exists because its adjust pin takes microamps
    /// instead of milliamps.
    /// </para>
    /// </summary>
    public static void LoadAdjustableSupply(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Adjustable bridge-rectified supply";

        // A 1:1 transformer against 50 V peak-to-peak: about 18 V RMS on the secondary, which
        // rectifies to roughly 24 V and leaves headroom over the whole adjustment range.
        var mains = Place(circuit, new FunctionGenerator
        {
            Shape = Waveform.Sine,
            Frequency = 50,
            AmplitudePeakToPeak = 50,
            OutputResistance = 1.0,
        }, -620, 40);

        var transformer = Place(circuit, new Transformer(1.0, 1.0, 0.999), -420, 40);
        var bridge = Place(circuit, new BridgeRectifier(), -220, 40);

        // Large enough that the troughs between mains peaks still clear the regulator's dropout,
        // which is the whole job of a reservoir — and no larger, so there is a volt or so of
        // ripple left on the rail to watch the regulator throw away.
        var reservoir = Place(circuit, new ElectrolyticCapacitor(470e-6) { VoltageRating = 35 }, -40, 160);

        var regulator = Place(circuit, new VoltageRegulator(RegulatorModel.Lm7805), 160, 20);

        // R1 sets the current through the adjustment network. Smaller swamps the regulator's
        // quiescent current and gives a more honest answer; larger wastes less.
        var setter = Place(circuit, new Resistor(220), 340, 140);
        var adjust = Place(circuit, new Potentiometer(470, 0.5), 340, 320);

        var output = Place(circuit, new ElectrolyticCapacitor(10e-6) { VoltageRating = 35 }, 520, 160);
        var load = Place(circuit, new Resistor(470), 680, 160);

        var gnd = Place(circuit, new Ground(), -620, 220);
        var gnd2 = Place(circuit, new Ground(), -220, 220);
        var gnd3 = Place(circuit, new Ground(), -40, 320);
        var gnd4 = Place(circuit, new Ground(), 340, 460);
        var gnd5 = Place(circuit, new Ground(), 520, 320);
        var gnd6 = Place(circuit, new Ground(), 680, 320);

        reservoir.RotationDegrees = 90;
        setter.RotationDegrees = 90;
        output.RotationDegrees = 90;
        load.RotationDegrees = 90;

        circuit.Connect(mains.Return, gnd.Pin);
        circuit.Connect(mains.Output, transformer.P1);
        circuit.Connect(transformer.P2, gnd.Pin);

        circuit.Connect(transformer.S1, bridge.Ac1);
        circuit.Connect(transformer.S2, bridge.Ac2);
        circuit.Connect(bridge.Negative, gnd2.Pin);

        circuit.Connect(bridge.Positive, reservoir.A);
        circuit.Connect(reservoir.B, gnd3.Pin);
        circuit.Connect(bridge.Positive, regulator.Input);

        // The GND pin is not grounded, which is the whole of the trick.
        circuit.Connect(regulator.Output, setter.A);
        circuit.Connect(setter.B, regulator.Common);

        // The pot as a rheostat: more track between the GND pin and ground lifts it further, so
        // turning the wiper up turns the output up. Its far end goes to ground as well, so the
        // unused half of the track is not left floating.
        circuit.Connect(regulator.Common, adjust.A);
        circuit.Connect(adjust.Wiper, gnd4.Pin);
        circuit.Connect(adjust.B, gnd4.Pin);

        circuit.Connect(regulator.Output, output.A);
        circuit.Connect(output.B, gnd5.Pin);
        circuit.Connect(regulator.Output, load.A);
        circuit.Connect(load.B, gnd6.Pin);

        vm.Scope.TimebasePerDivision = 10e-3;
        vm.Scope.VoltsPerDivision = 5.0;
        vm.Scope.Layout = ScopeLayout.Unified;
        vm.Scope.AddProbe(transformer.S1, "AC in");
        vm.Scope.AddProbe(bridge.Positive, "Rectified");
        vm.Scope.AddProbe(regulator.Output, "Regulated");

        // Mains is 50 Hz, so at the default 1/1000 speed a single cycle would take twenty seconds
        // of wall time.
        vm.Simulation.SpeedFactor = 1.0;
    }

    /// <summary>
    /// Two transceivers at the ends of fifty metres of cable, with the far terminator on a switch.
    /// <para>
    /// Open SW1 and the line is unterminated: every edge arrives, bounces off the open end, comes
    /// home and bounces again, so the receiver sees a staircase instead of a transition. Close it
    /// and the 120 Ω absorbs the wave — which is what the terminator in every RS-485 installation
    /// is for, and why it goes at the <i>ends</i> of the run rather than at each device.
    /// </para>
    /// </summary>
    public static void LoadRs485Link(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "RS-485 over fifty metres";

        var supply = Place(circuit, new DcVoltageSource(5.0), -680, 260);

        // Fast enough that the cable's 250 ns of delay matters, which is the whole point.
        var data = Place(circuit, new ClockSource(1e6) { Levels = LogicLevels.Cmos5V }, -680, -120);

        var near = Place(circuit, new Rs485Transceiver(), -380, 0);

        // Fifty metres of twisted pair: about 250 ns each way, and 100 Ω rather than a coax's 50.
        var cable = Place(circuit, new TransmissionLine
        {
            CharacteristicImpedance = 100,
            Length = 50,
            VelocityFactor = 0.66,
        }, -60, 0);

        var far = Place(circuit, new Rs485Transceiver(), 280, 0);

        var terminator = Place(circuit, new Resistor(120), 560, 60);
        var switchIn = Place(circuit, new ToggleSwitch(closed: true), 560, -100);

        var gnd = Place(circuit, new Ground(), -680, 400);
        var gnd2 = Place(circuit, new Ground(), -380, 200);
        var gnd3 = Place(circuit, new Ground(), 280, 200);

        terminator.RotationDegrees = 90;
        switchIn.RotationDegrees = 90;

        circuit.Connect(supply.Negative, gnd.Pin);

        foreach (var end in new[] { near, far })
        {
            circuit.Connect(end.Vcc, supply.Positive);
            circuit.Connect(end.Gnd, end == near ? gnd2.Pin : gnd3.Pin);

            // Receivers always listening: RE is active low.
            circuit.Connect(end.ReceiverEnable, end == near ? gnd2.Pin : gnd3.Pin);
        }

        // The near end talks; the far end only listens.
        circuit.Connect(near.DriverEnable, supply.Positive);
        circuit.Connect(near.DriverIn, data.Out);
        circuit.Connect(far.DriverEnable, gnd3.Pin);
        circuit.Connect(far.DriverIn, gnd3.Pin);

        // The pair, as two conductors of one cable rather than two wires.
        circuit.Connect(near.A, cable.NearPlus);
        circuit.Connect(near.B, cable.NearMinus);
        circuit.Connect(cable.FarPlus, far.A);
        circuit.Connect(cable.FarMinus, far.B);

        // The terminator across the far end, on a switch so it can be taken away.
        circuit.Connect(far.A, switchIn.A);
        circuit.Connect(switchIn.B, terminator.A);
        circuit.Connect(terminator.B, far.B);

        vm.Scope.TimebasePerDivision = 200e-9;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(near.A, "Near A");
        vm.Scope.AddProbe(far.A, "Far A");
        vm.Scope.AddProbe(far.ReceiverOut, "Received");
    }

    /// <summary>
    /// An LC tank whose capacitor is a voltage. Move RV1 in the CONTROLS panel and the resonance
    /// moves with it — and opening <b>Simulate &gt; Frequency Response</b> shows the peak itself
    /// rather than what the peak does to one particular signal.
    /// </summary>
    public static void LoadVaractorTuning(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Varactor-tuned tank";

        var supply = Place(circuit, new DcVoltageSource(20.0), -620, 240);

        // Driven through a high resistance, so the source excites the tank without damping it.
        var source = Place(circuit, new FunctionGenerator(Waveform.Sine, 10e6, 0.2)
        {
            OutputResistance = 10e3,
        }, -620, -100);

        var inductor = Place(circuit, new Inductor(10e-6) { SeriesResistance = 0.5 }, -260, 100);

        // The blocking capacitor is the part that is easy to leave out: without it the inductor
        // is a short at DC, the cathode is grounded, and no bias ever reaches the junction.
        var block = Place(circuit, new Capacitor(10e-9), -60, -100);
        var varactor = Place(circuit, new Varactor
        {
            ZeroBiasCapacitance = 100e-12,
            JunctionPotential = 0.7,
            GradingCoefficient = 1.0,
        }, 160, -100);

        var feed = Place(circuit, new Resistor(100e3), 160, 100);
        var tune = Place(circuit, new Potentiometer(10e3, 0.5), 400, 240);

        var gnd = Place(circuit, new Ground(), -620, 380);
        var gnd2 = Place(circuit, new Ground(), -260, 260);
        var gnd3 = Place(circuit, new Ground(), 320, -100);
        var gnd4 = Place(circuit, new Ground(), 400, 380);

        inductor.RotationDegrees = 90;
        feed.RotationDegrees = 90;

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(source.Return, gnd.Pin);

        // The tank: inductor to ground, and the varactor to ground through the blocking capacitor.
        circuit.Connect(source.Output, inductor.A);
        circuit.Connect(inductor.B, gnd2.Pin);
        circuit.Connect(source.Output, block.A);
        circuit.Connect(block.B, varactor.Cathode);
        circuit.Connect(varactor.Anode, gnd3.Pin);

        // The tuning voltage onto the cathode, through a resistance large enough not to load it.
        circuit.Connect(tune.Wiper, feed.A);
        circuit.Connect(feed.B, varactor.Cathode);
        circuit.Connect(tune.A, gnd4.Pin);
        circuit.Connect(tune.B, supply.Positive);

        vm.Scope.TimebasePerDivision = 50e-9;
        vm.Scope.VoltsPerDivision = 0.2;
        vm.Scope.AddProbe(inductor.A, "Tank");
        vm.Scope.AddProbe(varactor.Cathode, "Bias");
    }

    /// <summary>
    /// An Arduino writing two lines to an HD44780, over the six wires everybody uses.
    /// <para>
    /// There is no processor here to run a library, so the whole exchange is a pattern played on
    /// six pins — which turns out to be the best way to see what a library like LiquidCrystal
    /// actually does, because none of it is hidden. The initialisation dance is there in full: the
    /// eight-bit function set that switches the controller to four bits, then four commands and
    /// the characters, every byte of them sent as two nibbles with a pulse of E to latch each one.
    /// </para>
    /// <para>
    /// The pins are the ones from the Arduino tutorial everybody has run —
    /// <c>LiquidCrystal lcd(12, 11, 5, 4, 3, 2)</c> — so the wiring here is the wiring on the
    /// breadboard in front of whoever is reading it.
    /// </para>
    /// <para>
    /// Watch for the thing about these modules that catches everybody: the second line is not the
    /// continuation of the first, it is a <b>separate address</b>. Getting to it is a command,
    /// 0x80 | 0x40, and without it the text simply runs off the end of line one into memory nobody
    /// can see.
    /// </para>
    /// </summary>
    public static void LoadCharacterLcd(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "HD44780 character LCD";

        var board = Place(circuit, new ArduinoUnoBoard(), -460, 0);
        var lcd = Place(circuit, new CharacterLcd(), 320, -80);

        // The contrast divider a real module needs. Not modelled electrically — the pin does
        // nothing here — but leaving it off a schematic is how people end up with a display that
        // is powered, working, and showing two rows of blank boxes.
        var contrast = Place(circuit, new Potentiometer(10e3, 0.3), 120, 380);

        // And the backlight, which is an LED behind the glass like any other.
        var backlight = Place(circuit, new Resistor(220), 640, 380);

        var gnd = Place(circuit, new Ground(), -460, 380);
        var gnd2 = Place(circuit, new Ground(), 120, 520);
        var gnd3 = Place(circuit, new Ground(), 380, 200);
        var gnd4 = Place(circuit, new Ground(), 800, 520);

        backlight.RotationDegrees = 90;

        board.Pins = LcdDriveScript();

        circuit.Connect(board.GroundPins[0], gnd.Pin);

        // Power for the module off the board's own 5 V pin.
        circuit.Connect(board.Pin("5V"), lcd.Vcc);
        circuit.Connect(lcd.Gnd, gnd3.Pin);

        // Write only: the busy flag is what RW is for, and nothing here polls it.
        circuit.Connect(lcd.ReadWrite, gnd3.Pin);

        circuit.Connect(board.Pin("5V"), contrast.B);
        circuit.Connect(contrast.A, gnd2.Pin);
        circuit.Connect(contrast.Wiper, lcd.Contrast);

        circuit.Connect(board.Pin("5V"), backlight.A);
        circuit.Connect(backlight.B, lcd.Backlight);
        circuit.Connect(lcd.BacklightCathode, gnd4.Pin);

        // LiquidCrystal lcd(12, 11, 5, 4, 3, 2): RS, E, then D4 to D7.
        circuit.Connect(board.Pin("D12"), lcd.RegisterSelect);
        circuit.Connect(board.Pin("D11"), lcd.Enable);
        circuit.Connect(board.Pin("D5"), lcd.Data[4]);
        circuit.Connect(board.Pin("D4"), lcd.Data[5]);
        circuit.Connect(board.Pin("D3"), lcd.Data[6]);
        circuit.Connect(board.Pin("D2"), lcd.Data[7]);

        vm.Scope.TimebasePerDivision = 5e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(board.Pin("D11"), "E");
        vm.Scope.AddProbe(board.Pin("D12"), "RS");
        vm.Scope.AddProbe(board.Pin("D5"), "D4");
    }

    /// <summary>
    /// Builds the six pin patterns that write the display.
    /// <para>
    /// Written out rather than hand-typed as six strings of a hundred and thirty characters,
    /// which would be unreadable and unmaintainable in equal measure. Each latch becomes two
    /// steps: the data is set up and E goes high, then E falls and the controller takes what is
    /// on the pins — because an HD44780 latches on the <b>falling</b> edge, not the level.
    /// </para>
    /// </summary>
    private static string LcdDriveScript()
    {
        // Each entry is one latch: whether RS is high, and the nibble on D4-D7.
        List<(bool Data, int Nibble)> latches = [];

        void Command(int value)
        {
            latches.Add((false, (value >> 4) & 0x0F));
            latches.Add((false, value & 0x0F));
        }

        void Text(string text)
        {
            foreach (var c in text)
            {
                latches.Add((true, (c >> 4) & 0x0F));
                latches.Add((true, c & 0x0F));
            }
        }

        // The one eight-bit latch: a function set asking for four-bit working. Only the high
        // nibble reaches the controller, because D0-D3 are not wired — which is exactly why this
        // works on real hardware too.
        latches.Add((false, 0x2));

        Command(0x28);      // function set: four bits, two lines, 5x8 characters
        Command(0x0C);      // display on, cursor off
        Command(0x06);      // entry mode: move right after each character
        Command(0x01);      // clear

        Text("CIRQ LCD DEMO");

        // Line two is a separate address, not a continuation. This is the command that gets there.
        Command(0x80 | 0x40);

        Text("HD44780 4-BIT");

        var rs = new System.Text.StringBuilder();
        var enable = new System.Text.StringBuilder();
        var data = new System.Text.StringBuilder[4];

        for (var i = 0; i < 4; i++) data[i] = new System.Text.StringBuilder();

        foreach (var (isData, nibble) in latches)
        {
            // Two steps per latch, with everything but E held across both.
            for (var step = 0; step < 2; step++)
            {
                rs.Append(isData ? '1' : '0');
                enable.Append(step == 0 ? '1' : '0');

                for (var bit = 0; bit < 4; bit++)
                    data[bit].Append(((nibble >> bit) & 1) != 0 ? '1' : '0');
            }
        }

        // "once" rather than "seq": a looping pattern would run the initialisation again, and the
        // second time round the eight-bit function set would be read as half of a four-bit byte
        // and put every nibble after it out of step.
        const string rate = "once@2kHz:";

        return string.Join("; ",
        [
            $"D12={rate}{rs}",
            $"D11={rate}{enable}",
            $"D5={rate}{data[0]}",
            $"D4={rate}{data[1]}",
            $"D3={rate}{data[2]}",
            $"D2={rate}{data[3]}",
        ]);
    }

    /// <summary>
    /// The same follower three times, on one five volt supply, with three different op-amps in it.
    /// How much of the supply each one can actually use is the single most common surprise in
    /// single-supply analog work, and it is far easier to believe once seen side by side.
    /// </summary>
    public static void LoadRailToRail(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "How close to the rails";

        var supply = Place(circuit, new DcVoltageSource(5.0), -620, 240);

        // A slow ramp over the whole supply, so each output is asked to go everywhere it can.
        var signal = Place(circuit, new FunctionGenerator(Waveform.Triangle, 100, 5.0) { DcOffset = 2.5 },
            -620, -80);

        var gnd = Place(circuit, new Ground(), -620, 400);
        var gnd2 = Place(circuit, new Ground(), -620, 80);

        var u1 = Place(circuit, new OperationalAmplifier(OpAmpModel.Lm741), -180, -320);
        var u2 = Place(circuit, new OperationalAmplifier(OpAmpModel.Lm358), -180, 0);
        var u3 = Place(circuit, new OperationalAmplifier(OpAmpModel.Mcp6002), -180, 320);

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(signal.Return, gnd2.Pin);

        var y = -320;

        foreach (var amplifier in new[] { u1, u2, u3 })
        {
            var load = Place(circuit, new Resistor(10e3), 160, y + 120);
            var loadGround = Place(circuit, new Ground(), 160, y + 240);

            load.RotationDegrees = 90;

            circuit.Connect(amplifier.PositiveSupply, supply.Positive);
            circuit.Connect(amplifier.NegativeSupply, gnd.Pin);

            // Unity gain: the output is wired straight back to the inverting input, so whatever
            // the output can reach is the whole of what the part can do.
            circuit.Connect(amplifier.NonInverting, signal.Output);
            circuit.Connect(amplifier.Inverting, amplifier.Output);

            circuit.Connect(amplifier.Output, load.A);
            circuit.Connect(load.B, loadGround.Pin);

            y += 320;
        }

        vm.Scope.TimebasePerDivision = 2e-3;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.AddProbe(signal.Output, "In");
        vm.Scope.AddProbe(u1.Output, "LM741");
        vm.Scope.AddProbe(u2.Output, "LM358");
        vm.Scope.AddProbe(u3.Output, "MCP6002");
    }

    /// <summary>
    /// A strain-gauge bridge into an instrumentation amplifier: millivolts of difference sitting on
    /// half a supply of common mode, which is the measurement ordinary amplifiers cannot make.
    /// </summary>
    public static void LoadLoadCell(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Load cell and INA126";

        var supply = Place(circuit, new DcVoltageSource(5.0), -520, 120);
        var cell = Place(circuit, new LoadCell { LoadKilograms = 2.5 }, -260, -40);
        var amplifier = Place(circuit, new Ina126 { DifferentialGain = 100 }, 120, -40);

        // The reference pin is what the output is measured from, and on a single supply it cannot
        // be left at ground or half the answer has nowhere to go.
        var refTop = Place(circuit, new Resistor(1e3), 60, 220);
        var refBottom = Place(circuit, new Resistor(1e3), 60, 340);

        var gnd = Place(circuit, new Ground(), -520, 280);
        var gnd2 = Place(circuit, new Ground(), -260, 200);
        var gnd3 = Place(circuit, new Ground(), 60, 440);

        refTop.RotationDegrees = 90;
        refBottom.RotationDegrees = 90;

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(cell.ExcitationPositive, supply.Positive);
        circuit.Connect(cell.ExcitationNegative, gnd2.Pin);

        circuit.Connect(amplifier.PositiveSupply, supply.Positive);
        circuit.Connect(amplifier.NegativeSupply, gnd.Pin);
        circuit.Connect(amplifier.InPlus, cell.SignalPositive);
        circuit.Connect(amplifier.InMinus, cell.SignalNegative);

        circuit.Connect(supply.Positive, refTop.A);
        circuit.Connect(refTop.B, refBottom.A);
        circuit.Connect(refBottom.B, gnd3.Pin);
        circuit.Connect(refTop.B, amplifier.Reference);

        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 0.5;
        vm.Scope.AddProbe(cell.SignalPositive, "Bridge +");
        vm.Scope.AddProbe(amplifier.Output, "Amplified");
    }

    /// <summary>
    /// An electret microphone into an LM386 into a speaker: the whole of a small audio chain, and
    /// the two capacitors in it are the part worth looking at.
    /// </summary>
    public static void LoadAudioAmplifier(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Microphone, LM386 and speaker";

        var supply = Place(circuit, new DcVoltageSource(9.0), -620, 160);
        var microphone = Place(circuit, new Microphone { ToneFrequency = 1e3 }, -400, -80);
        var bias = Place(circuit, new Resistor(2.2e3), -400, -240);

        // Blocks the microphone's bias so only the sound reaches the amplifier.
        var coupling = Place(circuit, new Capacitor(1e-6), -220, -120);

        // Volume, and not a decoration: an electret straight into a gain of two hundred is a
        // square wave at the rails, which is the first thing anybody building this discovers.
        var volume = Place(circuit, new Potentiometer(10e3, 0.08), -100, -40);

        var amplifier = Place(circuit, new Lm386 { VoltageGain = 200 }, 120, -120);

        // And this one blocks the amplifier's idle half-supply, which would otherwise sit across
        // the voice coil continuously.
        var output = Place(circuit, new Capacitor(220e-6), 360, -120);
        var speaker = Place(circuit, new Speaker(8.0), 520, -40);

        var gnd = Place(circuit, new Ground(), -620, 320);
        var gnd2 = Place(circuit, new Ground(), -400, 120);
        var gnd3 = Place(circuit, new Ground(), -100, 140);
        var gnd4 = Place(circuit, new Ground(), 120, 140);
        var gnd5 = Place(circuit, new Ground(), 520, 160);

        bias.RotationDegrees = 90;
        volume.RotationDegrees = 90;
        speaker.RotationDegrees = 90;

        circuit.Connect(supply.Negative, gnd.Pin);

        // Electret bias: a resistor from the rail, and the sound arrives as a wobble on the node.
        circuit.Connect(bias.A, supply.Positive);
        circuit.Connect(bias.B, microphone.A);
        circuit.Connect(microphone.B, gnd2.Pin);

        circuit.Connect(microphone.A, coupling.A);
        // Signal at B and ground at A, so the Wiper control reads as a volume knob: the tap ratio
        // is measured from A, and a knob that gets quieter as it goes up is a knob wired backwards.
        circuit.Connect(coupling.B, volume.B);
        circuit.Connect(volume.A, gnd3.Pin);

        circuit.Connect(amplifier.InPlus, volume.Wiper);
        circuit.Connect(amplifier.InMinus, gnd4.Pin);
        circuit.Connect(amplifier.PositiveSupply, supply.Positive);
        circuit.Connect(amplifier.NegativeSupply, gnd4.Pin);

        circuit.Connect(amplifier.Output, output.A);
        circuit.Connect(output.B, speaker.A);
        circuit.Connect(speaker.B, gnd5.Pin);

        vm.Scope.TimebasePerDivision = 500e-6;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.AddProbe(microphone.A, "Microphone");
        vm.Scope.AddProbe(amplifier.Output, "LM386 out");
        vm.Scope.AddProbe(speaker.A, "Speaker");
    }

    /// <summary>
    /// Logic on one side, a relay coil on the other, and three parts between them that each do
    /// something a logic pin cannot: an optocoupler, a Darlington array and the relay itself.
    /// </summary>
    public static void LoadRelayDriver(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Optocoupler, ULN2003 and relay";

        var logic = Place(circuit, new DcVoltageSource(5.0), -680, 200);
        var coilSupply = Place(circuit, new DcVoltageSource(12.0), 560, 220);
        var clock = Place(circuit, new ClockSource(3.0) { Levels = LogicLevels.Ttl }, -680, -160);

        var ledResistor = Place(circuit, new Resistor(330), -460, -160);
        var opto = Place(circuit, new Optocoupler(OptocouplerModel.Pc817), -260, -80);
        var emitterLoad = Place(circuit, new Resistor(10e3), -100, 60);

        var driver = Place(circuit, new Uln2003(), 120, -80);
        var relay = Place(circuit, new Relay(), 400, -80);
        var lamp = Place(circuit, new Resistor(120), 740, -60);
        var restLamp = Place(circuit, new Resistor(120), 880, 100);

        var gnd = Place(circuit, new Ground(), -680, 360);
        var gnd2 = Place(circuit, new Ground(), -260, 160);
        var gnd3 = Place(circuit, new Ground(), -100, 200);
        var gnd4 = Place(circuit, new Ground(), 120, 200);
        var gnd5 = Place(circuit, new Ground(), 560, 380);
        var gnd6 = Place(circuit, new Ground(), 740, 100);
        var gnd7 = Place(circuit, new Ground(), 880, 260);

        emitterLoad.RotationDegrees = 90;
        lamp.RotationDegrees = 90;
        restLamp.RotationDegrees = 90;

        circuit.Connect(logic.Negative, gnd.Pin);
        circuit.Connect(coilSupply.Negative, gnd5.Pin);

        // The LED wants milliamps, not the microamps a logic pin thinks it is giving away.
        circuit.Connect(clock.Out, ledResistor.A);
        circuit.Connect(ledResistor.B, opto.Anode);
        circuit.Connect(opto.Cathode, gnd2.Pin);

        // Emitter follower, so the output goes high when the LED lights rather than the other way.
        circuit.Connect(opto.Collector, logic.Positive);
        circuit.Connect(opto.Emitter, emitterLoad.A);
        circuit.Connect(emitterLoad.B, gnd3.Pin);

        // No supply pin to wire: a ULN2003 is powered by whatever it is sinking from, and the
        // part says so by aliasing Vcc onto its ground pin. Connect it to a rail and the ground
        // pin is shorted to that rail.
        circuit.Connect(driver.Gnd, gnd4.Pin);
        circuit.Connect(driver.Inputs[0], opto.Emitter);

        // The other six channels are unused, and a Darlington input left floating is a Darlington
        // that switches on whatever the board picks up. Tie them down.
        for (var i = 1; i < driver.Inputs.Count; i++) circuit.Connect(driver.Inputs[i], gnd4.Pin);

        // Common goes to the coil's supply, which is where the array's own flyback diodes send
        // the inductive kick when the channel lets go.
        circuit.Connect(driver.Common, coilSupply.Positive);
        circuit.Connect(relay.CoilA, coilSupply.Positive);
        circuit.Connect(relay.CoilB, driver.Outputs[0]);

        // Both contacts are used, which is what a changeover relay is for: the lamp the coil turns
        // on, and the one it turns off.
        circuit.Connect(relay.Common, coilSupply.Positive);
        circuit.Connect(relay.NormallyOpen, lamp.A);
        circuit.Connect(lamp.B, gnd6.Pin);
        circuit.Connect(relay.NormallyClosed, restLamp.A);
        circuit.Connect(restLamp.B, gnd7.Pin);

        vm.Scope.TimebasePerDivision = 50e-3;
        vm.Scope.VoltsPerDivision = 5.0;
        vm.Scope.AddProbe(opto.Emitter, "Opto out");
        vm.Scope.AddProbe(relay.CoilB, "Coil");
        vm.Scope.AddProbe(lamp.A, "Lamp");
    }

    /// <summary>
    /// A solar panel and an adjustable load, which is the one circuit here where the thing under
    /// test is the load. Too light and too heavy both waste most of the panel.
    /// </summary>
    public static void LoadSolarPanel(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Finding a panel's maximum power point";

        var panel = Place(circuit, new SolarCell { CellCount = 6, ShortCircuitCurrent = 150e-3 },
            -320, 0);
        var load = Place(circuit, new Potentiometer(100.0, 0.5), 0, -40);
        var shunt = Place(circuit, new Resistor(1.0), 220, 100);

        var gnd = Place(circuit, new Ground(), -320, 220);
        var gnd2 = Place(circuit, new Ground(), 220, 240);

        shunt.RotationDegrees = 90;

        circuit.Connect(panel.B, gnd.Pin);
        circuit.Connect(panel.A, load.A);

        // Wiper tied to one end makes it a rheostat: one variable resistance rather than a divider.
        circuit.Connect(load.Wiper, load.B);
        circuit.Connect(load.B, shunt.A);
        circuit.Connect(shunt.B, gnd2.Pin);

        vm.Scope.TimebasePerDivision = 10e-3;
        vm.Scope.VoltsPerDivision = 0.5;
        vm.Scope.AddProbe(panel.A, "Panel");
        vm.Scope.AddProbe(shunt.A, "Shunt (1 ohm = 1 V per amp)");
    }

    /// <summary>
    /// A lithium charge cycle end to end: constant current until the cell reaches its float
    /// voltage, then constant voltage with the current tapering away to termination.
    /// </summary>
    public static void LoadLithiumCharge(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Lithium charge cycle";

        var supply = Place(circuit, new DcVoltageSource(5.0), -460, 80);
        var charger = Place(circuit, new LithiumCharger { ChargeCurrent = 0.5 }, -180, -40);

        // A capacitor standing in for the cell, and a large one: a real 18650 moves so little over
        // a charge that neither phase would be visible in a window anybody wants to watch.
        var cell = Place(circuit, new Capacitor(0.1), 320, 60);

        // The cell's own internal resistance, and it is what makes the second phase visible: with
        // nothing between the charger and an ideal capacitor there is no taper to watch, because
        // a capacitor at the float voltage stops taking current the instant it gets there.
        var internalResistance = Place(circuit, new Resistor(1.0), 140, -40);

        var chargingResistor = Place(circuit, new Resistor(1e3), 60, -260);
        var standbyResistor = Place(circuit, new Resistor(1e3), 240, -260);
        var chargingLed = Place(circuit, Led.OfColour("Red"), 60, -140);
        var standbyLed = Place(circuit, Led.OfColour("Green"), 240, -140);

        var gnd = Place(circuit, new Ground(), -460, 240);
        var gnd2 = Place(circuit, new Ground(), -180, 200);
        var gnd3 = Place(circuit, new Ground(), 320, 220);

        cell.RotationDegrees = 90;
        chargingResistor.RotationDegrees = 90;
        standbyResistor.RotationDegrees = 90;
        chargingLed.RotationDegrees = 90;
        standbyLed.RotationDegrees = 90;

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(charger.Input, supply.Positive);
        circuit.Connect(charger.Gnd, gnd2.Pin);

        circuit.Connect(charger.Battery, internalResistance.A);
        circuit.Connect(internalResistance.B, cell.A);
        circuit.Connect(cell.B, gnd3.Pin);

        // The status pins pull down, so each LED hangs off the rail rather than off the pin.
        circuit.Connect(chargingResistor.A, supply.Positive);
        circuit.Connect(chargingResistor.B, chargingLed.Anode);
        circuit.Connect(chargingLed.Cathode, charger.Charging);

        circuit.Connect(standbyResistor.A, supply.Positive);
        circuit.Connect(standbyResistor.B, standbyLed.Anode);
        circuit.Connect(standbyLed.Cathode, charger.Standby);

        vm.Scope.TimebasePerDivision = 100e-3;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.AddProbe(charger.Battery, "Charger out");
        vm.Scope.AddProbe(cell.A, "Cell");
        vm.Scope.AddProbe(charger.Charging, "CHRG");
    }

    /// <summary>
    /// Two-stage surge protection, which is how it is actually done: a varistor takes the energy
    /// at the input, a resistor limits what gets past it, and a TVS clamps the remainder down to
    /// something the electronics behind it can survive.
    /// </summary>
    public static void LoadSurgeProtection(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Two-stage surge protection";

        var supply = Place(circuit, new DcVoltageSource(24.0), -680, 140);

        // The surge itself: a short, hard pulse in series with the rail, the way a nearby
        // switching load puts one onto a supply that was perfectly quiet a moment ago.
        var surge = Place(circuit, new FunctionGenerator(Waveform.Square, 200, 160.0)
        {
            DutyCycle = 0.005,
            DcOffset = 80.0,
            OutputResistance = 20.0,
        }, -680, -140);

        var fuse = Place(circuit, new Fuse(1.0), -420, -260);
        var mov = Place(circuit, new Varistor(60.0) { EnergyRating = 70.0 }, -200, -140);
        var series = Place(circuit, new Resistor(10.0), 0, -260);
        var tvs = Place(circuit, new TransientSuppressor(24.0), 220, -140);
        var load = Place(circuit, new Resistor(240.0), 440, -140);

        var gnd = Place(circuit, new Ground(), -680, 300);
        var gnd2 = Place(circuit, new Ground(), -200, 40);
        var gnd3 = Place(circuit, new Ground(), 220, 40);
        var gnd4 = Place(circuit, new Ground(), 440, 40);

        mov.RotationDegrees = 90;
        tvs.RotationDegrees = 90;
        load.RotationDegrees = 90;

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, surge.Return);

        circuit.Connect(surge.Output, fuse.A);
        circuit.Connect(fuse.B, mov.A);
        circuit.Connect(mov.B, gnd2.Pin);

        circuit.Connect(mov.A, series.A);
        circuit.Connect(series.B, tvs.A);
        circuit.Connect(tvs.B, gnd3.Pin);

        circuit.Connect(tvs.A, load.A);
        circuit.Connect(load.B, gnd4.Pin);

        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 20.0;
        vm.Scope.AddProbe(mov.A, "Input");
        vm.Scope.AddProbe(load.A, "Protected");
    }

    /// <summary>
    /// A quadrature encoder, where the direction is not in either output but in the relationship
    /// between them. Turn the Detent control and watch which one moves first.
    /// </summary>
    public static void LoadRotaryEncoder(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Quadrature from a rotary encoder";

        var supply = Place(circuit, new DcVoltageSource(5.0), -400, 100);
        var encoder = Place(circuit, new RotaryEncoder(), -120, -40);

        // Both contacts are switches to the common pin, so both need pulling up to read anything.
        var pullA = Place(circuit, new Resistor(10e3), 140, -220);
        var pullB = Place(circuit, new Resistor(10e3), 300, -220);

        var gnd = Place(circuit, new Ground(), -400, 260);
        var gnd2 = Place(circuit, new Ground(), -120, 180);

        pullA.RotationDegrees = 90;
        pullB.RotationDegrees = 90;

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(encoder.Common, gnd2.Pin);

        circuit.Connect(pullA.A, supply.Positive);
        circuit.Connect(pullA.B, encoder.OutputA);
        circuit.Connect(pullB.A, supply.Positive);
        circuit.Connect(pullB.B, encoder.OutputB);

        vm.Scope.TimebasePerDivision = 10e-3;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(encoder.OutputA, "A");
        vm.Scope.AddProbe(encoder.OutputB, "B");
    }

    /// <summary>
    /// The same sequence of latches the parallel LCD example plays, with each one turned into a
    /// byte written to the port expander.
    /// <para>
    /// Every backpack sold has the same wiring — P0 is RS, P2 is E, P3 is the backlight and the
    /// top nibble is D4 to D7 — so this is the byte pattern a library produces on real hardware
    /// too. Note that it is <b>two writes per latch</b>, one with E high and one with E low,
    /// exactly as the six-wire version is two steps per latch. The bus is a different shape; the
    /// controller behind it is not.
    /// </para>
    /// </summary>
    private static string BackpackScript()
    {
        List<(bool Data, int Nibble)> latches = [];

        void Command(int value)
        {
            latches.Add((false, (value >> 4) & 0x0F));
            latches.Add((false, value & 0x0F));
        }

        void Text(string text)
        {
            foreach (var c in text)
            {
                latches.Add((true, (c >> 4) & 0x0F));
                latches.Add((true, c & 0x0F));
            }
        }

        latches.Add((false, 0x2));      // the eight-bit function set that asks for four-bit working

        Command(0x28);
        Command(0x0C);
        Command(0x06);
        Command(0x01);

        Text("I2C BACKPACK");

        Command(0x80 | 0x40);

        Text("TWO WIRES");

        List<string> bytes = [];

        foreach (var (isData, nibble) in latches)
        {
            foreach (var enable in new[] { true, false })
            {
                // Backlight on throughout, RW low throughout: the expander drives the whole port
                // every time, so every bit has to be right in every byte.
                var value = (nibble << 4) | 0x08 | (enable ? 0x04 : 0x00) | (isData ? 0x01 : 0x00);
                bytes.Add(value.ToString("X2"));
            }
        }

        return "w 27 " + string.Join(" ", bytes);
    }

    /// <summary>
    /// A negative rail made out of a positive one, and then something that needs it: an amplifier
    /// asked to follow a signal that goes below ground, which on one supply it cannot.
    /// </summary>
    public static void LoadNegativeRail(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Making a negative rail";

        var supply = Place(circuit, new DcVoltageSource(5.0), -620, 160);
        var pump = Place(circuit, new ChargePump(), -320, -40);

        // The flying capacitor, which is the whole mechanism: charged across the supply, then
        // turned over and emptied into the reservoir the other way up.
        var flying = Place(circuit, new Capacitor(10e-6), -140, -220);
        var reservoir = Place(circuit, new Capacitor(10e-6), -80, 120);

        var signal = Place(circuit, new FunctionGenerator(Waveform.Sine, 500, 4.0), -620, -240);
        var amplifier = Place(circuit, new OperationalAmplifier(OpAmpModel.Tl081), 180, -60);
        var load = Place(circuit, new Resistor(10e3), 420, 60);

        var gnd = Place(circuit, new Ground(), -620, 320);
        var gnd2 = Place(circuit, new Ground(), -620, -100);
        var gnd3 = Place(circuit, new Ground(), -320, 140);
        var gnd4 = Place(circuit, new Ground(), -80, 260);
        var gnd5 = Place(circuit, new Ground(), 420, 200);

        flying.RotationDegrees = 90;
        reservoir.RotationDegrees = 90;
        load.RotationDegrees = 90;

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(signal.Return, gnd2.Pin);

        circuit.Connect(pump.Supply, supply.Positive);
        circuit.Connect(pump.Ground, gnd3.Pin);
        circuit.Connect(pump.CapacitorPositive, flying.A);
        circuit.Connect(flying.B, pump.CapacitorNegative);
        circuit.Connect(pump.Output, reservoir.A);
        circuit.Connect(reservoir.B, gnd4.Pin);

        // The amplifier straddles both rails, so its output has somewhere to go in both directions.
        circuit.Connect(amplifier.PositiveSupply, supply.Positive);
        circuit.Connect(amplifier.NegativeSupply, pump.Output);
        circuit.Connect(amplifier.NonInverting, signal.Output);
        circuit.Connect(amplifier.Inverting, amplifier.Output);
        circuit.Connect(amplifier.Output, load.A);
        circuit.Connect(load.B, gnd5.Pin);

        vm.Scope.TimebasePerDivision = 500e-6;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.AddProbe(pump.Output, "Negative rail");
        vm.Scope.AddProbe(signal.Output, "In");
        vm.Scope.AddProbe(amplifier.Output, "Out");
    }

    /// <summary>
    /// A 3.3 V part and a 5 V part on the same two wires, which is the commonest wiring question
    /// there is and has a part made for it.
    /// </summary>
    public static void LoadLevelShifting(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "An I2C bus across a 3.3 V to 5 V boundary";

        var lowRail = Place(circuit, new DcVoltageSource(3.3), -680, 220);
        var highRail = Place(circuit, new DcVoltageSource(5.0), -680, 420);

        // A 3.3 V controller talking to a 5 V memory, which is the situation the part exists for.
        // Write four bytes, then set the address back with a write of nothing but the address,
        // then read them. Without that second write the read starts wherever the first one left
        // the pointer, which is a byte past the data and reads as zeros.
        var master = Place(circuit, new I2cMaster
        {
            Transactions = "w 50 00 00 43 49 52 51; w 50 00 00; r 50 4",
        }, -420, -120);
        var shifter = Place(circuit, new LevelShifter(), 40, -60);
        var memory = Place(circuit, new I2cEeprom(), 460, -100);

        var gnd = Place(circuit, new Ground(), -680, 580);
        var gnd2 = Place(circuit, new Ground(), -420, 140);
        var gnd3 = Place(circuit, new Ground(), 40, 200);
        var gnd4 = Place(circuit, new Ground(), 460, 160);

        circuit.Connect(lowRail.Negative, gnd.Pin);
        circuit.Connect(highRail.Negative, gnd.Pin);

        circuit.Connect(master.Vcc, lowRail.Positive);
        circuit.Connect(master.Gnd, gnd2.Pin);
        circuit.Connect(memory.Vcc, highRail.Positive);
        circuit.Connect(memory.Gnd, gnd4.Pin);

        // The shifter's own gates run from the two rails, and its pull-ups are what make every
        // high level on both sides. Nothing here drives a line up; everything only pulls down.
        circuit.Connect(shifter.LowReference, lowRail.Positive);
        circuit.Connect(shifter.HighReference, highRail.Positive);
        circuit.Connect(shifter.Gnd, gnd3.Pin);

        circuit.Connect(shifter.Low(0), master.Sda);
        circuit.Connect(shifter.High(0), memory.Sda);
        circuit.Connect(shifter.Low(1), master.Scl);
        circuit.Connect(shifter.High(1), memory.Scl);

        vm.Scope.TimebasePerDivision = 50e-6;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(master.Sda, "SDA at 3V3");
        vm.Scope.AddProbe(memory.Sda, "SDA at 5V");
        vm.Scope.AddProbe(master.Scl, "SCL at 3V3");
    }

    /// <summary>
    /// What a crystal is for, put next to the thing it replaces. A tuned circuit and a crystal at
    /// the same frequency, swept side by side — the difference is not subtle.
    /// </summary>
    public static void LoadCrystalQ(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "A crystal against a tuned circuit";

        var drive = Place(circuit, new FunctionGenerator(Waveform.Sine, 1e6, 2.0) { AcMagnitude = 1.0 },
            -560, 0);

        // Each resonator in series with the signal and a load after it, so what gets through is
        // what the resonator let past: a peak at resonance rather than the notch you get probing
        // across one.
        var crystal = Place(circuit, new Crystal(CrystalModel.Mhz1), -300, -160);
        var crystalLoad = Place(circuit, new Resistor(1e3), -60, -80);

        // An LC series-resonant at the same megahertz: 25 uH with about a nanofarad, and five
        // ohms of winding loss, which is what a real coil of that size costs.
        var coil = Place(circuit, new Inductor(25e-6) { SeriesResistance = 5.0 }, -300, 160);
        var tune = Place(circuit, new Capacitor(1013e-12), -120, 160);
        var tankLoad = Place(circuit, new Resistor(1e3), 60, 240);

        var gnd = Place(circuit, new Ground(), -560, 200);
        var gnd2 = Place(circuit, new Ground(), -60, 100);
        var gnd3 = Place(circuit, new Ground(), 60, 400);

        crystalLoad.RotationDegrees = 90;
        tankLoad.RotationDegrees = 90;

        circuit.Connect(drive.Return, gnd.Pin);

        circuit.Connect(drive.Output, crystal.A);
        circuit.Connect(crystal.B, crystalLoad.A);
        circuit.Connect(crystalLoad.B, gnd2.Pin);

        circuit.Connect(drive.Output, coil.A);
        circuit.Connect(coil.B, tune.A);
        circuit.Connect(tune.B, tankLoad.A);
        circuit.Connect(tankLoad.B, gnd3.Pin);

        // And the part you actually buy when you want a frequency: the same quartz with the
        // oscillator already round it, in a can with four pins.
        var moduleRail = Place(circuit, new DcVoltageSource(5.0), 560, 300);
        var module = Place(circuit, new OscillatorModule(1e6), 560, 0);
        var divider = Place(circuit, new Ic4040(), 800, 0);
        var moduleGround = Place(circuit, new Ground(), 560, 460);
        var dividerGround = Place(circuit, new Ground(), 800, 240);

        circuit.Connect(moduleRail.Negative, moduleGround.Pin);
        circuit.Connect(module.Vcc, moduleRail.Positive);
        circuit.Connect(module.Gnd, moduleGround.Pin);
        circuit.Connect(divider.Vcc, moduleRail.Positive);
        circuit.Connect(divider.Gnd, dividerGround.Pin);
        circuit.Connect(divider.Reset, dividerGround.Pin);
        circuit.Connect(module.Output, divider.Clock);

        vm.Scope.TimebasePerDivision = 2e-6;
        vm.Scope.VoltsPerDivision = 1.0;
        // Only the two analog nodes are probed. The divider is there to be looked at on the
        // canvas, not swept: a logic node has no small-signal response and would sit at the floor
        // of the Bode plot being distracting.
        vm.Scope.AddProbe(crystalLoad.A, "Through crystal");
        vm.Scope.AddProbe(tankLoad.A, "Through tank");
    }

    /// <summary>
    /// A choke that passes a signal and blocks the noise riding on both of its wires at once,
    /// which is the one thing a pair of separate inductors cannot do.
    /// </summary>
    public static void LoadCommonModeFilter(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "A choke that only sees common mode";

        // The wanted signal, driven between the two conductors.
        var signal = Place(circuit, new FunctionGenerator(Waveform.Sine, 100e3, 2.0), -640, -120);

        // And the unwanted one, lifting the whole pair together against ground — which is what
        // every nearby switching supply does to every cable near it.
        var noise = Place(circuit, new FunctionGenerator(Waveform.Sine, 5e6, 4.0), -640, 180);

        var choke = Place(circuit, new CommonModeChoke(1e-3, 0.995), -220, 0);

        // Terminated across the pair with the midpoint grounded, so common-mode current has a
        // path home and the choke has something to work against.
        var upper = Place(circuit, new Resistor(50), 200, -100);
        var lower = Place(circuit, new Resistor(50), 200, 100);

        var gnd = Place(circuit, new Ground(), -640, 340);
        var gnd2 = Place(circuit, new Ground(), 200, 280);

        upper.RotationDegrees = 90;
        lower.RotationDegrees = 90;

        circuit.Connect(noise.Return, gnd.Pin);
        circuit.Connect(signal.Return, noise.Output);

        circuit.Connect(signal.Output, choke.A1);
        circuit.Connect(noise.Output, choke.A2);

        circuit.Connect(choke.B1, upper.A);
        circuit.Connect(upper.B, lower.A);
        circuit.Connect(lower.B, choke.B2);
        circuit.Connect(upper.B, gnd2.Pin);

        vm.Scope.TimebasePerDivision = 2e-6;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.AddProbe(choke.A1, "Line in");
        vm.Scope.AddProbe(choke.B1, "Line out");
        vm.Scope.AddProbe(choke.B2, "Return out");
    }

    /// <summary>
    /// The same HD44780, driven over two wires instead of six. Everything the parallel example
    /// does is still done — it is just wrapped in a byte to a port expander each time.
    /// </summary>
    public static void LoadI2cLcd(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "An LCD on an I2C backpack";

        var supply = Place(circuit, new DcVoltageSource(5.0), -620, 160);
        var master = Place(circuit, new I2cMaster { Transactions = BackpackScript() }, -400, -80);
        var expander = Place(circuit, new Pcf8574 { Address = 0x27 }, -40, -40);
        var lcd = Place(circuit, new CharacterLcd(), 420, -100);
        var contrast = Place(circuit, new Potentiometer(10e3, 0.3), 200, 360);

        var sdaPull = Place(circuit, new Resistor(4.7e3), -240, -320);
        var sclPull = Place(circuit, new Resistor(4.7e3), -120, -320);

        var gnd = Place(circuit, new Ground(), -620, 320);
        var gnd2 = Place(circuit, new Ground(), -40, 180);
        var gnd3 = Place(circuit, new Ground(), 200, 500);
        var gnd4 = Place(circuit, new Ground(), 420, 200);

        contrast.RotationDegrees = 90;
        sdaPull.RotationDegrees = 90;
        sclPull.RotationDegrees = 90;

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(master.Vcc, supply.Positive);
        circuit.Connect(master.Gnd, gnd.Pin);
        circuit.Connect(expander.Vcc, supply.Positive);
        circuit.Connect(expander.Gnd, gnd2.Pin);

        circuit.Connect(master.Sda, expander.Sda);
        circuit.Connect(master.Scl, expander.Scl);
        circuit.Connect(sdaPull.A, supply.Positive);
        circuit.Connect(sdaPull.B, master.Sda);
        circuit.Connect(sclPull.A, supply.Positive);
        circuit.Connect(sclPull.B, master.Scl);

        // The backpack's wiring, which is the same on every one of them you can buy:
        // P0=RS, P1=RW, P2=E, P3=backlight, P4-P7=D4-D7.
        circuit.Connect(expander.Pins[0], lcd.RegisterSelect);
        circuit.Connect(expander.Pins[1], lcd.ReadWrite);
        circuit.Connect(expander.Pins[2], lcd.Enable);

        for (var i = 0; i < 4; i++) circuit.Connect(expander.Pins[4 + i], lcd.Data[4 + i]);

        // A PCF8574 releases a pin rather than driving it high, so every line it feeds needs
        // something to pull it up. On a real backpack that is the chip's own weak pull-up; here
        // it is explicit, because a released pin with nothing on it is a floating input.
        var pulled = new[] { lcd.RegisterSelect, lcd.Enable, lcd.Data[4], lcd.Data[5], lcd.Data[6], lcd.Data[7] };

        for (var i = 0; i < pulled.Length; i++)
        {
            var pull = Place(circuit, new Resistor(10e3), 180 + (i * 40), -420);
            pull.RotationDegrees = 90;

            circuit.Connect(pull.A, supply.Positive);
            circuit.Connect(pull.B, pulled[i]);
        }

        circuit.Connect(lcd.Vcc, supply.Positive);
        circuit.Connect(lcd.Gnd, gnd4.Pin);
        circuit.Connect(lcd.Backlight, supply.Positive);
        circuit.Connect(lcd.BacklightCathode, gnd4.Pin);

        circuit.Connect(contrast.A, supply.Positive);
        circuit.Connect(contrast.B, gnd3.Pin);
        circuit.Connect(contrast.Wiper, lcd.Contrast);

        vm.Scope.TimebasePerDivision = 500e-6;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.AddProbe(master.Scl, "SCL");
        vm.Scope.AddProbe(master.Sda, "SDA");
        vm.Scope.AddProbe(lcd.Enable, "E");
    }

    /// <summary>
    /// A thermistor, a comparator and the feedback resistor between them, which is the difference
    /// between a thermostat and a relay that chatters.
    /// </summary>
    public static void LoadThermostat(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Thermostat with hysteresis";

        var supply = Place(circuit, new DcVoltageSource(5.0), -600, 180);

        var sensor = Place(circuit, new Thermistor { Temperature = 20.0 }, -360, -140);
        var lower = Place(circuit, new Resistor(10e3), -360, 40);

        var referenceTop = Place(circuit, new Resistor(10e3), -160, -140);
        var referenceBottom = Place(circuit, new Resistor(10e3), -160, 40);

        var comparator = Place(circuit, new Comparator(ComparatorModel.Lm311), 100, -60);
        var feedback = Place(circuit, new Resistor(470e3), 100, -280);
        var pullUp = Place(circuit, new Resistor(4.7e3), 320, -200);

        var limiter = Place(circuit, new Resistor(330), 520, -60);
        var lamp = Place(circuit, Led.OfColour("Red"), 660, -60);
        var buzzer = Place(circuit, new Buzzer { Kind = BuzzerKind.Active }, 660, 140);

        var gnd = Place(circuit, new Ground(), -600, 340);
        var gnd2 = Place(circuit, new Ground(), -360, 180);
        var gnd3 = Place(circuit, new Ground(), -160, 180);
        var gnd4 = Place(circuit, new Ground(), 100, 120);
        var gnd5 = Place(circuit, new Ground(), 660, 300);

        sensor.RotationDegrees = 90;
        lower.RotationDegrees = 90;
        referenceTop.RotationDegrees = 90;
        referenceBottom.RotationDegrees = 90;
        pullUp.RotationDegrees = 90;

        circuit.Connect(supply.Negative, gnd.Pin);

        // The sensing divider: an NTC falls as it warms, so this node rises with temperature.
        circuit.Connect(sensor.A, supply.Positive);
        circuit.Connect(sensor.B, lower.A);
        circuit.Connect(lower.B, gnd2.Pin);

        circuit.Connect(referenceTop.A, supply.Positive);
        circuit.Connect(referenceTop.B, referenceBottom.A);
        circuit.Connect(referenceBottom.B, gnd3.Pin);

        circuit.Connect(comparator.PositiveSupply, supply.Positive);
        circuit.Connect(comparator.NegativeSupply, gnd4.Pin);
        circuit.Connect(comparator.NonInverting, sensor.B);
        circuit.Connect(comparator.Inverting, referenceTop.B);

        // The hysteresis, which moves the threshold the moment the output changes so the noise on
        // the sensor cannot walk it back across.
        circuit.Connect(feedback.A, comparator.Output);
        circuit.Connect(feedback.B, comparator.NonInverting);

        circuit.Connect(pullUp.A, supply.Positive);
        circuit.Connect(pullUp.B, comparator.Output);

        circuit.Connect(limiter.A, supply.Positive);
        circuit.Connect(limiter.B, lamp.Anode);
        circuit.Connect(lamp.Cathode, comparator.Output);

        circuit.Connect(buzzer.A, supply.Positive);
        circuit.Connect(buzzer.B, comparator.Output);
        circuit.Connect(gnd5.Pin, gnd.Pin);

        vm.Scope.TimebasePerDivision = 10e-3;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.AddProbe(sensor.B, "Sensor");
        vm.Scope.AddProbe(comparator.NonInverting, "Threshold");
        vm.Scope.AddProbe(comparator.Output, "Output");
    }

    /// <summary>
    /// The ULN2003's own reason for existing: four windings, energised in turn, with the array's
    /// freewheeling diodes catching each one as it lets go.
    /// </summary>
    public static void LoadStepper(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Stepper motor on a ULN2003";

        var logic = Place(circuit, new DcVoltageSource(5.0), -680, 240);
        var coilSupply = Place(circuit, new DcVoltageSource(12.0), 620, 300);
        var clock = Place(circuit, new ClockSource(40.0) { Levels = LogicLevels.Cmos5V }, -680, -160);

        // A 4017 walks one output at a time, which is exactly the wave-drive sequence.
        var sequencer = Place(circuit, new Ic4017(), -400, -40);
        var driver = Place(circuit, new Uln2003(), 40, -40);
        var motor = Place(circuit, new StepperMotor(), 400, -40);

        var gnd = Place(circuit, new Ground(), -680, 400);
        var gnd2 = Place(circuit, new Ground(), -400, 200);
        var gnd3 = Place(circuit, new Ground(), 40, 200);
        var gnd4 = Place(circuit, new Ground(), 620, 460);

        circuit.Connect(logic.Negative, gnd.Pin);
        circuit.Connect(coilSupply.Negative, gnd4.Pin);

        circuit.Connect(sequencer.Vcc, logic.Positive);
        circuit.Connect(sequencer.Gnd, gnd2.Pin);
        circuit.Connect(sequencer.Clock, clock.Out);
        circuit.Connect(sequencer.ClockInhibit, gnd2.Pin);

        // Reset on the fifth output, so it counts 0-3 and starts again: four coils, four steps.
        circuit.Connect(sequencer.Reset, sequencer.Outputs[4]);

        circuit.Connect(driver.Gnd, gnd3.Pin);
        circuit.Connect(driver.Common, coilSupply.Positive);

        for (var i = 0; i < 4; i++)
        {
            circuit.Connect(driver.Inputs[i], sequencer.Outputs[i]);
            circuit.Connect(driver.Outputs[i], motor.Coils[i]);
        }

        for (var i = 4; i < driver.Inputs.Count; i++) circuit.Connect(driver.Inputs[i], gnd3.Pin);

        circuit.Connect(motor.Common, coilSupply.Positive);

        vm.Scope.TimebasePerDivision = 10e-3;
        vm.Scope.VoltsPerDivision = 5.0;
        vm.Scope.AddProbe(motor.Coils[0], "Coil A");
        vm.Scope.AddProbe(motor.Coils[1], "Coil B");
    }

    /// <summary>
    /// Three cells, the same load on each, and the only thing separating them is the resistance
    /// inside them. It is the number nobody reads off the packet and the one that decides what a
    /// battery can actually run.
    /// </summary>
    public static void LoadBatteryInternalResistance(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "What internal resistance costs";

        var cells = new[]
        {
            BatteryModel.AlkalineAA,
            BatteryModel.CoinCell2032,
            BatteryModel.Lithium18650,
        };

        var y = -280;

        foreach (var model in cells)
        {
            var battery = Place(circuit, new Battery(model) { StateOfCharge = 1.0 }, -300, y);
            var load = Place(circuit, new DcCurrentSource { Current = 0.05 }, 40, y);
            var ground = Place(circuit, new Ground(), -300, y + 160);
            var loadGround = Place(circuit, new Ground(), 40, y + 160);

            circuit.Connect(battery.Negative, ground.Pin);
            circuit.Connect(battery.Positive, load.Positive);
            circuit.Connect(load.Negative, loadGround.Pin);

            vm.Scope.AddProbe(battery.Positive, model.Name);

            y += 280;
        }

        vm.Scope.TimebasePerDivision = 10e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
    }

    /// <summary>
    /// A lamp that turns itself on when it gets dark, with a proper reference rather than half the
    /// supply — and a switch to override it, because every real one has that too.
    /// </summary>
    public static void LoadNightLight(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Light-operated switch";

        var supply = Place(circuit, new DcVoltageSource(5.0), -640, 220);

        var sensor = Place(circuit, new LightDependentResistor { Illuminance = 500 }, -400, -140);
        var lower = Place(circuit, new Resistor(10e3), -400, 40);

        // A TL431 rather than another divider. A divider's threshold moves with the supply; this
        // one does not, which is the difference between a light switch and a battery meter.
        // A kilohm, so the shunt still has its milliamp with the rail down at four volts. Sized
        // by what is left at the bottom of the supply rather than by what flows at the top is the
        // classic way to get a reference that works on the bench and starves on batteries.
        var feed = Place(circuit, new Resistor(1e3), -180, -200);
        var reference = Place(circuit, new ShuntReference(), -180, -40);

        var comparator = Place(circuit, new Comparator(ComparatorModel.Lm311), 80, -80);
        var pullUp = Place(circuit, new Resistor(4.7e3), 300, -220);
        var limiter = Place(circuit, new Resistor(330), 480, -80);
        var lamp = Place(circuit, Led.OfColour("Yellow"), 620, -80);
        var mode = Place(circuit, new SpdtSwitch(), 620, 140);

        var gnd = Place(circuit, new Ground(), -640, 380);
        var gnd2 = Place(circuit, new Ground(), -400, 180);
        var gnd3 = Place(circuit, new Ground(), -180, 120);
        var gnd4 = Place(circuit, new Ground(), 80, 100);
        var gnd5 = Place(circuit, new Ground(), 760, 300);

        sensor.RotationDegrees = 90;
        lower.RotationDegrees = 90;
        feed.RotationDegrees = 90;
        limiter.RotationDegrees = 90;
        lamp.RotationDegrees = 90;
        pullUp.RotationDegrees = 90;

        circuit.Connect(supply.Negative, gnd.Pin);

        // Light makes the LDR conduct, so this node rises with the light on it.
        circuit.Connect(sensor.A, supply.Positive);
        circuit.Connect(sensor.B, lower.A);
        circuit.Connect(lower.B, gnd2.Pin);

        // Reference pin tied to the cathode makes it a fixed 2.5 V shunt.
        circuit.Connect(feed.A, supply.Positive);
        circuit.Connect(feed.B, reference.Cathode);
        circuit.Connect(reference.Reference, reference.Cathode);
        circuit.Connect(reference.Anode, gnd3.Pin);

        circuit.Connect(comparator.PositiveSupply, supply.Positive);
        circuit.Connect(comparator.NegativeSupply, gnd4.Pin);
        // Dark pulls the sensing node down, so it goes to the inverting input: below the
        // reference the output lets go, and the lamp's cathode has nowhere to sink to.
        // The other way round gives a lamp that comes on in daylight.
        circuit.Connect(comparator.NonInverting, sensor.B);
        circuit.Connect(comparator.Inverting, reference.Cathode);

        circuit.Connect(pullUp.A, supply.Positive);
        circuit.Connect(pullUp.B, comparator.Output);

        // The lamp hangs off the rail and is switched at its cathode end, which is what an
        // open-collector output can do.
        circuit.Connect(limiter.A, supply.Positive);
        circuit.Connect(limiter.B, lamp.Anode);
        circuit.Connect(lamp.Cathode, mode.Common);

        // Auto on one throw, permanently on at the other.
        circuit.Connect(mode.ThrowA, comparator.Output);
        circuit.Connect(mode.ThrowB, gnd5.Pin);

        vm.Scope.TimebasePerDivision = 10e-3;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.AddProbe(sensor.B, "Light");
        vm.Scope.AddProbe(reference.Cathode, "Reference");
        vm.Scope.AddProbe(lamp.Cathode, "Lamp");
    }

    /// <summary>
    /// Microvolts per degree out of two wires, and the thing every thermocouple circuit has to
    /// deal with: it measures a difference, not a temperature.
    /// </summary>
    public static void LoadThermocouple(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Thermocouple and cold junction";

        var supply = Place(circuit, new DcVoltageSource(5.0), -560, 160);
        var probe = Place(circuit, new Thermocouple(ThermocoupleType.K) { Temperature = 300.0 },
            -300, -80);
        // A hundred, not more: the gain is chosen for the range you want, and a thermocouple that
        // reads to its full twelve hundred degrees cannot also resolve the first fifty.
        var amplifier = Place(circuit, new Ina126 { DifferentialGain = 100 }, 100, -60);

        var referenceTop = Place(circuit, new Resistor(15e3), 40, 220);
        var referenceBottom = Place(circuit, new Resistor(5e3), 40, 360);

        var gnd = Place(circuit, new Ground(), -560, 320);
        var gnd2 = Place(circuit, new Ground(), -300, 120);
        var gnd3 = Place(circuit, new Ground(), 40, 480);

        probe.RotationDegrees = 90;
        referenceTop.RotationDegrees = 90;
        referenceBottom.RotationDegrees = 90;

        circuit.Connect(supply.Negative, gnd.Pin);

        circuit.Connect(probe.B, gnd2.Pin);
        circuit.Connect(amplifier.InPlus, probe.A);
        circuit.Connect(amplifier.InMinus, probe.B);

        circuit.Connect(amplifier.PositiveSupply, supply.Positive);
        circuit.Connect(amplifier.NegativeSupply, gnd.Pin);

        // The reference the output is measured from, set above the bottom rail so the amplifier
        // has room to sit at zero degrees of difference.
        circuit.Connect(referenceTop.A, supply.Positive);
        circuit.Connect(referenceTop.B, referenceBottom.A);
        circuit.Connect(referenceBottom.B, gnd3.Pin);
        circuit.Connect(referenceTop.B, amplifier.Reference);

        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.AddProbe(probe.A, "Junction");
        vm.Scope.AddProbe(amplifier.Output, "Amplified");
    }

    /// <summary>
    /// A servo, where the angle is in the width of a pulse and nothing else — not its height, not
    /// its rate, not the average.
    /// </summary>
    public static void LoadServoSweep(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Pulse width and a servo";

        var supply = Place(circuit, new DcVoltageSource(5.0), -520, 160);

        // Fifty hertz, and a duty cycle that works out at a millisecond and a half — the middle
        // of the travel. The Duty control is the joystick.
        var pulses = Place(circuit, new FunctionGenerator(Waveform.Square, 50.0, 5.0)
        {
            DcOffset = 2.5,
            DutyCycle = 0.075,
        }, -520, -140);

        var servo = Place(circuit, new Servo(), -80, -60);

        var gnd = Place(circuit, new Ground(), -520, 320);
        var gnd2 = Place(circuit, new Ground(), -520, 20);
        var gnd3 = Place(circuit, new Ground(), -80, 160);

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(pulses.Return, gnd2.Pin);

        circuit.Connect(servo.Supply, supply.Positive);
        circuit.Connect(servo.Ground, gnd3.Pin);
        circuit.Connect(servo.Signal, pulses.Output);

        vm.Scope.TimebasePerDivision = 5e-3;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.AddProbe(pulses.Output, "Pulses");
    }

    /// <summary>
    /// A Hall switch counting a magnet, and the two thresholds that let it do so cleanly where a
    /// mechanical contact cannot.
    /// </summary>
    public static void LoadHallCounter(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Counting with a Hall switch";

        var supply = Place(circuit, new DcVoltageSource(5.0), -520, 180);
        var sensor = Place(circuit, new HallSensor { FluxDensity = 0.0 }, -260, -60);

        // Open drain, like almost every Hall switch sold: it pulls down and lets go.
        var pullUp = Place(circuit, new Resistor(10e3), -40, -220);

        var counter = Place(circuit, new Ic4040(), 180, -60);
        var limiter = Place(circuit, new Resistor(330), 480, 40);
        var lamp = Place(circuit, Led.OfColour("Blue"), 620, 40);

        var gnd = Place(circuit, new Ground(), -520, 340);
        var gnd2 = Place(circuit, new Ground(), -260, 160);
        var gnd3 = Place(circuit, new Ground(), 180, 220);
        var gnd4 = Place(circuit, new Ground(), 620, 200);

        pullUp.RotationDegrees = 90;
        limiter.RotationDegrees = 90;
        lamp.RotationDegrees = 90;

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(sensor.Vcc, supply.Positive);
        circuit.Connect(sensor.Gnd, gnd2.Pin);

        circuit.Connect(pullUp.A, supply.Positive);
        circuit.Connect(pullUp.B, sensor.Output);

        circuit.Connect(counter.Vcc, supply.Positive);
        circuit.Connect(counter.Gnd, gnd3.Pin);
        circuit.Connect(counter.Reset, gnd3.Pin);
        circuit.Connect(counter.Clock, sensor.Output);

        circuit.Connect(limiter.A, counter.Outputs[0]);
        circuit.Connect(limiter.B, lamp.Anode);
        circuit.Connect(lamp.Cathode, gnd4.Pin);

        vm.Scope.TimebasePerDivision = 20e-3;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.AddProbe(sensor.Output, "Hall");
        vm.Scope.AddProbe(counter.Outputs[0], "Count / 2");
    }

    /// <summary>
    /// Four analog switches and eight ways to get them wrong. The one worth seeing is what happens
    /// when two of them are closed together.
    /// </summary>
    public static void LoadAnalogSwitch(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "A 4066 selecting between sources";

        var supply = Place(circuit, new DcVoltageSource(5.0), -680, 300);
        var switches = Place(circuit, new Ic4066(), 40, 0);
        var selector = Place(circuit, new DipSwitch { Position1 = true }, -260, 260);

        var gnd = Place(circuit, new Ground(), -680, 460);
        var gnd2 = Place(circuit, new Ground(), 40, 240);
        var gnd3 = Place(circuit, new Ground(), 420, 300);

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(switches.Vcc, supply.Positive);
        circuit.Connect(switches.Gnd, gnd2.Pin);

        // Four sources at four frequencies, so which one is getting through is obvious.
        var sources = new[] { 500.0, 1e3, 2e3, 4e3 };

        for (var i = 0; i < 4; i++)
        {
            var source = Place(circuit, new FunctionGenerator(Waveform.Sine, sources[i], 2.0)
            {
                DcOffset = 2.5,
            }, -680, -320 + (i * 160));

            var sourceGround = Place(circuit, new Ground(), -560, -240 + (i * 160));

            circuit.Connect(source.Return, sourceGround.Pin);

            var (a, b) = switches.Switch(i);
            circuit.Connect(source.Output, a);

            // Every switch's far side lands on the same node — the first one's B pin is that
            // node, so it is already there.
            if (i > 0) circuit.Connect(b, switches.Switch(0).B);

            // Each control pin comes off the rail through one section of the DIP switch, with a
            // resistor holding it down when the section is open — a CMOS control input left
            // floating decides for itself.
            var (dipA, dipB) = selector.Section(i);
            var hold = Place(circuit, new Resistor(100e3), -120, 180 + (i * 80));

            circuit.Connect(dipA, supply.Positive);
            circuit.Connect(dipB, switches.Control(i));
            circuit.Connect(hold.A, switches.Control(i));
            circuit.Connect(hold.B, gnd2.Pin);
        }

        // The common node, loaded high so the switch resistance is a rounding error rather than a
        // divider. Through a hundred ohms it would not be.
        var load = Place(circuit, new Resistor(100e3), 420, 120);
        load.RotationDegrees = 90;

        circuit.Connect(switches.Switch(0).B, load.A);
        circuit.Connect(load.B, gnd3.Pin);

        vm.Scope.TimebasePerDivision = 500e-6;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.AddProbe(switches.Switch(0).B, "Selected");
    }

    /// <summary>
    /// The two parts that sit on the line between the analog solver and the logic engine, doing
    /// the only two jobs there are to do there.
    /// </summary>
    public static void LoadAnalogBoundary(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Crossing between analog and logic";

        // A slow ramp is the hard case: it spends a long time near the threshold, which is exactly
        // where a few millivolts of noise decides the answer.
        var ramp = Place(circuit, new FunctionGenerator(Waveform.Triangle, 200.0, 4.0)
        {
            DcOffset = 2.0,
        }, -640, -80);

        var noise = Place(circuit, new NoiseSource { RmsVoltage = 60e-3 }, -420, -80);

        var toLogic = Place(circuit, new AdcBridge(), -120, -80);
        var toAnalog = Place(circuit, new DacBridge { AnalogHigh = 3.3 }, 180, -80);
        var load = Place(circuit, new Resistor(10e3), 420, 40);

        var gnd = Place(circuit, new Ground(), -640, 120);
        var gnd2 = Place(circuit, new Ground(), 420, 200);

        load.RotationDegrees = 90;

        circuit.Connect(ramp.Return, gnd.Pin);
        circuit.Connect(ramp.Output, noise.A);
        circuit.Connect(noise.B, toLogic.AnalogIn);

        circuit.Connect(toLogic.DigitalOut, toAnalog.DigitalIn);
        circuit.Connect(toAnalog.AnalogOut, load.A);
        circuit.Connect(load.B, gnd2.Pin);

        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(toLogic.AnalogIn, "Noisy ramp");
        vm.Scope.AddProbe(toLogic.DigitalOut, "As logic");
        vm.Scope.AddProbe(toAnalog.AnalogOut, "Back to analog");
    }

    /// <summary>
    /// Two transceivers onto one bus, and the counter that feeds it — the tri-state arrangement
    /// every processor uses to share eight wires between everything on the board.
    /// </summary>
    public static void LoadSharedBus(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "A bus two devices take turns on";

        var supply = Place(circuit, new DcVoltageSource(5.0), -760, 260);
        var clock = Place(circuit, new ClockSource(500.0), -760, -200);

        // The talker: a synchronous counter, so every bit of the byte it puts on the bus changes
        // on the same edge and there is nothing to decode a glitch from.
        var counter = Place(circuit, new Ic74161(), -520, -60);
        var talker = Place(circuit, new Ic74245(), -160, -60);

        // The listener, pointed the other way and enabled by the opposite phase, so exactly one
        // of the two is driving at any moment. That arrangement is the whole discipline of a bus.
        var listener = Place(circuit, new Ic74245(), 320, -60);
        var select = Place(circuit, new LogicToggle(), -420, 300);

        // The inverter is the whole discipline in one part: the two enables are opposites, so
        // whichever way SW1 is thrown exactly one transceiver is driving. Wire both enables to
        // the same signal instead and they fight — each holding a wire through a few tens of
        // ohms, the pair of them at half a supply, and nothing on the canvas warning you.
        var opposite = Place(circuit, new LogicGate(GateFunction.Not, 1), -160, 300);

        var gnd = Place(circuit, new Ground(), -760, 420);
        var gnd2 = Place(circuit, new Ground(), -520, 180);
        var gnd3 = Place(circuit, new Ground(), -160, 180);
        var gnd4 = Place(circuit, new Ground(), 320, 180);

        circuit.Connect(supply.Negative, gnd.Pin);

        foreach (var ic in new DigitalIc[] { counter, talker, listener })
        {
            circuit.Connect(ic.Vcc, supply.Positive);
        }

        circuit.Connect(counter.Gnd, gnd2.Pin);
        circuit.Connect(talker.Gnd, gnd3.Pin);
        circuit.Connect(listener.Gnd, gnd4.Pin);

        circuit.Connect(counter.Clock, clock.Out);
        circuit.Connect(counter.MasterReset, supply.Positive);
        circuit.Connect(counter.ParallelEnable, supply.Positive);
        circuit.Connect(counter.CountEnableP, supply.Positive);
        circuit.Connect(counter.CountEnableT, supply.Positive);

        foreach (var data in counter.Data) circuit.Connect(data, gnd2.Pin);

        // The counter's four bits onto the talker's A side; the top four are tied off.
        for (var i = 0; i < 4; i++) circuit.Connect(talker.A[i], counter.Outputs[i]);
        for (var i = 4; i < 8; i++) circuit.Connect(talker.A[i], gnd3.Pin);

        // One bus, both transceivers on it.
        for (var i = 0; i < 8; i++) circuit.Connect(talker.B[i], listener.B[i]);

        // Direction fixed, enables opposed: SW1 hands the bus from one to the other.
        circuit.Connect(talker.Direction, supply.Positive);
        circuit.Connect(listener.Direction, gnd4.Pin);
        circuit.Connect(talker.OutputEnable, select.Out);
        circuit.Connect(opposite.InputTerminals[0], select.Out);
        circuit.Connect(listener.OutputEnable, opposite.OutputTerminals[0]);

        // Something for the listener's A side to drive, and a resistor per bus wire so a released
        // bus reads as nothing rather than floating at whatever it was left at.
        for (var i = 0; i < 8; i++)
        {
            var pull = Place(circuit, new Resistor(10e3), 80 + (i * 34), 320);
            var pullGround = Place(circuit, new Ground(), 80 + (i * 34), 440);

            pull.RotationDegrees = 90;

            circuit.Connect(pull.A, talker.B[i]);
            circuit.Connect(pull.B, pullGround.Pin);

            var load = Place(circuit, new Resistor(10e3), 660 + (i * 34), 120);
            var loadGround = Place(circuit, new Ground(), 660 + (i * 34), 240);

            load.RotationDegrees = 90;

            circuit.Connect(load.A, listener.A[i]);
            circuit.Connect(load.B, loadGround.Pin);
        }

        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(counter.Outputs[0], "Count bit 0");
        vm.Scope.AddProbe(talker.B[0], "Bus bit 0");
        vm.Scope.AddProbe(listener.A[0], "Listener bit 0");
    }

    /// <summary>
    /// Two CAN nodes talking over each other, which on this bus is not a collision at all.
    /// </summary>
    public static void LoadCanArbitration(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "CAN arbitration";

        var supply = Place(circuit, new DcVoltageSource(5.0), -620, 260);

        // Two nodes with something to say at the same time. The clocks run at different rates so
        // the pattern of who wins changes as you watch.
        var first = Place(circuit, new CanTransceiver(), -260, -140);
        var second = Place(circuit, new CanTransceiver(), -260, 140);

        var firstTalk = Place(circuit, new ClockSource(2e3), -620, -160);
        var secondTalk = Place(circuit, new ClockSource(700.0), -620, 120);

        var nearEnd = Place(circuit, new Resistor(120), 140, -60);
        var farEnd = Place(circuit, new Resistor(120), 420, -60);

        var gnd = Place(circuit, new Ground(), -620, 420);
        var gnd2 = Place(circuit, new Ground(), -260, 40);
        var gnd3 = Place(circuit, new Ground(), -260, 320);

        nearEnd.RotationDegrees = 90;
        farEnd.RotationDegrees = 90;

        circuit.Connect(supply.Negative, gnd.Pin);

        circuit.Connect(first.Vcc, supply.Positive);
        circuit.Connect(first.Gnd, gnd2.Pin);
        circuit.Connect(second.Vcc, supply.Positive);
        circuit.Connect(second.Gnd, gnd3.Pin);

        circuit.Connect(first.TransmitIn, firstTalk.Out);
        circuit.Connect(second.TransmitIn, secondTalk.Out);

        // One pair, both nodes on it, terminated at each end as a real bus is.
        circuit.Connect(first.High, second.High);
        circuit.Connect(first.Low, second.Low);

        circuit.Connect(nearEnd.A, first.High);
        circuit.Connect(nearEnd.B, first.Low);
        circuit.Connect(farEnd.A, second.High);
        circuit.Connect(farEnd.B, second.Low);

        vm.Scope.TimebasePerDivision = 500e-6;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(first.High, "CANH");
        vm.Scope.AddProbe(first.Low, "CANL");
        vm.Scope.AddProbe(first.ReceiveOut, "What both nodes hear");
    }

    /// <summary>
    /// The same load switched by an IGBT and by a MOSFET, from one gate drive — the comparison
    /// that decides which of them a design wants.
    /// </summary>
    public static void LoadIgbtAgainstMosfet(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "IGBT against MOSFET";

        var supply = Place(circuit, new DcVoltageSource(60.0), -620, 180);
        var drive = Place(circuit, new FunctionGenerator(Waveform.Square, 2e3, 15.0) { DcOffset = 7.5 },
            -620, -200);

        var igbtLoad = Place(circuit, new Resistor(6.0), -160, -280);
        var igbt = Place(circuit, new Igbt(), -160, -100);

        var mosfetLoad = Place(circuit, new Resistor(6.0), 220, -280);
        var mosfet = Place(circuit, new Mosfet(MosfetModel.IrlZ44N), 220, -100);

        var gnd = Place(circuit, new Ground(), -620, 340);
        var gnd2 = Place(circuit, new Ground(), -620, -40);
        var gnd3 = Place(circuit, new Ground(), -160, 60);
        var gnd4 = Place(circuit, new Ground(), 220, 60);

        igbtLoad.RotationDegrees = 90;
        mosfetLoad.RotationDegrees = 90;

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(drive.Return, gnd2.Pin);

        circuit.Connect(supply.Positive, igbtLoad.A);
        circuit.Connect(igbtLoad.B, igbt.Collector);
        circuit.Connect(igbt.Emitter, gnd3.Pin);
        circuit.Connect(igbt.Gate, drive.Output);

        circuit.Connect(supply.Positive, mosfetLoad.A);
        circuit.Connect(mosfetLoad.B, mosfet.Drain);
        circuit.Connect(mosfet.Source, gnd4.Pin);
        circuit.Connect(mosfet.Gate, drive.Output);

        vm.Scope.TimebasePerDivision = 100e-6;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.AddProbe(igbt.Collector, "IGBT on-state");
        vm.Scope.AddProbe(mosfet.Drain, "MOSFET on-state");
    }

    /// <summary>
    /// A photodiode into a transimpedance amplifier, which is the only way the part is ever
    /// actually used.
    /// </summary>
    public static void LoadTransimpedance(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Photodiode and transimpedance amplifier";

        var positive = Place(circuit, new DcVoltageSource(9.0), -600, 160);
        var negative = Place(circuit, new DcVoltageSource(9.0), -600, 360);

        var diode = Place(circuit, new Photodiode { Illuminance = 300 }, -220, -120);

        // A megohm of feedback: a microamp of photocurrent becomes a volt, which is what makes
        // the part readable at all.
        var feedback = Place(circuit, new Resistor(1e6), 60, -300);
        var amplifier = Place(circuit, new OperationalAmplifier(OpAmpModel.Tl081), 80, -120);
        var load = Place(circuit, new Resistor(100e3), 380, -40);

        var gnd = Place(circuit, new Ground(), -600, 260);
        var gnd2 = Place(circuit, new Ground(), -220, 40);
        var gnd3 = Place(circuit, new Ground(), 80, 20);
        var gnd4 = Place(circuit, new Ground(), 380, 100);

        diode.RotationDegrees = 90;
        load.RotationDegrees = 90;

        // A split supply, so the output can go either side of ground.
        circuit.Connect(positive.Negative, gnd.Pin);
        circuit.Connect(negative.Positive, gnd.Pin);

        // The diode across the inputs, cathode to the summing junction. The amplifier holds that
        // junction at ground, so the diode sits at zero volts however much light falls on it —
        // which is what stops it saturating the way a load resistor lets it.
        circuit.Connect(diode.Anode, gnd2.Pin);
        circuit.Connect(diode.Cathode, amplifier.Inverting);

        circuit.Connect(amplifier.NonInverting, gnd3.Pin);
        circuit.Connect(amplifier.PositiveSupply, positive.Positive);
        circuit.Connect(amplifier.NegativeSupply, negative.Negative);

        circuit.Connect(feedback.A, amplifier.Inverting);
        circuit.Connect(feedback.B, amplifier.Output);

        circuit.Connect(amplifier.Output, load.A);
        circuit.Connect(load.B, gnd4.Pin);

        vm.Scope.TimebasePerDivision = 10e-3;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.AddProbe(diode.Cathode, "Summing junction");
        vm.Scope.AddProbe(amplifier.Output, "Output");
    }

    /// <summary>
    /// A counter, a ROM and a latch: something walks through addresses and something else answers
    /// with what is stored there. That is the shape of every computer ever built.
    /// </summary>
    public static void LoadAddressedMemory(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "A counter reading a ROM";

        var supply = Place(circuit, new DcVoltageSource(5.0), -880, 320);
        var clock = Place(circuit, new ClockSource(400.0), -880, -180);

        // The program counter. Synchronous, so the whole address changes at once and the memory
        // is never shown a number nobody asked for.
        var counter = Place(circuit, new Ic74161(), -620, -40);

        // Eight bytes to walk through. Type anything into Contents and the circuit reads it back.
        var rom = Place(circuit, new MemoryDevice
        {
            Contents = "48 45 4C 4C 4F 21 00 FF",
            IsReadOnly = true,
        }, -220, -40);

        // The byte the memory answered with, held steady after the memory has let the bus go —
        // which is what every processor does with the data it fetches.
        var latch = Place(circuit, new Ic74373(), 240, -40);

        var gnd = Place(circuit, new Ground(), -880, 480);
        var gnd2 = Place(circuit, new Ground(), -620, 180);
        var gnd3 = Place(circuit, new Ground(), -220, 220);
        var gnd4 = Place(circuit, new Ground(), 240, 180);

        circuit.Connect(supply.Negative, gnd.Pin);

        foreach (var ic in new DigitalIc[] { counter, rom, latch })
            circuit.Connect(ic.Vcc, supply.Positive);

        circuit.Connect(counter.Gnd, gnd2.Pin);
        circuit.Connect(rom.Gnd, gnd3.Pin);
        circuit.Connect(latch.Gnd, gnd4.Pin);

        circuit.Connect(counter.Clock, clock.Out);
        circuit.Connect(counter.MasterReset, supply.Positive);
        circuit.Connect(counter.ParallelEnable, supply.Positive);
        circuit.Connect(counter.CountEnableP, supply.Positive);
        circuit.Connect(counter.CountEnableT, supply.Positive);

        foreach (var data in counter.Data) circuit.Connect(data, gnd2.Pin);

        // Four address bits from the counter; the other seven are tied low, so it walks the first
        // sixteen bytes over and over.
        for (var i = 0; i < 4; i++) circuit.Connect(rom.Address[i], counter.Outputs[i]);
        for (var i = 4; i < MemoryDevice.AddressLines; i++) circuit.Connect(rom.Address[i], gnd3.Pin);

        // Selected and reading throughout, because nothing else is on this bus to take a turn.
        circuit.Connect(rom.ChipEnable, gnd3.Pin);
        circuit.Connect(rom.OutputEnable, gnd3.Pin);
        circuit.Connect(rom.WriteEnable, supply.Positive);

        // The data bus, with a resistor per wire so a released bus reads as nothing rather than
        // holding whatever was last on it.
        for (var i = 0; i < 8; i++)
        {
            var pull = Place(circuit, new Resistor(10e3), 0 + (i * 34), 300);
            var pullGround = Place(circuit, new Ground(), 0 + (i * 34), 420);

            pull.RotationDegrees = 90;

            circuit.Connect(rom.Data[i], pull.A);
            circuit.Connect(pull.B, pullGround.Pin);
            circuit.Connect(latch.Data[i], rom.Data[i]);

            var load = Place(circuit, new Resistor(10e3), 520 + (i * 34), 120);
            var loadGround = Place(circuit, new Ground(), 520 + (i * 34), 240);

            load.RotationDegrees = 90;

            circuit.Connect(latch.Outputs[i], load.A);
            circuit.Connect(load.B, loadGround.Pin);
        }

        // The latch is left open, so the outputs follow the bus. Close it — tie LE low — and it
        // freezes the last byte fetched, which is the point of having one.
        circuit.Connect(latch.LatchEnable, supply.Positive);
        circuit.Connect(latch.OutputEnable, gnd4.Pin);

        vm.Scope.TimebasePerDivision = 2e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(counter.Outputs[0], "Address bit 0");
        vm.Scope.AddProbe(rom.Data[0], "Data bit 0");
        vm.Scope.AddProbe(latch.Outputs[0], "Latched bit 0");
    }

    /// <summary>
    /// An SPI converter reading a potentiometer, which is the commonest thing anybody hangs off an
    /// SPI port — and compulsory on a board with no analog inputs of its own.
    /// </summary>
    public static void LoadSpiAdc(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Reading a voltage over SPI";

        var supply = Place(circuit, new DcVoltageSource(5.0), -620, 260);

        // Three bytes: a start bit, then single-ended and channel zero, then padding for the
        // answer to come back underneath. The whole conversation is one transaction.
        var master = Place(circuit, new SpiMaster
        {
            Transactions = "01 80 00",
            ClockFrequency = 200e3,
        }, -380, -120);

        var adc = Place(circuit, new Mcp3008(), 60, -60);
        var knob = Place(circuit, new Potentiometer(10e3, 0.6), 420, 120);

        var gnd = Place(circuit, new Ground(), -620, 420);
        var gnd2 = Place(circuit, new Ground(), -380, 120);
        var gnd3 = Place(circuit, new Ground(), 60, 220);
        var gnd4 = Place(circuit, new Ground(), 420, 300);

        knob.RotationDegrees = 90;

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(master.Vcc, supply.Positive);
        circuit.Connect(master.Gnd, gnd2.Pin);

        circuit.Connect(adc.Vcc, supply.Positive);
        circuit.Connect(adc.Gnd, gnd3.Pin);
        circuit.Connect(adc.AnalogGround, gnd3.Pin);

        // Reference tied to the same rail the divider runs from, which makes the reading
        // ratiometric: supply noise moves both ends and cancels out of the answer.
        circuit.Connect(adc.Reference, supply.Positive);

        circuit.Connect(master.Clock, adc.Clock);
        circuit.Connect(master.MasterOut, adc.DataIn);
        circuit.Connect(master.MasterIn, adc.DataOutPin);
        circuit.Connect(master.ChipSelect, adc.ChipSelect);

        // Ground at A and the rail at B, so the Wiper control reads as a knob: the tap ratio is
        // measured from A, and wired the other way round turning it up reads as less.
        circuit.Connect(knob.A, gnd4.Pin);
        circuit.Connect(knob.B, supply.Positive);
        circuit.Connect(knob.Wiper, adc.Inputs[0]);

        // The seven unused channels want tying down rather than left floating.
        for (var i = 1; i < 8; i++) circuit.Connect(adc.Inputs[i], gnd3.Pin);

        vm.Scope.TimebasePerDivision = 20e-6;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(adc.Clock, "CLK");
        vm.Scope.AddProbe(adc.DataIn, "MOSI");
        vm.Scope.AddProbe(adc.DataOutPin, "MISO");
    }

    /// <summary>
    /// An A4988 running a bipolar motor. The winding current is the thing to look at: it is
    /// regulated, not applied, and the sawtooth on it is the chopper working.
    /// </summary>
    public static void LoadMicrosteppingDrive(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Microstepping drive";

        var logic = Place(circuit, new DcVoltageSource(5.0), -620, 240);

        // Twelve volts into a two ohm winding would be six amps if anybody applied it. Nobody
        // does — the supply is high so the current rises fast, and the chopper takes care of the
        // rest. That is the whole idea of the part.
        var rail = Place(circuit, new DcVoltageSource(12.0), -620, 440);

        var clock = Place(circuit, new ClockSource(200.0), -400, -160);
        var driver = Place(circuit, new StepperDriver { CurrentLimit = 0.8 }, -60, 0);

        var motor = Place(circuit, new StepperMotor
        {
            Wiring = StepperWiring.Bipolar,
            CoilResistance = 2.0,
            CoilInductance = 2e-3,
            StepsPerRevolution = 200,
        }, 340, 0);

        var gnd = Place(circuit, new Ground(), -620, 400);
        var gnd2 = Place(circuit, new Ground(), -620, 600);
        var gnd3 = Place(circuit, new Ground(), -60, 240);

        circuit.Connect(logic.Negative, gnd.Pin);
        circuit.Connect(rail.Negative, gnd2.Pin);

        circuit.Connect(driver.Vcc, logic.Positive);
        circuit.Connect(driver.Gnd, gnd3.Pin);
        circuit.Connect(driver.Motor, rail.Positive);

        circuit.Connect(driver.Step, clock.Out);
        circuit.Connect(driver.Direction, logic.Positive);

        // Enable is active low, so grounding it turns the outputs on.
        circuit.Connect(driver.Enable, gnd3.Pin);

        // MS1-MS3 all low is full stepping. Tie MS1 to the logic rail for half steps, MS1 and MS2
        // for eighths — and the current in the winding below stops being a square wave and starts
        // being a staircase approximating a sine.
        foreach (var pin in new[] { driver.Ms1, driver.Ms2, driver.Ms3 })
            circuit.Connect(pin, gnd3.Pin);

        // C1-C3 is one winding on a bipolar motor and C2-C4 the other.
        circuit.Connect(driver.OutputA1, motor.Coils[0]);
        circuit.Connect(driver.OutputB1, motor.Coils[2]);
        circuit.Connect(driver.OutputA2, motor.Coils[1]);
        circuit.Connect(driver.OutputB2, motor.Coils[3]);

        vm.Scope.TimebasePerDivision = 2e-3;
        vm.Scope.VoltsPerDivision = 5.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(clock.Out, "STEP");
        vm.Scope.AddProbe(driver.OutputA1, "1A");

        // The one to watch. It is regulated rather than applied, so it is flat-topped at the
        // limit with the chopper's sawtooth on it, and it reverses every other step.
        var winding = vm.Scope.AddProbe(motor.Coils[0], "Winding 1");
        winding.Kind = Cirq.Core.Probing.ProbeKind.Current;

        vm.Simulation.SpeedFactor = 0.02;
    }

    /// <summary>
    /// A solid-state relay commanded mid-cycle. The point of the example is the delay: it does
    /// nothing at all until the mains next passes through zero.
    /// </summary>
    public static void LoadZeroCrossingSwitch(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Zero-crossing switch";

        var mains = Place(circuit, new FunctionGenerator(Waveform.Sine, 50, 340.0), -520, 0);
        var logic = Place(circuit, new DcVoltageSource(5.0), -520, 320);

        // A switch in the control side rather than a clock, so the moment of the command is
        // whenever you happen to click it — which is the point. Fire it at a peak and the relay
        // still waits.
        var command = Place(circuit, new ToggleSwitch(closed: false), -240, 320);
        var series = Place(circuit, new Resistor(470), -40, 320);

        var relay = Place(circuit, new SolidStateRelay(), 200, 160);
        var lamp = Place(circuit, new Resistor(100), 200, -80);

        var gnd = Place(circuit, new Ground(), -520, 200);
        var gnd2 = Place(circuit, new Ground(), -520, 480);
        var gnd3 = Place(circuit, new Ground(), 440, 340);

        circuit.Connect(mains.Return, gnd.Pin);
        circuit.Connect(logic.Negative, gnd2.Pin);

        circuit.Connect(mains.Output, lamp.A);
        circuit.Connect(lamp.B, relay.LoadA);
        circuit.Connect(relay.LoadB, gnd3.Pin);

        circuit.Connect(logic.Positive, command.A);
        circuit.Connect(command.B, series.A);
        circuit.Connect(series.B, relay.ControlPositive);
        circuit.Connect(relay.ControlNegative, gnd2.Pin);

        vm.Scope.TimebasePerDivision = 5e-3;
        vm.Scope.VoltsPerDivision = 100.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(mains.Output, "Mains");
        vm.Scope.AddProbe(relay.LoadA, "Load");

        var lampCurrent = vm.Scope.AddProbe(relay.LoadA, "Lamp I");
        lampCurrent.Kind = Cirq.Core.Probing.ProbeKind.Current;

        vm.Simulation.SpeedFactor = 0.05;
    }

    /// <summary>
    /// One LM324 doing the four things a single-supply analog front end needs, which is exactly
    /// why the part is sold four to a package.
    /// </summary>
    public static void LoadQuadOpAmpChain(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Quad op-amp chain";

        var supply = Place(circuit, new DcVoltageSource(5.0), -760, 300);
        var signal = Place(circuit, new FunctionGenerator(Waveform.Sine, 1e3, 0.4), -760, -120);
        var quad = Place(circuit, new QuadOpAmp(), -200, 40);

        // Half the rail, made by a divider and then buffered. On one supply there is no ground in
        // the middle of the signal, so you have to make one — and it has to be buffered, because
        // a bare divider is a few kilohms and every stage hanging off it would load it.
        var top = Place(circuit, new Resistor(10e3), -520, -180);
        var bottom = Place(circuit, new Resistor(10e3), -520, -20);

        // Stage two: non-inverting, gain 1 + 10k/10k = 2.
        //
        // Two, and not more, because of what this part can reach. An LM324 on a single five volt
        // supply swings from about 0.02 V to about 3.5 V — it gets to the bottom rail and stops a
        // volt and a half short of the top. Centred on half the rail that leaves a usable ±1 V,
        // so a 0.4 V input can be doubled and no more. Raise the gain here to 4.9 and the top of
        // the wave flattens against that limit while the bottom carries on, which is worth doing
        // once to see.
        var gainTop = Place(circuit, new Resistor(10e3), 100, -220);
        var gainBottom = Place(circuit, new Resistor(10e3), 100, -60);

        // Stage three: inverting, gain -1, so it comes out upside down at the same size.
        var inputResistor = Place(circuit, new Resistor(22e3), 300, 60);
        var feedback = Place(circuit, new Resistor(22e3), 420, -80);

        var coupling = Place(circuit, new Capacitor(1e-6), -420, 120);

        var gnd = Place(circuit, new Ground(), -760, 460);
        var gnd2 = Place(circuit, new Ground(), -760, 40);
        var gnd3 = Place(circuit, new Ground(), -520, 120);
        var gnd4 = Place(circuit, new Ground(), -200, 260);
        var gnd5 = Place(circuit, new Ground(), 100, 100);

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(quad.PositiveSupply, supply.Positive);
        circuit.Connect(quad.NegativeSupply, gnd4.Pin);

        var (bias, biasMinus, biasOut) = quad.Channel(0);
        var (bufferPlus, bufferMinus, bufferOut) = quad.Channel(1);
        var (gainPlus, gainMinus, gainOut) = quad.Channel(2);
        var (invertPlus, invertMinus, invertOut) = quad.Channel(3);

        // Channel A: the half-rail reference, as a follower on the divider.
        circuit.Connect(top.A, supply.Positive);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, gnd3.Pin);
        circuit.Connect(bias, top.B);
        circuit.Connect(biasMinus, biasOut);

        // Channel B: the signal, capacitor-coupled and sat on top of that reference. The
        // capacitor is what lets a source referred to ground drive a stage that is not.
        circuit.Connect(signal.Return, gnd2.Pin);
        circuit.Connect(signal.Output, coupling.A);
        circuit.Connect(coupling.B, bufferPlus);

        // The input needs a DC path or the capacitor leaves it floating, and the reference is
        // where it belongs — that is the whole reason the first channel exists. Ten kilohms
        // against the one microfarad puts the coupling corner at 16 Hz, well under the signal,
        // and settles the bias in a few tens of milliseconds rather than a second. A bare divider
        // could not supply it; a buffered one does not notice.
        var leak = Place(circuit, new Resistor(10e3), -420, 260);
        circuit.Connect(leak.A, bufferPlus);
        circuit.Connect(leak.B, biasOut);
        circuit.Connect(bufferMinus, bufferOut);

        // Channel C: gain of 4.9, referred to the same half rail rather than to ground.
        circuit.Connect(gainPlus, bufferOut);
        circuit.Connect(gainTop.A, gainOut);
        circuit.Connect(gainTop.B, gainMinus);
        circuit.Connect(gainBottom.A, gainMinus);
        circuit.Connect(gainBottom.B, biasOut);

        // Channel D: inverting, unity, about the reference again.
        circuit.Connect(invertPlus, biasOut);
        circuit.Connect(inputResistor.A, gainOut);
        circuit.Connect(inputResistor.B, invertMinus);
        circuit.Connect(feedback.A, invertMinus);
        circuit.Connect(feedback.B, invertOut);

        // Keeps the ground symbol from being orphaned when the divider is the only thing on it.
        circuit.Connect(gnd5.Pin, gnd3.Pin);

        vm.Scope.TimebasePerDivision = 500e-6;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(biasOut, "Bias");
        vm.Scope.AddProbe(bufferOut, "Buffered");
        vm.Scope.AddProbe(gainOut, "x4.9");
        vm.Scope.AddProbe(invertOut, "Inverted");
    }

    /// <summary>
    /// A PPTC on a five volt rail, with a short to put across it. What it teaches is that it does
    /// not open — it heats, and the trickle it keeps passing is what holds it hot.
    /// </summary>
    public static void LoadResettableFuse(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Resettable fuse";

        var supply = Place(circuit, new DcVoltageSource(5.0), -420, 120);
        var fuse = Place(circuit, new ResettableFuse(0.5), -180, -120);

        var load = Place(circuit, new Resistor(22.0), 60, 40);

        // The fault, in parallel with the load. Close it and the pair is about two ohms, which is
        // five times what the fuse will hold.
        var fault = Place(circuit, new ToggleSwitch(closed: false), 280, 40);
        var short_ = Place(circuit, new Resistor(2.2), 280, 200);

        var gnd = Place(circuit, new Ground(), -420, 280);
        var gnd2 = Place(circuit, new Ground(), 60, 220);
        var gnd3 = Place(circuit, new Ground(), 280, 340);

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, fuse.A);
        circuit.Connect(fuse.B, load.A);
        circuit.Connect(load.B, gnd2.Pin);

        circuit.Connect(fuse.B, fault.A);
        circuit.Connect(fault.B, short_.A);
        circuit.Connect(short_.B, gnd3.Pin);

        // Seconds, not milliseconds. A PPTC's thermal time constant is the slowest thing in this
        // whole library, and watching it trip in real time is most of the lesson.
        vm.Scope.TimebasePerDivision = 0.5;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(fuse.B, "Downstream");

        var railCurrent = vm.Scope.AddProbe(fuse.A, "Rail I");
        railCurrent.Kind = Cirq.Core.Probing.ProbeKind.Current;
    }

    /// <summary>
    /// Two UARTs talking through a pair of MAX232s, which is what a serial cable between two
    /// boards actually contains.
    /// </summary>
    public static void LoadRs232Link(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "RS-232 link";

        var rail = Place(circuit, new DcVoltageSource(5.0), -820, 320);

        var terminal = Place(circuit, new SerialTerminal
        {
            BaudRate = 9600,
            Message = "hi\r\n",
            StartDelay = 500e-6,
            RepeatInterval = 5e-3,
        }, -560, -80);

        var near = Place(circuit, new Max232(), -180, 0);
        var far = Place(circuit, new Max232(), 300, 0);

        var device = Place(circuit, new SerialDevice
        {
            BaudRate = 9600,
            Greeting = "READY\r\n",
        }, 660, -80);

        var gnd = Place(circuit, new Ground(), -820, 480);
        var gnd2 = Place(circuit, new Ground(), -560, 140);
        var gnd3 = Place(circuit, new Ground(), -180, 220);
        var gnd4 = Place(circuit, new Ground(), 300, 220);
        var gnd5 = Place(circuit, new Ground(), 660, 140);

        circuit.Connect(rail.Negative, gnd.Pin);

        foreach (var (part, ground) in new[] { (near, gnd3), (far, gnd4) })
        {
            circuit.Connect(part.Vcc, rail.Positive);
            circuit.Connect(part.Gnd, ground.Pin);
        }

        circuit.Connect(terminal.Vcc, rail.Positive);
        circuit.Connect(terminal.Gnd, gnd2.Pin);
        circuit.Connect(device.Vcc, rail.Positive);
        circuit.Connect(device.Gnd, gnd5.Pin);

        // TTL in one side, RS-232 out the other, and the cable in the middle carries ±8.5 V.
        circuit.Connect(terminal.Transmit, near.DriverInputs[0]);
        circuit.Connect(near.DriverOutputs[0], far.ReceiverInputs[0]);
        circuit.Connect(far.ReceiverOutputs[0], device.Receive);

        // And back the other way, on the second channel of each package.
        circuit.Connect(device.Transmit, far.DriverInputs[1]);
        circuit.Connect(far.DriverOutputs[1], near.ReceiverInputs[1]);
        circuit.Connect(near.ReceiverOutputs[1], terminal.Receive);

        vm.Scope.TimebasePerDivision = 200e-6;
        vm.Scope.VoltsPerDivision = 5.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(terminal.Transmit, "TTL TX");
        vm.Scope.AddProbe(near.DriverOutputs[0], "RS-232 line");
        vm.Scope.AddProbe(far.ReceiverOutputs[0], "TTL RX");
        vm.Scope.AddProbe(near.PumpPositive, "V+");

        vm.Simulation.SpeedFactor = 0.01;
    }

    /// <summary>
    /// A transistor wired for the DC sweep rather than for a transient. There is nothing to watch
    /// on the scope here — the whole point is <b>Simulate &gt; DC Sweep</b>.
    /// </summary>
    public static void LoadCurveTracer(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Curve tracer";

        // The collector supply is what gets swept, so it starts at the top of the range rather
        // than at some midpoint nobody chose.
        var collector = Place(circuit, new DcVoltageSource(5.0), -400, 0);

        // And the base is driven by a current rather than a voltage. That is the whole trick of a
        // curve tracer: a transistor's collector current follows its base current almost exactly
        // and its base voltage barely at all, so stepping a current gives evenly spaced curves
        // where stepping a voltage would give a useless bunch of them crowded together.
        var baseDrive = Place(circuit, new DcCurrentSource(20e-6), -400, 300);

        var transistor = Place(circuit, new BipolarTransistor(), 0, 60);

        var gnd = Place(circuit, new Ground(), -400, 180);
        var gnd2 = Place(circuit, new Ground(), -400, 480);
        var gnd3 = Place(circuit, new Ground(), 0, 260);

        circuit.Connect(collector.Negative, gnd.Pin);
        circuit.Connect(collector.Positive, transistor.Collector);
        circuit.Connect(transistor.Emitter, gnd3.Pin);

        // Current out of the source's positive terminal and into the base.
        circuit.Connect(baseDrive.Positive, gnd2.Pin);
        circuit.Connect(baseDrive.Negative, transistor.Base);

        // The probe is the Y axis of the sweep. On the supply's own terminal, because that is the
        // branch the collector current flows through and a current probe reads a branch.
        var current = vm.Scope.AddProbe(collector.Positive, "Ic");
        current.Kind = Cirq.Core.Probing.ProbeKind.Current;

        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 1.0;
    }

    /// <summary>
    /// A comparator with hysteresis, fed a triangle wave, with the scope already in XY — so the
    /// loop draws itself.
    /// </summary>
    public static void LoadHysteresisLoop(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Hysteresis loop";

        var supply = Place(circuit, new DcVoltageSource(5.0), -620, 220);

        // A triangle rather than a sine, so the input sweeps up and back at a constant rate and
        // the two switching points sit at the same place on the way through either way.
        var ramp = Place(circuit, new FunctionGenerator(Waveform.Triangle, 200, 4.0)
        {
            DcOffset = 2.5,
        }, -620, -120);

        var comparator = Place(circuit, new Comparator(ComparatorModel.Lm311), -220, -40);

        // The divider sets where it switches; the feedback resistor from the output back to the
        // same node is what makes the two thresholds different.
        var upper = Place(circuit, new Resistor(10e3), -400, 160);
        var lower = Place(circuit, new Resistor(10e3), -400, 300);
        var feedback = Place(circuit, new Resistor(100e3), 40, 160);
        var pullup = Place(circuit, new Resistor(4.7e3), 120, -180);

        var gnd = Place(circuit, new Ground(), -620, 380);
        var gnd2 = Place(circuit, new Ground(), -620, 40);
        var gnd3 = Place(circuit, new Ground(), -400, 400);
        var gnd4 = Place(circuit, new Ground(), -220, 120);

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(ramp.Return, gnd2.Pin);

        circuit.Connect(comparator.PositiveSupply, supply.Positive);
        circuit.Connect(comparator.NegativeSupply, gnd4.Pin);

        circuit.Connect(ramp.Output, comparator.NonInverting);

        circuit.Connect(upper.A, supply.Positive);
        circuit.Connect(upper.B, comparator.Inverting);
        circuit.Connect(lower.A, comparator.Inverting);
        circuit.Connect(lower.B, gnd3.Pin);

        // Open collector, so it needs a pull-up before it can pull anything.
        circuit.Connect(pullup.A, supply.Positive);
        circuit.Connect(pullup.B, comparator.Output);

        // The feedback that makes it a Schmitt trigger: the output moves the reference.
        circuit.Connect(feedback.A, comparator.Output);
        circuit.Connect(feedback.B, comparator.Inverting);

        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 1.0;

        var input = vm.Scope.AddProbe(ramp.Output, "Input");
        vm.Scope.AddProbe(comparator.Output, "Output");

        // Already in XY, with the input along the bottom. Switch Layout back to Unified to see
        // the same thing against time — it is the same data, and the loop is not visible there.
        vm.Scope.XyHorizontal = input;
        vm.Scope.Layout = ScopeLayout.Xy;

        vm.Simulation.SpeedFactor = 0.05;
    }

    /// <summary>
    /// Four nominally equal resistors, probed across the middle. It reads nothing at all until
    /// the tolerances are taken into account.
    /// </summary>
    public static void LoadResistorBridge(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Resistor bridge";

        var supply = Place(circuit, new DcVoltageSource(10.0), -420, 120);

        // Ordinary five percent parts, which is what the analysis is about.
        var leftTop = Place(circuit, new Resistor(10e3) { Tolerance = 0.05 }, -160, -120);
        var leftBottom = Place(circuit, new Resistor(10e3) { Tolerance = 0.05 }, -160, 120);
        var rightTop = Place(circuit, new Resistor(10e3) { Tolerance = 0.05 }, 160, -120);
        var rightBottom = Place(circuit, new Resistor(10e3) { Tolerance = 0.05 }, 160, 120);

        var gnd = Place(circuit, new Ground(), -420, 280);
        var gnd2 = Place(circuit, new Ground(), 0, 280);

        circuit.Connect(supply.Negative, gnd.Pin);

        foreach (var (top, bottom) in new[] { (leftTop, leftBottom), (rightTop, rightBottom) })
        {
            circuit.Connect(supply.Positive, top.A);
            circuit.Connect(top.B, bottom.A);
            circuit.Connect(bottom.B, gnd2.Pin);
        }

        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 0.1;

        // Across the middle, differentially. Both midpoints sit at five volts and the difference
        // between them is the measurement — which against ground would be the fourth digit of a
        // number that is mostly the supply.
        var bridge = vm.Scope.AddProbe(leftTop.B, "Bridge");
        bridge.Kind = ProbeKind.Differential;
        bridge.ReferenceTerminal = rightTop.B;
    }

    /// <summary>
    /// A shunt at the top of a rail: millivolts across it, twenty-four volts either side, and the
    /// power it costs to measure the current that way.
    /// </summary>
    public static void LoadHighSideSensing(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "High-side sensing";

        var supply = Place(circuit, new DcVoltageSource(24.0), -460, 120);
        var shunt = Place(circuit, new Resistor(0.1), -180, -100);
        var load = Place(circuit, new Resistor(12.0), 140, 60);

        var gnd = Place(circuit, new Ground(), -460, 280);
        var gnd2 = Place(circuit, new Ground(), 140, 220);

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, shunt.A);
        circuit.Connect(shunt.B, load.A);
        circuit.Connect(load.B, gnd2.Pin);

        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 0.05;

        // Across the shunt, not against ground. Both of its ends are within a fraction of a
        // percent of twenty-four volts, and the whole measurement is the difference.
        var across = vm.Scope.AddProbe(shunt.A, "Across the shunt");
        across.Kind = ProbeKind.Differential;
        across.ReferenceTerminal = shunt.B;

        // And what the measurement costs. Taken from the shunt's other end, because a terminal
        // carries one probe: clicking a pin that already has one selects it rather than stacking
        // a second on top, which is right on the canvas and means an example has to put its
        // second measurement somewhere else.
        var burned = vm.Scope.AddProbe(shunt.B, "Shunt power");
        burned.Kind = ProbeKind.Power;
        burned.ReferenceTerminal = shunt.A;

        var delivered = vm.Scope.AddProbe(load.A, "Load power");
        delivered.Kind = ProbeKind.Power;
        delivered.ReferenceTerminal = load.B;
    }

    /// <summary>
    /// A diode held at a constant current, which makes it a thermometer. There is nothing to watch
    /// on the scope; the point is <b>Simulate &gt; DC Sweep</b> over temperature.
    /// </summary>
    public static void LoadDiodeThermometer(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Diode thermometer";

        // A current source rather than a resistor from a rail, and that is the whole design. A
        // diode's forward drop only tracks temperature cleanly at a fixed current; fed through a
        // resistor, the current rises as the drop falls and flatters the reading.
        var bias = Place(circuit, new DcCurrentSource(1e-3), -300, 40);
        var sensor = Place(circuit, new Diode(), 40, 40);

        var gnd = Place(circuit, new Ground(), -300, 220);
        var gnd2 = Place(circuit, new Ground(), 40, 220);

        circuit.Connect(bias.Positive, gnd.Pin);
        circuit.Connect(bias.Negative, sensor.Anode);
        circuit.Connect(sensor.Cathode, gnd2.Pin);

        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 0.2;
        vm.Scope.AddProbe(sensor.Anode, "Vf");
    }

    private static T Place<T>(Circuit circuit, T component, double x, double y) where T : CircuitComponent
    {
        component.X = x;
        component.Y = y;
        circuit.Add(component);
        return component;
    }
}
