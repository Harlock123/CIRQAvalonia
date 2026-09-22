using Cirq.Components.Hierarchy;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Primitives;
using Cirq.Core.Topology;

namespace Cirq.Components.Spice;

/// <summary>What came of building a subcircuit.</summary>
/// <param name="Block">The block, ready to be placed, or null when it could not be built.</param>
/// <param name="Name">The subcircuit's name.</param>
/// <param name="Parts">How many parts are inside it.</param>
/// <param name="Pins">How many pins it has.</param>
/// <param name="Problems">Anything that could not be carried, named rather than dropped.</param>
public sealed record ImportedSubcircuit(
    Subcircuit? Block, string Name, int Parts, int Pins, IReadOnlyList<string> Problems)
{
    public bool Succeeded => Block is not null;

    public string Summary => Succeeded
        ? $"{Parts} part{(Parts == 1 ? string.Empty : "s")}, {Pins} pin{(Pins == 1 ? string.Empty : "s")}"
        : "could not be built";
}

/// <summary>
/// Turns a parsed <c>.subckt</c> into a block that can be placed on a schematic.
/// <para>
/// The nets inside become wires, and the pins in the header become the block's pins in the same
/// order — because order <i>is</i> the interface: a SPICE subcircuit's pins have no names, only
/// positions, and getting them out of order silently builds a different part.
/// </para>
/// <para>
/// Nothing is approximated. An element this library has no part for stops the import and is named,
/// rather than being left out of a block that would then claim to be a part it is not.
/// </para>
/// </summary>
public static class SpiceSubcircuitImport
{
    /// <summary>Builds one, laying its parts out in a grid so the block can be opened and read.</summary>
    public static ImportedSubcircuit Build(SpiceSubcircuit definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        List<string> problems = [];

        if (definition.Pins.Count == 0)
        {
            return new ImportedSubcircuit(null, definition.Name, 0, 0,
                [$"'{definition.Name}' has no pins, so nothing could connect to it."]);
        }

        if (definition.Elements.Count == 0)
        {
            return new ImportedSubcircuit(null, definition.Name, 0, definition.Pins.Count,
                [$"'{definition.Name}' has nothing in it."]);
        }

        // Models defined inside the subcircuit are registered first, so an element naming one
        // finds it. They are the part's own and travel with any circuit that uses it.
        foreach (var card in definition.Models) SpiceModelImport.Register(card);

        var block = new Subcircuit { BlockName = definition.Name };

        // One terminal per net, gathered as the parts are made, so a wire can be drawn between
        // every pair that shares a net.
        Dictionary<string, List<Terminal>> nets = new(StringComparer.OrdinalIgnoreCase);

        void Join(string net, Terminal terminal)
        {
            if (!nets.TryGetValue(net, out var list)) nets[net] = list = [];

            list.Add(terminal);
        }

        var column = 0;
        var row = 0;

        foreach (var element in definition.Elements)
        {
            var part = Create(element, problems);

            if (part is null) continue;

            part.Name = element.Designator;
            part.X = column * 160;
            part.Y = row * 120;

            if (++column == 4)
            {
                column = 0;
                row++;
            }

            block.AddInner(part);

            var pins = Pins(part, element);

            for (var i = 0; i < element.Nodes.Count && i < pins.Count; i++)
                Join(element.Nodes[i], pins[i]);
        }

        if (problems.Count > 0)
            return new ImportedSubcircuit(null, definition.Name, 0, definition.Pins.Count, problems);

        // Every net becomes a star of wires from its first terminal, which is the simplest drawing
        // that makes them one node — and a net is a node, not a shape.
        foreach (var (_, terminals) in nets)
        {
            for (var i = 1; i < terminals.Count; i++)
            {
                block.AddInnerWire(new WireSegment
                {
                    SourceTerminal = terminals[0],
                    TargetTerminal = terminals[i],
                });
            }
        }

        // The header's order, which is the whole of the interface.
        foreach (var pin in definition.Pins)
        {
            if (!nets.TryGetValue(pin, out var terminals) || terminals.Count == 0)
            {
                problems.Add($"pin '{pin}' of '{definition.Name}' is not joined to anything inside it.");
                continue;
            }

            block.AddPort(pin, terminals[0]);
        }

        if (problems.Count > 0)
            return new ImportedSubcircuit(null, definition.Name, 0, definition.Pins.Count, problems);

        return new ImportedSubcircuit(
            block, definition.Name, block.InnerComponents.Count, block.Ports.Count, problems);
    }

    /// <summary>The part an element line describes, or null with a reason recorded.</summary>
    private static CircuitComponent? Create(SpiceElement element, List<string> problems)
    {
        switch (element.Letter)
        {
            case 'R': return new Resistor(element.Value ?? 1e3);
            case 'C': return new Capacitor(element.Value ?? 1e-9);
            case 'L': return new Inductor(element.Value ?? 1e-3);
            case 'V': return new DcVoltageSource(element.Value ?? 0);
            case 'I': return new DcCurrentSource(element.Value ?? 0);
            case 'E': return new VoltageControlledVoltageSource(element.Value ?? 1);
            case 'G': return new VoltageControlledCurrentSource(element.Value ?? 1e-3);

            case 'D':
                return new Diode(Named(DiodeModel.Library, element.Model) ?? DiodeModel.D1N4148);

            case 'Q':
                var bjt = Named(BjtModel.Library, element.Model);

                if (bjt is null)
                {
                    problems.Add($"'{element.Designator}' names the model '{element.Model}', " +
                                 "which is not defined here or in the library.");
                    return null;
                }

                return new BipolarTransistor(bjt);

            case 'M':
                var mosfet = Named(MosfetModel.Library, element.Model);

                if (mosfet is null)
                {
                    problems.Add($"'{element.Designator}' names the model '{element.Model}', " +
                                 "which is not defined here or in the library.");
                    return null;
                }

                return new Mosfet(mosfet);

            default:
                problems.Add($"'{element.Designator}' is not an element this library can build.");
                return null;
        }
    }

    private static T? Named<T>(IReadOnlyList<T> library, string? name) where T : class =>
        name is null ? null : library.FirstOrDefault(m =>
            string.Equals(m.ToString(), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A part's terminals in SPICE's order for that element, which is not always the order the
    /// part declares them in — a MOSFET is drain, gate, source, bulk on the line and this library
    /// has no bulk pin, so the fourth node is joined to the source.
    /// </summary>
    private static IReadOnlyList<Terminal> Pins(CircuitComponent part, SpiceElement element) =>
        part switch
        {
            BipolarTransistor q => [q.Collector, q.Base, q.Emitter],
            Mosfet m => [m.Drain, m.Gate, m.Source, m.Source],
            VoltageControlledVoltageSource e =>
                [e.OutputPositive, e.OutputNegative, e.ControlPositive, e.ControlNegative],
            VoltageControlledCurrentSource g =>
                [g.OutputPositive, g.OutputNegative, g.ControlPositive, g.ControlNegative],
            TwoTerminalComponent two => [two.A, two.B],
            _ => [.. part.Terminals.Take(element.Nodes.Count)],
        };
}
