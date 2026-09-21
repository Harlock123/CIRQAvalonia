namespace Cirq.Engine.Decoding;

/// <summary>
/// Decodes SPI from its clock, its two data lines and its chip select.
/// <para>
/// SPI has no addressing, no acknowledgement and no framing inside the transfer — chip select is
/// the entire frame. That is what makes it fast and what makes it need a wire per device.
/// </para>
/// <para>
/// It is also <b>full duplex</b>, which is the thing most people meet it without noticing: a bit
/// goes out on MOSI and a bit comes back on MISO in the <i>same</i> clock, so a device's answer
/// arrives underneath the question rather than after it. Decoding both lines side by side is the
/// clearest way to see that, and it is why an MCP3008's ten-bit answer starts before the master
/// has finished asking.
/// </para>
/// <para>
/// Which clock edge samples the data is the <b>mode</b>, and getting it wrong is the classic SPI
/// fault: everything looks right on the scope and every byte is shifted by one bit.
/// </para>
/// </summary>
public static class SpiDecoder
{
    public const string Protocol = "SPI";

    /// <summary>
    /// Decodes one capture.
    /// </summary>
    /// <param name="clock">SCLK.</param>
    /// <param name="mosi">Master out, or null when it was not probed.</param>
    /// <param name="miso">Master in, or null.</param>
    /// <param name="chipSelect">Active-low CS, or null to treat the whole capture as one frame.</param>
    /// <param name="mode">0-3, the usual SPI mode number.</param>
    public static DecodeResult Decode(
        LogicTrace clock, LogicTrace? mosi, LogicTrace? miso, LogicTrace? chipSelect, int mode = 0)
    {
        if (clock.IsEmpty) return DecodeResult.Nothing(Protocol, "Assign a trace to the clock.");

        if (mosi is null && miso is null)
            return DecodeResult.Nothing(Protocol, "Assign a trace to MOSI or to MISO.");

        if (clock.SamplingWarning is { } coarse)
            return DecodeResult.Nothing(Protocol, coarse);

        // Mode is two bits: clock polarity and clock phase. With CPHA 0 the data is sampled on the
        // first edge of each clock, which is the rising one when CPOL is 0.
        var polarity = (mode & 2) != 0;
        var phase = (mode & 1) != 0;
        var sampleOnRising = polarity == phase;

        List<DecodedSpan> spans = [];

        var selected = chipSelect is null;
        var bits = 0;
        var outValue = 0;
        var inValue = 0;
        var byteStart = 0.0;

        foreach (var (time, what) in Events(clock, chipSelect, sampleOnRising))
        {
            if (what == Event.Select)
            {
                spans.Add(new DecodedSpan(time, time, SpanKind.Control, "CS↓",
                    "Chip select taken low, which is the whole of SPI's framing"));

                selected = true;
                bits = 0;
                outValue = 0;
                inValue = 0;
                continue;
            }

            if (what == Event.Deselect)
            {
                if (bits > 0)
                    spans.Add(new DecodedSpan(byteStart, time, SpanKind.Error, $"{bits} bits",
                        $"Chip select was released after {bits} bits, part way through a byte"));

                spans.Add(new DecodedSpan(time, time, SpanKind.Control, "CS↑",
                    "Chip select released, ending the frame"));

                selected = false;
                bits = 0;
                continue;
            }

            if (!selected) continue;

            if (bits == 0) byteStart = time;

            // Most significant bit first, which is what almost everything does.
            outValue = (outValue << 1) | (mosi is not null && mosi.LevelAt(time) ? 1 : 0);
            inValue = (inValue << 1) | (miso is not null && miso.LevelAt(time) ? 1 : 0);

            if (++bits < 8) continue;

            var text = mosi is not null && miso is not null
                ? $"{outValue:X2}/{inValue:X2}"
                : $"0x{(mosi is not null ? outValue : inValue):X2}";

            var detail = mosi is not null && miso is not null
                ? $"Out 0x{outValue:X2} while in 0x{inValue:X2} — the two happened in the same " +
                  "eight clocks, which is what full duplex means"
                : $"0x{(mosi is not null ? outValue : inValue):X2} " +
                  $"({(mosi is not null ? outValue : inValue)})";

            spans.Add(new DecodedSpan(byteStart, time, SpanKind.Data, text, detail));

            bits = 0;
            outValue = 0;
            inValue = 0;
        }

        var bytes = spans.Count(s => s.Kind == SpanKind.Data);

        var summary = bytes == 0
            ? "No complete bytes. Check the clock trace, and whether chip select ever went low."
            : $"{bytes} byte(s) in {Math.Max(spans.Count(s => s.Text == "CS↓"), 1)} frame(s), " +
              $"mode {mode} — sampled on the {(sampleOnRising ? "rising" : "falling")} clock edge";

        return new DecodeResult(Protocol, spans, summary);
    }

    private enum Event { Select, Deselect, Sample }

    private static IEnumerable<(double Time, Event What)> Events(
        LogicTrace clock, LogicTrace? chipSelect, bool sampleOnRising)
    {
        List<(double Time, Event What)> events = [];

        if (chipSelect is not null)
        {
            for (var i = 1; i < chipSelect.Edges.Count; i++)
            {
                var (time, level) = chipSelect.Edges[i];
                events.Add((time, level ? Event.Deselect : Event.Select));
            }
        }

        foreach (var time in clock.EdgesGoing(sampleOnRising))
            events.Add((time, Event.Sample));

        // Selection changes are ordered ahead of a clock edge at the same instant: the frame has
        // to be open before a bit in it counts.
        return events.OrderBy(e => e.Time).ThenBy(e => e.What == Event.Sample ? 1 : 0);
    }
}
