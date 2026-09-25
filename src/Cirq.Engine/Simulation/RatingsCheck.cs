using System.Reflection;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;

namespace Cirq.Engine.Simulation;

/// <summary>Which of a part's limits a finding is about.</summary>
public enum RatingKind
{
    /// <summary>Watts it is turning into heat, against what it can shed.</summary>
    Power,

    /// <summary>Where its die is, against the highest it is rated to reach.</summary>
    Temperature,

    /// <summary>The most it had across it, against what it may have.</summary>
    Voltage,

    /// <summary>The most that went through it, against what may.</summary>
    Current,

    /// <summary>
    /// Something the part says about itself that is not a number against a limit — a relay coil
    /// with no flyback diode, a transceiver outside its common-mode range, a cell being asked for
    /// most of its short-circuit current.
    /// </summary>
    Complaint,
}

/// <summary>One limit, and where the part is against it.</summary>
/// <param name="Name">The part's designator.</param>
/// <param name="Type">What kind of part it is.</param>
/// <param name="Kind">Which limit this is.</param>
/// <param name="Actual">Where it got to. NaN for a complaint, which has no number.</param>
/// <param name="Limit">What it may reach. NaN for a complaint.</param>
/// <param name="Unit">What both are measured in.</param>
/// <param name="Message">A complaint's own words, or empty.</param>
public sealed record RatingFinding(
    string Name,
    string Type,
    RatingKind Kind,
    double Actual,
    double Limit,
    string Unit,
    string Message = "")
{
    /// <summary>How much of the limit is being used, or NaN when there is no number.</summary>
    public double Fraction => Limit > 0 && !double.IsNaN(Actual) ? Actual / Limit : double.NaN;

    /// <summary>
    /// How bad it is. A complaint is always <see cref="PowerVerdict.Over"/> — a part only says
    /// something when something is wrong, and there is no fraction of a missing flyback diode.
    /// </summary>
    public PowerVerdict Verdict => Kind == RatingKind.Complaint
        ? PowerVerdict.Over
        : double.IsNaN(Fraction) ? PowerVerdict.Unrated
        : Fraction >= 1.0 ? PowerVerdict.Over
        : Fraction >= PowerRow.Derated ? PowerVerdict.Warm
        : PowerVerdict.Fine;

    /// <summary>The finding as a sentence.</summary>
    public string Describe() => Kind switch
    {
        RatingKind.Complaint => Message,
        RatingKind.Temperature =>
            $"die at {Actual:0.#} °C of {Limit:0} °C",
        _ => $"{SiPrefix.Format(Actual, Unit, 3)} of {SiPrefix.Format(Limit, Unit, 2)} " +
             $"({Fraction * 100:0}%)",
    };

    /// <summary>The words for the kind, for a column that has to be narrow.</summary>
    public string Word => Kind switch
    {
        RatingKind.Power => "power",
        RatingKind.Temperature => "temperature",
        RatingKind.Voltage => "voltage",
        RatingKind.Current => "current",
        _ => "wiring",
    };
}

/// <summary>Everything the circuit has to say about whether its parts are inside what they are sold for.</summary>
/// <param name="Findings">Worst first.</param>
/// <param name="Seconds">How long it was watched for, or zero for the operating point alone.</param>
/// <param name="Checked">How many parts were looked at.</param>
/// <param name="Supplies">
/// What is delivering power, and how much. Collected in the same walk as the limits rather than by
/// a second pass: averaging steps the circuit forward, so two passes would run it twice and end up
/// disagreeing about where it got to.
/// </param>
public sealed record RatingsResult(
    IReadOnlyList<RatingFinding> Findings,
    double Seconds,
    int Checked,
    IReadOnlyList<SupplyRow> Supplies)
{
    public static RatingsResult Empty { get; } = new([], 0, 0, []);

    /// <summary>Total current out of every supply, which is the figure a battery is sized against.</summary>
    public double SuppliedAmps => Supplies.Sum(s => Math.Abs(s.Amps));

    /// <summary>Everything the supplies are putting in.</summary>
    public double SuppliedWatts => Supplies.Sum(s => s.Watts);

    /// <summary>
    /// How long a cell of the given capacity would last at this draw, or null when nothing is being
    /// drawn. Arithmetic and nothing more — see <see cref="PowerBudgetResult.RunsFor"/>.
    /// </summary>
    public TimeSpan? RunsFor(double milliampHours)
    {
        var amps = SuppliedAmps;

        if (milliampHours <= 0 || amps <= 0) return null;

        var hours = milliampHours / 1000.0 / amps;

        return hours > 2_000_000 ? null : TimeSpan.FromHours(hours);
    }

    public IEnumerable<RatingFinding> Over => Findings.Where(f => f.Verdict == PowerVerdict.Over);

    public IEnumerable<RatingFinding> Warm => Findings.Where(f => f.Verdict == PowerVerdict.Warm);

    public bool IsAveraged => Seconds > 0;

    /// <summary>The whole thing in a sentence.</summary>
    public string Summary()
    {
        if (Checked == 0) return "Nothing here has a rating to check.";

        var over = Over.ToList();
        var warm = Warm.ToList();

        if (over.Count > 0)
            return $"{Name(over)} past what {(over.Count == 1 ? "it is" : "they are")} rated for.";

        if (warm.Count > 0)
            return $"Everything is inside its ratings, but {Name(warm)} " +
                   $"{(warm.Count == 1 ? "is" : "are")} over half way.";

        return $"All {Checked} rated parts are comfortably inside every limit.";
    }

    private static string Name(List<RatingFinding> findings)
    {
        var names = findings.Select(f => f.Name).Distinct().ToList();

        return names.Count <= 3
            ? string.Join(", ", names)
            : $"{string.Join(", ", names.Take(3))} and {names.Count - 3} more";
    }
}

/// <summary>
/// Gathers every limit in the circuit into one list.
/// <para>
/// All of this existed already and none of it was anywhere you could see it. Thirty-two component
/// types report their own violations — a relay coil with no flyback, an electrolytic over its
/// volts, a cell being asked for most of its short-circuit current — and those reached exactly two
/// places: a red ring drawn by about a dozen of the symbols, and the hover card. So a part could be
/// complaining and drawing nothing at all, and the only way to find it was to rest the pointer on
/// each of a hundred and eighty-eight in turn.
/// </para>
/// <para>
/// <b>Peaks for the limits, averages for the heat.</b> That distinction is the whole of why this is
/// not simply the power budget with more rows. Watts are thermal: what matters is the average over
/// long enough for the part to warm up, which is why a MOSFET survives a pulse that would destroy
/// it held on. Volts and amps are not — a dielectric breaks down at the instant the peak arrives,
/// and averaging is exactly the wrong thing to do to it. So a run watches the peak of one and the
/// mean of the other, at the same time.
/// </para>
/// </summary>
public sealed class RatingsCheck
{
    private readonly CircuitSimulator _simulator;

    private static readonly Dictionary<Type, PropertyInfo?> ViolationProperties = [];

    public RatingsCheck(CircuitSimulator simulator)
    {
        _simulator = simulator;
    }

    /// <summary>What the circuit says at the point it is solved to now.</summary>
    public RatingsResult At() => Assemble(Snapshot(), 0, SuppliesNow());

    /// <summary>What is delivering power at this instant.</summary>
    private List<SupplyRow> SuppliesNow()
    {
        List<SupplyRow> supplies = [];

        foreach (var part in Flattening.Flatten(_simulator.Circuit.Components))
        {
            var state = DeviceStates.Of(part, _simulator);

            if (state.Delivered is not { } delivered || delivered <= SupplyFloor) continue;
            if (state.Volts is not { } volts || state.Amps is not { } amps) continue;

            supplies.Add(new SupplyRow(part.Name, part.ComponentType, volts, -amps, delivered));
        }

        supplies.Sort((a, b) => b.Watts.CompareTo(a.Watts));

        return supplies;
    }

    /// <summary>Below this a "supply" is the solver's own residue rather than a rail.</summary>
    private const double SupplyFloor = 1e-7;

    /// <summary>
    /// What it says across a run: the worst each part reached, rather than where it happened to be.
    /// <para>
    /// Runs from where the circuit is rather than resetting it, and leaves it where it finishes —
    /// the same bargain a power budget makes, for the same reason.
    /// </para>
    /// </summary>
    public RatingsResult Over(double seconds, CancellationToken cancellationToken = default)
    {
        if (seconds <= 0) return At();

        var parts = Flattening.Flatten(_simulator.Circuit.Components).ToList();

        Dictionary<CircuitComponent, double> peakVolts = [];
        Dictionary<CircuitComponent, double> peakAmps = [];
        Dictionary<CircuitComponent, double> peakCelsius = [];
        Dictionary<CircuitComponent, double> joules = [];
        Dictionary<CircuitComponent, double> deliveredJoules = [];
        Dictionary<CircuitComponent, double> ampSeconds = [];
        Dictionary<CircuitComponent, double> voltSeconds = [];
        Dictionary<CircuitComponent, List<string>> complaints = [];

        var started = _simulator.Time;
        var previous = started;
        var elapsed = 0.0;

        while (elapsed < seconds && !cancellationToken.IsCancellationRequested)
        {
            _simulator.Step();

            var step = _simulator.Time - previous;
            previous = _simulator.Time;

            if (step <= 0) continue;

            foreach (var part in parts)
            {
                var state = DeviceStates.Of(part, _simulator);

                if (state.Volts is { } volts) Peak(peakVolts, part, Math.Abs(volts));
                if (state.Amps is { } amps) Peak(peakAmps, part, Math.Abs(amps));
                if (state.Celsius is { } celsius) Peak(peakCelsius, part, celsius);

                // Integrated, not peaked: this one is thermal.
                if (state.Watts is { } watts && !double.IsNaN(watts)) Add(joules, part, watts * step);

                // And what the supplies put in, which is a mean like the heat rather than a peak
                // like the ratings: what a rail costs to run is what it costs on average.
                if (state.Delivered is { } delivered) Add(deliveredJoules, part, delivered * step);
                if (state.Amps is { } signed) Add(ampSeconds, part, signed * step);
                if (state.Volts is { } across) Add(voltSeconds, part, across * step);

                // A complaint that happened at any point in the run happened, even if the part has
                // stopped saying it by the end. A fuse that blew is not un-blown by the next step.
                foreach (var said in ViolationsOf(part))
                {
                    var list = complaints.TryGetValue(part, out var found) ? found : complaints[part] = [];

                    if (!list.Contains(said)) list.Add(said);
                }
            }

            elapsed = _simulator.Time - started;
        }

        if (elapsed <= 0) return At();

        List<(CircuitComponent Part, DeviceState State, IReadOnlyList<string> Said)> worst = [];

        foreach (var part in parts)
        {
            var watts = joules.TryGetValue(part, out var energy) ? energy / elapsed : (double?)null;

            worst.Add((
                part,
                new DeviceState(
                    peakVolts.TryGetValue(part, out var v) ? v : null,
                    peakAmps.TryGetValue(part, out var a) ? a : null,
                    watts,
                    null,
                    peakCelsius.TryGetValue(part, out var c) ? c : null),
                complaints.TryGetValue(part, out var said) ? said : []));
        }

        List<SupplyRow> supplies = [];

        foreach (var (part, energy) in deliveredJoules)
        {
            var watts = energy / elapsed;
            if (watts <= SupplyFloor) continue;

            supplies.Add(new SupplyRow(
                part.Name, part.ComponentType,
                voltSeconds.GetValueOrDefault(part) / elapsed,
                -ampSeconds.GetValueOrDefault(part) / elapsed,
                watts));
        }

        supplies.Sort((a, b) => b.Watts.CompareTo(a.Watts));

        return Assemble(worst, elapsed, supplies);
    }

    private List<(CircuitComponent Part, DeviceState State, IReadOnlyList<string> Said)> Snapshot()
    {
        List<(CircuitComponent, DeviceState, IReadOnlyList<string>)> rows = [];

        foreach (var part in Flattening.Flatten(_simulator.Circuit.Components))
        {
            var state = DeviceStates.Of(part, _simulator);

            rows.Add((
                part,
                state with
                {
                    Volts = state.Volts is { } v ? Math.Abs(v) : null,
                    Amps = state.Amps is { } a ? Math.Abs(a) : null,
                },
                ViolationsOf(part)));
        }

        return rows;
    }

    private RatingsResult Assemble(
        List<(CircuitComponent Part, DeviceState State, IReadOnlyList<string> Said)> rows,
        double seconds,
        List<SupplyRow> supplies)
    {
        List<RatingFinding> findings = [];

        var rated = 0;

        foreach (var (part, state, said) in rows)
        {
            var any = false;

            if (part is IPowerRated { PowerRating: > 0 } power && state.Watts is { } watts)
            {
                findings.Add(new RatingFinding(
                    part.Name, part.ComponentType, RatingKind.Power, watts, power.PowerRating, "W"));

                any = true;
            }

            if (part is ISelfHeating { IsSelfHeating: true } thermal && state.Celsius is { } celsius)
            {
                findings.Add(new RatingFinding(
                    part.Name, part.ComponentType, RatingKind.Temperature,
                    celsius, thermal.MaximumJunctionTemperature, "°C"));

                any = true;
            }

            if (part is IVoltageRated { VoltageRating: > 0 } rating && state.Volts is { } volts)
            {
                findings.Add(new RatingFinding(
                    part.Name, part.ComponentType, RatingKind.Voltage,
                    volts, rating.VoltageRating, "V"));

                any = true;
            }

            if (part is ICurrentRated { RatedCurrent: > 0 } current && state.Amps is { } amps)
            {
                findings.Add(new RatingFinding(
                    part.Name, part.ComponentType, RatingKind.Current,
                    amps, current.RatedCurrent, "A"));

                any = true;
            }

            foreach (var complaint in said)
            {
                findings.Add(new RatingFinding(
                    part.Name, part.ComponentType, RatingKind.Complaint,
                    double.NaN, double.NaN, string.Empty, complaint));

                any = true;
            }

            if (any) rated++;
        }

        // Worst first, and worst means nearest a limit. A complaint has no fraction and comes
        // first of all, because a part that has said something is a part that has already decided.
        findings.Sort((a, b) => Rank(b).CompareTo(Rank(a)));

        return new RatingsResult(findings, seconds, rated, supplies);
    }

    private static double Rank(RatingFinding finding) =>
        finding.Kind == RatingKind.Complaint ? double.MaxValue
        : double.IsNaN(finding.Fraction) ? -1.0
        : finding.Fraction;

    private static void Peak(Dictionary<CircuitComponent, double> into, CircuitComponent part, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return;

        if (!into.TryGetValue(part, out var was) || value > was) into[part] = value;
    }

    private static void Add(Dictionary<CircuitComponent, double> into, CircuitComponent part, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return;

        into[part] = into.GetValueOrDefault(part) + value;
    }

    /// <summary>
    /// Whatever the part is saying about how it is being used.
    /// <para>
    /// By reflection, because <c>Violations</c> is declared on the thirty-two types that have
    /// something to say rather than on the base class. The alternative is an interface on every one
    /// of them, and the property is already read this way by the hover card — which is the other
    /// place, and until now the only other place, that any of this was visible.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> ViolationsOf(CircuitComponent part)
    {
        var type = part.GetType();

        if (!ViolationProperties.TryGetValue(type, out var property))
        {
            property = type.GetProperty("Violations", BindingFlags.Public | BindingFlags.Instance);

            if (property is not null && !typeof(IEnumerable<string>).IsAssignableFrom(property.PropertyType))
                property = null;

            ViolationProperties[type] = property;
        }

        if (property is null) return [];

        try
        {
            return property.GetValue(part) is IEnumerable<string> found ? [.. found] : [];
        }
        catch (TargetInvocationException)
        {
            return [];
        }
    }
}
