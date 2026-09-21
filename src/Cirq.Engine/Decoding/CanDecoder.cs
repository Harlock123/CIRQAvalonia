namespace Cirq.Engine.Decoding;

/// <summary>
/// Decodes a CAN frame from the bus.
/// <para>
/// Two things about CAN make it unlike everything else here, and both show up in the decode.
/// </para>
/// <para>
/// The first is that a <b>dominant bit beats a recessive one</b>. Every node that transmits also
/// listens, and a node that sent a recessive bit and hears a dominant one knows somebody with a
/// lower identifier is talking and stops — without a collision, without a retry, and without
/// either message being damaged. That is why the identifier is sent first and why the lowest
/// identifier is the highest priority.
/// </para>
/// <para>
/// The second is <b>bit stuffing</b>. After five bits of the same value the transmitter inserts an
/// opposite one purely so the receivers' clocks have an edge to resynchronise on, and the receiver
/// throws it away again. It is invisible to everything above the physical layer and completely
/// confusing on a scope, because the bit pattern on the wire is not the bit pattern in the
/// message. Removing the stuffing is most of what a decoder is for here.
/// </para>
/// </summary>
public static class CanDecoder
{
    public const string Protocol = "CAN";

    /// <summary>
    /// Decodes the first frame found.
    /// </summary>
    /// <param name="bus">
    /// The bus, dominant-low — either a node's TXD pin, or a differential probe across CANH and
    /// CANL with the polarity such that dominant reads low.
    /// </param>
    /// <param name="bitRate">Bits per second, which every node on a CAN bus must agree on.</param>
    public static DecodeResult Decode(LogicTrace bus, double bitRate)
    {
        if (bus.IsEmpty) return DecodeResult.Nothing(Protocol, "Assign a trace to the bus.");

        if (bitRate <= 0)
            return DecodeResult.Nothing(Protocol, "Give the bit rate every node on the bus agrees on.");

        if (bus.SamplingWarning is { } coarse) return DecodeResult.Nothing(Protocol, coarse);

        var bitTime = 1.0 / bitRate;

        // The bit rate has to match what is on the wire. A frame's shortest pulse is one bit
        // time, so a shortest pulse several times longer than that says the rate is wrong — and
        // decoding at the wrong rate gives a confident, wrong identifier rather than nothing.
        var shortest = bus.ShortestPulse;

        if (shortest > 0 && shortest > bitTime * 3)
            return DecodeResult.Nothing(Protocol,
                $"The shortest pulse on this trace lasts {shortest * 1e6:0.#} µs, where one bit " +
                $"at {bitRate / 1e3:0.#} kbit/s is {bitTime * 1e6:0.#} µs. Nothing here is a CAN " +
                "frame at that rate — check the rate, and that this trace is really carrying one.");

        // A frame has an edge every few bits — bit stuffing guarantees it. A trace with almost no
        // transitions in it is not carrying one, whatever the rate.
        if (bus.Edges.Count < 8)
            return DecodeResult.Nothing(Protocol,
                $"Only {Math.Max(bus.Edges.Count - 1, 0)} transition(s) on this trace. A CAN " +
                "frame has an edge every few bits, because bit stuffing puts one there.");

        // A frame starts with the start-of-frame bit, which is the first dominant bit after idle.
        var start = bus.EdgesGoing(rising: false).FirstOrDefault(double.NaN);

        if (double.IsNaN(start))
            return DecodeResult.Nothing(Protocol,
                "The bus never went dominant. A frame begins with a dominant start-of-frame bit.");

        List<DecodedSpan> spans = [];

        // Read the raw bits from the middle of each bit time and undo the stuffing as we go.
        List<(bool Bit, double Time, bool Stuffed)> bits = [];

        var same = 0;
        var previous = false;
        var index = 0;

        var rawRun = 0;
        var rawPrevious = false;

        int run(bool bit)
        {
            if (index > 0 && bit == rawPrevious) rawRun++;
            else rawRun = 1;

            rawPrevious = bit;
            return rawRun;
        }

        while (true)
        {
            var at = start + ((index + 0.5) * bitTime);
            if (at > bus.End) break;

            // A logical one is recessive and the line sits high; a logical zero is dominant and
            // pulls it down. Reading the level straight off gives the logical bit, and inverting
            // it here — which is easy to talk yourself into, since "dominant" sounds like the
            // active state — turns every field in the frame inside out.
            var bit = bus.LevelAt(at);

            // The sixth of a run is a stuffed bit, put there for the clocks and belonging to
            // nobody's message.
            var stuffed = index > 0 && same >= 5;

            bits.Add((bit, at, stuffed));

            if (stuffed) same = 1;
            else if (index > 0 && bit == previous) same++;
            else same = 1;

            // The protocol's own invariant, used as the check that the rate is right.
            //
            // Only the dominant runs, and that qualification is the whole of it. Bit stuffing
            // bounds a run to six either way, but it stops at the CRC — everything after it, the
            // delimiters and the end-of-frame and the gap before the next frame, is a long
            // deliberate stretch of recessive that no transmitter stuffs. Checking recessive runs
            // as well rejects every valid frame at its own ending. A long <i>dominant</i> run has
            // no such exception: it is an error frame or a wrong bit rate, and never a good one.
            // Counted for every bit and tested only for the dominant ones: short-circuiting past
            // the counter leaves it holding stale state, and dominant runs then accumulate across
            // the recessive bits between them.
            var runLength = run(bit);

            if (!bit && runLength > 7)
                return DecodeResult.Nothing(Protocol,
                    $"More than seven dominant bits in a row at {bitRate / 1e3:0.#} kbit/s, " +
                    "which bit stuffing makes impossible inside a frame. The rate is wrong, or " +
                    "this trace is not carrying a frame.");

            previous = bit;
            index++;

            // Eleven recessive bits in a row is the bus idle again, and the frame is over.
            if (bits.Count > 11 && bits.TakeLast(11).All(b => b.Bit)) break;
        }

        var payload = bits.Where(b => !b.Stuffed).ToList();
        var stuffedCount = bits.Count - payload.Count;

        if (payload.Count < 19)
            return DecodeResult.Nothing(Protocol,
                $"Only {payload.Count} bits before the bus went idle — too few for a frame. " +
                "Check the bit rate, and that dominant reads low on this trace.");

        var position = 0;

        bool Next() => payload[position++].Bit;

        int Take(int count)
        {
            var value = 0;
            for (var i = 0; i < count && position < payload.Count; i++)
                value = (value << 1) | (Next() ? 1 : 0);

            return value;
        }

        double At(int i) => payload[Math.Min(i, payload.Count - 1)].Time;

        var sofAt = At(0);

        // Start of frame: one dominant bit.
        Next();
        spans.Add(new DecodedSpan(sofAt, At(position), SpanKind.Control, "SOF",
            "Start of frame — one dominant bit against an idle recessive bus"));

        var idAt = At(position);
        var identifier = Take(11);

        spans.Add(new DecodedSpan(idAt, At(position), SpanKind.Address, $"ID 0x{identifier:X3}",
            $"Identifier {identifier} (0x{identifier:X3}). It is sent first and most significant " +
            "bit first on purpose: a lower identifier holds the bus dominant for longer and wins " +
            "arbitration, so the smaller the number the higher the priority."));

        var rtr = Next();
        spans.Add(new DecodedSpan(At(position - 1), At(position), SpanKind.Control,
            rtr ? "RTR" : "DATA",
            rtr
                ? "Remote transmission request: asking somebody else to send this identifier"
                : "An ordinary data frame"));

        // IDE and the reserved bit.
        var ide = Next();
        Next();

        if (ide)
        {
            return new DecodeResult(Protocol, spans,
                "An extended (29-bit) identifier, which this decoder does not take apart. The " +
                "standard-format fields above are still right.");
        }

        var lengthAt = At(position);
        var length = Math.Min(Take(4), 8);

        spans.Add(new DecodedSpan(lengthAt, At(position), SpanKind.Control, $"DLC {length}",
            $"{length} data byte(s) follow"));

        for (var i = 0; i < length && position + 8 <= payload.Count; i++)
        {
            var byteAt = At(position);
            var value = Take(8);

            spans.Add(new DecodedSpan(byteAt, At(position), SpanKind.Data, $"0x{value:X2}",
                $"Data byte {i}: {value} (0x{value:X2})"));
        }

        if (position + 15 <= payload.Count)
        {
            var crcAt = At(position);
            var crc = Take(15);

            spans.Add(new DecodedSpan(crcAt, At(position), SpanKind.Control, $"CRC 0x{crc:X4}",
                $"Fifteen-bit frame check, 0x{crc:X4}"));
        }

        if (position < payload.Count)
        {
            var ackAt = At(position);
            var ackDelimiterSkipped = Next();

            spans.Add(new DecodedSpan(ackAt, At(position),
                ackDelimiterSkipped ? SpanKind.Error : SpanKind.Response,
                ackDelimiterSkipped ? "NO ACK" : "ACK",
                ackDelimiterSkipped
                    ? "Nobody pulled the acknowledge slot dominant. Every node that received the " +
                      "frame correctly is supposed to, whether or not the message was for it — so " +
                      "this usually means nothing else is listening."
                    : "Some other node pulled the acknowledge slot dominant, which every node " +
                      "that received the frame does regardless of whether it wanted it"));
        }

        var summary =
            $"One frame, identifier 0x{identifier:X3}, {length} data byte(s)" +
            (stuffedCount > 0 ? $" — {stuffedCount} stuffed bit(s) removed" : string.Empty);

        return new DecodeResult(Protocol, spans, summary);
    }
}
