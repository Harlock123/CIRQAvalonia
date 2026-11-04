using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

/// <summary>
/// Base for every event-driven logic device.
/// <para>
/// The analog and digital domains meet here. On the analog side each output pin is stamped as a
/// Thevenin source at the family's VOH/VOL behind its output resistance, and each input pin as a
/// leakage resistance. On the digital side the scheduler samples the solved input voltages,
/// classifies them against VIH/VIL, and queues output transitions one propagation delay later.
/// </para>
/// </summary>
public abstract partial class DigitalComponent : CircuitComponent, IDigitalDevice
{
    private LogicState[] _outputStates = [];

    [ObservableProperty]
    public partial LogicLevels Levels { get; set; } = LogicLevels.Ttl;

    /// <summary>Propagation delay from an input change to the output settling, in seconds.</summary>
    [ObservableProperty]
    public partial double PropagationDelay { get; set; } = 10e-9;

    public IReadOnlyList<Terminal> InputTerminals
    {
        get;
        protected set
        {
            field = value;
            _rows = null;
        }
    } = [];

    public IReadOnlyList<Terminal> OutputTerminals
    {
        get;
        protected set
        {
            field = value;
            _rows = null;
        }
    } = [];

    /// <summary>One auxiliary branch per driven output pin.</summary>
    public override int VoltageSourceCount => OutputTerminals.Count;

    public override string DesignatorPrefix => "U";

    /// <summary>Sets up the pin roles and sizes the output state array.</summary>
    protected void ConfigurePins(IReadOnlyList<Terminal> inputs, IReadOnlyList<Terminal> outputs)
    {
        InputTerminals = inputs;
        OutputTerminals = outputs;
        _outputStates = new LogicState[outputs.Count];
        Array.Fill(_outputStates, LogicState.Low);
    }

    public LogicState GetOutputState(int outputIndex) => _outputStates[outputIndex];

    /// <summary>What each output was last asked to become, so asking again can be skipped.</summary>
    private LogicState?[] _requested = [];

    /// <summary>
    /// Schedules an output as <see cref="IDigitalContext.Schedule"/> does, skipping the call when
    /// the output was last asked for the same state — which the scheduler would ignore anyway, but
    /// only after a dictionary lookup. For a part with dozens of pins evaluated at every time point
    /// (a memory, an interface chip) that lookup was most of what evaluating it cost.
    /// </summary>
    protected void Request(IDigitalContext context, int outputIndex, LogicState state, double delaySeconds)
    {
        if (_requested.Length != _outputStates.Length) _requested = new LogicState?[_outputStates.Length];
        if (_requested[outputIndex] == state) return;

        _requested[outputIndex] = state;
        context.Schedule(this, outputIndex, state, delaySeconds);
    }

    public void ApplyTransition(int outputIndex, LogicState newState)
    {
        _outputStates[outputIndex] = newState;
        OnPropertyChanged(nameof(OutputSummary));
    }

    /// <summary>Compact display of the current output states, e.g. "1011".</summary>
    public string OutputSummary => string.Concat(_outputStates.Select(s => s.ToChar()));

    public virtual void ResetLogic()
    {
        Array.Fill(_outputStates, LogicState.Low);
        Array.Clear(_requested);
        OnPropertyChanged(nameof(OutputSummary));
    }

    public override void ResetState() => ResetLogic();

    /// <summary>Delay to use for a transition; zero while the power-on state is being settled.</summary>
    protected double DelayFor(IDigitalContext context) => context.IsInitializing ? 0.0 : PropagationDelay;

    public abstract void EvaluateLogic(IDigitalContext context);

    /// <summary>
    /// The rows this part stamps into, worked out once per system rather than looked up for every pin
    /// at every time point — which, on a board full of logic, was most of what stamping cost.
    /// </summary>
    private sealed record StampRows(MnaSystem System, int[] Inputs, int[] Outputs, int[] Branches, int Reference);

    private StampRows? _rows;

    private StampRows Rows(MnaSystem system)
    {
        if (_rows is { } rows && ReferenceEquals(rows.System, system)) return rows;

        rows = new StampRows(
            system,
            [.. InputTerminals.Select(system.Node)],
            [.. OutputTerminals.Select(system.Node)],
            [.. Enumerable.Range(0, OutputTerminals.Count).Select(i => system.Branch(this, i))],
            ReferenceNode(system));
        _rows = rows;
        return rows;
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var rows = Rows(system);
        var g = 1.0 / Levels.InputResistance;

        for (var i = 0; i < rows.Inputs.Length; i++)
            system.StampConductance(rows.Inputs[i], rows.Reference, g);

        for (var i = 0; i < rows.Outputs.Length; i++)
            StampOutput(system, rows, i);
    }

    /// <summary>
    /// The node input leakage and output drive are referenced to. Discrete gates reference ground;
    /// packaged ICs override this to reference their own GND pin.
    /// </summary>
    protected virtual int ReferenceNode(MnaSystem system) => Netlist.GroundIndex;

    private void StampOutput(MnaSystem system, StampRows rows, int index)
    {
        var branch = rows.Branches[index];
        var node = rows.Outputs[index];
        var reference = rows.Reference;
        var state = _outputStates[index];

        if (state is LogicState.HighImpedance)
        {
            // A released output carries no current. Constraining the branch to zero keeps its row
            // occupied, which is what stops the matrix going singular.
            system.Add(branch, branch, 1.0);
            return;
        }

        system.StampTheveninSource(branch, node, reference, OutputVoltage(state), OutputImpedance);
    }

    /// <summary>Voltage driven for a given output state. Overridden where the supply rail matters.</summary>
    protected virtual double OutputVoltage(LogicState state) => Levels.VoltageFor(state);

    /// <summary>
    /// Impedance behind a driven output. The logic family's figure suits something driving
    /// another gate; a part built to drive a load rather than a pin overrides it.
    /// </summary>
    protected virtual double OutputImpedance => Levels.OutputResistance;

    /// <summary>Current sourced by an output pin at the last solved point (positive = sourcing).</summary>
    public double OutputCurrent(MnaSystem system, int index) => -system.BranchCurrent(this, index);
}
