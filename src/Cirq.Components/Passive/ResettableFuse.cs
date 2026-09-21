using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Passive;

/// <summary>
/// A polymeric resettable fuse — a PPTC, of the sort sold as a Polyswitch or Multifuse.
/// <para>
/// It is not a fuse and it does not blow. It is a lump of polymer with carbon in it, and the
/// carbon makes a conducting path through it while it is cold. Pass too much current and it heats
/// itself; past a temperature the polymer expands, the carbon chains break apart, and the
/// resistance rises by three or four orders of magnitude in a few milliseconds. The current falls
/// to a trickle — enough to keep the device warm and therefore tripped, which is why it stays
/// tripped — and the moment the supply is removed it cools, the carbon reconnects, and it works
/// again.
/// </para>
/// <para>
/// Three consequences follow from that, and all three surprise people who think of it as a fuse
/// that resets itself.
/// </para>
/// <para>
/// It <b>does not interrupt</b> the circuit. Tripped it is a few hundred ohms rather than an open,
/// so a fault still draws a milliamp or two and anything downstream stays half-powered rather than
/// dead. It is <b>slow</b>: hundreds of milliseconds at the hold current, milliseconds at ten
/// times it, where a fuse of the same rating is quicker. And its <b>cold resistance is not
/// nothing</b> — tens or hundreds of milliohms in series with whatever it protects, which at an
/// amp is a measurable drop and at ten is a problem.
/// </para>
/// <para>
/// What it is for is the fault that keeps happening: a USB port somebody shorts, a motor that
/// stalls, a battery pack a customer will reverse. A fuse there means a service call. This one
/// means a pause.
/// </para>
/// </summary>
public partial class ResettableFuse : TwoTerminalComponent, ICurrentReporting
{
    /// <summary>Normalised temperature: 0 is cold and 1 is the trip point.</summary>
    private double _heat;

    public ResettableFuse(double holdCurrent = 0.5) : base("A", "B")
    {
        HoldCurrent = holdCurrent;
    }

    /// <summary>
    /// The current it will pass indefinitely without tripping, in amps. The number on the part.
    /// </summary>
    [ObservableProperty]
    public partial double HoldCurrent { get; set; }

    /// <summary>
    /// The current at which it is guaranteed to trip, in amps. Typically twice the hold current,
    /// and the gap between the two is the awkward band where it may do either.
    /// </summary>
    [ObservableProperty]
    public partial double TripCurrent { get; set; } = 1.0;

    /// <summary>Resistance while cold and conducting, in ohms.</summary>
    [ObservableProperty]
    public partial double ColdResistance { get; set; } = 0.1;

    /// <summary>
    /// Resistance once tripped, in ohms. Large, but nothing like an open circuit — which is the
    /// thing about this part that catches people.
    /// </summary>
    [ObservableProperty]
    public partial double TrippedResistance { get; set; } = 400.0;

    /// <summary>
    /// How long it takes to trip at twice the trip current, in seconds. Everything faster or
    /// slower scales against this, because what is really happening is a lump of plastic warming
    /// up and the heating goes as the square of the current.
    /// </summary>
    [ObservableProperty]
    public partial double TripTime { get; set; } = 0.1;

    /// <summary>
    /// How long it takes to cool once the current is gone, in seconds. Longer than tripping, as
    /// it is in life: it heats itself quickly and has nothing but the air to cool it.
    /// </summary>
    [ObservableProperty]
    public partial double CoolingTime { get; set; } = 2.0;

    public override string ComponentType => "Resettable Fuse";

    public override string DesignatorPrefix => "F";

    public override string ValueLabel => IsTripped
        ? "tripped"
        : $"{SiPrefix.Format(HoldCurrent, "A")} hold";

    /// <summary>Current through it at the last solved point, in amps.</summary>
    public double Current { get; private set; }

    /// <summary>True once it has gone high-resistance.</summary>
    public bool IsTripped { get; private set; }

    /// <summary>
    /// How hot it is, from 0 to 1, where 1 is the trip point. Worth watching: the whole behaviour
    /// of the part is this number rising and falling.
    /// </summary>
    public double Heat => _heat;

    /// <summary>Its resistance right now, in ohms.</summary>
    public double Resistance => IsTripped
        ? Math.Max(TrippedResistance, 1e-3)
        : Math.Max(ColdResistance, 1e-6);

    public IReadOnlyList<string> Violations => IsTripped
        ? [$"tripped, and still passing {SiPrefix.Format(Math.Abs(Current), "A")} — a PPTC goes " +
           "high-resistance rather than open, so the fault is reduced rather than removed. It will " +
           "not reset until the current through it stops"]
        : [];

    public override void StampMatrix(MnaSystem system, SimulationState state) =>
        system.StampResistor(system.Node(A), system.Node(B), Resistance);

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        Current = (system.NodeVoltage(A) - system.NodeVoltage(B)) / Resistance;

        if (!state.IsTransient) return;

        var step = state.TimeStep;
        if (step <= 0) return;

        // Heating goes as I², like any resistor, and is measured against the trip current so that
        // the number means "fraction of the way to tripping" rather than a temperature nobody has
        // a feel for.
        var trip = Math.Max(TripCurrent, 1e-9);
        var drive = (Current * Current) / (trip * trip);

        // Warming towards whatever this current would eventually hold it at, and cooling towards
        // the ambient the rest of the time. One equation does both: the target is the drive.
        var tau = drive > _heat ? Math.Max(TripTime, 1e-9) * 4.0 : Math.Max(CoolingTime, 1e-9);

        _heat += (drive - _heat) * Math.Clamp(step / tau, 0.0, 1.0);
        _heat = Math.Max(_heat, 0.0);

        if (!IsTripped && _heat >= 1.0)
        {
            IsTripped = true;
            NotifyValueChanged();
        }
        else if (IsTripped && _heat < 0.35)
        {
            // Cool enough for the carbon to have found its way back together. Deliberately well
            // below the trip point, because a part that reset the instant it dipped under would
            // chatter across a fault instead of staying out of the way.
            IsTripped = false;
            NotifyValueChanged();
        }
    }

    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state) =>
        ReferenceEquals(terminal, A) ? Current : -Current;

    public override void ResetState()
    {
        Current = 0;
        IsTripped = false;
        _heat = 0;
    }

    partial void OnHoldCurrentChanged(double value) => NotifyValueChanged();
}
