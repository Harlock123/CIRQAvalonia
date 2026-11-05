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
    [Description("A processor's state. A 6502, 65C02, 6510 or 8500: its registers (A, X, Y, S, PC), its flags (NV-BDIZC, capitals set), the instruction it is executing (disassembled from its bus), how many cycles and instructions it has run, whether it is running, waiting (WAI) or stopped (STP/JAM), any reads that found the data bus at no logic level, its last bus cycles, and on a 6510 or 8500 its I/O port ($0000 direction, $0001 data, and the P0-P5 pins). A 68000: D0-D7, A0-A7, USP and SSP, PC, SR and its flags (T, S, the interrupt mask, XNZVC), the last instruction it finished, its clocks, bus cycles and wait states, the last exception, whether it is running, stopped (STOP), halted (double bus fault) or held in reset, and its last bus cycles with size, function code and wait states. A 6809 or 6809E: A, B, D, X, Y, U, S, PC, DP and CC with its flags (EFHINZVC, capitals set), the instruction it is executing, its E cycles (dead ones included) and instructions, the last interrupt it took, whether NMI is armed, whether it is running, in SYNC, in CWAI, halted, granted the bus or stopped, MRDY's stretch, and its last bus cycles with the dead ($FFFF) ones marked. A Z80: AF, BC, DE, HL and the alternate set, IX, IY, SP, PC, I and R, WZ, IFF1 and IFF2, the interrupt mode, the flags (SZYHXPNC, capitals set), the instruction it is executing, its T-states, instructions and wait states, the last interrupt, whether it is running, halted (HALT), holding the bus released (/BUSREQ) or held in reset, and its last machine cycles with the refreshes and acknowledges marked. An 8085: A, BC, DE, HL, SP and PC, the flags (SZKAPVC, capitals set — K and V the undocumented ones), the interrupt enable, the RST 7.5/6.5/5.5 masks, the RST 7.5 and TRAP latches, SID, SOD and HLDA, the instruction it is executing, its T-states, instructions, wait and hold states, the last interrupt (with the bytes an INTR acknowledge read off the bus), whether it is running, halted (HLT), holding the bus (HOLD) or held in reset, and its last machine cycles with the bus idle cycles and acknowledges marked. In the app this is the live simulation; otherwise where the last run_transient left it.")]
    public Task<string> CpuState(
        [Description("The processor's designator, e.g. \"U2\". Optional when the circuit has only one.")] string? part = null,
        [Description("How many of its most recent bus cycles to list, from 0 to 64. Default 16.")] int cycles = 16,
        CancellationToken cancel = default) =>
        Read("cpu_state", s =>
        {
            if (cycles is < 0 or > 64) throw new ArgumentException("cycles must be from 0 to 64.");
            var any = AnyProcessor(s.Circuit, part);
            if (any is Cpu68000 m68k) return State68000(m68k, cycles);
            if (any is Processor6809 m6809) return State6809(m6809, cycles);
            if (any is CpuZ80 z80) return StateZ80(z80, cycles);
            if (any is Cpu8085 i8085) return State8085(i8085, cycles);
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
    [Description("Disassembles code held in a memory part (a ROM or RAM), as the processor would see it: give the address in the processor's memory map and where the chip starts in it (origin; a ROM decoded at $8000 has origin 32768). Lists each instruction's address, bytes and text. For a 6502 (variant 65C02 or 6502) undocumented NMOS opcodes are marked *, and with the chip holding $FFFA-$FFFF the NMI, reset and IRQ vectors are given too. For a 68000 (variant 68000) give the pair of byte-wide chips its 16-bit memory is made of: part the even bytes (D8-D15) and odd_part the odd ones (D0-D7); origin is then where the pair starts, and with origin 0 the reset vectors (the stack pointer and PC) are given. For a 6809 (variant 6809) undocumented opcodes are marked *, and with the chip holding $FFF2-$FFFF the SWI3, SWI2, FIRQ, IRQ, SWI, NMI and RESET vectors are given too. For a Z80 (variant Z80) in Zilog's syntax, the undocumented opcodes (IXH and IXL, SLL, the DDCB copies, the ED duplicates) marked *. For an 8085 (variant 8085) in Intel's mnemonics, the undocumented DSUB, ARHL, RDEL, LDHI, LDSI, RSTV, SHLX, LHLX, JNK and JK marked *, and with the chip holding $0000-$003F the restart addresses (RST 0-7, TRAP, RST 5.5, 6.5, 7.5) named.")]
    public Task<string> Disassemble(
        [Description("The memory part's designator, e.g. \"U3\".")] string part,
        [Description("Where to start, in the processor's address space, e.g. 32768 for $8000. Default: the origin.")] int? address = null,
        [Description("The processor address of the chip's first byte, e.g. 32768 for a ROM at $8000. Default 0.")] int origin = 0,
        [Description("How many instructions, from 1 to 200. Default 24.")] int count = 24,
        [Description("\"65C02\" (default), \"6502\" for the NMOS part's opcodes, \"68000\", \"6809\", \"Z80\", or \"8085\".")] string variant = "65C02",
        [Description("For a 68000: the chip holding the odd bytes (D0-D7, /LDS), part being the even one. Optional; without it part is read as holding every byte.")] string? odd_part = null,
        CancellationToken cancel = default) =>
        Read("disassemble", s =>
        {
            if (variant?.Trim() is "68000" or "68k" or "MC68000") return Disassemble68000(s.Circuit, part, odd_part, address, origin, count);
            if (variant?.Trim().ToUpperInvariant() is "6809" or "MC6809" or "6809E" or "MC6809E") return Disassemble6809(s.Circuit, part, address, origin, count);
            if (variant?.Trim().ToUpperInvariant() is "Z80" or "Z8400") return DisassembleZ80(s.Circuit, part, address, origin, count);
            if (variant?.Trim().ToUpperInvariant() is "8085" or "8085A" or "8085AH" or "I8085") return Disassemble8085(s.Circuit, part, address, origin, count);
            if (count is < 1 or > 200) throw new ArgumentException("count must be from 1 to 200.");
            if (origin is < 0 or > 0xFFFF) throw new ArgumentException("origin must be from 0 to 65535.");
            var kind = variant?.Trim().ToUpperInvariant() switch
            {
                "65C02" or "W65C02" or "W65C02S" or "CMOS" => Cpu6502Variant.Wdc65C02,
                "6502" or "NMOS" => Cpu6502Variant.Nmos6502,
                _ => throw new ArgumentException($"variant is \"65C02\", \"6502\", \"68000\", \"6809\", \"Z80\" or \"8085\", not \"{variant}\"."),
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

    [McpServerTool(Name = "peripheral_state", ReadOnly = true, Idempotent = true)]
    [Description("A peripheral chip's state. A 6821 or 6520 PIA: each port's data direction register, output register and control register with its flags and CA2/CB2 mode, the pins, and whether /IRQA and /IRQB are low. A 6850 ACIA: the control register (divide, word format, transmitter control, receive interrupt), the status register and its bits (RDRF, TDRE, DCD, CTS, FE, OVRN, PE, IRQ), the baud rates its TxCLK and RxCLK give, whether it is still waiting for a master reset, and what it has sent and received. A 68681 DUART: each channel's MR1, MR2, clock select and baud rate, status (RxRDY, FFULL, TxRDY, TxEMT, OE, PE, FE, RB), whether its transmitter and receiver are on, the FIFO depth, its mode (normal, echo, loopbacks) and what it has sent and received; ISR, IMR, IVR, ACR, the output port and OPCR, and the counter/timer's preload, count, output and whether it runs. A 68230 PI/T: PGCR, PSRR, each port's control, direction and output registers, PSR, the port and timer vectors, the timer's control, preload, count and ZDS. A 68901 MFP: GPIP data, edges and directions, the interrupt enable, pending, in-service and mask registers (channel 15 in bit 15), VR, each timer's control, count, reload and time-outs, and the USART's control, status and what it has sent. A Z80 PIO: each port's mode, output and input registers, I/O register and mask (mode 3), vector, interrupt control and RDY. A Z80 CTC: each channel's control word decoded, time constant, down-counter, whether it runs or waits, its zero counts, and the vector. A Z80 SIO/0 or DART: each channel's write registers WR0-WR7, RR0 and RR1 with their bits, the format and baud rates, the characters waiting, and what it has sent and received, and the vector with RR2. For the Z80 chips, which interrupt sources are pending and under service in the daisy chain. An 8155 or 8156: the command register decoded (each port's direction, port C's ALT mode), the status register and its bits (INTR, BF, INTE for each port, TIMER), and the timer's length, mode, whether it runs, the count still to go and its terminal counts. An 8255A: the control word, each port's mode and direction, the output and input latches, what port C reads as, and the handshake bits (IBF, /OBF, INTR, INTE). An 8251A: the mode and command, what the next control write will be taken as, the format, clock factor and baud rates, the status and its bits (TxRDY, RxRDY, TxEMPTY, PE, OE, FE, SYNDET/BRKDET, DSR), break detect, and what it has sent and received. An 8253 or 8254: each counter's mode, BCD, byte order, count register, counting element, OUT, GATE, null count and latch, and on the 8254 its status byte. An 8259A: IRR, ISR and IMR with the levels named, the ICWs, 8085 or 8086 mode, edge or level, AEOI, the lowest priority, the read select, INT, the acknowledges and spurious IR7s, and each level's CALL address or vector. In the app this is the live simulation; otherwise where the last run_transient left it.")]
    public Task<string> PeripheralState(
        [Description("The chip's designator, e.g. \"U13\". Optional when the circuit has only one 6821, 6520, 6850, 68681, 68230, 68901, Z80 PIO, CTC, SIO or DART, or 8155, 8156, 8255A, 8251A, 8253, 8254 or 8259A.")] string? part = null,
        CancellationToken cancel = default) =>
        Read("peripheral_state", s =>
        {
            var all = s.Circuit.Components.Where(IsPeripheral).ToList();
            CircuitComponent chip;
            if (string.IsNullOrWhiteSpace(part))
            {
                chip = all.Count switch
                {
                    1 => all[0],
                    0 => throw new ArgumentException("The circuit has no 6821, 6520, 6850, 68681, 68230, 68901, Z80 PIO, CTC, SIO or DART, or 8155, 8156, 8255A, 8251A, 8253, 8254 or 8259A."),
                    _ => throw new ArgumentException($"The circuit has {all.Count} of them ({string.Join(", ", all.Select(c => c.Name))}): say which with part."),
                };
            }
            else
            {
                var c = Pins.Part(s.Circuit, part);
                chip = IsPeripheral(c) ? c : throw new ArgumentException($"{c.Name} is a {c.ComponentType}, not a 6821, 6520, 6850, 68681, 68230, 68901, Z80 PIO, CTC, SIO or DART, or 8155, 8156, 8255A, 8251A, 8253, 8254 or 8259A.");
            }

            static string Hex(int value, int digits = 2) => "$" + value.ToString(digits == 2 ? "X2" : digits == 4 ? "X4" : "X6", CultureInfo.InvariantCulture);

            switch (chip)
            {
                case PeripheralInterfaceAdapter pia:
                    return StatePia(pia);

                case Acia6850 acia:
                    return StateAcia(acia);

                case Z80Pio pio:
                    return StatePio(pio);

                case Z80Ctc ctc:
                    return StateCtc(ctc);

                case Z80SerialController sio:
                    return StateSio(sio);

                case RamIoTimer8155 riot:
                    return State8155(riot);

                case Ppi8255 ppi:
                    return State8255(ppi);

                case Usart8251 uart:
                    return State8251(uart);

                case IntervalTimer pit:
                    return State8253(pit);

                case Pic8259 pic:
                    return State8259(pic);

                case Duart68681 duart:
                {
                    JsonObject Channel(bool b)
                    {
                        var c = duart.Describe(b);
                        var status = c.Status;
                        string[] names = ["RxRDY", "FFULL", "TxRDY", "TxEMT", "OE", "PE", "FE", "RB"];
                        var bits = names.Where((_, i) => (status & (1 << i)) != 0).Select(n => (JsonNode)n).ToArray();
                        var sent = b ? duart.TransmittedTextB : duart.TransmittedTextA;
                        var received = string.Concat((b ? duart.ReceivedB : duart.ReceivedA).Select(x => x is >= 0x20 and < 0x7F ? (char)x : '.'));
                        return new JsonObject
                        {
                            ["mr1"] = Hex(c.Mr1),
                            ["mr2"] = Hex(c.Mr2),
                            ["csr"] = Hex(c.Csr),
                            ["baud"] = Math.Round(b ? duart.BaudRateB : duart.BaudRateA, 1),
                            ["status"] = Hex(status),
                            ["status_bits"] = new JsonArray(bits),
                            ["transmitter_on"] = c.Tx,
                            ["receiver_on"] = c.Rx,
                            ["fifo"] = c.Fifo,
                            ["mode"] = c.Mode,
                            ["sent"] = sent,
                            ["received"] = received,
                        };
                    }

                    return new JsonObject
                    {
                        ["part"] = duart.Name,
                        ["chip"] = duart.PartNumber,
                        ["channel_a"] = Channel(false),
                        ["channel_b"] = Channel(true),
                        ["isr"] = Hex(duart.InterruptStatus),
                        ["imr"] = Hex(duart.InterruptMask),
                        ["ivr"] = Hex(duart.InterruptVector),
                        ["acr"] = Hex(duart.AuxiliaryControl),
                        ["output_port"] = Hex(duart.OutputPortRegister),
                        ["opcr"] = Hex(duart.OutputPortConfiguration),
                        ["counter"] = new JsonObject
                        {
                            ["preload"] = duart.CounterPreload,
                            ["count"] = duart.CounterValue,
                            ["running"] = duart.CounterRunning,
                            ["output"] = duart.CounterOutput,
                        },
                    };
                }

                case Pit68230 pit:
                {
                    JsonObject Port((byte Control, byte Direction, byte Output) p) => new()
                    {
                        ["control"] = Hex(p.Control),
                        ["submode"] = p.Control >> 6,
                        ["direction"] = Hex(p.Direction),
                        ["output"] = Hex(p.Output),
                    };

                    return new JsonObject
                    {
                        ["part"] = pit.Name,
                        ["chip"] = pit.PartNumber,
                        ["pgcr"] = Hex(pit.GeneralControl),
                        ["mode"] = pit.GeneralControl >> 6,
                        ["psrr"] = Hex(pit.ServiceRequest),
                        ["port_a"] = Port(pit.PortARegisters),
                        ["port_b"] = Port(pit.PortBRegisters),
                        ["port_c"] = new JsonObject { ["direction"] = Hex(pit.PortCDirection), ["output"] = Hex(pit.PortCOutput) },
                        ["psr"] = Hex(pit.PortStatus),
                        ["pivr"] = Hex(pit.PortVector),
                        ["timer"] = new JsonObject
                        {
                            ["tcr"] = Hex(pit.TimerControl),
                            ["tivr"] = Hex(pit.TimerVector),
                            ["preload"] = Hex(pit.TimerPreload, 6),
                            ["count"] = Hex(pit.TimerCount, 6),
                            ["zero_detected"] = pit.ZeroDetected,
                        },
                    };
                }

                default:
                {
                    var mfp = (Mfp68901)chip;
                    var timers = new JsonArray();
                    for (var i = 0; i < 4; i++)
                    {
                        var t = mfp.TimerState(i);
                        timers.Add(new JsonObject
                        {
                            ["timer"] = ((char)('A' + i)).ToString(),
                            ["control"] = Hex(t.Control),
                            ["count"] = t.Count,
                            ["reload"] = t.Reload,
                            ["timeouts"] = t.Timeouts,
                        });
                    }

                    return new JsonObject
                    {
                        ["part"] = mfp.Name,
                        ["chip"] = mfp.PartNumber,
                        ["gpdr"] = Hex(mfp.GpipData),
                        ["aer"] = Hex(mfp.ActiveEdges),
                        ["ddr"] = Hex(mfp.Direction),
                        ["ier"] = Hex(mfp.InterruptEnable, 4),
                        ["ipr"] = Hex(mfp.InterruptPending, 4),
                        ["isr"] = Hex(mfp.InterruptInService, 4),
                        ["imr"] = Hex(mfp.InterruptMask, 4),
                        ["vr"] = Hex(mfp.VectorRegister),
                        ["timers"] = timers,
                        ["ucr"] = Hex(mfp.UsartControl),
                        ["rsr"] = Hex(mfp.ReceiverStatus),
                        ["tsr"] = Hex(mfp.TransmitterStatus),
                        ["sent"] = mfp.TransmittedText,
                    };
                }
            }
        }, cancel);

    private static bool IsPeripheral(CircuitComponent c) => c is Duart68681 or Pit68230 or Mfp68901 or PeripheralInterfaceAdapter or Acia6850 or Z80Peripheral or RamIoTimer8155 or Ppi8255 or Usart8251 or IntervalTimer or Pic8259;

    private static string Hex2(int value) => "$" + value.ToString("X2", CultureInfo.InvariantCulture);

    /// <summary>A 6821's (or 6520's) ports, control registers and interrupts, for peripheral_state.</summary>
    private static JsonObject StatePia(PeripheralInterfaceAdapter pia)
    {
        static string Line2(int control, bool b)
        {
            var mode = (control >> 3) & 7;
            var line = b ? "CB2" : "CA2";
            return mode switch
            {
                < 4 => $"{line} an input, {((mode & 2) != 0 ? "rising" : "falling")} edge, interrupt {((mode & 1) != 0 ? "on" : "off")}",
                4 => b ? "CB2 a write strobe, restored by CB1" : "CA2 a read strobe, restored by CA1",
                5 => b ? "CB2 a one-cycle write strobe" : "CA2 a one-cycle read strobe",
                _ => $"{line} held {((mode & 1) != 0 ? "high" : "low")}",
            };
        }

        JsonObject Port(byte direction, byte output, byte control, bool b) => new()
        {
            ["direction"] = Hex2(direction),
            ["output"] = Hex2(output),
            ["control"] = Hex2(control),
            ["data_register_selected"] = (control & 0x04) != 0,
            ["c1_edge"] = (control & 0x02) != 0 ? "rising" : "falling",
            ["c1_interrupt_enabled"] = (control & 0x01) != 0,
            ["c1_flag"] = (control & 0x80) != 0,
            ["c2_flag"] = (control & 0x40) != 0,
            ["c2"] = Line2(control, b),
        };

        return new JsonObject
        {
            ["part"] = pia.Name,
            ["chip"] = pia.PartNumber,
            ["port_a"] = Port(pia.DirectionA, pia.OutputA, pia.ControlA, false),
            ["port_b"] = Port(pia.DirectionB, pia.OutputB, pia.ControlB, true),
            ["irqa_low"] = pia.IrqAAsserted,
            ["irqb_low"] = pia.IrqBAsserted,
            ["note"] = "Port A's lines are pulled up and read as their pins; port B's are three-state and read their output register where they drive.",
        };
    }

    /// <summary>A 6850's registers, rates and traffic, for peripheral_state.</summary>
    private static JsonObject StateAcia(Acia6850 acia)
    {
        var status = acia.Status;
        string[] names = ["RDRF", "TDRE", "DCD", "CTS", "FE", "OVRN", "PE", "IRQ"];
        var control = acia.Control;
        return new JsonObject
        {
            ["part"] = acia.Name,
            ["chip"] = acia.PartNumber,
            ["control"] = Hex2(control),
            ["divide"] = (control & 3) == 3 ? "master reset" : $"÷{acia.Divide}",
            ["word"] = acia.WordFormat,
            ["transmitter"] = ((control >> 5) & 3) switch { 0 => "/RTS low, interrupt off", 1 => "/RTS low, interrupt on", 2 => "/RTS high, interrupt off", _ => "/RTS low, sending a break" },
            ["receive_interrupt"] = (control & 0x80) != 0,
            ["status"] = Hex2(status),
            ["status_bits"] = new JsonArray([.. names.Where((_, i) => (status & (1 << i)) != 0).Select(n => (JsonNode)n)]),
            ["in_reset"] = acia.InReset,
            ["transmit_baud"] = Math.Round(acia.TransmitBaud, 1),
            ["receive_baud"] = Math.Round(acia.ReceiveBaud, 1),
            ["sent"] = acia.TransmittedText,
            ["received"] = string.Concat(acia.ReceivedBytes.Select(x => x is >= 0x20 and < 0x7F ? (char)x : '.')),
        };
    }

    /// <summary>A 6809's registers, flags, state and latest bus cycles, for cpu_state.</summary>
    private static JsonObject State6809(Processor6809 cpu, int cycles)
    {
        var core = cpu.Core;
        var log = cpu.BusLog;
        var recent = log.Skip(Math.Max(0, log.Count - cycles)).Select(c => (JsonNode)string.Create(CultureInfo.InvariantCulture,
            $"{c.Kind switch { M6809CycleKind.Fetch => "fetch", M6809CycleKind.Write => "write", M6809CycleKind.Dead => "dead", M6809CycleKind.Vector => "vector", _ => "read" }} ${c.Address:X4} {c.Data:X2}")).ToArray();

        var result = new JsonObject
        {
            ["part"] = cpu.Name,
            ["chip"] = cpu.PartNumber,
            ["pc"] = $"${core.PC:X4}",
            ["a"] = $"${core.A:X2}",
            ["b"] = $"${core.B:X2}",
            ["d"] = $"${core.D:X4}",
            ["x"] = $"${core.X:X4}",
            ["y"] = $"${core.Y:X4}",
            ["u"] = $"${core.U:X4}",
            ["s"] = $"${core.S:X4}",
            ["dp"] = $"${core.DP:X2}",
            ["cc"] = $"${core.CC:X2}",
            ["flags"] = Processor6809.Flags(core.CC),
            ["instruction"] = cpu.CurrentInstruction,
            ["cycles"] = core.Cycles,
            ["instructions"] = core.Instructions,
            ["last_interrupt"] = core.LastInterrupt,
            ["nmi_armed"] = core.NmiArmed,
            ["state"] = core.IsStopped ? "stopped" : core.ResetAsserted ? "held in reset" : core.IsHalted ? "halted" : core.IsGranted ? "bus granted"
                : core.IsSyncing ? "SYNC" : core.IsWaiting ? "CWAI" : "running",
            ["stretched_quarters"] = cpu.StretchedQuarters,
            ["recent_cycles"] = new JsonArray(recent),
        };
        if (cpu.UndefinedReads > 0) result["undefined_reads"] = $"{cpu.UndefinedReads}, first at ${cpu.FirstUndefinedRead:X4}";
        if (core.Cycles == 0) result["note"] = "It hasn't run a cycle yet: run_transient runs the circuit (or transport run, in the app).";
        return result;
    }

    /// <summary>6809 code in a memory part, for disassemble.</summary>
    private static JsonObject Disassemble6809(Circuit circuit, string part, int? address, int origin, int count)
    {
        if (count is < 1 or > 200) throw new ArgumentException("count must be from 1 to 200.");
        if (origin is < 0 or > 0xFFFF) throw new ArgumentException("origin must be from 0 to 65535.");
        var memory = Memory(circuit, part);
        var start = address ?? origin;
        if (start < origin || start >= origin + memory.Size)
            throw new ArgumentException($"{memory.Name} holds ${origin:X4}-${origin + memory.Size - 1:X4} with origin ${origin:X4}; ${start:X4} isn't in it.");

        byte Byte(int a) => a >= origin && a < origin + memory.Size ? memory.ReadByte(a - origin) : (byte)0;

        var lines = new JsonArray();
        var at = start;
        for (var i = 0; i < count && at < origin + memory.Size; i++)
        {
            var (text, length) = Opcodes6809.Disassemble(at, a => Byte(a & 0xFFFF));
            var bytes = string.Join(" ", Enumerable.Range(0, length).Select(k => Byte(at + k).ToString("X2", CultureInfo.InvariantCulture)));
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"${at:X4}  {bytes,-14}  {text}"));
            at += length;
        }

        var result = new JsonObject { ["part"] = memory.Name, ["variant"] = "6809", ["lines"] = lines };
        if (origin + memory.Size == 0x10000)
        {
            int Word(int a) => (Byte(a) << 8) | Byte(a + 1);
            result["vectors"] = new JsonObject
            {
                ["swi3"] = $"${Word(0xFFF2):X4}", ["swi2"] = $"${Word(0xFFF4):X4}", ["firq"] = $"${Word(0xFFF6):X4}", ["irq"] = $"${Word(0xFFF8):X4}",
                ["swi"] = $"${Word(0xFFFA):X4}", ["nmi"] = $"${Word(0xFFFC):X4}", ["reset"] = $"${Word(0xFFFE):X4}",
            };
        }

        return result;
    }

    /// <summary>A 68000's registers, flags, state and latest bus cycles, for cpu_state.</summary>
    private static JsonObject State68000(Cpu68000 cpu, int cycles)
    {
        var core = cpu.Core;
        var d = new JsonObject();
        var a = new JsonObject();
        for (var i = 0; i < 8; i++)
        {
            d[$"d{i}"] = $"${core.GetD(i):X8}";
            a[$"a{i}"] = $"${core.GetA(i):X8}";
        }

        var log = cpu.BusLog.Where(c => c.Kind != M68000CycleKind.Instruction).ToList();
        var recent = log.Skip(Math.Max(0, log.Count - cycles)).Select(c => (JsonNode)string.Create(CultureInfo.InvariantCulture,
            $"{c.Kind switch { M68000CycleKind.Write => "write", M68000CycleKind.InterruptAcknowledge => "iack", _ => "read" }}.{(c.IsWord ? 'w' : 'b')} ${c.Address:X6} {(c.IsWord ? c.Data.ToString("X4", CultureInfo.InvariantCulture) : c.Data.ToString("X2", CultureInfo.InvariantCulture))} {Cpu68000BusDecoder.Space(c.FunctionCode)}{(c.WaitStates > 0 ? $" +{c.WaitStates}w" : "")}")).ToArray();

        var result = new JsonObject
        {
            ["part"] = cpu.Name,
            ["chip"] = cpu.PartNumber,
            ["pc"] = $"${core.PC & 0xFFFFFF:X6}",
            ["sr"] = $"${core.SR:X4}",
            ["flags"] = Cpu68000.Flags(core.SR),
            ["data"] = d,
            ["address"] = a,
            ["usp"] = $"${core.Usp:X8}",
            ["ssp"] = $"${core.Ssp:X8}",
            ["instruction"] = cpu.CurrentInstruction,
            ["clocks"] = cpu.ClockCycles,
            ["bus_cycles"] = cpu.BusCycles,
            ["wait_states"] = cpu.WaitStates,
            ["instructions"] = core.Instructions,
            ["last_exception"] = core.LastException,
            ["state"] = core.IsHalted ? "halted" : core.IsStopped ? "stopped" : core.ResetAsserted ? "held in reset" : "running",
            ["recent_cycles"] = new JsonArray(recent),
        };
        if (cpu.UndefinedReads > 0) result["undefined_reads"] = $"{cpu.UndefinedReads}, first at ${cpu.FirstUndefinedRead:X6}";
        if (cpu.ClockCycles == 0) result["note"] = "It hasn't run yet: run_transient runs the circuit (or transport run, in the app).";
        return result;
    }

    /// <summary>68000 code in a memory part, or across a pair of them, for disassemble.</summary>
    private static JsonObject Disassemble68000(Circuit circuit, string part, string? oddPart, int? address, int origin, int count)
    {
        if (count is < 1 or > 200) throw new ArgumentException("count must be from 1 to 200.");
        if (origin < 0) throw new ArgumentException("origin can't be negative.");
        var even = Memory(circuit, part);
        var odd = string.IsNullOrWhiteSpace(oddPart) ? null : Memory(circuit, oddPart);
        if (odd is not null && odd.Size != even.Size) throw new ArgumentException($"{even.Name} and {odd.Name} are different sizes: a pair is two of the same chip.");
        var size = odd is null ? even.Size : even.Size * 2;
        var start = address ?? origin;
        if (start < origin || start >= origin + size)
            throw new ArgumentException($"The memory holds ${origin:X6}-${origin + size - 1:X6} with origin ${origin:X6}; ${start:X6} isn't in it.");
        if ((start & 1) != 0) throw new ArgumentException("A 68000 instruction starts at an even address.");

        byte Byte(long at)
        {
            var offset = at - origin;
            if (offset < 0 || offset >= size) return 0;
            if (odd is null) return even.ReadByte((int)offset);
            return (offset & 1) == 0 ? even.ReadByte((int)(offset >> 1)) : odd.ReadByte((int)(offset >> 1));
        }

        ushort Word(uint at) => (ushort)((Byte(at) << 8) | Byte(at + 1));

        var lines = new JsonArray();
        var pc = (uint)start;
        for (var i = 0; i < count && pc < origin + size; i++)
        {
            var (text, length) = M68000Disassembler.Disassemble(pc, Word);
            var words = string.Join(" ", Enumerable.Range(0, length / 2).Select(k => Word(pc + (uint)(k * 2)).ToString("X4", CultureInfo.InvariantCulture)));
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"${pc:X6}  {words,-24}  {text}"));
            pc += (uint)length;
        }

        var result = new JsonObject
        {
            ["part"] = odd is null ? even.Name : $"{even.Name} (even) and {odd.Name} (odd)",
            ["variant"] = "68000",
            ["lines"] = lines,
        };
        if (origin == 0 && size >= 8)
        {
            uint Long(uint at) => (uint)((Word(at) << 16) | Word(at + 2));
            result["vectors"] = new JsonObject { ["ssp"] = $"${Long(0):X8}", ["reset_pc"] = $"${Long(4):X8}" };
        }

        return result;
    }

    /// <summary>A Z80's registers, flags, state and latest machine cycles, for cpu_state.</summary>
    private static JsonObject StateZ80(CpuZ80 cpu, int cycles)
    {
        var core = cpu.Core;
        var log = cpu.BusLog;
        var recent = log.Skip(Math.Max(0, log.Count - cycles)).Select(c => (JsonNode)string.Create(CultureInfo.InvariantCulture,
            $"{c.Kind switch { Z80CycleKind.Fetch => c.Halted ? "halt-nop" : "m1", Z80CycleKind.Write => "write", Z80CycleKind.IoRead => "in", Z80CycleKind.IoWrite => "out", Z80CycleKind.Acknowledge => "inta", Z80CycleKind.Internal => "refresh", _ => "read" }} ${c.Address:X4}{(c.IsRefresh ? "" : $" {c.Data:X2}")}{(c.WaitStates > 0 ? $" +{c.WaitStates}w" : "")}")).ToArray();

        var result = new JsonObject
        {
            ["part"] = cpu.Name,
            ["chip"] = cpu.PartNumber,
            ["pc"] = $"${core.PC:X4}",
            ["af"] = $"${core.AF:X4}",
            ["bc"] = $"${core.BC:X4}",
            ["de"] = $"${core.DE:X4}",
            ["hl"] = $"${core.HL:X4}",
            ["alternate"] = new JsonObject
            {
                ["af"] = $"${core.AlternateAF:X4}",
                ["bc"] = $"${core.AlternateBC:X4}",
                ["de"] = $"${core.AlternateDE:X4}",
                ["hl"] = $"${core.AlternateHL:X4}",
            },
            ["ix"] = $"${core.IX:X4}",
            ["iy"] = $"${core.IY:X4}",
            ["sp"] = $"${core.SP:X4}",
            ["i"] = $"${core.I:X2}",
            ["r"] = $"${core.R:X2}",
            ["wz"] = $"${core.WZ:X4}",
            ["iff1"] = core.Iff1,
            ["iff2"] = core.Iff2,
            ["interrupt_mode"] = core.InterruptMode,
            ["flags"] = CpuZ80.Flags(core.F),
            ["instruction"] = cpu.CurrentInstruction,
            ["t_states"] = core.TStates,
            ["instructions"] = core.Instructions,
            ["wait_states"] = core.WaitStates,
            ["last_interrupt"] = core.LastInterrupt,
            ["state"] = core.ResetAsserted ? "held in reset" : core.BusAcknowledged ? "bus released" : core.Halted ? "halted" : "running",
            ["recent_cycles"] = new JsonArray(recent),
        };
        if (cpu.UndefinedReads > 0) result["undefined_reads"] = $"{cpu.UndefinedReads}, first at ${cpu.FirstUndefinedRead:X4}";
        if (core.TStates == 0) result["note"] = "It hasn't run a T-state yet: run_transient runs the circuit (or transport run, in the app).";
        return result;
    }

    /// <summary>Z80 code in a memory part, for disassemble.</summary>
    private static JsonObject DisassembleZ80(Circuit circuit, string part, int? address, int origin, int count)
    {
        if (count is < 1 or > 200) throw new ArgumentException("count must be from 1 to 200.");
        if (origin is < 0 or > 0xFFFF) throw new ArgumentException("origin must be from 0 to 65535.");
        var memory = Memory(circuit, part);
        var start = address ?? origin;
        if (start < origin || start >= origin + memory.Size)
            throw new ArgumentException($"{memory.Name} holds ${origin:X4}-${origin + memory.Size - 1:X4} with origin ${origin:X4}; ${start:X4} isn't in it.");

        byte Byte(int a) => a >= origin && a < origin + memory.Size ? memory.ReadByte(a - origin) : (byte)0;

        var lines = new JsonArray();
        var at = start;
        for (var i = 0; i < count && at < origin + memory.Size; i++)
        {
            var (text, length) = OpcodesZ80.Disassemble(at, a => Byte(a & 0xFFFF));
            var bytes = string.Join(" ", Enumerable.Range(0, length).Select(k => Byte(at + k).ToString("X2", CultureInfo.InvariantCulture)));
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"${at:X4}  {bytes,-11}  {text}"));
            at += length;
        }

        return new JsonObject { ["part"] = memory.Name, ["variant"] = "Z80", ["lines"] = lines };
    }

    /// <summary>The daisy chain's view of a Z80 peripheral: its sources pending and under service.</summary>
    private static JsonObject Chain(Z80Peripheral chip)
    {
        JsonArray Named(IReadOnlyList<bool> flags) => [.. chip.InterruptSources.Where((_, i) => flags[i]).Select(n => (JsonNode)n)];
        return new JsonObject
        {
            ["pending"] = Named(chip.PendingSources),
            ["under_service"] = Named(chip.InService),
            ["int_low"] = chip.Interrupting,
            ["acknowledges"] = chip.Acknowledges,
            ["retis"] = chip.Returns,
        };
    }

    /// <summary>A Z80 PIO's ports and interrupts, for peripheral_state.</summary>
    private static JsonObject StatePio(Z80Pio pio)
    {
        JsonObject Port(bool b)
        {
            var p = pio.Describe(b);
            var port = new JsonObject
            {
                ["mode"] = p.Mode,
                ["mode_name"] = p.Mode switch { 0 => "output", 1 => "input", 2 => "bidirectional", _ => "bit control" },
                ["output"] = Hex2(p.Output),
                ["input"] = Hex2(p.Input),
                ["vector"] = Hex2(p.Vector),
                ["interrupt_enabled"] = p.InterruptEnabled,
                ["ready"] = p.Ready,
            };
            if (p.Mode == 3)
            {
                port["io_register"] = Hex2(p.Direction);
                port["mask"] = Hex2(p.Mask);
                port["logic"] = $"{(p.And ? "AND" : "OR")}, active {(p.ActiveHigh ? "high" : "low")}";
            }

            return port;
        }

        return new JsonObject
        {
            ["part"] = pio.Name,
            ["chip"] = pio.PartNumber,
            ["port_a"] = Port(false),
            ["port_b"] = Port(true),
            ["chain"] = Chain(pio),
        };
    }

    /// <summary>A Z80 CTC's channels, for peripheral_state.</summary>
    private static JsonObject StateCtc(Z80Ctc ctc)
    {
        var channels = new JsonArray();
        for (var i = 0; i < 4; i++)
        {
            var c = ctc.Describe(i);
            var counter = (c.Control & 0x40) != 0;
            channels.Add(new JsonObject
            {
                ["channel"] = i,
                ["control"] = Hex2(c.Control),
                ["mode"] = counter ? $"counter, {((c.Control & 0x10) != 0 ? "rising" : "falling")} edges" : $"timer, prescaler {((c.Control & 0x20) != 0 ? 256 : 16)}",
                ["interrupt_enabled"] = (c.Control & 0x80) != 0,
                ["time_constant"] = c.TimeConstant,
                ["counter"] = c.Counter,
                ["state"] = c.Running ? "running" : c.AwaitingConstant ? "waiting for a time constant" : c.AwaitingTrigger ? "waiting for its trigger" : "stopped",
                ["zero_counts"] = c.ZeroCounts,
            });
        }

        return new JsonObject
        {
            ["part"] = ctc.Name,
            ["chip"] = ctc.PartNumber,
            ["vector"] = Hex2(ctc.Vector),
            ["channels"] = channels,
            ["chain"] = Chain(ctc),
        };
    }

    /// <summary>A Z80 SIO's or DART's channels, for peripheral_state.</summary>
    private static JsonObject StateSio(Z80SerialController sio)
    {
        JsonObject Channel(bool b)
        {
            var rr0 = sio.ReadRegisterValue(b, 0);
            var rr1 = sio.ReadRegisterValue(b, 1);
            string[] rr0Names = ["Rx available", b ? "-" : "INT pending", "Tx buffer empty", "DCD", sio.IsDart ? "RI" : "SYNC", "CTS", "Tx underrun", "break"];
            string[] rr1Names = ["all sent", "-", "-", "-", "parity error", "Rx overrun", "framing error", "-"];
            var (tx, rx) = sio.Baud(b);
            var wr = new JsonObject();
            for (var r = 0; r < (sio.IsDart ? 6 : 8); r++)
                if (r != 2 || b) wr[$"wr{r}"] = Hex2(sio.WriteRegisterValue(b, r));
            return new JsonObject
            {
                ["write_registers"] = wr,
                ["rr0"] = Hex2(rr0),
                ["rr0_bits"] = new JsonArray([.. rr0Names.Where((n, i) => n != "-" && (rr0 & (1 << i)) != 0).Select(n => (JsonNode)n)]),
                ["rr1"] = Hex2(rr1),
                ["rr1_bits"] = new JsonArray([.. rr1Names.Where((n, i) => n != "-" && (rr1 & (1 << i)) != 0).Select(n => (JsonNode)n)]),
                ["format"] = sio.Format(b),
                ["transmit_baud"] = Math.Round(tx, 1),
                ["receive_baud"] = Math.Round(rx, 1),
                ["waiting"] = sio.Waiting(b),
                ["sent"] = sio.TransmittedText(b),
                ["received"] = string.Concat(sio.ReceivedBytes(b).Select(x => x is >= 0x20 and < 0x7F ? (char)x : '.')),
            };
        }

        return new JsonObject
        {
            ["part"] = sio.Name,
            ["chip"] = sio.PartNumber,
            ["channel_a"] = Channel(false),
            ["channel_b"] = Channel(true),
            ["vector_rr2"] = Hex2(sio.ReadRegisterValue(true, 2)),
            ["chain"] = Chain(sio),
            ["note"] = "Asynchronous modes only: the synchronous ones (SDLC, bisync) are not modelled.",
        };
    }

    /// <summary>An 8155's or 8156's command and status, ports and timer, for peripheral_state.</summary>
    private static JsonObject State8155(RamIoTimer8155 chip)
    {
        var status = chip.Status;
        string[] statusNames = ["A INTR", "A BF", "A INTE", "B INTR", "B BF", "B INTE", "TIMER", "-"];
        var (a, b, c) = chip.OutputLatches;
        var command = chip.Command;
        return new JsonObject
        {
            ["part"] = chip.Name,
            ["chip"] = chip.PartNumber,
            ["command"] = Hex2(command),
            ["port_a"] = (command & 1) != 0 ? $"output {Hex2(a)}" : "input",
            ["port_b"] = (command & 2) != 0 ? $"output {Hex2(b)}" : "input",
            ["port_c"] = chip.PortCAlternative switch
            {
                1 => "ALT1: all inputs",
                2 => $"ALT2: all outputs, {Hex2(c & 0x3F)}",
                3 => "ALT3: port A strobed (PC0 INTR, PC1 BF, PC2 /STB), PC3-PC5 outputs",
                _ => "ALT4: ports A and B strobed",
            },
            ["status"] = Hex2(status),
            ["status_bits"] = new JsonArray([.. statusNames.Where((n, i) => n != "-" && (status & (1 << i)) != 0).Select(n => (JsonNode)n)]),
            ["timer"] = new JsonObject
            {
                ["length"] = chip.TimerLength,
                ["mode"] = chip.TimerMode switch { 0 => "single square wave", 1 => "continuous square wave", 2 => "single pulse", _ => "continuous pulses" },
                ["running"] = chip.TimerRunning,
                ["remaining"] = chip.TimerRemaining,
                ["terminal_counts"] = chip.TerminalCounts,
            },
        };
    }

    /// <summary>An 8255A's modes, latches and handshakes, for peripheral_state.</summary>
    private static JsonObject State8255(Ppi8255 ppi)
    {
        var p = ppi.Describe();
        static string Mode(int mode, bool input) => $"mode {mode}, {(mode == 2 ? "bidirectional" : input ? "input" : "output")}";
        return new JsonObject
        {
            ["part"] = ppi.Name,
            ["chip"] = ppi.PartNumber,
            ["control"] = Hex2(p.Control),
            ["port_a"] = new JsonObject { ["mode"] = Mode(p.ModeA, p.AInput), ["output_latch"] = Hex2(p.OutputA), ["input_latch"] = Hex2(p.InputA) },
            ["port_b"] = new JsonObject { ["mode"] = Mode(p.ModeB, p.BInput), ["output_latch"] = Hex2(p.OutputB), ["input_latch"] = Hex2(p.InputB) },
            ["port_c"] = new JsonObject
            {
                ["upper"] = p.CUpperInput ? "input" : "output",
                ["lower"] = p.CLowerInput ? "input" : "output",
                ["output_latch"] = Hex2(p.OutputC),
                ["reads_as"] = Hex2(p.PortC),
            },
            ["handshakes"] = new JsonObject
            {
                ["ibf_a"] = p.IbfA, ["obf_a"] = p.ObfA, ["intr_a"] = p.IntrA, ["inte_a"] = p.InteA, ["inte_2"] = p.Inte2,
                ["ibf_b"] = p.IbfB, ["obf_b"] = p.ObfB, ["intr_b"] = p.IntrB, ["inte_b"] = p.InteB,
            },
        };
    }

    /// <summary>An 8251A's mode, command, status, rates and traffic, for peripheral_state.</summary>
    private static JsonObject State8251(Usart8251 uart)
    {
        var status = uart.Status;
        string[] statusNames = ["TxRDY", "RxRDY", "TxEMPTY", "PE", "OE", "FE", "SYNDET/BRKDET", "DSR"];
        return new JsonObject
        {
            ["part"] = uart.Name,
            ["chip"] = uart.PartNumber,
            ["mode"] = Hex2(uart.Mode),
            ["command"] = Hex2(uart.Command),
            ["expects"] = uart.Expects,
            ["format"] = uart.Format,
            ["clock_factor"] = uart.Factor,
            ["status"] = Hex2(status),
            ["status_bits"] = new JsonArray([.. statusNames.Where((_, i) => (status & (1 << i)) != 0).Select(n => (JsonNode)n)]),
            ["transmit_baud"] = Math.Round(uart.TransmitBaud, 1),
            ["receive_baud"] = Math.Round(uart.ReceiveBaud, 1),
            ["break_detected"] = uart.BreakDetected,
            ["sent"] = uart.TransmittedText,
            ["received"] = string.Concat(uart.ReceivedBytes.Select(x => x is >= 0x20 and < 0x7F ? (char)x : '.')),
            ["note"] = "Asynchronous modes only: a synchronous mode is accepted but leaves both halves idle.",
        };
    }

    /// <summary>An 8253's or 8254's three counters, for peripheral_state.</summary>
    private static JsonObject State8253(IntervalTimer pit)
    {
        var counters = new JsonArray();
        for (var i = 0; i < 3; i++)
        {
            var c = pit.Describe(i);
            var counter = new JsonObject
            {
                ["counter"] = i,
                ["programmed"] = c.Programmed,
                ["mode"] = c.Mode,
                ["mode_name"] = c.Mode switch
                {
                    0 => "interrupt on terminal count",
                    1 => "retriggerable one-shot",
                    2 => "rate generator",
                    3 => "square wave",
                    4 => "software-triggered strobe",
                    _ => "hardware-triggered strobe",
                },
                ["bcd"] = c.Bcd,
                ["read_write"] = c.ReadWrite switch { 1 => "LSB", 2 => "MSB", _ => "LSB then MSB" },
                ["count_register"] = c.CountRegister,
                ["counting_element"] = c.CountingElement,
                ["out"] = c.Output,
                ["gate"] = c.Gate,
                ["null_count"] = c.NullCount,
                ["count_latched"] = c.CountLatched,
                ["running"] = c.Running,
                ["terminal_counts"] = c.Expirations,
            };
            if (pit is Pit8254 p8254) counter["status_byte"] = Hex2(p8254.StatusByte(i));
            counters.Add(counter);
        }

        return new JsonObject { ["part"] = pit.Name, ["chip"] = pit.PartNumber, ["counters"] = counters };
    }

    /// <summary>An 8259A's registers, mode and vectors, for peripheral_state.</summary>
    private static JsonObject State8259(Pic8259 pic)
    {
        static JsonArray Levels(byte bits) => [.. Enumerable.Range(0, 8).Where(i => (bits & (1 << i)) != 0).Select(i => (JsonNode)$"IR{i}")];
        var result = new JsonObject
        {
            ["part"] = pic.Name,
            ["chip"] = pic.PartNumber,
            ["initialised"] = pic.Initialised,
            ["irr"] = Hex2(pic.Irr),
            ["isr"] = Hex2(pic.Isr),
            ["imr"] = Hex2(pic.Imr),
            ["requesting"] = Levels(pic.Irr),
            ["in_service"] = Levels(pic.Isr),
            ["masked"] = Levels(pic.Imr),
            ["icw"] = new JsonArray(Hex2(pic.Icw1), Hex2(pic.Icw2), Hex2(pic.Icw3), Hex2(pic.Icw4)),
            ["mode"] = pic.Mode8086 ? "8086" : "8085 (CALL)",
            ["trigger"] = pic.LevelTriggered ? "level" : "edge",
            ["automatic_eoi"] = pic.AutomaticEoi,
            ["single"] = pic.Single,
            ["special_mask_mode"] = pic.SpecialMaskMode,
            ["lowest_priority"] = $"IR{pic.LowestPriority}",
            ["read_select"] = pic.ReadSelect,
            ["int_high"] = pic.Interrupting,
            ["acknowledges"] = pic.Acknowledges,
            ["spurious"] = pic.Spurious,
        };
        if (!pic.Initialised && pic.ExpectingIcw > 0) result["expecting"] = $"ICW{pic.ExpectingIcw}";
        result["handlers"] = pic.Mode8086
            ? new JsonArray([.. Enumerable.Range(0, 8).Select(i => (JsonNode)$"IR{i}: vector {Hex2(pic.Vector(i))}")])
            : new JsonArray([.. Enumerable.Range(0, 8).Select(i => (JsonNode)$"IR{i}: CALL ${pic.CallAddress(i):X4}")]);
        return result;
    }

    /// <summary>An 8085's registers, flags, interrupt system, state and latest machine cycles, for cpu_state.</summary>
    private static JsonObject State8085(Cpu8085 cpu, int cycles)
    {
        var core = cpu.Core;
        var log = cpu.BusLog;
        var recent = log.Skip(Math.Max(0, log.Count - cycles)).Select(c => (JsonNode)string.Create(CultureInfo.InvariantCulture,
            $"{c.Kind switch { I8085CycleKind.OpcodeFetch => "fetch", I8085CycleKind.MemoryWrite => "write", I8085CycleKind.IoRead => "in", I8085CycleKind.IoWrite => "out", I8085CycleKind.InterruptAcknowledge => "inta", I8085CycleKind.RestartAcknowledge => "rst-ack", I8085CycleKind.BusIdle => "bus-idle", I8085CycleKind.Halt => "halt", I8085CycleKind.Hold => "hold", _ => "read" }} ${c.Address:X4}{(c.Kind is I8085CycleKind.BusIdle or I8085CycleKind.Halt or I8085CycleKind.Hold or I8085CycleKind.RestartAcknowledge ? "" : $" {c.Data:X2}")}{(c.WaitStates > 0 ? $" +{c.WaitStates}w" : "")}")).ToArray();

        var result = new JsonObject
        {
            ["part"] = cpu.Name,
            ["chip"] = cpu.PartNumber,
            ["pc"] = $"${core.PC:X4}",
            ["a"] = $"${core.A:X2}",
            ["bc"] = $"${core.BC:X4}",
            ["de"] = $"${core.DE:X4}",
            ["hl"] = $"${core.HL:X4}",
            ["sp"] = $"${core.SP:X4}",
            ["flags"] = Cpu8085.Flags(core.F),
            ["interrupts_enabled"] = core.InterruptsEnabled,
            ["masks"] = new JsonObject
            {
                ["rst_7_5"] = (core.Masks & 4) != 0,
                ["rst_6_5"] = (core.Masks & 2) != 0,
                ["rst_5_5"] = (core.Masks & 1) != 0,
            },
            ["rst_7_5_pending"] = core.Rst75Pending,
            ["trap_pending"] = core.TrapPending,
            ["sid"] = core.Sid,
            ["sod"] = core.Sod,
            ["hlda"] = core.HoldAcknowledged,
            ["instruction"] = cpu.CurrentInstruction,
            ["t_states"] = core.TStates,
            ["instructions"] = core.Instructions,
            ["wait_states"] = core.WaitStates,
            ["hold_states"] = core.HoldStates,
            ["last_interrupt"] = core.LastInterrupt,
            ["state"] = core.ResetAsserted ? "held in reset" : core.Kind == I8085CycleKind.Hold ? "bus held (HOLD)" : core.Halted ? "halted" : "running",
            ["recent_cycles"] = new JsonArray(recent),
        };
        if (core.LastAcknowledgeBytes.Count > 0)
            result["last_acknowledge_bytes"] = string.Join(" ", core.LastAcknowledgeBytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
        if (cpu.UndefinedReads > 0) result["undefined_reads"] = $"{cpu.UndefinedReads}, first at ${cpu.FirstUndefinedRead:X4}";
        if (core.TStates == 0) result["note"] = "It hasn't run a T-state yet: run_transient runs the circuit (or transport run, in the app).";
        return result;
    }

    /// <summary>8085 code in a memory part, for disassemble, with the restart addresses named when the chip holds page 0.</summary>
    private static JsonObject Disassemble8085(Circuit circuit, string part, int? address, int origin, int count)
    {
        if (count is < 1 or > 200) throw new ArgumentException("count must be from 1 to 200.");
        if (origin is < 0 or > 0xFFFF) throw new ArgumentException("origin must be from 0 to 65535.");
        var memory = Memory(circuit, part);
        var start = address ?? origin;
        if (start < origin || start >= origin + memory.Size)
            throw new ArgumentException($"{memory.Name} holds ${origin:X4}-${origin + memory.Size - 1:X4} with origin ${origin:X4}; ${start:X4} isn't in it.");

        byte Byte(int a) => a >= origin && a < origin + memory.Size ? memory.ReadByte(a - origin) : (byte)0;

        var lines = new JsonArray();
        var at = start;
        for (var i = 0; i < count && at < origin + memory.Size; i++)
        {
            var (text, length) = Opcodes8085.Disassemble(at, a => Byte(a & 0xFFFF));
            var bytes = string.Join(" ", Enumerable.Range(0, length).Select(k => Byte(at + k).ToString("X2", CultureInfo.InvariantCulture)));
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"${at:X4}  {bytes,-8}  {text}"));
            at += length;
        }

        var result = new JsonObject { ["part"] = memory.Name, ["variant"] = "8085", ["lines"] = lines };
        if (origin == 0 && memory.Size >= 0x40)
        {
            result["restarts"] = new JsonObject
            {
                ["reset"] = "$0000",
                ["trap"] = "$0024",
                ["rst_5_5"] = "$002C",
                ["rst_6_5"] = "$0034",
                ["rst_7_5"] = "$003C",
                ["rstv"] = "$0040",
                ["rst_n"] = "RST n calls n x 8: $0000, $0008 ... $0038",
            };
        }

        return result;
    }

    /// <summary>A 6502-family processor or a 68000: the one called <paramref name="name"/>, or the circuit's only processor.</summary>
    private static CircuitComponent AnyProcessor(Circuit circuit, string? name)
    {
        var all = circuit.Components.Where(c => c is Processor6502 or Cpu68000 or Processor6809 or CpuZ80 or Cpu8085).ToList();
        if (string.IsNullOrWhiteSpace(name))
        {
            return all.Count switch
            {
                1 => all[0],
                0 => throw new ArgumentException("The circuit has no 6502, 65C02, 6510, 68000, 6809, Z80 or 8085."),
                _ => throw new ArgumentException($"The circuit has {all.Count} processors ({string.Join(", ", all.Select(c => c.Name))}): say which with part."),
            };
        }

        var c = Pins.Part(circuit, name);
        return c is Processor6502 or Cpu68000 or Processor6809 or CpuZ80 or Cpu8085 ? c : throw new ArgumentException($"{c.Name} is a {c.ComponentType}, not a processor.{(all.Count > 0 ? $" The processors: {string.Join(", ", all.Select(p => p.Name))}." : "")}");
    }

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
