using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Passive;

/// <summary>
/// An eight-way DIP switch: eight separate contacts in one package, with nothing joining them.
/// <para>
/// It is the part you set once and forget — an address, a baud rate, a configuration the firmware
/// reads at power-up. Each section is genuinely independent, with its own pair of pins, so there
/// is no common rail and wiring one expecting a shared side is the usual first mistake.
/// </para>
/// <para>
/// The sections are toggles in the properties panel rather than something you double-click on the
/// canvas: at this size the symbol has no room for eight separate hit targets, and setting them is
/// something you do deliberately rather than while a simulation runs.
/// </para>
/// </summary>
public partial class DipSwitch : MechanicalContact
{
    private const int Sections = 8;

    private readonly Terminal[] _a = new Terminal[Sections];
    private readonly Terminal[] _b = new Terminal[Sections];

    public DipSwitch()
    {
        var pins = new List<Terminal>();

        for (var i = 0; i < Sections; i++)
        {
            var y = -70 + (i * 20);
            _a[i] = new Terminal($"a{i + 1}", $"{i + 1}A", TerminalType.Passive, new Point(-40, y));
            _b[i] = new Terminal($"b{i + 1}", $"{i + 1}B", TerminalType.Passive, new Point(40, y));
            pins.Add(_a[i]);
            pins.Add(_b[i]);
        }

        Terminals = pins;
    }

    /// <summary>The two pins of one section, indexed from 0.</summary>
    public (Terminal A, Terminal B) Section(int index) => (_a[index], _b[index]);

    [ObservableProperty] public partial bool Position1 { get; set; }
    [ObservableProperty] public partial bool Position2 { get; set; }
    [ObservableProperty] public partial bool Position3 { get; set; }
    [ObservableProperty] public partial bool Position4 { get; set; }
    [ObservableProperty] public partial bool Position5 { get; set; }
    [ObservableProperty] public partial bool Position6 { get; set; }
    [ObservableProperty] public partial bool Position7 { get; set; }
    [ObservableProperty] public partial bool Position8 { get; set; }

    public override string ComponentType => "DIP Switch";

    public override string DesignatorPrefix => "SW";

    /// <summary>The settings as a row of ones and zeros, section one first.</summary>
    public override string ValueLabel => string.Concat(Enumerable.Range(0, Sections)
        .Select(i => IsClosed(i) ? '1' : '0'));

    /// <summary>Whether a given section is closed, indexed from 0.</summary>
    public bool IsClosed(int index) => index switch
    {
        0 => Position1, 1 => Position2, 2 => Position3, 3 => Position4,
        4 => Position5, 5 => Position6, 6 => Position7, _ => Position8,
    };

    /// <summary>The settings read as a binary number, section one the least significant bit.</summary>
    public int Value
    {
        get
        {
            var value = 0;
            for (var i = 0; i < Sections; i++)
                if (IsClosed(i)) value |= 1 << i;
            return value;
        }
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        for (var i = 0; i < Sections; i++) StampContact(system, _a[i], _b[i], IsClosed(i));
    }

    public override double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state)
    {
        for (var i = 0; i < Sections; i++)
        {
            if (!ReferenceEquals(terminal, _a[i]) && !ReferenceEquals(terminal, _b[i])) continue;

            var current = ContactCurrent(system, _a[i], _b[i], IsClosed(i));
            return ReferenceEquals(terminal, _b[i]) ? -current : current;
        }

        return 0;
    }

    partial void OnPosition1Changed(bool value) => NotifyValueChanged();
    partial void OnPosition2Changed(bool value) => NotifyValueChanged();
    partial void OnPosition3Changed(bool value) => NotifyValueChanged();
    partial void OnPosition4Changed(bool value) => NotifyValueChanged();
    partial void OnPosition5Changed(bool value) => NotifyValueChanged();
    partial void OnPosition6Changed(bool value) => NotifyValueChanged();
    partial void OnPosition7Changed(bool value) => NotifyValueChanged();
    partial void OnPosition8Changed(bool value) => NotifyValueChanged();
}
