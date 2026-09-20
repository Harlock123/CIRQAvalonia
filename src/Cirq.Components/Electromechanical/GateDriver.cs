using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Electromechanical;

/// <summary>
/// A MOSFET gate driver: a logic input in, amps of gate current out.
/// <para>
/// The part exists because of one number people do not expect. A power MOSFET's gate is a
/// <b>capacitor</b> — tens of nanofarads, once the Miller effect is counted — and switching the
/// transistor means moving all of that charge. Do the arithmetic: 50 nF taken to 10 V in 50 ns
/// needs <b>ten amps</b>. A logic pin can manage twenty milliamps, so driving a power FET from a
/// microcontroller does not fail, it just takes five hundred times longer.
/// </para>
/// <para>
/// And slow switching is where the heat comes from. A MOSFET is cheap to keep on, because
/// R<sub>DS(on)</sub> is milliohms, and cheap to keep off, because nothing flows. It is expensive
/// in <i>between</i>, passing most of the current with most of the voltage across it, which for
/// those few microseconds is a hundred times the power it dissipates the rest of the time. Switch
/// slowly at 100 kHz and the transistor spends a fifth of its life in that state; it will be too
/// hot to touch, and nothing in the schematic will say why.
/// </para>
/// <para>
/// The second thing a driver does is <b>lift the gate to its own supply</b>. It takes a 3.3 V
/// logic input and swings the gate to twelve, which matters because a FET's on-resistance is
/// specified at a gate voltage most logic cannot reach — drive a 10 V-rated FET with 3.3 V and it
/// is not off, and not properly on either, which is the worst place for it to be.
/// </para>
/// <para>
/// Real drivers sink harder than they source, and this one does too: turning a FET <i>off</i>
/// quickly matters more than turning it on, because a gate left drifting near its threshold while
/// something else in a half-bridge turns on is how shoot-through happens.
/// </para>
/// </summary>
public sealed partial class GateDriver : DigitalComponent
{
    private double _supply = 12.0;

    public GateDriver()
    {
        // Tens of nanoseconds, which is what separates a driver from a logic gate.
        PropagationDelay = 40e-9;
        Levels = LogicLevels.Cmos33V;

        Input = new Terminal("in", "IN", TerminalType.Input, new Point(-40, 0));
        Output = new Terminal("out", "OUT", TerminalType.Output, new Point(40, 0));
        Vcc = new Terminal("vcc", "VCC", TerminalType.Power, new Point(0, -30));
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(0, 30));

        Terminals = [Input, Output, Vcc, Gnd];
        ConfigurePins([Input], [Output]);
    }

    public Terminal Input { get; }

    /// <summary>To the gate. Push-pull, and stiff — that is the entire point of the part.</summary>
    public Terminal Output { get; }

    /// <summary>The driver's own supply, which is what the gate is swung to rather than the logic rail.</summary>
    public Terminal Vcc { get; }

    public Terminal Gnd { get; }

    /// <summary>Peak current it can push into a gate, in amps.</summary>
    [ObservableProperty]
    public partial double PeakSourceCurrent { get; set; } = 2.0;

    /// <summary>
    /// Peak current it can pull out of one. Larger than the source current, as real drivers are:
    /// getting a FET off in a hurry is the half that keeps a half-bridge alive.
    /// </summary>
    [ObservableProperty]
    public partial double PeakSinkCurrent { get; set; } = 3.0;

    /// <summary>Whether the output follows the input or opposes it.</summary>
    [ObservableProperty]
    public partial bool IsInverting { get; set; }

    /// <summary>Supply below which it will not drive properly, in volts.</summary>
    [ObservableProperty]
    public partial double UnderVoltageLockout { get; set; } = 8.0;

    /// <summary>
    /// Gate voltage a FET is unlikely to survive, in volts. Nothing in the model breaks at it —
    /// it is here so the part can say something when the supply is set somewhere a real gate oxide
    /// would not come back from.
    /// </summary>
    [ObservableProperty]
    public partial double MaximumGateVoltage { get; set; } = 20.0;

    public override string ComponentType => "Gate Driver";

    public override string ValueLabel =>
        $"{SiPrefix.Format(PeakSourceCurrent, "A")}/{SiPrefix.Format(PeakSinkCurrent, "A")}";

    /// <summary>The driver's supply at the last solved point, which is what the gate swings to.</summary>
    public double SupplyVoltage => _supply;

    /// <summary>True while the supply is too low for it to drive anything properly.</summary>
    public bool IsLockedOut => _supply < UnderVoltageLockout;

    /// <summary>
    /// Resistance behind the output while sourcing, in ohms — the supply divided by the peak
    /// current, which is how a driver's "2 A" rating is actually defined.
    /// </summary>
    public double SourceResistance => Math.Max(_supply, 1.0) / Math.Max(PeakSourceCurrent, 1e-3);

    /// <summary>Resistance behind it while sinking.</summary>
    public double SinkResistance => Math.Max(_supply, 1.0) / Math.Max(PeakSinkCurrent, 1e-3);

    /// <summary>
    /// Nonlinear, because what it drives depends on a voltage elsewhere in the circuit — its own
    /// supply — rather than only on its inputs. Without saying so it would be stamped once from
    /// whatever the supply node happened to be at the start of the solve, which is nothing.
    /// </summary>
    public override bool IsNonlinear => true;

    protected override int ReferenceNode(MnaSystem system) => system.Node(Gnd);

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        // Read the supply before stamping: the output swings to the driver's rail, not the logic
        // family's, and that is most of why one of these is fitted.
        _supply = system.IterationVoltage(Vcc) - system.IterationVoltage(Gnd);

        base.StampMatrix(system, state);
    }

    protected override double OutputVoltage(LogicState state)
    {
        if (IsLockedOut) return 0.0;

        return state switch
        {
            LogicState.High => Math.Max(_supply, 0.0),
            _ => 0.0,
        };
    }

    /// <summary>
    /// Asymmetric, unlike an ordinary logic output. It changes with the state rather than being
    /// one number, which is what lets the turn-off be faster than the turn-on.
    /// </summary>
    protected override double OutputImpedance =>
        GetOutputState(0) == LogicState.High ? SourceResistance : SinkResistance;

    public override void EvaluateLogic(IDigitalContext context)
    {
        var level = context.ReadInput(Input, Levels);

        var high = level switch
        {
            LogicState.High => !IsInverting,
            LogicState.Low => IsInverting,

            // A floating input is not a level, and a driver that guessed would be worse than one
            // that holds the gate off.
            _ => false,
        };

        context.Schedule(this, 0, high ? LogicState.High : LogicState.Low, DelayFor(context));
    }

    public IReadOnlyList<string> Violations
    {
        get
        {
            List<string> found = [];

            if (IsLockedOut && _supply > 0.5)
            {
                found.Add($"running from {_supply:0.#} V, below its {UnderVoltageLockout:0.#} V lockout — " +
                          "the gate will not be taken high enough to turn a FET properly on, and a " +
                          "half-on FET is where the heat is");
            }

            if (_supply > MaximumGateVoltage)
            {
                found.Add($"driving the gate to {_supply:0.#} V against a {MaximumGateVoltage:0.#} V " +
                          "limit — a gate oxide punctures once and stays punctured");
            }

            return found;
        }
    }

    partial void OnPeakSourceCurrentChanged(double value) => NotifyValueChanged();

    partial void OnPeakSinkCurrentChanged(double value) => NotifyValueChanged();
}
