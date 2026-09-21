using System.Text;

namespace Cirq.Engine.Decoding;

/// <summary>
/// Decodes an asynchronous serial line.
/// <para>
/// There is no clock, which is the whole of what makes UART what it is. Both ends agree a bit rate
/// beforehand and then each one counts. A falling edge says a character is starting; the receiver
/// waits half a bit to get to the middle of that start bit, then samples every bit time after
/// that. Nothing on the wire says where the middle of a bit is, so if the two ends disagree about
/// the rate by more than a few percent the sampling drifts out of the character before it ends —
/// which is why a wrong baud rate gives garbage rather than nothing.
/// </para>
/// <para>
/// The idle state is high and the start bit is low, so a line that has come loose reads as a
/// permanent stream of nothing rather than as a break.
/// </para>
/// </summary>
public static class UartDecoder
{
    public const string Protocol = "UART";

    /// <summary>
    /// Decodes a line at a stated bit rate.
    /// </summary>
    /// <param name="line">The signal, idle high.</param>
    /// <param name="baudRate">Bits per second, which both ends must have agreed.</param>
    /// <param name="dataBits">Usually eight.</param>
    /// <param name="parity">0 none, 1 odd, 2 even.</param>
    public static DecodeResult Decode(
        LogicTrace line, double baudRate, int dataBits = 8, int parity = 0)
    {
        if (line.IsEmpty) return DecodeResult.Nothing(Protocol, "Assign a trace to the line.");

        if (baudRate <= 0)
            return DecodeResult.Nothing(Protocol, "Give the bit rate both ends have agreed on.");

        if (line.SamplingWarning is { } coarse) return DecodeResult.Nothing(Protocol, coarse);

        var bitTime = 1.0 / baudRate;

        // A bit time worth fewer than two samples cannot be decoded however clean the trace is.
        if (line.SampleInterval > 0 && bitTime < line.SampleInterval * 2)
            return DecodeResult.Nothing(Protocol,
                $"A bit at {baudRate:0} baud lasts {bitTime * 1e6:0.##} µs and the capture has a " +
                $"sample every {line.SampleInterval * 1e6:0.##} µs. Either the rate is wrong or " +
                "the capture is too coarse to decode at it.");

        List<DecodedSpan> spans = [];
        var text = new StringBuilder();

        var cursor = line.Start;

        while (cursor < line.End)
        {
            // The next falling edge is a character starting — or is nothing, and we are done.
            var start = line.EdgesGoing(rising: false).FirstOrDefault(t => t > cursor, double.NaN);
            if (double.IsNaN(start)) break;

            // Half a bit in is the middle of the start bit, and every bit after that is one bit
            // time on. This is exactly what a real receiver does, and it is why the tolerance on
            // the rate is set by how far the last bit has drifted rather than the first.
            var middle = start + (bitTime / 2.0);
            if (middle + (dataBits + 1.5) * bitTime > line.End) break;

            var value = 0;
            var ones = 0;

            for (var i = 0; i < dataBits; i++)
            {
                var at = middle + ((i + 1) * bitTime);
                var bit = line.LevelAt(at);

                // Least significant bit first, which is what asynchronous serial has always done.
                if (bit) { value |= 1 << i; ones++; }
            }

            var afterData = middle + ((dataBits + 1) * bitTime);
            var parityOk = true;

            if (parity != 0)
            {
                var sent = line.LevelAt(afterData);
                var wanted = parity == 1 ? ones % 2 == 0 : ones % 2 != 0;

                parityOk = sent == wanted;
                afterData += bitTime;
            }

            var stopHigh = line.LevelAt(afterData);
            var end = afterData + (bitTime / 2.0);

            if (!stopHigh)
            {
                spans.Add(new DecodedSpan(start, end, SpanKind.Error, "FRAMING",
                    "The stop bit was low. Either the bit rate is wrong, or what is on the wire " +
                    "is not a character at this rate — a wrong rate reads as framing errors and " +
                    "plausible-looking rubbish rather than as silence."));
            }
            else if (!parityOk)
            {
                spans.Add(new DecodedSpan(start, end, SpanKind.Error, $"0x{value:X2} PARITY",
                    $"Byte 0x{value:X2} arrived with the wrong parity bit"));
            }
            else
            {
                var printable = value is >= 32 and < 127 ? $" '{(char)value}'" : string.Empty;
                if (value is >= 32 and < 127) text.Append((char)value);

                spans.Add(new DecodedSpan(start, end, SpanKind.Data, $"0x{value:X2}{printable}",
                    $"Byte {value} (0x{value:X2}){printable}, least significant bit first"));
            }

            // Resume from the middle of the stop bit, not from the end of the character. Back-to-
            // back characters have the next start bit falling the instant this one's stop bit
            // finishes, and a cursor placed at that instant steps straight over the edge it is
            // looking for — which resynchronises inside the next character and decodes it wrong.
            cursor = afterData;
        }

        var errors = spans.Count(s => s.Kind == SpanKind.Error);

        var summary = spans.Count == 0
            ? $"Nothing decoded at {baudRate:0} baud. The line idles high and a character starts " +
              "with a falling edge."
            : $"{spans.Count} character(s) at {baudRate:0} baud" +
              (errors > 0 ? $", {errors} bad" : string.Empty) +
              (text.Length > 0 ? $" — \"{text}\"" : string.Empty);

        return new DecodeResult(Protocol, spans, summary);
    }
}
