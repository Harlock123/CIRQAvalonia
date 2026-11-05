using Cirq.Components.Digital;
using static Cirq.Mcp.Tests.Calls;

namespace Cirq.Mcp.Tests;

/// <summary>cpu_state and disassemble, and a run handing its processor and memory state back to the circuit.</summary>
public class ProcessorToolTests
{
    [Fact]
    public async Task A_run_leaves_the_processor_and_the_lcd_where_it_ended()
    {
        using var b = new Bench();
        await b.Tools.LoadExample("Ben Eater 6502 Computer");

        var before = await Parse(b.Tools.CpuState());
        Assert.Equal(0, (long)before["cycles"]!);
        Assert.Contains("hasn't run", (string)before["note"]!);

        await b.Tools.RunTransient(System.Text.Json.JsonSerializer.SerializeToElement("4m"), plot: false);

        var after = await Parse(b.Tools.CpuState("U2", cycles: 4));
        Assert.Equal("running", (string)after["state"]!);
        Assert.InRange((long)after["cycles"]!, 3990, 4010);
        Assert.Equal(4, after["recent_cycles"]!.AsArray().Count);
        Assert.Null(after["undefined_reads"]);

        Assert.Equal("Hello, world!", b.Circuit.Components.OfType<CharacterLcd>().Single().Line(0).TrimEnd());

        // The RAM holds what the run wrote — the stack — while its preset stays empty.
        var stack = await Parse(b.Tools.ReadMemory("U4", start: 0x1F0, length: 16));
        Assert.True((int)stack["used_bytes"]! > 0);
    }

    [Fact]
    public async Task The_rom_disassembles_from_its_origin_with_its_vectors()
    {
        using var b = new Bench();
        await b.Tools.LoadExample("Ben Eater 6502 Computer");

        var listing = await Parse(b.Tools.Disassemble("U3", origin: 0x8000, count: 3));
        Assert.Equal(["$8000  A2 FF     LDX #$FF", "$8002  9A        TXS", "$8003  A9 E0     LDA #$E0"],
            listing["lines"]!.AsArray().Select(l => (string)l!));
        Assert.Equal("$8000", (string)listing["vectors"]!["reset"]!);

        Assert.Contains("isn't in it", await Error(b.Tools.Disassemble("U3", address: 0x1000, origin: 0x8000)));
        Assert.Contains("not a memory", await Error(b.Tools.Disassemble("U2")));
        Assert.Contains("variant is", await Error(b.Tools.Disassemble("U3", variant: "Z80")));
        Assert.Contains("not a processor", await Error(b.Tools.CpuState("U3")));
    }

    /// <summary>The 68000 computer: its ROM pair disassembled with the reset vectors, a run, its registers and wait states, and the DUART's state.</summary>
    [Fact]
    public async Task The_68000_computer_disassembles_runs_and_reports()
    {
        using var b = new Bench();
        await b.Tools.LoadExample("68000 Single-Board Computer");

        var listing = await Parse(b.Tools.Disassemble("U3", odd_part: "U4", address: 0x400, origin: 0, count: 2, variant: "68000"));
        Assert.Equal(["$000400  13FC 0010 00C0 0005       MOVE.B #$10,$C00005", "$000408  13FC 0013 00C0 0001       MOVE.B #$13,$C00001"],
            listing["lines"]!.AsArray().Select(l => (string)l!));
        Assert.Equal("$00210000", (string)listing["vectors"]!["ssp"]!);
        Assert.Equal("$00000400", (string)listing["vectors"]!["reset_pc"]!);
        Assert.Contains("even address", await Error(b.Tools.Disassemble("U3", odd_part: "U4", address: 0x401, variant: "68000")));

        await b.Tools.RunTransient(System.Text.Json.JsonSerializer.SerializeToElement("1m"), plot: false);

        var cpu = await Parse(b.Tools.CpuState(cycles: 4));
        Assert.Equal("MC68000", (string)cpu["chip"]!);
        Assert.Equal("running", (string)cpu["state"]!);
        Assert.InRange(Convert.ToUInt32(((string)cpu["ssp"]!)[1..], 16), 0x20FFF0u, 0x210000u);   // the stack, a call or two deep
        Assert.StartsWith("tS7", (string)cpu["flags"]!);
        Assert.True((long)cpu["wait_states"]! > 100);
        Assert.Equal(4, cpu["recent_cycles"]!.AsArray().Count);

        var duart = await Parse(b.Tools.PeripheralState());
        Assert.Equal("MC68681", (string)duart["chip"]!);
        Assert.Equal(38400, (double)duart["channel_a"]!["baud"]!);
        Assert.True((bool)duart["channel_a"]!["transmitter_on"]!);
        Assert.StartsWith("CI", (string)duart["channel_a"]!["sent"]!);
        Assert.Contains("not a 68681", await Error(b.Tools.PeripheralState("U2")));
    }

    [Fact]
    public async Task Peripheral_state_reads_a_pit_and_an_mfp()
    {
        using var b = new Bench();
        await b.Tools.AddParts([new() { Kind = "68230 PI/T" }, new() { Kind = "68901 MFP", X = 300 }]);
        Assert.Contains("say which", await Error(b.Tools.PeripheralState()));

        var pit = await Parse(b.Tools.PeripheralState("U1"));
        Assert.Equal("$0F", (string)pit["pivr"]!);
        Assert.Equal("$000000", (string)pit["timer"]!["preload"]!);

        var mfp = await Parse(b.Tools.PeripheralState("U2"));
        Assert.Equal(4, mfp["timers"]!.AsArray().Count);
        Assert.Equal("$0000", (string)mfp["ipr"]!);
    }

    [Fact]
    public async Task Cpu_state_needs_a_processor()
    {
        using var b = new Bench();
        await b.Tools.AddParts([new() { Kind = "Resistor" }]);
        Assert.Contains("no 6502", await Error(b.Tools.CpuState()));
        Assert.Contains("no 68681", await Error(b.Tools.PeripheralState()));
    }

    /// <summary>sid_state before and after a run of the SID player, and what it says without a SID.</summary>
    [Fact]
    public async Task Sid_state_describes_the_voices_after_a_run()
    {
        using var b = new Bench();
        await b.Tools.LoadExample("6510 SID Player");

        var before = await Parse(b.Tools.SidState());
        Assert.Contains("hasn't been clocked", (string)before["note"]!);
        Assert.Equal("6581", (string)before["chip"]!);

        await b.Tools.RunTransient(System.Text.Json.JsonSerializer.SerializeToElement("8m"), plot: false);

        var after = await Parse(b.Tools.SidState("U8"));
        Assert.Equal(15, (int)after["volume"]!);
        Assert.Equal(1e6, (double)after["clock_hz"]!, 1.0);
        var bass = after["voices"]![0]!;
        Assert.Equal(["sawtooth"], bass["waveforms"]!.AsArray().Select(w => (string)w!));
        Assert.True((bool)bass["filtered"]!);
        Assert.Equal("$0735", (string)bass["frequency"]!);
        Assert.Equal(["pulse"], after["voices"]![1]!["waveforms"]!.AsArray().Select(w => (string)w!));
        Assert.Equal(8, (int)after["filter"]!["resonance"]!);

        var cpu = await Parse(b.Tools.CpuState("U2", cycles: 0));
        Assert.Equal("6510", (string)cpu["chip"]!);
        Assert.Equal("$01", (string)cpu["port"]!["direction"]!);

        Assert.Contains("not a SID", await Error(b.Tools.SidState("U2")));
    }

    [Fact]
    public async Task Sid_state_needs_a_sid()
    {
        using var b = new Bench();
        await b.Tools.AddParts([new() { Kind = "Resistor" }]);
        Assert.Contains("no SID", await Error(b.Tools.SidState()));
    }
}
