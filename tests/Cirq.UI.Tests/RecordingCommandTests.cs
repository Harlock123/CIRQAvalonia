using Cirq.Components.Passive;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The Play Speaker Recording command, up to the point where it hands the file to the desktop.
/// What it does past that is the desktop's business and is not something a test should be starting
/// processes to find out — so what is checked here is that it says something useful in each of the
/// cases where there is nothing to play.
/// </summary>
public class RecordingCommandTests
{
    [Fact]
    public void ItSaysSoWhenNoSpeakerIsRecording()
    {
        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        vm.Circuit.Add(new Speaker(8.0));

        vm.PlayRecordingCommand.Execute(null);

        Assert.Contains("Recording Path", vm.StatusMessage);
    }

    [Fact]
    public void AndWhenOneIsArmedButNothingHasRunYet()
    {
        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();

        var speaker = new Speaker(8.0) { RecordingPath = Path.Combine(Path.GetTempPath(), "unused.wav") };
        vm.Circuit.Add(speaker);

        vm.PlayRecordingCommand.Execute(null);

        Assert.Contains("run the circuit first", vm.StatusMessage);
        Assert.Equal(0, speaker.RecordedSeconds);
    }

    /// <summary>
    /// An empty path is the default and means "do not record", so a speaker that has never been
    /// asked to record writes nothing anywhere.
    /// </summary>
    [Fact]
    public void ASpeakerWithNoPathRecordsNothing()
    {
        var speaker = new Speaker(8.0);

        Assert.False(speaker.IsRecording);
        Assert.Equal(string.Empty, speaker.RecordingPath);

        // And asking it to flush is harmless rather than an exception about an empty filename.
        speaker.Flush();
        Assert.Null(speaker.RecordingError);
    }
}
