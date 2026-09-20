using Cirq.Components.Nonlinear;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Buses;

/// <summary>
/// A bidirectional logic level shifter — the little BSS138 board that lets 3.3 V and 5 V parts
/// talk to each other.
/// <para>
/// One MOSFET per channel, with its gate tied to the low-voltage rail, its source on the low side
/// and its drain on the high side, and a pull-up on each side. That arrangement does something
/// that looks impossible at first: it passes signals <b>both ways</b> through a transistor that
/// can only conduct one.
/// </para>
/// <para>
/// Pull the low side down and the gate-source voltage becomes the whole low rail, so the channel
/// turns on and drags the high side down with it. Pull the <i>high</i> side down instead and the
/// transistor is the wrong way round to help — but its <b>body diode</b> is not. The diode
/// conducts, the low side falls, and once it has fallen the channel turns on properly and
/// finishes the job. The body diode, a nuisance everywhere else in this library, is the entire
/// trick here.
/// </para>
/// <para>
/// It is modelled as those two things rather than as logic, because the cascade is the point: the
/// diode has to pull the low side down <i>before</i> the channel can help, and a part that simply
/// decided "either side is low, so both are low" would hide the mechanism and, worse, would end up
/// reading the pin it was itself driving.
/// </para>
/// <para>
/// What follows from the circuit is the thing people get caught by: this only shifts signals that
/// are <b>pulled down and released</b>, never driven high. It is made for I²C, which works that
/// way already. For a push-pull output it is the wrong part — the pull-ups are what make the high
/// level, and something driving hard against them fights the shifter rather than passing through.
/// </para>
/// </summary>
public sealed partial class LevelShifter : CircuitComponent
{
    /// <summary>Channels on the board, as the common four-way module has.</summary>
    public const int Channels = 4;

    private readonly Terminal[] _low = new Terminal[Channels];
    private readonly Terminal[] _high = new Terminal[Channels];

    /// <summary>Last linearisation point of each body diode, for junction limiting.</summary>
    private readonly double[] _previousDiode = new double[Channels];

    private bool _limitedThisIteration;

    public LevelShifter()
    {
        LowReference = new Terminal("lv", "LV", TerminalType.Power, new Point(-50, -52));
        HighReference = new Terminal("hv", "HV", TerminalType.Power, new Point(50, -52));
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(-50, 52));

        List<Terminal> all = [LowReference, HighReference, Gnd];

        for (var i = 0; i < Channels; i++)
        {
            var y = -24 + (i * 16);

            _low[i] = new Terminal($"lv{i + 1}", $"LV{i + 1}", TerminalType.Bidirectional, new Point(-50, y));
            _high[i] = new Terminal($"hv{i + 1}", $"HV{i + 1}", TerminalType.Bidirectional, new Point(50, y));

            all.Add(_low[i]);
            all.Add(_high[i]);
        }

        Terminals = all;
    }

    /// <summary>The low-voltage supply, which is also what every gate is tied to.</summary>
    public Terminal LowReference { get; }

    /// <summary>The high-voltage supply.</summary>
    public Terminal HighReference { get; }

    public Terminal Gnd { get; }

    /// <summary>The low side of a channel, indexed from zero.</summary>
    public Terminal Low(int channel) => _low[channel];

    /// <summary>The high side of a channel, indexed from zero.</summary>
    public Terminal High(int channel) => _high[channel];

    /// <summary>
    /// Pull-up fitted on each pin, in ohms. The module has them; a shifter built from loose
    /// MOSFETs does not, and then nothing works until you add them.
    /// </summary>
    [ObservableProperty]
    public partial double PullUpResistance { get; set; } = 10e3;

    /// <summary>Gate threshold of the MOSFETs, in volts. A BSS138 is about 1.3.</summary>
    [ObservableProperty]
    public partial double ThresholdVoltage { get; set; } = 1.3;

    /// <summary>Channel resistance once it is on, in ohms.</summary>
    [ObservableProperty]
    public partial double OnResistance { get; set; } = 10.0;

    public override string ComponentType => "Level Shifter";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => $"{Channels}-ch bidirectional";

    public override bool IsNonlinear => true;

    /// <summary>Voltage on the low rail at the last solved point.</summary>
    public double LowVoltage { get; private set; }

    /// <summary>Voltage on the high rail at the last solved point.</summary>
    public double HighVoltage { get; private set; }

    /// <summary>True while both rails are present and the right way round.</summary>
    public bool IsUsable => LowVoltage > 0.5 && HighVoltage >= LowVoltage - 0.2;

    /// <summary>Saturation current of a body diode, in amps.</summary>
    private const double DiodeSaturation = 1e-14;

    /// <summary>
    /// How sharply the channel comes on, in volts. Wide enough that Newton always has a gradient
    /// to walk down, narrow enough that the transistor still behaves like a switch.
    /// </summary>
    private const double TransitionWidth = 0.12;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var gate = system.Node(LowReference);
        var lowRail = system.Node(LowReference);
        var highRail = system.Node(HighReference);

        var pullUp = Math.Max(PullUpResistance, 1.0);
        _limitedThisIteration = false;

        for (var i = 0; i < Channels; i++)
        {
            var low = system.Node(_low[i]);
            var high = system.Node(_high[i]);

            // The pull-ups the module carries. Without them a released channel floats and the
            // part does nothing at all — which is what happens when someone builds this from
            // loose transistors and forgets them.
            system.StampResistor(low, lowRail, pullUp);
            system.StampResistor(high, highRail, pullUp);

            StampBodyDiode(system, state, i, low, high);
            StampChannel(system, gate, low, high);
        }
    }

    /// <summary>
    /// The body diode, anode on the low side and cathode on the high side. It is what carries a
    /// high side that has been pulled down back into the low side.
    /// <para>
    /// The junction is limited between iterations, as every other diode in the library is. Without
    /// it the first trial voltage across this one is the whole rail — three volts and more of
    /// forward bias — and an exponential of that overflows to infinity before it ever reaches the
    /// matrix, which arrives as a singular factorisation rather than as anything resembling a
    /// diode. It is the same limiting SPICE calls <c>pnjlim</c> and for the same reason.
    /// </para>
    /// </summary>
    private void StampBodyDiode(MnaSystem system, SimulationState state, int channel, int low, int high)
    {
        var vt = state.ThermalVoltage;
        var raw = system.IterationVoltageAcross(low, high);

        var limited = Diode.LimitJunctionVoltage(
            raw, _previousDiode[channel], vt, Junction.CriticalVoltage(DiodeSaturation, vt));

        if (Math.Abs(limited - raw) > 1e-12) _limitedThisIteration = true;
        _previousDiode[channel] = limited;

        var (current, conductance) = Junction.Evaluate(limited, DiodeSaturation, vt);

        system.StampNorton(low, high, conductance, current - (conductance * limited));
    }

    /// <summary>Not converged while any junction is still being held back.</summary>
    public override bool HasConverged(MnaSystem system, SimulationState state) => !_limitedThisIteration;

    /// <summary>
    /// The channel, as a conductance between the two sides controlled by the gate-source voltage.
    /// <para>
    /// The source is the low side, so the control voltage is the low rail above the low pin: zero
    /// while the pin rests at its rail and the transistor is off, and the whole rail once
    /// something drags the pin down.
    /// </para>
    /// <para>
    /// Only the conductance is stamped, and it is re-evaluated from the iteration voltages each
    /// time round. Adding the exact derivative with respect to the gate — the transconductance —
    /// is what a textbook Newton step would do, and it made the matrix singular: that term lands
    /// on the low pin's own diagonal with a minus sign, and near the threshold with the high side
    /// already pulled down it is larger than everything else on that diagonal put together, so
    /// the diagonal goes negative and the factorisation fails. Re-evaluating instead converges to
    /// the same answer a little more slowly, and the pull-up on each pin is what guarantees it
    /// converges at all.
    /// </para>
    /// </summary>
    private void StampChannel(MnaSystem system, int gate, int low, int high)
    {
        var vgs = system.IterationVoltageAcross(gate, low);

        system.StampConductance(low, high, Channel(vgs));
    }

    /// <summary>Channel conductance for a gate-source voltage.</summary>
    private double Channel(double vgs)
    {
        var full = 1.0 / Math.Max(OnResistance, 1e-3);

        // A bounded S-curve rather than a step, so the conductance moves smoothly as the pin is
        // dragged down rather than snapping between two values the solver has to jump across.
        var x = Math.Clamp((vgs - ThresholdVoltage) / TransitionWidth, -20.0, 20.0);

        return full / (1.0 + Math.Exp(-x));
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        LowVoltage = system.NodeVoltage(LowReference) - system.NodeVoltage(Gnd);
        HighVoltage = system.NodeVoltage(HighReference) - system.NodeVoltage(Gnd);
    }

    /// <summary>What is wrong with how this part is wired, if anything.</summary>
    public IReadOnlyList<string> Violations
    {
        get
        {
            // Nothing powered at all is an unbuilt circuit rather than a mistake.
            if (LowVoltage < 0.1 && HighVoltage < 0.1) return [];

            if (LowVoltage < 0.5)
            {
                return ["the LV pin has no supply on it — every gate is tied to it, so without it " +
                        "no channel can turn on and nothing passes either way"];
            }

            if (HighVoltage < 0.5)
                return ["the HV pin has no supply on it, so the high side has nothing to pull up to"];

            if (HighVoltage < LowVoltage - 0.2)
            {
                return [$"HV is {HighVoltage:0.0} V against LV at {LowVoltage:0.0} V — the rails are " +
                        "the wrong way round, and the body diodes conduct regardless of what " +
                        "anything is driving"];
            }

            return [];
        }
    }

    public override void ResetState()
    {
        LowVoltage = 0;
        HighVoltage = 0;

        Array.Clear(_previousDiode);
        _limitedThisIteration = false;
    }
}
