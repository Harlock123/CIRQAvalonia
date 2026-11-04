namespace Cirq.Core.Topology;

/// <summary>
/// The resolved electrical topology of a circuit: every terminal mapped to a node index, with
/// ground fixed at index <c>-1</c>.
/// </summary>
public sealed class Netlist
{
    public const int GroundIndex = -1;

    private readonly Dictionary<Terminal, Net> _terminalToNet;

    internal Netlist(IReadOnlyList<Net> nets, Dictionary<Terminal, Net> terminalToNet, Net? groundNet)
    {
        Nets = nets;
        _terminalToNet = terminalToNet;
        GroundNet = groundNet;
    }

    public IReadOnlyList<Net> Nets { get; }

    public Net? GroundNet { get; }

    /// <summary>Number of non-ground nodes, i.e. the size of the MNA conductance block.</summary>
    public int NodeCount => Nets.Count(n => !n.IsGround);

    public Net NetOf(Terminal terminal)
    {
        if (terminal.Cached is { } cached && ReferenceEquals(cached.Owner, this)) return cached.Net;

        if (!_terminalToNet.TryGetValue(terminal, out var net))
            throw new CircuitTopologyException($"Terminal '{terminal}' is not part of this netlist.");

        terminal.Cached = new NetCache(this, net);
        return net;
    }

    /// <summary>Node index for a terminal; <see cref="GroundIndex"/> when it sits on ground.</summary>
    public int IndexOf(Terminal terminal) => NetOf(terminal).Index;

    public bool Contains(Terminal terminal) => _terminalToNet.ContainsKey(terminal);
}
