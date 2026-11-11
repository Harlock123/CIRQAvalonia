using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Nonlinear;

/// <summary>
/// A 2N2646 unijunction transistor: a bar of silicon between base 1 and base 2 (RBB, 7 kΩ) with the emitter's junction
/// part-way along, at the intrinsic stand-off ratio η (0.65). The emitter stays off until it's a diode drop above
/// η·VBB — the peak point — then the bar's conductivity collapses between it and B1 and it fires, dumping whatever's on
/// the emitter into B1, until the emitter current falls below the valley current (4 mA) and it turns off again. A
/// capacitor charged through a resistor on the emitter is the relaxation oscillator it was made for, at about
/// 1 / (R·C·ln(1/(1−η))), with a pulse across a resistor in B1.
/// </summary>
public sealed partial class Unijunction : CircuitComponent
{
    private readonly JunctionStamp _emitter = new();
    private bool _fired;

    public Unijunction()
    {
        Emitter = new Terminal("e", "E", TerminalType.Passive, new Point(-40, 10));
        Base2 = new Terminal("b2", "B2", TerminalType.Passive, new Point(10, -40));
        Base1 = new Terminal("b1", "B1", TerminalType.Passive, new Point(10, 40));
        Terminals = [Emitter, Base2, Base1];
    }

    public Terminal Emitter { get; }
    public Terminal Base1 { get; }
    public Terminal Base2 { get; }

    [ObservableProperty]
    public partial double StandOffRatio { get; set; } = 0.65;

    [ObservableProperty]
    public partial double InterbaseResistance { get; set; } = 7e3;

    [ObservableProperty]
    public partial double ValleyCurrent { get; set; } = 4e-3;

    public bool Fired => _fired;

    public double EmitterCurrent => _emitter.Current;

    public override string ComponentType => "UJT";

    public override string DesignatorPrefix => "Q";

    public override string ValueLabel => "2N2646";

    public override bool IsNonlinear => true;

    public override int InternalNodeCount => 1;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var (e, b1, b2, n) = (system.Node(Emitter), system.Node(Base1), system.Node(Base2), system.InternalNode(this));
        var eta = Math.Clamp(StandOffRatio, 0.3, 0.9);
        system.StampResistor(b2, n, (1 - eta) * InterbaseResistance);
        system.StampResistor(n, b1, _fired ? 20 : eta * InterbaseResistance);
        _emitter.Stamp(system, e, n, 1e-13);
    }

    public override bool HasConverged(MnaSystem system, SimulationState state) => !_emitter.Limited;

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var ie = _emitter.Current;
        if (!_fired && ie > 5e-6) _fired = true;
        else if (_fired && ie < ValleyCurrent) _fired = false;
    }

    public override void ResetState()
    {
        _emitter.Reset();
        _fired = false;
    }
}

/// <summary>
/// A 2N6027 programmable unijunction transistor, which is really a small thyristor gated at its anode: it fires when
/// the anode is a diode drop above the gate — so two resistors on the gate set the peak voltage, where a UJT's is fixed
/// — and then conducts anode to cathode, about a volt across it, until the current falls below its valley current
/// (70 µA here, as with 10 kΩ on the gate). A capacitor on the anode charged through a resistor makes the same
/// relaxation oscillator as a UJT, at a frequency the gate divider sets.
/// </summary>
public sealed partial class ProgrammableUnijunction : CircuitComponent
{
    private readonly JunctionStamp _gate = new();
    private readonly JunctionStamp _main = new();
    private bool _fired;

    public ProgrammableUnijunction()
    {
        Anode = new Terminal("a", "A", TerminalType.Passive, new Point(0, -40));
        Gate = new Terminal("g", "G", TerminalType.Passive, new Point(-40, -10));
        Cathode = new Terminal("k", "K", TerminalType.Passive, new Point(0, 40));
        Terminals = [Anode, Gate, Cathode];
    }

    public Terminal Anode { get; }
    public Terminal Gate { get; }
    public Terminal Cathode { get; }

    [ObservableProperty]
    public partial double ValleyCurrent { get; set; } = 70e-6;

    public bool Fired => _fired;

    public double AnodeCurrent => _gate.Current + (_fired ? _main.Current : 0);

    public override string ComponentType => "PUT";

    public override string DesignatorPrefix => "Q";

    public override string ValueLabel => "2N6027";

    public override bool IsNonlinear => true;

    public override int InternalNodeCount => 1;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var (a, g, k, n) = (system.Node(Anode), system.Node(Gate), system.Node(Cathode), system.InternalNode(this));
        _gate.Stamp(system, a, g, 1e-13);
        if (_fired)
        {
            _main.Stamp(system, a, n, 1e-10);
            system.StampResistor(n, k, 1.0);
        }
        else system.StampResistor(n, k, 1e9);
        system.StampResistor(a, k, 1e9);
    }

    public override bool HasConverged(MnaSystem system, SimulationState state) => !_gate.Limited && (!_fired || !_main.Limited);

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        if (!_fired && _gate.Current > 2e-6) _fired = true;
        else if (_fired && _main.Current + _gate.Current < ValleyCurrent) _fired = false;
    }

    public override void ResetState()
    {
        _gate.Reset();
        _main.Reset();
        _fired = false;
    }
}
