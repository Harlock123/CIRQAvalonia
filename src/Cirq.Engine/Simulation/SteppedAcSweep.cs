using System.Reflection;
using Cirq.Core.Topology;
using Cirq.Engine.Numerics;

namespace Cirq.Engine.Simulation;

/// <summary>What a stepped frequency sweep was asked for.</summary>
/// <param name="Sweep">The sweep to run at each value — the same request a plain sweep takes.</param>
/// <param name="Parameter">What to vary, and over what range. The same target a DC sweep steps.</param>
public sealed record AcStepRequest(AcSweepRequest Sweep, SweepTarget Parameter);

/// <summary>One pass: the whole sweep, with the parameter held at one value.</summary>
/// <param name="Value">What the parameter was.</param>
/// <param name="Result">The response across frequency at that value.</param>
/// <param name="Failure">Why this pass produced nothing, or null when it produced something.</param>
public sealed record AcStepRun(double Value, AcSweepResult? Result, string? Failure = null)
{
    public bool Succeeded => Result is not null;
}

/// <summary>The family.</summary>
/// <param name="Label">What was stepped, for the legend.</param>
/// <param name="Runs">One per value, in order.</param>
public sealed record AcStepResult(string Label, IReadOnlyList<AcStepRun> Runs)
{
    public static AcStepResult Empty { get; } = new(string.Empty, []);

    /// <summary>The passes that produced an answer.</summary>
    public IEnumerable<AcStepRun> Successful => Runs.Where(r => r.Succeeded);

    /// <summary>
    /// Where each pass turned over, for the summary line. A family of curves is read for how the
    /// corner moves as much as for the curves themselves, and reading four corners off a plot by
    /// eye is exactly the thing this is meant to replace.
    /// </summary>
    public IEnumerable<(double Value, double? Corner)> Corners(string label) =>
        Successful.Select(r => (r.Value, r.Result!.CornerOf(label)));
}

/// <summary>
/// A frequency sweep run once per value of a parameter, so a Bode plot can carry a family of curves
/// instead of one.
/// <para>
/// DC sweeps and transients could both already step a second parameter; this could not, and it is
/// the one where the question comes up most. "What does the feedback resistor do to the peaking",
/// "how far does the corner move across the capacitor's tolerance band", "which gate resistor stops
/// it ringing" are all questions about a <i>family</i> of responses, and answering them one sweep
/// at a time means writing four sets of numbers down and comparing them by hand.
/// </para>
/// <para>
/// Each pass re-solves the bias point before sweeping, which matters more here than it does for a
/// stepped transient: a small-signal sweep linearises about the operating point, and a parameter
/// worth stepping usually moves it. Sweeping four values about one stale bias point would produce
/// four curves of a circuit that only exists at one of them.
/// </para>
/// </summary>
public sealed class SteppedAcSweep
{
    private readonly CircuitSimulator _simulator;

    public SteppedAcSweep(CircuitSimulator simulator)
    {
        _simulator = simulator;
    }

    /// <summary>Runs the family and puts the parameter and the circuit back where they were.</summary>
    public AcStepResult Run(AcStepRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var property = request.Parameter.IsTemperature ? null : request.Parameter.Resolve();

        if (!request.Parameter.IsTemperature && property is null)
            throw new ArgumentException(
                $"'{request.Parameter.Component?.Name}' has no writable number called " +
                $"'{request.Parameter.PropertyName}' to step.", nameof(request));

        if (_simulator.Circuit.Probes.Count == 0) return AcStepResult.Empty;

        var was = Read(request.Parameter, property);

        List<AcStepRun> runs = [];

        try
        {
            foreach (var value in request.Parameter.Values())
            {
                cancellationToken.ThrowIfCancellationRequested();

                Write(request.Parameter, property, value);

                runs.Add(Once(value, request.Sweep, cancellationToken));
            }
        }
        finally
        {
            Write(request.Parameter, property, was);

            Restore();
        }

        return new AcStepResult(request.Parameter.Label, runs);
    }

    private AcStepRun Once(double value, AcSweepRequest sweep, CancellationToken cancellationToken)
    {
        try
        {
            // The bias point first, because the sweep linearises about it and this value may have
            // moved it. Reset as well as re-solve: a device carrying state from the previous pass
            // would bias from where that pass left it.
            _simulator.Reset();
            _simulator.SolveOperatingPoint();

            return new AcStepRun(value, new AcSweep(_simulator).Run(sweep, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ConvergenceException or SingularMatrixException)
        {
            // One value the circuit will not bias at should not throw the others away. An amplifier
            // whose feedback resistor is small enough to stop it biasing is a real answer, and the
            // family is more useful with a gap in it than not at all.
            return new AcStepRun(value, null, ex.Message);
        }
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
            // Best effort: a circuit that will not bias now was not going to bias before the sweep
            // either, and throwing here would hide whatever the sweep did find.
        }
    }

    private double Read(SweepTarget target, PropertyInfo? property)
    {
        if (target.IsTemperature) return _simulator.Settings.TemperatureKelvin - 273.15;

        return property is null ? 0.0 : Convert.ToDouble(property.GetValue(target.Component) ?? 0.0);
    }

    private void Write(SweepTarget target, PropertyInfo? property, double value)
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
