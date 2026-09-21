using System.Reflection;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Numerics;

namespace Cirq.Engine.Simulation;

/// <summary>
/// One thing to vary across a sweep: a numeric property of a component, and the range to walk it
/// over.
/// <para>
/// It is deliberately a <i>property</i> rather than a source voltage. Sweeping a supply is the
/// common case and the one everybody means by "DC sweep", but the same machinery stepping a
/// resistance, a temperature, a light level or a transistor's beta is a parameter sweep, and
/// there is no reason to build that twice.
/// </para>
/// </summary>
/// <param name="Component">The part to vary.</param>
/// <param name="PropertyName">Its property, which must be a writable <c>double</c> or <c>int</c>.</param>
/// <param name="Start">First value.</param>
/// <param name="Stop">Last value.</param>
/// <param name="Points">How many values, counting both ends. Two or more.</param>
public sealed record SweepTarget(
    CircuitComponent Component,
    string PropertyName,
    double Start,
    double Stop,
    int Points = 51)
{
    /// <summary>The values this target works out to, first to last inclusive.</summary>
    public IReadOnlyList<double> Values()
    {
        var count = Math.Max(Points, 2);

        List<double> values = [];
        for (var i = 0; i < count; i++)
            values.Add(Start + ((Stop - Start) * i / (count - 1.0)));

        return values;
    }

    /// <summary>What to call this on an axis, e.g. "V1 Voltage".</summary>
    public string Label => $"{Component.Name} {PropertyName}";

    /// <summary>
    /// The property itself, or null when the component has no such writable number. Resolved once
    /// per sweep rather than per point, since reflection is not free and a sweep is a loop.
    /// </summary>
    public PropertyInfo? Resolve()
    {
        var property = Component.GetType().GetProperty(
            PropertyName, BindingFlags.Public | BindingFlags.Instance);

        if (property is null || !property.CanRead || !property.CanWrite) return null;

        return property.PropertyType == typeof(double) || property.PropertyType == typeof(int)
            ? property
            : null;
    }
}

/// <summary>
/// What a DC sweep was asked for: an X axis, and optionally a second parameter to step.
/// </summary>
/// <param name="Primary">Swept along the X axis.</param>
/// <param name="Step">
/// Stepped to give a family of curves, one per value. This is what turns a single line into a
/// curve tracer: sweeping a transistor's collector voltage gives one curve, and stepping its base
/// current as well gives the fan of them that is on the front of every datasheet.
/// </param>
public sealed record DcSweepRequest(SweepTarget Primary, SweepTarget? Step = null);

/// <summary>One probe's values across the X axis, within one curve of the family.</summary>
/// <param name="Label">What the probe is called.</param>
/// <param name="Kind">Voltage, current or logic — the same choice the scope offers.</param>
/// <param name="Values">One per X point. NaN where the solver could not converge.</param>
public sealed record DcTrace(string Label, ProbeKind Kind, IReadOnlyList<double> Values);

/// <summary>
/// One curve of the family: every probe, solved with the stepped parameter held at one value.
/// </summary>
/// <param name="StepValue">What the stepped parameter was, or NaN when nothing was stepped.</param>
/// <param name="Traces">One per probe.</param>
public sealed record DcSweepCurve(double StepValue, IReadOnlyList<DcTrace> Traces);

/// <summary>The whole answer.</summary>
/// <param name="X">The swept values, in order.</param>
/// <param name="XLabel">What the X axis is.</param>
/// <param name="StepLabel">What was stepped, or null when nothing was.</param>
/// <param name="Curves">One per stepped value; exactly one when nothing was stepped.</param>
public sealed record DcSweepResult(
    IReadOnlyList<double> X,
    string XLabel,
    string? StepLabel,
    IReadOnlyList<DcSweepCurve> Curves)
{
    /// <summary>True when every point of every curve failed to converge.</summary>
    public bool IsEmpty =>
        Curves.Count == 0 ||
        Curves.All(c => c.Traces.All(t => t.Values.All(double.IsNaN)));

    /// <summary>
    /// Points the solver could not reach, across the whole result. A few are normal at the edges
    /// of a device's range; a lot means the sweep is asking for something the circuit cannot do.
    /// </summary>
    public int FailedPoints =>
        Curves.Sum(c => c.Traces.Sum(t => t.Values.Count(double.IsNaN)));
}

/// <summary>
/// A DC sweep: step a parameter, solve the operating point, write down what the probes say, and
/// do it again.
/// <para>
/// This is the analysis that draws the curves parts are specified by — a diode's exponential, a
/// transistor's output characteristic, a MOSFET's square law, a panel's maximum power point, a
/// comparator's hysteresis as an actual loop. None of those is visible in a transient run without
/// building a ramp generator first, and none of them is a frequency response.
/// </para>
/// <para>
/// Each point starts from the solution of the one before it, which is not an optimisation so much
/// as the thing that makes it work at all: a diode asked for 0.7 V out of nowhere is a hard solve,
/// and the same diode asked to move ten millivolts from where it already is converges in two
/// iterations. Where that still fails the point is recorded as NaN and the sweep carries on, since
/// one unreachable corner should not throw away the rest of the curve.
/// </para>
/// </summary>
public sealed class DcSweep
{
    private readonly CircuitSimulator _simulator;

    public DcSweep(CircuitSimulator simulator)
    {
        _simulator = simulator;
    }

    /// <summary>
    /// Runs a sweep and puts every swept property back where it was. The circuit is left at its
    /// own bias point afterwards, so the scope and the canvas are not showing the last point of
    /// the sweep as though it were the operating point.
    /// </summary>
    public DcSweepResult Run(DcSweepRequest request, CancellationToken cancellationToken = default)
    {
        var primary = request.Primary.Resolve()
            ?? throw new ArgumentException(
                $"'{request.Primary.Component.Name}' has no writable number called " +
                $"'{request.Primary.PropertyName}' to sweep.", nameof(request));

        var stepProperty = request.Step?.Resolve();

        if (request.Step is not null && stepProperty is null)
            throw new ArgumentException(
                $"'{request.Step.Component.Name}' has no writable number called " +
                $"'{request.Step.PropertyName}' to step.", nameof(request));

        var x = request.Primary.Values();
        var stepValues = request.Step?.Values() ?? [double.NaN];

        var primaryWas = Read(primary, request.Primary.Component);
        var stepWas = stepProperty is null ? 0.0 : Read(stepProperty, request.Step!.Component);

        List<DcSweepCurve> curves = [];

        try
        {
            foreach (var stepValue in stepValues)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (stepProperty is not null)
                {
                    Write(stepProperty, request.Step!.Component, stepValue);

                    // A new curve starts somewhere else entirely, so the previous curve's last
                    // point is a poor guess. Beginning from scratch costs one slow solve and
                    // avoids dragging a stale solution across a discontinuity.
                    _simulator.System.ResetSolution();
                }

                curves.Add(SweepOnce(primary, request.Primary.Component, x, stepValue, cancellationToken));
            }
        }
        finally
        {
            Write(primary, request.Primary.Component, primaryWas);
            if (stepProperty is not null) Write(stepProperty, request.Step!.Component, stepWas);

            // Back to the circuit's own operating point, whatever happened during the sweep.
            _simulator.System.ResetSolution();
            TrySolve();
        }

        return new DcSweepResult(x, request.Primary.Label, request.Step?.Label, curves);
    }

    private DcSweepCurve SweepOnce(
        PropertyInfo property,
        CircuitComponent component,
        IReadOnlyList<double> x,
        double stepValue,
        CancellationToken cancellationToken)
    {
        var probes = _simulator.Circuit.Probes;
        List<double>[] values = [.. probes.Select(_ => new List<double>())];

        foreach (var point in x)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Write(property, component, point);

            var solved = TrySolve();

            for (var i = 0; i < probes.Count; i++)
                values[i].Add(solved ? _simulator.SampleProbe(probes[i]) : double.NaN);
        }

        List<DcTrace> traces = [];
        for (var i = 0; i < probes.Count; i++)
            traces.Add(new DcTrace(probes[i].Label, probes[i].Kind, values[i]));

        return new DcSweepCurve(stepValue, traces);
    }

    /// <summary>
    /// Solves one point, reporting failure rather than throwing. A sweep walks into corners a
    /// single run never reaches — a transistor driven past its rail, a supply taken to zero — and
    /// a gap in one curve is a better answer than no curves at all.
    /// </summary>
    private bool TrySolve()
    {
        try
        {
            _simulator.SolveOperatingPoint();
            return true;
        }
        catch (Exception ex) when (ex is ConvergenceException or SingularMatrixException)
        {
            // The failed attempt leaves rubbish in the solution vector, which would then be the
            // next point's initial guess. Clear it so the next point starts clean.
            _simulator.System.ResetSolution();
            return false;
        }
    }

    private static double Read(PropertyInfo property, CircuitComponent component) =>
        Convert.ToDouble(property.GetValue(component) ?? 0.0);

    private static void Write(PropertyInfo property, CircuitComponent component, double value)
    {
        // int and double have a common type, so a conditional expression unifies to double and an
        // int property handed a boxed double is refused outright. Two statements, as the slider
        // view model has to do for the same reason.
        if (property.PropertyType == typeof(int))
            property.SetValue(component, (int)Math.Round(value));
        else
            property.SetValue(component, value);
    }
}
