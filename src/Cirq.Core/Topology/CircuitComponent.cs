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

    /// <summary>
    /// Settings driven by an expression over the circuit's parameters rather than by a typed
    /// number, keyed by property name.
    /// <para>
    /// Empty for almost every part, and for every part in every file written before parameters
    /// existed. A part with an entry here still has an ordinary value in the property itself — the
    /// expression is re-evaluated and written into it whenever the parameters change — so
    /// everything that reads a component, from the solver to the parts list to the file format,
    /// goes on reading a plain number and knows nothing about any of this.
    /// </para>
    /// <para>
    /// That is the whole design: an expression is a way of <i>setting</i> a value, not a second
    /// kind of value. A hundred and eighty-eight component types would otherwise each need to know
    /// that their resistance might be an expression.
    /// </para>
    /// </summary>
    public Dictionary<string, string> Expressions { get; } = [];

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
    /// Contributes whatever the DC stamp cannot express to a small-signal frequency solve:
    /// reactance, and AC excitation.
    /// <para>
    /// Most components need nothing here, and that is the point of the arrangement. A sweep first
    /// stamps every component's ordinary <see cref="StampMatrix"/> into a real matrix at the bias
    /// point — which for a resistor is its conductance, and for a diode or a transistor is exactly
    /// the small-signal slope it linearised to — and folds that into the complex one. What is
    /// left over is only what a real number cannot hold.
    /// </para>
    /// <para>
    /// So a capacitor adds jωC, an inductor adds jωL to the branch diagonal its DC stamp already
    /// put a resistance on, and a source adds whatever AC magnitude it was given. Everything else
    /// inherits the empty implementation and is correct.
    /// </para>
    /// </summary>
    public virtual void StampAc(AcSystem system, SimulationState state) { }

    /// <summary>
    /// True when <see cref="StampAc"/> contributes a plain <c>G + jωC</c> — a constant part and a
    /// part proportional to frequency — which every resistance, capacitance and inductance does.
    /// <para>
    /// It matters to anything that wants the circuit as a <b>finite</b> set of natural
    /// frequencies, because that only exists when the whole system can be written that way. A
    /// delay cannot: <c>e^{-sτ}</c> has infinitely many poles, so a circuit containing one has no
    /// finite list to report and a pole-zero analysis has to decline rather than return the poles
    /// of some other circuit.
    /// </para>
    /// <para>
    /// Declared rather than detected, because detecting it means sampling the stamp at two
    /// frequencies and a short delay is indistinguishable from a small inductance at any frequency
    /// low enough to try — which is exactly how a transmission line got through the numeric check
    /// that was written first.
    /// </para>
    /// </summary>
    public virtual bool HasLinearAcStamp => true;

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

    /// <summary>
    /// Which sheet of the document this is drawn on, or empty for the first one.
    /// <para>
    /// A drawing concern and nothing else. Sheets are pages of one circuit, not separate circuits:
    /// the solver is handed every part on every sheet at once, and two sheets are joined the way
    /// two ends of a large sheet already are, by naming a net rather than drawing a wire to it.
    /// Nothing in the engine knows this property exists.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial string Sheet { get; set; } = string.Empty;

    /// <summary>
    /// What this part is to be built as — <c>Resistor_SMD:R_0805_2012Metric</c>, or whatever the
    /// board library calls it. Empty for the great majority of parts, which are never going to be
    /// built.
    /// <para>
    /// A string rather than a chosen thing from a list, because the list belongs to whatever board
    /// tool is on the other end and this one has no business having an opinion about it. It is
    /// carried rather than used: nothing here simulates a footprint, and the only thing that reads
    /// it is the netlist written out for a board.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial string Footprint { get; set; } = string.Empty;

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
