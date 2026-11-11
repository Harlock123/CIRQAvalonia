using System.Globalization;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Core.Topology;
using Cirq.Core.Units;

namespace Cirq.Components.Protoboard;

/// <summary>What a build step does.</summary>
public enum BuildStepKind { Board, Cut, Link, Part, Wire, Bench, Check }

/// <summary>One step of building the circuit: what to do, in words, and what on the board it is about.</summary>
/// <param name="Number">From 1.</param>
/// <param name="Kind">What sort of step it is.</param>
/// <param name="Text">The instruction.</param>
/// <param name="Part">The part it fits, when it fits one.</param>
/// <param name="Wire">The jumper, link or wire it fits.</param>
/// <param name="Bench">The bench equipment it connects.</param>
/// <param name="Holes">The holes it uses, to pick out on the board.</param>
public sealed record BuildStep(
    int Number, BuildStepKind Kind, string Text, BoardPart? Part = null, BoardWire? Wire = null, BenchPart? Bench = null,
    IReadOnlyList<BoardHole>? Holes = null);

/// <summary>
/// The board as a list of things to do, in the order a person builds it.
/// <para>
/// On breadboards: the jumpers that join the rails, the chips across the channel, the transistors, regulators and modules,
/// the resistors, capacitors, diodes and LEDs, the rest of the jumpers net by net (ground, then the supply, then the
/// signals), and last the supply and instruments on the bench. On stripboard the copper is cut and the wire links fitted
/// first, while the board is bare, then the parts. On perfboard the parts go in and then the wiring on the back. Each
/// step says what is particular about it: a resistor's colour bands, which leg of an LED is which, the stripe on an
/// electrolytic, which way a chip's notch faces and where pin 1 goes, the colour of each wire.
/// </para>
/// </summary>
public static class BuildGuide
{
    /// <summary>The colours the protoboard draws signal wires in, by name, in the same order.</summary>
    private static readonly string[] SignalColours =
        ["yellow", "green", "blue", "white", "purple", "orange", "teal", "brown", "pink", "grey", "lime green", "violet"];

    public static string WireColour(WireRole role, int colour) => role switch
    {
        WireRole.Ground => "black",
        WireRole.Supply => "red",
        WireRole.OtherSupply => "orange",
        _ => SignalColours[colour % SignalColours.Length],
    };

    public static IReadOnlyList<BuildStep> Steps(ProtoboardLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var steps = new List<BuildStep>();
        void Add(BuildStepKind kind, string text, BoardPart? part = null, BoardWire? wire = null, BenchPart? bench = null, IReadOnlyList<BoardHole>? holes = null) =>
            steps.Add(new BuildStep(steps.Count + 1, kind, text, part, wire, bench, holes ?? part?.Holes ?? (wire is null ? [] : [wire.From, wire.To])));

        string Hole(BoardHole h) => layout.HoleName(h);

        switch (layout.Kind)
        {
            case BoardKind.Breadboard:
                Add(BuildStepKind.Board, layout.Boards == 1
                    ? "Set out one full-size breadboard (830 holes), the + rails at the top and bottom edges."
                    : $"Set out {layout.Boards} full-size breadboards one above the other, numbered 1 to {layout.Boards} from the top.");
                break;
            case BoardKind.Stripboard:
                Add(BuildStepKind.Board, $"Cut a piece of stripboard {layout.Columns} strips wide and {layout.Rows} holes long, strips running down: the strips are lettered A to {SolderedLayout.Letters(layout.Columns)} from the left and the holes numbered from the top.");
                foreach (var cut in layout.Cuts.OrderBy(c => c.Column).ThenBy(c => c.Row))
                    Add(BuildStepKind.Cut, $"Cut the strip at {Hole(cut)}, with a spot-face cutter or a drill bit turned by hand, so the copper either side is parted.", holes: [cut]);
                foreach (var link in layout.Wires.OrderBy(w => w.From.Column).ThenBy(w => w.From.Row))
                    Add(BuildStepKind.Link, $"Fit a wire link from {Hole(link.From)} to {Hole(link.To)} ({layout.Caption(link.Net)}).", wire: link);
                break;
            case BoardKind.Pcb or BoardKind.DoubleSidedPcb:
                var traces = layout.Wires.Count(w => w.IsTrace);
                var sides = layout.Layers == 2
                    ? $"double-sided, {traces} trace{(traces == 1 ? "" : "s")} on the two sides and {layout.Vias.Count} via{(layout.Vias.Count == 1 ? "" : "s")}"
                    : $"single-sided, {traces} trace{(traces == 1 ? "" : "s")} on the copper side";
                var pour = layout.Pour.Count > 0 ? ", the spare copper poured as ground" : "";
                Add(BuildStepKind.Board, $"Have the printed circuit board made from its Gerber files (File › Export Gerber Files for a PCB…): {sides}{pour}, {layout.Columns} by {layout.Rows} holes at 2.54 mm. Holes are lettered across and numbered down, as on the drawing.");
                break;
            default:
                Add(BuildStepKind.Board, $"Cut a piece of perfboard {layout.Columns} holes across and {layout.Rows} down; columns are lettered from the left and holes numbered from the top.");
                break;
        }

        // Rail-to-rail jumpers first on breadboards: the supply should reach every rail before anything goes in.
        if (layout.Kind == BoardKind.Breadboard)
            foreach (var wire in layout.Wires.Where(w => Breadboard.IsRail(w.From.Row) && Breadboard.IsRail(w.To.Row)))
                Add(BuildStepKind.Wire, $"A {WireColour(wire.Role, wire.Colour)} wire from {Hole(wire.From)} to {Hole(wire.To)}, joining the rails ({layout.Caption(wire.Net)}).", wire: wire);

        foreach (var part in layout.Parts.Where(p => p.Package.Form == BoardForm.Dip).OrderBy(p => p.Holes[0].Board).ThenBy(p => p.Holes[0].Column))
            Add(BuildStepKind.Part, Dip(part, Hole, layout.Kind), part);
        foreach (var part in layout.Parts.Where(p => p.Package.Form == BoardForm.Inline).OrderBy(p => p.Holes[0].Column))
            Add(BuildStepKind.Part, Inline(part, Hole), part);
        foreach (var part in layout.Parts.Where(p => p.Package.Form == BoardForm.TwoLead).OrderBy(p => p.Holes[0].Board).ThenBy(p => p.Holes.Min(h => h.Column)))
            Add(BuildStepKind.Part, TwoLead(part, Hole), part);

        if (layout.Kind == BoardKind.Breadboard)
        {
            foreach (var wire in layout.Wires.Where(w => !(Breadboard.IsRail(w.From.Row) && Breadboard.IsRail(w.To.Row)))
                         .OrderBy(w => w.Role).ThenBy(w => w.Colour).ThenBy(w => w.From.Column))
                Add(BuildStepKind.Wire, $"A {WireColour(wire.Role, wire.Colour)} wire from {Hole(wire.From)} to {Hole(wire.To)} ({layout.Caption(wire.Net)}).", wire: wire);
        }
        else if (layout.Kind.IsPcb())
        {
            foreach (var jumper in layout.Wires.Where(w => !w.IsTrace).OrderBy(w => w.From.Column))
                Add(BuildStepKind.Link, $"A wire jumper on the component side from {Hole(jumper.From)} to {Hole(jumper.To)} ({layout.Caption(jumper.Net)}): the traces couldn't get through there.", wire: jumper);
        }
        else if (layout.Kind == BoardKind.Perfboard)
        {
            foreach (var net in layout.Wires.GroupBy(w => w.Net).OrderBy(g => g.First().Role).ThenBy(g => g.First().Colour))
                Add(BuildStepKind.Wire, $"On the back, wire {layout.Caption(net.Key)}: {string.Join(", ", net.Select(w => $"{Hole(w.From)} to {Hole(w.To)}"))}.",
                    wire: net.First(), holes: [.. net.SelectMany(w => new[] { w.From, w.To }).Distinct()]);
        }

        foreach (var item in layout.Bench)
        {
            var leads = string.Join(", ", item.Leads.Select(l => $"{l.Terminal.Name} to {Hole(l.To)}"));
            Add(BuildStepKind.Bench, $"Connect {item.Part.Name} ({Describe(item.Part, item.Package)}): {leads}.", bench: item, holes: [.. item.Leads.Select(l => l.To)]);
        }

        Add(BuildStepKind.Check, layout.Matches
            ? "Check it against the schematic, then power it up and run it (F5): the board matches the schematic, net for net."
            : $"Before powering it up: {string.Join(" ", layout.Problems)}");
        return steps;
    }

    private static string Describe(CircuitComponent part, BoardPackage package)
    {
        var what = package.Look switch
        {
            BoardLook.Supply => "the bench supply",
            BoardLook.Generator => "the signal generator",
            BoardLook.DevBoard => "the board",
            _ => part.ComponentType,
        };
        return package.Label.Length > 0 && package.Label != part.Name ? $"{what}, {package.Label}" : what;
    }

    private static string Dip(BoardPart part, Func<BoardHole, string> hole, BoardKind kind)
    {
        var half = part.Holes.Count / 2;
        var where = kind == BoardKind.Breadboard ? "across the channel" : "with each pin on a strip of its own";
        return $"Fit {part.Part.Name}, the {part.Package.Label} ({part.Holes.Count} pins), {where} with its notch to the left: " +
               $"pin 1 in {hole(part.Holes[0])}, pins 1 to {half} along {hole(part.Holes[0])}–{hole(part.Holes[half - 1])} " +
               $"and {half + 1} to {part.Holes.Count} back along {hole(part.Holes[half])}–{hole(part.Holes[^1])}.";
    }

    private static string Inline(BoardPart part, Func<BoardHole, string> hole)
    {
        var pins = string.Join(", ", part.Package.Pins.Select((p, i) => p is null ? null : $"{p.Name} in {hole(part.Holes[i])}").Where(s => s is not null));
        var facing = part.Package.Look switch
        {
            BoardLook.To92 => ", its flat face towards you",
            BoardLook.To220 => ", its metal tab at the back",
            _ => "",
        };
        return $"Fit {part.Part.Name} ({part.Package.Label}){facing}: {pins}.";
    }

    private static string TwoLead(BoardPart part, Func<BoardHole, string> hole)
    {
        var (a, b) = (hole(part.Holes[0]), hole(part.Holes[1]));
        switch (part.Part)
        {
            case Resistor r:
                var bands = ColourCodes.For(r.Resistance, r.Tolerance)?.ToString().Replace(" ", ", ", StringComparison.Ordinal);
                return $"{part.Part.Name}, {r.ValueLabel}{(bands is null ? "" : $" ({bands})")}: from {a} to {b}.";
            case Led led:
                return $"{part.Part.Name} ({led.ValueLabel.Replace("LED ", "", StringComparison.Ordinal).Trim('(', ')')}): the longer leg (anode) in {a}, the shorter (cathode, by the flat on the rim) in {b}.";
            case Diode d:
                return $"{part.Part.Name} ({d.ValueLabel}): anode in {a}, the end with the band (cathode) in {b}.";
            case ElectrolyticCapacitor c:
                return $"{part.Part.Name}, {c.ValueLabel}: the + lead (the longer) in {a}, the − lead (the striped side) in {b}.";
            case Capacitor c:
                return $"{part.Part.Name}, {SiPrefix.Format(c.Capacitance, "F")}: in {a} and {b}.";
            default:
                var value = part.Part.ValueLabel;
                return $"{part.Part.Name} ({(value.Length > 0 ? value : part.Part.ComponentType)}): in {a} and {b}.";
        }
    }

    /// <summary>The steps as plain numbered text.</summary>
    public static string Text(IReadOnlyList<BuildStep> steps) =>
        string.Join(Environment.NewLine, (steps ?? []).Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Number}. {s.Text}")));
}
