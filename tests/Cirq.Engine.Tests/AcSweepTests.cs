using Cirq.Components.Ics;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// The small-signal solve, against closed-form answers throughout. A frequency response is the one
/// thing in this simulator with an exact expected value at every point — no integration error, no
/// step size, just algebra — so the tests are written to that rather than to a tolerance band.
/// </summary>
public class AcSweepTests
{
    private static AcSweepResult Sweep(Circuit circuit, AcSweepRequest? request = null)
    {
        var simulator = new CircuitSimulator(circuit);

        simulator.Reset();
        simulator.SolveOperatingPoint();
        simulator.ResolveProbes();

        return new AcSweep(simulator).Run(request ?? new AcSweepRequest(1, 1e6, 40));
    }

    private static (Circuit Circuit, FunctionGenerator Source, Ground Gnd) Rig()
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());
        var source = circuit.Add(new FunctionGenerator());

        circuit.Connect(source.Return, gnd.Pin);

        return (circuit, source, gnd);
    }

    /// <summary>An RC low-pass turns over at 1/(2πRC), and there is no argument about it.</summary>
    [Theory]
    [InlineData(10e3, 100e-9, 159.155)]
    [InlineData(1e3, 1e-6, 159.155)]
    [InlineData(4.7e3, 10e-9, 3386.28)]
    public void AnRcLowPassTurnsOverWhereTheArithmeticSaysItDoes(double ohms, double farads, double expected)
    {
        var (circuit, source, gnd) = Rig();
        var resistor = circuit.Add(new Resistor(ohms));
        var capacitor = circuit.Add(new Capacitor(farads));

        circuit.Connect(source.Output, resistor.A);
        circuit.Connect(resistor.B, capacitor.A);
        circuit.Connect(capacitor.B, gnd.Pin);
        circuit.Probes.Add(new SignalProbe { TargetTerminal = capacitor.A, Label = "Out" });

        var sweep = Sweep(circuit, new AcSweepRequest(expected / 100, expected * 100, 200));

        Assert.Equal(expected, sweep.CornerOf("Out") ?? 0, expected * 0.02);
    }

    /// <summary>
    /// And rolls off at twenty decibels a decade past it, with the phase heading for ninety
    /// degrees of lag — the two halves of a first-order pole.
    /// </summary>
    [Fact]
    public void AndFallsAtTwentyDecibelsADecade()
    {
        var (circuit, source, gnd) = Rig();
        var resistor = circuit.Add(new Resistor(10e3));
        var capacitor = circuit.Add(new Capacitor(100e-9));

        circuit.Connect(source.Output, resistor.A);
        circuit.Connect(resistor.B, capacitor.A);
        circuit.Connect(capacitor.B, gnd.Pin);
        circuit.Probes.Add(new SignalProbe { TargetTerminal = capacitor.A, Label = "Out" });

        var sweep = Sweep(circuit, new AcSweepRequest(1e3, 1e5, 40));
        var trace = sweep.Traces[0];

        var first = trace.Decibels(0);
        var last = trace.Decibels(sweep.Frequencies.Count - 1);

        // Two decades of span, well past the corner.
        Assert.Equal(-40.0, last - first, 0.5);
        Assert.Equal(-90.0, trace.Degrees(sweep.Frequencies.Count - 1), 1.0);
    }

    /// <summary>A high-pass is the same filter read the other way round.</summary>
    [Fact]
    public void AnRcHighPassLeadsInsteadOfLagging()
    {
        var (circuit, source, gnd) = Rig();
        var capacitor = circuit.Add(new Capacitor(100e-9));
        var resistor = circuit.Add(new Resistor(10e3));

        circuit.Connect(source.Output, capacitor.A);
        circuit.Connect(capacitor.B, resistor.A);
        circuit.Connect(resistor.B, gnd.Pin);
        circuit.Probes.Add(new SignalProbe { TargetTerminal = resistor.A, Label = "Out" });

        var sweep = Sweep(circuit, new AcSweepRequest(1, 1e5, 60));
        var trace = sweep.Traces[0];

        Assert.Equal(90.0, trace.Degrees(0), 2.0);
        Assert.Equal(0.0, trace.Decibels(sweep.Frequencies.Count - 1), 0.1);
    }

    /// <summary>
    /// An LC tank peaks at 1/(2π√(LC)), and its height is set by how much resistance is damping
    /// it. The frequency is exact; the peak is the Q.
    /// </summary>
    [Fact]
    public void AnLcTankResonatesWhereItShould()
    {
        var (circuit, source, gnd) = Rig();
        var resistor = circuit.Add(new Resistor(100.0));
        var inductor = circuit.Add(new Inductor(1e-3));
        var capacitor = circuit.Add(new Capacitor(100e-9));

        circuit.Connect(source.Output, resistor.A);
        circuit.Connect(resistor.B, inductor.A);
        circuit.Connect(inductor.B, gnd.Pin);
        circuit.Connect(resistor.B, capacitor.A);
        circuit.Connect(capacitor.B, gnd.Pin);
        circuit.Probes.Add(new SignalProbe { TargetTerminal = capacitor.A, Label = "Tank" });

        var sweep = Sweep(circuit, new AcSweepRequest(1e3, 1e6, 200));
        var trace = sweep.Traces[0];

        var peakAt = 0;
        for (var i = 1; i < sweep.Frequencies.Count; i++)
            if (trace.Decibels(i) > trace.Decibels(peakAt)) peakAt = i;

        // 1/(2*pi*sqrt(1mH * 100nF)) = 15.92 kHz.
        Assert.Equal(15915.5, sweep.Frequencies[peakAt], 15915.5 * 0.02);
    }

    /// <summary>
    /// The op-amp's gain-bandwidth product, which the macromodel has never been asked for
    /// directly. A gain of ten from a 1 MHz part turns over at the product over the noise gain —
    /// 1 MHz / 11, not 1 MHz / 10, which is the distinction that catches people.
    /// </summary>
    [Fact]
    public void AnOpAmpRollsOffAtItsGainBandwidthProduct()
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());
        var pos = circuit.Add(new DcVoltageSource(15));
        var neg = circuit.Add(new DcVoltageSource(15));
        var source = circuit.Add(new FunctionGenerator());
        var amp = circuit.Add(new OperationalAmplifier(OpAmpModel.Lm741));
        var input = circuit.Add(new Resistor(1e3));
        var feedback = circuit.Add(new Resistor(10e3));

        circuit.Connect(pos.Negative, gnd.Pin);
        circuit.Connect(neg.Positive, gnd.Pin);
        circuit.Connect(amp.PositiveSupply, pos.Positive);
        circuit.Connect(amp.NegativeSupply, neg.Negative);
        circuit.Connect(source.Return, gnd.Pin);
        circuit.Connect(source.Output, input.A);
        circuit.Connect(input.B, amp.Inverting);
        circuit.Connect(amp.Inverting, feedback.A);
        circuit.Connect(feedback.B, amp.Output);
        circuit.Connect(amp.NonInverting, gnd.Pin);

        circuit.Probes.Add(new SignalProbe { TargetTerminal = amp.Output, Label = "Out" });

        var sweep = Sweep(circuit, new AcSweepRequest(10, 1e7, 40));
        var trace = sweep.Traces[0];

        // A gain of -10 is 20 dB, whatever sign it arrives with.
        Assert.Equal(20.0, trace.Decibels(0), 0.1);
        Assert.Equal(1e6 / 11.0, sweep.CornerOf("Out") ?? 0, 1e6 / 11.0 * 0.1);
    }

    /// <summary>
    /// A quarter-wave open stub looks like a short circuit at its resonance — the reflection comes
    /// back inverted and cancels what is arriving. A metre of line at 0.66c delays 5.05 ns, so the
    /// quarter wave is at 49.5 MHz. It is the sort of thing that is obvious in this view and
    /// nearly impossible to find in the time domain.
    /// </summary>
    [Fact]
    public void AQuarterWaveStubLooksLikeAShort()
    {
        var (circuit, source, gnd) = Rig();
        source.OutputResistance = 50.0;

        var line = circuit.Add(new TransmissionLine { CharacteristicImpedance = 50, Length = 1.0 });
        var leak = circuit.Add(new Resistor(1e6));

        circuit.Connect(source.Output, line.NearPlus);
        circuit.Connect(line.NearMinus, gnd.Pin);
        circuit.Connect(line.FarMinus, gnd.Pin);
        circuit.Connect(line.FarPlus, leak.A);
        circuit.Connect(leak.B, gnd.Pin);

        circuit.Probes.Add(new SignalProbe { TargetTerminal = line.NearPlus, Label = "Stub" });

        var sweep = Sweep(circuit, new AcSweepRequest(1e6, 3e8, 120));
        var trace = sweep.Traces[0];

        var lowest = 0;
        for (var i = 1; i < sweep.Frequencies.Count; i++)
            if (trace.Decibels(i) < trace.Decibels(lowest)) lowest = i;

        Assert.Equal(49.46e6, sweep.Frequencies[lowest], 49.46e6 * 0.03);
        Assert.True(trace.Decibels(lowest) < -25, "a quarter-wave stub should look like a short");
    }

    /// <summary>
    /// The bead's impedance curve, which is the picture its datasheet prints and the reason the
    /// part is so often misused. Driven through a resistor, the voltage after it follows the
    /// divider, so the dip is where the bead is biggest.
    /// </summary>
    [Fact]
    public void AFerriteBeadPeaksAtItsStatedFrequency()
    {
        var (circuit, source, gnd) = Rig();
        var bead = circuit.Add(new FerriteBead(600) { PeakFrequency = 100e6 });
        var load = circuit.Add(new Resistor(50.0));

        circuit.Connect(source.Output, bead.A);
        circuit.Connect(bead.B, load.A);
        circuit.Connect(load.B, gnd.Pin);

        circuit.Probes.Add(new SignalProbe { TargetTerminal = bead.B, Label = "After" });

        var sweep = Sweep(circuit, new AcSweepRequest(1e5, 1e10, 60));
        var trace = sweep.Traces[0];

        var lowest = 0;
        for (var i = 1; i < sweep.Frequencies.Count; i++)
            if (trace.Decibels(i) < trace.Decibels(lowest)) lowest = i;

        Assert.Equal(100e6, sweep.Frequencies[lowest], 100e6 * 0.2);
    }

    /// <summary>
    /// A supply is a short to a small signal, whatever its DC voltage: only the source given an AC
    /// magnitude drives the sweep. Without that, every rail in a circuit would be injecting its
    /// own volts at every frequency.
    /// </summary>
    [Fact]
    public void ASupplyIsAShortCircuitToASmallSignal()
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());
        var rail = circuit.Add(new DcVoltageSource(12.0));
        var resistor = circuit.Add(new Resistor(1e3));

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, resistor.A);
        circuit.Connect(resistor.B, gnd.Pin);

        circuit.Probes.Add(new SignalProbe { TargetTerminal = rail.Positive, Label = "Rail" });

        var sweep = Sweep(circuit);

        Assert.All(sweep.Traces[0].Response, v => Assert.True(v.Magnitude < 1e-9));
    }

    /// <summary>And a source told to drive one does, whatever its DC value is doing.</summary>
    [Fact]
    public void ASourceGivenAnAcMagnitudeDrivesTheSweep()
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());
        var rail = circuit.Add(new DcVoltageSource(12.0) { AcMagnitude = 0.5 });
        var resistor = circuit.Add(new Resistor(1e3));

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, resistor.A);
        circuit.Connect(resistor.B, gnd.Pin);

        circuit.Probes.Add(new SignalProbe { TargetTerminal = rail.Positive, Label = "Rail" });

        var sweep = Sweep(circuit);

        Assert.All(sweep.Traces[0].Response, v => Assert.Equal(0.5, v.Magnitude, 1e-9));
    }

    /// <summary>
    /// A divider has no frequency response at all, which is worth pinning: it is the case where
    /// every reactance is absent and the answer must be flat rather than nearly flat.
    /// </summary>
    [Fact]
    public void AResistiveDividerIsFlatAcrossTheWholeSpan()
    {
        var (circuit, source, gnd) = Rig();
        var upper = circuit.Add(new Resistor(1e3));
        var lower = circuit.Add(new Resistor(1e3));

        circuit.Connect(source.Output, upper.A);
        circuit.Connect(upper.B, lower.A);
        circuit.Connect(lower.B, gnd.Pin);
        circuit.Probes.Add(new SignalProbe { TargetTerminal = lower.A, Label = "Mid" });

        var sweep = Sweep(circuit, new AcSweepRequest(0.1, 1e9, 20));

        Assert.All(sweep.Traces[0].Response, v => Assert.Equal(0.5, v.Magnitude, 1e-9));
    }

    [Fact]
    public void ADecadeSweepIsSpacedInRatiosAndALinearOneInSteps()
    {
        var decade = new AcSweepRequest(10, 1000, 10).Frequencies();
        var linear = new AcSweepRequest(10, 1000, 11, SweepSpacing.Linear).Frequencies();

        Assert.Equal(10, decade[0], 1e-9);
        Assert.Equal(1000, decade[^1], 1e-6);
        Assert.Equal(100, decade[decade.Count / 2], 1.0);

        Assert.Equal(11, linear.Count);
        Assert.Equal(109.0, linear[1], 1e-9);
    }
}
