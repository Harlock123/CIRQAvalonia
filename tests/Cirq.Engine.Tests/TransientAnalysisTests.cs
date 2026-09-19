using Cirq.Core.Simulation;
using Cirq.Engine.Simulation;
using Cirq.Engine.Tests.Support;

namespace Cirq.Engine.Tests;

/// <summary>
/// Accuracy tests that measure the transient solver against closed-form solutions rather than
/// against previously recorded output.
/// </summary>
public class TransientAnalysisTests
{
    private const double R = 1e3;
    private const double C = 1e-6;
    private const double Tau = R * C;      // 1 ms
    private const double V0 = 5.0;

    private static SimulationSettings StepSettings(IntegrationMethod method, double dt) => new()
    {
        TimeStep = dt,
        MaxTimeStep = dt,
        Integration = method,
        // Start from a discharged capacitor so the source is a true step at t = 0.
        UseInitialConditions = true,
    };

    [Theory]
    [InlineData(IntegrationMethod.Trapezoidal, 1e-6, 1e-4)]
    [InlineData(IntegrationMethod.BackwardEuler, 1e-6, 2e-3)]
    public void RcStepResponseMatchesAnalyticalSolution(IntegrationMethod method, double dt, double tolerance)
    {
        var (circuit, output, _, _, _) = CircuitFactory.RcLowPass(R, C, V0);
        var sim = new CircuitSimulator(circuit, StepSettings(method, dt));

        sim.Reset();
        sim.SolveOperatingPoint();

        var worstError = 0.0;
        while (sim.Time < 5 * Tau)
        {
            sim.Step();
            var expected = V0 * (1.0 - Math.Exp(-sim.Time / Tau));
            worstError = Math.Max(worstError, Math.Abs(sim.NodeVoltage(output) - expected));
        }

        Assert.True(worstError < tolerance,
            $"{method} peak error {worstError:g4} V exceeded the {tolerance:g4} V budget.");
    }

    [Fact]
    public void RcStepResponseHitsTheKnownTimeConstantLandmarks()
    {
        var (circuit, output, _, _, _) = CircuitFactory.RcLowPass(R, C, V0);
        var sim = new CircuitSimulator(circuit, StepSettings(IntegrationMethod.Trapezoidal, 1e-7));

        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(Tau);

        // One time constant charges a capacitor to 63.2% of the supply.
        Assert.Equal(V0 * 0.632, sim.NodeVoltage(output), 0.005);

        sim.Run(4 * Tau);
        // Five time constants is the usual "fully charged" engineering rule, 99.3%.
        Assert.Equal(V0 * 0.993, sim.NodeVoltage(output), 0.01);
    }

    [Fact]
    public void TrapezoidalIsMoreAccurateThanBackwardEulerAtTheSameStep()
    {
        static double PeakError(IntegrationMethod method)
        {
            var (circuit, output, _, _, _) = CircuitFactory.RcLowPass(R, C, V0);
            var sim = new CircuitSimulator(circuit, StepSettings(method, 2e-5));
            sim.Reset();
            sim.SolveOperatingPoint();

            var worst = 0.0;
            while (sim.Time < 3 * Tau)
            {
                sim.Step();
                worst = Math.Max(worst, Math.Abs(sim.NodeVoltage(output) - V0 * (1.0 - Math.Exp(-sim.Time / Tau))));
            }
            return worst;
        }

        var trapezoidal = PeakError(IntegrationMethod.Trapezoidal);
        var backwardEuler = PeakError(IntegrationMethod.BackwardEuler);

        Assert.True(trapezoidal < backwardEuler * 0.25,
            $"Expected second-order accuracy, but trapezoidal error {trapezoidal:g3} was not well below Euler's {backwardEuler:g3}.");
    }

    [Fact]
    public void HalvingTheTimeStepQuartersTheTrapezoidalError()
    {
        static double PeakError(double dt)
        {
            var (circuit, output, _, _, _) = CircuitFactory.RcLowPass(R, C, V0);
            var sim = new CircuitSimulator(circuit, StepSettings(IntegrationMethod.Trapezoidal, dt));
            sim.Reset();
            sim.SolveOperatingPoint();

            var worst = 0.0;
            while (sim.Time < 2 * Tau)
            {
                sim.Step();
                worst = Math.Max(worst, Math.Abs(sim.NodeVoltage(output) - V0 * (1.0 - Math.Exp(-sim.Time / Tau))));
            }
            return worst;
        }

        var coarse = PeakError(4e-5);
        var fine = PeakError(2e-5);

        // Second-order convergence: the ratio should be near 4. The first Backward-Euler
        // start-up step keeps it from being exact, so the window is generous.
        var ratio = coarse / fine;
        Assert.InRange(ratio, 2.5, 6.0);
    }

    [Fact]
    public void RcDischargeDecaysExponentially()
    {
        var (circuit, output, source, _, _) = CircuitFactory.RcLowPass(R, C, V0);
        var sim = new CircuitSimulator(circuit, StepSettings(IntegrationMethod.Trapezoidal, 1e-6));

        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(10 * Tau);
        var charged = sim.NodeVoltage(output);
        Assert.Equal(V0, charged, 0.01);

        // Collapse the supply to zero: the capacitor now discharges through R.
        source.Voltage = 0;
        var startTime = sim.Time;
        sim.Run(Tau);

        var expected = charged * Math.Exp(-(sim.Time - startTime) / Tau);
        Assert.Equal(expected, sim.NodeVoltage(output), 0.01);
    }

    [Fact]
    public void RlCurrentRisesWithTheExpectedTimeConstant()
    {
        const double resistance = 100.0;
        const double inductance = 10e-3;
        const double supply = 5.0;
        var tau = inductance / resistance;   // 100 us

        var (circuit, _, inductor, _) = CircuitFactory.RlSeries(resistance, inductance, supply);
        var sim = new CircuitSimulator(circuit, StepSettings(IntegrationMethod.Trapezoidal, 1e-7));

        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(tau);

        var final = supply / resistance;
        Assert.Equal(final * 0.632, Math.Abs(inductor.Current), final * 0.01);

        sim.Run(9 * tau);
        Assert.Equal(final, Math.Abs(inductor.Current), final * 0.01);
    }

    [Fact]
    public void SeriesRlcRingsAtItsNaturalFrequency()
    {
        const double l = 1e-3;
        const double c = 1e-6;
        const double r = 5.0;              // Lightly damped.
        var expectedHz = 1.0 / (2 * Math.PI * Math.Sqrt(l * c));   // ~5033 Hz

        var circuit = new Cirq.Core.Topology.Circuit();
        var source = circuit.Add(new Cirq.Components.Sources.DcVoltageSource(1.0));
        var res = circuit.Add(new Cirq.Components.Passive.Resistor(r));
        var ind = circuit.Add(new Cirq.Components.Passive.Inductor(l));
        var cap = circuit.Add(new Cirq.Components.Passive.Capacitor(c));
        var gnd = circuit.Add(new Cirq.Components.Sources.Ground());

        circuit.Connect(source.Positive, res.A);
        circuit.Connect(res.B, ind.A);
        circuit.Connect(ind.B, cap.A);
        circuit.Connect(cap.B, gnd.Pin);
        circuit.Connect(source.Negative, gnd.Pin);

        var sim = new CircuitSimulator(circuit, StepSettings(IntegrationMethod.Trapezoidal, 1e-7));
        sim.Reset();
        sim.SolveOperatingPoint();

        // Count zero crossings of the capacitor current to recover the ringing frequency.
        var crossings = 0;
        var previous = 0.0;
        var firstCrossing = 0.0;
        var lastCrossing = 0.0;

        while (sim.Time < 2e-3)
        {
            sim.Step();
            var current = cap.Current;
            if (previous != 0 && Math.Sign(current) != Math.Sign(previous))
            {
                crossings++;
                if (crossings == 1) firstCrossing = sim.Time;
                lastCrossing = sim.Time;
            }
            previous = current;
        }

        Assert.True(crossings >= 4, $"Expected a ringing response, saw {crossings} zero crossings.");
        // Consecutive zero crossings are half a period apart.
        var measuredHz = (crossings - 1) / (2.0 * (lastCrossing - firstCrossing));
        Assert.Equal(expectedHz, measuredHz, expectedHz * 0.05);
    }
}
