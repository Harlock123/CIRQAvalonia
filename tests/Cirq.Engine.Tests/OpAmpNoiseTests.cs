using Cirq.Components.Ics;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// An op-amp's own noise, which in almost any circuit built around one is the dominant term.
/// <para>
/// It is quoted input-referred — a voltage in series with the input and a current into each pin —
/// because that is the only form that belongs to the part rather than to the circuit. What comes
/// out is that voltage multiplied by the <b>noise gain</b>, which is the gain the amplifier has
/// for its own input and is not always the gain it has for the signal.
/// </para>
/// </summary>
public class OpAmpNoiseTests
{
    private const double Room = 300.0;

    /// <summary>
    /// A non-inverting amplifier: gain 1 + Rf/Rg, fed from a source with no impedance so the
    /// amplifier's own voltage noise is the only thing at the input.
    /// </summary>
    private static (CircuitSimulator Sim, SignalProbe Out, OpAmpModel Model) Amplifier(
        double rf, double rg, OpAmpModel? model = null)
    {
        var circuit = new Circuit();

        var positive = circuit.Add(new DcVoltageSource(15.0) { Name = "VP" });
        var negative = circuit.Add(new DcVoltageSource(-15.0) { Name = "VN" });
        var drive = circuit.Add(new DcVoltageSource(0.0) { Name = "VIN" });

        var amp = circuit.Add(new OperationalAmplifier { Name = "U1", Model = model ?? OpAmpModel.Lm741 });
        var feedback = circuit.Add(new Resistor(rf) { Name = "RF" });
        var gain = circuit.Add(new Resistor(rg) { Name = "RG" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(positive.Negative, ground.Pin);
        circuit.Connect(negative.Positive, ground.Pin);
        circuit.Connect(amp.PositiveSupply, positive.Positive);
        circuit.Connect(amp.NegativeSupply, negative.Negative);

        circuit.Connect(drive.Negative, ground.Pin);
        circuit.Connect(drive.Positive, amp.NonInverting);

        circuit.Connect(amp.Output, feedback.A);
        circuit.Connect(feedback.B, amp.Inverting);
        circuit.Connect(amp.Inverting, gain.A);
        circuit.Connect(gain.B, ground.Pin);

        var probe = new SignalProbe("Out", amp.Output, default);
        circuit.Probes.Add(probe);

        var sim = new CircuitSimulator(circuit);
        sim.Settings.TemperatureKelvin = Room;
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.ResolveProbes();

        return (sim, probe, amp.Model);
    }

    // ---- the closed form ---------------------------------------------------

    /// <summary>
    /// The amplifier's voltage noise comes out multiplied by the noise gain, 1 + Rf/Rg. With the
    /// resistors small enough that their own Johnson noise is negligible, that is the whole answer
    /// — and it scales with the gain exactly.
    /// </summary>
    [Theory]
    [InlineData(10.0)]
    [InlineData(100.0)]
    public void TheAmplifiersVoltageNoiseComesOutTimesTheNoiseGain(double gain)
    {
        const double rg = 100.0;

        var (sim, probe, model) = Amplifier(rg * (gain - 1), rg);

        // A decade below the closed-loop corner. Above it the noise gain has started following
        // the open-loop gain down, so a measurement there reads low — at the corner exactly, by
        // the 1/√2 the corner is defined as. That is the amplifier running out of gain rather
        // than getting quieter, and it is a different fact from the one this test is about.
        var at = model.GainBandwidthProduct / gain / 10.0;

        var result = new NoiseAnalysis(sim).Run(
            new NoiseRequest(new AcSweepRequest(at, at * 2, 10), probe));

        Assert.True(result.IsUsable, result.Problem);

        var expected = model.VoltageNoiseAt(at) * gain;

        // Within ten percent: the resistors add a little of their own on top, which is the point
        // of keeping them small rather than zero.
        Assert.Equal(expected, result.Density[0], expected * 0.1);
    }

    /// <summary>
    /// And it is the <b>noise</b> gain, not the signal gain. An inverting amplifier of gain −1 has
    /// a signal gain of one and a noise gain of two, which is the classic trap: the same circuit
    /// built inverting is twice as noisy for the same signal.
    /// </summary>
    [Fact]
    public void AnInvertingStageHasANoiseGainOneHigherThanItsSignalGain()
    {
        var circuit = new Circuit();

        var positive = circuit.Add(new DcVoltageSource(15.0));
        var negative = circuit.Add(new DcVoltageSource(-15.0));
        var drive = circuit.Add(new DcVoltageSource(0.0));

        var amp = circuit.Add(new OperationalAmplifier { Name = "U1", Model = OpAmpModel.Lm741 });
        var input = circuit.Add(new Resistor(100.0) { Name = "RIN" });
        var feedback = circuit.Add(new Resistor(100.0) { Name = "RF" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(positive.Negative, ground.Pin);
        circuit.Connect(negative.Positive, ground.Pin);
        circuit.Connect(amp.PositiveSupply, positive.Positive);
        circuit.Connect(amp.NegativeSupply, negative.Negative);

        circuit.Connect(drive.Negative, ground.Pin);
        circuit.Connect(drive.Positive, input.A);
        circuit.Connect(input.B, amp.Inverting);
        circuit.Connect(amp.Inverting, feedback.A);
        circuit.Connect(feedback.B, amp.Output);
        circuit.Connect(amp.NonInverting, ground.Pin);

        var probe = new SignalProbe("Out", amp.Output, default);
        circuit.Probes.Add(probe);

        var sim = new CircuitSimulator(circuit);
        sim.Settings.TemperatureKelvin = Room;
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.ResolveProbes();

        var result = new NoiseAnalysis(sim).Run(
            new NoiseRequest(new AcSweepRequest(1e4, 2e4, 10), probe));

        // Signal gain 1, noise gain 1 + 100/100 = 2.
        var expected = OpAmpModel.Lm741.VoltageNoiseAt(1e4) * 2.0;

        Assert.Equal(expected, result.Density[0], expected * 0.1);
    }

    // ---- flicker -----------------------------------------------------------

    /// <summary>
    /// Below the corner the noise rises as 1/√f, which is what makes a DC-coupled precision
    /// circuit hard. At the corner itself the density is √2 times the white floor, by the
    /// definition of where the corner is.
    /// </summary>
    [Fact]
    public void BelowTheCornerTheNoiseRisesAsOneOverRootF()
    {
        var model = OpAmpModel.Lm741;
        var corner = model.VoltageNoiseCornerHz;

        Assert.Equal(Math.Sqrt(2.0) * model.VoltageNoiseDensity, model.VoltageNoiseAt(corner), 1e-12);

        // A decade below the corner is √10 times noisier than the corner's own 1/f part.
        var low = model.VoltageNoiseAt(corner / 100.0);

        Assert.Equal(10.0, low / model.VoltageNoiseDensity, 0.2);

        // And far above it the white floor is all that is left.
        Assert.Equal(model.VoltageNoiseDensity, model.VoltageNoiseAt(corner * 1000), model.VoltageNoiseDensity * 0.01);
    }

    /// <summary>The rise shows up in a real measurement, not only in the model's arithmetic.</summary>
    [Fact]
    public void AnAmplifierIsNoisierAtOneHertzThanAtTenKilohertz()
    {
        var (sim, probe, _) = Amplifier(9e3, 1e3);

        var result = new NoiseAnalysis(sim).Run(
            new NoiseRequest(new AcSweepRequest(1, 1e5, 20), probe));

        Assert.True(result.IsUsable, result.Problem);

        // The worst point in a band this wide is its bottom end, which is flicker noise.
        var worst = result.Worst();

        Assert.NotNull(worst);
        Assert.True(worst.Value.Frequency < 10,
            $"the worst noise should be at the bottom of the band, not {worst.Value.Frequency:0.#} Hz");

        Assert.True(result.Density[0] > result.Density[^1] * 3,
            $"1 Hz should be far noisier than 100 kHz: {result.Density[0]:E2} against {result.Density[^1]:E2}");
    }

    // ---- which part, and the trade -----------------------------------------

    /// <summary>
    /// The whole point of the ranking, and the trade a feedback network is actually chosen on.
    /// <para>
    /// With small resistors the amplifier's <b>voltage</b> noise is everything — the resistors
    /// make almost none and there is no impedance for the current noise to work into. Make the
    /// same network a ten-thousand times larger and the voltage noise has not changed at all,
    /// while the <b>current</b> noise now has nine hundred kilohms to develop across. Same part,
    /// same gain, and a completely different answer about what to go and fix.
    /// </para>
    /// </summary>
    [Fact]
    public void SmallResistorsMakeItAVoltageNoiseProblemAndLargeOnesACurrentNoiseProblem()
    {
        string Leader(double scale)
        {
            var (sim, probe, _) = Amplifier(9e3 * scale, 1e3 * scale);

            var result = new NoiseAnalysis(sim).Run(
                new NoiseRequest(new AcSweepRequest(1e4, 1e5, 10), probe));

            Assert.True(result.IsUsable, result.Problem);

            return result.Contributors[0].Name;
        }

        Assert.Equal("U1 voltage noise", Leader(0.01));
        Assert.Contains("current noise", Leader(100.0));
    }

    /// <summary>
    /// Current noise is what decides between a bipolar input and a FET one, and it only shows up
    /// against a large source impedance. The LM741's is fifty times the TL081's, so looking at a
    /// megohm the JFET part wins — and it is the current-noise generator that says why.
    /// </summary>
    [Fact]
    public void CurrentNoiseIsWhatChoosesBetweenABipolarAndAFetInput()
    {
        double Density(OpAmpModel model)
        {
            var circuit = new Circuit();

            var positive = circuit.Add(new DcVoltageSource(15.0));
            var negative = circuit.Add(new DcVoltageSource(-15.0));
            var drive = circuit.Add(new DcVoltageSource(0.0));

            var amp = circuit.Add(new OperationalAmplifier { Name = "U1", Model = model });
            var feedback = circuit.Add(new Resistor(9e3) { Name = "RF" });
            var gain = circuit.Add(new Resistor(1e3) { Name = "RG" });

            // A megohm in series with the non-inverting input: the classic high-impedance source,
            // and the only place a part's current noise has room to show itself.
            var source = circuit.Add(new Resistor(1e6) { Name = "RSRC" });
            var ground = circuit.Add(new Ground());

            circuit.Connect(positive.Negative, ground.Pin);
            circuit.Connect(negative.Positive, ground.Pin);
            circuit.Connect(amp.PositiveSupply, positive.Positive);
            circuit.Connect(amp.NegativeSupply, negative.Negative);

            circuit.Connect(drive.Negative, ground.Pin);
            circuit.Connect(drive.Positive, source.A);
            circuit.Connect(source.B, amp.NonInverting);

            circuit.Connect(amp.Output, feedback.A);
            circuit.Connect(feedback.B, amp.Inverting);
            circuit.Connect(amp.Inverting, gain.A);
            circuit.Connect(gain.B, ground.Pin);

            var probe = new SignalProbe("Out", amp.Output, default);
            circuit.Probes.Add(probe);

            var sim = new CircuitSimulator(circuit);
            sim.Settings.TemperatureKelvin = Room;
            sim.Reset();
            sim.SolveOperatingPoint();
            sim.ResolveProbes();

            return new NoiseAnalysis(sim).Run(
                new NoiseRequest(new AcSweepRequest(1e4, 1e5, 10), probe)).Density[0];
        }

        var bipolar = Density(OpAmpModel.Lm741);
        var jfet = Density(OpAmpModel.Tl081);

        Assert.True(jfet < bipolar,
            $"looking at a megohm the JFET part should be quieter: {jfet:E2} against {bipolar:E2}");
    }
}
