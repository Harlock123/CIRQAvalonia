using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Cirq.Components.Processors;
using Cirq.Core.Topology;
using ModelContextProtocol.Server;

namespace Cirq.Mcp;

/// <summary>The tools for a 6502 in the circuit: where it is, and what a program in a memory part says.</summary>
public sealed partial class CircuitTools
{
    [McpServerTool(Name = "cpu_state", ReadOnly = true, Idempotent = true)]
    [Description("A 6502 or 65C02's state: its registers (A, X, Y, S, PC), its flags (NV-BDIZC, capitals set), the instruction it is executing (disassembled from its bus), how many cycles and instructions it has run, whether it is running, waiting (WAI) or stopped (STP/JAM), any reads that found the data bus at no logic level, and its last bus cycles. In the app this is the live simulation; otherwise where the last run_transient left it.")]
    public Task<string> CpuState(
        [Description("The processor's designator, e.g. \"U2\". Optional when the circuit has only one.")] string? part = null,
        [Description("How many of its most recent bus cycles to list, from 0 to 64. Default 16.")] int cycles = 16,
        CancellationToken cancel = default) =>
        Read("cpu_state", s =>
        {
            if (cycles is < 0 or > 64) throw new ArgumentException("cycles must be from 0 to 64.");
            var cpu = Processor(s.Circuit, part);
            var core = cpu.Core;
            var log = cpu.BusLog;
            var recent = log.Skip(Math.Max(0, log.Count - cycles)).Select(c => (JsonNode)string.Create(CultureInfo.InvariantCulture,
                $"{(c.Sync ? "fetch" : c.IsRead ? "read" : "write")} ${c.Address:X4} {c.Data:X2}")).ToArray();

            var result = new JsonObject
            {
                ["part"] = cpu.Name,
                ["chip"] = cpu.PartNumber,
                ["pc"] = $"${core.PC:X4}",
                ["a"] = $"${core.A:X2}",
                ["x"] = $"${core.X:X2}",
                ["y"] = $"${core.Y:X2}",
                ["s"] = $"${core.S:X2}",
                ["flags"] = Processor6502.Flags(core.P),
                ["instruction"] = cpu.CurrentInstruction,
                ["cycles"] = core.Cycles,
                ["instructions"] = core.Instructions,
                ["state"] = core.IsStopped ? "stopped" : core.IsWaiting ? "waiting" : core.ResetAsserted ? "held in reset" : "running",
                ["recent_cycles"] = new JsonArray(recent),
            };
            if (cpu.UndefinedReads > 0) result["undefined_reads"] = $"{cpu.UndefinedReads}, first at ${cpu.FirstUndefinedRead:X4}";
            if (core.Cycles == 0) result["note"] = "It hasn't run a cycle yet: run_transient runs the circuit (or transport run, in the app).";
            return result;
        }, cancel);

    [McpServerTool(Name = "disassemble", ReadOnly = true, Idempotent = true)]
    [Description("Disassembles 6502 code held in a memory part (a ROM or RAM), as the processor would see it: give the address in the processor's memory map and where the chip starts in it (origin; a ROM decoded at $8000 has origin 32768). Lists each instruction's address, bytes and text; undocumented NMOS opcodes are marked *. With origin set and the chip holding $FFFA-$FFFF, the NMI, reset and IRQ vectors are given too.")]
    public Task<string> Disassemble(
        [Description("The memory part's designator, e.g. \"U3\".")] string part,
        [Description("Where to start, in the processor's address space, e.g. 32768 for $8000. Default: the origin.")] int? address = null,
        [Description("The processor address of the chip's first byte, e.g. 32768 for a ROM at $8000. Default 0.")] int origin = 0,
        [Description("How many instructions, from 1 to 200. Default 24.")] int count = 24,
        [Description("\"65C02\" (default) or \"6502\" for the NMOS part's opcodes.")] string variant = "65C02",
        CancellationToken cancel = default) =>
        Read("disassemble", s =>
        {
            if (count is < 1 or > 200) throw new ArgumentException("count must be from 1 to 200.");
            if (origin is < 0 or > 0xFFFF) throw new ArgumentException("origin must be from 0 to 65535.");
            var kind = variant?.Trim().ToUpperInvariant() switch
            {
                "65C02" or "W65C02" or "W65C02S" or "CMOS" => Cpu6502Variant.Wdc65C02,
                "6502" or "NMOS" => Cpu6502Variant.Nmos6502,
                _ => throw new ArgumentException($"variant is \"65C02\" or \"6502\", not \"{variant}\"."),
            };
            var memory = Memory(s.Circuit, part);
            var start = address ?? origin;
            if (start < origin || start >= origin + memory.Size)
                throw new ArgumentException($"{memory.Name} holds ${origin:X4}-${origin + memory.Size - 1:X4} with origin ${origin:X4}; ${start:X4} isn't in it.");

            byte Byte(int a) => a >= origin && a < origin + memory.Size ? memory.ReadByte(a - origin) : (byte)0;

            var lines = new JsonArray();
            var at = start;
            for (var i = 0; i < count && at < origin + memory.Size; i++)
            {
                var (text, length) = Opcodes6502.Disassemble(kind, at, Byte);
                var bytes = string.Join(" ", Enumerable.Range(0, length).Select(k => Byte(at + k).ToString("X2", CultureInfo.InvariantCulture)));
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"${at:X4}  {bytes,-8}  {text}"));
                at += length;
            }

            var result = new JsonObject { ["part"] = memory.Name, ["variant"] = kind == Cpu6502Variant.Nmos6502 ? "6502" : "65C02", ["lines"] = lines };
            if (origin + memory.Size == 0x10000)
            {
                int Word(int a) => Byte(a) | (Byte(a + 1) << 8);
                result["vectors"] = new JsonObject { ["nmi"] = $"${Word(0xFFFA):X4}", ["reset"] = $"${Word(0xFFFC):X4}", ["irq"] = $"${Word(0xFFFE):X4}" };
            }

            return result;
        }, cancel);

    /// <summary>The processor called <paramref name="name"/>, or the circuit's only one.</summary>
    private static Processor6502 Processor(Circuit circuit, string? name)
    {
        var all = circuit.Components.OfType<Processor6502>().ToList();
        if (string.IsNullOrWhiteSpace(name))
        {
            return all.Count switch
            {
                1 => all[0],
                0 => throw new ArgumentException("The circuit has no 6502 or 65C02."),
                _ => throw new ArgumentException($"The circuit has {all.Count} processors ({string.Join(", ", all.Select(c => c.Name))}): say which with part."),
            };
        }

        var c = Pins.Part(circuit, name);
        return c as Processor6502 ?? throw new ArgumentException($"{c.Name} is a {c.ComponentType}, not a processor.{(all.Count > 0 ? $" The processors: {string.Join(", ", all.Select(p => p.Name))}." : "")}");
    }
}
