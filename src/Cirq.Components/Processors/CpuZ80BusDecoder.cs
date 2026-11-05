using System.Globalization;
using Cirq.Engine.Decoding;

namespace Cirq.Components.Processors;

/// <summary>
/// Reads a Z80's bus as a logic analyser with a Z80 pod would: one row per machine cycle — an M1
/// fetch, a memory or I/O read or write, an interrupt acknowledge, a refresh — with the wait states
/// /WAIT added, and where an instruction starts, the instruction put back together from the bytes
/// fetched and read after it.
/// <para>
/// It works from the cycles the processor recorded, as the 6502's, 68000's and 6809's decoders do.
/// A refresh is the second half of every M1: /MREQ with /RFSH, R on A0-A6 and I on A8-A15, and no
/// data. HALT's NOPs are M1 cycles too, marked as such.
/// </para>
/// </summary>
public static class CpuZ80BusDecoder
{
    public const string Protocol = "Z80 bus";

    /// <summary>Every recorded cycle, as spans: instructions where they start, reads, writes, I/O, acknowledges and refreshes.</summary>
    public static DecodeResult Decode(IReadOnlyList<Z80BusCycle> cycles)
    {
        ArgumentNullException.ThrowIfNull(cycles);
        if (cycles.Count == 0)
            return DecodeResult.Nothing(Protocol, "The processor has not run a cycle yet: run the circuit, then decode.");

        List<DecodedSpan> spans = new(cycles.Count);
        int instructions = 0, writes = 0, io = 0, refreshes = 0, waits = 0, acknowledges = 0;

        for (var i = 0; i < cycles.Count; i++)
        {
            var c = cycles[i];
            var start = i > 0 ? cycles[i - 1].Time : c.Time;
            var wait = c.WaitStates > 0 ? string.Create(CultureInfo.InvariantCulture, $" +{c.WaitStates}w") : string.Empty;
            var waitDetail = c.WaitStates > 0 ? string.Create(CultureInfo.InvariantCulture, $", {c.WaitStates} wait state(s) from /WAIT") : string.Empty;
            waits += c.WaitStates;

            switch (c.Kind)
            {
                case Z80CycleKind.Fetch when c.Halted:
                    spans.Add(new DecodedSpan(start, c.Time, SpanKind.Control, string.Create(CultureInfo.InvariantCulture, $"HALT NOP ${c.Address:X4}"),
                        "An M1 of the NOPs HALT runs, the byte ignored and PC kept"));
                    break;

                case Z80CycleKind.Fetch when c.First:
                    instructions++;
                    spans.Add(new DecodedSpan(start, c.Time, SpanKind.Control, Instruction(cycles, i) + wait,
                        string.Create(CultureInfo.InvariantCulture, $"M1 opcode fetch from ${c.Address:X4}: {c.Data:X2}{waitDetail}")));
                    break;

                case Z80CycleKind.Fetch:
                    spans.Add(new DecodedSpan(start, c.Time, SpanKind.Control, string.Create(CultureInfo.InvariantCulture, $"M1 ${c.Address:X4} {c.Data:X2}{wait}"),
                        $"M1 fetch of the opcode after a prefix{waitDetail}"));
                    break;

                case Z80CycleKind.Internal:
                    refreshes++;
                    spans.Add(new DecodedSpan(start, c.Time, SpanKind.Data, string.Create(CultureInfo.InvariantCulture, $"RFSH ${c.Address:X4}"),
                        string.Create(CultureInfo.InvariantCulture, $"Refresh: I = ${c.Address >> 8:X2}, R = ${c.Address & 0x7F:X2} on A0-A6")));
                    break;

                case Z80CycleKind.Acknowledge:
                    acknowledges++;
                    spans.Add(new DecodedSpan(start, c.Time, SpanKind.Control, string.Create(CultureInfo.InvariantCulture, $"INTA {c.Data:X2}{wait}"),
                        string.Create(CultureInfo.InvariantCulture, $"Interrupt acknowledge, /M1 with /IORQ at PC ${c.Address:X4}: the device put {c.Data:X2} on the bus{waitDetail}")));
                    break;

                case Z80CycleKind.IoRead or Z80CycleKind.IoWrite:
                {
                    io++;
                    var output = c.Kind == Z80CycleKind.IoWrite;
                    spans.Add(new DecodedSpan(start, c.Time, output ? SpanKind.Address : SpanKind.Data,
                        string.Create(CultureInfo.InvariantCulture, $"{(output ? "OUT" : "IN")} ${c.Address & 0xFF:X2} {c.Data:X2}{wait}"),
                        string.Create(CultureInfo.InvariantCulture, $"I/O {(output ? "write" : "read")}, port ${c.Address & 0xFF:X2} with ${c.Address >> 8:X2} on A8-A15{waitDetail}")));
                    break;
                }

                default:
                {
                    var write = c.Kind == Z80CycleKind.Write;
                    if (write) writes++;
                    spans.Add(new DecodedSpan(start, c.Time, write ? SpanKind.Address : SpanKind.Data,
                        string.Create(CultureInfo.InvariantCulture, $"{(write ? 'W' : 'R')} ${c.Address:X4} {c.Data:X2}{wait}"),
                        (write ? "Memory write" : "Memory read") + waitDetail));
                    break;
                }
            }
        }

        var summary = string.Create(CultureInfo.InvariantCulture,
            $"{cycles.Count} cycles, {instructions} instructions, {writes} writes, {io} I/O, {refreshes} refreshes");
        if (acknowledges > 0) summary += string.Create(CultureInfo.InvariantCulture, $", {acknowledges} acknowledges");
        if (waits > 0) summary += string.Create(CultureInfo.InvariantCulture, $", {waits} wait states");
        return new DecodeResult(Protocol, spans, summary);
    }

    /// <summary>
    /// The instruction whose first M1 is the cycle at <paramref name="index"/>: <c>$0012 LD A,$41</c>.
    /// Its other bytes are those fetched after a prefix and read from the addresses following it, up
    /// to the next instruction; any the log does not have yet show as a question mark.
    /// </summary>
    public static string Instruction(IReadOnlyList<Z80BusCycle> cycles, int index)
    {
        ArgumentNullException.ThrowIfNull(cycles);
        var fetch = cycles[index];
        Dictionary<int, byte> bytes = new() { [fetch.Address] = fetch.Data };

        for (var k = index + 1; k < cycles.Count && k <= index + 14; k++)
        {
            var c = cycles[k];
            if ((c.Kind == Z80CycleKind.Fetch && (c.First || c.Halted)) || c.Kind == Z80CycleKind.Acknowledge) break;
            var offset = (c.Address - fetch.Address) & 0xFFFF;
            if (c.Kind is Z80CycleKind.Fetch or Z80CycleKind.Read && offset is > 0 and < 4) bytes.TryAdd(c.Address, c.Data);
        }

        var (text, length) = OpcodesZ80.Disassemble(fetch.Address, a => bytes.GetValueOrDefault(a & 0xFFFF));
        var complete = Enumerable.Range(0, length).All(i => bytes.ContainsKey((fetch.Address + i) & 0xFFFF));
        if (!complete) text = text.Split(' ')[0] + " ?";

        return string.Create(CultureInfo.InvariantCulture, $"${fetch.Address:X4} {text}");
    }
}
