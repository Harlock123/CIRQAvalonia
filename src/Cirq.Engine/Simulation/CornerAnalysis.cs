using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Numerics;

namespace Cirq.Engine.Simulation;

/// <summary>What a corner analysis was asked for.</summary>
/// <param name="Probe">The probe whose extremes are being hunted. Null takes the first.</param>
public sealed record CornerRequest(SignalProbe? Probe = null);

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

        if (targets.Count == 0)
        {
            return CornerResult.Unusable(
                "No part in this circuit has a tolerance. Set one on a resistor, capacitor or " +
                "inductor in the properties panel — they default to exact.");
        }

        var probe = request.Probe ?? _circuit.Probes.FirstOrDefault();

        if (probe is null)
            return CornerResult.Unusable("Nothing to measure — put a probe on the node you care about.");

        var simulator = new CircuitSimulator(_circuit);

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

        try
        {
            // Which way each part pushes, one at a time. n solves rather than 2ⁿ, and the only
            // thing that makes a corner analysis affordable on a circuit with twenty parts in it.
            List<(MonteCarlo.ToleranceTarget Target, int Sign)> pushes = [];

            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                target.Property.SetValue(target.Component, target.Nominal * (1 + target.Tolerance));

                var raised = Read();

                target.Property.SetValue(target.Component, target.Nominal);

                // A part the answer does not depend on is left at nominal: putting it at an
                // extreme would be noise in the recipe rather than part of it.
                var sign = raised is null ? 0 : Math.Sign(raised.Value - nominal.Value);

                pushes.Add((target, sign));
            }

            var highest = Extreme(pushes, +1, Read);
            var lowest = Extreme(pushes, -1, Read);

            if (highest is null || lowest is null)
                return CornerResult.Unusable("A corner of this circuit could not be solved.");

            return new CornerResult(
                probe.Label, nominal.Value, lowest, highest, targets.Count, solves);
        }
        finally
        {
            foreach (var target in targets)
                target.Property.SetValue(target.Component, target.Nominal);

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
    /// Every part pushed the way that moves the answer in one direction, solved once.
    /// </summary>
    private static Corner? Extreme(
        List<(MonteCarlo.ToleranceTarget Target, int Sign)> pushes, int direction, Func<double?> read)
    {
        List<CornerSetting> settings = [];

        foreach (var (target, sign) in pushes)
        {
            if (sign == 0)
            {
                target.Property.SetValue(target.Component, target.Nominal);
                continue;
            }

            var high = sign == direction;
            var value = target.Nominal * (1 + (high ? target.Tolerance : -target.Tolerance));

            target.Property.SetValue(target.Component, value);

            settings.Add(new CornerSetting(
                target.Component.Name,
                high ? CornerDirection.High : CornerDirection.Low,
                value));
        }

        var reading = read();

        return reading is null ? null : new Corner(reading.Value, settings);
    }
}
