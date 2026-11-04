using Cirq.Components.Digital;
using static Cirq.Mcp.Tests.Calls;

namespace Cirq.Mcp.Tests;

/// <summary>load_memory and read_memory: an image into a ROM or RAM, and the bytes back out.</summary>
public class MemoryToolTests
{
    [Fact]
    public async Task Intel_hex_text_loads_at_its_own_addresses_wrapped_into_the_chip()
    {
        using var b = new Bench();
        await b.Tools.AddParts([new() { Kind = "28C256" }]);

        var hex = MemoryImage.ToIntelHex(0xFFFC, [0x00, 0x80]);
        var loaded = await Parse(b.Tools.LoadMemory("U1", hex: hex));
        Assert.Equal("Intel HEX", (string)loaded["format"]!);
        Assert.Contains("2 bytes at $7FFC-$7FFD", (string)loaded["loaded"]!);

        var read = await Parse(b.Tools.ReadMemory("U1", start: 0x7FF0, length: 16));
        Assert.Equal("7FF0: 00 00 00 00 00 00 00 00 00 00 00 00 00 80 00 00", (string)read["rows"]![0]!);
        Assert.Equal(1, (int)read["used_bytes"]!);

        // One undo step, and undo takes the image back out.
        await b.Tools.Undo();
        Assert.Equal(0, b.Circuit.Components.OfType<Eeprom28C256>().Single().UsedBytes);
    }

    [Fact]
    public async Task Plain_bytes_base64_and_a_file_load_at_the_offset()
    {
        using var b = new Bench();
        await b.Tools.AddParts([new() { Kind = "62256" }, new() { Kind = "2716", X = 200 }, new() { Kind = "27C256", X = 400 }]);

        await b.Tools.LoadMemory("U1", hex: "A9 01 8D 00 60", offset: 0x200);
        Assert.Equal(0x8D, b.Circuit.Components.OfType<Sram62256>().Single().ReadByte(0x202));

        await b.Tools.LoadMemory("U2", base64: Convert.ToBase64String([0xEA, 0x4C]));
        Assert.Equal(0x4C, b.Circuit.Components.OfType<Eprom2716>().Single().ReadByte(1));

        File.WriteAllBytes(b.Folder.File("rom.bin"), [1, 2, 3, 4]);
        var loaded = await Parse(b.Tools.LoadMemory("U3", path: "rom.bin", offset: 16));
        Assert.Equal("rom.bin", (string)loaded["from"]!);
        Assert.Equal(4, b.Circuit.Components.OfType<Eprom27C256>().Single().ReadByte(19));
    }

    [Fact]
    public async Task Mistakes_are_said_in_words()
    {
        using var b = new Bench();
        await b.Tools.AddParts([new() { Kind = "2716" }, new() { Kind = "Resistor", X = 200 }]);

        Assert.Contains("exactly one of", await Error(b.Tools.LoadMemory("U1")));
        Assert.Contains("exactly one of", await Error(b.Tools.LoadMemory("U1", hex: "00", base64: "AA==")));
        Assert.Contains("not a memory", await Error(b.Tools.LoadMemory("R1", hex: "00")));
        Assert.Contains("Line 1", await Error(b.Tools.LoadMemory("U1", hex: ":0100000055AB")));
        Assert.Contains("whole bytes", await Error(b.Tools.LoadMemory("U1", hex: "A9 1")));
        Assert.Contains("bigger than this 2K part", await Error(b.Tools.LoadMemory("U1", base64: Convert.ToBase64String(new byte[3000]))));
        Assert.Contains("outside the working folder", await Error(b.Tools.LoadMemory("U1", path: "../x.bin")));
        Assert.Contains("start must be below 2048", await Error(b.Tools.ReadMemory("U1", start: 4096)));
        Assert.Contains("length must be", await Error(b.Tools.ReadMemory("U1", length: 5000)));

        // Nothing changed, and no undo steps were left behind by the failures.
        Assert.Equal(1, b.Session.UndoCount);
    }

    [Fact]
    public async Task A_full_image_is_cut_short_in_the_settings_list()
    {
        using var b = new Bench();
        await b.Tools.AddParts([new() { Kind = "27C256" }]);
        await b.Tools.LoadMemory("U1", base64: Convert.ToBase64String([.. Enumerable.Range(0, 32768).Select(i => (byte)(i | 1))]));

        var part = await Parse(b.Tools.GetPart("U1"));
        var contents = part["settings"]!.AsArray().Single(s => (string)s!["name"]! == "Contents")!;
        Assert.True(((string)contents["value"]!).Length < 700);
        Assert.Contains("read_memory", (string)contents["note"]!);
    }
}
