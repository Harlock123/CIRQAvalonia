using Cirq.Components.Explaining;
using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;

namespace Cirq.Components.Tests;

/// <summary>
/// Reading a schematic and saying what it is.
/// <para>
/// Two things are checked of every recogniser: that it finds the thing it is for, and that it
/// stays quiet when the structure is not there. The second matters more. A confident wrong
/// description is the one thing here that could teach somebody something false, and they would
/// have no way to tell — so a recogniser that fires on the wrong circuit is worse than one that
/// never fires at all.
/// </para>
/// </summary>
public class CircuitExplainerTests
{
    private static IReadOnlyList<Explanation> Explain(Circuit circuit) =>
        CircuitExplainer.Explain(circuit);

    private static Explanation Single(Circuit circuit, string containing)
    {
        var found = Explain(circuit).Where(e => e.Headline.Contains(containing, StringComparison.OrdinalIgnoreCase)).ToList();

        return Assert.Single(found);
    }

    // ---- dividers ----------------------------------------------------------

    [Fact]
    public void ItRecognisesADividerAndWorksOutTheTap()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(10.0) { Name = "V1" });
        var top = circuit.Add(new Resistor(6.8e3) { Name = "R1" });
        var bottom = circuit.Add(new Resistor(3.3e3) { Name = "R2" });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);

        var found = Single(circuit, "divider");

        // 10 V × 3.3/(6.8+3.3) = 3.267 V.
        Assert.Contains("3.267V", found.Detail);
        Assert.Equal("R1, R2", found.Designators);
    }

    /// <summary>
    /// Two resistors in series with something else hanging off the tap is not a divider whose
    /// ratio can be quoted, because the load changes it. Saying the unloaded number would be
    /// worse than saying nothing.
    /// </summary>
    [Fact]
    public void ALoadedTapIsNotQuotedAsADivider()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(10.0) { Name = "V1" });
        var top = circuit.Add(new Resistor(6.8e3) { Name = "R1" });
        var bottom = circuit.Add(new Resistor(3.3e3) { Name = "R2" });
        var load = circuit.Add(new Resistor(1e3) { Name = "R3" });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(top.B, load.A);
        circuit.Connect(load.B, ground.Pin);
        circuit.Connect(bottom.B, ground.Pin);

        Assert.DoesNotContain(Explain(circuit), e => e.Headline.Contains("divider"));
    }

    // ---- filters -----------------------------------------------------------

    [Fact]
    public void ItTellsALowPassFromAHighPass()
    {
        var low = new Circuit();

        var lowSource = low.Add(new DcVoltageSource(0) { Name = "V1" });
        var lowR = low.Add(new Resistor(1e3) { Name = "R1" });
        var lowC = low.Add(new Capacitor(100e-9) { Name = "C1" });
        var lowGnd = low.Add(new Ground { Name = "GND1" });

        low.Connect(lowSource.Negative, lowGnd.Pin);
        low.Connect(lowSource.Positive, lowR.A);
        low.Connect(lowR.B, lowC.A);
        low.Connect(lowC.B, lowGnd.Pin);

        Assert.Contains("1.592kHz", Single(low, "low-pass").Detail);

        var high = new Circuit();

        var highSource = high.Add(new DcVoltageSource(0) { Name = "V1" });
        var highC = high.Add(new Capacitor(100e-9) { Name = "C1" });
        var highR = high.Add(new Resistor(1e3) { Name = "R1" });
        var highGnd = high.Add(new Ground { Name = "GND1" });

        high.Connect(highSource.Negative, highGnd.Pin);
        high.Connect(highSource.Positive, highC.A);
        high.Connect(highC.B, highR.A);
        high.Connect(highR.B, highGnd.Pin);

        Assert.Contains("1.592kHz", Single(high, "high-pass").Detail);
    }

    // ---- decoupling --------------------------------------------------------

    [Fact]
    public void ACapacitorAcrossARailIsDecoupling()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(5.0) { Name = "V1" });
        var capacitor = circuit.Add(new Capacitor(100e-9) { Name = "C1" });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, capacitor.A);
        circuit.Connect(capacitor.B, ground.Pin);

        Assert.Contains("5V", Single(circuit, "decouples").Detail);
    }

    // ---- LEDs --------------------------------------------------------------

    [Fact]
    public void ItWorksOutAnLedsCurrent()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(5.0) { Name = "V1" });
        var resistor = circuit.Add(new Resistor(330.0) { Name = "R1" });
        var led = circuit.Add(new Led { Name = "D1" });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, resistor.A);
        circuit.Connect(resistor.B, led.Anode);
        circuit.Connect(led.Cathode, ground.Pin);

        var found = Single(circuit, "current");

        // (5 − about 1.8) / 330 is around 10 mA, which is what anybody would write down.
        Assert.Contains("mA", found.Detail);
        Assert.Contains("330Ω", found.Detail);
    }

    // ---- op-amps -----------------------------------------------------------

    private static (Circuit Circuit, OperationalAmplifier Amp) Stage(bool inverting, double rf, double rg)
    {
        var circuit = new Circuit();

        var positive = circuit.Add(new DcVoltageSource(15.0) { Name = "V1" });
        var negative = circuit.Add(new DcVoltageSource(-15.0) { Name = "V2" });
        var amp = circuit.Add(new OperationalAmplifier { Name = "U1" });
        var feedback = circuit.Add(new Resistor(rf) { Name = "RF" });
        var gain = circuit.Add(new Resistor(rg) { Name = "RG" });
        var input = circuit.Add(new DcVoltageSource(0.1) { Name = "V3" });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Connect(positive.Negative, ground.Pin);
        circuit.Connect(negative.Positive, ground.Pin);
        circuit.Connect(input.Negative, ground.Pin);
        circuit.Connect(amp.PositiveSupply, positive.Positive);
        circuit.Connect(amp.NegativeSupply, negative.Negative);

        circuit.Connect(amp.Output, feedback.A);
        circuit.Connect(feedback.B, amp.Inverting);
        circuit.Connect(amp.Inverting, gain.A);

        if (inverting)
        {
            circuit.Connect(gain.B, input.Positive);
            circuit.Connect(amp.NonInverting, ground.Pin);
        }
        else
        {
            circuit.Connect(gain.B, ground.Pin);
            circuit.Connect(amp.NonInverting, input.Positive);
        }

        return (circuit, amp);
    }

    [Fact]
    public void ItReadsAnInvertingAmplifiersGain()
    {
        var (circuit, _) = Stage(inverting: true, rf: 100e3, rg: 10e3);

        var found = Single(circuit, "inverting amplifier");

        Assert.Contains("−10", found.Detail);
        Assert.Contains("10kΩ", found.Detail);
    }

    [Fact]
    public void ItReadsANonInvertingAmplifiersGain()
    {
        var (circuit, _) = Stage(inverting: false, rf: 100e3, rg: 10e3);

        Assert.Contains("= 11", Single(circuit, "non-inverting amplifier").Detail);
    }

    [Fact]
    public void ItRecognisesAFollower()
    {
        var circuit = new Circuit();

        var positive = circuit.Add(new DcVoltageSource(15.0) { Name = "V1" });
        var negative = circuit.Add(new DcVoltageSource(-15.0) { Name = "V2" });
        var amp = circuit.Add(new OperationalAmplifier { Name = "U1" });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Connect(positive.Negative, ground.Pin);
        circuit.Connect(negative.Positive, ground.Pin);
        circuit.Connect(amp.PositiveSupply, positive.Positive);
        circuit.Connect(amp.NegativeSupply, negative.Negative);
        circuit.Connect(amp.NonInverting, ground.Pin);
        circuit.Connect(amp.Output, amp.Inverting);

        Assert.Contains("gain of one", Single(circuit, "follower").Detail);
    }

    // ---- flyback -----------------------------------------------------------

    [Fact]
    public void ItRecognisesAFlybackDiode()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(12.0) { Name = "V1" });
        var coil = circuit.Add(new Inductor(10e-3) { Name = "L1" });
        var diode = circuit.Add(new Diode(DiodeModel.D1N4001) { Name = "D1" });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, coil.A);
        circuit.Connect(coil.B, ground.Pin);
        circuit.Connect(diode.Cathode, coil.A);
        circuit.Connect(diode.Anode, coil.B);

        var found = Single(circuit, "flyback");

        Assert.Contains("L1", found.Headline);
        Assert.Contains("hundreds of volts", found.Detail);
    }

    /// <summary>An LED across a coil is not a flyback diode; it is an LED, and is left alone.</summary>
    [Fact]
    public void AnLedIsNotAFlybackDiode()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(12.0) { Name = "V1" });
        var coil = circuit.Add(new Inductor(10e-3) { Name = "L1" });
        var led = circuit.Add(new Led { Name = "D1" });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, coil.A);
        circuit.Connect(coil.B, ground.Pin);
        circuit.Connect(led.Cathode, coil.A);
        circuit.Connect(led.Anode, coil.B);

        Assert.DoesNotContain(Explain(circuit), e => e.Headline.Contains("flyback"));
    }

    // ---- nothing to say ----------------------------------------------------

    /// <summary>
    /// A circuit with no structure it knows is left alone rather than described vaguely. Silence
    /// is the correct answer far more often than anything else.
    /// </summary>
    [Fact]
    public void ItSaysNothingAboutSomethingItDoesNotRecognise()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(5.0) { Name = "V1" });
        var one = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, one.A);
        circuit.Connect(one.B, ground.Pin);

        Assert.Empty(Explain(circuit));
    }

    [Fact]
    public void AnEmptyCircuitExplainsNothing() => Assert.Empty(Explain(new Circuit()));

}
