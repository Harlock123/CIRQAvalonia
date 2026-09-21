namespace Cirq.Engine.Decoding;

/// <summary>
/// Decodes 1-Wire, where the data, the clock and often the power all share one conductor.
/// <para>
/// Everything is in <b>how long the master holds the line down</b>. The line idles high through a
/// pull-up; the master starts every bit by pulling it low, and the width of that pull decides what
/// the bit is: a short pull is a one, a long pull is a zero. For a read slot the master pulls
/// briefly and then lets go, and the <i>slave</i> decides whether to keep holding it down.
/// </para>
/// <para>
/// A reset is the same idea an order of magnitude slower — the master holds the line down for
/// almost half a millisecond, and any device present answers by pulling it down itself once the
/// master lets go. That presence pulse is the only handshake there is.
/// </para>
/// </summary>
public static class OneWireDecoder
{
    public const string Protocol = "1-Wire";

    // Standard-speed timings from the datasheets, in seconds.
    private const double ResetLow = 480e-6;
    private const double SlotMaximum = 120e-6;
    private const double ZeroThreshold = 30e-6;

    public static DecodeResult Decode(LogicTrace line)
    {
        if (line.IsEmpty) return DecodeResult.Nothing(Protocol, "Assign a trace to the line.");

        // A write-one slot is six microseconds wide on a real part. Miss one and every bit after
        // it is in the wrong place, so this refuses rather than producing shifted bytes.
        if (line.SamplingWarning is { } coarse) return DecodeResult.Nothing(Protocol, coarse);

        List<DecodedSpan> spans = [];

        var bits = 0;
        var value = 0;
        var byteStart = 0.0;
        var afterReset = false;

        // Every stretch of the line being low, with how long it lasted.
        foreach (var (start, end) in LowPulses(line))
        {
            var width = end - start;

            if (width >= ResetLow * 0.8)
            {
                Flush(spans, ref bits, ref value, byteStart, end);

                spans.Add(new DecodedSpan(start, end, SpanKind.Control, "RESET",
                    $"The master held the line down for {width * 1e6:0} µs. Anything on the bus " +
                    "answers by pulling it down itself once the master lets go."));

                afterReset = true;
                continue;
            }

            // A presence pulse is the first low after a reset, and it is the slave doing it.
            if (afterReset)
            {
                spans.Add(new DecodedSpan(start, end, SpanKind.Response, "PRESENCE",
                    $"A device answered the reset by holding the line down for {width * 1e6:0} µs"));

                afterReset = false;
                continue;
            }

            if (width > SlotMaximum) continue;

            if (bits == 0) byteStart = start;

            // Short pull is a one, long pull is a zero. That is the entire encoding.
            var bit = width < ZeroThreshold;

            // Least significant bit first, which 1-Wire does and I²C does not.
            if (bit) value |= 1 << bits;

            if (++bits < 8) continue;

            spans.Add(new DecodedSpan(byteStart, end, SpanKind.Data, $"0x{value:X2}",
                $"Byte {value} (0x{value:X2}){Command(value)}, least significant bit first"));

            bits = 0;
            value = 0;
        }

        Flush(spans, ref bits, ref value, byteStart, line.End);

        var bytes = spans.Count(s => s.Kind == SpanKind.Data);

        var summary = spans.Count == 0
            ? "Nothing decoded. 1-Wire idles high; every bit starts with the master pulling it down."
            : $"{spans.Count(s => s.Text == "RESET")} reset(s), {bytes} byte(s)";

        return new DecodeResult(Protocol, spans, summary);
    }

    private static void Flush(
        List<DecodedSpan> spans, ref int bits, ref int value, double start, double end)
    {
        if (bits > 0)
            spans.Add(new DecodedSpan(start, end, SpanKind.Error, $"{bits} bits",
                $"A byte was cut short after {bits} bits"));

        bits = 0;
        value = 0;
    }

    /// <summary>The handful of commands worth naming, which is most of what a capture contains.</summary>
    private static string Command(int value) => value switch
    {
        0xCC => " — SKIP ROM, addressing whatever is there without naming it",
        0x33 => " — READ ROM",
        0x55 => " — MATCH ROM",
        0xF0 => " — SEARCH ROM",
        0x44 => " — CONVERT T, which is what starts a temperature measurement",
        0xBE => " — READ SCRATCHPAD",
        0x4E => " — WRITE SCRATCHPAD",
        _ => string.Empty,
    };

    private static IEnumerable<(double Start, double End)> LowPulses(LogicTrace line)
    {
        double? fell = null;

        for (var i = 1; i < line.Edges.Count; i++)
        {
            var (time, level) = line.Edges[i];

            if (!level) { fell ??= time; continue; }

            if (fell is { } start) yield return (start, time);
            fell = null;
        }
    }
}
