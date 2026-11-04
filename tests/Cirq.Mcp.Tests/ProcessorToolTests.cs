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

    [Fact]
    public async Task Cpu_state_needs_a_processor()
    {
        using var b = new Bench();
        await b.Tools.AddParts([new() { Kind = "Resistor" }]);
        Assert.Contains("no 6502", await Error(b.Tools.CpuState()));
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
