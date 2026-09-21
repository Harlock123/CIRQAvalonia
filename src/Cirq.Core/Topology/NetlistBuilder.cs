namespace Cirq.Core.Topology;

/// <summary>
/// Resolves wires into nets using union-find, then assigns MNA node indices with ground at -1.
/// </summary>
public static class NetlistBuilder
{
    public static Netlist Build(IEnumerable<CircuitComponent> components, IEnumerable<WireSegment> wires)
    {
        var terminals = components.SelectMany(c => c.Terminals).ToList();
        var parent = new Dictionary<Terminal, Terminal>();
        foreach (var t in terminals) parent[t] = t;

        Terminal Find(Terminal t)
        {
            var root = t;
            while (!ReferenceEquals(parent[root], root)) root = parent[root];
            while (!ReferenceEquals(parent[t], root))
            {
                var next = parent[t];
                parent[t] = root;
                t = next;
            }
            return root;
        }

        void Union(Terminal a, Terminal b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (!ReferenceEquals(ra, rb)) parent[ra] = rb;
        }

        var wireList = wires.ToList();
        foreach (var w in wireList)
        {
            if (w.SourceTerminal is null || w.TargetTerminal is null) continue;
            if (!parent.ContainsKey(w.SourceTerminal))
                throw new CircuitTopologyException(
                    $"Wire {w.Id} references terminal '{w.SourceTerminal}' whose component is not in the circuit.");
            if (!parent.ContainsKey(w.TargetTerminal))
                throw new CircuitTopologyException(
                    $"Wire {w.Id} references terminal '{w.TargetTerminal}' whose component is not in the circuit.");
            Union(w.SourceTerminal, w.TargetTerminal);
        }

        // Then the labels. Every terminal carrying the same name is one net, however far apart
        // they are on the page and whether or not a wire runs between them — which is the whole
        // point of naming a net rather than drawing it.
        var named = new Dictionary<string, Terminal>(StringComparer.OrdinalIgnoreCase);

        foreach (var component in components)
        {
            if (component is not INetNaming label) continue;

            var name = label.NetName?.Trim() ?? string.Empty;
            if (name.Length == 0) continue;

            if (!parent.ContainsKey(label.NamedTerminal))
                throw new CircuitTopologyException(
                    $"Net label '{name}' has a terminal whose component is not in the circuit.");

            if (named.TryGetValue(name, out var first)) Union(first, label.NamedTerminal);
            else named[name] = label.NamedTerminal;
        }

        var groups = new Dictionary<Terminal, List<Terminal>>();
        foreach (var t in terminals)
        {
            var root = Find(t);
            if (!groups.TryGetValue(root, out var list)) groups[root] = list = [];
            list.Add(t);
        }

        static bool IsGroundGroup(List<Terminal> group) =>
            group.Any(t => t.Type == TerminalType.Ground || t.Owner is IGroundReference);

        // A net's name is whatever a label on it says. Two different labels on one net is a
        // mistake worth catching, but not here — the netlist's job is to answer what is connected
        // to what, and the electrical rule check is where contradictions are reported.
        static string? NameOf(List<Terminal> group) =>
            group
                .Select(t => t.Owner as INetNaming)
                .FirstOrDefault(l => l is not null && l.NetName.Trim().Length > 0)
                ?.NetName.Trim();

        var nets = new List<Net>();
        var map = new Dictionary<Terminal, Net>();

        var groundTerminals = new List<Terminal>();
        var signalGroups = new List<List<Terminal>>();
        foreach (var group in groups.Values)
        {
            if (IsGroundGroup(group)) groundTerminals.AddRange(group);
            else signalGroups.Add(group);
        }

        // Ground is allocated first so that every other net receives a non-negative index.
        Net? groundNet = null;
        if (groundTerminals.Count > 0)
        {
            groundNet = new Net(
                Guid.NewGuid(), Netlist.GroundIndex, groundTerminals, isGround: true,
                NameOf(groundTerminals));
            nets.Add(groundNet);
            foreach (var t in groundTerminals) map[t] = groundNet;
        }

        var nextIndex = 0;
        foreach (var group in signalGroups)
        {
            var net = new Net(Guid.NewGuid(), nextIndex++, group, isGround: false, NameOf(group));
            nets.Add(net);
            foreach (var t in group) map[t] = net;
        }

        foreach (var w in wireList)
        {
            if (w.SourceTerminal is not null && map.TryGetValue(w.SourceTerminal, out var n)) w.NetId = n.Id;
        }

        return new Netlist(nets, map, groundNet);
    }
}
