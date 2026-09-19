using Cirq.Components.Nonlinear;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Sources;

/// <summary>
/// A photovoltaic panel, modelled as what a solar cell electrically is: a current source in
/// parallel with the diode it is made of.
/// <para>
/// That single fact explains everything awkward about them. Light makes current, not voltage, so
/// a cell in the dark is just a diode and a cell in the sun is a diode with current forced through
/// it backwards. The voltage you get is whatever that current develops across the diode — which
/// means it barely moves with brightness, while the current is almost exactly proportional to it.
/// </para>
/// <para>
/// The consequence is the knee. Draw less than the light is making and the voltage holds up; draw
/// more and it collapses, because there is no more current to be had at any voltage. A panel is
/// not a battery with a lower capacity: it has a maximum power point part way down that knee, and
/// loading it either side of that gives you less.
/// </para>
/// </summary>
public partial class SolarCell : TwoTerminalComponent, IInteractiveComponent, ICurrentReporting
{
    private double _previousJunctionVoltage;
    private bool _limitedThisIteration;

    public SolarCell() : base("+", "-")
    {
    }

    /// <summary>Cells in series. Six tenths of a volt each, so six of them make a 3.6 V panel.</summary>
    [ObservableProperty]
    public partial int CellCount { get; set; } = 6;

    /// <summary>Short-circuit current in full sun, in amps.</summary>
    [ObservableProperty]
    public partial double ShortCircuitCurrent { get; set; } = 150e-3;

    /// <summary>How brightly lit it is, from nought in the dark to one in full sun.</summary>
    [ObservableProperty]
    public partial double Illumination { get; set; } = 1.0;

    /// <summary>Illumination it drops to when you shade it by double-clicking.</summary>
    [ObservableProperty]
    public partial double ShadedIllumination { get; set; } = 0.15;

    /// <summary>Series resistance of the cell and its interconnects, in ohms.</summary>
    [ObservableProperty]
    public partial double SeriesResistance { get; set; } = 0.6;

    /// <summary>Shunt resistance across the junction, in ohms. Leakage round the edge of a cell.</summary>
    [ObservableProperty]
    public partial double ShuntResistance { get; set; } = 400.0;

    /// <summary>Ideality of the junction, which sets how sharp the knee is.</summary>
    [ObservableProperty]
    public partial double Ideality { get; set; } = 1.4;

    public override string ComponentType => "Solar Cell";

    public override string DesignatorPrefix => "PV";

    public override string ValueLabel => IsLit
        ? $"{SiPrefix.Format(Power, "W")}"
        : "dark";

    public override bool IsNonlinear => true;

    /// <summary>One internal node: the junction, behind the series resistance.</summary>
    public override int InternalNodeCount => 1;

    /// <summary>Current out of the + pin at the last solved point, in amps.</summary>
    public double OutputCurrent { get; private set; }

    /// <summary>Terminal voltage at the last solved point.</summary>
    public double Voltage { get; private set; }

    /// <summary>Power it is delivering, in watts.</summary>
    public double Power => Math.Max(Voltage * OutputCurrent, 0.0);

    /// <summary>True while there is enough light on it to do anything.</summary>
    public bool IsLit => Illumination > 0.02;

    public string InteractionHint => Illumination > ShadedIllumination ? "Shade it" : "Put it in the sun";

    public void Interact() =>
        Illumination = Illumination > ShadedIllumination ? ShadedIllumination : 1.0;

    /// <summary>The current the light is making, before the diode takes its share.</summary>
    public double PhotoCurrent => Math.Clamp(Illumination, 0.0, 2.0) * ShortCircuitCurrent;

    /// <summary>
    /// Saturation current of the junction, chosen so the open-circuit voltage lands near six
    /// tenths of a volt per cell in full sun — which is the number panels are actually sold by.
    /// </summary>
    private double SaturationCurrent => ShortCircuitCurrent / Math.Exp(0.6 / (Ideality * 0.02585));

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var plus = system.Node(A);
        var minus = system.Node(B);
        var junction = system.InternalNode(this);

        _limitedThisIteration = false;

        // The series resistance sits between the junction and the terminal, which is what rounds
        // off the top of the curve and why a shaded panel's voltage sags under load.
        system.StampResistor(junction, plus, Math.Max(SeriesResistance, 1e-6));
        system.StampResistor(junction, minus, Math.Max(ShuntResistance, 1.0));

        // Light makes current. Everything else about a cell follows from that.
        system.StampCurrentSource(minus, junction, PhotoCurrent);

        // ...and the cell is a diode, which is what turns that current into a voltage.
        var cells = Math.Max(CellCount, 1);
        var vt = state.ThermalVoltage * Math.Max(Ideality, 0.1) * cells;

        var raw = system.IterationVoltageAcross(junction, minus);
        var limited = Junction.Limit(raw, _previousJunctionVoltage, vt,
            Junction.CriticalVoltage(SaturationCurrent, vt));

        if (Math.Abs(limited - raw) > 1e-12) _limitedThisIteration = true;
        _previousJunctionVoltage = limited;

        var (current, conductance) = Junction.Evaluate(limited, SaturationCurrent, vt);

        system.StampNorton(junction, minus, conductance, current - (conductance * limited));
    }

    public override bool HasConverged(MnaSystem system, SimulationState state) => !_limitedThisIteration;

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        Voltage = system.NodeVoltage(A) - system.NodeVoltage(B);

        var junction = system.NodeVoltage(system.InternalNode(this)) - system.NodeVoltage(B);
        OutputCurrent = (junction - Voltage) / Math.Max(SeriesResistance, 1e-6);

        NotifyValueChanged();
    }

    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state) =>
        CurrentIntoPin(terminal, -OutputCurrent);

    public override void ResetState()
    {
        _previousJunctionVoltage = 0;
        _limitedThisIteration = false;
        OutputCurrent = 0;
        Voltage = 0;
    }

    partial void OnIlluminationChanged(double value) => NotifyValueChanged();
}
