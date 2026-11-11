using Cirq.Components.Processors;
using Cirq.Components.Protoboard;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>The bench instruments, guided builds, lesson files, the share page, the design rules, the bill and the debugger, as the editor drives them.</summary>
public class BenchAndLessonTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("cirq-bench-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); }
        catch (IOException) { }
    }

    private static MainWindowViewModel Open(string example)
    {
        var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        vm.Scope.ClearProbes();
        Examples.All.Single(e => e.Name == example).Build(vm);
        return vm;
    }

    [Fact]
    public void Bench_probes_come_and_go_with_their_instruments()
    {
        using var vm = Open("555 Astable");
        var output = vm.Circuit.Components.Single(c => c.Name == "U1").TerminalByName("OUT");
        vm.IsAnalyserShown = true;
        vm.SetBenchProbe("LA D0", ProbeKind.Logic, output);
        vm.IsBenchScopeShown = true;
        vm.SetBenchProbe("Bench CH1", ProbeKind.Voltage, output);
        Assert.Equal(ProbeKind.Logic, vm.Scope.Probes.Single(p => p.Label == "LA D0").Kind);
        Assert.Equal(ProbeKind.Voltage, vm.Scope.Probes.Single(p => p.Label == "Bench CH1").Kind);

        // Clipped elsewhere, it moves; unclipped, it goes; the analyser put away, its probes go with it.
        var trigger = vm.Circuit.Components.Single(c => c.Name == "U1").TerminalByName("TRIG");
        vm.SetBenchProbe("LA D0", ProbeKind.Logic, trigger);
        Assert.Same(trigger, vm.Scope.Probes.Single(p => p.Label == "LA D0").TargetTerminal);
        vm.SetBenchProbe("Bench CH1", ProbeKind.Voltage, null);
        Assert.DoesNotContain(vm.Scope.Probes, p => p.Label == "Bench CH1");
        vm.IsAnalyserShown = false;
        Assert.DoesNotContain(vm.Scope.Probes, p => p.Label.StartsWith("LA ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_signal_generator_on_the_bench_is_wired_by_dragging_its_lead()
    {
        using var vm = Open("RC Low-Pass");
        var before = vm.Circuit.Components.Count;
        vm.AddBenchGeneratorCommand.Execute(null);
        var generator = vm.Circuit.Components.OfType<Cirq.Components.Sources.FunctionGenerator>().Last();
        Assert.Equal(before + 2, vm.Circuit.Components.Count);
        Assert.Equal(BoardForm.Bench, BoardPackages.For(generator).Form);

        var target = vm.Circuit.Components.OfType<Cirq.Components.Passive.Capacitor>().First().Terminals[0];
        vm.MoveBenchLead(generator.Output, target);
        var netlist = vm.Circuit.BuildNetlist();
        Assert.Same(netlist.NetOf(target), netlist.NetOf(generator.Output));

        // Moved again, it leaves where it was.
        var ground = vm.Circuit.Components.OfType<Cirq.Components.Sources.Ground>().First().Terminals[0];
        vm.MoveBenchLead(generator.Output, ground);
        netlist = vm.Circuit.BuildNetlist();
        Assert.NotSame(netlist.NetOf(target), netlist.NetOf(generator.Output));
        vm.UndoCommand.Execute(null);
        netlist = vm.Circuit.BuildNetlist();
        Assert.Equal(netlist.NetOf(vm.Circuit.Components.OfType<Cirq.Components.Passive.Capacitor>().First().Terminals[0]).Index,
            netlist.NetOf(vm.Circuit.Components.OfType<Cirq.Components.Sources.FunctionGenerator>().Last().Output).Index);
    }

    [Fact]
    public void Every_build_lesson_is_built_hole_by_hole_then_measured_and_scored()
    {
        var scores = new Dictionary<string, int>();
        foreach (var lesson in BuildLessons.All)
        {
            using var vm = new MainWindowViewModel { FaultScores = scores, SaveSettings = () => { } };
            vm.StartBuildLessonCommand.Execute(lesson);
            Assert.True(vm.IsBuildingLesson, vm.StatusMessage);
            Assert.Contains(lesson.Intro, vm.StatusMessage);

            var layout = ProtoboardLayout.Arrange(vm.Circuit);
            var steps = BuildGuide.Steps(layout);

            // A hole no step uses is a mistake, named back.
            var stray = new BoardHole(1, Breadboard.A, 60);
            vm.BuildLessonHole(stray);
            Assert.StartsWith($"That's {layout.HoleName(stray)}", vm.StatusMessage);

            while (vm.IsBuildingLesson)
            {
                var step = steps[vm.BuildLessonStep];
                var holes = step.Holes ?? step.Part?.Holes ?? (step.Wire is { } w ? [w.From, w.To] : [.. step.Bench!.Leads.Select(l => l.To)]);
                foreach (var hole in holes.Distinct()) vm.BuildLessonHole(hole);
            }

            Assert.True(vm.IsMeterShown);
            foreach (var task in lesson.Measures)
            {
                var dot = task.Pin.IndexOf('.', StringComparison.Ordinal);
                var terminal = vm.Circuit.Components.First(c => c.Name == task.Pin[..dot]).TerminalByName(task.Pin[(dot + 1)..]);
                var net = vm.Circuit.BuildNetlist().NetOf(terminal);
                var hole = layout.Parts.SelectMany(p => p.Package.Pins.Select((t, i) => (t, h: p.Holes[i]))).First(x => x.t is not null && vm.Circuit.BuildNetlist().NetOf(x.t).Index == net.Index).h;
                vm.BuildLessonReading(hole, null);
                Assert.Contains("run the circuit", vm.StatusMessage);
                vm.BuildLessonReading(hole, (task.Low + task.High) / 2);
            }

            Assert.Contains("Score 95 out of 100. A new best.", vm.StatusMessage);
            Assert.Null(vm.CurrentBuild);
        }

        Assert.Equal(BuildLessons.All.Count, scores.Keys.Count(k => k.StartsWith("Build: ", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_lesson_saved_to_a_file_opens_with_its_fault_and_symptom()
    {
        var path = Path.Combine(_folder, "my lesson.cirqlesson");
        using (var teacher = Open("Inverting Amplifier"))
        {
            Assert.Throws<InvalidOperationException>(() => teacher.SaveFaultLesson(path, "No gain", "It doesn't amplify."));
            var resistor = teacher.Circuit.Components.Single(c => c.Name == "R2");
            FaultFinding.Hide(teacher.Circuit, FaultKind.Open, resistor.TerminalByName("A"));
            Assert.Contains("saved", teacher.SaveFaultLesson(path, "No gain", "It should invert and amplify. It doesn't."));
        }

        Assert.DoesNotContain("Faults", File.ReadAllText(path));   // the fault is the lesson's, not the circuit's

        using var student = new MainWindowViewModel();
        student.OpenFaultLesson(path);
        Assert.True(student.HasHiddenFault);
        Assert.Equal("No gain", student.CurrentLesson!.Name);
        Assert.Contains("It should invert and amplify. It doesn't.", student.StatusMessage);
        var fault = student.Circuit.Faults.Single();
        Assert.Equal(("R2", "A", FaultKind.Open), (fault.First.Owner!.Name, fault.First.Name, fault.Kind));

        File.WriteAllText(Path.Combine(_folder, "bad.cirqlesson"), "{ not json");
        Assert.Throws<InvalidDataException>(() => student.OpenFaultLesson(Path.Combine(_folder, "bad.cirqlesson")));
    }

    [Fact]
    public void The_share_page_holds_the_schematic_the_board_the_parts_the_steps_and_the_file()
    {
        using var vm = Open("555 Astable");
        var path = Path.Combine(_folder, "astable.html");
        Assert.StartsWith("Share page written", vm.ExportSharePage(path));
        var html = File.ReadAllText(path);
        Assert.Contains("<svg", html);
        Assert.Contains("data:image/png;base64,", html);
        Assert.Contains("Download the circuit (.cirq)", html);
        Assert.Contains("NE555", html);
        foreach (var step in BuildGuide.Steps(ProtoboardLayout.Arrange(vm.Circuit))) Assert.Contains(System.Net.WebUtility.HtmlEncode(step.Text), html);

        // The embedded file is the circuit, whole.
        var data = System.Text.RegularExpressions.Regex.Match(html, "data:application/json;base64,([^\"]+)").Groups[1].Value;
        var reopened = Cirq.Components.Serialization.CircuitSerializer.FromJson(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(data)));
        Assert.Equal(vm.Circuit.Components.Count, reopened.Circuit.Components.Count);
    }

    [Fact]
    public void The_design_rules_and_the_bill_of_materials_come_from_the_menu()
    {
        using var vm = Open("LED Chaser");
        vm.CheckDesignRulesCommand.Execute("0");
        Assert.True(vm.Circuit.Board.Kind.IsPcb());
        Assert.NotNull(vm.DrcIssues);
        Assert.StartsWith("Design rules (Board house (standard))", vm.StatusMessage);

        vm.IsDoubleSidedPcb = true;
        Assert.Equal(BoardKind.DoubleSidedPcb, vm.Circuit.Board.Kind);
        Assert.Null(vm.DrcIssues);
        vm.IsGroundPour = false;
        Assert.False(vm.Circuit.Board.GroundPour);
        vm.UndoCommand.Execute(null);
        Assert.True(vm.Circuit.Board.GroundPour);

        var bom = Path.Combine(_folder, "chaser.csv");
        Assert.StartsWith("Bill of materials written", vm.ExportBillOfMaterials(bom));
        Assert.Contains("4017", File.ReadAllText(bom));
        var gerbers = Path.Combine(_folder, "chaser.zip");
        Assert.Contains("double-sided", vm.ExportGerbers(gerbers));
        using var zip = System.IO.Compression.ZipFile.OpenRead(gerbers);
        Assert.Equal(7, zip.Entries.Count);
    }

    [Fact]
    public void The_firmware_debugger_lists_the_code_and_sets_breakpoints()
    {
        using var vm = Open("ATmega328P Blink");
        var chip = vm.Circuit.Components.OfType<Atmega328p>().Single();
        var debugger = new FirmwareDebuggerViewModel(vm, chip);
        debugger.Refresh();
        Assert.Contains("ATmega328P", debugger.Title);
        Assert.Equal("rjmp 0x0017", debugger.Lines[0].Text);   // to start, past the vector at word 22
        Assert.True(debugger.Lines[0].IsPc);
        Assert.Contains("PC $0000", debugger.Registers);
        Assert.Contains("PORTB 00000000", debugger.Io);

        debugger.BreakpointText = "$2C";
        debugger.AddBreakpointCommand.Execute(null);
        Assert.Contains(0x2C, chip.Breakpoints);
        debugger.BreakpointText = "zz";
        debugger.AddBreakpointCommand.Execute(null);
        Assert.Contains("isn't an address", debugger.Status);
        debugger.ToggleBreakpoint(0x2C);
        Assert.Empty(chip.Breakpoints);
        Assert.Equal(0x34, FirmwareDebuggerViewModel.Parse("0x0035"));

        // Breaking at start's second instruction, the run pauses there.
        chip.Breakpoints.Add(0x30);
        Assert.True(vm.Simulation.Rebuild());
        var sim = vm.Simulation.Simulator!;
        while (!chip.Halted && sim.Time < 1e-3) sim.Step();
        Assert.True(chip.Halted);
        debugger.Refresh();
        Assert.StartsWith("Stopped at $0030 (breakpoint)", debugger.Status);
        Assert.Contains(debugger.Lines, l => l.IsPc && l.HasBreakpoint);
    }
}
