using Cirq.Components.Electromechanical;
using Cirq.Components.Boards;
using Cirq.Components.Bridges;
using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.UI.ViewModels;

/// <summary>A single entry in the component palette.</summary>
/// <param name="Name">Label shown in the palette.</param>
/// <param name="Description">Tooltip text.</param>
/// <param name="Create">Factory producing a fresh instance to place on the canvas.</param>
public sealed record PaletteItem(string Name, string Description, Func<CircuitComponent> Create);

/// <summary>A named group of palette entries.</summary>
public sealed record PaletteCategory(string Name, IReadOnlyList<PaletteItem> Items);

/// <summary>
/// A palette category together with its expanded state. The state lives here rather than on the
/// shared <see cref="PaletteCategory"/> records so two windows do not fight over it.
/// </summary>
public sealed partial class PaletteCategoryViewModel : ObservableObject
{
    public PaletteCategoryViewModel(PaletteCategory category, bool isExpanded)
    {
        Name = category.Name;
        Items = category.Items;
        IsExpanded = isExpanded;
    }

    public string Name { get; }

    public IReadOnlyList<PaletteItem> Items { get; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>
    /// Disclosure triangle for the group header. Kept on the view model rather than left to the
    /// Expander's own chevron, because that one is laid out after the header content and so sits
    /// at a different place on every row.
    /// </summary>
    public string Chevron => IsExpanded ? "▾" : "▸";

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(Chevron));

    /// <summary>Count shown beside the header so a collapsed group still says how much is inside.</summary>
    public int Count => Items.Count;
}

/// <summary>Every component the editor can place, grouped for the palette sidebar.</summary>
public static class ComponentCatalog
{
    public static IReadOnlyList<PaletteCategory> Categories { get; } =
    [
        new("Passive",
        [
            new("Resistor", "Ideal linear resistor", () => new Resistor(1e3)),
            new("Electrolytic Cap", "Polarised, with real ESR — warns if reversed or over its rating",
                () => new ElectrolyticCapacitor(100e-6)),
            new("Capacitor", "Trapezoidal / Backward-Euler companion model", () => new Capacitor(100e-9)),
            new("Inductor", "Branch-current formulation, with an optional saturation current",
                () => new Inductor(1e-3)),
            new("Transformer", "Two magnetically coupled windings", () => new Transformer()),
            new("Transformer (CT)", "Centre-tapped secondary — full-wave rectification, or a split supply",
                () => new CentreTappedTransformer()),
            new("Crystal", "Quartz resonator — the frequency comes from the part, not the circuit",
                () => new Crystal()),
            new("Potentiometer", "Three-terminal variable divider", () => new Potentiometer()),
        ]),

        new("Switches",
        [
            new("Switch (SPST)", "Latching on/off contact. Double-click on the canvas to flip it",
                () => new ToggleSwitch()),
            new("Push Button", "Momentary contact. Double-click to press and release",
                () => new PushButton()),
            new("DIP Switch (8)", "Eight independent contacts — set them in the properties panel",
                () => new DipSwitch()),
            new("Switch (SPDT)", "Changeover contact, common plus two throws",
                () => new SpdtSwitch()),
        ]),

        new("Sources",
        [
            new("Ground", "Explicit 0 V reference", () => new Ground()),
            new("DC Voltage", "Independent voltage source", () => new DcVoltageSource(5.0)),
            new("DC Current", "Independent current source", () => new DcCurrentSource(1e-3)),
            new("Battery", "A cell that sags under load and runs down — 9 V alkaline by default",
                () => new Battery()),
            new("Solar Cell", "A photovoltaic panel with its knee — double-click to shade it",
                () => new SolarCell()),
            new("Function Generator", "Sine, square, triangle, sawtooth", () => new FunctionGenerator()),
        ]),

        new("Semiconductors",
        [
            new("Diode 1N4148", "Small-signal switching diode", () => new Diode(DiodeModel.D1N4148)),
            new("Diode 1N4001", "General purpose rectifier", () => new Diode(DiodeModel.D1N4001)),
            new("SCR", "Latches on from a gate pulse and stays on until the current falls away",
                () => new SiliconControlledRectifier()),
            new("Triac", "Latches in either direction — the part in a lamp dimmer",
                () => new Triac()),
            new("Diac", "Two-terminal breakover device, the usual thing that fires a triac",
                () => new Diac()),
            new("Bridge Rectifier", "Four 1N4001 diodes in a package, full-wave",
                () => new BridgeRectifier()),
            new("Schottky 1N5817", "Low forward drop, fast recovery", () => new Diode(DiodeModel.D1N5817)),
            new("TVS Diode", "Bidirectional transient suppressor for a supply or signal line",
                () => new TransientSuppressor()),
            new("Varistor (MOV)", "Mains surge absorber — soft knee, and it wears out",
                () => new Varistor()),
            new("Zener 5.1V", "Diode with modelled reverse breakdown", () => new Diode(DiodeModel.Zener(5.1))),
            new("Zener 12V", "12 V shunt regulator diode", () => new Diode(DiodeModel.Zener(12.0))),
        ]),

        new("Transistors",
        [
            new("NPN 2N3904", "Bipolar, Ebers-Moll transport model", () => new BipolarTransistor(BjtModel.N2N3904)),
            new("PNP 2N3906", "Complementary bipolar for high-side switching",
                () => new BipolarTransistor(BjtModel.P2N3906)),
            new("NPN BC547", "Higher gain small-signal bipolar", () => new BipolarTransistor(BjtModel.Bc547)),
            new("NPN TIP31C", "Power bipolar for driving real loads", () => new BipolarTransistor(BjtModel.Tip31C)),
            new("N-MOSFET 2N7000", "Small-signal enhancement MOSFET", () => new Mosfet(MosfetModel.N2N7000)),
            new("N-MOSFET IRLZ44N", "Logic-level power MOSFET", () => new Mosfet(MosfetModel.IrlZ44N)),
            new("N-JFET 2N3819", "Depletion device — conducts with no gate drive",
                () => new JunctionFet(JfetModel.J2N3819)),
            new("N-JFET J201", "Low pinch-off, for small-signal work",
                () => new JunctionFet(JfetModel.J201)),
            new("P-JFET 2N5460", "P-channel depletion device", () => new JunctionFet(JfetModel.J2N5460)),
            new("P-MOSFET IRF9540", "Power MOSFET for high-side switching", () => new Mosfet(MosfetModel.Irf9540)),
        ]),

        new("LEDs & Displays",
        [
            new("LED Red", "Around 1.8 V forward drop", () => Led.OfColour("Red")),
            new("LED Amber", "Around 1.9 V forward drop", () => Led.OfColour("Amber")),
            new("LED Yellow", "Around 1.95 V forward drop", () => Led.OfColour("Yellow")),
            new("LED Green", "Around 2.0 V forward drop", () => Led.OfColour("Green")),
            new("LED Blue", "Around 3.0 V forward drop", () => Led.OfColour("Blue")),
            new("LED White", "Blue die plus phosphor, around 3.1 V", () => Led.OfColour("White")),
            new("Character LCD", "HD44780 16x2 — the display on the front of everything",
                () => new CharacterLcd()),
            new("7-Segment (CA)", "Common-anode display, driven by pulling segments low",
                () => new SevenSegmentDisplay(commonAnode: true)),
            new("7-Segment (CC)", "Common-cathode display, driven by pulling segments high",
                () => new SevenSegmentDisplay(commonAnode: false)),
        ]),

        new("Power",
        [
            new("Regulator 7805", "Fixed 5 V linear regulator", () => new VoltageRegulator(RegulatorModel.Lm7805)),
            new("Regulator 7809", "Fixed 9 V linear regulator", () => new VoltageRegulator(RegulatorModel.Lm7809)),
            new("Regulator 7812", "Fixed 12 V linear regulator", () => new VoltageRegulator(RegulatorModel.Lm7812)),
            new("Regulator 7905", "Fixed -5 V linear regulator", () => new VoltageRegulator(RegulatorModel.Lm7905)),
            new("Regulator LM317", "Adjustable, 1.25 V between OUT and ADJ",
                () => new VoltageRegulator(RegulatorModel.Lm317)),
            new("MC34063", "Switching controller — you build the topology around it",
                () => new SwitchingRegulator()),
            new("ICL7660", "Charge pump — a negative rail from a positive one, no inductor",
                () => new ChargePump()),
            new("TL431 Reference", "Programmable 2.5 V shunt — the adjustable half of a feedback loop",
                () => new ShuntReference()),
            new("Regulator LD1117", "Low-dropout 3.3 V regulator", () => new VoltageRegulator(RegulatorModel.Ld1117)),
        ]),

        new("Analog ICs",
        [
            new("LM741", "Op-amp macromodel with slew and rail limits", () => new OpAmp741()),
            new("TL081", "JFET input op-amp, 3 MHz and 13 V/us",
                () => new OperationalAmplifier(OpAmpModel.Tl081)),
            new("LM358", "Single-supply op-amp, output swings near the negative rail",
                () => new OperationalAmplifier(OpAmpModel.Lm358)),
            new("NE555", "Timer with comparators, latch and discharge", () => new Ne555()),
            new("LM311", "Comparator with an open-collector output", () => new Comparator(ComparatorModel.Lm311)),
            new("LM393", "Single-supply comparator, open collector",
                () => new Comparator(ComparatorModel.Lm393)),
            new("TLV3501", "Fast push-pull comparator, no pull-up needed",
                () => new Comparator(ComparatorModel.Tlv3501)),
            new("LM386", "Audio power amp — single supply, gain of 20, drives a speaker",
                () => new Lm386()),
            new("INA126", "Instrumentation amp for a small difference on a large common voltage",
                () => new Ina126()),
            new("LM339", "Quad comparator. Outputs tie together into a wired-AND",
                () => new QuadComparator()),
        ]),

        new("Logic Gates",
        [
            new("AND", "Two-input AND", () => new LogicGate(GateFunction.And)),
            new("OR", "Two-input OR", () => new LogicGate(GateFunction.Or)),
            new("NOT", "Inverter", () => new LogicGate(GateFunction.Not)),
            new("NAND", "Two-input NAND", () => new LogicGate(GateFunction.Nand)),
            new("NOR", "Two-input NOR", () => new LogicGate(GateFunction.Nor)),
            new("XOR", "Two-input XOR", () => new LogicGate(GateFunction.Xor)),
            new("XNOR", "Two-input XNOR", () => new LogicGate(GateFunction.Xnor)),
        ]),

        new("74xx Series",
        [
            new("7400", "Quad 2-input NAND", () => new Ic7400()),
            new("7402", "Quad 2-input NOR. Outputs come first on each gate", () => new Ic7402()),
            new("7404", "Hex inverter", () => new Ic7404()),
            new("7408", "Quad 2-input AND", () => new Ic7408()),
            new("7410", "Triple 3-input NAND", () => new Ic7410()),
            new("7420", "Dual 4-input NAND", () => new Ic7420()),
            new("7432", "Quad 2-input OR", () => new Ic7432()),
            new("7447", "BCD to seven-segment decoder, open-collector outputs", () => new Ic7447()),
            new("7474", "Dual D flip-flop, preset and clear", () => new Ic7474()),
            new("7476", "Dual JK flip-flop, falling-edge triggered", () => new Ic7476()),
            new("7486", "Quad 2-input exclusive-OR", () => new Ic7486()),
            new("7490", "Decade / BCD counter", () => new Ic7490()),
            new("74595", "8-bit shift register with an output latch — three pins become eight",
                () => new Ic74595()),
            new("74HC14", "Hex Schmitt inverter — hysteresis for slow edges and oscillators",
                () => new Ic74hc14()),
            new("74138", "3-to-8 decoder with three enables, active-low outputs", () => new Ic74138()),
            new("74151", "8-to-1 multiplexer with complementary outputs", () => new Ic74151()),
            new("74164", "8-bit serial-in parallel-out shift register", () => new Ic74164()),
            new("74165", "8-bit parallel-in serial-out shift register", () => new Ic74165()),
        ]),

        new("40xx Series",
        [
            new("4001", "Quad 2-input NOR", () => new Ic4001()),
            new("4011", "Quad 2-input NAND — the workhorse of the family", () => new Ic4011()),
            new("4013", "Dual D flip-flop. Set and reset are active high, unlike a 7474's",
                () => new Ic4013()),
            new("4017", "Decade counter with ten decoded outputs — the LED chaser part",
                () => new Ic4017()),
            new("4040", "12-stage binary ripple counter, divides down to 4096", () => new Ic4040()),
            new("4051", "8-channel analog multiplexer / demultiplexer", () => new Ic4051()),
            new("4060", "14-stage counter with an on-chip oscillator — a timer in three parts",
                () => new Ic4060()),
            new("4066", "Quad bilateral analog switch", () => new Ic4066()),
            new("4069", "Hex inverter", () => new Ic4069()),
            new("4070", "Quad 2-input exclusive-OR", () => new Ic4070()),
            new("4071", "Quad 2-input OR", () => new Ic4071()),
            new("4081", "Quad 2-input AND", () => new Ic4081()),
            new("4093", "Quad NAND Schmitt trigger — one gate, a resistor and a cap make an oscillator",
                () => new Ic4093()),
            new("4511", "BCD to 7-segment latch and driver for a common-cathode display",
                () => new Ic4511()),
        ]),

        new("Buses",
        [
            new("I2C Master", "Plays a written list of I2C transactions onto SDA and SCL",
                () => new I2cMaster()),
            new("I2C EEPROM", "24LC256 — writes and reads back over two wires",
                () => new I2cEeprom()),
            new("I2C Expander", "PCF8574 — eight pins from two wires, the LCD backpack chip",
                () => new Pcf8574()),
            new("I2C Clock", "DS1307 real-time clock — BCD registers that move on their own",
                () => new Ds1307()),
            new("SPI Master", "Plays a written list of SPI transfers", () => new SpiMaster()),
        ]),

        new("Digital I/O",
        [
            new("Logic Toggle", "Manually switched logic level", () => new LogicToggle()),
            new("Clock", "Free-running logic clock", () => new ClockSource(1e3)),
            new("Rotary Encoder", "Quadrature contacts, bounce and all — double-click to turn it",
                () => new RotaryEncoder()),
            new("Oscillator", "Crystal oscillator can — a square wave, a supply and an enable",
                () => new OscillatorModule()),
            new("ADC Bridge", "Analog in, logic out, with hysteresis", () => new AdcBridge()),
            new("DAC Bridge", "Logic in, configurable analog out", () => new DacBridge()),
        ]),

        new("Sensors & Actuators",
        [
            new("DC Motor", "Armature, back-EMF and rotor inertia — warns if it is left stalled",
                () => new DcMotor()),
            new("LDR", "Light-dependent resistor. Double-click it to cover and uncover it",
                () => new LightDependentResistor()),
            new("Thermistor (NTC)", "10k B3950 bead. Double-click it to warm and cool it",
                () => new Thermistor(ThermistorKind.Ntc)),
            new("Thermistor (PTC)", "Resistance rises with temperature",
                () => new Thermistor(ThermistorKind.Ptc)),
            new("Piezo Buzzer", "Passive element — silent unless the drive alternates",
                () => new Buzzer(BuzzerKind.Passive)),
            new("Thermocouple", "Type K junction — microvolts per degree, and only ever a difference",
                () => new Thermocouple()),
            new("Load Cell", "Strain-gauge bridge. It needs exciting before it says anything",
                () => new LoadCell()),
            new("Microphone", "Electret capsule — needs a bias resistor to do anything",
                () => new Microphone()),
            new("Servo", "Hobby RC servo — the angle comes from the pulse width",
                () => new Servo()),
            new("Stepper Motor", "Four-phase unipolar, the ULN2003's usual passenger",
                () => new StepperMotor()),
            new("Ultrasonic Ranger", "HC-SR04 — the distance is in the width of the echo pulse",
                () => new UltrasonicRanger()),
            new("Speaker", "Moving-coil voice coil, and it reports the power it is taking",
                () => new Speaker()),
            new("Active Buzzer", "Has its own oscillator, so DC makes it sound",
                () => new Buzzer(BuzzerKind.Active)),
        ]),

        new("Switching & Isolation",
        [
            new("Relay (SPDT)", "Coil and changeover contact — warns if the coil has no flyback diode",
                () => new Relay()),
            new("Fuse 1A", "Opens on its melting integral, not on instantaneous current",
                () => new Fuse(1.0)),
            new("Fuse 500mA", "Half-amp quick-blow", () => new Fuse(0.5)),
            new("ULN2003", "Seven Darlington sinks — the way logic drives relays and steppers",
                () => new Uln2003()),
            new("Optocoupler PC817", "LED and phototransistor, galvanically isolated",
                () => new Optocoupler(OptocouplerModel.Pc817)),
            new("Optocoupler 4N35", "Isolated, with a higher saturation knee",
                () => new Optocoupler(OptocouplerModel.FourN35)),
        ]),

        new("Dev Boards",
        [
            new("Raspberry Pi", "40-pin header, 3.3V logic — not 5V tolerant",
                () => new RaspberryPiBoard()),
            new("Arduino Uno R3", "14 digital, 6 analog, 5V logic", () => new ArduinoUnoBoard()),
            new("Arduino Nano", "14 digital, 8 analog, 5V logic", () => new ArduinoNanoBoard()),
            new("Arduino Mega 2560", "54 digital, 16 analog, 5V logic", () => new ArduinoMegaBoard()),
        ]),
    ];

    /// <summary>Flat list of every palette entry.</summary>
    public static IEnumerable<PaletteItem> AllItems => Categories.SelectMany(c => c.Items);
}
