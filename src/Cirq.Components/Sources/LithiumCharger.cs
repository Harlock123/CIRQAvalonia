using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Sources;

/// <summary>Which part of the charge a TP4056 is in.</summary>
public enum ChargePhase
{
    /// <summary>Nothing to charge from, or nothing to charge.</summary>
    Idle,

    /// <summary>Pushing a fixed current in, which is most of the charge and most of the time.</summary>
    ConstantCurrent,

    /// <summary>Holding the cell at its float voltage while the current tails away.</summary>
    ConstantVoltage,

    /// <summary>The current has fallen far enough that it calls it done.</summary>
    Complete,
}

/// <summary>
/// A TP4056 lithium cell charger: the little red board on the end of every hobby project's USB
/// socket.
/// <para>
/// A lithium cell cannot simply be connected to a supply. Put five volts across a cell sitting at
/// three and the only thing deciding the current is the cell's own internal resistance — tens of
/// amps, briefly, and then a fire. Charging one is a <b>procedure</b>, and this part is that
/// procedure in a chip.
/// </para>
/// <para>
/// It has two phases. <b>Constant current</b> comes first: push a fixed current in, and let the
/// cell's voltage rise wherever it likes. That is most of the charge and nearly all of the time.
/// Once the cell reaches its float voltage — 4.2 V, and the number is not negotiable — it switches
/// to <b>constant voltage</b>: hold exactly 4.2 and let the current fall away as the cell fills.
/// When the current has dropped to about a tenth of what it started at, it is as full as it is
/// going to get and the charger stops.
/// </para>
/// <para>
/// The thing people get caught by is heat. This is a <b>linear</b> charger, so everything between
/// the input and the cell is thrown away inside the chip: charging at one amp from five volts into
/// a cell sitting at three and a half is one and a half watts in a part the size of a grain of
/// rice. That is why a board that works at 300 mA gets too hot to touch at a full amp, and it is
/// reported here rather than left to be discovered by smell.
/// </para>
/// </summary>
public sealed partial class LithiumCharger : CircuitComponent
{
    /// <summary>
    /// How sharply the charger backs off as the cell approaches its float voltage, in volts. Small
    /// enough to be a proper constant-current phase, wide enough that the solver has a gradient.
    /// </summary>
    private const double TaperWidth = 0.03;

    public LithiumCharger()
    {
        Input = new Terminal("in", "IN", TerminalType.Power, new Point(-40, -24));
        Battery = new Terminal("bat", "BAT", TerminalType.Passive, new Point(40, -24));
        Charging = new Terminal("chrg", "CHRG", TerminalType.Output, new Point(40, 10));
        Standby = new Terminal("stdby", "STDBY", TerminalType.Output, new Point(40, 30));
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(-40, 30));

        Terminals = [Input, Battery, Charging, Standby, Gnd];
    }

    /// <summary>Where the energy comes from. Five volts, usually from a USB socket.</summary>
    public Terminal Input { get; }

    /// <summary>The cell.</summary>
    public Terminal Battery { get; }

    /// <summary>Open drain, pulled low while charging. The red LED on the board.</summary>
    public Terminal Charging { get; }

    /// <summary>Open drain, pulled low when it has finished. The blue one.</summary>
    public Terminal Standby { get; }

    public Terminal Gnd { get; }

    /// <summary>
    /// The constant current, in amps. On a real board this is set by one resistor, and the boards
    /// ship with it at a full amp whether or not the cell you are charging wants that.
    /// </summary>
    [ObservableProperty]
    public partial double ChargeCurrent { get; set; } = 1.0;

    /// <summary>
    /// The voltage a full cell is held at. Four point two, and it is not a number to be creative
    /// with: a little over shortens the cell's life and a lot over is a fire.
    /// </summary>
    [ObservableProperty]
    public partial double FloatVoltage { get; set; } = 4.2;

    /// <summary>
    /// Fraction of the charge current at which it decides the cell is full. A tenth is what the
    /// datasheet uses.
    /// </summary>
    [ObservableProperty]
    public partial double TerminationFraction { get; set; } = 0.1;

    /// <summary>How much more than the cell the input has to be before anything happens, in volts.</summary>
    [ObservableProperty]
    public partial double Dropout { get; set; } = 0.1;

    /// <summary>Dissipation above which it is worth saying something, in watts.</summary>
    [ObservableProperty]
    public partial double ThermalWarningWatts { get; set; } = 1.0;

    /// <summary>Resistance of a status pin when it is pulling down, in ohms.</summary>
    [ObservableProperty]
    public partial double StatusOnResistance { get; set; } = 50.0;

    public override string ComponentType => "Li-Ion Charger";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => Phase switch
    {
        ChargePhase.ConstantCurrent => $"CC {ChargeCurrent * 1000:0} mA",
        ChargePhase.ConstantVoltage => $"CV {Current * 1000:0} mA",
        ChargePhase.Complete => "charged",
        _ => "idle",
    };

    public override bool IsNonlinear => true;

    /// <summary>Which part of the charge it is in.</summary>
    public ChargePhase Phase { get; private set; } = ChargePhase.Idle;

    /// <summary>Current going into the cell at the last solved point, in amps.</summary>
    public double Current { get; private set; }

    /// <summary>The cell's voltage at the last solved point.</summary>
    public double CellVoltage { get; private set; }

    /// <summary>Power being thrown away inside the chip, in watts.</summary>
    public double Dissipation { get; private set; }

    /// <summary>True once it has finished and latched off.</summary>
    private bool _complete;

    /// <summary>
    /// How hard it is pushing at a given cell voltage, and the slope of that.
    /// <para>
    /// Full current well below the float voltage, tapering to nothing as the cell reaches it.
    /// The taper is what the constant-voltage phase <i>is</i>: the charger is not choosing to
    /// reduce the current, it is holding a voltage and the cell is taking less.
    /// </para>
    /// </summary>
    private (double Current, double Slope) Demand(double cell, double headroom)
    {
        if (_complete || headroom <= 0) return (0, 0);

        var below = FloatVoltage - cell;

        if (below <= 0) return (0, 0);

        var exponent = Math.Exp(-below / TaperWidth);
        var current = ChargeCurrent * (1.0 - exponent);

        // The current falls as the cell rises, so this is a positive conductance seen from the
        // cell — the charger looks like a resistor to anything trying to push it about, which is
        // what keeps the solve well behaved.
        var slope = ChargeCurrent * exponent / TaperWidth;

        return (Math.Max(current, 0.0), slope);
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var input = system.Node(Input);
        var battery = system.Node(Battery);
        var gnd = system.Node(Gnd);

        var cell = system.IterationVoltageAcross(battery, gnd);
        var supply = system.IterationVoltageAcross(input, gnd);

        // Nothing happens until the input is meaningfully above the cell. A linear charger cannot
        // lift a voltage, only drop one.
        var headroom = supply - cell - Dropout;

        var (current, slope) = Demand(cell, headroom);

        // Current out of the input and into the cell, with the slope stamped as the conductance
        // it is so Newton has the derivative of the taper rather than guessing at it.
        system.StampNorton(input, battery, slope, current - (slope * (supply - cell)));

        // The two status pins, each an open drain that is either pulling down or not there.
        var on = 1.0 / Math.Max(StatusOnResistance, 1e-3);
        const double off = 1e-9;

        system.StampConductance(system.Node(Charging), gnd,
            Phase is ChargePhase.ConstantCurrent or ChargePhase.ConstantVoltage ? on : off);

        system.StampConductance(system.Node(Standby), gnd,
            Phase == ChargePhase.Complete ? on : off);
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        CellVoltage = system.NodeVoltage(Battery) - system.NodeVoltage(Gnd);

        var supply = system.NodeVoltage(Input) - system.NodeVoltage(Gnd);
        var headroom = supply - CellVoltage - Dropout;

        var (current, _) = Demand(CellVoltage, headroom);
        Current = current;

        // Everything the charger does not put into the cell it turns into heat, because there is
        // nothing else for it to do with it.
        Dissipation = Math.Max(supply - CellVoltage, 0.0) * Current;

        if (headroom <= 0)
        {
            Phase = ChargePhase.Idle;
            return;
        }

        // Termination latches: once it has decided the cell is full it stays off rather than
        // starting again the moment the voltage sags a little, which is what a real one does and
        // what stops it cycling a cell to death.
        if (!_complete && Current > 0 && Current < ChargeCurrent * TerminationFraction
            && CellVoltage > FloatVoltage - (TaperWidth * 4))
        {
            _complete = true;
        }

        Phase = _complete ? ChargePhase.Complete
            : Current >= ChargeCurrent * 0.95 ? ChargePhase.ConstantCurrent
            : Current > 0 ? ChargePhase.ConstantVoltage
            : ChargePhase.Idle;
    }

    /// <summary>What is worth knowing about how this charger is being used.</summary>
    public IReadOnlyList<string> Violations
    {
        get
        {
            List<string> found = [];

            if (Dissipation > ThermalWarningWatts)
            {
                found.Add($"throwing away {Dissipation:0.0} W as heat — this is a linear charger, " +
                          "so everything between the input and the cell is dissipated inside it. " +
                          "Lower the charge current or use a lower input voltage");
            }

            return found;
        }
    }

    public override void ResetState()
    {
        Phase = ChargePhase.Idle;
        Current = 0;
        CellVoltage = 0;
        Dissipation = 0;
        _complete = false;
    }
}
