using Cirq.Components.Ics;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// A circuit's natural frequencies, against the ones that can be written down.
/// <para>
/// An RC has a pole at −1/RC and nowhere else. A series RLC has a conjugate pair at
/// <c>ω₀ = 1/√(LC)</c> with <c>Q = (1/R)√(L/C)</c>. A high-pass has a zero at the origin. Every
/// figure here is checked against one of those rather than against a previous run, because a
/// pole-zero solver that is subtly wrong returns numbers that look exactly as reasonable as the
/// right ones.
/// </para>
/// </summary>
public class PoleZeroTests
{
    private static CircuitSimulator Solved(Circuit circuit)
    {
        var sim = new CircuitSimulator(circuit);

        sim.Reset();
        sim.SolveOperatingPoint();
        sim.ResolveProbes();

        return sim;
    }

    private static PoleZeroResult Run(CircuitSimulator sim, PoleZeroRequest? request = null) =>
        new PoleZeroAnalysis(sim).Run(request ?? new PoleZeroRequest());

    /// <summary>An RC low-pass has exactly one pole, on the real axis at −1/RC.</summary>
    [Fact]
    public void AnRcHasOnePoleAtMinusOneOverRc()
    {
        var circuit = new Circuit();

        var source = circuit.Add(new DcVoltageSource(0) { Name = "V1", AcMagnitude = 1.0 });
        var resistor = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var capacitor = circuit.Add(new Capacitor(100e-9) { Name = "C1" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(source.Negative, ground.Pin);
        circuit.Connect(source.Positive, resistor.A);
        circuit.Connect(resistor.B, capacitor.A);
        circuit.Connect(capacitor.B, ground.Pin);

        var result = Run(Solved(circuit));

        Assert.True(result.IsUsable);

        var pole = Assert.Single(result.Poles);

        Assert.Equal(-1.0 / (1e3 * 100e-9), pole.S.Real, 1e-3);
        Assert.Equal(0.0, pole.S.Imaginary, 1e-6);
        Assert.False(pole.IsOscillatory);
        Assert.True(result.IsStable);

        // The same number the corner of its Bode plot is at.
        Assert.Equal(1.0 / (2.0 * Math.PI * 1e3 * 100e-9), pole.Hertz, 1e-6);

        // And the time constant a step response would show.
        Assert.Equal(1e3 * 100e-9, pole.TimeConstant!.Value, 1e-12);
    }

    /// <summary>
    /// A series RLC rings at <c>1/2π√(LC)</c> with a Q of <c>(1/R)√(L/C)</c> — the two numbers
    /// every tuned circuit is specified by, and both of them properties of the poles rather than
    /// of anything it was driven with.
    /// </summary>
    [Fact]
    public void ASeriesRlcRingsAtItsOwnFrequencyAndQ()
    {
        var circuit = new Circuit();

        var source = circuit.Add(new DcVoltageSource(0) { Name = "V1", AcMagnitude = 1.0 });
        var resistor = circuit.Add(new Resistor(10.0) { Name = "R1" });
        var inductor = circuit.Add(new Inductor(100e-6) { Name = "L1", SeriesResistance = 0 });
        var capacitor = circuit.Add(new Capacitor(1e-9) { Name = "C1" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(source.Negative, ground.Pin);
        circuit.Connect(source.Positive, resistor.A);
        circuit.Connect(resistor.B, inductor.A);
        circuit.Connect(inductor.B, capacitor.A);
        circuit.Connect(capacitor.B, ground.Pin);

        var result = Run(Solved(circuit));

        Assert.True(result.IsUsable);
        Assert.True(result.IsStable);

        var pair = result.Poles.Where(p => p.IsOscillatory).ToList();

        Assert.Equal(2, pair.Count);
        Assert.Equal(pair[0].S.Real, pair[1].S.Real, Math.Abs(pair[0].S.Real) * 1e-9);
        Assert.Equal(pair[0].S.Imaginary, -pair[1].S.Imaginary, Math.Abs(pair[0].S.Imaginary) * 1e-9);

        var natural = 1.0 / (2.0 * Math.PI * Math.Sqrt(100e-6 * 1e-9));
        var q = Math.Sqrt(100e-6 / 1e-9) / 10.0;

        Assert.Equal(natural, pair[0].Hertz, natural * 1e-6);
        Assert.Equal(q, pair[0].Q!.Value, q * 1e-6);

        // The decay is set by R/2L, which is what the envelope of the ringing falls at.
        Assert.Equal(-10.0 / (2.0 * 100e-6), pair[0].S.Real, 1e-3);
    }

    /// <summary>
    /// Two RC sections hanging off the same source do not interact, because a source is a short
    /// for small signals — so there are two poles and each is its own section's.
    /// </summary>
    [Fact]
    public void TwoIndependentSectionsGiveTwoPoles()
    {
        var circuit = new Circuit();

        var source = circuit.Add(new DcVoltageSource(0) { Name = "V1", AcMagnitude = 1.0 });
        var ground = circuit.Add(new Ground());

        var r1 = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var c1 = circuit.Add(new Capacitor(100e-9) { Name = "C1" });
        var r2 = circuit.Add(new Resistor(10e3) { Name = "R2" });
        var c2 = circuit.Add(new Capacitor(1e-6) { Name = "C2" });

        circuit.Connect(source.Negative, ground.Pin);
        circuit.Connect(source.Positive, r1.A);
        circuit.Connect(r1.B, c1.A);
        circuit.Connect(c1.B, ground.Pin);
        circuit.Connect(source.Positive, r2.A);
        circuit.Connect(r2.B, c2.A);
        circuit.Connect(c2.B, ground.Pin);

        var poles = Run(Solved(circuit)).Poles.OrderBy(p => p.S.Real).ToList();

        Assert.Equal(2, poles.Count);
        Assert.Equal(-1.0 / (1e3 * 100e-9), poles[0].S.Real, 1e-3);
        Assert.Equal(-1.0 / (10e3 * 1e-6), poles[1].S.Real, 1e-6);

        // The slow one is what decides how long the whole thing takes to settle.
        Assert.Equal(poles[1].S.Real, Run(Solved(circuit)).Dominant!.S.Real, 1e-6);
    }

    /// <summary>
    /// A high-pass has a pole at −1/RC and a zero at the origin — which is only another way of
    /// saying it passes nothing at DC. The zero at the origin is the one a naive method loses.
    /// </summary>
    [Fact]
    public void AHighPassHasAZeroAtTheOrigin()
    {
        var circuit = new Circuit();

        var source = circuit.Add(new DcVoltageSource(0) { Name = "V1", AcMagnitude = 1.0 });
        var capacitor = circuit.Add(new Capacitor(100e-9) { Name = "C1" });
        var resistor = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(source.Negative, ground.Pin);
        circuit.Connect(source.Positive, capacitor.A);
        circuit.Connect(capacitor.B, resistor.A);
        circuit.Connect(resistor.B, ground.Pin);

        var result = Run(Solved(circuit), new PoleZeroRequest(resistor.A, source));

        Assert.True(result.IsUsable);

        var pole = Assert.Single(result.Poles);
        Assert.Equal(-1.0 / (1e3 * 100e-9), pole.S.Real, 1e-3);

        var zero = Assert.Single(result.Zeros);

        Assert.Equal(0.0, zero.S.Magnitude, 1e-6);
    }

    /// <summary>
    /// A low-pass has no zeros at all, which is worth asserting next to the high-pass: the method
    /// has to be able to say "none" as well as find one.
    /// </summary>
    [Fact]
    public void ALowPassHasNoZeros()
    {
        var circuit = new Circuit();

        var source = circuit.Add(new DcVoltageSource(0) { Name = "V1", AcMagnitude = 1.0 });
        var resistor = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var capacitor = circuit.Add(new Capacitor(100e-9) { Name = "C1" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(source.Negative, ground.Pin);
        circuit.Connect(source.Positive, resistor.A);
        circuit.Connect(resistor.B, capacitor.A);
        circuit.Connect(capacitor.B, ground.Pin);

        var result = Run(Solved(circuit), new PoleZeroRequest(capacitor.A, source));

        Assert.Single(result.Poles);
        Assert.Empty(result.Zeros);
    }

    /// <summary>
    /// A follower with a capacitive load is the classic near-unstable circuit, and its poles say
    /// the same thing the loop sweep does from the other direction: a lightly damped pair, ringing
    /// near where the loop crosses over. Two analyses built on different machinery agreeing is
    /// worth more than either agreeing with itself.
    /// </summary>
    [Fact]
    public void AFollowerWithACapacitiveLoadRingsWhereItsLoopCrossesOver()
    {
        var circuit = new Circuit();

        var positive = circuit.Add(new DcVoltageSource(15.0) { Name = "V1" });
        var negative = circuit.Add(new DcVoltageSource(-15.0) { Name = "V2" });
        var amp = circuit.Add(new OperationalAmplifier { Name = "U1", Model = OpAmpModel.Lm741 });
        var load = circuit.Add(new Capacitor(100e-9) { Name = "C1" });
        var probe = circuit.Add(new LoopProbe { Name = "LP1" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(positive.Negative, ground.Pin);
        circuit.Connect(negative.Positive, ground.Pin);
        circuit.Connect(amp.PositiveSupply, positive.Positive);
        circuit.Connect(amp.NegativeSupply, negative.Negative);

        circuit.Connect(amp.NonInverting, ground.Pin);
        circuit.Connect(amp.Output, probe.A);
        circuit.Connect(probe.B, amp.Inverting);
        circuit.Connect(amp.Output, load.A);
        circuit.Connect(load.B, ground.Pin);

        var sim = Solved(circuit);

        var poles = Run(sim);

        Assert.True(poles.IsUsable);

        var ringing = poles.Poles
            .Where(p => p.IsOscillatory)
            .OrderByDescending(p => p.Q ?? 0)
            .FirstOrDefault();

        Assert.NotNull(ringing);
        Assert.True(ringing.Q > 2, $"expected a lightly damped pair, got Q of {ringing.Q}");

        // The loop sweep, from the other end.
        var stability = new StabilityAnalysis(sim).Run(
            new StabilityRequest(new AcSweepRequest(1, 1e7, 60), probe));

        Assert.NotNull(stability.CrossoverHz);

        // A second-order loop rings at about its crossover, and the two agree within a factor a
        // quarter either way — which is as close as the relationship itself is.
        Assert.InRange(ringing.Hertz, stability.CrossoverHz.Value * 0.75, stability.CrossoverHz.Value * 1.25);

        // And a phase margin of a few degrees is a Q of several, by PM ≈ 100·ζ.
        var impliedQ = 1.0 / (2.0 * (stability.PhaseMarginDegrees!.Value / 100.0));

        Assert.InRange(ringing.Q!.Value, impliedQ * 0.5, impliedQ * 2.0);
    }

    /// <summary>
    /// A delay has infinitely many poles, so a circuit containing one cannot be reduced to a list
    /// of them. It says so rather than returning the poles of something else.
    /// </summary>
    [Fact]
    public void ADelayIsRefusedRatherThanApproximated()
    {
        var circuit = new Circuit();

        var source = circuit.Add(new DcVoltageSource(0) { Name = "V1", AcMagnitude = 1.0 });
        var line = circuit.Add(new TransmissionLine { Name = "T1" });
        var load = circuit.Add(new Resistor(50.0) { Name = "R1" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(source.Negative, ground.Pin);
        circuit.Connect(source.Positive, line.NearPlus);
        circuit.Connect(line.NearMinus, ground.Pin);
        circuit.Connect(line.FarPlus, load.A);
        circuit.Connect(load.B, ground.Pin);
        circuit.Connect(line.FarMinus, ground.Pin);

        var result = Run(Solved(circuit));

        Assert.False(result.IsUsable);
        Assert.Contains("delay", result.Problem, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The numeric backstop, proved to be real rather than decorative: a part that <i>claims</i>
    /// its stamp is a plain G + jωC while behaving otherwise is still caught, because the matrix
    /// is sampled across twelve decades and a curve cannot hide across all of them.
    /// </summary>
    [Fact]
    public void APartThatMisdeclaresItselfIsStillCaught()
    {
        var circuit = new Circuit();

        var source = circuit.Add(new DcVoltageSource(0) { Name = "V1", AcMagnitude = 1.0 });
        var liar = circuit.Add(new DishonestReactance { Name = "X1" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(source.Negative, ground.Pin);
        circuit.Connect(source.Positive, liar.A);
        circuit.Connect(liar.B, ground.Pin);

        var result = Run(Solved(circuit));

        Assert.False(result.IsUsable);
        Assert.Contains("changes shape with frequency", result.Problem);
    }

    /// <summary>
    /// A part whose admittance goes as the <b>square</b> of frequency, and which says nothing
    /// about it. Nothing real behaves this way; it exists to prove the sampling catches what the
    /// declaration would miss.
    /// </summary>
    private sealed class DishonestReactance : Cirq.Components.TwoTerminalComponent
    {
        public override string ComponentType => "Dishonest";

        public override string DesignatorPrefix => "X";

        public override string ValueLabel => "ω²";

        public override void StampMatrix(
            Cirq.Core.Simulation.MnaSystem system, Cirq.Core.Simulation.SimulationState state) =>
            system.StampResistor(system.Node(A), system.Node(B), 1e6);

        public override void StampAc(
            Cirq.Core.Simulation.AcSystem system, Cirq.Core.Simulation.SimulationState state)
        {
            var omega = state.AngularFrequency;

            system.StampAdmittance(
                system.Node(A), system.Node(B), new System.Numerics.Complex(0, 1e-15 * omega * omega));
        }
    }

    /// <summary>A circuit with nothing that stores energy has no modes, and says so plainly.</summary>
    [Fact]
    public void APurelyResistiveCircuitHasNoPoles()
    {
        var circuit = new Circuit();

        var source = circuit.Add(new DcVoltageSource(5.0) { Name = "V1" });
        var top = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var bottom = circuit.Add(new Resistor(1e3) { Name = "R2" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(source.Negative, ground.Pin);
        circuit.Connect(source.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);

        var result = Run(Solved(circuit));

        Assert.True(result.IsUsable);
        Assert.Empty(result.Poles);
        Assert.Contains("nothing here stores energy", result.Verdict);
    }
}
