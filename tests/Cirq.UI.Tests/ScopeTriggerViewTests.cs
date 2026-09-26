using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The trigger as the scope uses it: which part of the recording ends up on screen.
/// <para>
/// The arithmetic of finding an edge is tested in Cirq.Engine.Tests.ScopeTriggerTests. What is
/// tested here is the decision the display makes with it — that the same feature lands in the same
/// place every repaint, that a mode with nothing to show holds rather than lying, and that a single
/// shot is a photograph and stays one.
/// </para>
/// </summary>
public class ScopeTriggerViewTests
{
    /// <summary>A scope with one probe carrying a kilohertz sine, recorded up to <paramref name="until"/>.</summary>
    private static (ScopeViewModel Scope, SignalProbe Probe) Sine(double until, double amplitude = 1.0)
    {
        var circuit = new Circuit();
        var scope = new ScopeViewModel(circuit);
        var probe = new SignalProbe { Label = "In" };

        for (var t = 0.0; t <= until; t += 1e-6)
            probe.Record(t, amplitude * Math.Sin(2 * Math.PI * 1e3 * t));

        scope.Probes.Add(probe);

        // One millisecond across the face: exactly one cycle of the signal.
        scope.TimebasePerDivision = 1e-3 / ScopeViewModel.HorizontalDivisions;

        return (scope, probe);
    }

    /// <summary>More of the same sine, so a single shot has something to fire on after it is armed.</summary>
    private static void RecordOn(SignalProbe probe, double from, double until, double amplitude = 1.0)
    {
        for (var t = from; t <= until; t += 1e-6)
            probe.Record(t, amplitude * Math.Sin(2 * Math.PI * 1e3 * t));
    }

    [Fact]
    public void WithoutATriggerTheDisplayFollowsTheNewestSamples()
    {
        var (scope, _) = Sine(5e-3);

        Assert.Equal(TriggerMode.Off, scope.TriggerMode);
        Assert.Equal(5e-3 - scope.WindowSeconds, scope.WindowStart(5e-3), 1e-12);
        Assert.Null(scope.TriggeredAt);
    }

    /// <summary>
    /// The whole point: the window start does not depend on where the recording happens to end. Two
    /// repaints a fraction of a cycle apart put the same edge in the same place, which is what makes
    /// a waveform stand still.
    /// </summary>
    [Fact]
    public void ATriggeredDisplayStandsStill()
    {
        var (scope, _) = Sine(6e-3);

        scope.TriggerMode = TriggerMode.Auto;
        scope.TriggerLevel = 0;
        scope.TriggerSlope = TriggerSlope.Rising;

        var first = scope.WindowStart(5.0e-3);
        var offsetInWindow = (scope.TriggeredAt ?? 0) - first;

        var second = scope.WindowStart(6.0e-3);
        var offsetAgain = (scope.TriggeredAt ?? 0) - second;

        // Different edges — the second repaint has more signal to work with — but each one sits a
        // fifth of the way across the screen, which is where the trigger is.
        Assert.NotEqual(first, second);
        Assert.Equal(offsetInWindow, offsetAgain, 1e-9);
        Assert.Equal(scope.WindowSeconds * scope.TriggerPosition, offsetInWindow, 1e-9);
    }

    [Fact]
    public void TheEdgeIsOneTheWholeWindowHasSamplesFor()
    {
        var (scope, _) = Sine(6e-3);

        scope.TriggerMode = TriggerMode.Auto;

        scope.WindowStart(5e-3);

        // A millisecond window with the trigger a fifth in needs 0.8 ms of signal after the edge, so
        // the newest usable upward zero crossing is the one at 4 ms rather than the one at 5.
        Assert.Equal(4e-3, scope.TriggeredAt!.Value, 1e-5);
    }

    [Fact]
    public void NormalHoldsItsLastCaptureWhenNothingTriggers()
    {
        var (scope, _) = Sine(6e-3);

        scope.TriggerMode = TriggerMode.Normal;
        scope.TriggerLevel = 0;

        var held = scope.WindowStart(5e-3);
        Assert.NotNull(scope.TriggeredAt);

        // Now ask for a level the signal never reaches.
        scope.TriggerLevel = 50;

        Assert.Equal(held, scope.WindowStart(5.5e-3), 1e-12);
        Assert.Null(scope.TriggeredAt);
        Assert.Equal("Waiting for an edge", scope.TriggerStatus);
    }

    [Fact]
    public void AutoFreeRunsWhenNothingTriggers()
    {
        var (scope, _) = Sine(6e-3);

        scope.TriggerMode = TriggerMode.Auto;
        scope.TriggerLevel = 50;

        Assert.Equal(5e-3 - scope.WindowSeconds, scope.WindowStart(5e-3), 1e-12);
        Assert.Contains("free running", scope.TriggerStatus);
    }

    [Fact]
    public void ASingleShotIsAPhotograph()
    {
        var (scope, probe) = Sine(3e-3);

        var stopped = 0;
        scope.SingleShotCaptured += (_, _) => stopped++;

        // Arming looks forward, not back: the edge it catches is one that has not happened yet, so
        // the run has to go on for there to be anything to catch.
        scope.TriggerMode = TriggerMode.Single;
        Assert.True(scope.IsSingleShot);
        Assert.Contains("Armed", scope.TriggerStatus);

        Assert.False(scope.HasCaptured);

        RecordOn(probe, 3e-3, 6e-3);

        var start = scope.WindowStart(6e-3);

        Assert.True(scope.HasCaptured);
        Assert.Equal(1, stopped);
        Assert.Contains("Captured at", scope.TriggerStatus);

        var caught = scope.TriggeredAt;

        // More signal arrives; the photograph does not move, and nothing fires twice.
        RecordOn(probe, 6e-3, 9e-3);

        Assert.Equal(start, scope.WindowStart(9e-3), 1e-12);
        Assert.Equal(caught, scope.TriggeredAt);
        Assert.Equal(1, stopped);
    }

    [Fact]
    public void ArmingAgainLooksForAFreshEdge()
    {
        var (scope, probe) = Sine(3e-3);

        scope.TriggerMode = TriggerMode.Single;
        RecordOn(probe, 3e-3, 6e-3);
        scope.WindowStart(6e-3);

        var first = scope.TriggeredAt;
        Assert.NotNull(first);

        scope.ArmCommand.Execute(null);

        Assert.False(scope.HasCaptured);
        Assert.Null(scope.TriggeredAt);

        RecordOn(probe, 6e-3, 9e-3);
        scope.WindowStart(9e-3);

        // Armed at the newest sample, so the edge it caught is a later one than before.
        Assert.True(scope.TriggeredAt > first, $"{scope.TriggeredAt} should be after {first}");
    }

    [Fact]
    public void TurningTheTriggerOffLetsTheDisplayRunAgain()
    {
        var (scope, probe) = Sine(3e-3);

        scope.TriggerMode = TriggerMode.Single;
        RecordOn(probe, 3e-3, 6e-3);
        scope.WindowStart(6e-3);
        Assert.True(scope.HasCaptured);

        scope.TriggerMode = TriggerMode.Off;

        Assert.False(scope.HasCaptured);
        Assert.False(scope.IsTriggering);
        Assert.Equal(6e-3 - scope.WindowSeconds, scope.WindowStart(6e-3), 1e-12);
    }
}
