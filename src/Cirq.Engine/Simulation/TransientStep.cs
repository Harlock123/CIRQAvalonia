using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Engine.Numerics;

namespace Cirq.Engine.Simulation;

/// <summary>What a stepped transient was asked for.</summary>
/// <param name="Parameter">What to vary, and over what range. The same target a DC sweep uses.</param>
/// <param name="Duration">How long to run each pass, in seconds.</param>
/// <param name="SampleInterval">
/// How often to record, or null to work one out from the duration. A probe keeps ten thousand
/// points, so sampling finer than the run is long throws the beginning of it away.
/// </param>
public sealed record TransientStepRequest(
    SweepTarget Parameter, double Duration, double? SampleInterval = null);

/// <summary>One probe's trace from one pass.</summary>
/// <param name="Label">The probe's name.</param>
/// <param name="Kind">Voltage, current or logic — which decides the axis it belongs on.</param>
/// <param name="Unit">The unit the probe reports in.</param>
/// <param name="Samples">Time and value, in order.</param>
public sealed record TransientTrace(
    string Label, ProbeKind Kind, string Unit, IReadOnlyList<DataPoint> Samples);

/// <summary>One pass: the whole circuit run with the parameter held at one value.</summary>
/// <param name="Value">What the parameter was.</param>
/// <param name="Traces">One per probe. Empty when the pass failed.</param>
/// <param name="Problem">Why this pass produced nothing, or null when it worked.</param>
public sealed record TransientRun(
    double Value, IReadOnlyList<TransientTrace> Traces, string? Problem = null)
{
    public bool Succeeded => Problem is null && Traces.Count > 0;
}

/// <summary>The whole family of runs.</summary>
/// <param name="Label">What was stepped, for the legend.</param>
/// <param name="Runs">One per value, in the order they were asked for.</param>
public sealed record TransientStepResult(string Label, IReadOnlyList<TransientRun> Runs)
{
    public bool IsEmpty => Runs.All(r => !r.Succeeded);

    /// <summary>How many passes produced a trace.</summary>
    public int Succeeded => Runs.Count(r => r.Succeeded);

    public static TransientStepResult Empty => new(string.Empty, []);
}

/// <summary>
/// Runs the same transient several times over, with one component's value changed each time.
/// <para>
/// A DC sweep steps a parameter and records an <i>operating point</i> at each value, which answers
/// what a circuit settles at. It cannot answer the question people actually ask most often, which
/// is about how a circuit <b>gets</b> there: try three capacitor values and see how the ringing
/// changes; try four gate resistors and watch the switching edge; try a handful of feedback
/// resistors and find the one where the step response stops overshooting. That is a family of
/// transients, not a family of bias points.
/// </para>
/// <para>
/// Each pass is a fresh run from the same starting conditions — reset, bias point, then the
/// transient — because a circuit's response to a step depends on where it started, and carrying
/// the previous value's final state into the next pass would make every curve after the first a
/// different experiment. That is the opposite of what a DC sweep does, and deliberately so: there
/// the previous point is the best possible initial guess, here it is contamination.
/// </para>
/// </summary>
public sealed class TransientStep
{
    private readonly CircuitSimulator _simulator;

    public TransientStep(CircuitSimulator simulator)
    {
        _simulator = simulator;
    }

    /// <summary>
    /// Runs the family and puts the parameter, the sampling and the circuit back where they were.
    /// </summary>
    public TransientStepResult Run(
        TransientStepRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Duration <= 0)
            throw new ArgumentException("A run needs a duration.", nameof(request));

        var property = request.Parameter.IsTemperature ? null : request.Parameter.Resolve();

        if (!request.Parameter.IsTemperature && property is null)
            throw new ArgumentException(
                $"'{request.Parameter.Component?.Name}' has no writable number called " +
                $"'{request.Parameter.PropertyName}' to step.", nameof(request));

        var probes = _simulator.Circuit.Probes;

        if (probes.Count == 0) return TransientStepResult.Empty;

        var wasValue = Read(request.Parameter, property);
        var wasInterval = _simulator.Settings.ProbeSampleInterval;
        var wasMaxStep = _simulator.Settings.MaxTimeStep;

        // A probe's history is a fixed ten thousand points. Sampling finer than the run is long
        // silently throws away its beginning — which on a step response is the only part anybody
        // wanted — so the interval is set from the duration unless it was stated.
        var interval = request.SampleInterval ?? (request.Duration / 4000.0);

        List<TransientRun> runs = [];

        try
        {
            _simulator.Settings.ProbeSampleInterval = interval;
            _simulator.Settings.MaxTimeStep = Math.Min(wasMaxStep, interval);

            foreach (var value in request.Parameter.Values())
            {
                cancellationToken.ThrowIfCancellationRequested();

                Write(request.Parameter, property, value);

                runs.Add(Once(value, probes, request.Duration, cancellationToken));
            }
        }
        finally
        {
            Write(request.Parameter, property, wasValue);

            _simulator.Settings.ProbeSampleInterval = wasInterval;
            _simulator.Settings.MaxTimeStep = wasMaxStep;

            // Back to where the circuit was, so the canvas and the scope are not left showing the
            // last pass as though it were the state of things.
            Restore();
        }

        return new TransientStepResult(request.Parameter.Label, runs);
    }

    private TransientRun Once(
        double value, IReadOnlyList<SignalProbe> probes, double duration,
        CancellationToken cancellationToken)
    {
        try
        {
            _simulator.RunTransient(duration, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ConvergenceException or SingularMatrixException)
        {
            // One value the circuit cannot be solved at should not throw away the others. A
            // regulator asked for a capacitor too small to be stable is a real answer, and the
            // family is more useful with a gap in it than not at all.
            return new TransientRun(value, [], ex.Message);
        }

        // Copied out now: the next pass resets the probes, and a reference to the live buffer
        // would then be a reference to the pass after this one.
        List<TransientTrace> traces = [];

        foreach (var probe in probes)
        {
            traces.Add(new TransientTrace(
                probe.Label, probe.Kind, probe.Unit, [.. probe.HistoryBuffer.ToArray()]));
        }

        return new TransientRun(value, traces);
    }

    /// <summary>Leaves the circuit at its own operating point, whatever happened during the run.</summary>
    private void Restore()
    {
        try
        {
            _simulator.Reset();
            _simulator.SolveOperatingPoint();
        }
        catch (Exception ex) when (ex is ConvergenceException or SingularMatrixException)
        {
            // Put back is best-effort: a circuit that will not bias was not going to bias before
            // the sweep either, and throwing here would hide whatever the sweep did find.
        }
    }

    private double Read(SweepTarget target, System.Reflection.PropertyInfo? property)
    {
        if (target.IsTemperature) return _simulator.Settings.TemperatureKelvin - 273.15;

        return property is null ? 0.0 : Convert.ToDouble(property.GetValue(target.Component) ?? 0.0);
    }

    private void Write(SweepTarget target, System.Reflection.PropertyInfo? property, double value)
    {
        if (target.IsTemperature)
        {
            _simulator.Settings.TemperatureKelvin = value + 273.15;
            return;
        }

        if (property is null) return;

        // int and double have no common type a conditional expression can unify, and an int
        // property handed a boxed double is refused outright — so the two cases are written out.
        if (property.PropertyType == typeof(int))
        {
            property.SetValue(target.Component, (int)Math.Round(value));
            return;
        }

        property.SetValue(target.Component, value);
    }
}
