using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;
using Cirq.Core.Probing;

namespace Cirq.Components.Passive;

/// <summary>
/// Shared contact behaviour. A closed contact is a small resistance rather than a true short, and
/// an open one is a large resistance rather than a true break, which keeps the matrix
/// well-conditioned without changing anything a user would notice.
/// </summary>
public abstract partial class MechanicalContact : CircuitComponent, ICurrentReporting
{
    /// <summary>Resistance of a closed contact, in ohms.</summary>
    [ObservableProperty]
    public partial double ClosedResistance { get; set; } = 0.02;

    /// <summary>Resistance of an open contact, in ohms.</summary>
    [ObservableProperty]
    public partial double OpenResistance { get; set; } = 1e9;

    /// <summary>Current through one contact, from <paramref name="a"/> to <paramref name="b"/>.</summary>
    protected double ContactCurrent(MnaSystem system, Terminal a, Terminal b, bool closed) =>
        (system.NodeVoltage(a) - system.NodeVoltage(b))
        / Math.Max(closed ? ClosedResistance : OpenResistance, 1e-9);

    /// <summary>
    /// Abstract rather than a default of zero: a contact that silently reported no current would
    /// be worse than one that refused to compile.
    /// </summary>
    public abstract double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state);

    protected void StampContact(MnaSystem system, Terminal a, Terminal b, bool closed) =>
        system.StampConductance(
            system.Node(a), system.Node(b),
            1.0 / Math.Max(closed ? ClosedResistance : OpenResistance, 1e-9));
}

/// <summary>Single-pole single-throw switch: a latching on/off contact.</summary>
public partial class ToggleSwitch : MechanicalContact, IInteractiveComponent
{
    public ToggleSwitch(bool closed = false)
    {
        IsClosed = closed;

        A = new Terminal("a", "A", TerminalType.Passive, new Point(-30, 0));
        B = new Terminal("b", "B", TerminalType.Passive, new Point(30, 0));
        Terminals = [A, B];
    }

    public Terminal A { get; }
    public Terminal B { get; }

    [ObservableProperty]
    public partial bool IsClosed { get; set; }

    public override string ComponentType => "Switch (SPST)";

    public override string DesignatorPrefix => "SW";

    public override string ValueLabel => IsClosed ? "closed" : "open";

    public string InteractionHint => IsClosed ? "Open the switch" : "Close the switch";

    public void Interact() => IsClosed = !IsClosed;

    public override void StampMatrix(MnaSystem system, SimulationState state) =>
        StampContact(system, A, B, IsClosed);

    public override double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state)
    {
        var current = ContactCurrent(system, A, B, IsClosed);
        return ReferenceEquals(terminal, B) ? -current : current;
    }

    partial void OnIsClosedChanged(bool value) => NotifyValueChanged();
}

/// <summary>
/// Momentary push button. It latches in the simulator because there is no way to hold a mouse
/// button down while time advances, so operating it toggles between pressed and released.
/// </summary>
public partial class PushButton : MechanicalContact, IInteractiveComponent
{
    public PushButton(bool normallyOpen = true)
    {
        IsNormallyOpen = normallyOpen;

        A = new Terminal("a", "A", TerminalType.Passive, new Point(-30, 0));
        B = new Terminal("b", "B", TerminalType.Passive, new Point(30, 0));
        Terminals = [A, B];
    }

    public Terminal A { get; }
    public Terminal B { get; }

    [ObservableProperty]
    public partial bool IsPressed { get; set; }

    /// <summary>A normally-open button conducts only while pressed; normally-closed is the reverse.</summary>
    [ObservableProperty]
    public partial bool IsNormallyOpen { get; set; }

    public override string ComponentType => "Push Button";

    public override string DesignatorPrefix => "SW";

    public override string ValueLabel => IsPressed ? "pressed" : IsNormallyOpen ? "N/O" : "N/C";

    public string InteractionHint => IsPressed ? "Release the button" : "Press the button";

    /// <summary>True when the contact is currently conducting.</summary>
    public bool IsConducting => IsNormallyOpen ? IsPressed : !IsPressed;

    public void Interact() => IsPressed = !IsPressed;

    public override void StampMatrix(MnaSystem system, SimulationState state) =>
        StampContact(system, A, B, IsConducting);

    public override double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state)
    {
        var current = ContactCurrent(system, A, B, IsConducting);
        return ReferenceEquals(terminal, B) ? -current : current;
    }

    partial void OnIsPressedChanged(bool value) => NotifyValueChanged();

    partial void OnIsNormallyOpenChanged(bool value) => NotifyValueChanged();
}

/// <summary>
/// Single-pole double-throw switch. The common terminal is always connected to exactly one of the
/// two throws, so it works as a changeover or as a selector.
/// </summary>
public partial class SpdtSwitch : MechanicalContact, IInteractiveComponent
{
    public SpdtSwitch(bool selectB = false)
    {
        IsThrownToB = selectB;

        Common = new Terminal("com", "COM", TerminalType.Passive, new Point(-30, 0));
        ThrowA = new Terminal("a", "A", TerminalType.Passive, new Point(30, -20));
        ThrowB = new Terminal("b", "B", TerminalType.Passive, new Point(30, 20));
        Terminals = [Common, ThrowA, ThrowB];
    }

    public Terminal Common { get; }
    public Terminal ThrowA { get; }
    public Terminal ThrowB { get; }

    /// <summary>False selects throw A, true selects throw B.</summary>
    [ObservableProperty]
    public partial bool IsThrownToB { get; set; }

    public override string ComponentType => "Switch (SPDT)";

    public override string DesignatorPrefix => "SW";

    public override string ValueLabel => IsThrownToB ? "to B" : "to A";

    public string InteractionHint => IsThrownToB ? "Throw to A" : "Throw to B";

    public void Interact() => IsThrownToB = !IsThrownToB;

    public override double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state)
    {
        // Both paths are stamped at all times — the unselected one as a near-open — so both carry
        // a current, and the common pin carries their sum rather than only the selected one.
        var toA = ContactCurrent(system, Common, ThrowA, !IsThrownToB);
        var toB = ContactCurrent(system, Common, ThrowB, IsThrownToB);

        if (ReferenceEquals(terminal, ThrowA)) return -toA;
        if (ReferenceEquals(terminal, ThrowB)) return -toB;
        return toA + toB;
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        StampContact(system, Common, ThrowA, !IsThrownToB);
        StampContact(system, Common, ThrowB, IsThrownToB);
    }

    partial void OnIsThrownToBChanged(bool value) => NotifyValueChanged();
}
