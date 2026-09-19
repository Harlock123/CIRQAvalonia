using Cirq.Core.Units;

namespace Cirq.Components.Boards;

/// <summary>What a configurable pin is doing.</summary>
public enum PinMode
{
    /// <summary>Reading the node. The pin presents a high impedance and drives nothing.</summary>
    Input,

    /// <summary>Reading, with the internal pull-up to the logic rail engaged.</summary>
    InputPullUp,

    /// <summary>Reading, with the internal pull-down to ground engaged.</summary>
    InputPullDown,

    /// <summary>Driven low.</summary>
    OutputLow,

    /// <summary>Driven high at the board's logic voltage.</summary>
    OutputHigh,

    /// <summary>A square wave at <see cref="PinSetting.Frequency"/>.</summary>
    Clock,

    /// <summary>A square wave at a set duty cycle — the same mechanism as a clock, named for intent.</summary>
    Pwm,

    /// <summary>Steps through <see cref="PinSetting.Pattern"/> and repeats it.</summary>
    Sequence,

    /// <summary>Steps through the pattern once, then holds its last step.</summary>
    SequenceOnce,
}

/// <summary>
/// One pin's configuration.
/// <para>
/// <see cref="Pattern"/> is held as a normalised string of <c>0</c>, <c>1</c> and <c>z</c> rather
/// than a parsed list, so the record keeps value equality — a list field would compare by
/// reference and quietly break every round-trip comparison.
/// </para>
/// </summary>
public sealed record PinSetting(
    string PinName,
    PinMode Mode,
    double Frequency = 1000.0,
    double DutyCycle = 0.5,
    string Pattern = "")
{
    public bool IsDriving => Mode is PinMode.OutputLow or PinMode.OutputHigh
        or PinMode.Clock or PinMode.Pwm or PinMode.Sequence or PinMode.SequenceOnce;

    public bool IsTimed => Mode is PinMode.Clock or PinMode.Pwm
        or PinMode.Sequence or PinMode.SequenceOnce;

    public bool IsSequence => Mode is PinMode.Sequence or PinMode.SequenceOnce;

    /// <summary>How long one full pass of the pattern takes, in seconds.</summary>
    public double PatternDuration => Frequency > 0 ? Pattern.Length / Frequency : double.PositiveInfinity;
}

/// <summary>
/// Parses the compact pin-setup string a board carries.
/// <para>
/// A board has forty-odd pins, and the properties inspector is built by reflection over scalar
/// properties — forty separate properties would be unreadable, and most circuits configure two or
/// three pins. One text field keeps the whole setup visible at a glance, round-trips through the
/// existing file format with no special cases, and reads sensibly in a diff.
/// </para>
/// <example>
/// <code>GPIO17=high; GPIO18=clock@1kHz; GPIO27=in-pullup; GPIO12=pwm@500Hz:25%</code>
/// </example>
/// </summary>
public static class PinConfiguration
{
    /// <summary>The result of reading a setup string: what it asked for, and anything wrong with it.</summary>
    public sealed record Result(IReadOnlyList<PinSetting> Settings, IReadOnlyList<string> Problems)
    {
        public bool IsValid => Problems.Count == 0;
    }

    /// <summary>
    /// Reads a setup string against a board. Unknown pin names and malformed entries are reported
    /// rather than thrown: a typo in one entry should not discard the other nine, and the board
    /// shows the complaint in its status readout.
    /// </summary>
    public static Result Parse(string? text, BoardProfile profile)
    {
        List<PinSetting> settings = [];
        List<string> problems = [];

        if (string.IsNullOrWhiteSpace(text)) return new Result(settings, problems);

        // Several pins share a name on a real header — a Pi has eight GNDs and two 5V pins — so
        // the first one wins rather than the lookup throwing.
        var byName = new Dictionary<string, BoardPin>(StringComparer.OrdinalIgnoreCase);
        foreach (var pin in profile.AllPins) byName.TryAdd(pin.Name, pin);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in text.Split([';', ',', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = raw.Trim();
            if (entry.Length == 0) continue;

            var equals = entry.IndexOf('=');
            if (equals <= 0)
            {
                problems.Add($"'{entry}' is not pin=mode");
                continue;
            }

            var pinName = entry[..equals].Trim();
            var spec = entry[(equals + 1)..].Trim();

            if (!byName.TryGetValue(pinName, out var pin))
            {
                problems.Add($"{pinName} is not a pin on this board");
                continue;
            }

            if (!pin.IsConfigurable)
            {
                problems.Add($"{pin.Name} is a {pin.Function.ToString().ToLowerInvariant()} pin and cannot be set");
                continue;
            }

            if (!seen.Add(pin.Name))
            {
                problems.Add($"{pin.Name} is configured more than once");
                continue;
            }

            if (!TryParseSpec(spec, out var mode, out var frequency, out var duty, out var pattern, out var reason))
            {
                problems.Add($"{pin.Name}: {reason}");
                continue;
            }

            // An analog input has no digital buffer, so it can be read but never driven.
            if (pin.Function is PinFunction.AnalogIn && mode is not
                (PinMode.Input or PinMode.InputPullUp or PinMode.InputPullDown))
            {
                problems.Add($"{pin.Name} is an analog input and can only be read");
                continue;
            }

            if (mode is PinMode.Pwm && !pin.SupportsPwm)
                problems.Add($"{pin.Name} has no hardware PWM (simulated anyway)");

            settings.Add(new PinSetting(pin.Name, mode, frequency, duty, pattern));
        }

        return new Result(settings, problems);
    }

    /// <summary>Longest pattern accepted, which is far more steps than a schematic needs.</summary>
    public const int MaximumPatternLength = 256;

    private static bool TryParseSpec(
        string spec, out PinMode mode, out double frequency, out double duty,
        out string pattern, out string reason)
    {
        mode = PinMode.Input;
        frequency = 1000.0;
        duty = 0.5;
        pattern = string.Empty;
        reason = string.Empty;

        // clock@1kHz  /  pwm@500Hz:25%
        var at = spec.IndexOf('@');
        var head = (at < 0 ? spec : spec[..at]).Trim().ToLowerInvariant();

        switch (head)
        {
            case "in" or "input" or "read": mode = PinMode.Input; break;
            case "in-pullup" or "pullup" or "input-pullup": mode = PinMode.InputPullUp; break;
            case "in-pulldown" or "pulldown" or "input-pulldown": mode = PinMode.InputPullDown; break;
            case "high" or "out-high" or "1": mode = PinMode.OutputHigh; break;
            case "low" or "out-low" or "0": mode = PinMode.OutputLow; break;
            case "clock" or "clk": mode = PinMode.Clock; break;
            case "pwm": mode = PinMode.Pwm; break;
            case "seq" or "sequence": mode = PinMode.Sequence; break;
            case "once" or "seq-once": mode = PinMode.SequenceOnce; break;
            default:
                reason = $"'{head}' is not a mode";
                return false;
        }

        if (at < 0)
        {
            if (mode is PinMode.Sequence or PinMode.SequenceOnce)
            {
                reason = $"{head} needs a step rate and a pattern, as {head}@1kHz:1011";
                return false;
            }

            if (mode is not (PinMode.Clock or PinMode.Pwm)) return true;
            reason = $"{head} needs a frequency, as {head}@1kHz";
            return false;
        }

        var tail = spec[(at + 1)..].Trim();
        var colon = tail.IndexOf(':');
        var frequencyText = (colon < 0 ? tail : tail[..colon]).Trim();

        if (!SiPrefix.TryParse(frequencyText.TrimEnd('h', 'H', 'z', 'Z'), out frequency) || frequency <= 0)
        {
            reason = $"'{frequencyText}' is not a frequency";
            return false;
        }

        if (mode is PinMode.Sequence or PinMode.SequenceOnce)
        {
            if (colon < 0)
            {
                reason = $"{head} needs a pattern after the rate, as {head}@1kHz:1011";
                return false;
            }

            // Spaces and underscores are grouping for the reader — 1100_1010 beats 11001010.
            var raw = tail[(colon + 1)..].Replace("_", string.Empty).Replace(" ", string.Empty);

            if (raw.Length == 0)
            {
                reason = "the pattern is empty";
                return false;
            }

            if (raw.Length > MaximumPatternLength)
            {
                reason = $"the pattern is {raw.Length} steps, over the {MaximumPatternLength} limit";
                return false;
            }

            foreach (var c in raw)
            {
                if (c is '0' or '1' or 'z' or 'Z') continue;
                reason = $"'{c}' is not a pattern step — use 0, 1 or z";
                return false;
            }

            pattern = raw.ToLowerInvariant();
            return true;
        }

        if (colon >= 0)
        {
            var dutyText = tail[(colon + 1)..].Trim().TrimEnd('%');
            if (!double.TryParse(dutyText, out var percent) || percent is <= 0 or >= 100)
            {
                reason = $"'{dutyText}' is not a duty cycle between 0 and 100";
                return false;
            }

            duty = percent / 100.0;
        }
        else if (mode is PinMode.Pwm)
        {
            // PWM without a stated duty is a 50% square wave, same as a clock.
            duty = 0.5;
        }

        return true;
    }

    /// <summary>Renders settings back to the compact form, so a programmatic change stays editable.</summary>
    public static string Format(IEnumerable<PinSetting> settings) =>
        string.Join("; ", settings.Select(s => s.Mode switch
        {
            PinMode.Input => $"{s.PinName}=in",
            PinMode.InputPullUp => $"{s.PinName}=in-pullup",
            PinMode.InputPullDown => $"{s.PinName}=in-pulldown",
            PinMode.OutputHigh => $"{s.PinName}=high",
            PinMode.OutputLow => $"{s.PinName}=low",
            PinMode.Clock => $"{s.PinName}=clock@{SiPrefix.Format(s.Frequency, "Hz")}",
            PinMode.Pwm => $"{s.PinName}=pwm@{SiPrefix.Format(s.Frequency, "Hz")}:{s.DutyCycle * 100:0.#}%",
            PinMode.Sequence => $"{s.PinName}=seq@{SiPrefix.Format(s.Frequency, "Hz")}:{s.Pattern}",
            PinMode.SequenceOnce => $"{s.PinName}=once@{SiPrefix.Format(s.Frequency, "Hz")}:{s.Pattern}",
            _ => $"{s.PinName}=in",
        }));
}
