using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class ScrTests
{
    /// <summary>An SCR in series with a lamp, with a switchable gate drive.</summary>
    private static (CircuitSimulator Sim, SiliconControlledRectifier Scr, ToggleSwitch Trigger,
        DcVoltageSource Source) Rig(double supply = 12.0)
    {
        var circuit = new Circuit();
        var source = circuit.Add(new DcVoltageSource(supply));
        var load = circuit.Add(new Resistor(100));
        var scr = circuit.Add(new SiliconControlledRectifier());
        var gateResistor = circuit.Add(new Resistor(1e3));
        var trigger = circuit.Add(new ToggleSwitch(false));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(source.Negative, gnd.Pin);
        circuit.Connect(source.Positive, load.A);
        circuit.Connect(load.B, scr.Anode);
        circuit.Connect(scr.Cathode, gnd.Pin);

        // Gate driven from the supply through a resistor and a switch.
        circuit.Connect(source.Positive, trigger.A);
        circuit.Connect(trigger.B, gateResistor.A);
        circuit.Connect(gateResistor.B, scr.Gate);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { UseInitialConditions = true });
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, scr, trigger, source);
    }

    [Fact]
    public void ItBlocksUntilTheGateIsDriven()
    {
        var (sim, scr, _, _) = Rig();

        sim.Run(1e-3);

        Assert.False(scr.IsLatched);
        Assert.True(Math.Abs(scr.MainCurrent) < 1e-6, $"{scr.MainCurrent:0.000000} A leaked through");
    }

    [Fact]
    public void AGatePulseFiresIt()
    {
        var (sim, scr, trigger, _) = Rig();

        trigger.IsClosed = true;
        sim.Run(1e-3);

        Assert.True(scr.IsLatched);
        Assert.Equal(1, scr.Firings);
    }

    /// <summary>
    /// The defining behaviour, and the thing nothing else in the library does: once fired it
    /// ignores the gate entirely. Taking the drive away does not turn it off.
    /// </summary>
    [Fact]
    public void RemovingTheGateDriveDoesNotTurnItOff()
    {
        var (sim, scr, trigger, _) = Rig();

        trigger.IsClosed = true;
        sim.Run(1e-3);
        Assert.True(scr.IsLatched);

        trigger.IsClosed = false;
        sim.Run(5e-3);

        Assert.True(scr.IsLatched, "an SCR holds on after the gate goes away — that is the point of it");
        Assert.True(scr.GateCurrent < 1e-6, "and it is holding on with no gate current at all");
    }

    /// <summary>The only way to stop it: take the anode current below the holding current.</summary>
    [Fact]
    public void InterruptingTheAnodeCurrentDropsItOut()
    {
        var (sim, scr, trigger, source) = Rig();

        trigger.IsClosed = true;
        sim.Run(1e-3);
        trigger.IsClosed = false;
        Assert.True(scr.IsLatched);

        // Reverse the supply: the anode current collapses and the latch lets go.
        source.Voltage = -12.0;
        sim.Run(1e-3);

        Assert.False(scr.IsLatched);
    }

    [Fact]
    public void ItBlocksInReverseLikeTheRectifierItIs()
    {
        var (sim, scr, trigger, _) = Rig(supply: -12.0);

        trigger.IsClosed = true;
        sim.Run(1e-3);

        Assert.False(scr.IsLatched);
    }
}

public class TriacAndDiacTests
{
    /// <summary>A triac in series with a load on an AC supply, gated through a resistor.</summary>
    private static (CircuitSimulator Sim, Triac Triac, Resistor Load, FunctionGenerator Mains)
        Dimmer(double gateResistance)
    {
        var circuit = new Circuit();
        var mains = circuit.Add(new FunctionGenerator
        {
            Shape = Waveform.Sine,
            Frequency = 50,
            AmplitudePeakToPeak = 60,
            OutputResistance = 1.0,
        });
        var load = circuit.Add(new Resistor(100));
        var triac = circuit.Add(new Triac());
        var gate = circuit.Add(new Resistor(gateResistance));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(mains.Return, gnd.Pin);
        circuit.Connect(mains.Output, load.A);
        circuit.Connect(load.B, triac.MainTerminal2);
        circuit.Connect(triac.MainTerminal1, gnd.Pin);
        circuit.Connect(load.B, gate.A);
        circuit.Connect(gate.B, triac.Gate);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { UseInitialConditions = true });
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, triac, load, mains);
    }

    /// <summary>
    /// A triac conducts either way, which is what separates it from an SCR and why it can control
    /// a mains load on both half cycles rather than just one.
    /// </summary>
    [Fact]
    public void ItFiresOnBothHalvesOfTheCycle()
    {
        var (sim, triac, _, _) = Dimmer(gateResistance: 2e3);

        var positive = false;
        var negative = false;
        sim.TimePointAccepted += _ =>
        {
            if (!triac.IsLatched) return;
            if (triac.MainCurrent > 0) positive = true;
            if (triac.MainCurrent < 0) negative = true;
        };

        sim.Run(60e-3);        // three mains cycles

        Assert.True(positive, "it never conducted on the positive half");
        Assert.True(negative, "it never conducted on the negative half");
    }

    /// <summary>
    /// It turns itself off at every zero crossing, because nothing else can turn it off — the
    /// current simply falls below the holding current. That is why a dimmer has to re-fire twice
    /// per cycle, and why it fires many times over a run rather than latching once.
    /// </summary>
    [Fact]
    public void ItTurnsItselfOffAtEveryZeroCrossing()
    {
        var (sim, triac, _, _) = Dimmer(gateResistance: 2e3);

        sim.Run(100e-3);       // five mains cycles

        // Two half cycles each, so about ten firings; the exact count depends on where in the
        // first cycle it starts.
        Assert.InRange(triac.Firings, 7, 13);
    }

    [Fact]
    public void ALargerGateResistorFiresItLaterAndDeliversLessPower()
    {
        double PowerWith(double gateResistance)
        {
            var (sim, _, load, _) = Dimmer(gateResistance);

            var energy = 0.0;
            var previous = 0.0;
            sim.TimePointAccepted += s =>
            {
                var v = sim.NodeVoltage(load.A) - sim.NodeVoltage(load.B);
                energy += v * v / 100.0 * (s.Time - previous);
                previous = s.Time;
            };

            sim.Run(100e-3);
            return energy;
        }

        var early = PowerWith(1e3);
        var late = PowerWith(20e3);

        Assert.True(late < early,
            $"a later firing angle should deliver less: {late * 1e3:0.0} mJ against {early * 1e3:0.0} mJ");
    }

    [Fact]
    public void ADiacBlocksUntilItsBreakoverVoltage()
    {
        var circuit = new Circuit();
        var source = circuit.Add(new DcVoltageSource(20.0));
        var series = circuit.Add(new Resistor(1e3));
        var diac = circuit.Add(new Diac());
        var gnd = circuit.Add(new Ground());

        circuit.Connect(source.Negative, gnd.Pin);
        circuit.Connect(source.Positive, series.A);
        circuit.Connect(series.B, diac.A);
        circuit.Connect(diac.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { UseInitialConditions = true });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1e-3);

        // 20 V is below the 32 V breakover, so it stays blocking.
        Assert.False(diac.IsLatched);

        source.Voltage = 45.0;
        sim.Run(1e-3);

        Assert.True(diac.IsLatched, "past breakover it should have fired");
    }

    [Fact]
    public void ADiacFiresOnEitherPolarity()
    {
        foreach (var supply in new[] { 45.0, -45.0 })
        {
            var circuit = new Circuit();
            var source = circuit.Add(new DcVoltageSource(supply));
            var series = circuit.Add(new Resistor(1e3));
            var diac = circuit.Add(new Diac());
            var gnd = circuit.Add(new Ground());

            circuit.Connect(source.Negative, gnd.Pin);
            circuit.Connect(source.Positive, series.A);
            circuit.Connect(series.B, diac.A);
            circuit.Connect(diac.B, gnd.Pin);

            var sim = new CircuitSimulator(circuit, new SimulationSettings { UseInitialConditions = true });
            sim.Reset();
            sim.SolveOperatingPoint();
            sim.Run(1e-3);

            Assert.True(diac.IsLatched, $"it should fire at {supply} V, not just positive");
        }
    }
}
