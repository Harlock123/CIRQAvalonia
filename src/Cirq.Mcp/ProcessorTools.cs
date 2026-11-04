using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Cirq.Components.Processors;
using Cirq.Core.Topology;
using ModelContextProtocol.Server;

namespace Cirq.Mcp;

/// <summary>The tools for a 6502 in the circuit: where it is, what a program in a memory part says, and what a SID is playing.</summary>
public sealed partial class CircuitTools
{
    [McpServerTool(Name = "cpu_state", ReadOnly = true, Idempotent = true)]
    [Description("A 6502, 65C02, 6510 or 8500's state: its registers (A, X, Y, S, PC), its flags (NV-BDIZC, capitals set), the instruction it is executing (disassembled from its bus), how many cycles and instructions it has run, whether it is running, waiting (WAI) or stopped (STP/JAM), any reads that found the data bus at no logic level, its last bus cycles, and on a 6510 or 8500 its I/O port ($0000 direction, $0001 data, and the P0-P5 pins). In the app this is the live simulation; otherwise where the last run_transient left it.")]
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
            if (cpu is Processor6510 port)
            {
                result["port"] = new JsonObject
                {
                    ["direction"] = $"${port.PortDirection:X2}",
                    ["data"] = $"${port.PortOutput:X2}",
                    ["pins"] = $"${port.PortPins:X2}",
                    ["reads_as"] = $"${port.PortValue:X2}",
                };
            }

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

    [McpServerTool(Name = "sid_state", ReadOnly = true, Idempotent = true)]
    [Description("A 6581 or 8580 SID's state: for each voice its frequency (register and hertz at the measured clock), pulse width, waveforms, gate/sync/ring/test bits, attack/decay/sustain/release nibbles and its envelope's phase and level; the filter's cutoff (register, and hertz with the datasheet capacitors), resonance, which voices go through it and the modes; the volume, voice 3 off, OSC3, ENV3 and the paddles; and the voltage AUDIO OUT is driven to. In the app this is the live simulation; otherwise where the last run_transient left it.")]
    public Task<string> SidState(
        [Description("The SID's designator, e.g. \"U8\". Optional when the circuit has only one.")] string? part = null,
        CancellationToken cancel = default) =>
        Read("sid_state", s =>
        {
            var all = s.Circuit.Components.OfType<SidChip>().ToList();
            SidChip sid;
            if (string.IsNullOrWhiteSpace(part))
            {
                sid = all.Count switch
                {
                    1 => all[0],
                    0 => throw new ArgumentException("The circuit has no SID (6581 or 8580)."),
                    _ => throw new ArgumentException($"The circuit has {all.Count} SIDs ({string.Join(", ", all.Select(c => c.Name))}): say which with part."),
                };
            }
            else
            {
                var c = Pins.Part(s.Circuit, part);
                sid = c as SidChip ?? throw new ArgumentException($"{c.Name} is a {c.ComponentType}, not a SID.");
            }

            var core = sid.Core;
            var voices = new JsonArray();
            for (var i = 0; i < 3; i++)
            {
                var v = core.Voices[i];
                var waves = new[] { (SidCore.Triangle, "triangle"), (SidCore.Sawtooth, "sawtooth"), (SidCore.Pulse, "pulse"), (SidCore.Noise, "noise") }
                    .Where(w => (v.Control & w.Item1) != 0).Select(w => (JsonNode)w.Item2).ToArray();
                var bits = new[] { (SidCore.Gate, "gate"), (SidCore.Sync, "sync"), (SidCore.Ring, "ring"), (SidCore.Test, "test") }
                    .Where(w => (v.Control & w.Item1) != 0).Select(w => (JsonNode)w.Item2).ToArray();
                voices.Add(new JsonObject
                {
                    ["voice"] = i + 1,
                    ["frequency"] = $"${v.Frequency:X4}",
                    ["hz"] = Math.Round(sid.VoiceFrequency(i), 2),
                    ["pulse_width"] = string.Create(CultureInfo.InvariantCulture, $"${v.PulseWidth:X3} ({v.PulseWidth / 40.96:0.#} %)"),
                    ["waveforms"] = new JsonArray(waves),
                    ["bits"] = new JsonArray(bits),
                    ["adsr"] = $"{v.AttackDecay >> 4:X}{v.AttackDecay & 15:X}{v.SustainRelease >> 4:X}{v.SustainRelease & 15:X}",
                    ["envelope"] = new JsonObject
                    {
                        ["phase"] = v.Phase switch { EnvelopePhase.Attack => "attack", EnvelopePhase.DecaySustain => "decay/sustain", _ => "release" },
                        ["level"] = v.Envelope,
                    },
                    ["filtered"] = (core.Routing & (1 << i)) != 0,
                });
            }

            var modes = new[] { (core.LowPass, "low-pass"), (core.BandPass, "band-pass"), (core.HighPass, "high-pass") }
                .Where(m => m.Item1).Select(m => (JsonNode)m.Item2).ToArray();
            var result = new JsonObject
            {
                ["part"] = sid.Name,
                ["chip"] = sid.PartNumber,
                ["clock_hz"] = double.IsNaN(sid.ClockFrequency) ? null : Math.Round(sid.ClockFrequency),
                ["voices"] = voices,
                ["filter"] = new JsonObject
                {
                    ["cutoff"] = core.Cutoff,
                    ["cutoff_hz"] = Math.Round(sid.CutoffFrequency),
                    ["resonance"] = core.Resonance,
                    ["modes"] = new JsonArray(modes),
                    ["ext_in_filtered"] = (core.Routing & 8) != 0,
                },
                ["volume"] = core.Volume,
                ["voice3_off"] = core.Voice3Off,
                ["osc3"] = $"${core.Read(0x1B):X2}",
                ["env3"] = $"${core.Read(0x1C):X2}",
                ["pot_x"] = core.PotX,
                ["pot_y"] = core.PotY,
                ["audio_out_v"] = Math.Round(sid.OutputLevel, 3),
                ["cycles"] = core.Cycles,
            };
            if (core.Cycles == 0) result["note"] = "It hasn't been clocked yet: run_transient runs the circuit (or transport run, in the app).";
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
                0 => throw new ArgumentException("The circuit has no 6502, 65C02 or 6510."),
                _ => throw new ArgumentException($"The circuit has {all.Count} processors ({string.Join(", ", all.Select(c => c.Name))}): say which with part."),
            };
        }

        var c = Pins.Part(circuit, name);
        return c as Processor6502 ?? throw new ArgumentException($"{c.Name} is a {c.ComponentType}, not a processor.{(all.Count > 0 ? $" The processors: {string.Join(", ", all.Select(p => p.Name))}." : "")}");
    }
}
