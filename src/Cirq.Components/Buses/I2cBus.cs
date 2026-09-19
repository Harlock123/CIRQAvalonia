using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Buses;

/// <summary>
/// Shared wiring for everything on an I²C bus.
/// <para>
/// I²C is <b>open drain</b>: nothing on the bus ever drives a line high. Every device can only
/// pull a line down or let go of it, and a pair of resistors to the supply is what pulls it back
/// up. That is what lets a dozen devices share two wires without any of them fighting, and it is
/// also why a bus with no pull-ups does not work at all rather than working badly — there is
/// nothing to make a high level out of. Forgetting them is the classic first mistake, and it
/// behaves here exactly as it does on a breadboard.
/// </para>
/// </summary>
public abstract partial class I2cDevice : DigitalComponent
{
    /// <summary>
    /// Pin positions are settable because a terminal is immutable once made, and a part with
    /// eight port pins down one side needs its bus pins somewhere other than the default.
    /// </summary>
    protected I2cDevice(Point? sda = null, Point? scl = null, Point? vcc = null, Point? gnd = null)
    {
        Sda = new Terminal("sda", "SDA", TerminalType.Bidirectional, sda ?? new Point(40, -20));
        Scl = new Terminal("scl", "SCL", TerminalType.Bidirectional, scl ?? new Point(40, 20));
        Vcc = new Terminal("vcc", "VCC", TerminalType.Power, vcc ?? new Point(-40, -20));
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, gnd ?? new Point(-40, 20));

        Levels = LogicLevels.Cmos33V;
    }

    /// <summary>The data line. Pulled down or released, never driven high.</summary>
    public Terminal Sda { get; }

    /// <summary>The clock line.</summary>
    public Terminal Scl { get; }

    public Terminal Vcc { get; }

    public Terminal Gnd { get; }

    public override string DesignatorPrefix => "U";

    protected override int ReferenceNode(Cirq.Core.Simulation.MnaSystem system) => system.Node(Gnd);

    /// <summary>Reads a bus line as a level, which is a node voltage rather than a driven state.</summary>
    protected bool IsHigh(IDigitalContext context, Terminal line) =>
        Levels.Classify(context.NodeVoltage(line) - context.NodeVoltage(Gnd)) != LogicState.Low;

    /// <summary>
    /// Pulls a line down or lets go of it. There is no third option on this bus — driving high
    /// is what a device must never do, because another one may be pulling low at the time.
    /// </summary>
    protected void Drive(IDigitalContext context, int outputIndex, bool pullLow) =>
        context.Schedule(this, outputIndex, pullLow ? LogicState.Low : LogicState.HighImpedance,
            DelayFor(context));
}
