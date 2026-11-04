using System.Globalization;
using Cirq.Engine.Decoding;

namespace Cirq.Components.Processors;

/// <summary>
/// Reads a 6502's bus as a logic analyser with a 6502 pod would: one row per clock — the address,
/// the byte, which way it went — and on every SYNC cycle the instruction that was fetched, put back
/// together from the bytes read in the cycles after it.
/// <para>
/// It works from the cycles the processor recorded rather than from twenty-six probed traces,
/// because a bus decoder needs every address and data line sampled at the same instant, and a
/// processor knows exactly when that instant was. What it shows is what was on the pins.
/// </para>
/// </summary>
public static class Cpu6502BusDecoder
{
    public const string Protocol = "6502 bus";

    /// <summary>Every recorded cycle, as spans: instructions on SYNC cycles, reads and writes otherwise.</summary>
    public static DecodeResult Decode(Cpu6502Variant variant, IReadOnlyList<BusCycle> cycles)
    {
        ArgumentNullException.ThrowIfNull(cycles);
        if (cycles.Count == 0)
            return DecodeResult.Nothing(Protocol, "The processor has not run a cycle yet: run the circuit, then decode.");

        List<DecodedSpan> spans = new(cycles.Count);
        var instructions = 0;
        var writes = 0;

        for (var i = 0; i < cycles.Count; i++)
        {
            var c = cycles[i];
            var start = i > 0 ? cycles[i - 1].Time : c.Time;

            if (c.Sync)
            {
                instructions++;
                var text = Instruction(variant, cycles, i);
                spans.Add(new DecodedSpan(start, c.Time, SpanKind.Control, text,
                    $"Opcode fetch from ${c.Address:X4}: {c.Data:X2}"));
                continue;
            }

            if (!c.IsRead) writes++;

            spans.Add(new DecodedSpan(start, c.Time, c.IsRead ? SpanKind.Data : SpanKind.Address,
                string.Create(CultureInfo.InvariantCulture, $"{(c.IsRead ? 'R' : 'W')} ${c.Address:X4} {c.Data:X2}"),
                c.IsRead ? "Read" : "Write"));
        }

        return new DecodeResult(Protocol, spans,
            $"{cycles.Count} cycles, {instructions} instructions, {writes} writes");
    }

    /// <summary>The instruction fetched by the SYNC cycle at the start of <paramref name="cycles"/>.</summary>
    public static string Instruction(Cpu6502Variant variant, IReadOnlyList<BusCycle> cycles) => Instruction(variant, cycles, 0);

    /// <summary>
    /// The instruction fetched by the SYNC cycle at <paramref name="index"/>: <c>$8012 LDA #$48</c>.
    /// The operand bytes are the ones read from the following addresses in the next cycles; any the
    /// log does not have yet show as question marks.
    /// </summary>
    public static string Instruction(Cpu6502Variant variant, IReadOnlyList<BusCycle> cycles, int index)
    {
        var fetch = cycles[index];
        var entry = Opcodes6502.For(variant)[fetch.Data];
        Dictionary<int, byte> bytes = new() { [fetch.Address] = fetch.Data };

        for (var k = index + 1; k < cycles.Count && k <= index + 7 && !cycles[k].Sync; k++)
        {
            var offset = (cycles[k].Address - fetch.Address) & 0xFFFF;
            if (cycles[k].IsRead && offset is > 0 and < 3 && offset < entry.Length) bytes.TryAdd(cycles[k].Address, cycles[k].Data);
        }

        var complete = Enumerable.Range(0, entry.Length).All(i => bytes.ContainsKey((fetch.Address + i) & 0xFFFF));
        var (text, _) = Opcodes6502.Disassemble(variant, fetch.Address, a => bytes.GetValueOrDefault(a));

        return string.Create(CultureInfo.InvariantCulture, $"${fetch.Address:X4} {(complete ? text : entry.Mnemonic + " ?")}");
    }
}
