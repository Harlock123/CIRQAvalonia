using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Nonlinear;

/// <summary>
/// A phototransistor: light in, current out.
/// <para>
/// It is the opposite of the light-dependent resistor beside it in the palette, and the difference
/// is worth understanding before choosing one. An LDR is a <b>resistance</b> that falls as it is
/// lit, so what it does to a circuit depends on whatever it is wired in series with. This is a
/// <b>current source</b> commanded by light: it passes a current proportional to how much falls on
/// it and, until it runs out of voltage, does not much care what it is connected to.
/// </para>
/// <para>
/// The other difference is the one that decides which you use. An LDR is made of a bulk
/// photoconductor and takes tens of milliseconds to respond — it is useless above a few hertz,
/// which is why no remote control, optical encoder or pulse oximeter has ever contained one. A
/// phototransistor is a junction and responds in microseconds.
/// </para>
/// <para>
/// Being a current source is also what makes it awkward: the current is in microamps to
/// milliamps, and turning that into a voltage is what the load resistor — or, done properly, a
/// transimpedance amplifier round one of the op-amps in this library — is for. Too small a load
/// and the output barely moves; too large and it saturates in ordinary room light.
/// </para>
/// </summary>
public partial class Phototransistor : CircuitComponent, IInteractiveComponent, ICurrentReporting
{
    public Phototransistor()
    {
        Collector = new Terminal("c", "C", TerminalType.Passive, new Point(0, -30));
        Emitter = new Terminal("e", "E", TerminalType.Passive, new Point(0, 30));

        Terminals = [Collector, Emitter];
    }

    public Terminal Collector { get; }

    public Terminal Emitter { get; }

    /// <summary>
    /// Light falling on it, in lux. Ordinary room lighting is a few hundred; an overcast day
    /// outdoors is ten thousand, and direct sun a hundred thousand.
    /// </summary>
    [ObservableProperty]
    public partial double Illuminance { get; set; } = 300.0;

    /// <summary>What it swings to when you double-click it.</summary>
    [ObservableProperty]
    public partial double AlternateIlluminance { get; set; } = 5.0;

    /// <summary>
    /// Collector current per lux, in amps. A milliamp at a thousand lux is typical of the
    /// ordinary five-millimetre parts.
    /// </summary>
    [ObservableProperty]
    public partial double Responsivity { get; set; } = 1e-6;

    /// <summary>
    /// What it passes in the dark, in amps. Small, but not zero — and it is what decides how
    /// large a load resistor you can get away with.
    /// </summary>
    [ObservableProperty]
    public partial double DarkCurrent { get; set; } = 100e-9;

    /// <summary>
    /// Collector-emitter voltage below which it stops behaving as a current source, in volts.
    /// Once it is down here the transistor is saturated and the load is deciding the current.
    /// </summary>
    [ObservableProperty]
    public partial double SaturationVoltage { get; set; } = 0.2;

    public override string ComponentType => "Phototransistor";

    public override string DesignatorPrefix => "Q";

    public override string ValueLabel => $"{Illuminance:0} lx";

    public override bool IsNonlinear => true;

    public string InteractionHint => "Light and shade it";

    /// <summary>Current it would pass given enough voltage to do it, in amps.</summary>
    public double PhotoCurrent => DarkCurrent + (Math.Max(Illuminance, 0.0) * Math.Max(Responsivity, 0.0));

    /// <summary>Collector current at the last solved point, in amps.</summary>
    public double CollectorCurrent { get; private set; }

    /// <summary>Collector-emitter voltage at the last solved point.</summary>
    public double CollectorVoltage { get; private set; }

    /// <summary>
    /// True when it has run out of voltage to work with — the load is too large for this much
    /// light, and the output has stopped following it.
    /// </summary>
    public bool IsSaturated => CollectorVoltage < SaturationVoltage * 1.5 && PhotoCurrent > DarkCurrent * 2;

    public void Interact() =>
        (Illuminance, AlternateIlluminance) = (AlternateIlluminance, Illuminance);

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var collector = system.Node(Collector);
        var emitter = system.Node(Emitter);

        var knee = Math.Max(SaturationVoltage, 1e-3);

        // Never linearised at a negative collector voltage. Out of saturation this is very nearly
        // a current source, so the linear solve overshoots well past zero; evaluated there the
        // device looks like an open, the load takes the node back to the rail, and Newton sits in
        // a two-point cycle. Zero is where the conductance is greatest, so it is the safe place to
        // stand — and it never binds at a real operating point, which sits a little above it.
        var raw = system.IterationVoltageAcross(collector, emitter);
        var vce = Math.Max(raw, 0.0);

        var exponent = Math.Exp(-vce / knee);
        var saturation = 1.0 - exponent;

        var available = PhotoCurrent;
        var current = available * saturation;

        // The slope as it comes out of saturation, which is all the conductance it has.
        var gce = available * exponent / knee;

        system.StampNorton(collector, emitter, gce, current - (gce * vce));
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        CollectorVoltage = system.NodeVoltage(Collector) - system.NodeVoltage(Emitter);

        var knee = Math.Max(SaturationVoltage, 1e-3);
        var saturation = 1.0 - Math.Exp(-Math.Max(CollectorVoltage, 0.0) / knee);

        CollectorCurrent = PhotoCurrent * saturation;
    }

    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state) =>
        ReferenceEquals(terminal, Collector) ? CollectorCurrent : -CollectorCurrent;

    public override void ResetState()
    {
        CollectorCurrent = 0;
        CollectorVoltage = 0;
    }

    partial void OnIlluminanceChanged(double value) => NotifyValueChanged();
}
