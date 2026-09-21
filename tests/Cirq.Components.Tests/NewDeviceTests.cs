using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The five parts added to cover what the palette could not previously show: a tri-state bus, a
/// wired-AND one, a device that conducts with a voltage offset rather than a resistance, light
/// turned into current without gain, and a counter whose outputs all move together.
/// </summary>
public class NewDeviceTests
{
    // ---- IGBT ------------------------------------------------------------

    private static (CircuitSimulator Sim, Igbt Q, Resistor Load) IgbtRig(double gateVolts, double rail = 400.0)
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(rail));
        var load = circuit.Add(new Resistor(40.0));
        var q = circuit.Add(new Igbt());
        var drive = circuit.Add(new DcVoltageSource(gateVolts));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, load.A);
        circuit.Connect(load.B, q.Collector);
        circuit.Connect(q.Emitter, gnd.Pin);
        circuit.Connect(drive.Negative, gnd.Pin);
        circuit.Connect(drive.Positive, q.Gate);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        return (sim, q, load);
    }

    [Fact]
    public void TheIgbtNeedsItsThresholdBeforeItConductsAtAll()
    {
        var (_, off, _) = IgbtRig(gateVolts: 4.0);

        Assert.False(off.IsOn);
        Assert.Equal(0.0, off.CollectorCurrent, 1e-6);

        var (_, on, _) = IgbtRig(gateVolts: 15.0);

        Assert.True(on.IsOn);
        Assert.True(on.CollectorCurrent > 9.0, $"only {on.CollectorCurrent:0.00} A");
    }

    /// <summary>
    /// The defining difference from a MOSFET: the on-state is a junction drop plus a small
    /// resistance, not a resistance alone. It does not go to zero however hard it is driven.
    /// </summary>
    [Fact]
    public void AndConductsWithAVoltageOffsetRatherThanAResistance()
    {
        var (_, q, _) = IgbtRig(gateVolts: 15.0);

        var expected = q.Model.OffsetVoltage + (q.CollectorCurrent * q.Model.OnResistance);

        Assert.Equal(expected, q.CollectorVoltage, 0.05);
        Assert.True(q.CollectorVoltage > q.Model.OffsetVoltage);

        // A MOSFET in the same place would be at a few tens of millivolts, not over a volt.
        Assert.True(q.CollectorVoltage > 1.0);
    }

    /// <summary>
    /// And the reason IGBTs are slow: turning the gate off does not stop the current, because the
    /// bipolar section is full of stored charge that has to recombine first.
    /// </summary>
    [Fact]
    public void AndKeepsConductingAfterTheGateHasGone()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(300.0));
        var load = circuit.Add(new Resistor(30.0));
        var q = circuit.Add(new Igbt());
        var drive = circuit.Add(new FunctionGenerator(Waveform.Square, 5e3, 15.0) { DcOffset = 7.5 });
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, load.A);
        circuit.Connect(load.B, q.Collector);
        circuit.Connect(q.Emitter, gnd.Pin);
        circuit.Connect(drive.Return, gnd.Pin);
        circuit.Connect(drive.Output, q.Gate);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(200e-6);

        var tailing = 0;
        var peak = 0.0;

        while (sim.Time < 500e-6)
        {
            sim.Step();

            if (!q.IsTailing) continue;

            tailing++;
            peak = Math.Max(peak, q.Dissipation);
        }

        Assert.True(tailing > 0, "the gate went off and the current stopped dead");

        // The tail happens with the full rail across the device, which is where the loss is: far
        // more power than the on-state ever dissipates.
        Assert.True(peak > 100, $"the tail only reached {peak:0} W");
    }

    // ---- photodiode ------------------------------------------------------

    private static (Photodiode Diode, double Across) PhotodiodeInto(double lux, double load, double bias)
    {
        var circuit = new Circuit();
        var diode = circuit.Add(new Photodiode { Illuminance = lux });
        var resistor = circuit.Add(new Resistor(load));
        var supply = circuit.Add(new DcVoltageSource(bias));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, diode.Cathode);
        circuit.Connect(diode.Anode, resistor.A);
        circuit.Connect(resistor.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        return (diode, sim.NodeVoltage(resistor.A) - sim.NodeVoltage(resistor.B));
    }

    /// <summary>
    /// Current proportional to light, and linear across the decades — which is what makes it a
    /// measuring device rather than a detector.
    /// </summary>
    [Fact]
    public void ThePhotodiodePassesACurrentProportionalToTheLight()
    {
        var dark = PhotodiodeInto(0, 100e3, 5.0).Diode;
        var dim = PhotodiodeInto(100, 100e3, 5.0).Diode;
        var bright = PhotodiodeInto(1000, 100e3, 5.0).Diode;

        Assert.True(dark.Current < 10e-9, $"{dark.Current * 1e9:0.0} nA in the dark");

        // Ten times the light, ten times the current — the dark current aside.
        Assert.Equal(10.0, (bright.Current - dark.Current) / (dim.Current - dark.Current), 0.2);
    }

    /// <summary>
    /// It has no gain, which is the whole trade against the phototransistor beside it: three
    /// orders of magnitude less current for the same light, in exchange for speed and linearity.
    /// </summary>
    [Fact]
    public void AndHasNoneOfThePhototransistorsGain()
    {
        var diode = new Photodiode { Illuminance = 1000 };
        var transistor = new Phototransistor { Illuminance = 1000 };

        Assert.True(transistor.PhotoCurrent > diode.PhotoCurrent * 100,
            $"{transistor.PhotoCurrent:E2} A against {diode.PhotoCurrent:E2} A");
    }

    /// <summary>
    /// Too large a load resistor and it runs out of voltage: the junction forward-biases and takes
    /// the photocurrent back. That is exactly why the part wants a transimpedance amplifier, which
    /// holds it at zero volts and never lets this happen.
    /// </summary>
    [Fact]
    public void AndSaturatesWhenTheLoadResistorIsTooLarge()
    {
        var (small, acrossSmall) = PhotodiodeInto(10_000, 100e3, 5.0);
        var (large, acrossLarge) = PhotodiodeInto(10_000, 10e6, 5.0);

        // A hundred times the resistor does not give a hundred times the voltage.
        Assert.True(Math.Abs(acrossLarge) < Math.Abs(acrossSmall) * 10);

        Assert.True(small.IsReverseBiased, "the small load should leave it reverse biased");
        Assert.False(large.IsReverseBiased, "the large load should have pulled it forward");
    }

    /// <summary>
    /// Reverse bias is what makes it fast: it widens the depletion region and the junction
    /// capacitance falls with it.
    /// </summary>
    [Fact]
    public void AndReverseBiasLowersItsCapacitance()
    {
        var unbiased = PhotodiodeInto(100, 1e3, 0.0).Diode;
        var biased = PhotodiodeInto(100, 1e3, 20.0).Diode;

        Assert.True(biased.Capacitance < unbiased.Capacitance / 2,
            $"{biased.Capacitance * 1e12:0.0} pF against {unbiased.Capacitance * 1e12:0.0} pF");
    }

    // ---- 74245 -----------------------------------------------------------

    private static (CircuitSimulator Sim, Ic74245 Ic, LogicToggle Dir, LogicToggle Enable, Resistor Load)
        TransceiverRig(bool aToB, bool enabled, bool sourceBit)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var ic = circuit.Add(new Ic74245());
        var gnd = circuit.Add(new Ground());

        var dir = circuit.Add(new LogicToggle(aToB));
        var enable = circuit.Add(new LogicToggle(!enabled));     // active low
        var source = circuit.Add(new LogicToggle(sourceBit));

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(ic.Vcc, rail.Positive);
        circuit.Connect(ic.Gnd, gnd.Pin);
        circuit.Connect(ic.Direction, dir.Out);
        circuit.Connect(ic.OutputEnable, enable.Out);

        circuit.Connect(aToB ? ic.A[0] : ic.B[0], source.Out);

        // A pull-down on the far side, so a released pin reads as nothing rather than floating.
        var load = circuit.Add(new Resistor(10e3));
        circuit.Connect(aToB ? ic.B[0] : ic.A[0], load.A);
        circuit.Connect(load.B, gnd.Pin);

        for (var i = 1; i < 8; i++)
        {
            var a = circuit.Add(new Resistor(10e3));
            circuit.Connect(ic.A[i], a.A);
            circuit.Connect(a.B, gnd.Pin);

            var b = circuit.Add(new Resistor(10e3));
            circuit.Connect(ic.B[i], b.A);
            circuit.Connect(b.B, gnd.Pin);
        }

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(100e-6);

        return (sim, ic, dir, enable, load);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheTransceiverCarriesABitWhicheverWayItIsPointed(bool aToB)
    {
        var (sim, ic, _, _, load) = TransceiverRig(aToB, enabled: true, sourceBit: true);

        Assert.True(ic.IsEnabled);
        Assert.Equal(aToB, ic.IsAToB);

        // The far side is being driven high against its pull-down, which only a driven output does.
        Assert.True(sim.NodeVoltage(load.A) > 2.0,
            $"the far side only reached {sim.NodeVoltage(load.A):0.00} V");
    }

    /// <summary>
    /// The thing no other part in the palette does: released means <i>nothing</i>, so the pull-down
    /// alone decides the wire. An open-collector output could not do this — it would still be
    /// pulling the line down.
    /// </summary>
    [Fact]
    public void AndReleasesTheBusEntirelyWhenItIsDisabled()
    {
        var (sim, ic, _, _, load) = TransceiverRig(aToB: true, enabled: false, sourceBit: true);

        Assert.False(ic.IsEnabled);
        Assert.True(sim.NodeVoltage(load.A) < 0.2,
            $"a released pin left the bus at {sim.NodeVoltage(load.A):0.00} V");
    }

    // ---- 74161 -----------------------------------------------------------

    [Fact]
    public void TheSynchronousCounterCountsAndRollsOver()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var ic = circuit.Add(new Ic74161());
        var clock = circuit.Add(new ClockSource(1e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(ic.Vcc, rail.Positive);
        circuit.Connect(ic.Gnd, gnd.Pin);
        circuit.Connect(ic.Clock, clock.Out);

        foreach (var pin in new[] { ic.MasterReset, ic.ParallelEnable, ic.CountEnableP, ic.CountEnableT })
            circuit.Connect(pin, rail.Positive);

        foreach (var data in ic.Data) circuit.Connect(data, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        List<int> seen = [];
        var last = -1;

        while (sim.Time < 20e-3)
        {
            sim.Step();

            if (ic.Count == last) continue;

            seen.Add(ic.Count);
            last = ic.Count;
        }

        // Every value in order, and back round to zero after fifteen.
        Assert.Equal([.. Enumerable.Range(1, 15)], seen.Take(15));
        Assert.Equal(0, seen[15]);
    }

    /// <summary>
    /// Terminal count is what chains two of these together, and it is gated by CET rather than
    /// being a bare decode of fifteen — which is what stops the carry rippling.
    /// </summary>
    [Fact]
    public void AndItsCarryIsGatedByTheEnableThatChainsIt()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var ic = circuit.Add(new Ic74161());
        var clock = circuit.Add(new ClockSource(1e3));
        var enable = circuit.Add(new LogicToggle(true));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(ic.Vcc, rail.Positive);
        circuit.Connect(ic.Gnd, gnd.Pin);
        circuit.Connect(ic.Clock, clock.Out);
        circuit.Connect(ic.CountEnableT, enable.Out);

        foreach (var pin in new[] { ic.MasterReset, ic.ParallelEnable, ic.CountEnableP })
            circuit.Connect(pin, rail.Positive);

        foreach (var data in ic.Data) circuit.Connect(data, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        var sawCarry = false;

        while (sim.Time < 20e-3)
        {
            sim.Step();

            if (ic.Count == 15 && sim.NodeVoltage(ic.TerminalCount) > 2.5) sawCarry = true;
        }

        Assert.True(sawCarry, "the carry never went high at fifteen");
    }

    // ---- CAN -------------------------------------------------------------

    private static (CircuitSimulator Sim, CanTransceiver First, CanTransceiver Second)
        CanBus(bool firstDominant, bool secondDominant)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());

        var first = circuit.Add(new CanTransceiver());
        var second = circuit.Add(new CanTransceiver());

        // TXD low is a dominant bit, which is the inversion that catches everybody.
        var txFirst = circuit.Add(new LogicToggle(!firstDominant));
        var txSecond = circuit.Add(new LogicToggle(!secondDominant));

        circuit.Connect(rail.Negative, gnd.Pin);

        foreach (var node in new[] { first, second })
        {
            circuit.Connect(node.Vcc, rail.Positive);
            circuit.Connect(node.Gnd, gnd.Pin);
        }

        circuit.Connect(first.High, second.High);
        circuit.Connect(first.Low, second.Low);
        circuit.Connect(first.TransmitIn, txFirst.Out);
        circuit.Connect(second.TransmitIn, txSecond.Out);

        // A hundred and twenty ohms at each end, as a real bus is terminated.
        foreach (var _ in new[] { 0, 1 })
        {
            var terminator = circuit.Add(new Resistor(120));
            circuit.Connect(terminator.A, first.High);
            circuit.Connect(terminator.B, first.Low);
        }

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(20e-6);

        return (sim, first, second);
    }

    [Fact]
    public void AQuietCanBusSitsRecessive()
    {
        var (_, first, second) = CanBus(firstDominant: false, secondDominant: false);

        Assert.False(first.IsBusDominant);
        Assert.False(second.IsBusDominant);
        Assert.Equal(0.0, first.Difference, 0.2);
    }

    [Fact]
    public void AndOneDriverPullsItDominant()
    {
        var (_, first, _) = CanBus(firstDominant: true, secondDominant: false);

        Assert.True(first.IsDriving);
        Assert.True(first.IsBusDominant);
        Assert.True(first.Difference > first.ReceiverThreshold,
            $"only {first.Difference:0.000} V across the pair");
    }

    /// <summary>
    /// The whole point of the bus. A node sending recessive while somebody else sends dominant
    /// hears the dominant bit and knows it has been outranked — no collision, no retry, and the
    /// other message carries on without noticing.
    /// </summary>
    [Fact]
    public void AndTheRecessiveNodeLosesArbitrationWithoutACollision()
    {
        var (_, winner, loser) = CanBus(firstDominant: true, secondDominant: false);

        Assert.True(loser.LostArbitration);
        Assert.False(winner.LostArbitration);

        // Both hear the same bus, which is what makes the test meaningful at all.
        Assert.True(winner.IsBusDominant);
        Assert.True(loser.IsBusDominant);
    }

    /// <summary>And two nodes both sending dominant is not a fault — they simply agree.</summary>
    [Fact]
    public void AndTwoDominantDriversDoNotFight()
    {
        var (_, first, second) = CanBus(firstDominant: true, secondDominant: true);

        Assert.True(first.IsBusDominant);
        Assert.False(first.LostArbitration);
        Assert.False(second.LostArbitration);
    }
}
