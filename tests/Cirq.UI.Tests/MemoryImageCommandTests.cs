using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.UI.Services;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// Edit > Load Memory Image: a binary or Intel HEX file into the selected memory part, as one undo
/// step, with what went wrong said in words.
/// </summary>
public class MemoryImageCommandTests : IDisposable
{
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("cirq-ui-image-");

    public void Dispose() => _folder.Delete(recursive: true);

    private string File(string name, byte[] bytes)
    {
        var path = Path.Combine(_folder.FullName, name);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task ABinaryIsLoadedAtItsOffsetAndCanBeUndone()
    {
        using var vm = new MainWindowViewModel();
        var rom = vm.Circuit.Add(new Eprom2716());
        vm.SelectedComponent = rom;
        vm.FileDialogs = new FakeFileDialogs { MemoryImage = new MemoryImageRequest(File("rom.bin", [0xA9, 0x01, 0x60]), 0x10) };

        await vm.LoadMemoryImageCommand.ExecuteAsync(null);

        Assert.Equal(0xA9, rom.ReadByte(0x10));
        Assert.Equal(0x60, rom.ReadByte(0x12));
        Assert.Contains("3 bytes", vm.StatusMessage);
        Assert.True(vm.IsModified);

        vm.UndoCommand.Execute(null);
        var restored = vm.Circuit.Components.OfType<Eprom2716>().Single();
        Assert.Equal(0, restored.UsedBytes);
    }

    /// <summary>S-records, and one lane of a 68000's image into one of its pair of ROMs.</summary>
    [Fact]
    public async Task AnSRecordFileLoadsItsOddBytes()
    {
        using var vm = new MainWindowViewModel();
        var rom = vm.Circuit.Add(new Eeprom28C256());
        vm.SelectedComponent = rom;
        var records = MemoryImage.ToSRecords(0x400, [0x30, 0x3C, 0x00, 0xFF]);
        vm.FileDialogs = new FakeFileDialogs
        {
            MemoryImage = new MemoryImageRequest(File("rom.s19", System.Text.Encoding.ASCII.GetBytes(records)), 0, ByteLane.Odd),
        };

        await vm.LoadMemoryImageCommand.ExecuteAsync(null);

        Assert.Equal(0x3C, rom.ReadByte(0x200));
        Assert.Equal(0xFF, rom.ReadByte(0x201));
        Assert.Contains("S-record, odd bytes", vm.StatusMessage);
    }

    [Fact]
    public async Task ABadHexFileIsReportedWithItsLine()
    {
        using var vm = new MainWindowViewModel();
        var ram = vm.Circuit.Add(new Sram62256());
        vm.SelectedComponent = ram;
        var dialogs = new FakeFileDialogs { MemoryImage = new MemoryImageRequest(File("bad.hex", ":0100000055AB\n"u8.ToArray()), 0) };
        vm.FileDialogs = dialogs;

        await vm.LoadMemoryImageCommand.ExecuteAsync(null);

        var report = Assert.Single(dialogs.Reports);
        Assert.Contains("Line 1", report.Message);
        Assert.Equal(0, ram.UsedBytes);
    }

    [Fact]
    public async Task ItNeedsAMemorySelected()
    {
        using var vm = new MainWindowViewModel();
        vm.SelectedComponent = vm.Circuit.Components.OfType<Resistor>().First();
        vm.FileDialogs = new FakeFileDialogs();

        await vm.LoadMemoryImageCommand.ExecuteAsync(null);

        Assert.Contains("Select a memory part", vm.StatusMessage);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1792", 1792)]
    [InlineData("$700", 0x700)]
    [InlineData("0x7f0", 0x7F0)]
    [InlineData("-1", null)]
    [InlineData("seven", null)]
    public void TheOffsetIsReadAsDecimalOrHex(string typed, int? expected) =>
        Assert.Equal(expected, StorageProviderFileDialogs.ParseOffset(typed));
}
