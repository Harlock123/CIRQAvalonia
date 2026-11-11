using Cirq.Components.Protoboard;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>Fault-finding lessons, their scores and Claude's coaching; and a circuit's Gerber files from the editor.</summary>
public class FaultLessonTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("cirq-lessons-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); }
        catch (IOException) { }
    }

    [Fact]
    public void Every_lesson_sets_up_its_fault_and_a_find_is_scored_and_kept()
    {
        var scores = new Dictionary<string, int>();
        var saves = 0;
        foreach (var lesson in FaultLessons.All)
        {
            using var vm = new MainWindowViewModel { FaultScores = scores, SaveSettings = () => saves++ };
            vm.StartLessonCommand.Execute(lesson);
            Assert.True(vm.HasHiddenFault, $"{lesson.Name}: {vm.StatusMessage}");
            Assert.Same(lesson, vm.CurrentLesson);
            Assert.Contains(lesson.Symptom, vm.StatusMessage);
            var fault = vm.Circuit.Faults.Single();
            Assert.Equal(lesson.Kind, fault.Kind);
            Assert.Equal(lesson.Pin, $"{fault.First.Owner!.Name}.{fault.First.Name}");

            vm.SelectedComponent = fault.First.Owner;
            vm.GuessFaultCommand.Execute(null);
            Assert.Contains("Score 100 out of 100. A new best.", vm.StatusMessage);
            Assert.False(vm.HasHiddenFault);
            Assert.Null(vm.CurrentLesson);
        }

        Assert.Equal(FaultLessons.All.Select(l => l.Name).Distinct().Count(), scores.Count);
        Assert.Equal(scores.Count, saves);

        // A worse score the second time leaves the best alone.
        using var again = new MainWindowViewModel { FaultScores = scores, SaveSettings = () => saves++ };
        var first = FaultLessons.All[0];
        again.StartLessonCommand.Execute(first);
        Assert.Equal($"{first.Name}  (best 100)", again.LessonHeader(first));
        again.FaultHintCommand.Execute(null);
        again.SelectedComponent = again.Circuit.Faults[0].First.Owner;
        again.GuessFaultCommand.Execute(null);
        Assert.Contains("Score 90 out of 100. Your best is 100.", again.StatusMessage);
        Assert.Equal(100, scores[first.Name]);
    }

    [Fact]
    public void Coach_me_asks_claude_to_guide_without_giving_the_fault_away()
    {
        using var vm = new MainWindowViewModel();
        string? asked = null;
        vm.RequestCoach += (_, prompt) => asked = prompt;
        vm.CoachMeCommand.Execute(null);
        Assert.Null(asked);
        Assert.StartsWith("There's no fault hidden", vm.StatusMessage);

        vm.StartLessonCommand.Execute(FaultLessons.All[0]);
        vm.CoachMeCommand.Execute(null);
        Assert.NotNull(asked);
        Assert.Contains("Don't tell me where it is", asked);
        Assert.Contains("measure", asked);
        Assert.Contains(FaultLessons.All[0].Symptom, asked);
    }

    [Fact]
    public void Gerber_files_are_exported_from_any_board_as_a_pcb()
    {
        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        Examples.All.Single(e => e.Name == "555 Astable").Build(vm);
        Assert.Equal(Cirq.Core.Topology.BoardKind.Breadboard, vm.Circuit.Board.Kind);
        var path = Path.Combine(_folder, "astable gerbers.zip");
        var said = vm.ExportGerbers(path);
        Assert.StartsWith("Gerber files written to astable gerbers.zip", said);
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        Assert.Equal(["astable_gerbers-B_Cu.gbl", "astable_gerbers-B_Mask.gbs", "astable_gerbers-Edge_Cuts.gm1", "astable_gerbers-F_SilkS.gto", "astable_gerbers.drl"],
            zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));

        // On a PCB in the view, the board drawn is the board exported.
        vm.IsPcb = true;
        Assert.True(vm.IsPcb);
        Assert.Contains("printed circuit board", vm.StatusMessage);
        var layout = ProtoboardLayout.Arrange(vm.Circuit);
        Assert.Equal(Cirq.Core.Topology.BoardKind.Pcb, layout.Kind);
        Assert.True(layout.Matches, string.Join(" ", layout.Problems));
    }
}
