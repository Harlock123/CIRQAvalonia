using Cirq.Core.Simulation;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Core.Topology;

/// <summary>
/// Base type for every placeable device. A component owns its schematic placement, its terminals,
/// and the rules for contributing to the simulation matrices.
/// </summary>
public abstract partial class CircuitComponent : ObservableObject
{
    private IReadOnlyList<Terminal> _terminals = [];

    public Guid Id { get; init; } = Guid.NewGuid();

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double X { get; set; }

    [ObservableProperty]
    public partial double Y { get; set; }

    [ObservableProperty]
    public partial double RotationDegrees { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Short type label shown in the palette and the inspector, e.g. "Resistor".</summary>
    public virtual string ComponentType => GetType().Name;

    /// <summary>The reference designator prefix used when auto-naming, e.g. "R" for resistors.</summary>
    public virtual string DesignatorPrefix => "U";

    public IReadOnlyList<Terminal> Terminals
    {
        get => _terminals;
        init
        {
            _terminals = value;
            foreach (var t in value) t.Owner = this;
        }
    }

    /// <summary>Assigns terminals after construction (used by components that build pins dynamically).</summary>
    protected void SetTerminals(IReadOnlyList<Terminal> terminals)
    {
        _terminals = terminals;
        foreach (var t in terminals) t.Owner = this;
        OnPropertyChanged(nameof(Terminals));
    }

    public Terminal TerminalByName(string name) =>
        Terminals.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"{ComponentType} '{Name}' has no terminal named '{name}'.");

    /// <summary>
    /// Human-readable parameter summary rendered next to the symbol (e.g. "10k", "100nF").
    /// </summary>
    public virtual string ValueLabel => string.Empty;


    // ---- simulation contract -------------------------------------------

    /// <summary>
    /// Contributes this component's contribution to the MNA matrix and right-hand side for the
    /// time point described by <paramref name="state"/>. Non-linear devices linearise about
    /// <see cref="MnaSystem.PreviousSolution"/>.
    /// </summary>
    public abstract void StampMatrix(MnaSystem system, SimulationState state);

    /// <summary>
    /// Number of auxiliary branch-current unknowns this component needs (ideal voltage sources,
    /// inductors, and controlled voltage sources each need one).
    /// </summary>
    public virtual int VoltageSourceCount => 0;

    /// <summary>
    /// Extra node-voltage unknowns the component needs for its internal topology. Macromodels such
    /// as the 741 use these for gain-stage nodes that exist inside the package and are therefore
    /// absent from the drawn netlist.
    /// </summary>
    public virtual int InternalNodeCount => 0;

    /// <summary>True when the component must be iterated by Newton-Raphson.</summary>
    public virtual bool IsNonlinear => false;

    /// <summary>Clears all internal history, returning the component to its power-on state.</summary>
    public virtual void ResetState() { }

    /// <summary>Called before a time point is attempted, so the component can prepare companions.</summary>
    public virtual void BeginTimeStep(SimulationState state) { }

    /// <summary>
    /// Called once a time point has converged and been accepted. Components latch the voltages and
    /// currents their companion models need for the next step here.
    /// </summary>
    public virtual void CommitTimeStep(MnaSystem system, SimulationState state) { }

    /// <summary>
    /// Lets a non-linear device veto convergence even when the solution delta looks small, which
    /// diodes use to reject iterations where the junction voltage is still being limited.
    /// </summary>
    public virtual bool HasConverged(MnaSystem system, SimulationState state) => true;

    /// <summary>Raises change notification for the derived <see cref="ValueLabel"/>.</summary>
    protected void NotifyValueChanged() => OnPropertyChanged(nameof(ValueLabel));
}
