using System.Globalization;
using Cirq.Components.Serialization;
using Cirq.Components.Spice;
using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using Cirq.Core.Verification;
using Cirq.Engine.Simulation;

namespace Cirq.Cli;

/// <summary>
/// What the tool can be asked to do.
/// <para>
/// The point of all of it is the exit code from <see cref="Check"/>. A circuit's requirements are
/// saved in the circuit, and until now the only way to find out whether they were met was to open
/// the application and look — which means they were checked when somebody remembered to, which
/// means they were not checked. A command that runs the circuit and exits non-zero is a circuit
/// that can be tested the same way the code around it is.
/// </para>
/// </summary>
public static class Commands
{
    /// <summary>Holds a circuit to the requirements saved in it.</summary>
    public static int Check(Options options, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(options);

        var (circuit, exit) = Load(options, output, error);

        if (circuit is null) return exit;

        var enabled = circuit.Specs.Where(s => s.IsEnabled).ToList();

        if (enabled.Count == 0)
        {
            error.WriteLine("cirq: this circuit has no requirements to check.");
            return CommandLine.Misused;
        }

        if (circuit.Probes.Count == 0)
        {
            error.WriteLine("cirq: this circuit has no probes, so there is nothing to measure.");
            return CommandLine.Misused;
        }

        var seconds = options.Number("for", 10e-3);
        var quiet = options.Has("quiet");

        var simulator = new CircuitSimulator(circuit);

        simulator.Reset();
        simulator.ResolveProbes();

        return options.Has("over")
            ? OverRange(options, circuit, simulator, seconds, quiet, output, error)
            : AtOnePoint(circuit, simulator, seconds, quiet, output);
    }

    private static int AtOnePoint(
        Circuit circuit, CircuitSimulator simulator, double seconds, bool quiet, TextWriter output)
    {
        simulator.Settings.ProbeSampleInterval = seconds / 4000.0;
        simulator.RunTransient(seconds);

        var samples = circuit.Probes.ToDictionary(
            p => p.Label,
            p => (IReadOnlyList<Cirq.Core.Primitives.DataPoint>?)p.HistoryBuffer.ToArray());

        var results = SpecCheck.EvaluateAllFrom(
            circuit.Specs, label => samples.GetValueOrDefault(label));

        var failed = results.Count(r => r.Passed == false);
        var unknown = results.Count(r => r.Passed is null);

        if (!quiet)
        {
            foreach (var result in results)
            {
                var mark = result.Passed switch { true => "pass", false => "FAIL", _ => "  ? " };

                output.WriteLine($"  {mark}  {result.Explanation}");
            }

            output.WriteLine();
        }

        output.WriteLine(Verdict(results.Count, failed, unknown, $"after {Seconds(seconds)}"));

        return failed > 0 ? CommandLine.Failed : CommandLine.Ok;
    }

    private static int OverRange(
        Options options, Circuit circuit, CircuitSimulator simulator, double seconds, bool quiet,
        TextWriter output, TextWriter error)
    {
        var what = options.Text("over") ?? "temperature";

        if (Target(circuit, what, options) is not { } target)
        {
            error.WriteLine(
                $"cirq: nothing called \"{what}\" to sweep. Use \"temperature\", or a part and its " +
                "setting as R1.Resistance.");

            return CommandLine.Misused;
        }

        var result = new SpecSweep(simulator).Run(new SpecSweepRequest(target, seconds));

        if (!quiet)
        {
            foreach (var margin in result.Margins)
            {
                var mark = margin.Judged.Count == 0 ? "  ? " : margin.Fails ? "FAIL" : "pass";
                var where = margin.Fails && margin.Window is { } window
                    ? $" — met from {Value(target, window.From)} to {Value(target, window.To)}"
                    : margin.Worst is { } worst
                        ? $" — worst at {Value(target, worst.Value)}"
                        : string.Empty;

                output.WriteLine($"  {mark}  {margin.Spec.Describe()}{where}");
            }

            output.WriteLine();
        }

        foreach (var problem in result.Problems) error.WriteLine($"cirq: {problem}");

        output.WriteLine(result.Summary());

        return result.Margins.Any(m => m.Fails) ? CommandLine.Failed : CommandLine.Ok;
    }

    /// <summary>
    /// Records what the circuit does now, into the circuit, so a later run can be held against it.
    /// </summary>
    public static int Baseline(Options options, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(options);

        var path = options.RequirePath();
        var (circuit, exit) = Load(options, output, error);

        if (circuit is null) return exit;

        if (circuit.Probes.Count == 0)
        {
            error.WriteLine("cirq: this circuit has no probes, so there would be nothing to record.");
            return CommandLine.Misused;
        }

        var seconds = options.Number("for", 10e-3);

        var traces = RunAndCollect(circuit, seconds);

        circuit.Baseline = TraceBaseline.From(traces, options.Text("note") ?? "Recorded by cirq");

        // Written back into the circuit, because that is where a baseline lives: it travels with
        // the design, the way the requirements do, rather than in a file beside it that can be lost
        // or get out of step.
        File.WriteAllText(path, CircuitSerializer.ToJson(circuit));

        output.WriteLine(
            $"Recorded {circuit.Baseline.Traces.Count} trace(s) over {Seconds(seconds)} into " +
            $"{Path.GetFileName(path)}");

        return CommandLine.Ok;
    }

    /// <summary>Runs the circuit and says what moved since the baseline was recorded.</summary>
    public static int Compare(Options options, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(options);

        var (circuit, exit) = Load(options, output, error);

        if (circuit is null) return exit;

        if (circuit.Baseline.IsEmpty)
        {
            error.WriteLine(
                "cirq: this circuit has no baseline. Record one with cirq baseline, or take one in " +
                "the application under Simulate > Baseline.");

            return CommandLine.Misused;
        }

        var seconds = options.Number("for", 10e-3);
        var quiet = options.Has("quiet");

        var comparison = BaselineCheck.Against(circuit.Baseline, RunAndCollect(circuit, seconds));

        if (!quiet)
        {
            foreach (var trace in comparison.Traces)
            {
                var mark = trace.IsUnchanged ? "same" : "MOVED";
                var what = trace.Missing ? " — gone since the baseline"
                    : trace.Added ? " — new since the baseline"
                    : trace.Changes.Count == 0 ? string.Empty
                    : " — " + string.Join(", ", trace.Changes.Select(Describe));

                output.WriteLine($"  {mark}  {trace.Label}{what}");
            }

            output.WriteLine();
        }

        output.WriteLine(comparison.Summary());

        return comparison.IsUnchanged ? CommandLine.Ok : CommandLine.Failed;
    }

    /// <summary>One measurement's move, in the words a person would use for it.</summary>
    private static string Describe(MeasurementChange change) =>
        change.Appeared ? $"{change.Quantity} appeared"
        : change.Disappeared ? $"{change.Quantity} went away"
        : double.IsNaN(change.Fraction)
            ? $"{change.Quantity} {change.Was:g4} to {change.Now:g4}"
            : $"{change.Quantity} {change.Fraction * 100:+0.#;-0.#}%";

    /// <summary>Runs the circuit once and hands back what every probe recorded.</summary>
    private static List<(string Label, string Unit, IReadOnlyList<DataPoint> Samples)> RunAndCollect(
        Circuit circuit, double seconds)
    {
        var simulator = new CircuitSimulator(circuit);

        simulator.Settings.ProbeSampleInterval = seconds / 4000.0;
        simulator.Reset();
        simulator.ResolveProbes();
        simulator.RunTransient(seconds);

        return [.. circuit.Probes.Select(p =>
            (p.Label, p.Unit, (IReadOnlyList<DataPoint>)p.HistoryBuffer.ToArray()))];
    }

    /// <summary>Runs the circuit and writes what the probes recorded.</summary>
    public static int RunTransient(Options options, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(options);

        var (circuit, exit) = Load(options, output, error);

        if (circuit is null) return exit;

        if (circuit.Probes.Count == 0)
        {
            error.WriteLine("cirq: this circuit has no probes, so there would be nothing in the file.");
            return CommandLine.Misused;
        }

        var seconds = options.Number("for", 10e-3);
        var interval = options.Number("every", seconds / 2000.0);

        var simulator = new CircuitSimulator(circuit);

        simulator.Settings.ProbeSampleInterval = interval;
        simulator.Reset();
        simulator.ResolveProbes();
        simulator.RunTransient(seconds);

        var path = options.Text("csv")
                   ?? System.IO.Path.ChangeExtension(options.RequirePath(), ".csv");

        WriteCsv(circuit, path);

        var rows = circuit.Probes.Max(p => p.HistoryBuffer.Count);

        output.WriteLine(
            $"Ran {Seconds(seconds)} and wrote {rows} rows for " +
            $"{circuit.Probes.Count} probe(s) to {path}");

        return CommandLine.Ok;
    }

    /// <summary>Writes the circuit out as a SPICE deck.</summary>
    public static int Netlist(Options options, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(options);

        var (circuit, exit) = Load(options, output, error);

        if (circuit is null) return exit;

        var written = SpiceNetlistWriter.Write(circuit, circuit.Title);

        if (written.IsEmpty)
        {
            error.WriteLine("cirq: nothing in this circuit can be written as a netlist.");
            return CommandLine.Misused;
        }

        var path = options.Text("o") ?? options.Text("out");

        if (path is null)
        {
            output.Write(written.Netlist);
        }
        else
        {
            File.WriteAllText(path, written.Netlist);
            output.WriteLine($"Wrote {written.Written.Count} part(s) to {path}");
        }

        // Said on the error stream so that redirecting the deck to a file still shows them.
        foreach (var skipped in written.Skipped) error.WriteLine($"cirq: skipped {skipped}");

        return CommandLine.Ok;
    }

    /// <summary>What is in the circuit, without running it.</summary>
    public static int Info(Options options, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(options);

        var (circuit, exit) = Load(options, output, error);

        if (circuit is null) return exit;

        var netlist = circuit.BuildNetlist();
        var parts = Flattening.Flatten(circuit.Components).Count();

        output.WriteLine($"{circuit.Title}");
        output.WriteLine($"  {parts} part(s), {circuit.Wires.Count} wire(s), {netlist.NodeCount} node(s)");

        if (circuit.Sheets.Count > 0)
            output.WriteLine($"  {circuit.Sheets.Count} sheet(s): {string.Join(", ", circuit.Sheets)}");

        output.WriteLine($"  {circuit.Probes.Count} probe(s): " +
                         $"{string.Join(", ", circuit.Probes.Select(p => p.Label))}");

        output.WriteLine($"  {circuit.Specs.Count} requirement(s)");

        foreach (var spec in circuit.Specs)
            output.WriteLine($"    {(spec.IsEnabled ? "" : "(off) ")}{spec.Describe()}");

        if (circuit.Parameters.Count > 0)
        {
            output.WriteLine($"  {circuit.Parameters.Count} parameter(s): " +
                             string.Join(", ", circuit.Parameters.Select(p => $"{p.Name} = {p.Expression}")));
        }

        output.WriteLine($"  ambient {circuit.AmbientTemperatureCelsius:0.#} °C" +
                         (circuit.AdaptiveTimeStep ? ", solver chooses the time step" : string.Empty));

        return CommandLine.Ok;
    }

    // ---- the parts they share ------------------------------------------------

    /// <summary>
    /// Reads the circuit, and says what was lost on the way in. A warning is written to the error
    /// stream rather than swallowed: a part this build does not know about is exactly the thing that
    /// would otherwise make a check pass for the wrong reason.
    /// </summary>
    private static (Circuit? Circuit, int Exit) Load(Options options, TextWriter output, TextWriter error)
    {
        var path = options.RequirePath();

        CircuitLoadResult result;

        try
        {
            result = CircuitSerializer.FromJson(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is CircuitFormatException or System.Text.Json.JsonException)
        {
            error.WriteLine($"cirq: {path} is not a circuit this can read — {ex.Message}");
            return (null, CommandLine.Misused);
        }

        foreach (var warning in result.Warnings) error.WriteLine($"cirq: {warning}");

        return (result.Circuit, CommandLine.Ok);
    }

    private static SweepTarget? Target(Circuit circuit, string what, Options options)
    {
        if (string.Equals(what, "temperature", StringComparison.OrdinalIgnoreCase))
        {
            return SweepTarget.OverTemperature(
                options.Number("from", -40), options.Number("to", 85), options.Count("points", 8));
        }

        var dot = what.IndexOf('.', StringComparison.Ordinal);

        if (dot <= 0) return null;

        var designator = what[..dot];
        var property = what[(dot + 1)..];

        var part = Flattening.Flatten(circuit.Components)
            .FirstOrDefault(c => string.Equals(c.Name, designator, StringComparison.OrdinalIgnoreCase));

        if (part is null) return null;

        var target = new SweepTarget(
            part, property, options.Number("from", 0), options.Number("to", 0),
            options.Count("points", 8));

        return target.Resolve() is null ? null : target;
    }

    private static void WriteCsv(Circuit circuit, string path)
    {
        using var writer = new StreamWriter(path);

        // One row per sample of the first probe, with the others read at the same index. Probes are
        // recorded on the same interval, so the columns line up by construction.
        writer.Write("time");

        foreach (var probe in circuit.Probes) writer.Write($",{Escape(probe.Label)}");

        writer.WriteLine();

        var longest = circuit.Probes.Max(p => p.HistoryBuffer.Count);

        for (var i = 0; i < longest; i++)
        {
            var time = circuit.Probes
                .Where(p => i < p.HistoryBuffer.Count)
                .Select(p => p.HistoryBuffer[i].Time)
                .FirstOrDefault();

            writer.Write(time.ToString("G9", CultureInfo.InvariantCulture));

            foreach (var probe in circuit.Probes)
            {
                writer.Write(',');

                if (i < probe.HistoryBuffer.Count)
                    writer.Write(probe.HistoryBuffer[i].Value.ToString("G9", CultureInfo.InvariantCulture));
            }

            writer.WriteLine();
        }
    }

    private static string Escape(string label) =>
        label.Contains(',', StringComparison.Ordinal) ? $"\"{label}\"" : label;

    private static string Verdict(int total, int failed, int unknown, string when)
    {
        if (failed > 0)
            return $"{failed} of {total} requirement(s) not met {when}.";

        return unknown > 0
            ? $"{total - unknown} of {total} requirement(s) met {when}; {unknown} could not be measured."
            : $"All {total} requirement(s) met {when}.";
    }

    private static string Seconds(double seconds) => SiPrefix.Format(seconds, "s", 3);

    private static string Value(SweepTarget target, double value) =>
        target.IsTemperature ? $"{value:0.#} °C" : SiPrefix.Format(value, string.Empty, 3);
}
