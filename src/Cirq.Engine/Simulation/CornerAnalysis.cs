using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Numerics;

namespace Cirq.Engine.Simulation;

/// <summary>What a corner analysis was asked for.</summary>
/// <param name="Probe">The probe whose extremes are being hunted. Null takes the first.</param>
/// <param name="Over">
/// A temperature range to include as one more axis, or null to hold the circuit at the temperature
/// it is set to.
/// <para>
/// Worth asking for separately rather than always doing, because it is a different question. "The
/// worst this can be off the parts bin" and "the worst this can be off the parts bin anywhere in
/// the car" are both real, and the second needs a range somebody has decided on.
/// </para>
/// </param>
public sealed record CornerRequest(SignalProbe? Probe = null, TemperatureRange? Over = null);

/// <summary>The two ends of a temperature range, in degrees Celsius.</summary>
public sealed record TemperatureRange(double ColdCelsius, double HotCelsius)
{
    /// <summary>The commercial and industrial band most parts are specified over.</summary>
    public static TemperatureRange Industrial { get; } = new(-40, 85);

    public bool IsEmpty => Math.Abs(HotCelsius - ColdCelsius) < 1e-9;
}

/// <summary>Which way a part was pushed at a corner.</summary>
public enum CornerDirection
{
    /// <summary>At the bottom of its tolerance band.</summary>
    Low,

    /// <summary>At the top of it.</summary>
    High,
}

/// <summary>One part's place in a corner.</summary>
/// <param name="Name">The part.</param>
/// <param name="Direction">Which end of its band it was put at.</param>
/// <param name="Value">What that works out to.</param>
public sealed record CornerSetting(string Name, CornerDirection Direction, double Value)
{
    public override string ToString() =>
        $"{Name} {(Direction == CornerDirection.High ? "high" : "low")}";
}

/// <summary>One extreme, and the combination of parts that produced it.</summary>
/// <param name="Value">What the probe read there.</param>
/// <param name="Settings">Every varied part and which way it went.</param>
public sealed record Corner(double Value, IReadOnlyList<CornerSetting> Settings)
{
    /// <summary>The combination, written the way somebody would say it out loud.</summary>
    public string Describe() => Settings.Count == 0
        ? "nothing varied"
        : string.Join(", ", Settings);
}

/// <summary>What a corner analysis found.</summary>
/// <param name="Label">The probe it measured.</param>
/// <param name="Nominal">What it reads with every part at its marked value.</param>
/// <param name="Lowest">The worst case downwards, and how to get there.</param>
/// <param name="Highest">The worst case upwards.</param>
/// <param name="Varied">How many parts have a tolerance.</param>
/// <param name="Solves">How many circuits were solved to find out.</param>
/// <param name="Problem">Why the answer should not be believed, or null when it can be.</param>
public sealed record CornerResult(
    string Label,
    double Nominal,
    Corner? Lowest,
    Corner? Highest,
    int Varied,
    int Solves,
    string? Problem = null)
{
    public bool IsUsable => Problem is null && Lowest is not null && Highest is not null;

    /// <summary>The whole spread, as a fraction of nominal.</summary>
    public double Spread => Math.Abs(Nominal) < 1e-15 || !IsUsable
        ? 0
        : (Highest!.Value - Lowest!.Value) / Math.Abs(Nominal);

    /// <summary>The widest departure from nominal either way, as a fraction.</summary>
    public double WorstFractionalError => Math.Abs(Nominal) < 1e-15 || !IsUsable
        ? 0
        : Math.Max(Math.Abs(Highest!.Value - Nominal), Math.Abs(Lowest!.Value - Nominal))
          / Math.Abs(Nominal);

    public static CornerResult Unusable(string problem) => new(string.Empty, 0, null, null, 0, 0, problem);
}

/// <summary>
/// The worst the circuit can ever be, rather than the worst four hundred random builds happened
/// to be.
/// <para>
/// Tolerance analysis samples: it builds the circuit many times from parts drawn at random and
/// reports the spread. That answers "what will most of them do". It cannot answer "what is the
/// worst this can <i>ever</i> be", because the corner where every part is at its extreme in the
/// same direction is one combination out of 2ⁿ, and random sampling essentially never lands on it.
/// A thousand trials of ten parts explores a thousandth of the space.
/// </para>
/// <para>
/// This finds the corner directly. Each part is nudged in turn to see which way it pushes the
/// answer — that is n extra solves, not 2ⁿ — and then all of them are put at the end of their
/// bands that pushes the same way. Two solves later you have both extremes and the recipe for
/// each, which is what a specification is written from.
/// </para>
/// <para>
/// The assumption this makes, stated plainly: it assumes the answer moves <b>monotonically</b>
/// with each part. That is true of almost every circuit and every part in it — more resistance
/// here means more output, always — and it is false where a circuit has an internal maximum, such
/// as a matched pair or a tuned load. Where it is false the corner found is still a real
/// combination and still a real answer; it is simply not guaranteed to be the worst one, and the
/// tolerance analysis is the better tool.
/// </para>
/// </summary>
public sealed class CornerAnalysis
{
    private readonly Circuit _circuit;

    public CornerAnalysis(Circuit circuit)
    {
        _circuit = circuit;
    }

    /// <summary>Finds both extremes, putting every varied part back afterwards.</summary>
    public CornerResult Run(CornerRequest? request = null, CancellationToken cancellationToken = default)
    {
        request ??= new CornerRequest();

        var targets = MonteCarlo.Targets(_circuit).ToList();
        var varying = request.Over is { IsEmpty: false };

        if (targets.Count == 0 && !varying)
        {
            return CornerResult.Unusable(
                "No part in this circuit has a tolerance, and no temperature range was given. Set " +
                "a tolerance on a resistor, capacitor or inductor in the properties panel — they " +
                "default to exact — or ask for a range.");
        }

        var probe = request.Probe ?? _circuit.Probes.FirstOrDefault();

        if (probe is null)
            return CornerResult.Unusable("Nothing to measure — put a probe on the node you care about.");

        // Started from the circuit's own ambient rather than from the default: a circuit saved to
        // run at 85 °C has its corners at 85 °C, and finding them at 27 would be answering about a
        // different circuit.
        var settings = new SimulationSettings
        {
            TemperatureKelvin = _circuit.AmbientTemperatureCelsius + 273.15,
        };

        var simulator = new CircuitSimulator(_circuit, settings);

        simulator.Reset();
        simulator.SolveOperatingPoint();
        simulator.ResolveProbes();

        var solves = 0;

        double? Read()
        {
            solves++;

            simulator.System.ResetSolution();

            try
            {
                simulator.SolveOperatingPoint();
                return simulator.SampleProbe(probe);
            }
            catch (Exception ex) when (ex is ConvergenceException or SingularMatrixException)
            {
                return null;
            }
        }

        var nominal = Read();

        if (nominal is null)
            return CornerResult.Unusable("The circuit does not solve at its marked values.");

        // Every part with a tolerance, and the temperature if a range was asked for, as the same
        // kind of thing: something with a low end, a high end and a value in the middle. The rest
        // of the analysis then has one case to think about instead of two.
        List<Axis> axes = [.. targets.Select(Axis.For)];

        if (request.Over is { IsEmpty: false } range)
        {
            var was = settings.TemperatureKelvin - 273.15;

            axes.Add(new Axis(
                "temperature",
                Math.Min(range.ColdCelsius, range.HotCelsius),
                Math.Max(range.ColdCelsius, range.HotCelsius),
                was,
                celsius => settings.TemperatureKelvin = celsius + 273.15));
        }

        try
        {
            // Which way each axis pushes, one at a time. n solves rather than 2ⁿ, and the only
            // thing that makes a corner analysis affordable on a circuit with twenty parts in it.
            List<(Axis Axis, int Sign)> pushes = [];

            foreach (var axis in axes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                axis.Set(axis.High);

                var raised = Read();

                axis.Set(axis.Nominal);

                // Something the answer does not depend on is left where it was: putting it at an
                // extreme would be noise in the recipe rather than part of it.
                var sign = raised is null ? 0 : Math.Sign(raised.Value - nominal.Value);

                pushes.Add((axis, sign));
            }

            var highest = Extreme(pushes, +1, Read);
            var lowest = Extreme(pushes, -1, Read);

            if (highest is null || lowest is null)
                return CornerResult.Unusable("A corner of this circuit could not be solved.");

            return new CornerResult(
                probe.Label, nominal.Value, lowest, highest, axes.Count, solves);
        }
        finally
        {
            foreach (var axis in axes) axis.Set(axis.Nominal);

            simulator.System.ResetSolution();

            try
            {
                simulator.SolveOperatingPoint();
            }
            catch (Exception ex) when (ex is ConvergenceException or SingularMatrixException)
            {
                // Putting it back is best effort; a circuit that will not solve now would not
                // have solved before.
            }
        }
    }

    /// <summary>
    /// Every axis pushed the way that moves the answer in one direction, solved once.
    /// </summary>
    private static Corner? Extreme(List<(Axis Axis, int Sign)> pushes, int direction, Func<double?> read)
    {
        List<CornerSetting> settings = [];

        foreach (var (axis, sign) in pushes)
        {
            if (sign == 0)
            {
                axis.Set(axis.Nominal);
                continue;
            }

            var high = sign == direction;
            var value = high ? axis.High : axis.Low;

            axis.Set(value);

            settings.Add(new CornerSetting(
                axis.Name, high ? CornerDirection.High : CornerDirection.Low, value));
        }

        var reading = read();

        return reading is null ? null : new Corner(reading.Value, settings);
    }

    /// <summary>
    /// Something a corner can be pushed along: a part's value inside its tolerance band, or the
    /// temperature between two limits.
    /// </summary>
    private sealed record Axis(string Name, double Low, double High, double Nominal, Action<double> Set)
    {
        public static Axis For(MonteCarlo.ToleranceTarget target) => new(
            target.Component.Name,
            target.Nominal * (1 - target.Tolerance),
            target.Nominal * (1 + target.Tolerance),
            target.Nominal,
            value => target.Property.SetValue(target.Component, value));
    }
}
