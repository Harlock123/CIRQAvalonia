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
    /// The pages this drawing is spread over, in order. Empty for a circuit on one sheet, which is
    /// most of them and every circuit written before sheets existed.
    /// <para>
    /// A real design is a power supply page, a logic page and an I/O page rather than one enormous
    /// sheet, and the parts of it that connect do so by name. Blocks give <i>hierarchy</i> — a
    /// section drawn as one symbol — which is a different thing: a block hides its contents, a
    /// sheet just puts them somewhere else.
    /// </para>
    /// </summary>
    public ObservableCollection<string> Sheets { get; } = [];

    /// <summary>
    /// What is drawn on one sheet. Everything, when the document has no sheets — a circuit that has
    /// never been split is one page whatever anything is labelled.
    /// </summary>
    public IEnumerable<CircuitComponent> OnSheet(string? sheet) =>
        Sheets.Count == 0 || sheet is null
            ? Components
            : Components.Where(c => Belongs(c, sheet));

    /// <summary>
    /// Whether a part belongs to a sheet. A part whose sheet is empty, or names one that has since
    /// been removed, is on the first — better somewhere visible than nowhere at all.
    /// </summary>
    public bool Belongs(CircuitComponent component, string sheet)
    {
        ArgumentNullException.ThrowIfNull(component);

        if (Sheets.Count == 0) return true;

        var own = component.Sheet;

        return own.Length == 0 || !Sheets.Contains(own)
            ? sheet == Sheets[0]
            : string.Equals(own, sheet, StringComparison.Ordinal);
    }

    /// <summary>
    /// Adds a page, and returns what it ended up called.
    /// <para>
    /// The first call splits a drawing that has never been split. Everything already on it becomes
    /// page one without being relabelled — a part that names no sheet is on the first — so a
    /// circuit drawn before sheets existed acquires them without a single part moving.
    /// </para>
    /// </summary>
    public string AddSheet(string? name = null)
    {
        if (Sheets.Count == 0) Sheets.Add("Sheet 1");

        var wanted = string.IsNullOrWhiteSpace(name) ? $"Sheet {Sheets.Count + 1}" : name.Trim();
        var chosen = wanted;

        for (var i = 2; Sheets.Any(s => string.Equals(s, chosen, StringComparison.OrdinalIgnoreCase)); i++)
            chosen = $"{wanted} {i}";

        Sheets.Add(chosen);

        return chosen;
    }

    /// <summary>
    /// Renames a page, bringing what is on it along. False when there is no such page, when the
    /// new name is blank, or when something else already has it.
    /// </summary>
    public bool RenameSheet(string from, string to)
    {
        var index = IndexOfSheet(from);
        if (index < 0) return false;

        var wanted = to?.Trim() ?? string.Empty;
        if (wanted.Length == 0) return false;
        if (string.Equals(wanted, Sheets[index], StringComparison.Ordinal)) return true;
        if (Sheets.Any(s => string.Equals(s, wanted, StringComparison.OrdinalIgnoreCase))) return false;

        // Parts name the page they are on, so they follow the name. The ones naming nothing are
        // left alone: they are on the first page by saying nothing, and writing the name into them
        // would turn an implicit arrangement into a permanent one.
        foreach (var component in Components)
            if (string.Equals(component.Sheet, Sheets[index], StringComparison.Ordinal))
                component.Sheet = wanted;

        Sheets[index] = wanted;

        return true;
    }

    /// <summary>
    /// Takes a page out, moving what was on it to the page beside it.
    /// <para>
    /// Deleting the parts would be the other reading, and the wrong one: the pages are a way of
    /// arranging a drawing, so losing one should cost the arrangement rather than the circuit.
    /// Anything that has to go can be selected and deleted, which says so.
    /// </para>
    /// <para>
    /// The last page cannot be removed — a drawing is on at least one.
    /// </para>
    /// </summary>
    public bool RemoveSheet(string name)
    {
        var index = IndexOfSheet(name);
        if (index < 0 || Sheets.Count <= 1) return false;

        var moved = Sheets[index == 0 ? 1 : index - 1];

        foreach (var component in Components.Where(c => Belongs(c, Sheets[index])).ToList())
            component.Sheet = moved;

        Sheets.RemoveAt(index);

        return true;
    }

    private int IndexOfSheet(string? name)
    {
        if (name is null) return -1;

        for (var i = 0; i < Sheets.Count; i++)
            if (string.Equals(Sheets[i], name, StringComparison.Ordinal)) return i;

        return -1;
    }

    /// <summary>
    /// The wires drawn on one sheet.
    /// <para>
    /// A wire has no sheet of its own: it is on the page its ends are on. That cannot disagree with
    /// itself, because a wire is drawn by clicking two pins and only one page's pins are on screen
    /// — and it means splitting a drawing into sheets never has to move the wires, only the parts.
    /// A part connects to another page by name, which is what net labels are for.
    /// </para>
    /// </summary>
    public IEnumerable<WireSegment> WiresOn(string? sheet) =>
        Sheets.Count == 0 || sheet is null
            ? Wires
            : Wires.Where(w => w.SourceTerminal?.Owner is { } owner && Belongs(owner, sheet));

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

    /// <summary>
    /// Whether the solver may choose its own time step for this circuit: shorter where the waveform
    /// bends, longer where it does not, and landing on the instant a part switches.
    /// <para>
    /// Part of the document for the same reason the temperature is. It is not a preference about the
    /// application, it is a statement about this circuit — a converter whose oscillator has to be
    /// caught at the right instant needs it, and a circuit somebody is reading step by step does not
    /// want it. Off by default, because a fixed step gives the same answer every run and that is
    /// worth a great deal in something whose results people check by eye.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial bool AdaptiveTimeStep { get; set; }

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

    /// <summary>
    /// Empties the document — everything saved with it, not only the parts. A page arrangement, a
    /// set of named numbers or a recorded baseline left behind from the last circuit would belong
    /// to nothing.
    /// </summary>
    public void Clear()
    {
        Probes.Clear();
        Specs.Clear();
        Wires.Clear();
        Components.Clear();
        Parameters.Clear();
        Sheets.Clear();
        Baseline = Cirq.Core.Probing.TraceBaseline.Empty;

        // The conditions go back to the defaults as well. A new drawing inheriting the last one's
        // 85 °C would be a new drawing quietly running somewhere nobody asked for.
        AmbientTemperatureCelsius = 27.0;
        AdaptiveTimeStep = false;
    }

    /// <summary>Resolves the current topology into a netlist.</summary>
    public Netlist BuildNetlist() => NetlistBuilder.Build(Components, Wires);
}
