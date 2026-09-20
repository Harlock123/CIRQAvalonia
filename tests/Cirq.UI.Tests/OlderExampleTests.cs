using Cirq.Components.Digital;
using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The examples that predate the habit of shipping one with its own test. Each of these is now
/// described in the guide, and a described behaviour that nothing checks is a claim waiting to
/// quietly stop being true — so each assertion here is the sentence the guide makes about it.
/// </summary>
public class OlderExampleTests
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
    /// The point of the circuit: what you choose is the base <i>current</i>. Under a milliamp
    /// through the 4.7 kΩ moves fifteen times that through the LED, and the base sits at a diode
    /// drop rather than wherever the resistor would have put it.
    /// <para>
    /// The measurement is the LED's current rather than the collector's voltage, because a
    /// transistor that is off leaves the collector floating between its own leakage and the LED's,
    /// which settles well below the rail without the LED being lit by any useful definition.
    /// </para>
    /// </summary>
    [Fact]
    public void TheTransistorSwitchTradesALittleBaseCurrentForALotOfCollectorCurrent()
    {
        var dark = LedCurrent(pressed: false);
        Assert.True(dark < 0.1e-3, $"the LED drew {dark * 1e3:0.000} mA with the button out");

        using var vm = Load("Transistor Switch");

        var button = vm.Circuit.Components.OfType<PushButton>().Single();
        button.IsPressed = true;
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var q = vm.Circuit.Components.OfType<BipolarTransistor>().Single();
        var sim = vm.Simulation.Simulator!;
        sim.Run(3e-3);

        // Saturated: the collector is a few tens of millivolts up, not a diode drop below the rail.
        Assert.True(sim.NodeVoltage(q.Collector) < 0.3,
            $"the collector only reached {sim.NodeVoltage(q.Collector):0.00} V with the button in");

        // The base is clamped by its own junction, whatever the resistor would have divided down to.
        var baseVoltage = sim.NodeVoltage(q.Base);
        Assert.InRange(baseVoltage, 0.6, 0.95);

        var baseCurrent = (5.0 - baseVoltage) / 4.7e3;
        var collectorCurrent = LedCurrent(pressed: true);

        Assert.True(collectorCurrent > 10e-3, $"the LED only drew {collectorCurrent * 1e3:0.0} mA");
        Assert.True(collectorCurrent > 8 * baseCurrent,
            $"only {collectorCurrent / baseCurrent:0.0}x current gain, which is not a switch");
    }

    private static double LedCurrent(bool pressed)
    {
        using var vm = Load("Transistor Switch");

        vm.Circuit.Components.OfType<PushButton>().Single().IsPressed = pressed;
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var led = vm.Circuit.Components.OfType<Led>().Single();
        var sim = vm.Simulation.Simulator!;
        sim.Run(3e-3);

        // Through the 220 Ω limiter, which is the only path to the LED's anode.
        return (5.0 - sim.NodeVoltage(led.Anode)) / 220.0;
    }

    /// <summary>
    /// The flyback diode is the whole example. With it, the drain stops a diode drop above the
    /// rail; without it, the number the solver produces is not a voltage anybody's transistor is
    /// going to survive.
    /// </summary>
    [Fact]
    public void TheMosfetDriverClampsTheInductiveKickToADiodeDropAboveTheRail()
    {
        using var vm = Load("MOSFET Driver");

        var m = vm.Circuit.Components.OfType<Mosfet>().Single();
        var sim = vm.Simulation.Simulator!;

        var peak = 0.0;
        while (sim.Time < 6e-3)
        {
            sim.Step();
            peak = Math.Max(peak, sim.NodeVoltage(m.Drain));
        }

        Assert.InRange(peak, 12.0, 14.0);
    }

    /// <summary>And taking the diode out is the demonstration, so it had better be dramatic.</summary>
    [Fact]
    public void AndWithoutItTheDrainGoesSomewhereNoPartWouldSurvive()
    {
        using var vm = Load("MOSFET Driver");

        var m = vm.Circuit.Components.OfType<Mosfet>().Single();
        vm.Circuit.Remove(vm.Circuit.Components.OfType<Diode>().Single());
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var sim = vm.Simulation.Simulator!;

        var peak = 0.0;
        while (sim.Time < 6e-3)
        {
            sim.Step();
            peak = Math.Max(peak, sim.NodeVoltage(m.Drain));
        }

        Assert.True(peak > 1e4, $"the unclamped drain only reached {peak:0} V");
    }

    /// <summary>
    /// An open-collector output pulls down and nothing else, so the pull-up is not an accessory.
    /// </summary>
    [Fact]
    public void TheComparatorTriggerSquaresTheSineBetweenTheRails()
    {
        using var vm = Load("Comparator Trigger");

        var u = vm.Circuit.Components.OfType<Comparator>().Single();
        var sim = vm.Simulation.Simulator!;

        var (low, high) = SwingOf(sim, u.Output, 3e-3);

        Assert.True(high > 4.5, $"the output only reached {high:0.00} V");
        Assert.True(low < 0.5, $"the output only fell to {low:0.00} V");
    }

    /// <summary>Remove the pull-up and it never goes high at all, because nothing drives it there.</summary>
    [Fact]
    public void AndWithoutThePullUpItNeverGoesHighAtAll()
    {
        using var vm = Load("Comparator Trigger");

        var u = vm.Circuit.Components.OfType<Comparator>().Single();
        var pullUp = vm.Circuit.Components.OfType<Resistor>()
            .Single(r => Math.Abs(r.Resistance - 4.7e3) < 1.0);

        vm.Circuit.Remove(pullUp);
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var (_, high) = SwingOf(vm.Simulation.Simulator!, u.Output, 3e-3);

        Assert.True(high < 0.5, $"the output reached {high:0.00} V with nothing pulling it up");
    }

    /// <summary>
    /// Two open-collector outputs on one node: either comparator pulling is enough, so the node is
    /// high only while the signal is inside the window its ladder sets.
    /// </summary>
    [Fact]
    public void TheWindowDetectorIsHighOnlyBetweenItsTwoLimits()
    {
        using var vm = Load("Window Detector");

        var ic = vm.Circuit.Components.OfType<QuadComparator>().Single();
        var signal = vm.Circuit.Components.OfType<FunctionGenerator>().Single();
        var sim = vm.Simulation.Simulator!;

        var (_, _, output) = ic.Channel(0);
        var insideAndLow = 0;
        var outsideAndHigh = 0;

        while (sim.Time < 20e-3)
        {
            sim.Step();

            var v = sim.NodeVoltage(signal.Output);
            var flagged = sim.NodeVoltage(output) > 2.5;

            // A margin round each limit, so a sample taken mid-transition is not counted either way.
            if (v is > 1.7 and < 3.3 && !flagged) insideAndLow++;
            if ((v < 1.3 || v > 3.7) && flagged) outsideAndHigh++;
        }

        Assert.Equal(0, insideAndLow);
        Assert.Equal(0, outsideAndHigh);
    }

    /// <summary>
    /// Self-bias, which is the thing a depletion device makes possible: the gate is at ground
    /// through a megohm and the source resistor lifts the source above it, so V_GS is negative
    /// with no second supply and no divider anywhere.
    /// </summary>
    [Fact]
    public void TheJfetAmplifierSelfBiasesToANegativeGateSourceVoltage()
    {
        using var vm = Load("JFET Amplifier");

        var j = vm.Circuit.Components.OfType<JunctionFet>().Single();
        var sim = vm.Simulation.Simulator!;

        sim.Run(5e-3);

        var gateToSource = sim.NodeVoltage(j.Gate) - sim.NodeVoltage(j.Source);
        Assert.True(gateToSource < -0.2, $"V_GS came out at {gateToSource:0.000} V, which is not biased");

        // The gate is a reverse-biased junction, so it holds its ground reference without taking
        // current to do it: a megohm dropping nothing worth measuring.
        Assert.True(Math.Abs(sim.NodeVoltage(j.Gate)) < 0.05);
    }

    /// <summary>
    /// And the bypass capacitor is what keeps the gain. Without it the source follows the gate and
    /// most of the signal never lands across the junction at all.
    /// </summary>
    [Fact]
    public void AndTheSourceBypassIsWorthAFactorOfThreeInGain()
    {
        var bypassed = JfetGain(removeBypass: false);
        var degenerated = JfetGain(removeBypass: true);

        Assert.True(bypassed > 6.0, $"the bypassed stage only managed a gain of {bypassed:0.0}");
        Assert.InRange(bypassed / degenerated, 2.5, 4.0);
    }

    private static double JfetGain(bool removeBypass)
    {
        using var vm = Load("JFET Amplifier");

        var j = vm.Circuit.Components.OfType<JunctionFet>().Single();

        if (removeBypass)
        {
            vm.Circuit.Remove(vm.Circuit.Components.OfType<Capacitor>()
                .Single(c => c.Capacitance > 1e-6));
            Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);
        }

        var sim = vm.Simulation.Simulator!;

        // Past the coupling capacitor's settling, so the measurement is of the stage and not of
        // the bias still arriving.
        sim.Run(5e-3);

        var (inLow, inHigh) = (double.MaxValue, double.MinValue);
        var (outLow, outHigh) = (double.MaxValue, double.MinValue);

        while (sim.Time < 8e-3)
        {
            sim.Step();

            inLow = Math.Min(inLow, sim.NodeVoltage(j.Gate));
            inHigh = Math.Max(inHigh, sim.NodeVoltage(j.Gate));
            outLow = Math.Min(outLow, sim.NodeVoltage(j.Drain));
            outHigh = Math.Max(outHigh, sim.NodeVoltage(j.Drain));
        }

        return (outHigh - outLow) / (inHigh - inLow);
    }

    /// <summary>
    /// Half-wave: the capacitor is refilled once per cycle rather than twice, so the ripple is at
    /// the line frequency. That is the whole reason nobody builds one.
    /// </summary>
    [Fact]
    public void TheHalfWaveRectifierRefillsOncePerCycle()
    {
        using var vm = Load("Half-Wave Rectifier");

        var diode = vm.Circuit.Components.OfType<Diode>().Single();
        var sim = vm.Simulation.Simulator!;

        // Past the first few cycles, so the reservoir is charged and the ripple is steady.
        sim.Run(80e-3);

        List<double> troughs = [];
        var previous = sim.NodeVoltage(diode.Cathode);
        var falling = true;

        while (sim.Time < 180e-3)
        {
            sim.Step();

            var v = sim.NodeVoltage(diode.Cathode);
            if (falling && v > previous) { troughs.Add(sim.Time); falling = false; }
            if (v < previous) falling = true;

            previous = v;
        }

        // One refill per 50 Hz cycle, so the troughs are twenty milliseconds apart.
        Assert.True(troughs.Count >= 4, $"only found {troughs.Count} refills");

        var period = (troughs[^1] - troughs[0]) / (troughs.Count - 1);
        Assert.Equal(20e-3, period, 1e-3);

        // And the rail rides one forward drop below the 12 V peak of the 24 V peak-to-peak
        // secondary, with the ripple of a reservoir that is only topped up every 20 ms.
        var peak = double.MinValue;
        var trough = double.MaxValue;

        while (sim.Time < 220e-3)
        {
            sim.Step();

            var v = sim.NodeVoltage(diode.Cathode);
            peak = Math.Max(peak, v);
            trough = Math.Min(trough, v);
        }

        Assert.InRange(peak, 10.8, 11.8);
        Assert.InRange(peak - trough, 1.2, 2.5);
    }

    /// <summary>
    /// A thyristor remembers. The gate fires it and then has no further say, so the lamp stays lit
    /// with the button released.
    /// </summary>
    [Fact]
    public void TheScrLatchStaysOnAfterTheButtonIsReleased()
    {
        using var vm = Load("SCR Latch");

        var scr = vm.Circuit.Components.OfType<SiliconControlledRectifier>().Single();
        var button = vm.Circuit.Components.OfType<PushButton>().Single();
        var sim = vm.Simulation.Simulator!;

        sim.Run(20e-3);
        Assert.False(scr.IsLatched);

        button.IsPressed = true;
        sim.Run(20e-3);
        Assert.True(scr.IsLatched);

        button.IsPressed = false;
        sim.Run(50e-3);

        Assert.True(scr.IsLatched, "letting go of the gate turned it off, which is not what an SCR does");
    }

    /// <summary>
    /// And the only way out is the anode circuit: open it, the current falls below the holding
    /// current, and it drops out — then stays out when the supply comes back.
    /// </summary>
    [Fact]
    public void AndOnlyInterruptingTheAnodeTurnsItOff()
    {
        using var vm = Load("SCR Latch");

        var scr = vm.Circuit.Components.OfType<SiliconControlledRectifier>().Single();
        var button = vm.Circuit.Components.OfType<PushButton>().Single();
        var interrupter = vm.Circuit.Components.OfType<ToggleSwitch>().Single();
        var sim = vm.Simulation.Simulator!;

        button.IsPressed = true;
        sim.Run(20e-3);
        button.IsPressed = false;
        sim.Run(20e-3);
        Assert.True(scr.IsLatched);

        interrupter.IsClosed = false;
        sim.Run(20e-3);
        Assert.False(scr.IsLatched);

        interrupter.IsClosed = true;
        sim.Run(50e-3);

        Assert.False(scr.IsLatched, "it re-fired on its own with the gate doing nothing");
    }

    /// <summary>
    /// Three counter stages address eight taps of an equal ladder, so the common pin walks up in
    /// eight even steps and drops back. A D/A converter out of a counter and some resistors.
    /// </summary>
    [Fact]
    public void TheStaircaseGeneratorClimbsInEightEvenSteps()
    {
        using var vm = Load("Staircase Generator");

        var mux = vm.Circuit.Components.OfType<Ic4051>().Single();
        var sim = vm.Simulation.Simulator!;

        List<double> levels = [];
        var last = double.NaN;

        while (sim.Time < 25e-3)
        {
            sim.Step();

            var v = sim.NodeVoltage(mux.Common);
            if (double.IsNaN(last) || Math.Abs(v - last) > 0.05) { levels.Add(v); last = v; }
        }

        // One full ramp, and then it starts again from the bottom.
        Assert.True(levels.Count >= 9, $"only {levels.Count} levels in the window");

        var ramp = levels.Take(8).ToList();

        for (var i = 1; i < ramp.Count; i++)
            Assert.Equal(0.61, ramp[i] - ramp[i - 1], 0.06);

        Assert.Equal(ramp[0], levels[8], 0.06);
    }

    /// <summary>
    /// The 4060's frequency comes out of the network on the canvas rather than out of a property,
    /// so more than doubling the timing capacitor more than doubles the period.
    /// </summary>
    [Fact]
    public void TheFourZeroSixZeroTimerSlowsInProportionToItsCapacitor()
    {
        var atHundred = Q4Period(100e-9);
        var atTwoTwenty = Q4Period(220e-9);

        Assert.Equal(2.2, atTwoTwenty / atHundred, 0.25);
    }

    private static double Q4Period(double timingCapacitance)
    {
        using var vm = Load("4060 Timer");

        var ct = vm.Circuit.Components.OfType<Capacitor>().Single();
        ct.Capacitance = timingCapacitance;
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var counter = vm.Circuit.Components.OfType<Ic4060>().Single();
        var sim = vm.Simulation.Simulator!;

        var edges = 0;
        var wasHigh = false;
        var first = double.NaN;
        var lastEdge = double.NaN;

        while (sim.Time < 400e-3)
        {
            sim.Step();

            var high = sim.NodeVoltage(counter.Outputs[0]) > 2.5;
            if (high && !wasHigh)
            {
                if (double.IsNaN(first)) first = sim.Time;
                lastEdge = sim.Time;
                edges++;
            }

            wasHigh = high;
        }

        Assert.True(edges >= 3, $"only {edges} edges on Q4 at {timingCapacitance * 1e9:0} nF");

        return (lastEdge - first) / (edges - 1);
    }

    private static (double Low, double High) SwingOf(
        Cirq.Engine.Simulation.CircuitSimulator sim, Cirq.Core.Topology.Terminal terminal, double until)
    {
        var low = double.MaxValue;
        var high = double.MinValue;

        while (sim.Time < until)
        {
            sim.Step();

            var v = sim.NodeVoltage(terminal);
            low = Math.Min(low, v);
            high = Math.Max(high, v);
        }

        return (low, high);
    }
}
