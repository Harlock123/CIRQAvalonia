namespace Cirq.Engine.Decoding;

/// <summary>
/// Decodes I²C from its two wires.
/// <para>
/// The whole protocol is in what SDA does <i>while SCL is high</i>. Normally it does nothing —
/// data is only allowed to change while the clock is low, so a transition during a high clock is
/// not data at all but a framing event: SDA falling is a START, SDA rising is a STOP. That one
/// rule is why the bus needs no separate framing signal and why both lines have to be
/// open-collector.
/// </para>
/// <para>
/// Everything else falls out of it. Eight bits clocked in, then a ninth bit in which the
/// <i>receiver</i> pulls SDA down to acknowledge. The first byte after a START is an address with
/// the read/write bit in its bottom position, which is why I²C addresses are seven bits and why
/// the same device appears at two consecutive eight-bit numbers in half the datasheets ever
/// written.
/// </para>
/// </summary>
public static class I2cDecoder
{
    public const string Protocol = "I²C";

    public static DecodeResult Decode(LogicTrace sda, LogicTrace scl)
    {
        if (sda.IsEmpty || scl.IsEmpty)
            return DecodeResult.Nothing(Protocol, "Assign a trace to SDA and to SCL.");

        // The clock only. Every bit is sampled on a clock edge, so a well-resolved SCL is what
        // decides whether the decode is sound — a one-sample glitch on SDA between two clock
        // edges changes nothing, and open-collector lines being released and re-driven produce
        // those routinely.
        if (scl.SamplingWarning is { } coarse) return DecodeResult.Nothing(Protocol, coarse);

        List<DecodedSpan> spans = [];

        var started = false;       // inside a transaction
        var expectAddress = false; // the byte after a START is an address
        var bits = 0;
        var value = 0;
        var byteStart = 0.0;

        // Every clock edge in order, plus the SDA transitions that fall between them.
        foreach (var (time, level) in Framing(sda, scl))
        {
            if (level is Event.Start or Event.Restart)
            {
                if (bits is > 0 and < 8) spans.Add(Truncated(byteStart, time, bits));

                spans.Add(new DecodedSpan(time, time, SpanKind.Control,
                    level == Event.Restart ? "RESTART" : "START",
                    level == Event.Restart
                        ? "SDA fell while SCL was high without a STOP first — the master is keeping the bus"
                        : "SDA fell while SCL was high, which is the only thing that can begin a transaction"));

                started = true;
                expectAddress = true;
                bits = 0;
                value = 0;
                continue;
            }

            if (level == Event.Stop)
            {
                // A capture that begins part way through, or whose lines settle at power-up, can
                // show SDA rising with the clock already high before any transaction has started.
                // That is not a stop condition; it is the bus coming up.
                if (!started) continue;

                if (bits is > 0 and < 8) spans.Add(Truncated(byteStart, time, bits));

                spans.Add(new DecodedSpan(time, time, SpanKind.Control, "STOP",
                    "SDA rose while SCL was high, releasing the bus"));

                started = false;
                bits = 0;
                value = 0;
                continue;
            }

            if (!started) continue;

            // A data bit, sampled on the rising clock edge.
            var bit = level == Event.BitHigh;

            if (bits == 0) byteStart = time;

            if (bits < 8)
            {
                value = (value << 1) | (bit ? 1 : 0);
                bits++;

                if (bits < 8) continue;

                if (expectAddress)
                {
                    var address = value >> 1;
                    var reading = (value & 1) != 0;

                    spans.Add(new DecodedSpan(byteStart, time, SpanKind.Address,
                        $"0x{address:X2}{(reading ? " R" : " W")}",
                        $"Seven-bit address {address} (0x{address:X2}), " +
                        $"{(reading ? "reading from" : "writing to")} it. " +
                        $"The whole byte on the wire was 0x{value:X2}."));
                }
                else
                {
                    spans.Add(new DecodedSpan(byteStart, time, SpanKind.Data,
                        $"0x{value:X2}", $"Data byte {value} (0x{value:X2}, {Binary(value)})"));
                }

                continue;
            }

            // The ninth bit, which the receiver drives.
            spans.Add(new DecodedSpan(time, time,
                bit ? SpanKind.Error : SpanKind.Response,
                bit ? "NACK" : "ACK",
                bit
                    ? "Nobody pulled SDA down on the ninth clock. After an address that usually " +
                      "means no device has it. After the last byte of a read it is not a fault " +
                      "at all — the master leaves SDA alone on purpose, and that is how it tells " +
                      "the slave to stop sending."
                    : "The receiver pulled SDA down on the ninth clock to accept the byte"));

            expectAddress = false;
            bits = 0;
            value = 0;
        }

        var bytes = spans.Count(s => s.Kind is SpanKind.Data or SpanKind.Address);
        var nacks = spans.Count(s => s.Text == "NACK");

        var summary = spans.Count == 0
            ? "No start condition found. I²C begins with SDA falling while SCL is high."
            : $"{spans.Count(s => s.Text == "START")} transaction(s), {bytes} byte(s)" +
              (nacks > 0 ? $", {nacks} not acknowledged" : string.Empty);

        return new DecodeResult(Protocol, spans, summary);
    }

    private static DecodedSpan Truncated(double start, double end, int bits) =>
        new(start, end, SpanKind.Error, $"{bits} bits",
            $"A byte was cut short after {bits} bits by a framing condition");

    private enum Event { Start, Restart, Stop, BitHigh, BitLow }

    /// <summary>
    /// Walks the two lines together and reports what happened, in order: the framing conditions
    /// that SDA makes while SCL is high, and the data bits that the rising clock samples.
    /// </summary>
    private static IEnumerable<(double Time, Event What)> Framing(LogicTrace sda, LogicTrace scl)
    {
        List<(double Time, Event What)> events = [];

        // Framing: an SDA transition with the clock already high.
        var wasStarted = false;

        for (var i = 1; i < sda.Edges.Count; i++)
        {
            var (time, level) = sda.Edges[i];
            if (!scl.LevelAt(time)) continue;

            if (level)
            {
                events.Add((time, Event.Stop));
                wasStarted = false;
            }
            else
            {
                events.Add((time, wasStarted ? Event.Restart : Event.Start));
                wasStarted = true;
            }
        }

        // Data: whatever SDA is at each rising clock edge — but only the edges that are really
        // data.
        //
        // The distinction matters and is easy to miss. A STOP is SDA rising while SCL is high, so
        // the master must first bring SCL up and only then release SDA; that rising clock edge is
        // the setup for the framing condition and not a ninth data bit, and counting it produces
        // a phantom one-bit byte before every STOP. What separates the two is what happens next:
        // a real data bit is always followed by the clock going low again, and a setup edge never
        // is, because the framing condition happens while the clock is still high.
        var framing = events.Select(e => e.Time).ToList();

        foreach (var time in scl.EdgesGoing(rising: true))
        {
            var falls = scl.NextAfter(time) is { Level: false } next ? next.Time : double.MaxValue;
            var nextFrame = framing.FirstOrDefault(f => f > time, double.MaxValue);

            if (falls > nextFrame) continue;

            events.Add((time, sda.LevelAt(time) ? Event.BitHigh : Event.BitLow));
        }

        return events.OrderBy(e => e.Time);
    }

    private static string Binary(int value) => "0b" + Convert.ToString(value, 2).PadLeft(8, '0');
}
