using Cirq.Core.Simulation;
using Cirq.Core.Topology;

namespace Cirq.Engine.Simulation;

/// <summary>How a part is doing against what it is rated for.</summary>
public enum PowerVerdict
{
    /// <summary>Nothing was said about what it can take, so nothing can be said about this.</summary>
    Unrated,

    /// <summary>Comfortably inside it.</summary>
    Fine,

    /// <summary>Inside it, but not by the margin anybody designs for.</summary>
    Warm,

    /// <summary>Past it. This is the row the whole report exists to put in front of somebody.</summary>
    Over,
}

/// <summary>One part's line in the budget.</summary>
/// <param name="Name">Its designator.</param>
/// <param name="Type">What kind of part it is.</param>
/// <param name="Watts">What it is turning into heat.</param>
/// <param name="Rating">What it is rated to turn into heat, or zero when unstated.</param>
/// <param name="Celsius">Where its die is, when it models that.</param>
/// <param name="MaximumCelsius">The highest that die is rated to reach.</param>
public sealed record PowerRow(
    string Name,
    string Type,
    double Watts,
    double Rating,
    double? Celsius = null,
    double? MaximumCelsius = null)
{
    /// <summary>
    /// How much of its rating it is using, as a fraction. NaN when it has no rating, which is not
    /// the same as zero and must not print as a percentage.
    /// </summary>
    public double Fraction => Rating > 0 ? Watts / Rating : double.NaN;

    /// <summary>
    /// Where the die is as a fraction of the way to its limit, or NaN when it is not modelled.
    /// <para>
    /// Measured from the ambient rather than from absolute zero, because the rise is the part the
    /// design controls. A junction at 60 °C in a 25 °C room with a 150 °C limit has used a quarter
    /// of what it has, not two fifths.
    /// </para>
    /// </summary>
    public double ThermalFraction(double ambientCelsius) =>
        Celsius is { } die && MaximumCelsius is { } limit && limit > ambientCelsius
            ? (die - ambientCelsius) / (limit - ambientCelsius)
            : double.NaN;

    /// <summary>
    /// The verdict, taken from whichever of the two limits is closer to being reached.
    /// <para>
    /// Both, because they are different limits and a part can be nowhere near one and past the
    /// other. A TO-220 regulator dropping nine volts at half an amp is inside its package rating
    /// and well past what the heatsink it does not have can carry away.
    /// </para>
    /// </summary>
    public PowerVerdict VerdictAt(double ambientCelsius)
    {
        var worst = Worst(Fraction, ThermalFraction(ambientCelsius));

        if (double.IsNaN(worst)) return PowerVerdict.Unrated;

        return worst >= 1.0 ? PowerVerdict.Over
            : worst >= Derated ? PowerVerdict.Warm
            : PowerVerdict.Fine;
    }

    private static double Worst(double a, double b) =>
        double.IsNaN(a) ? b : double.IsNaN(b) ? a : Math.Max(a, b);

    /// <summary>
    /// Where "inside its rating" stops being reassuring. Half is the number the trade has used for
    /// as long as there have been resistors: a part run at its full rating is a part at the
    /// temperature its rating was measured at, in free air, which is not where it is.
    /// </summary>
    public const double Derated = 0.5;
}

/// <summary>One supply, and what it is being asked for.</summary>
/// <param name="Name">Its designator.</param>
/// <param name="Type">What kind of source it is.</param>
/// <param name="Volts">What it is holding its output at.</param>
/// <param name="Amps">What is being drawn out of it.</param>
/// <param name="Watts">The product of the two.</param>
public sealed record SupplyRow(string Name, string Type, double Volts, double Amps, double Watts);

/// <summary>What the circuit costs to run, and what it costs the parts that run it.</summary>
/// <param name="Parts">Everything that dissipates, worst first.</param>
/// <param name="Supplies">Everything delivering power.</param>
/// <param name="Seconds">
/// How long the figures were averaged over, or zero when they are the instant the circuit happens
/// to be at.
/// </param>
/// <param name="AmbientCelsius">The room the parts are in, which the die limits are measured from.</param>
public sealed record PowerBudgetResult(
    IReadOnlyList<PowerRow> Parts,
    IReadOnlyList<SupplyRow> Supplies,
    double Seconds,
    double AmbientCelsius)
{
    public static PowerBudgetResult Empty { get; } = new([], [], 0, 25.0);

    /// <summary>Everything the parts are turning into heat.</summary>
    public double DissipatedWatts => Parts.Sum(p => p.Watts);

    /// <summary>Everything the supplies are putting in.</summary>
    public double SuppliedWatts => Supplies.Sum(s => s.Watts);

    /// <summary>Total current out of every supply, which is the figure a battery is sized against.</summary>
    public double SuppliedAmps => Supplies.Sum(s => Math.Abs(s.Amps));

    /// <summary>The parts that are past a limit, worst first.</summary>
    public IEnumerable<PowerRow> Over =>
        Parts.Where(p => p.VerdictAt(AmbientCelsius) == PowerVerdict.Over);

    /// <summary>The ones that are inside every limit but not by much.</summary>
    public IEnumerable<PowerRow> Warm =>
        Parts.Where(p => p.VerdictAt(AmbientCelsius) == PowerVerdict.Warm);

    /// <summary>True when the figures are an average rather than one instant.</summary>
    public bool IsAveraged => Seconds > 0;

    /// <summary>
    /// How long a cell of the given capacity would last at this draw, or null when nothing is
    /// being drawn.
    /// <para>
    /// An arithmetic answer and nothing more: real cells deliver less than their rating at high
    /// currents, less again when cold, and a circuit that sleeps between bursts draws nothing like
    /// its running current on average. It is the right order of magnitude and the wrong number to
    /// put in a specification.
    /// </para>
    /// </summary>
    public TimeSpan? RunsFor(double milliampHours)
    {
        var amps = SuppliedAmps;

        if (milliampHours <= 0 || amps <= 0) return null;

        var hours = milliampHours / 1000.0 / amps;

        // Beyond a few centuries the answer has stopped meaning anything, and TimeSpan overflows
        // at about 29 000 years — which a nanoamp sleep current reaches easily.
        return hours > 2_000_000 ? null : TimeSpan.FromHours(hours);
    }

    /// <summary>The whole thing in a sentence, which is what somebody reads first.</summary>
    public string Summary()
    {
        if (Parts.Count == 0 && Supplies.Count == 0) return "Nothing in this circuit dissipates or supplies anything.";

        var over = Over.ToList();
        var warm = Warm.ToList();

        var draw = Supplies.Count == 0
            ? $"{Format(DissipatedWatts, "W")} dissipated"
            : $"{Format(SuppliedWatts, "W")} in, {Format(DissipatedWatts, "W")} accounted for";

        if (over.Count > 0)
            return $"{Name(over)} past {(over.Count == 1 ? "its rating" : "their ratings")} — {draw}.";

        if (warm.Count > 0)
            return $"{Name(warm)} over half {(warm.Count == 1 ? "its rating" : "their ratings")} — {draw}.";

        return $"Every rated part is inside half its rating — {draw}.";
    }

    private static string Name(List<PowerRow> rows) =>
        rows.Count <= 3
            ? string.Join(", ", rows.Select(r => r.Name))
            : $"{string.Join(", ", rows.Take(3).Select(r => r.Name))} and {rows.Count - 3} more";

    private static string Format(double value, string unit) =>
        Core.Units.SiPrefix.Format(value, unit, 3);
}

/// <summary>
/// What every part is dissipating, against what it is rated for.
/// <para>
/// The solver has always known this and nothing has ever asked it. A semiconductor is the part of
/// a circuit people worry about thermally, and the library already models that: a MOSFET reports
/// its own watts and its own die temperature and complains when it is past them. A resistor sitting
/// at nine tenths of a watt in a quarter-watt package says nothing at all, solves perfectly, and is
/// the reason a working breadboard becomes a smell.
/// </para>
/// <para>
/// <b>Averaging is not optional for a switching circuit.</b> The power in a part that switches is a
/// function of time, and the instant the solver happens to be sitting at is not a summary of it: a
/// MOSFET caught between states is dissipating a great deal more than it does on average, and one
/// caught hard on is dissipating a great deal less. <see cref="Over"/> integrates across a run and
/// divides, which is the figure a heatsink is chosen against. <see cref="At"/> is the answer at the
/// bias point, which is the right one for a circuit that does not switch and a trap for one that
/// does — so the result says which it is and the window says so too.
/// </para>
/// </summary>
public sealed class PowerBudget
{
    private readonly CircuitSimulator _simulator;

    public PowerBudget(CircuitSimulator simulator)
    {
        _simulator = simulator;
    }

    /// <summary>
    /// The budget at the point the circuit is solved to now. Nothing is run and nothing is
    /// disturbed; this is a reading of the matrix as it stands.
    /// </summary>
    public PowerBudgetResult At()
    {
        List<PowerRow> parts = [];
        List<SupplyRow> supplies = [];

        foreach (var part in Flattening.Flatten(_simulator.Circuit.Components))
        {
            var state = DeviceStates.Of(part, _simulator);

            if (state.Watts is { } watts)
                parts.Add(Row(part, watts, state.Celsius));

            if (Supplying(state))
                supplies.Add(new SupplyRow(
                    part.Name, part.ComponentType,
                    state.Volts!.Value, -state.Amps!.Value, state.Delivered!.Value));
        }

        return Assemble(parts, supplies, 0);
    }

    /// <summary>
    /// The budget averaged across a run, which is the only honest one for a circuit that switches.
    /// <para>
    /// Runs from where the circuit is now rather than resetting it: a budget taken across the first
    /// microseconds after power-up is a budget of the inrush, and what anybody wants is the steady
    /// state. Everything the run disturbs — the time, the probe histories — is the caller's to
    /// restore, exactly as it is for a stepped transient.
    /// </para>
    /// </summary>
    /// <param name="seconds">How long to average over. Several cycles of whatever the circuit does.</param>
    /// <param name="cancellationToken">Stops early; what has been integrated so far is returned.</param>
    public PowerBudgetResult Over(double seconds, CancellationToken cancellationToken = default)
    {
        if (seconds <= 0) return At();

        var components = Flattening.Flatten(_simulator.Circuit.Components).ToList();

        // Joules, not watts, until the end. Integrating and then dividing is what makes this an
        // average rather than a sample: adding watts up and dividing by the count would weight a
        // step the solver took in a nanosecond exactly as heavily as one it took in a microsecond,
        // and near a switching edge those differ by three orders of magnitude.
        Dictionary<CircuitComponent, double> joules = [];
        Dictionary<CircuitComponent, double> ampSeconds = [];
        Dictionary<CircuitComponent, double> voltSeconds = [];
        Dictionary<CircuitComponent, double> deliveredJoules = [];
        Dictionary<CircuitComponent, double?> die = [];

        var started = _simulator.Time;
        var previous = started;
        var elapsed = 0.0;

        while (elapsed < seconds && !cancellationToken.IsCancellationRequested)
        {
            _simulator.Step();

            var step = _simulator.Time - previous;
            previous = _simulator.Time;

            if (step <= 0) continue;

            foreach (var part in components)
            {
                var state = DeviceStates.Of(part, _simulator);

                if (state.Watts is { } watts) Add(joules, part, watts * step);
                if (state.Delivered is { } delivered) Add(deliveredJoules, part, delivered * step);
                if (state.Amps is { } amps) Add(ampSeconds, part, amps * step);
                if (state.Volts is { } volts) Add(voltSeconds, part, volts * step);

                // The die is a state variable rather than something to average: what matters is
                // where it got to, and it got hottest at the end of a run that was heating it.
                if (state.Celsius is { } celsius)
                    die[part] = Math.Max(die.GetValueOrDefault(part) ?? double.MinValue, celsius);
            }

            elapsed = _simulator.Time - started;
        }

        if (elapsed <= 0) return At();

        List<PowerRow> parts = [];
        List<SupplyRow> supplies = [];

        foreach (var part in components)
        {
            if (joules.TryGetValue(part, out var energy))
                parts.Add(Row(part, energy / elapsed, die.GetValueOrDefault(part)));

            if (!deliveredJoules.TryGetValue(part, out var deliveredEnergy)) continue;

            var watts = deliveredEnergy / elapsed;
            if (watts <= SupplyFloor) continue;

            supplies.Add(new SupplyRow(
                part.Name, part.ComponentType,
                voltSeconds.GetValueOrDefault(part) / elapsed,
                -ampSeconds.GetValueOrDefault(part) / elapsed,
                watts));
        }

        return Assemble(parts, supplies, elapsed);
    }

    private static void Add(Dictionary<CircuitComponent, double> into, CircuitComponent part, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return;

        into[part] = into.GetValueOrDefault(part) + value;
    }

    /// <summary>
    /// True when the part is putting power into the circuit rather than taking it out.
    /// <para>
    /// Decided by the sign of what it delivers rather than by what type it is, which is the only
    /// test that holds up: a battery being charged is a load, a regulator is both at once, and a
    /// motor that is being turned by the thing it was driving is a generator regardless of what it
    /// says on the symbol.
    /// </para>
    /// </summary>
    private static bool Supplying(DeviceState state) =>
        state.Delivered is { } delivered && delivered > SupplyFloor
        && state.Volts is not null && state.Amps is not null;

    /// <summary>
    /// Below this a "supply" is the solver's own residue. A tenth of a microwatt is not a power
    /// rail, and listing thirty of them buries the two that are.
    /// </summary>
    private const double SupplyFloor = 1e-7;

    private PowerRow Row(CircuitComponent part, double watts, double? celsius) => new(
        part.Name,
        part.ComponentType,
        watts,
        part is IPowerRated rated ? rated.PowerRating : 0.0,
        celsius,
        part is ISelfHeating { IsSelfHeating: true } thermal ? thermal.MaximumJunctionTemperature : null);

    private PowerBudgetResult Assemble(List<PowerRow> parts, List<SupplyRow> supplies, double seconds)
    {
        var ambient = _simulator.Settings.TemperatureKelvin - 273.15;

        // Worst first, and "worst" means closest to a limit rather than hottest: a half-watt in a
        // five-watt package is a part doing its job, and eighty milliwatts in a hundred-milliwatt
        // one is the part about to fail. Unrated parts sort by raw dissipation below the rest,
        // since there is nothing to compare them against.
        parts.Sort((a, b) =>
        {
            var rank = Rank(b, ambient).CompareTo(Rank(a, ambient));

            return rank != 0 ? rank : b.Watts.CompareTo(a.Watts);
        });

        supplies.Sort((a, b) => b.Watts.CompareTo(a.Watts));

        return new PowerBudgetResult(parts, supplies, seconds, ambient);
    }

    private static double Rank(PowerRow row, double ambient)
    {
        var electrical = row.Fraction;
        var thermal = row.ThermalFraction(ambient);

        var worst = double.IsNaN(electrical) ? thermal : double.IsNaN(thermal) ? electrical
            : Math.Max(electrical, thermal);

        return double.IsNaN(worst) ? -1.0 : worst;
    }
}
