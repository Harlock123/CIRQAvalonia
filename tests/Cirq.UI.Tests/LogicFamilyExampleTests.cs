using Cirq.Components.Digital;
using Cirq.Components.Nonlinear;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The examples for the 0.43 logic families, each run and asserted against what it exists to show.
/// </summary>
public class LogicFamilyExampleTests
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

    /// <summary>The toggle driving the net a label names.</summary>
    private static LogicToggle Toggle(MainWindowViewModel vm, string label)
    {
        var netlist = vm.Circuit.BuildNetlist();
        var labels = vm.Circuit.Components.OfType<Cirq.Components.Sources.NetLabel>().Where(l => l.NetName == label).ToList();
        return vm.Circuit.Components.OfType<LogicToggle>()
            .Single(t => labels.Any(l => netlist.IndexOf(l.Pin) == netlist.IndexOf(t.Out)));
    }

    /// <summary>Nine plus five is fourteen on the LEDs; the same toggles in logic mode give A XNOR B.</summary>
    [Fact]
    public void TheAluAddsAndThenComputesALogicFunction()
    {
        using var vm = Load("4-Bit ALU");
        var alu = vm.Circuit.Components.OfType<Ic74181>().Single();
        var sim = vm.Simulation.Simulator!;

        sim.Run(1e-3);
        Assert.Equal(14, alu.Result);
        var lit = vm.Circuit.Components.OfType<Led>().Where(l => l.Brightness > 0.3).Count();
        Assert.Equal(4, lit);   // F1, F2, F3 and Cn+4 (no carry out)

        Toggle(vm, "M").State = true;
        sim.Run(1e-3);
        Assert.Equal(~(9 ^ 5) & 0xF, alu.Result);
    }

    /// <summary>Nine against five lights A&gt;B alone; make B nine too and A=B lights instead.</summary>
    [Fact]
    public void TheComparatorLightsOneOfThree()
    {
        using var vm = Load("Magnitude Comparator");
        var comparator = vm.Circuit.Components.OfType<Ic7485>().Single();
        var sim = vm.Simulation.Simulator!;

        sim.Run(1e-3);
        Assert.Equal("A>B", comparator.Verdict);
        Assert.Single(vm.Circuit.Components.OfType<Led>(), l => l.Brightness > 0.3);

        Toggle(vm, "B3").State = true;
        Toggle(vm, "B2").State = false;
        sim.Run(1e-3);
        Assert.Equal("A=B", comparator.Verdict);
    }

    /// <summary>
    /// A lone 100 µs trigger comes out a millisecond long; the burst 300 µs apart holds Q high from its
    /// first pulse to a millisecond after its last.
    /// </summary>
    [Fact]
    public void ThePulseStretcherMakesEachLonePulseAMillisecondAndHoldsThroughTheBurst()
    {
        using var vm = Load("Pulse Stretcher");
        var oneShot = vm.Circuit.Components.OfType<Ic4538>().Single();
        var sim = vm.Simulation.Simulator!;

        // Sample Q every 10 µs through one cycle of the pattern.
        var high = new List<bool>();
        while (sim.Time < 9.5e-3)
        {
            sim.Run(10e-6);
            high.Add(oneShot.IsRunning(0));
        }

        var runs = new List<int>();
        var length = 0;
        foreach (var h in high)
        {
            if (h) length++;
            else if (length > 0) { runs.Add(length); length = 0; }
        }

        // Three pulses: a millisecond, the burst (first at 4.0 ms, last at 5.2 ms, so 2.2 ms), a millisecond.
        Assert.Equal(3, runs.Count);
        Assert.InRange(runs[0] * 10e-6, 0.95e-3, 1.05e-3);
        Assert.InRange(runs[1] * 10e-6, 2.1e-3, 2.3e-3);
        Assert.InRange(runs[2] * 10e-6, 0.95e-3, 1.05e-3);
    }

    /// <summary>Thirty-seven milliseconds of a 1 kHz clock, and the two displays read 3 and 7.</summary>
    [Fact]
    public void TheTwoDigitCounterShowsItsCount()
    {
        using var vm = Load("Two-Digit Counter");
        var counters = vm.Circuit.Components.OfType<Ic4026>().OrderBy(c => c.X).ToList();
        var displays = vm.Circuit.Components.OfType<SevenSegmentDisplay>().OrderBy(d => d.X).ToList();
        var sim = vm.Simulation.Simulator!;

        sim.Run(37.2e-3);

        var count = (10 * counters[0].Count) + counters[1].Count;
        Assert.InRange(count, 36, 38);
        Assert.Equal(count / 10, displays[0].DisplayedDigit);
        Assert.Equal(count % 10, displays[1].DisplayedDigit);
    }

    /// <summary>Each byte appears on the LEDs only once its strobe has come, and they hold it while the next shifts in.</summary>
    [Fact]
    public void TheSerialDisplayShowsEachByteOnItsStrobe()
    {
        using var vm = Load("Serial Display");
        var register = vm.Circuit.Components.OfType<Ic4094>().Single();
        var sim = vm.Simulation.Simulator!;

        // One byte is 26 steps at 10 kHz, 2.6 ms, its strobe at 1.7 ms. Part way through the second
        // byte the first is showing; past the second's strobe, the second.
        sim.Run(3.8e-3);
        Assert.Equal(0b1000_0001, register.Latched);

        sim.Run(1.4e-3);
        Assert.Equal(0b0100_0010, register.Latched);

        var lit = vm.Circuit.Components.OfType<Led>().Count(l => l.Brightness > 0.3);
        Assert.Equal(2, lit);
    }

    /// <summary>Address 0 passes source 1 to both outputs; A high passes source 2, both channels at once.</summary>
    [Fact]
    public void TheStereoSelectorSwitchesBothChannelsTogether()
    {
        using var vm = Load("Stereo Selector");
        var mux = vm.Circuit.Components.OfType<Ic4052>().Single();
        var generators = vm.Circuit.Components.OfType<Cirq.Components.Sources.FunctionGenerator>().ToList();
        var sim = vm.Simulation.Simulator!;

        double Worst(int section, double frequency)
        {
            var source = generators.Single(g => Math.Abs(g.Frequency - frequency) < 1e-9);
            var worst = 0.0;
            var until = sim.Time + 2e-3;
            while (sim.Time < until)
            {
                sim.Step();
                worst = Math.Max(worst, Math.Abs(sim.NodeVoltage(mux.CommonOf(section)) - sim.NodeVoltage(source.Output)));
            }

            return worst;
        }

        sim.Run(1e-3);
        Assert.True(Worst(0, 500) < 0.01);
        Assert.True(Worst(1, 750) < 0.01);

        Toggle(vm, "A").State = true;
        sim.Run(1e-3);
        Assert.True(Worst(0, 2e3) < 0.01);
        Assert.True(Worst(1, 3e3) < 0.01);
    }
}
