using Avalonia.Controls;
using Avalonia.Threading;
using Cirq.Mcp;
using Cirq.UI.Services;
using Cirq.UI.Tests.Support;
using Cirq.UI.ViewModels;
using Cirq.UI.Views;

namespace Cirq.UI.Tests;

/// <summary>
/// An agent pressing Run on a circuit it built: the live run goes at a pace its clock can be seen at, not the window's
/// thousandth of real time, under which a few-hertz clock sat flat until someone changed the speed by hand.
/// </summary>
[Collection(WindowCollection.Name)]
public class TransportPaceTests(WindowSession session) : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("cirq-pace-").FullName;
    private readonly List<Window> _windows = [];

    public void Dispose()
    {
        session.Run(() =>
        {
            foreach (var window in _windows) window.Close();
        });
        try { Directory.Delete(_folder, true); }
        catch (IOException) { }
    }

    private static T Finish<T>(Task<T> task)
    {
        for (var i = 0; i < 30000 && !task.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }

        Assert.True(task.IsCompleted, "It didn't finish.");
        return task.GetAwaiter().GetResult();
    }

    private (MainWindowViewModel Editor, CircuitTools Tools) Open()
    {
        var app = (App)Avalonia.Application.Current!;
        var editor = new MainWindowViewModel { Autosave = new AutosaveStore(Path.Combine(_folder, $"recovery-{_windows.Count}.cirq")) };
        var window = new MainWindow { DataContext = editor, Width = 1400, Height = 900 };
        window.Show();
        _windows.Add(window);
        Dispatcher.UIThread.RunJobs();
        return (editor, new CircuitTools(app.Agents.ChatHost(editor, _folder)));
    }

    private static Dictionary<string, System.Text.Json.JsonElement> Settings(object o) =>
        System.Text.Json.JsonSerializer.SerializeToElement(o).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    [Fact]
    public void An_agent_running_a_slow_clock_gets_a_pace_and_timebase_it_can_be_seen_at() => session.Run(() =>
    {
        var (editor, tools) = Open();
        Finish(tools.NewCircuit(discard_changes: true));
        Finish(tools.AddParts([new() { Kind = "Clock", X = 0, Y = 0, Settings = Settings(new { Frequency = "10" }) }, new() { Kind = "Ground", X = 0, Y = 100 }]));
        Finish(tools.AddProbe("CLK1.CLK", "Clock"));
        Assert.Equal(1e-3, editor.Simulation.SpeedFactor);

        var answer = Finish(tools.Transport("run"));
        Assert.Contains("Running", answer);
        Assert.Contains("CLK1 at 10Hz can be seen", answer);
        Assert.Equal(0.2, editor.Simulation.SpeedFactor, 9);
        Assert.True(editor.Scope.TimebasePerDivision * 10 >= 0.2, $"{editor.Scope.TimebasePerDivision}");

        // A second of real time is a fifth of a second of the circuit's: two cycles of the clock, both levels seen.
        var until = DateTime.UtcNow.AddSeconds(1.5);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Finish(tools.Transport("pause"));
        var values = editor.Circuit.Probes[0].HistoryBuffer.ToArray().Select(p => p.Value).ToList();
        Assert.True(values.Max() - values.Min() > 1, $"the clock stayed at {values.Min()}–{values.Max()} V; t = {editor.Simulation.SimulationTime}");

        // A speed asked for is the speed it runs at, and the menu follows.
        Assert.Contains("Speed max speed", Finish(tools.Transport("run", System.Text.Json.JsonSerializer.SerializeToElement("max"))));
        Assert.True(editor.Simulation.IsMaximumThroughput);
        Finish(tools.Transport("pause"));
        Assert.Contains("Speed 1/100 speed", Finish(tools.Transport("run", System.Text.Json.JsonSerializer.SerializeToElement(0.01))));
        Assert.Equal(0.01, editor.Simulation.SpeedFactor);
        Finish(tools.Transport("pause"));
    });

    [Fact]
    public void A_circuit_with_nothing_setting_a_pace_runs_at_the_speed_it_was_left_at() => session.Run(() =>
    {
        var (editor, tools) = Open();

        // The window's own RC low-pass is driven by a 1 kHz generator: a thousandth of real time already shows it.
        var answer = Finish(tools.Transport("run"));
        Finish(tools.Transport("pause"));
        Assert.DoesNotContain("Speed set", answer);
        Assert.Equal(1e-3, editor.Simulation.SpeedFactor);
    });
}
