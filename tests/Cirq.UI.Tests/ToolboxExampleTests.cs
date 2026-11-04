using Cirq.Components.Boards;
using Cirq.Components.Digital;
using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Core.Probing;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The examples built for the 0.41 parts — the lamp, the logic blocks, the Darlington, the
/// analog thermometers, the pixel strip, the keypad, the two new boards and the adder and
/// up/down counters. Each is run the way the menu runs it and held to what it is there to show.
/// </summary>
public class ToolboxExampleTests
{
    private static MainWindowViewModel Load(string name)
    {
        var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        vm.Scope.ClearProbes();
        Examples.All.Single(e => e.Name == name).Build(vm);
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);
        return vm;
    }

    /// <summary>
    /// The editor starts a run at switch-on, so the first few milliseconds are the inrush: about
    /// ten times the current the lamp settles at.
    /// </summary>
    [Fact]
    public void TheLampDrawsItsInrushAndSettles()
    {
        using var vm = Load("Lamp Inrush");
        var sim = vm.Simulation.Simulator!;
        var lamp = vm.Circuit.Components.OfType<IncandescentLamp>().Single();

        var peak = 0.0;
        while (sim.Time < 5e-3)
        {
            sim.Step();
            peak = Math.Max(peak, lamp.Current);
        }

        sim.Run(0.25);

        Assert.True(peak > 3.5, $"the inrush peaked at only {peak:0.00} A");
        Assert.Equal(5.0 / 12.0, lamp.Current, 0.02);
        Assert.Contains(vm.Scope.Probes, p => p.Kind is ProbeKind.Current);
    }

    /// <summary>Eleven clock periods in, the four stages read eleven.</summary>
    [Fact]
    public void TheRippleCounterCountsTheClockInBinary()
    {
        using var vm = Load("Ripple Counter");
        var sim = vm.Simulation.Simulator!;
        var stages = vm.Circuit.Components.OfType<TFlipFlop>().OrderBy(s => s.X).ToArray();

        // The clock's first rising edge is at the start, and the counter counts falling ones of Q'.
        sim.Run(11.25e-3);

        var count = stages.Select((s, i) => s.State == Cirq.Core.Digital.LogicState.High ? 1 << i : 0).Sum();

        Assert.Equal(11, count);
        Assert.Contains(vm.Circuit.Components.OfType<Led>(), l => l.Brightness > 0.3);
    }

    [Fact]
    public void TheTriStateBusIsHandedFromOneBufferToTheOther()
    {
        using var vm = Load("Tri-State Bus");
        var sim = vm.Simulation.Simulator!;
        var buffers = vm.Circuit.Components.OfType<TriStateBuffer>().OrderBy(b => b.Y).ToArray();

        // Step 4 of six: B has it, and B's input is a steady high.
        sim.Run(3.5e-3);
        Assert.False(buffers[0].IsEnabled);
        Assert.True(buffers[1].IsEnabled);
        Assert.True(sim.NodeVoltage(buffers[1].Out) > 3.0);

        // Step 6: nobody has it, and the pull-down does.
        sim.Run(2e-3);
        Assert.False(buffers[0].IsEnabled || buffers[1].IsEnabled);
        Assert.True(sim.NodeVoltage(buffers[1].Out) < 0.1);
        Assert.DoesNotContain(buffers, b => b.IsContending);
    }

    /// <summary>
    /// The board's PWM through the Darlington: the collector sits about a volt up in every
    /// on-pulse, and the filament averages the pulses into a steady, partial glow.
    /// </summary>
    [Fact]
    public void TheDarlingtonDimsTheLampFromAPwmPin()
    {
        using var vm = Load("Darlington Lamp Dimmer");
        var sim = vm.Simulation.Simulator!;
        var q = vm.Circuit.Components.OfType<DarlingtonTransistor>().Single();
        var lamp = vm.Circuit.Components.OfType<IncandescentLamp>().Single();

        sim.Run(0.2);

        var lowest = double.MaxValue;
        var dimmest = double.MaxValue;
        var brightest = 0.0;
        var end = sim.Time + 4e-3;

        while (sim.Time < end)
        {
            sim.Step();
            lowest = Math.Min(lowest, q.Vce);
            dimmest = Math.Min(dimmest, lamp.Brightness);
            brightest = Math.Max(brightest, lamp.Brightness);
        }

        Assert.InRange(lowest, 0.6, 1.5);
        Assert.InRange(dimmest, 0.05, 0.9);
        Assert.True(brightest - dimmest < 0.15, $"the glow swung from {dimmest:0.00} to {brightest:0.00}");
        Assert.Empty(vm.Circuit.Components.OfType<DeveloperBoard>().Single().Violations);
    }

    /// <summary>The board reads 0.25 V on A0 and 0.75 V on A1 at 25 °C, and a warmed LM35 reads more.</summary>
    [Fact]
    public void TheArduinoReadsBothThermometers()
    {
        using var vm = Load("Analog Thermometer");
        var sim = vm.Simulation.Simulator!;
        var board = vm.Circuit.Components.OfType<DeveloperBoard>().Single();
        var lm35 = vm.Circuit.Components.OfType<TemperatureSensorIc>().Single(s => s.Model == TemperatureSensorModel.Lm35);

        sim.Run(1e-3);
        Assert.Contains("A0=0.25V", board.InputSummary);
        Assert.Contains("A1=0.75V", board.InputSummary);

        lm35.Interact();
        sim.Run(1e-3);
        Assert.Contains("A0=0.36V", board.InputSummary);
    }

    /// <summary>
    /// A frame every 290 µs: by the time eight have been sent the head has run the length of the
    /// strip, with its tail behind it.
    /// </summary>
    [Fact]
    public void TheNeoPixelChaserRunsItsHeadAlongTheStrip()
    {
        using var vm = Load("NeoPixel Chaser");
        var sim = vm.Simulation.Simulator!;
        var strip = vm.Circuit.Components.OfType<NeoPixelStrip>().Single();

        sim.Run(0.35e-3);
        Assert.Equal(1, strip.FramesShown);
        Assert.Equal(255, Brightest(strip.Pixels[0]));

        sim.Run(2.0e-3);
        Assert.Equal(8, strip.FramesShown);
        Assert.Equal(255, Brightest(strip.Pixels[7]));
        Assert.Equal(63, Brightest(strip.Pixels[6]));
        Assert.Equal(0, Brightest(strip.Pixels[0]));

        static int Brightest(Cirq.Core.Primitives.Color c) => Math.Max(c.R, Math.Max(c.G, c.B));
    }
}
