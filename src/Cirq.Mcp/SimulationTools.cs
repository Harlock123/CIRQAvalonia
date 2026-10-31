using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cirq.Components.Serialization;
using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using Cirq.Engine.Numerics;
using Cirq.Engine.Simulation;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cirq.Mcp;

/// <summary>
/// The tools that run the circuit. Each run takes a copy of the circuit on the host's thread and solves the copy on a
/// worker thread, so the editor stays responsive and its own simulation is left alone; a transient run's samples are
/// then written back into the circuit's probes, so the scope shows what the agent saw.
/// </summary>
public sealed partial class CircuitTools
{
    /// <summary>How long one analysis may take, at most.</summary>
    public static readonly TimeSpan RunLimit = TimeSpan.FromSeconds(120);

    /// <summary>The most time points one transient run may solve.</summary>
    public const int MaxSteps = 2_000_000;

    [McpServerTool(Name = "operating_point", ReadOnly = true, Idempotent = true)]
    [Description("Solves the circuit's DC operating point (the bias point: capacitors open, inductors shorted, every source at its t = 0 value) and returns the voltage of every net, the reading of every probe, and the current through each source and part that reports one. The quickest check that a circuit is biased as meant.")]
    public async Task<string> OperatingPoint(CancellationToken cancel = default)
    {
        var copy = await Snapshot(cancel).ConfigureAwait(false);
        return await Solve(() =>
        {
            var simulator = Simulator(copy, null);
            simulator.Reset();
            simulator.SolveOperatingPoint();
            simulator.ResolveProbes();
            var nets = new JsonArray();
            foreach (var net in simulator.Netlist.Nets.Where(n => !n.IsGround && n.Terminals.Count(t => t.Owner is not null) > 1 || n.Label is not null))
                nets.Add(new JsonObject { ["net"] = net.Name, ["volts"] = Round(simulator.NodeVoltage(net.Index)), ["pins"] = string.Join(", ", net.Terminals.Where(t => t.Owner is not null).Select(Pins.Name)) });
            var currents = new JsonArray();
            foreach (var c in copy.Components)
            {
                if (c.Terminals.Count == 0 || (c is not ICurrentReporting && c.VoltageSourceCount == 0)) continue;
                var into = c.Terminals[0];
                currents.Add(new JsonObject { ["part"] = c.Name, ["amps_into"] = Pins.Name(into), ["amps"] = Round(simulator.TerminalCurrent(into)) });
            }
            var probes = new JsonArray(copy.Probes.Select(p => (JsonNode)new JsonObject
            {
                ["probe"] = p.Label, ["reading"] = p.Kind == ProbeKind.Logic ? (simulator.SampleProbe(p) >= 0.75 ? "1" : simulator.SampleProbe(p) <= 0.25 ? "0" : "x") : SiPrefix.Format(simulator.SampleProbe(p), p.Unit),
            }).ToArray());
            return new JsonObject { ["nets"] = nets, ["probes"] = probes, ["currents"] = currents, ["temperature_c"] = copy.AmbientTemperatureCelsius }.ToJsonString(Json.Options);
        }, cancel).ConfigureAwait(false);
    }

    [McpServerTool(Name = "run_transient")]
    [Description("Runs the circuit through time from power-on (the bias point, then each time step) for a duration, and reports each probe's measurements (min, max, mean, RMS, peak to peak, frequency, duty cycle, rise and fall time) with a plot of the traces against time to look at. The samples go into the circuit's probes, so in the app the scope shows the run. Pick a duration that shows several cycles of what you expect (a 1 kHz oscillator: 5 ms). Needs at least one probe (add_probe).")]
    public async Task<CallToolResult> RunTransient(
        [Description("How long to simulate, in seconds (or engineering text, e.g. \"5m\" for 5 ms). From 1 ns to 100 s.")] JsonElement duration,
        [Description("The solver's time step in seconds (or text, e.g. \"1u\"). Default: a ten-thousandth of the duration. Smaller is more accurate and slower.")] JsonElement? time_step = null,
        [Description("Only report the time after this, in seconds (e.g. to skip the start-up). Default 0.")] JsonElement? from = null,
        [Description("Include the plot. Default true.")] bool plot = true,
        [Description("The plot's width in pixels, from 200 to 1600. Default 900.")] int width = 900,
        [Description("The plot's height in pixels, from 150 to 1200. Default 500.")] int height = 500,
        CancellationToken cancel = default)
    {
        var seconds = Seconds(duration, "duration");
        if (seconds is < 1e-9 or > 100) throw new McpException("duration must be from 1 ns to 100 s.");
        var step = Seconds(time_step, "time_step", Math.Max(seconds / 10_000, 1e-12));
        if (step <= 0 || step > seconds) throw new McpException("time_step must be more than 0 and no more than the duration.");
        if (seconds / step > MaxSteps) throw new McpException($"That's {seconds / step:0} time steps, more than the {MaxSteps} one run may take. Make the step larger or the duration shorter.");
        var start = Seconds(from, "from", 0);
        if (start < 0 || start >= seconds) throw new McpException("from must be at least 0 and less than the duration.");
        Size(width, height);

        var copy = await Snapshot(cancel).ConfigureAwait(false);
        if (copy.Probes.Count == 0) throw new McpException("The circuit has no probes, so nothing would be recorded. add_probe puts one on the pin you want to watch (the output, say), then run again.");

        var traces = await Solve(() =>
        {
            var settings = new SimulationSettings
            {
                TimeStep = step,
                MaxTimeStep = Math.Max(step * 10, step),
                MinTimeStep = Math.Min(1e-12, step / 1000),
                Integration = IntegrationMethod.Trapezoidal,
                UseInitialConditions = true,
                ProbeSampleInterval = seconds / 4000,
            };
            var simulator = Simulator(copy, settings);
            simulator.RunTransient(seconds, cancel);
            return copy.Probes.Select(p => new Plots.Trace(p.Label, p.Kind, p.HistoryBuffer.ToArray())).ToList();
        }, cancel).ConfigureAwait(false);

        // What the agent saw goes into the circuit's own probes, so the scope shows the same run.
        await Run("ran the circuit", s =>
        {
            s.ShowRun(() =>
            {
                foreach (var probe in s.Circuit.Probes)
                {
                    var trace = traces.FirstOrDefault(t => t.Label == probe.Label);
                    if (trace is null) continue;
                    probe.ResetHistory();
                    foreach (var point in trace.Samples) probe.Record(point.Time, point.Value);
                }
            });
            return true;
        }, cancel).ConfigureAwait(false);

        var report = new JsonObject
        {
            ["duration"] = SiPrefix.Format(seconds, "s"),
            ["time_step"] = SiPrefix.Format(step, "s"),
        };
        if (start > 0) report["from"] = SiPrefix.Format(start, "s");
        var measured = new JsonArray();
        var shown = traces.Select(t => t with { Samples = t.Samples.Where(p => p.Time >= start).ToList() }).ToList();
        foreach (var t in shown) measured.Add(Measure(t));
        report["probes"] = measured;
        if (!plot) return new CallToolResult { Content = [new TextContentBlock { Text = report.ToJsonString(Json.Options) }] };
        var png = await Solve(() => Plots.Transient(shown, width, height), cancel).ConfigureAwait(false);
        return new CallToolResult { Content = [ImageContentBlock.FromBytes(png, "image/png"), new TextContentBlock { Text = report.ToJsonString(Json.Options) }] };
    }

    [McpServerTool(Name = "get_trace", ReadOnly = true, Idempotent = true)]
    [Description("The samples a probe recorded in the last run (run_transient, or the app's own Run), as [time, value] pairs, thinned to at most points pairs evenly across the time asked for: for reading exact values off a trace.")]
    public Task<string> GetTrace(
        [Description("The probe's label.")] string probe,
        [Description("Start time in seconds (or text such as \"2m\"). Default: the first sample.")] JsonElement? from = null,
        [Description("End time in seconds. Default: the last sample.")] JsonElement? to = null,
        [Description("At most how many pairs, from 2 to 500. Default 50.")] int points = 50,
        CancellationToken cancel = default)
    {
        if (points is < 2 or > 500) throw new McpException("points must be from 2 to 500.");
        double? a = from is { ValueKind: not JsonValueKind.Null } f ? Seconds(f, "from") : null, b = to is { ValueKind: not JsonValueKind.Null } t ? Seconds(t, "to") : null;
        return Read("get_trace", s =>
        {
            var p = Census.Probe(s.Circuit, probe);
            var samples = p.HistoryBuffer.ToArray().Where(x => (a is null || x.Time >= a) && (b is null || x.Time <= b)).ToList();
            if (samples.Count == 0) throw new InvalidOperationException($"{p.Label} has no samples{(a is null && b is null ? "" : " in that time")}. run_transient records them.");
            var stride = Math.Max(1.0, samples.Count / (double)points);
            var pairs = new JsonArray();
            for (double i = 0; i < samples.Count && pairs.Count < points; i += stride)
            {
                var x = samples[(int)i];
                pairs.Add(new JsonArray(Round(x.Time), Round(x.Value)));
            }
            return new JsonObject { ["probe"] = p.Label, ["unit"] = p.Unit, ["samples"] = samples.Count, ["pairs"] = pairs };
        }, cancel);
    }

    [McpServerTool(Name = "ac_sweep")]
    [Description("A small-signal frequency response (Simulate › Frequency Response): the circuit is linearised at its bias point and driven by every source's AC magnitude (a function generator's is 1 V by default) across a range of frequencies, and each voltage probe's gain and phase are reported (the gain at the low end, the peak, the −3 dB corner and the gain at the top) with a Bode plot. For filters and amplifiers.")]
    public async Task<CallToolResult> AcSweep(
        [Description("The lowest frequency in Hz (or text, e.g. \"10\"). Default 10.")] JsonElement? start_hz = null,
        [Description("The highest frequency in Hz (e.g. \"1M\"). Default 1 MHz.")] JsonElement? stop_hz = null,
        [Description("Points per decade, from 5 to 200. Default 20.")] int points_per_decade = 20,
        [Description("Include the Bode plot. Default true.")] bool plot = true,
        [Description("The plot's width in pixels. Default 900.")] int width = 900,
        [Description("The plot's height in pixels. Default 600.")] int height = 600,
        CancellationToken cancel = default)
    {
        var low = Seconds(start_hz, "start_hz", 10);
        var high = Seconds(stop_hz, "stop_hz", 1e6);
        if (low <= 0 || high <= low || high > 1e12) throw new McpException("start_hz must be more than 0 and stop_hz more than start_hz (and at most 1 THz).");
        if (points_per_decade is < 5 or > 200) throw new McpException("points_per_decade must be from 5 to 200.");
        Size(width, height);
        var copy = await Snapshot(cancel).ConfigureAwait(false);
        if (copy.Probes.Count == 0) throw new McpException("The circuit has no probes: add_probe on the output (and the input, to compare) first.");

        var result = await Solve(() =>
        {
            var simulator = Simulator(copy, null);
            simulator.Reset();
            simulator.SolveOperatingPoint();
            simulator.ResolveProbes();
            return new AcSweep(simulator).Run(new AcSweepRequest(low, high, points_per_decade), cancel);
        }, cancel).ConfigureAwait(false);

        var report = new JsonObject { ["from"] = SiPrefix.Format(low, "Hz"), ["to"] = SiPrefix.Format(high, "Hz") };
        var traces = new JsonArray();
        foreach (var t in result.Traces)
        {
            var db = Enumerable.Range(0, result.Frequencies.Count).Select(t.Decibels).ToList();
            var peak = db.IndexOf(db.Max());
            var o = new JsonObject
            {
                ["probe"] = t.Label,
                ["gain_db_at_start"] = Round(db[0]),
                ["phase_at_start"] = Round(t.Degrees(0)),
                ["peak_gain_db"] = Round(db[peak]),
                ["peak_at"] = SiPrefix.Format(result.Frequencies[peak], "Hz"),
                ["gain_db_at_stop"] = Round(db[^1]),
                ["phase_at_stop"] = Round(t.Degrees(db.Count - 1)),
            };
            if (result.CornerOf(t.Label) is { } corner) o["corner_minus_3db"] = SiPrefix.Format(corner, "Hz");
            if (db.All(x => x < -250)) o["note"] = "No response at all: nothing in the circuit has an AC magnitude to drive it (a Function Generator or Waveform Source does; a DC source's AcMagnitude is 0 unless set).";
            traces.Add(o);
        }
        report["probes"] = traces;
        if (!plot) return new CallToolResult { Content = [new TextContentBlock { Text = report.ToJsonString(Json.Options) }] };
        var png = await Solve(() => Plots.Bode(result, width, height), cancel).ConfigureAwait(false);
        return new CallToolResult { Content = [ImageContentBlock.FromBytes(png, "image/png"), new TextContentBlock { Text = report.ToJsonString(Json.Options) }] };
    }

    [McpServerTool(Name = "dc_sweep")]
    [Description("A DC sweep (Simulate › DC Sweep): walks one setting (a source's Voltage, a resistor's Resistance, any number setting, or the temperature) across a range, solving the operating point at each value, and reports every probe against it with a plot; optionally a second setting is stepped too, giving a family of curves (a transistor's output curves: sweep the collector supply, step the base current). For transfer curves, thresholds, regulation and the like.")]
    public async Task<CallToolResult> DcSweep(
        [Description("What to sweep: Part.Setting, e.g. \"V1.Voltage\" or \"R2.Resistance\", or \"temperature\" (°C).")] string sweep,
        [Description("The first value (number or engineering text).")] JsonElement start,
        [Description("The last value.")] JsonElement stop,
        [Description("How many values, from 2 to 1001, counting both ends. Default 51.")] int points = 51,
        [Description("A second setting to step, as sweep. Optional.")] string? step = null,
        [Description("The stepped setting's first value.")] JsonElement? step_start = null,
        [Description("Its last value.")] JsonElement? step_stop = null,
        [Description("How many steps, from 2 to 12. Default 5.")] int step_points = 5,
        [Description("Include the plot. Default true.")] bool plot = true,
        [Description("The plot's width in pixels. Default 900.")] int width = 900,
        [Description("The plot's height in pixels. Default 550.")] int height = 550,
        CancellationToken cancel = default)
    {
        if (points is < 2 or > 1001) throw new McpException("points must be from 2 to 1001.");
        if (step is not null && step_points is < 2 or > 12) throw new McpException("step_points must be from 2 to 12.");
        if (step is not null && (step_start is not { ValueKind: not JsonValueKind.Null } || step_stop is not { ValueKind: not JsonValueKind.Null })) throw new McpException("A step needs step_start and step_stop as well.");
        Size(width, height);
        double a = Seconds(start, "start"), b = Seconds(stop, "stop");
        var copy = await Snapshot(cancel).ConfigureAwait(false);
        if (copy.Probes.Count == 0) throw new McpException("The circuit has no probes: add_probe on what you want to see against the sweep first.");
        var primary = Target(copy, sweep, a, b, points);
        var stepped = step is null ? null : Target(copy, step, Seconds(step_start!.Value, "step_start"), Seconds(step_stop!.Value, "step_stop"), step_points);

        var result = await Solve(() =>
        {
            var simulator = Simulator(copy, null);
            simulator.Reset();
            simulator.SolveOperatingPoint();
            simulator.ResolveProbes();
            return new Cirq.Engine.Simulation.DcSweep(simulator).Run(new DcSweepRequest(primary, stepped), cancel);
        }, cancel).ConfigureAwait(false);

        var report = new JsonObject { ["x"] = result.XLabel };
        if (result.StepLabel is { } stepLabel) report["stepped"] = stepLabel;
        // A table of at most about 21 rows, evenly through the sweep, for reading values off.
        var stride = Math.Max(1, (result.X.Count - 1) / 20);
        var rows = new JsonArray();
        for (var i = 0; i < result.X.Count; i += stride)
        {
            var row = new JsonObject { ["x"] = Round(result.X[i]) };
            foreach (var curve in result.Curves)
                foreach (var t in curve.Traces)
                    row[double.IsNaN(curve.StepValue) ? t.Label : $"{t.Label} @ {SiPrefix.Format(curve.StepValue)}"] = double.IsNaN(t.Values[i]) ? null : Round(t.Values[i]);
            rows.Add(row);
        }
        report["table"] = rows;
        if (result.FailedPoints > 0) report["note"] = $"{result.FailedPoints} points didn't converge and are left empty.";
        if (!plot) return new CallToolResult { Content = [new TextContentBlock { Text = report.ToJsonString(Json.Options) }] };
        var png = await Solve(() => Plots.DcSweep(result, width, height), cancel).ConfigureAwait(false);
        return new CallToolResult { Content = [ImageContentBlock.FromBytes(png, "image/png"), new TextContentBlock { Text = report.ToJsonString(Json.Options) }] };
    }

    [McpServerTool(Name = "transport")]
    [Description("Works the editor's own simulation, as its Run (F5), Pause and Reset (F8) buttons do: run lets the person watch the circuit live (the scope scrolling, LEDs lighting, displays counting), pause stops it, reset rebuilds it and solves the bias point again. Only in the app; the headless server has no live simulation (use run_transient).")]
    public Task<string> Transport(
        [Description("run, pause or reset.")] string action,
        CancellationToken cancel = default) =>
        Read($"pressed {action}", s =>
        {
            if (!Enum.TryParse<TransportAction>(action?.Trim(), true, out var a) || !Enum.IsDefined(a) || int.TryParse(action, out _))
                throw new ArgumentException($"action is run, pause or reset, not \"{action}\".");
            return new JsonObject { ["status"] = s.Transport(a) };
        }, cancel);

    [McpServerTool(Name = "set_scope", Idempotent = true)]
    [Description("Sets the editor's oscilloscope, so the person sees what matters: its time per division (ten divisions across) and volts per division. Only in the app.")]
    public Task<string> SetScope(
        [Description("Seconds per division, e.g. \"200u\". Optional.")] JsonElement? timebase = null,
        [Description("Volts per division, e.g. 2. Optional.")] JsonElement? volts_per_division = null,
        CancellationToken cancel = default)
    {
        double? t = timebase is { ValueKind: not JsonValueKind.Null } tb ? Seconds(tb, "timebase") : null, v = volts_per_division is { ValueKind: not JsonValueKind.Null } vd ? Seconds(vd, "volts_per_division") : null;
        if (t is <= 0 or > 10) throw new McpException("timebase must be more than 0 and at most 10 s.");
        if (v is <= 0 or > 1000) throw new McpException("volts_per_division must be more than 0 and at most 1000.");
        return Read("set the scope", s =>
        {
            var scope = s.Scope(t, v) ?? throw new NotSupportedException("The headless server has no scope; run_transient's plot shows the traces.");
            return new JsonObject { ["timebase"] = SiPrefix.Format(scope.Timebase, "s") + "/div", ["volts_per_division"] = SiPrefix.Format(scope.VoltsPerDivision, "V") + "/div" };
        }, cancel);
    }

    // ───────────────────────────── plumbing ─────────────────────────────

    /// <summary>A copy of the circuit to solve away from the host's thread.</summary>
    private Task<Circuit> Snapshot(CancellationToken cancel) =>
        Run("read the circuit", s =>
        {
            if (s.Circuit.Components.Count == 0) throw new InvalidOperationException("The circuit is empty: add_parts first.");
            return CircuitSerializer.FromJson(CircuitSerializer.ToJson(s.Circuit)).Circuit;
        }, cancel);

    private static CircuitSimulator Simulator(Circuit circuit, SimulationSettings? settings)
    {
        // The analyses that solve a bias point leave capacitors open, as the editor's own do; only a transient run
        // starts them at their initial voltage, so a source switched on at t = 0 is a true step.
        settings ??= new SimulationSettings();
        settings.TemperatureKelvin = circuit.AmbientTemperatureCelsius + 273.15;
        settings.AdaptiveTimeStep = circuit.AdaptiveTimeStep;
        return new CircuitSimulator(circuit, settings);
    }

    /// <summary>Solves on a worker thread, within <see cref="RunLimit"/>, with the solver's failures put in words.</summary>
    private static async Task<T> Solve<T>(Func<T> work, CancellationToken cancel)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(RunLimit);
        try
        {
            return await Task.Run(work, limit.Token).WaitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new McpException($"The run took longer than {RunLimit.TotalSeconds:0} seconds and was stopped. Simulate less time, or use a larger time_step.");
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(e switch
            {
                ConvergenceException => $"The solver couldn't find a solution: {e.Message} Check for a part wired to nothing, a node with no path to ground, or two sources fighting.",
                CircuitTopologyException => $"The circuit can't be solved as wired: {e.Message}",
                SingularMatrixException => $"The circuit can't be solved as wired: {e.Message} A net with no DC path to ground (a capacitor in series with nothing else), a missing Ground part, or a loop of voltage sources makes this.",
                _ => Explain(e),
            }, e);
        }
    }

    /// <summary>One probe's measurements, in engineering notation.</summary>
    private static JsonObject Measure(Plots.Trace t)
    {
        var o = new JsonObject { ["probe"] = t.Label, ["kind"] = t.Kind.ToString().ToLowerInvariant() };
        if (t.Samples.Count < 2) { o["note"] = "No samples."; return o; }
        var m = TraceMeasurements.OfAll(t.Samples);
        var unit = t.Unit;
        if (t.Kind == ProbeKind.Logic)
        {
            o["high_fraction"] = Round(m.Mean);
            o["final"] = t.Samples[^1].Value >= 0.75 ? "1" : t.Samples[^1].Value <= 0.25 ? "0" : "x";
        }
        else
        {
            o["min"] = SiPrefix.Format(m.Minimum, unit);
            o["max"] = SiPrefix.Format(m.Maximum, unit);
            o["mean"] = SiPrefix.Format(m.Mean, unit);
            o["rms"] = SiPrefix.Format(m.Rms, unit);
            o["peak_to_peak"] = SiPrefix.Format(m.PeakToPeak, unit);
            o["final"] = SiPrefix.Format(t.Samples[^1].Value, unit);
        }
        if (m.Frequency is { } f) o["frequency"] = SiPrefix.Format(f, "Hz");
        if (m.DutyCycle is { } duty) o["duty_cycle"] = $"{duty * 100:0.#} %";
        if (m.RiseTime is { } rise) o["rise_time"] = SiPrefix.Format(rise, "s");
        if (m.FallTime is { } fall) o["fall_time"] = SiPrefix.Format(fall, "s");
        return o;
    }

    private static SweepTarget Target(Circuit circuit, string what, double start, double stop, int points)
    {
        if (string.Equals(what?.Trim(), "temperature", StringComparison.OrdinalIgnoreCase)) return SweepTarget.OverTemperature(start, stop, points);
        var text = what?.Trim() ?? "";
        var dot = text.IndexOf('.');
        if (dot <= 0) throw new McpException($"\"{what}\" isn't something to sweep: give Part.Setting, as \"V1.Voltage\", or \"temperature\".");
        CircuitComponent part;
        try { part = Pins.Part(circuit, text[..dot]); }
        catch (ArgumentException e) { throw new McpException(e.Message); }
        var name = text[(dot + 1)..];
        var property = part.GetType().GetProperties().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
            && p.CanWrite && (p.PropertyType == typeof(double) || p.PropertyType == typeof(int)));
        if (property is null)
        {
            var numbers = ComponentReflection.EditableProperties(part.GetType()).Where(p => p.PropertyType == typeof(double) || p.PropertyType == typeof(int)).Select(p => p.Name).ToList();
            throw new McpException($"{part.Name} has no number setting {name} to sweep. Its number settings: {string.Join(", ", numbers)}.");
        }
        return new SweepTarget(part, property.Name, start, stop, points);
    }

    /// <summary>A number from a JSON number or plain or engineering text: 0.005, "5m", "5 ms", "100u", "2.2k", "10 kHz".</summary>
    private static double Seconds(JsonElement value, string what)
    {
        try
        {
            var number = Settings.Number(value, what);
            if (double.IsFinite(number)) return number;
        }
        catch (ArgumentException) { }
        throw new McpException($"{what} must be a number, plain (0.005) or in engineering notation (\"5m\", \"100u\", \"2.2k\"); not {value.GetRawText()}.");
    }

    private static double Seconds(JsonElement? value, string what, double fallback) => value is { ValueKind: not JsonValueKind.Null } v ? Seconds(v, what) : fallback;

    private static void Size(int width, int height)
    {
        if (width is < 200 or > 1600 || height is < 150 or > 1200) throw new McpException("width must be from 200 to 1600 pixels and height from 150 to 1200.");
    }

    private static double Round(double v) => double.IsFinite(v) ? double.Parse(v.ToString("G5", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture) : v;
}
