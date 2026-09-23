using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;

namespace Cirq.Components.Explaining;

/// <summary>
/// A schematic with the questions a recogniser asks already answered.
/// <para>
/// Everything here is "what is on this net", "what is on the other end of this part", "is this a
/// rail" — asked over and over by every pattern, and unpleasant to spell out each time against a
/// raw netlist. Built once per explanation and thrown away.
/// </para>
/// </summary>
internal sealed class Sheet
{
    private readonly Netlist _netlist;
    private readonly List<CircuitComponent> _parts;
    private readonly Dictionary<int, List<(CircuitComponent Part, Terminal Pin)>> _onNet = [];

    public Sheet(Circuit circuit)
    {
        _netlist = circuit.BuildNetlist();
        _parts = [.. Cirq.Core.Topology.Flattening.Flatten(circuit.Components)];

        foreach (var part in _parts)
        {
            foreach (var pin in part.Terminals)
            {
                if (!_netlist.Contains(pin)) continue;

                var index = _netlist.IndexOf(pin);

                if (!_onNet.TryGetValue(index, out var list)) _onNet[index] = list = [];

                list.Add((part, pin));
            }
        }
    }

    public IEnumerable<Net> Nets => _netlist.Nets;

    public IEnumerable<T> All<T>() where T : CircuitComponent => _parts.OfType<T>();

    public Net? NetOf(Terminal terminal) =>
        _netlist.Contains(terminal) ? _netlist.NetOf(terminal) : null;

    public bool IsGround(Net net) => net.IsGround;

    /// <summary>How many parts are on a net, counted once each however many pins they have on it.</summary>
    public int Count(Net net) =>
        _onNet.TryGetValue(net.Index, out var list) ? list.Select(e => e.Part).Distinct().Count() : 0;

    /// <summary>Parts of a kind with a pin on a net.</summary>
    public IEnumerable<T> On<T>(Net net) where T : CircuitComponent =>
        _onNet.TryGetValue(net.Index, out var list)
            ? list.Select(e => e.Part).OfType<T>().Distinct()
            : [];

    /// <summary>Parts of a kind with one pin on each of two nets — a part bridging them.</summary>
    public IEnumerable<T> Between<T>(Net one, Net two) where T : CircuitComponent
    {
        if (one.Index == two.Index) yield break;

        foreach (var part in On<T>(one))
        {
            var pins = part.Terminals.Where(t => _netlist.Contains(t)).ToList();

            if (pins.Any(t => _netlist.IndexOf(t) == two.Index)) yield return part;
        }
    }

    /// <summary>The net on a two-terminal part's other end.</summary>
    public Net? Other(TwoTerminalComponent part, Net from)
    {
        var a = NetOf(part.A);
        var b = NetOf(part.B);

        if (a is null || b is null) return null;

        return a.Index == from.Index ? b : b.Index == from.Index ? a : null;
    }

    /// <summary>
    /// The voltage a net is held at by a supply, or null when nothing on it is a supply.
    /// <para>
    /// Only a source's own terminal counts, and only when its other end is on ground. A rail is a
    /// net somebody put a supply on; a net that merely happens to sit at five volts is an answer,
    /// not a rail, and calling it one would turn every divider tap into a supply.
    /// </para>
    /// </summary>
    public double? RailVoltage(Net? net)
    {
        if (net is null || net.IsGround) return null;

        foreach (var (part, pin) in _onNet.GetValueOrDefault(net.Index) ?? [])
        {
            switch (part)
            {
                case DcVoltageSource source:
                {
                    var other = NetOf(ReferenceEquals(pin, source.Positive)
                        ? source.Negative
                        : source.Positive);

                    if (other?.IsGround != true) continue;

                    return ReferenceEquals(pin, source.Positive) ? source.Voltage : -source.Voltage;
                }

                case Battery battery:
                {
                    var other = NetOf(ReferenceEquals(pin, battery.Positive)
                        ? battery.Negative
                        : battery.Positive);

                    if (other?.IsGround != true) continue;

                    return ReferenceEquals(pin, battery.Positive)
                        ? battery.OpenCircuitVoltage
                        : -battery.OpenCircuitVoltage;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Whether anything on a net switches it — a switch, a button, or a logic part. It is what
    /// tells a pull-up from a resistor that merely happens to reach a rail.
    /// </summary>
    public bool HasSwitching(Net net)
    {
        foreach (var (part, _) in _onNet.GetValueOrDefault(net.Index) ?? [])
        {
            if (part is IInteractiveComponent) return true;
            if (part is DigitalComponent) return true;
        }

        return false;
    }
}
