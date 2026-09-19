using Cirq.Components.Ics;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class Ne555Tests
{
    /// <summary>
    /// The textbook astable: R1 from Vcc to DISCH, R2 from DISCH to THRES/TRIG, and C from there
    /// to ground. Oscillates at 1 / (ln2 · (R1 + 2·R2) · C).
    /// </summary>
    private static (CircuitSimulator Sim, Ne555 Timer, Capacitor C)
        Astable(double r1, double r2, double c, double supply = 9.0)
    {
        var circuit = new Circuit { Title = "555 astable" };
        var vcc = circuit.Add(new DcVoltageSource(supply));
        var gnd = circuit.Add(new Ground());
        var timer = circuit.Add(new Ne555());
        var ra = circuit.Add(new Resistor(r1));
        var rb = circuit.Add(new Resistor(r2));
        var cap = circuit.Add(new Capacitor(c));
        var control = circuit.Add(new Capacitor(10e-9));   // The usual 10 nF CTRL decoupling cap.

        circuit.Connect(vcc.Negative, gnd.Pin);
        circuit.Connect(timer.Vcc, vcc.Positive);
        circuit.Connect(timer.Gnd, gnd.Pin);
        circuit.Connect(timer.Reset, vcc.Positive);       // Reset held inactive.

        circuit.Connect(ra.A, vcc.Positive);
        circuit.Connect(ra.B, timer.Discharge);
        circuit.Connect(rb.A, timer.Discharge);
        circuit.Connect(rb.B, cap.A);
        circuit.Connect(cap.B, gnd.Pin);

        circuit.Connect(timer.Threshold, cap.A);
        circuit.Connect(timer.Trigger, cap.A);

        circuit.Connect(control.A, timer.Control);
        circuit.Connect(control.B, gnd.Pin);

        var period = Math.Log(2) * (r1 + 2 * r2) * c;
        var settings = new SimulationSettings
        {
            TimeStep = period / 2000,
            MaxTimeStep = period / 2000,
            UseInitialConditions = true,   // Start from a discharged timing capacitor.
        };

        return (new CircuitSimulator(circuit, settings), timer, cap);
    }

    /// <summary>Measures the output frequency and duty cycle by timing the rising/falling edges.</summary>
    private static (double Frequency, double Duty, int Cycles) MeasureOutput(
        CircuitSimulator sim, Ne555 timer, double duration)
    {
        var risingEdges = new List<double>();
        var fallingEdges = new List<double>();
        var wasHigh = false;

        while (sim.Time < duration)
        {
            sim.Step();
            var isHigh = sim.NodeVoltage(timer.Out) > 2.0;
            if (isHigh && !wasHigh) risingEdges.Add(sim.Time);
            if (!isHigh && wasHigh) fallingEdges.Add(sim.Time);
            wasHigh = isHigh;
        }

        if (risingEdges.Count < 3) return (0, 0, risingEdges.Count);

        // Ignore the first cycle: it starts from a fully discharged capacitor and so is longer.
        var period = (risingEdges[^1] - risingEdges[1]) / (risingEdges.Count - 2);

        var highTimes = new List<double>();
        foreach (var rise in risingEdges.Skip(1))
        {
            var fall = fallingEdges.FirstOrDefault(f => f > rise, double.NaN);
            if (!double.IsNaN(fall)) highTimes.Add(fall - rise);
        }

        var duty = highTimes.Count > 0 ? highTimes.Average() / period : 0;
        return (1.0 / period, duty, risingEdges.Count);
    }

    [Fact]
    public void AstableMultivibratorOscillatesAtItsDesignFrequency()
    {
        // R1 = 10k, R2 = 10k, C = 100 nF gives 1.44 / (30k * 100n) ~= 481 Hz.
        const double r1 = 10e3, r2 = 10e3, c = 100e-9;
        var expected = Ne555.AstableFrequency(r1, r2, c);

        var (sim, timer, _) = Astable(r1, r2, c);
        sim.Reset();
        sim.SolveOperatingPoint();

        var (frequency, duty, cycles) = MeasureOutput(sim, timer, 20.0 / expected);

        Assert.True(cycles >= 5, $"Expected a free-running oscillator, saw {cycles} output pulses.");
        Assert.Equal(expected, frequency, expected * 0.05);

        // Duty = (R1 + R2) / (R1 + 2*R2) = 2/3 for equal resistors.
        Assert.Equal(Ne555.AstableDutyCycle(r1, r2), duty, 0.05);
    }

    [Theory]
    [InlineData(10e3, 10e3, 100e-9)]
    [InlineData(4.7e3, 47e3, 10e-9)]
    [InlineData(100e3, 100e3, 10e-9)]
    [InlineData(1e3, 10e3, 1e-6)]
    public void AstableFrequencyTracksTheTimingComponents(double r1, double r2, double c)
    {
        var expected = Ne555.AstableFrequency(r1, r2, c);
        var (sim, timer, _) = Astable(r1, r2, c);
        sim.Reset();
        sim.SolveOperatingPoint();

        var (frequency, _, cycles) = MeasureOutput(sim, timer, 15.0 / expected);

        Assert.True(cycles >= 5, $"Only saw {cycles} pulses for R1={r1}, R2={r2}, C={c}.");
        Assert.Equal(expected, frequency, expected * 0.06);
    }

    [Fact]
    public void FrequencyIsIndependentOfTheSupplyVoltage()
    {
        // Both comparator thresholds scale with Vcc, so the timing cancels out. That is the
        // property the internal divider exists to provide, so it is worth pinning down.
        const double r1 = 10e3, r2 = 10e3, c = 100e-9;
        var expected = Ne555.AstableFrequency(r1, r2, c);

        foreach (var supply in new[] { 5.0, 9.0, 15.0 })
        {
            var (sim, timer, _) = Astable(r1, r2, c, supply);
            sim.Reset();
            sim.SolveOperatingPoint();
            var (frequency, _, cycles) = MeasureOutput(sim, timer, 15.0 / expected);

            Assert.True(cycles >= 5, $"No oscillation at Vcc = {supply} V.");
            Assert.Equal(expected, frequency, expected * 0.06);
        }
    }

    [Fact]
    public void TimingCapacitorRampsBetweenTheOneThirdAndTwoThirdsThresholds()
    {
        const double supply = 9.0;
        var (sim, _, cap) = Astable(10e3, 10e3, 100e-9, supply);
        sim.Reset();
        sim.SolveOperatingPoint();

        // Skip the first charge from zero, then track the capacitor's excursion.
        sim.Run(3.0 / Ne555.AstableFrequency(10e3, 10e3, 100e-9));

        var min = double.MaxValue;
        var max = double.MinValue;
        var end = sim.Time + 5.0 / Ne555.AstableFrequency(10e3, 10e3, 100e-9);
        while (sim.Time < end)
        {
            sim.Step();
            var v = sim.NodeVoltage(cap.A);
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }

        Assert.Equal(supply / 3.0, min, supply * 0.05);
        Assert.Equal(2.0 * supply / 3.0, max, supply * 0.05);
    }

    [Fact]
    public void ResetPinHoldsTheOutputLow()
    {
        const double r1 = 10e3, r2 = 10e3, c = 100e-9;
        var circuit = new Circuit();
        var vcc = circuit.Add(new DcVoltageSource(9.0));
        var gnd = circuit.Add(new Ground());
        var timer = circuit.Add(new Ne555());
        var ra = circuit.Add(new Resistor(r1));
        var rb = circuit.Add(new Resistor(r2));
        var cap = circuit.Add(new Capacitor(c));

        circuit.Connect(vcc.Negative, gnd.Pin);
        circuit.Connect(timer.Vcc, vcc.Positive);
        circuit.Connect(timer.Gnd, gnd.Pin);
        circuit.Connect(timer.Reset, gnd.Pin);        // Reset asserted.
        circuit.Connect(ra.A, vcc.Positive);
        circuit.Connect(ra.B, timer.Discharge);
        circuit.Connect(rb.A, timer.Discharge);
        circuit.Connect(rb.B, cap.A);
        circuit.Connect(cap.B, gnd.Pin);
        circuit.Connect(timer.Threshold, cap.A);
        circuit.Connect(timer.Trigger, cap.A);

        var period = Math.Log(2) * (r1 + 2 * r2) * c;
        var sim = new CircuitSimulator(circuit, new SimulationSettings
        {
            TimeStep = period / 500,
            MaxTimeStep = period / 500,
            UseInitialConditions = true,
        });

        sim.Reset();
        sim.SolveOperatingPoint();

        var highest = 0.0;
        while (sim.Time < 5 * period)
        {
            sim.Step();
            highest = Math.Max(highest, sim.NodeVoltage(timer.Out));
        }

        Assert.True(highest < 1.0, $"Output reached {highest:0.##} V while RESET was held low.");
        Assert.False(timer.IsOutputHigh);
    }

    [Fact]
    public void AnUnpoweredTimerDrivesNothing()
    {
        var (sim, timer, _) = Astable(10e3, 10e3, 100e-9, supply: 0.5);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1e-3);

        Assert.False(timer.IsOutputHigh);
        Assert.True(Math.Abs(sim.NodeVoltage(timer.Out)) < 0.5);
    }
}
