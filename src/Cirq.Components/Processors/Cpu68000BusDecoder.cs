using System.Globalization;
using Cirq.Engine.Decoding;

namespace Cirq.Components.Processors;

/// <summary>
/// Reads a 68000's bus as a logic analyser with a 68000 pod would: a row per bus cycle — read or
/// write, word or byte, the address, the data and the function code — with any wait states, and a
/// row where each instruction starts, disassembled from the words it used.
/// <para>
/// It works from the cycles the processor recorded, as the 6502's decoder does: a 68000 has 23
/// address lines, 16 data lines, three function codes and four strobes, and only the processor knows
/// exactly when each cycle's data was taken. The function codes read UD and UP (user data and
/// program), SD and SP (supervisor data and program), and an interrupt acknowledge shows its level.
/// </para>
/// </summary>
public static class Cpu68000BusDecoder
{
    public const string Protocol = "68000 bus";

    /// <summary>The two letters for a function code: UD, UP, SD, SP, or IA for an interrupt acknowledge.</summary>
    public static string Space(int functionCode) => functionCode switch
    {
        1 => "UD",
        2 => "UP",
        5 => "SD",
        6 => "SP",
        7 => "IA",
        _ => $"FC{functionCode}",
    };

    /// <summary>Every logged cycle and instruction as spans, in the order they began.</summary>
    public static DecodeResult Decode(IReadOnlyList<M68000BusCycle> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (log.Count == 0)
            return DecodeResult.Nothing(Protocol, "The processor has not run a cycle yet: run the circuit, then decode.");

        List<(double Start, int Order, DecodedSpan Span)> spans = new(log.Count);
        int cycles = 0, instructions = 0, writes = 0;
        long waits = 0;

        for (var i = 0; i < log.Count; i++)
        {
            var c = log[i];
            switch (c.Kind)
            {
                case M68000CycleKind.Instruction:
                {
                    instructions++;
                    var words = c.Words ?? [c.Data];
                    spans.Add((c.Start, i, new DecodedSpan(c.Start, c.End, SpanKind.Control, Instruction(c.Address, words),
                        string.Join(" ", words.Select(w => w.ToString("X4", CultureInfo.InvariantCulture))))));
                    break;
                }

                case M68000CycleKind.InterruptAcknowledge:
                    cycles++;
                    waits += c.WaitStates;
                    spans.Add((c.Start, i, new DecodedSpan(c.Start, c.End, SpanKind.Control,
                        string.Create(CultureInfo.InvariantCulture, $"IACK level {(c.Address >> 1) & 7}"),
                        Detail(c, "Interrupt acknowledge"))));
                    break;

                default:
                {
                    cycles++;
                    waits += c.WaitStates;
                    var write = c.Kind == M68000CycleKind.Write;
                    if (write) writes++;
                    var text = string.Create(CultureInfo.InvariantCulture,
                        $"{(write ? 'W' : 'R')}.{(c.IsWord ? 'W' : 'B')} ${c.Address:X6} {(c.IsWord ? c.Data.ToString("X4", CultureInfo.InvariantCulture) : c.Data.ToString("X2", CultureInfo.InvariantCulture))} ({Space(c.FunctionCode)})");
                    spans.Add((c.Start, i, new DecodedSpan(c.Start, c.End, write ? SpanKind.Address : SpanKind.Data, text,
                        Detail(c, write ? "Write" : "Read"))));
                    break;
                }
            }
        }

        var ordered = spans.OrderBy(s => s.Start).ThenBy(s => s.Span.Kind == SpanKind.Control && s.Span.Text.StartsWith('$') ? 0 : 1).ThenBy(s => s.Order)
            .Select(s => s.Span).ToList();
        return new DecodeResult(Protocol, ordered,
            $"{cycles} bus cycles, {instructions} instructions, {writes} writes, {waits} wait states");
    }

    private static string Detail(M68000BusCycle c, string what)
    {
        var strobes = c.IsWord ? "/UDS and /LDS" : c.UpperByte ? "/UDS" : "/LDS";
        return string.Create(CultureInfo.InvariantCulture,
            $"{what}, {strobes}, FC={c.FunctionCode}{(c.WaitStates > 0 ? $", {c.WaitStates} wait state{(c.WaitStates == 1 ? "" : "s")}" : "")}");
    }

    /// <summary>An instruction from its words: <c>$000400 MOVE.L D0,(A1)+</c>.</summary>
    public static string Instruction(uint address, IReadOnlyList<ushort> words)
    {
        var (text, _) = M68000Disassembler.Disassemble(address, a =>
        {
            var i = (int)((a - address) / 2);
            return i >= 0 && i < words.Count ? words[i] : (ushort)0;
        });
        return string.Create(CultureInfo.InvariantCulture, $"${address & 0xFFFFFF:X6} {text}");
    }
}
