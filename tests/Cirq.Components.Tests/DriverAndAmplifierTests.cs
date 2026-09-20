using Cirq.Components.Digital;
using Cirq.Components.Ics;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class Uln2003Tests
{
    /// <summary>One channel with a load wired the right way: supply, load, then the output pin.</summary>
    private static (CircuitSimulator Sim, Uln2003 Driver, Resistor Load, DcVoltageSource Drive) Rig(
        bool on, bool loadToGround = false)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(12.0));
        var driver = circuit.Add(new Uln2003());
        var load = circuit.Add(new Resistor(120));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(driver.Gnd, gnd.Pin);
        circuit.Connect(driver.Common, rail.Positive);

        if (loadToGround)
        {
            // The mistake: a sink driver cannot push, so this does nothing at all.
            circuit.Connect(driver.Outputs[0], load.A);
            circuit.Connect(load.B, gnd.Pin);
        }
        else
        {
            circuit.Connect(rail.Positive, load.A);
            circuit.Connect(load.B, driver.Outputs[0]);
        }

        var drive = circuit.Add(new DcVoltageSource(on ? 5.0 : 0.0));
        circuit.Connect(drive.Negative, gnd.Pin);
        circuit.Connect(drive.Positive, driver.Inputs[0]);

        // The other six inputs need tying off.
        for (var i = 1; i < 7; i++) circuit.Connect(driver.Inputs[i], gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(10e-6);
        return (sim, driver, load, drive);
    }

    [Fact]
    public void ADrivenChannelSinksTheLoadCurrent()
    {
        var (sim, driver, load, _) = Rig(on: true);

        Assert.True(driver.IsSinking(0));

        // 12 V across 120 ohms less the Darlington's own drop.
        var current = (12.0 - sim.NodeVoltage(load.B)) / 120.0;
        Assert.InRange(current, 0.085, 0.1);
    }

    /// <summary>A Darlington does not saturate to nothing — about a volt stays behind.</summary>
    [Fact]
    public void AConductingOutputKeepsItsSaturationVoltage()
    {
        var (sim, driver, load, _) = Rig(on: true);

        Assert.Equal(driver.SaturationVoltage, sim.NodeVoltage(load.B), 0.25);
        Assert.True(sim.NodeVoltage(load.B) > 0.5,
            "a Darlington sink that reached zero volts would be a MOSFET");
    }

    [Fact]
    public void AnUndrivenChannelReleasesItsOutput()
    {
        var (sim, driver, load, _) = Rig(on: false);

        Assert.False(driver.IsSinking(0));

        // Released, not pulled low: the load sees the supply at both ends and nothing flows.
        var current = (12.0 - sim.NodeVoltage(load.B)) / 120.0;
        Assert.True(Math.Abs(current) < 1e-3, $"{current:g3} A through a released output");
    }

    /// <summary>
    /// The usual first mistake, and it should do nothing rather than half-work: a sink driver
    /// cannot source, so a load between the output and ground never sees a voltage.
    /// </summary>
    [Fact]
    public void ALoadWiredToGroundInsteadOfTheSupplyDoesNothing()
    {
        var (sim, _, load, _) = Rig(on: true, loadToGround: true);

        Assert.True(Math.Abs(sim.NodeVoltage(load.A)) < 1.5,
            "there is nothing to push current through a load wired this way");
    }

    /// <summary>
    /// The COM pin's diodes, which are the reason the pin is there. An inductive load switched off
    /// has to put its current somewhere, and with COM tied to the load's supply it freewheels
    /// round through the diode instead of taking the output pin with it.
    /// </summary>
    [Fact]
    public void ComClampsAnInductiveLoadAtADiodeDropAboveItsSupply()
    {
        var peak = InductiveKick(clamped: true);

        Assert.InRange(peak, 12.0, 14.0);
    }

    /// <summary>
    /// And with COM left unconnected there is no path, which is modelled rather than assumed away.
    /// Leaving that pin off is the mistake this part invites and the answer is not a small one.
    /// </summary>
    [Fact]
    public void AndWithComUnconnectedThereIsNothingToClampIt()
    {
        var peak = InductiveKick(clamped: false);

        Assert.True(peak > 100.0, $"the unclamped output only reached {peak:0} V");
    }

    private static double InductiveKick(bool clamped)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(12.0));
        var driver = circuit.Add(new Uln2003());
        var coil = circuit.Add(new Inductor(10e-3) { SeriesResistance = 8.0 });
        var clock = circuit.Add(new ClockSource(500.0));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(driver.Gnd, gnd.Pin);

        if (clamped) circuit.Connect(driver.Common, rail.Positive);

        circuit.Connect(rail.Positive, coil.A);
        circuit.Connect(coil.B, driver.Outputs[0]);
        circuit.Connect(driver.Inputs[0], clock.Out);

        for (var i = 1; i < 7; i++) circuit.Connect(driver.Inputs[i], gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        var peak = 0.0;

        while (sim.Time < 6e-3)
        {
            sim.Step();
            peak = Math.Max(peak, sim.NodeVoltage(coil.B));
        }

        return peak;
    }

    [Fact]
    public void AllSevenChannelsAreIndependent()
    {
        var driver = new Uln2003();

        var pins = driver.Inputs.Concat(driver.Outputs).Append(driver.Common).ToList();
        Assert.Equal(pins.Count, pins.Distinct().Count());
        Assert.Equal(7, driver.Inputs.Count);
        Assert.Equal(7, driver.Outputs.Count);
    }
}

public class FixedGainAmplifierTests
{
    /// <summary>An LM386 on a single supply with a signal on its + input.</summary>
    private static (CircuitSimulator Sim, Lm386 Amp, Speaker Load) AudioRig(double input, double gain = 20)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(9.0));
        var signal = circuit.Add(new DcVoltageSource(input));
        var amp = circuit.Add(new Lm386 { VoltageGain = gain });
        // No initial condition: the bias point is the steady state, where the coupling capacitor
        // has charged to the idle output and is blocking DC, which is what it is there to do.
        var coupling = circuit.Add(new Capacitor(220e-6));
        var speaker = circuit.Add(new Speaker(8.0));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(amp.PositiveSupply, rail.Positive);
        circuit.Connect(amp.NegativeSupply, gnd.Pin);

        circuit.Connect(signal.Negative, gnd.Pin);
        circuit.Connect(signal.Positive, amp.InPlus);
        circuit.Connect(amp.InMinus, gnd.Pin);

        // Coupled, because the output idles at half the supply.
        circuit.Connect(amp.Output, coupling.A);
        circuit.Connect(coupling.B, speaker.A);
        circuit.Connect(speaker.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, amp, speaker);
    }

    /// <summary>With no input the output sits at half the supply, so it can swing both ways.</summary>
    [Fact]
    public void TheOutputIdlesAtHalfTheSupply()
    {
        var (sim, amp, _) = AudioRig(input: 0.0);

        Assert.Equal(4.5, sim.NodeVoltage(amp.Output), 0.2);
        Assert.False(amp.IsClipping);
    }

    /// <summary>The gain is the gain: a tenth of a volt in, two volts out at ×20.</summary>
    [Fact]
    public void ItAmplifiesByItsStatedGain()
    {
        var (quiet, ampQ, _) = AudioRig(input: 0.0);
        var (loud, ampL, _) = AudioRig(input: 0.1);

        var swing = loud.NodeVoltage(ampL.Output) - quiet.NodeVoltage(ampQ.Output);

        Assert.Equal(2.0, swing, 0.2);
    }

    /// <summary>Asked for more than the rails allow, it clips rather than inventing volts.</summary>
    [Fact]
    public void ItClipsAtTheRails()
    {
        var (sim, amp, _) = AudioRig(input: 1.0);      // x20 wants 20 V from a 9 V rail

        Assert.True(amp.IsClipping);
        Assert.InRange(sim.NodeVoltage(amp.Output), 7.0, 9.0);
    }

    /// <summary>Driving the speaker puts real power into it, which is what it reports.</summary>
    [Fact]
    public void TheSpeakerReportsThePowerItIsTaking()
    {
        var circuit = new Circuit();
        var source = circuit.Add(new FunctionGenerator(Waveform.Sine, 1e3, 4.0));
        var speaker = circuit.Add(new Speaker(8.0));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(source.Return, gnd.Pin);
        circuit.Connect(source.Output, speaker.A);
        circuit.Connect(speaker.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1.0);

        // 2 V peak into 6.4 ohms of coil resistance is 2^2/(2*6.4) = 0.31 W average.
        Assert.Equal(0.31, speaker.AveragePower, 0.08);
        Assert.True(speaker.IsSounding);
    }

    /// <summary>An instrumentation amp lifts a small difference off a large common voltage.</summary>
    [Fact]
    public void TheInstrumentationAmpRejectsTheCommonMode()
    {
        static double Measure(double common, double difference)
        {
            var circuit = new Circuit();
            var rail = circuit.Add(new DcVoltageSource(12.0));
            var reference = circuit.Add(new DcVoltageSource(2.5));
            var plus = circuit.Add(new DcVoltageSource(common + (difference / 2)));
            var minus = circuit.Add(new DcVoltageSource(common - (difference / 2)));
            var amp = circuit.Add(new Ina126 { DifferentialGain = 100 });
            var load = circuit.Add(new Resistor(100e3));
            var gnd = circuit.Add(new Ground());

            foreach (var s in new[] { rail, reference, plus, minus }) circuit.Connect(s.Negative, gnd.Pin);
            circuit.Connect(amp.PositiveSupply, rail.Positive);
            circuit.Connect(amp.NegativeSupply, gnd.Pin);
            circuit.Connect(amp.Reference, reference.Positive);
            circuit.Connect(amp.InPlus, plus.Positive);
            circuit.Connect(amp.InMinus, minus.Positive);
            circuit.Connect(amp.Output, load.A);
            circuit.Connect(load.B, gnd.Pin);

            var sim = new CircuitSimulator(circuit);
            sim.Reset();
            sim.SolveOperatingPoint();
            return sim.NodeVoltage(amp.Output);
        }

        // 10 mV of difference at a gain of 100 is 1 V above the 2.5 V reference...
        Assert.Equal(3.5, Measure(common: 2.0, difference: 10e-3), 0.05);

        // ...and moving both inputs four volts together changes nothing, which is the whole point.
        Assert.Equal(3.5, Measure(common: 6.0, difference: 10e-3), 0.05);
    }

    /// <summary>The datasheet's gain formula, so a resistor value can be turned into a gain.</summary>
    [Fact]
    public void TheGainResistorFormulaMatchesTheDatasheet()
    {
        Assert.Equal(5.0, Ina126.GainForResistance(double.PositiveInfinity), 1e-6);
        Assert.Equal(105.0, Ina126.GainForResistance(800.0), 1e-6);

        var amp = new Ina126 { DifferentialGain = 105.0 };
        Assert.Equal(800.0, amp.GainSettingResistance, 1e-6);
    }
}
