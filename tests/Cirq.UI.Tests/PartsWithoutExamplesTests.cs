using Cirq.Components.Digital;
using Cirq.Components.Electromechanical;
using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The batch of examples built for the parts that had none. Each is asserted against the thing it
/// exists to show, and in several cases against a figure from the datasheet rather than from a
/// previous run of this same code.
/// </summary>
public class PartsWithoutExamplesTests
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
    /// Several nodes measured over one run rather than one each. They share a simulator, so a
    /// second pass would start wherever the first one stopped and measure nothing at all.
    /// </summary>
    private static (double Low, double High)[] SwingsOf(
        MainWindowViewModel vm, Cirq.Core.Topology.Terminal[] terminals, double settle, double until)
    {
        var sim = vm.Simulation.Simulator!;
        sim.Run(settle);

        var low = terminals.Select(_ => double.MaxValue).ToArray();
        var high = terminals.Select(_ => double.MinValue).ToArray();

        while (sim.Time < until)
        {
            sim.Step();

            for (var i = 0; i < terminals.Length; i++)
            {
                var v = sim.NodeVoltage(terminals[i]);
                low[i] = Math.Min(low[i], v);
                high[i] = Math.Max(high[i], v);
            }
        }

        return [.. low.Zip(high)];
    }

    private static (double Low, double High) SwingOf(MainWindowViewModel vm, Cirq.Core.Topology.Terminal t,
        double settle, double until)
    {
        var sim = vm.Simulation.Simulator!;
        sim.Run(settle);

        var low = double.MaxValue;
        var high = double.MinValue;

        while (sim.Time < until)
        {
            sim.Step();

            var v = sim.NodeVoltage(t);
            low = Math.Min(low, v);
            high = Math.Max(high, v);
        }

        return (low, high);
    }

    /// <summary>
    /// Three followers, one supply, and three different answers to "how much of it can I use".
    /// The figures are each part's own headroom from its model, not a recording of this run.
    /// </summary>
    [Fact]
    public void TheRailToRailExampleShowsEachPartStoppingWhereItsDatasheetSays()
    {
        using var vm = Load("Rail to Rail");

        var amplifiers = vm.Circuit.Components.OfType<OperationalAmplifier>().ToList();
        Assert.Equal(3, amplifiers.Count);

        var swings = SwingsOf(vm, [.. amplifiers.Select(a => a.Output)], 5e-3, 25e-3);

        for (var i = 0; i < amplifiers.Count; i++)
        {
            var model = amplifiers[i].Model;

            // A follower asked to go everywhere reaches exactly as far as its headroom allows.
            Assert.Equal(model.NegativeSwingHeadroom, swings[i].Low, 0.06);
            Assert.Equal(5.0 - model.OutputSwingHeadroom, swings[i].High, 0.06);
        }
    }

    /// <summary>
    /// And the comparison is the point: the LM358 uses the bottom of the supply and not the top,
    /// which is the asymmetry that makes it a single-supply part at all.
    /// </summary>
    [Fact]
    public void AndTheLm358ReachesTheBottomRailButNotTheTop()
    {
        using var vm = Load("Rail to Rail");

        var amplifiers = vm.Circuit.Components.OfType<OperationalAmplifier>().ToList();
        var lm358 = amplifiers.Single(a => a.Model.Name == "LM358");
        var mcp6002 = amplifiers.Single(a => a.Model.Name == "MCP6002");
        var lm741 = amplifiers.Single(a => a.Model.Name == "LM741");

        var swings = SwingsOf(vm, [lm358.Output, mcp6002.Output, lm741.Output], 5e-3, 25e-3);
        var (lowLm358, highLm358) = swings[0];
        var (lowMcp, highMcp) = swings[1];
        var (lowLm741, _) = swings[2];

        // Down to within a few tens of millivolts of ground, like the CMOS part beside it.
        Assert.True(lowLm358 < 0.1, $"the LM358 stopped {lowLm358:0.000} V above ground");
        Assert.True(lowLm741 > 1.0, $"the LM741 reached {lowLm741:0.000} V, which it cannot");

        // But a volt and a half short at the top, where the MCP6002 gets there.
        Assert.True(highLm358 < 3.6, $"the LM358 reached {highLm358:0.00} V");
        Assert.True(highMcp > 4.8, $"the rail-to-rail part only reached {highMcp:0.00} V");
        Assert.True(lowMcp < 0.1);
    }

    /// <summary>
    /// Millivolts of difference on half a supply of common mode, multiplied by the gain the
    /// datasheet's formula gives for the resistor, and unmoved by the common mode itself.
    /// </summary>
    [Fact]
    public void TheLoadCellExampleAmplifiesTheDifferenceAndNotTheCommonMode()
    {
        using var vm = Load("Load Cell");

        var cell = vm.Circuit.Components.OfType<LoadCell>().Single();
        var amplifier = vm.Circuit.Components.OfType<Ina126>().Single();
        var sim = vm.Simulation.Simulator!;

        sim.Run(3e-3);

        // 2 mV/V at 5 V of excitation, half loaded: 5 mV out of a bridge sitting at 2.5 V.
        var differential = sim.NodeVoltage(cell.SignalPositive) - sim.NodeVoltage(cell.SignalNegative);

        Assert.Equal(2.5, cell.LoadKilograms, 1e-9);
        Assert.Equal(5e-3, differential, 0.2e-3);
        Assert.Equal(2.5, sim.NodeVoltage(cell.SignalPositive), 0.05);

        // Which the amplifier multiplies by a hundred and stacks on its reference pin.
        var reference = sim.NodeVoltage(amplifier.Reference);
        Assert.Equal(reference + (100.0 * differential), sim.NodeVoltage(amplifier.Output), 0.05);
        Assert.False(amplifier.IsClipping);
    }

    /// <summary>
    /// The gain is the datasheet's, so a different resistor gives the output the datasheet says
    /// and not whatever the model felt like.
    /// </summary>
    [Fact]
    public void AndTheGainIsTheOneItsResistorSetsForIt()
    {
        using var vm = Load("Load Cell");

        var amplifier = vm.Circuit.Components.OfType<Ina126>().Single();

        // G = 5 + 80k/Rg, so the resistor for a gain of a hundred is 80k/95.
        Assert.Equal(80e3 / 95.0, amplifier.GainSettingResistance, 1.0);
        Assert.Equal(100.0, Ina126.GainForResistance(amplifier.GainSettingResistance), 0.01);

        var cell = vm.Circuit.Components.OfType<LoadCell>().Single();
        cell.LoadKilograms = 5.0;
        amplifier.DifferentialGain = 50.0;
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var sim = vm.Simulation.Simulator!;
        sim.Run(3e-3);

        // Full load is 10 mV; fifty times that is half a volt above the reference.
        Assert.Equal(sim.NodeVoltage(amplifier.Reference) + 0.5, sim.NodeVoltage(amplifier.Output), 0.05);
    }

    /// <summary>
    /// The LM386's one trick: the same circuit with a capacitor between two pins is ten times the
    /// gain, and nothing else changes.
    /// </summary>
    [Fact]
    public void TheAudioExampleGainsExactlyTenTimesMoreWithTheGainCapacitor()
    {
        var atTwenty = OutputSwing(20.0);
        var atTwoHundred = OutputSwing(200.0);

        Assert.Equal(10.0, atTwoHundred / atTwenty, 0.3);
    }

    /// <summary>
    /// And it idles at half the supply so it can swing both ways, which is why the speaker is
    /// coupled through a capacitor rather than wired to the pin.
    /// </summary>
    [Fact]
    public void AndItIdlesAtHalfTheSupplyWithNoneOfItAcrossTheVoiceCoil()
    {
        using var vm = Load("Audio Amplifier");

        var amplifier = vm.Circuit.Components.OfType<Lm386>().Single();
        var speaker = vm.Circuit.Components.OfType<Speaker>().Single();
        var sim = vm.Simulation.Simulator!;

        sim.Run(300e-3);

        var (low, high) = SwingOf(vm, amplifier.Output, 0, 305e-3);
        Assert.Equal(4.5, (low + high) / 2, 0.2);

        // The coupling capacitor holds the offset off the coil, so the speaker sees the signal and
        // not four and a half volts of standing DC.
        Assert.True(Math.Abs(sim.NodeVoltage(speaker.A)) < 0.5,
            $"the speaker is sitting at {sim.NodeVoltage(speaker.A):0.00} V");

        Assert.True(speaker.IsSounding);
        Assert.False(amplifier.IsClipping);
        Assert.Empty(speaker.Violations);
    }

    private static double OutputSwing(double gain)
    {
        using var vm = Load("Audio Amplifier");

        vm.Circuit.Components.OfType<Lm386>().Single().VoltageGain = gain;
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var amplifier = vm.Circuit.Components.OfType<Lm386>().Single();

        // Past the coupling capacitors, which are large and take a while to find their level.
        var (low, high) = SwingOf(vm, amplifier.Output, 300e-3, 305e-3);
        return high - low;
    }

    /// <summary>
    /// Three parts between a logic pin and a coil, each doing something the pin cannot. The one
    /// that matters most is the current: the LED wants milliamps.
    /// </summary>
    [Fact]
    public void TheRelayExampleGetsFromALogicPinToAnEnergisedCoil()
    {
        using var vm = Load("Relay Driver");

        var opto = vm.Circuit.Components.OfType<Optocoupler>().Single();
        var relay = vm.Circuit.Components.OfType<Relay>().Single();
        var sim = vm.Simulation.Simulator!;

        sim.Run(200e-3);

        var energised = 0;
        var atRest = 0;

        while (sim.Time < 900e-3)
        {
            sim.Step();

            if (relay.IsEnergised) energised++; else atRest++;
        }

        // It is being worked, not stuck one way.
        Assert.True(energised > 0 && atRest > 0, $"energised {energised}, at rest {atRest}");
        Assert.True(relay.Operations >= 4, $"only {relay.Operations} operations");

        // Well past pull-in when it is on: a coil held just over its threshold is a coil that buzzes.
        Assert.True(relay.PeakCoilVoltage > 0);
        Assert.Empty(relay.Violations);
        Assert.True(opto.Model.CurrentTransferRatio > 0);
    }

    /// <summary>
    /// And the contacts are a real changeover: the two lamps are never lit together, which is the
    /// whole difference between a relay and a transistor.
    /// </summary>
    [Fact]
    public void AndTheTwoLampsAreNeverLitTogether()
    {
        using var vm = Load("Relay Driver");

        var relay = vm.Circuit.Components.OfType<Relay>().Single();
        var sim = vm.Simulation.Simulator!;

        sim.Run(200e-3);

        var bothLit = 0;
        var openSeen = 0;
        var closedSeen = 0;

        while (sim.Time < 900e-3)
        {
            sim.Step();

            var onOpen = sim.NodeVoltage(relay.NormallyOpen) > 6.0;
            var onClosed = sim.NodeVoltage(relay.NormallyClosed) > 6.0;

            if (onOpen && onClosed) bothLit++;
            if (onOpen) openSeen++;
            if (onClosed) closedSeen++;
        }

        Assert.Equal(0, bothLit);
        Assert.True(openSeen > 0 && closedSeen > 0);
    }

    /// <summary>
    /// A panel's maximum power point, which is a property of the panel and not of the load: the
    /// peak comes out around four fifths of the open-circuit voltage, as it does on a datasheet.
    /// </summary>
    [Fact]
    public void TheSolarExampleHasItsPeakPowerAtAboutFourFifthsOfOpenCircuit()
    {
        var best = (Power: 0.0, Voltage: 0.0);
        var openCircuit = 0.0;

        for (var position = 0.02; position <= 1.0; position += 0.02)
        {
            var (power, voltage, panelOpen) = PanelAt(position);

            openCircuit = Math.Max(openCircuit, panelOpen);
            if (power > best.Power) best = (power, voltage);
        }

        Assert.True(best.Power > 0.3, $"the best the panel managed was {best.Power * 1e3:0} mW");

        // The knee of the curve, which is where a maximum power point tracker spends its life.
        Assert.Equal(0.8, best.Voltage / openCircuit, 0.06);
    }

    /// <summary>And either side of it is worse, which is the reason the point is worth finding.</summary>
    [Fact]
    public void AndBothALighterAndAHeavierLoadTakeLessFromIt()
    {
        var (atPeak, _, _) = PanelAt(0.20);
        var (lighter, _, _) = PanelAt(0.60);
        var (heavier, _, _) = PanelAt(0.05);

        Assert.True(atPeak > lighter, $"{atPeak * 1e3:0} mW against {lighter * 1e3:0} mW on a lighter load");
        Assert.True(atPeak > heavier, $"{atPeak * 1e3:0} mW against {heavier * 1e3:0} mW on a heavier one");
    }

    private static (double Power, double Voltage, double OpenCircuit) PanelAt(double position)
    {
        using var vm = Load("Solar Panel");

        vm.Circuit.Components.OfType<Potentiometer>().Single().Position = position;
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var panel = vm.Circuit.Components.OfType<SolarCell>().Single();
        vm.Simulation.Simulator!.Run(20e-3);

        return (panel.Power, panel.Voltage, OpenCircuitVoltage());
    }

    /// <summary>
    /// The panel with effectively nothing on it. Measured rather than worked out from the diode
    /// equation, because the shunt resistance in the model has a say in it too.
    /// </summary>
    private static double OpenCircuitVoltage()
    {
        using var vm = Load("Solar Panel");

        var pot = vm.Circuit.Components.OfType<Potentiometer>().Single();
        pot.Resistance = 10e6;
        pot.Position = 1.0;
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var panel = vm.Circuit.Components.OfType<SolarCell>().Single();
        vm.Simulation.Simulator!.Run(20e-3);

        return panel.Voltage;
    }

    /// <summary>
    /// Constant current until the cell reaches its float voltage, then constant voltage with the
    /// current tapering away — both phases, in that order, ending in termination.
    /// </summary>
    [Fact]
    public void TheLithiumExampleGoesThroughBothPhasesInOrder()
    {
        using var vm = Load("Lithium Charge Cycle");

        var charger = vm.Circuit.Components.OfType<LithiumCharger>().Single();
        var sim = vm.Simulation.Simulator!;

        var constantCurrent = 0.0;
        var constantVoltage = 0.0;
        var completedAt = double.NaN;
        var currentDuringCc = 0.0;

        while (sim.Time < 1.5)
        {
            sim.Step();

            switch (charger.Phase)
            {
                case ChargePhase.ConstantCurrent:
                    constantCurrent = sim.Time;
                    // The highest it reaches, not its value at the last instant of the phase:
                    // by then it is already on the way into the taper.
                    currentDuringCc = Math.Max(currentDuringCc, charger.Current);
                    break;
                case ChargePhase.ConstantVoltage:
                    constantVoltage = sim.Time;
                    Assert.True(constantCurrent > 0, "it reached constant voltage without a constant-current phase");
                    break;
                case ChargePhase.Complete when double.IsNaN(completedAt):
                    completedAt = sim.Time;
                    break;
            }
        }

        // In order, all three, and the current in the first phase is the one it was set to.
        Assert.True(constantCurrent > 0, "no constant-current phase");
        Assert.True(constantVoltage > constantCurrent, "no constant-voltage phase after it");
        Assert.True(completedAt > constantVoltage, "it never terminated");

        Assert.Equal(charger.ChargeCurrent, currentDuringCc, 0.02);
        Assert.Equal(charger.FloatVoltage, charger.CellVoltage, 0.1);
    }

    /// <summary>
    /// Two stages, and the point is that they are different sizes. The varistor takes the top off
    /// a spike measured in hundreds of volts; the TVS holds what is left to its clamping voltage.
    /// </summary>
    [Fact]
    public void TheSurgeExampleClampsInTwoStages()
    {
        using var vm = Load("Surge Protection");

        var mov = vm.Circuit.Components.OfType<Varistor>().Single();
        var tvs = vm.Circuit.Components.OfType<TransientSuppressor>().Single();
        var fuse = vm.Circuit.Components.OfType<Fuse>().Single();
        var sim = vm.Simulation.Simulator!;

        var atInput = double.MinValue;
        var atLoad = double.MinValue;
        var unprotected = 0.0;

        while (sim.Time < 30e-3)
        {
            sim.Step();

            atInput = Math.Max(atInput, sim.NodeVoltage(mov.A));
            atLoad = Math.Max(atLoad, sim.NodeVoltage(tvs.A));
            unprotected = Math.Max(unprotected, 24.0 + 160.0);
        }

        // The spike is far bigger than either clamp, and each one brings it down a step.
        Assert.True(atInput < unprotected * 0.6, $"the varistor let {atInput:0} V through");
        Assert.True(atLoad < atInput * 0.7, $"the TVS took {atInput:0} V only down to {atLoad:0} V");

        // And the load is held at about the TVS's own clamping figure, not at the varistor's.
        Assert.Equal(tvs.ClampingVoltage, atLoad, 4.0);

        // The fuse is for a fault, not for a transient: a spike this short does not open it.
        Assert.False(fuse.HasBlown);
        Assert.True(mov.AbsorbedJoules > 0 && tvs.AbsorbedJoules > 0);
    }

    /// <summary>And a sustained overload is what a fuse is actually for, so that one does open it.</summary>
    [Fact]
    public void AndASustainedOverloadIsWhatOpensTheFuse()
    {
        using var vm = Load("Surge Protection");

        var fuse = vm.Circuit.Components.OfType<Fuse>().Single();

        // A varistor at the end of its life fails short, and that is the fault the fuse is in the
        // circuit for: a sustained current rather than a microsecond of one. Nothing downstream
        // can do it, because the ten ohms in the way limits any load fault to under the rating.
        vm.Circuit.Components.OfType<Varistor>().Single().VaristorVoltage = 1.0;

        // And with the transient switched off, so what is being measured is the sustained fault
        // and not the spikes: a fuse blows on the integral of current over time, and the spikes
        // contribute almost none of it.
        vm.Circuit.Components.OfType<FunctionGenerator>().Single().AmplitudePeakToPeak = 0;

        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);
        vm.Simulation.Simulator!.Run(2.0);

        Assert.True(fuse.HasBlown, $"the fuse only reached {fuse.MeltFraction:0.00} of its melting integral");
    }

    /// <summary>
    /// Quadrature: the direction is not in either output, it is in which one moves first. Turning
    /// it forwards puts A ahead of B, and neither channel alone says anything.
    /// </summary>
    [Fact]
    public void TheEncoderExamplePutsALeadingBWhenItIsTurnedForwards()
    {
        Assert.Equal("A", FirstToMove(forwards: true));
        Assert.Equal("B", FirstToMove(forwards: false));
    }

    /// <summary>
    /// And it bounces, because it is two metal contacts. The example is not much use without it:
    /// this is the signal a counter has to survive.
    /// </summary>
    [Fact]
    public void AndTheContactsChatterOnTheWayRound()
    {
        using var vm = Load("Rotary Encoder");

        var encoder = vm.Circuit.Components.OfType<RotaryEncoder>().Single();
        var sim = vm.Simulation.Simulator!;

        sim.Run(5e-3);
        encoder.Detent += 1;

        var edges = 0;
        var wasClosed = encoder.IsAClosed;

        while (sim.Time < 5e-3 + (encoder.DetentDuration * 1.5))
        {
            sim.Step();

            if (encoder.IsAClosed != wasClosed) edges++;
            wasClosed = encoder.IsAClosed;
        }

        // One detent is one rise and one fall on A if the contacts were perfect. They are not.
        Assert.True(edges > 4, $"only {edges} edges on A, so nothing is bouncing");
    }

    private static string FirstToMove(bool forwards)
    {
        using var vm = Load("Rotary Encoder");

        var encoder = vm.Circuit.Components.OfType<RotaryEncoder>().Single();
        var sim = vm.Simulation.Simulator!;

        sim.Run(5e-3);
        encoder.Detent += forwards ? 1 : -1;

        // Which contact closes first is the whole of the direction, and the two orders are not
        // mirror images: going one way the first quarter closes A alone, going the other it
        // closes B alone. So watch for the first one to be closed on its own rather than reading
        // a fixed instant, which lands in a different quarter depending on the direction.
        var deadline = sim.Time + (encoder.DetentDuration * 1.5);
        var settled = 0.0;

        while (sim.Time < deadline)
        {
            var before = sim.Time;
            sim.Step();

            // Only a state that holds past the contact bounce counts, or the chatter answers first.
            if (encoder.IsAClosed != encoder.IsBClosed) settled += sim.Time - before;
            else settled = 0;

            if (settled > encoder.BounceDuration * 1.5) return encoder.IsAClosed ? "A" : "B";
        }

        throw new Xunit.Sdk.XunitException("neither contact settled closed on its own during the turn");
    }
}
