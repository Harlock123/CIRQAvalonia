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
