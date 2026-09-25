using System.Collections.ObjectModel;
using Cirq.Core.Probing;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Core.Topology;

/// <summary>The editable document: components, the wires between them, and any attached probes.</summary>
public partial class Circuit : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; } = "Untitled circuit";

    public ObservableCollection<CircuitComponent> Components { get; } = [];

    public ObservableCollection<WireSegment> Wires { get; } = [];

    public ObservableCollection<SignalProbe> Probes { get; } = [];

    /// <summary>
    /// What this circuit is supposed to do, written down so it can be checked rather than
    /// remembered. Travels with the circuit, because a requirement that lives in one person's
    /// head is not a requirement.
    /// </summary>
    public ObservableCollection<Cirq.Core.Verification.DesignSpec> Specs { get; } = [];

    /// <summary>
    /// The numbers this circuit gives names to, so that several parts can be set from one place.
    /// <para>
    /// Empty in every circuit that does not use them, which is most. See
    /// <see cref="CircuitParameters"/> for what they are for.
    /// </para>
    /// </summary>
    public ObservableCollection<CircuitParameter> Parameters { get; } = [];

    /// <summary>
    /// What this circuit produced when somebody last said "this is right", kept so that what it
    /// produces next can be held against it.
    /// <para>
    /// Part of the document rather than of the session, for the same reason the requirements are:
    /// a baseline that lives in one person's running application is not a baseline. Empty until
    /// one is taken.
    /// </para>
    /// </summary>
    public Cirq.Core.Probing.TraceBaseline Baseline { get; set; } =
        Cirq.Core.Probing.TraceBaseline.Empty;

    /// <summary>
    /// The temperature everything in this circuit is at, in degrees Celsius. 27 °C is what every
    /// model in the library is characterised at, and what a datasheet means by "room temperature".
    /// <para>
    /// It belongs to the circuit rather than to the application, because it is part of the
    /// experiment: a circuit saved to show what happens at 85 °C should still be at 85 °C when it
    /// is opened again. Every semiconductor junction reads it, so changing it moves diode drops,
    /// transistor gains and leakage together — which is why it is one number rather than a
    /// property on each part.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial double AmbientTemperatureCelsius { get; set; } = 27.0;

    /// <summary>Adds a component, auto-naming it with the next free reference designator.</summary>
    public T Add<T>(T component) where T : CircuitComponent
    {
        if (string.IsNullOrWhiteSpace(component.Name))
            component.Name = NextDesignator(component.DesignatorPrefix);
        Components.Add(component);
        return component;
    }

    public string NextDesignator(string prefix)
    {
        var used = new HashSet<string>(Components.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        for (var i = 1; ; i++)
        {
            var candidate = $"{prefix}{i}";
            if (used.Add(candidate)) return candidate;
        }
    }

    /// <summary>Creates a wire between two terminals.</summary>
    public WireSegment Connect(Terminal source, Terminal target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (source.Equals(target))
            throw new CircuitTopologyException("A wire cannot connect a terminal to itself.");

        var wire = new WireSegment { SourceTerminal = source, TargetTerminal = target };
        Wires.Add(wire);
        return wire;
    }

    /// <summary>Removes a component along with every wire and probe that referenced it.</summary>
    public void Remove(CircuitComponent component)
    {
        var owned = component.Terminals.ToHashSet();
        foreach (var w in Wires.Where(w => owned.Contains(w.SourceTerminal) || owned.Contains(w.TargetTerminal)).ToList())
            Wires.Remove(w);
        foreach (var p in Probes.Where(p => p.TargetTerminal is not null && owned.Contains(p.TargetTerminal)).ToList())
            Probes.Remove(p);
        Components.Remove(component);
    }

    public void Remove(WireSegment wire) => Wires.Remove(wire);

    public void Clear()
    {
        Probes.Clear();
        Specs.Clear();
        Wires.Clear();
        Components.Clear();
    }

    /// <summary>Resolves the current topology into a netlist.</summary>
    public Netlist BuildNetlist() => NetlistBuilder.Build(Components, Wires);
}
