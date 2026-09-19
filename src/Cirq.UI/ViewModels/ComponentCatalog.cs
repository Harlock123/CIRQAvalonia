using Cirq.Components.Bridges;
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
            new("Capacitor", "Trapezoidal / Backward-Euler companion model", () => new Capacitor(100e-9)),
            new("Inductor", "Branch-current formulation", () => new Inductor(1e-3)),
            new("Transformer", "Two magnetically coupled windings", () => new Transformer()),
            new("Potentiometer", "Three-terminal variable divider", () => new Potentiometer()),
        ]),

        new("Switches",
        [
            new("Switch (SPST)", "Latching on/off contact. Double-click on the canvas to flip it",
                () => new ToggleSwitch()),
            new("Push Button", "Momentary contact. Double-click to press and release",
                () => new PushButton()),
            new("Switch (SPDT)", "Changeover contact, common plus two throws",
                () => new SpdtSwitch()),
        ]),

        new("Sources",
        [
            new("Ground", "Explicit 0 V reference", () => new Ground()),
            new("DC Voltage", "Independent voltage source", () => new DcVoltageSource(5.0)),
            new("DC Current", "Independent current source", () => new DcCurrentSource(1e-3)),
            new("Function Generator", "Sine, square, triangle, sawtooth", () => new FunctionGenerator()),
        ]),

        new("Semiconductors",
        [
            new("Diode 1N4148", "Small-signal switching diode", () => new Diode(DiodeModel.D1N4148)),
            new("Diode 1N4001", "General purpose rectifier", () => new Diode(DiodeModel.D1N4001)),
            new("Schottky 1N5817", "Low forward drop, fast recovery", () => new Diode(DiodeModel.D1N5817)),
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
            new("74138", "3-to-8 decoder with three enables, active-low outputs", () => new Ic74138()),
            new("74151", "8-to-1 multiplexer with complementary outputs", () => new Ic74151()),
            new("74164", "8-bit serial-in parallel-out shift register", () => new Ic74164()),
            new("74165", "8-bit parallel-in serial-out shift register", () => new Ic74165()),
        ]),

        new("Digital I/O",
        [
            new("Logic Toggle", "Manually switched logic level", () => new LogicToggle()),
            new("Clock", "Free-running logic clock", () => new ClockSource(1e3)),
            new("ADC Bridge", "Analog in, logic out, with hysteresis", () => new AdcBridge()),
            new("DAC Bridge", "Logic in, configurable analog out", () => new DacBridge()),
        ]),
    ];

    /// <summary>Flat list of every palette entry.</summary>
    public static IEnumerable<PaletteItem> AllItems => Categories.SelectMany(c => c.Items);
}
