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
