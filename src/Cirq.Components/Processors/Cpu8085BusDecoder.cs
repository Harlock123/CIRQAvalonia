using System.Globalization;
using Cirq.Engine.Decoding;

namespace Cirq.Components.Processors;

/// <summary>
/// Reads an 8085's bus as a logic analyser with an 8085 pod would: one row per machine cycle — an
/// opcode fetch, a memory or I/O read or write, an INTR acknowledge with /INTA, the restart
/// acknowledge of TRAP and the RSTs, a bus idle cycle — with the wait states READY added, where a
/// halt or a hold began, and where an instruction starts, the instruction put back together from the
/// bytes fetched and read after it.
/// <para>
/// It works from the cycles the processor recorded, as the other processors' decoders do: the
/// status lines IO/M, S1 and S0 say what each machine cycle is, so the record carries that rather
/// than the demultiplexed bus a probe would have to rebuild from ALE.
/// </para>
/// </summary>
public static class Cpu8085BusDecoder
{
    public const string Protocol = "8085 bus";

    /// <summary>Every recorded cycle, as spans: instructions where they start, reads, writes, I/O, acknowledges, bus idle cycles, halts and holds.</summary>
    public static DecodeResult Decode(IReadOnlyList<I8085BusCycle> cycles)
    {
        ArgumentNullException.ThrowIfNull(cycles);
        if (cycles.Count == 0)
            return DecodeResult.Nothing(Protocol, "The processor has not run a cycle yet: run the circuit, then decode.");

        List<DecodedSpan> spans = new(cycles.Count);
        int instructions = 0, writes = 0, io = 0, idle = 0, waits = 0, acknowledges = 0, halts = 0, holds = 0;

        for (var i = 0; i < cycles.Count; i++)
        {
            var c = cycles[i];
            var start = i > 0 ? cycles[i - 1].Time : c.Time;
            var wait = c.WaitStates > 0 ? string.Create(CultureInfo.InvariantCulture, $" +{c.WaitStates}w") : string.Empty;
            var waitDetail = c.WaitStates > 0 ? string.Create(CultureInfo.InvariantCulture, $", {c.WaitStates} wait state(s) from READY") : string.Empty;
            waits += c.WaitStates;

            switch (c.Kind)
            {
                case I8085CycleKind.OpcodeFetch when c.First:
                    instructions++;
                    spans.Add(new DecodedSpan(start, c.Time, SpanKind.Control, Instruction(cycles, i) + wait,
                        string.Create(CultureInfo.InvariantCulture, $"Opcode fetch from ${c.Address:X4}: {c.Data:X2} (IO/M 0, S1 1, S0 1){waitDetail}")));
                    break;

                case I8085CycleKind.OpcodeFetch:
                    spans.Add(new DecodedSpan(start, c.Time, SpanKind.Control, string.Create(CultureInfo.InvariantCulture, $"OF ${c.Address:X4} {c.Data:X2}{wait}"),
                        "Opcode fetch" + waitDetail));
                    break;

                case I8085CycleKind.InterruptAcknowledge:
                    acknowledges++;
                    spans.Add(new DecodedSpan(start, c.Time, SpanKind.Control, string.Create(CultureInfo.InvariantCulture, $"INTA {c.Data:X2}{wait}"),
                        string.Create(CultureInfo.InvariantCulture, $"INTR acknowledge, /INTA with PC ${c.Address:X4} on the bus: the device put {c.Data:X2} there{waitDetail}")));
                    break;

                case I8085CycleKind.RestartAcknowledge:
                    acknowledges++;
                    spans.Add(new DecodedSpan(start, c.Time, SpanKind.Control, string.Create(CultureInfo.InvariantCulture, $"RST ACK ${c.Address:X4}"),
                        "The restart a TRAP or an RST 7.5, 6.5 or 5.5 makes up: an opcode fetch with no /RD (IO/M 1, S1 1, S0 1), then PC pushed"));
                    break;

                case I8085CycleKind.BusIdle:
                    idle++;
                    spans.Add(new DecodedSpan(start, c.Time, SpanKind.Data, "BI",
                        "Bus idle: a machine cycle inside the processor (DAD, DSUB, RDEL, ARHL, LDHI, LDSI), no ALE and no strobe"));
                    break;

                case I8085CycleKind.Halt:
                    halts++;
                    spans.Add(new DecodedSpan(start, c.Time, SpanKind.Control, string.Create(CultureInfo.InvariantCulture, $"HALT ${c.Address:X4}"),
                        "HLT: the bus floats, S1 and S0 low, until an interrupt"));
                    break;

                case I8085CycleKind.Hold:
                    holds++;
                    spans.Add(new DecodedSpan(start, c.Time, SpanKind.Control, "HOLD",
                        "HOLD granted: HLDA high, the buses, /RD, /WR and IO/M floating"));
                    break;

                case I8085CycleKind.IoRead or I8085CycleKind.IoWrite:
                {
                    io++;
                    var output = c.Kind == I8085CycleKind.IoWrite;
                    spans.Add(new DecodedSpan(start, c.Time, output ? SpanKind.Address : SpanKind.Data,
                        string.Create(CultureInfo.InvariantCulture, $"{(output ? "OUT" : "IN")} ${c.Address & 0xFF:X2} {c.Data:X2}{wait}"),
                        string.Create(CultureInfo.InvariantCulture, $"I/O {(output ? "write" : "read")}, port ${c.Address & 0xFF:X2} on AD0-AD7 and A8-A15{waitDetail}")));
                    break;
                }

                default:
                {
                    var write = c.Kind == I8085CycleKind.MemoryWrite;
                    if (write) writes++;
                    spans.Add(new DecodedSpan(start, c.Time, write ? SpanKind.Address : SpanKind.Data,
                        string.Create(CultureInfo.InvariantCulture, $"{(write ? 'W' : 'R')} ${c.Address:X4} {c.Data:X2}{wait}"),
                        (write ? "Memory write" : "Memory read") + waitDetail));
                    break;
                }
            }
        }

        var summary = string.Create(CultureInfo.InvariantCulture,
            $"{cycles.Count} cycles, {instructions} instructions, {writes} writes, {io} I/O, {idle} bus idle");
        if (acknowledges > 0) summary += string.Create(CultureInfo.InvariantCulture, $", {acknowledges} acknowledges");
        if (halts > 0) summary += string.Create(CultureInfo.InvariantCulture, $", {halts} halts");
        if (holds > 0) summary += string.Create(CultureInfo.InvariantCulture, $", {holds} holds");
        if (waits > 0) summary += string.Create(CultureInfo.InvariantCulture, $", {waits} wait states");
        return new DecodeResult(Protocol, spans, summary);
    }

    /// <summary>
    /// The instruction whose opcode fetch is the cycle at <paramref name="index"/>: <c>$0012 MVI A,$41</c>.
    /// Its other bytes are those read from the addresses following it, up to the next instruction;
    /// any the log does not have yet show as a question mark.
    /// </summary>
    public static string Instruction(IReadOnlyList<I8085BusCycle> cycles, int index)
    {
        ArgumentNullException.ThrowIfNull(cycles);
        var fetch = cycles[index];
        Dictionary<int, byte> bytes = new() { [fetch.Address] = fetch.Data };
        var ended = false;

        for (var k = index + 1; k < cycles.Count && k <= index + 6; k++)
        {
            var c = cycles[k];
            if (c.Kind is I8085CycleKind.OpcodeFetch or I8085CycleKind.InterruptAcknowledge or I8085CycleKind.RestartAcknowledge or I8085CycleKind.Halt)
            {
                ended = true;
                break;
            }

            var offset = (c.Address - fetch.Address) & 0xFFFF;
            if (c.Kind == I8085CycleKind.MemoryRead && offset is > 0 and < 3) bytes.TryAdd(c.Address, c.Data);
        }

        var (text, length) = Opcodes8085.Disassemble(fetch.Address, a => bytes.GetValueOrDefault(a & 0xFFFF));

        // A conditional jump or call not taken reads only its low byte: the high byte is never on the bus.
        var complete = Enumerable.Range(0, length).All(i => bytes.ContainsKey((fetch.Address + i) & 0xFFFF));
        if (!complete && ended && length == 3 && bytes.ContainsKey((fetch.Address + 1) & 0xFFFF) && IsConditional(fetch.Data))
            text = text.Split(' ')[0] + " (not taken)";
        else if (!complete) text = text.Split(' ')[0] + " ?";

        return string.Create(CultureInfo.InvariantCulture, $"${fetch.Address:X4} {text}");
    }

    /// <summary>Jcc, Ccc, JNK and JK: the instructions that skip their high byte when not taken.</summary>
    private static bool IsConditional(byte op) => (op & 0xC7) is 0xC2 or 0xC4 || op is 0xDD or 0xFD;
}
