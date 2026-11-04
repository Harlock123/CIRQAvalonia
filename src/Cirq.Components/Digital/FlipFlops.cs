using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

/// <summary>
/// Base for the generic storage blocks in the Logic Gates group: the D, JK and T flip-flops and the
/// SR and D latches.
/// <para>
/// These are the textbook symbols rather than packaged parts. Like the gates beside them they have
/// no supply pins and drive at their logic family's levels, so a counter or a state machine can be
/// drawn the way it is taught — one box per bit — before anybody picks a 7474 for it. Every one has
/// Q and its complement Q', and every one powers up with Q low.
/// </para>
/// <para>
/// The edge-triggered ones sample on the rising edge of CLK unless
/// <see cref="TriggersOnFallingEdge"/> is set, which is how a JK in most textbooks (and the 7476)
/// is drawn, with a bubble on the clock.
/// </para>
/// </summary>
public abstract partial class FlipFlopBlock : DigitalComponent
{
    private LogicState _lastClock = LogicState.Unknown;

    protected FlipFlopBlock(params (string Id, string Name)[] inputs)
    {
        List<Terminal> pins = [];
        var span = (inputs.Length - 1) * 20.0;

        for (var i = 0; i < inputs.Length; i++)
        {
            var y = inputs.Length == 1 ? 0 : (-span / 2) + (i * 20.0);
            pins.Add(new Terminal(inputs[i].Id, inputs[i].Name, TerminalType.Input, new Point(-40, y)));
        }

        Q = new Terminal("q", "Q", TerminalType.Output, new Point(40, -10));
        QNot = new Terminal("qn", "Q'", TerminalType.Output, new Point(40, 10));

        Terminals = [.. pins, Q, QNot];
        ConfigurePins(pins, [Q, QNot]);
        ApplyTransition(1, LogicState.High);
    }

    public Terminal Q { get; }

    public Terminal QNot { get; }

    /// <summary>The name of the clock or enable pin, which the symbol marks, or null for none.</summary>
    public virtual string? ClockPin => "CLK";

    /// <summary>True when the clock pin is an edge (drawn with the triangle) rather than a level.</summary>
    public virtual bool IsEdgeTriggered => true;

    /// <summary>Sample on the falling edge of the clock rather than the rising one.</summary>
    [ObservableProperty]
    public partial bool TriggersOnFallingEdge { get; set; }

    /// <summary>What it is holding.</summary>
    public LogicState State { get; protected set; } = LogicState.Low;

    public override string DesignatorPrefix => "FF";

    public override string ValueLabel => $"Q={State.ToChar()}";

    /// <summary>Half the symbol's height, so the captions can clear it.</summary>
    public double HalfHeight => Math.Max(30.0, InputTerminals.Max(t => Math.Abs(t.CanvasOffset.Y)) + 12.0);

    /// <summary>The input of this name, for wiring.</summary>
    protected Terminal Input(string id) => InputTerminals.First(t => t.Id == id);

    /// <summary>True on the evaluation at which the clock makes the edge this block samples on.</summary>
    protected bool ClockEdge(IDigitalContext context, Terminal clock)
    {
        var now = context.ReadInput(clock, Levels);

        // Nothing that happens while the power-on state is being settled is a clock edge. Without
        // this, a ripple counter counts its own Q' outputs coming up and powers up at fifteen.
        var edge = !context.IsInitializing && (TriggersOnFallingEdge
            ? _lastClock.IsHigh() && now.IsLow()
            : _lastClock.IsLow() && now.IsHigh());

        _lastClock = now;
        return edge;
    }

    /// <summary>Works out the next state from the inputs; called on every evaluation.</summary>
    protected abstract void Update(IDigitalContext context);

    /// <summary>What Q' drives. The complement, except where a latch is in its forbidden state.</summary>
    protected virtual LogicState Complement => State.Invert();

    public override void EvaluateLogic(IDigitalContext context)
    {
        var before = State;
        Update(context);

        context.Schedule(this, 0, State, DelayFor(context));
        context.Schedule(this, 1, Complement, DelayFor(context));

        if (before != State) NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        ApplyTransition(1, LogicState.High);
        State = LogicState.Low;
        _lastClock = LogicState.Unknown;
        NotifyValueChanged();
    }
}

/// <summary>
/// A D flip-flop: on the clock edge Q takes whatever D is, and holds it until the next. The one
/// register every synchronous design is built out of.
/// </summary>
public sealed partial class DFlipFlop() : FlipFlopBlock(("d", "D"), ("clk", "CLK"))
{
    public Terminal D => Input("d");

    public Terminal Clock => Input("clk");

    public override string ComponentType => "D Flip-Flop";

    protected override void Update(IDigitalContext context)
    {
        if (!ClockEdge(context, Clock)) return;

        var d = context.ReadInput(D, Levels);
        State = d.IsDriven() ? d : LogicState.Unknown;
    }
}

/// <summary>
/// A JK flip-flop: J sets, K resets, both together toggle, neither holds — all on the clock edge.
/// It is the SR latch with the forbidden state turned into something useful.
/// </summary>
public sealed partial class JkFlipFlop() : FlipFlopBlock(("j", "J"), ("clk", "CLK"), ("k", "K"))
{
    public Terminal J => Input("j");

    public Terminal Clock => Input("clk");

    public Terminal K => Input("k");

    public override string ComponentType => "JK Flip-Flop";

    protected override void Update(IDigitalContext context)
    {
        if (!ClockEdge(context, Clock)) return;

        var j = context.ReadInput(J, Levels);
        var k = context.ReadInput(K, Levels);

        if (!j.IsDriven() || !k.IsDriven())
        {
            State = LogicState.Unknown;
            return;
        }

        State = (j.IsHigh(), k.IsHigh()) switch
        {
            (true, true) => State.IsDriven() ? State.Invert() : LogicState.Unknown,
            (true, false) => LogicState.High,
            (false, true) => LogicState.Low,
            _ => State,
        };
    }
}

/// <summary>
/// A T flip-flop: with T high every clock edge toggles Q, so it divides the clock by two. Chain
/// them and you have a ripple counter, which is what they are for.
/// </summary>
public sealed partial class TFlipFlop() : FlipFlopBlock(("t", "T"), ("clk", "CLK"))
{
    public Terminal T => Input("t");

    public Terminal Clock => Input("clk");

    public override string ComponentType => "T Flip-Flop";

    protected override void Update(IDigitalContext context)
    {
        if (!ClockEdge(context, Clock)) return;

        var t = context.ReadInput(T, Levels);

        if (!t.IsDriven()) State = LogicState.Unknown;
        else if (t.IsHigh()) State = State.IsDriven() ? State.Invert() : LogicState.Unknown;
    }
}

/// <summary>
/// A D latch: transparent — Q follows D — while EN is high, and holding what it last saw once EN
/// falls. A level rather than an edge, which is the whole difference from the flip-flop.
/// </summary>
public sealed partial class DLatch() : FlipFlopBlock(("d", "D"), ("en", "EN"))
{
    public Terminal D => Input("d");

    public Terminal Enable => Input("en");

    public override string ComponentType => "D Latch";

    public override string? ClockPin => "EN";

    public override bool IsEdgeTriggered => false;

    protected override void Update(IDigitalContext context)
    {
        var enable = context.ReadInput(Enable, Levels);

        if (TriggersOnFallingEdge ? enable.IsLow() : enable.IsHigh())
        {
            var d = context.ReadInput(D, Levels);
            State = d.IsDriven() ? d : LogicState.Unknown;
        }
        else if (!enable.IsDriven())
        {
            State = LogicState.Unknown;
        }
    }
}

/// <summary>
/// An SR latch, active high, the way two cross-coupled NOR gates make one: S sets, R resets, and
/// with both low it remembers.
/// <para>
/// Both high is the state the textbooks call forbidden, and it is modelled as the NOR pair does it:
/// Q and Q' are both low, which is not a contradiction so much as a refusal to say. The trouble
/// comes when both are let go at once — the two gates race, and which one wins is decided by the
/// silicon rather than by the design. That outcome is given here as unknown (X) rather than
/// guessed at; let go of one first and the other decides.
/// </para>
/// </summary>
public sealed partial class SrLatch() : FlipFlopBlock(("s", "S"), ("r", "R"))
{
    private bool _bothHeld;

    public Terminal S => Input("s");

    public Terminal R => Input("r");

    public override string ComponentType => "SR Latch";

    public override string? ClockPin => null;

    public override bool IsEdgeTriggered => false;

    /// <summary>True while both inputs are held high — the state with Q and Q' both low.</summary>
    public bool IsForbidden => _bothHeld;

    protected override LogicState Complement => _bothHeld ? LogicState.Low : State.Invert();

    protected override void Update(IDigitalContext context)
    {
        var s = context.ReadInput(S, Levels);
        var r = context.ReadInput(R, Levels);

        if (s.IsHigh() && r.IsHigh())
        {
            _bothHeld = true;
            State = LogicState.Low;
            return;
        }

        if (s.IsHigh()) State = LogicState.High;
        else if (r.IsHigh()) State = LogicState.Low;
        else if (_bothHeld) State = LogicState.Unknown;     // both released together: the race

        _bothHeld = false;
    }

    public override void ResetLogic()
    {
        _bothHeld = false;
        base.ResetLogic();
    }
}

/// <summary>
/// A buffer whose output can be switched off: driven while EN is asserted, released to high
/// impedance while it is not.
/// <para>
/// That release is what lets several of them share one wire — a bus — provided only one is enabled
/// at a time. Enable two that disagree and nothing in the logic decides between them: each output
/// is a source behind its output resistance, so the analog solver puts the wire wherever the two
/// resistances divide it, somewhere near the middle of the rail, which every input reading it
/// calls unknown. Both outputs carry the current — sixty-odd milliamps for two TTL outputs — and
/// both report the fight, because on a real board that is the current that kills one of them.
/// </para>
/// <para>
/// The output pin is bidirectional, which is how a part that is built to share a net says so to
/// the rule check.
/// </para>
/// </summary>
public sealed partial class TriStateBuffer : DigitalComponent
{
    public TriStateBuffer()
    {
        In = new Terminal("a", "A", TerminalType.Input, new Point(-40, 0));
        Enable = new Terminal("en", "EN", TerminalType.Input, new Point(0, 30));
        Out = new Terminal("y", "Y", TerminalType.Bidirectional, new Point(40, 0));

        Terminals = [In, Enable, Out];
        ConfigurePins([In, Enable], [Out]);
    }

    public Terminal In { get; }

    public Terminal Enable { get; }

    public Terminal Out { get; }

    /// <summary>Enabled by a low on EN rather than a high, as most bus parts are.</summary>
    [ObservableProperty]
    public partial bool EnableActiveLow { get; set; }

    public override string ComponentType => "Tri-State Buffer";

    public override string ValueLabel => IsEnabled ? "driving" : "Hi-Z";

    /// <summary>True while the output is being driven rather than released.</summary>
    public bool IsEnabled => GetOutputState(0) is not LogicState.HighImpedance;

    /// <summary>Current out of the output pin at the last solved point, in amps.</summary>
    public double Current { get; private set; }

    /// <summary>True when the output is driven and fighting something else on its net.</summary>
    public bool IsContending { get; private set; }

    /// <summary>What is wrong with how this part is being run, if anything.</summary>
    public IReadOnlyList<string> Violations => IsContending
        ?
        [
            $"its output is being pulled away from the level it is driving, with {Math.Abs(Current) * 1000:0} mA " +
            "flowing — another output on the net is enabled and disagrees, or the load is far too heavy",
        ]
        : [];

    public override void EvaluateLogic(IDigitalContext context)
    {
        var enable = context.ReadInput(Enable, Levels);
        var on = EnableActiveLow ? enable.IsLow() : enable.IsHigh();

        LogicState state;

        if (on)
        {
            var a = context.ReadInput(In, Levels);
            state = a.IsDriven() ? a : LogicState.Unknown;
        }
        else
        {
            state = enable.IsDriven() ? LogicState.HighImpedance : LogicState.Unknown;
        }

        context.Schedule(this, 0, state, DelayFor(context));
    }

    public override void StampMatrix(Cirq.Core.Simulation.MnaSystem system, Cirq.Core.Simulation.SimulationState state)
    {
        base.StampMatrix(system, state);

        // A released output still leaks, as an input does. Without it a bus whose every driver has
        // let go would be a node with nothing at all holding it.
        if (GetOutputState(0) is LogicState.HighImpedance)
            system.StampConductance(system.Node(Out), Cirq.Core.Topology.Netlist.GroundIndex, 1.0 / Levels.InputResistance);
    }

    public override void CommitTimeStep(Cirq.Core.Simulation.MnaSystem system, Cirq.Core.Simulation.SimulationState state)
    {
        var driving = GetOutputState(0);
        Current = driving is LogicState.HighImpedance ? 0 : OutputCurrent(system, 0);

        // Judged by where the wire actually is rather than by the current, because a heavy load and
        // a second driver both draw current and only one of them is a fight. Driven high and held
        // down into the forbidden band, or the other way round, and something is pulling against it.
        var pulled = Math.Abs(system.NodeVoltage(Out) - Levels.VoltageFor(driving));

        IsContending = driving.IsDriven() && pulled > 0.35 * (Levels.Voh - Levels.Vol);
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        Current = 0;
        IsContending = false;
    }

    partial void OnEnableActiveLowChanged(bool value) => NotifyValueChanged();
}
