using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

/// <summary>
/// Drives a fixed sequence of bits onto one or more lines, one step per clock period.
/// <para>
/// The digital side had a clock and a switch: something that changes forever at a fixed rate, and
/// something a person operates. Neither of those is a <i>stimulus</i>. A counter that should reset
/// on the third pulse, a shift register being loaded, a state machine that has to see a particular
/// sequence of inputs to reach the state you are debugging — all of those need a pattern, and
/// without one the only way to produce them was to sit in front of the circuit flipping a toggle
/// while it ran.
/// </para>
/// <para>
/// It is the logic analyser's own notation read backwards: a row per line, a character per step.
/// <code>
/// 00110011
/// 00001111
/// </code>
/// Two lines, eight steps, the second changing at half the rate of the first — which is a two-bit
/// count, written the way it appears on a timing diagram.
/// </para>
/// </summary>
public partial class PatternGenerator : DigitalComponent, IBreakpointSource
{
    private LogicState[][] _steps = [];
    private string _compiledFrom = string.Empty;
    private int _compiledLines;

    /// <summary>
    /// A generator with a fixed number of lines.
    /// <para>
    /// Fixed because a component's pins are fixed: <see cref="CircuitComponent.Terminals"/> is set
    /// at construction and nothing may change it afterwards, which is what stops a topology
    /// shifting under a solve that is halfway through. So the pattern text does not grow the part —
    /// rows past the last line are ignored, and lines the pattern says nothing about are released
    /// rather than driven, which is the honest answer for a pin nothing has an opinion about.
    /// </para>
    /// </summary>
    /// <param name="lines">How many output lines. Four covers a nibble and a couple of controls.</param>
    public PatternGenerator(int lines = 4)
    {
        PropagationDelay = 0;

        List<Terminal> outputs = [];

        var count = Math.Clamp(lines, 1, 16);

        for (var i = 0; i < count; i++)
        {
            var y = (i - ((count - 1) / 2.0)) * 20.0;

            outputs.Add(new Terminal($"d{i}", $"D{i}", TerminalType.Output, new Point(30, y)));
        }

        Terminals = outputs;
        OutputTerminals = outputs;
        InputTerminals = [];

        ConfigurePins([], outputs);
    }

    /// <summary>
    /// The pattern, a line per output and a character per step.
    /// <para>
    /// <c>0</c> and <c>1</c> are the levels; <c>-</c>, <c>z</c> and <c>Z</c> release the line,
    /// which is how a pattern shares a bus with something else. Spaces are ignored, so a long
    /// pattern can be grouped into nibbles to be read at all.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial string Pattern { get; set; } = "0011 0011\n0000 1111";

    /// <summary>How many steps a second are played.</summary>
    [ObservableProperty]
    [Operable("Rate", Minimum = 1, Maximum = 1e6, Unit = "Hz", IsLogarithmic = true)]
    public partial double StepRate { get; set; } = 1e3;

    /// <summary>What to do once the pattern has been played out.</summary>
    [ObservableProperty]
    public partial bool Repeats { get; set; } = true;

    /// <summary>How long to wait before the first step, in seconds.</summary>
    [ObservableProperty]
    public partial double StartDelay { get; set; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    public override string ComponentType => "Pattern Generator";

    public override string DesignatorPrefix => "PG";

    public override string ValueLabel
    {
        get
        {
            Compile();

            return _steps.Length == 0
                ? "empty"
                : $"{_steps.Length} × {_compiledLines} at {SiPrefix.Format(StepRate, "Hz")}";
        }
    }

    /// <summary>How long one step lasts.</summary>
    public double StepPeriod => StepRate > 0 ? 1.0 / StepRate : double.PositiveInfinity;

    /// <summary>How many steps the pattern has.</summary>
    public int StepCount
    {
        get
        {
            Compile();

            return _steps.Length;
        }
    }

    /// <summary>Which step is being played at a moment, or −1 before the first and after the last.</summary>
    public int StepAt(double time)
    {
        Compile();

        if (!IsEnabled || _steps.Length == 0 || StepRate <= 0) return -1;

        var elapsed = time - StartDelay;
        if (elapsed < 0) return -1;

        var index = (int)Math.Floor(elapsed / StepPeriod);

        if (index < _steps.Length) return index;

        return Repeats ? index % _steps.Length : _steps.Length - 1;
    }

    /// <summary>What one line should be driving at a moment.</summary>
    public LogicState StateAt(double time, int line)
    {
        var step = StepAt(time);

        // Unknown is a released line rather than a level, which is what a pin the pattern never
        // mentions should be: driving it low would be this part asserting something about a line
        // nobody wrote a row for.
        if (step < 0 || line < 0 || line >= _compiledLines) return LogicState.Unknown;

        return _steps[step][line];
    }

    public override void EvaluateLogic(IDigitalContext context)
    {
        for (var line = 0; line < OutputTerminals.Count; line++)
            context.Schedule(this, line, StateAt(context.Time, line), 0);
    }

    /// <summary>
    /// The next step boundary, so the solver lands exactly on every edge.
    /// <para>
    /// Without it the transient loop steps over short steps at a fast rate, and a pattern that
    /// looks right in the box arrives at the circuit with pulses missing.
    /// </para>
    /// </summary>
    public double? NextBreakpointAfter(double time)
    {
        Compile();

        if (!IsEnabled || _steps.Length == 0 || StepRate <= 0) return null;

        var period = StepPeriod;
        var elapsed = time - StartDelay;

        if (elapsed < 0) return StartDelay;

        var index = Math.Floor(elapsed / period);

        // One past the end is where a pattern that does not repeat finally stops moving.
        if (!Repeats && index >= _steps.Length - 1) return null;

        return StartDelay + ((index + 1) * period);
    }

    partial void OnPatternChanged(string value) => NotifyValueChanged();

    partial void OnStepRateChanged(double value) => NotifyValueChanged();

    // ---- the pattern -------------------------------------------------------

    private void Compile()
    {
        if (_compiledFrom == Pattern) return;

        _compiledFrom = Pattern;

        List<List<LogicState>> lines = [];

        foreach (var row in (Pattern ?? string.Empty).Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            List<LogicState> states = [];

            foreach (var character in row)
            {
                switch (character)
                {
                    case '0': states.Add(LogicState.Low); break;
                    case '1': states.Add(LogicState.High); break;
                    case '-' or 'z' or 'Z': states.Add(LogicState.Unknown); break;

                    // Anything else — spaces, underscores, the vertical bars people draw timing
                    // diagrams with — is grouping rather than a step. A pattern long enough to
                    // matter is a pattern nobody can count the characters of without them.
                    default: break;
                }
            }

            if (states.Count > 0) lines.Add(states);
        }

        _compiledLines = lines.Count;

        var steps = lines.Count == 0 ? 0 : lines.Max(l => l.Count);

        _steps = new LogicState[steps][];

        for (var step = 0; step < steps; step++)
        {
            _steps[step] = new LogicState[lines.Count];

            for (var line = 0; line < lines.Count; line++)
            {
                // A short row holds its last value rather than going unknown, so a line that is
                // constant can be written as a single character instead of being padded out to
                // the length of the longest one.
                _steps[step][line] = step < lines[line].Count
                    ? lines[line][step]
                    : lines[line][^1];
            }
        }
    }
}
