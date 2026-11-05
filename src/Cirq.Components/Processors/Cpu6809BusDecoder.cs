using System.Globalization;
using Cirq.Engine.Decoding;

namespace Cirq.Components.Processors;

/// <summary>
/// Reads a 6809's bus as a logic analyser with a 6809 pod would: one row per E cycle — the address,
/// the byte, which way it went — with the dead cycles marked as such, the vector reads with BS high
/// marked as vectors, and where an instruction starts, the instruction put back together from the
/// bytes read after it.
/// <para>
/// It works from the cycles the processor recorded, as the 6502's and 68000's decoders do. A dead
/// cycle is a read of $FFFF the processor ignores; a real 6809 board shows it on the bus exactly
/// so, and a logic analyser that does not know the difference shows it as a read.
/// </para>
/// </summary>
public static class Cpu6809BusDecoder
{
    public const string Protocol = "6809 bus";

    /// <summary>Every recorded cycle, as spans: instructions where they start, vectors, reads, writes and dead cycles.</summary>
    public static DecodeResult Decode(IReadOnlyList<M6809BusCycle> cycles)
    {
        ArgumentNullException.ThrowIfNull(cycles);
        if (cycles.Count == 0)
            return DecodeResult.Nothing(Protocol, "The processor has not run a cycle yet: run the circuit, then decode.");

        List<DecodedSpan> spans = new(cycles.Count);
        int instructions = 0, writes = 0, dead = 0;

        for (var i = 0; i < cycles.Count; i++)
        {
            var c = cycles[i];
            var start = i > 0 ? cycles[i - 1].Time : c.Time;

            switch (c.Kind)
            {
                case M6809CycleKind.Fetch:
                    instructions++;
                    spans.Add(new DecodedSpan(start, c.Time, SpanKind.Control, Instruction(cycles, i),
                        $"Opcode fetch from ${c.Address:X4}: {c.Data:X2}"));
                    break;

                case M6809CycleKind.Dead:
                    dead++;
                    spans.Add(new DecodedSpan(start, c.Time, SpanKind.Data, "dead $FFFF", "A dead (VMA) cycle: $FFFF read and ignored"));
                    break;

                case M6809CycleKind.Vector:
                    spans.Add(new DecodedSpan(start, c.Time, SpanKind.Control,
                        string.Create(CultureInfo.InvariantCulture, $"V ${c.Address:X4} {c.Data:X2}"),
                        $"Vector read, BS high: {VectorName(c.Address)}"));
                    break;

                default:
                {
                    var write = c.Kind == M6809CycleKind.Write;
                    if (write) writes++;
                    spans.Add(new DecodedSpan(start, c.Time, write ? SpanKind.Address : SpanKind.Data,
                        string.Create(CultureInfo.InvariantCulture, $"{(write ? 'W' : 'R')} ${c.Address:X4} {c.Data:X2}"),
                        write ? "Write" : "Read"));
                    break;
                }
            }
        }

        return new DecodeResult(Protocol, spans,
            $"{cycles.Count} cycles, {instructions} instructions, {writes} writes, {dead} dead");
    }

    /// <summary>Which vector an address in $FFF2-$FFFF belongs to.</summary>
    public static string VectorName(int address) => (address & 0xFFFE) switch
    {
        0xFFF2 => "SWI3",
        0xFFF4 => "SWI2",
        0xFFF6 => "FIRQ",
        0xFFF8 => "IRQ",
        0xFFFA => "SWI",
        0xFFFC => "NMI",
        0xFFFE => "RESET",
        _ => "reserved",
    };

    /// <summary>
    /// The instruction whose first byte is the cycle at <paramref name="index"/>: <c>$E012 LDA #$41</c>.
    /// Its other bytes are the ones read from the following addresses in the cycles after it; any the
    /// log does not have yet show as question marks.
    /// </summary>
    public static string Instruction(IReadOnlyList<M6809BusCycle> cycles, int index)
    {
        ArgumentNullException.ThrowIfNull(cycles);
        var fetch = cycles[index];
        Dictionary<int, byte> bytes = new() { [fetch.Address] = fetch.Data };

        for (var k = index + 1; k < cycles.Count && k <= index + 10 && cycles[k].Kind != M6809CycleKind.Fetch; k++)
        {
            var offset = (cycles[k].Address - fetch.Address) & 0xFFFF;
            if (cycles[k].Kind == M6809CycleKind.Read && offset is > 0 and < 5) bytes.TryAdd(cycles[k].Address, cycles[k].Data);
        }

        var (text, length) = Opcodes6809.Disassemble(fetch.Address, a => bytes.GetValueOrDefault(a & 0xFFFF));
        // Not all its bytes read yet: the mnemonic, and a question mark for the operand.
        var complete = Enumerable.Range(0, length).All(i => bytes.ContainsKey((fetch.Address + i) & 0xFFFF));
        if (!complete) text = text.Split(' ')[0] + " ?";

        return string.Create(CultureInfo.InvariantCulture, $"${fetch.Address:X4} {text}");
    }
}
