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

    public IReadOnlyList<Terminal> InputTerminals { get; protected set; } = [];

    public IReadOnlyList<Terminal> OutputTerminals { get; protected set; } = [];

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
        OnPropertyChanged(nameof(OutputSummary));
    }

    public override void ResetState() => ResetLogic();

    /// <summary>Delay to use for a transition; zero while the power-on state is being settled.</summary>
    protected double DelayFor(IDigitalContext context) => context.IsInitializing ? 0.0 : PropagationDelay;

    public abstract void EvaluateLogic(IDigitalContext context);

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        for (var i = 0; i < InputTerminals.Count; i++)
            system.StampConductance(system.Node(InputTerminals[i]), ReferenceNode(system), 1.0 / Levels.InputResistance);

        for (var i = 0; i < OutputTerminals.Count; i++)
            StampOutput(system, i);
    }

    /// <summary>
    /// The node input leakage and output drive are referenced to. Discrete gates reference ground;
    /// packaged ICs override this to reference their own GND pin.
    /// </summary>
    protected virtual int ReferenceNode(MnaSystem system) => Netlist.GroundIndex;

    private void StampOutput(MnaSystem system, int index)
    {
        var branch = system.Branch(this, index);
        var node = system.Node(OutputTerminals[index]);
        var reference = ReferenceNode(system);
        var state = _outputStates[index];

        if (state is LogicState.HighImpedance)
        {
            // A released output carries no current. Constraining the branch to zero keeps its row
            // occupied, which is what stops the matrix going singular.
            system.Add(branch, branch, 1.0);
            return;
        }

        system.StampTheveninSource(branch, node, reference, OutputVoltage(state), Levels.OutputResistance);
    }

    /// <summary>Voltage driven for a given output state. Overridden where the supply rail matters.</summary>
    protected virtual double OutputVoltage(LogicState state) => Levels.VoltageFor(state);

    /// <summary>Current sourced by an output pin at the last solved point (positive = sourcing).</summary>
    public double OutputCurrent(MnaSystem system, int index) => -system.BranchCurrent(this, index);
}
