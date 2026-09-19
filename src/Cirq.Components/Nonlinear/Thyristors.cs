using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Nonlinear;

/// <summary>
/// Shared behaviour for the thyristor family: devices that latch on and then stay on until the
/// current through them falls away, whatever the thing that triggered them does afterwards.
/// <para>
/// Latching is what makes these different from every other device in the library. A transistor
/// follows its input; a thyristor remembers. Once it fires, removing the gate drive does nothing —
/// it conducts until the main current drops below its holding current, which on AC means until the
/// next zero crossing. That is exactly why they are used for mains switching and phase control,
/// and why a triac dimmer works at all.
/// </para>
/// <para>
/// The latch is decided once per accepted time point rather than inside the Newton loop. Within a
/// step the device is simply on or off, which keeps the solve well behaved; a latch that could
/// flip mid-iteration would give the solver two answers to oscillate between.
/// </para>
/// </summary>
public abstract partial class LatchingDevice : CircuitComponent
{
    /// <summary>Which way current was last flowing, so the forward drop opposes it correctly.</summary>
    private double _conductingSign = 1.0;

    /// <summary>Resistance when conducting, in ohms.</summary>
    [ObservableProperty]
    public partial double OnResistance { get; set; } = 0.1;

    /// <summary>Resistance when blocking, in ohms.</summary>
    [ObservableProperty]
    public partial double OffResistance { get; set; } = 1e9;

    /// <summary>Voltage dropped across the device while it conducts, in volts.</summary>
    [ObservableProperty]
    public partial double ForwardDrop { get; set; } = 1.1;

    /// <summary>
    /// Current below which it stops conducting, in amps. This is the whole reason a triac
    /// controlling a mains load turns itself off at every zero crossing.
    /// </summary>
    [ObservableProperty]
    public partial double HoldingCurrent { get; set; } = 5e-3;

    /// <summary>True while the device is conducting.</summary>
    public bool IsLatched { get; protected set; }

    /// <summary>Current through the main terminals at the last solved point, in amps.</summary>
    public double MainCurrent { get; protected set; }

    /// <summary>Number of times it has fired, which is one per half cycle on a mains dimmer.</summary>
    public int Firings { get; protected set; }

    /// <summary>
    /// Stamps the main path. Conducting, it is a resistance in series with the forward drop, with
    /// the drop taken against whichever way the current was last going — held from the last
    /// accepted point so the device stays linear inside the solve.
    /// </summary>
    protected void StampMainPath(MnaSystem system, int from, int to)
    {
        if (!IsLatched)
        {
            system.StampConductance(from, to, 1.0 / Math.Max(OffResistance, 1.0));
            return;
        }

        var conductance = 1.0 / Math.Max(OnResistance, 1e-6);
        system.StampNorton(from, to, conductance, -_conductingSign * ForwardDrop * conductance);
    }

    /// <summary>Remembers the current direction, and reports whether the latch should drop out.</summary>
    protected bool UpdateConduction(double current)
    {
        MainCurrent = current;

        if (Math.Abs(current) > 1e-9) _conductingSign = Math.Sign(current);

        return IsLatched && Math.Abs(current) < HoldingCurrent;
    }

    protected void Fire()
    {
        if (IsLatched) return;

        IsLatched = true;
        Firings++;
        NotifyValueChanged();
    }

    protected void Release()
    {
        if (!IsLatched) return;

        IsLatched = false;
        NotifyValueChanged();
    }

    public override void ResetState()
    {
        IsLatched = false;
        MainCurrent = 0;
        Firings = 0;
        _conductingSign = 1.0;
    }
}

/// <summary>
/// A silicon controlled rectifier: a diode you switch on with a gate pulse and cannot switch off.
/// <para>
/// It blocks in both directions until the gate is driven, then conducts in the forward direction
/// only. The gate has no further say — the device conducts until the anode current falls below its
/// holding current. On DC that means it stays on until the supply is interrupted, which is why an
/// SCR makes a good crowbar and a poor lamp switch.
/// </para>
/// </summary>
public partial class SiliconControlledRectifier : LatchingDevice
{
    public SiliconControlledRectifier()
    {
        Anode = new Terminal("a", "A", TerminalType.Passive, new Point(-40, 0));
        Cathode = new Terminal("k", "K", TerminalType.Passive, new Point(40, 0));
        Gate = new Terminal("g", "G", TerminalType.Input, new Point(0, 40));

        Terminals = [Anode, Cathode, Gate];
    }

    public Terminal Anode { get; }

    public Terminal Cathode { get; }

    public Terminal Gate { get; }

    /// <summary>Gate current that fires it, in amps.</summary>
    [ObservableProperty]
    public partial double GateTriggerCurrent { get; set; } = 200e-6;

    /// <summary>Forward voltage at which it breaks over without any gate drive, in volts.</summary>
    [ObservableProperty]
    public partial double BreakoverVoltage { get; set; } = 400.0;

    /// <summary>Resistance from gate to cathode, which sets the gate drive it needs.</summary>
    [ObservableProperty]
    public partial double GateResistance { get; set; } = 100.0;

    public override string ComponentType => "SCR";

    public override string DesignatorPrefix => "Q";

    public override string ValueLabel => IsLatched ? "conducting" : "blocking";

    /// <summary>Gate current at the last solved point, in amps.</summary>
    public double GateCurrent { get; private set; }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        StampMainPath(system, system.Node(Anode), system.Node(Cathode));
        system.StampResistor(system.Node(Gate), system.Node(Cathode), Math.Max(GateResistance, 1e-3));
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var across = system.NodeVoltage(Anode) - system.NodeVoltage(Cathode);
        var current = IsLatched
            ? (across - (Math.Sign(across) * ForwardDrop)) / Math.Max(OnResistance, 1e-6)
            : across / Math.Max(OffResistance, 1.0);

        GateCurrent = (system.NodeVoltage(Gate) - system.NodeVoltage(Cathode))
                      / Math.Max(GateResistance, 1e-3);

        var shouldRelease = UpdateConduction(current);

        // Reverse voltage blocks regardless: an SCR is a rectifier first.
        if (across < 0 || shouldRelease)
        {
            Release();
            return;
        }

        if (across > 0 && (GateCurrent > GateTriggerCurrent || across > BreakoverVoltage)) Fire();
    }

    public override void ResetState()
    {
        base.ResetState();
        GateCurrent = 0;
    }
}

/// <summary>
/// A triac: two SCRs back to back in one package, so it latches and conducts in either direction.
/// <para>
/// That is what makes it the part in a lamp dimmer. Fire it part-way through each mains half cycle
/// and it conducts for the rest of that half, turning itself off at the zero crossing when the
/// current falls below its holding current — so the fraction of each half cycle it conducts for,
/// and with it the power in the lamp, is set by how late you fire it.
/// </para>
/// </summary>
public partial class Triac : LatchingDevice
{
    public Triac()
    {
        MainTerminal1 = new Terminal("mt1", "MT1", TerminalType.Passive, new Point(-40, 0));
        MainTerminal2 = new Terminal("mt2", "MT2", TerminalType.Passive, new Point(40, 0));
        Gate = new Terminal("g", "G", TerminalType.Input, new Point(0, 40));

        Terminals = [MainTerminal1, MainTerminal2, Gate];
    }

    public Terminal MainTerminal1 { get; }

    public Terminal MainTerminal2 { get; }

    public Terminal Gate { get; }

    /// <summary>Gate current that fires it, of either sign, in amps.</summary>
    [ObservableProperty]
    public partial double GateTriggerCurrent { get; set; } = 5e-3;

    /// <summary>Voltage at which it breaks over without gate drive, in volts.</summary>
    [ObservableProperty]
    public partial double BreakoverVoltage { get; set; } = 400.0;

    /// <summary>Resistance from gate to MT1, which sets the gate drive it needs.</summary>
    [ObservableProperty]
    public partial double GateResistance { get; set; } = 100.0;

    public override string ComponentType => "Triac";

    public override string DesignatorPrefix => "Q";

    public override string ValueLabel => IsLatched ? "conducting" : "blocking";

    /// <summary>Gate current at the last solved point, in amps.</summary>
    public double GateCurrent { get; private set; }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        StampMainPath(system, system.Node(MainTerminal1), system.Node(MainTerminal2));
        system.StampResistor(system.Node(Gate), system.Node(MainTerminal1), Math.Max(GateResistance, 1e-3));
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var across = system.NodeVoltage(MainTerminal1) - system.NodeVoltage(MainTerminal2);
        var current = IsLatched
            ? (across - (Math.Sign(across) * ForwardDrop)) / Math.Max(OnResistance, 1e-6)
            : across / Math.Max(OffResistance, 1.0);

        GateCurrent = (system.NodeVoltage(Gate) - system.NodeVoltage(MainTerminal1))
                      / Math.Max(GateResistance, 1e-3);

        if (UpdateConduction(current))
        {
            // Falling below the holding current is how a triac turns itself off at every zero
            // crossing — there is no other way to stop it.
            Release();
            return;
        }

        // Either polarity of gate drive fires it, and in either polarity of main voltage.
        if (Math.Abs(GateCurrent) > GateTriggerCurrent || Math.Abs(across) > BreakoverVoltage) Fire();
    }

    public override void ResetState()
    {
        base.ResetState();
        GateCurrent = 0;
    }
}

/// <summary>
/// A diac: a two-terminal device that blocks until the voltage across it reaches its breakover,
/// then conducts in whichever direction it was pushed.
/// <para>
/// It has no gate, which is the point — it is the thing that fires a triac. An RC network charges
/// towards the mains, the diac breaks over when it reaches about thirty volts and dumps the
/// capacitor into the triac gate, and moving the RC's time constant moves the firing angle. That
/// is a lamp dimmer in three components.
/// </para>
/// </summary>
public partial class Diac : LatchingDevice
{
    public Diac()
    {
        A = new Terminal("a", "A1", TerminalType.Passive, new Point(-30, 0));
        B = new Terminal("b", "A2", TerminalType.Passive, new Point(30, 0));

        Terminals = [A, B];
        ForwardDrop = 5.0;          // a diac drops rather more than a triac once it fires
        HoldingCurrent = 100e-6;
    }

    public Terminal A { get; }

    public Terminal B { get; }

    /// <summary>Voltage at which it breaks over, in volts. Around 32 V for the usual part.</summary>
    [ObservableProperty]
    public partial double BreakoverVoltage { get; set; } = 32.0;

    public override string ComponentType => "Diac";

    public override string DesignatorPrefix => "D";

    public override string ValueLabel => IsLatched ? "conducting" : $"{BreakoverVoltage:0} V";

    public override void StampMatrix(MnaSystem system, SimulationState state) =>
        StampMainPath(system, system.Node(A), system.Node(B));

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var across = system.NodeVoltage(A) - system.NodeVoltage(B);
        var current = IsLatched
            ? (across - (Math.Sign(across) * ForwardDrop)) / Math.Max(OnResistance, 1e-6)
            : across / Math.Max(OffResistance, 1.0);

        if (UpdateConduction(current))
        {
            Release();
            return;
        }

        if (Math.Abs(across) > BreakoverVoltage) Fire();
    }
}
