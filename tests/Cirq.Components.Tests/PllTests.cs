using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// A 4046 closed round a loop filter, which is the only way to find out whether a PLL works: the
/// part on its own does nothing, and everything interesting is in what is wired round it.
/// </summary>
public class PllTests
{
    private sealed record Rig(CircuitSimulator Sim, Ic4046 Pll, FunctionGenerator Signal);

    /// <summary>
    /// The textbook arrangement: comparator II into an RC loop filter, the filter into the VCO's
    /// control pin, and the VCO's own output back to the comparator.
    /// </summary>
    private static Rig Build(
        double signalHz = 20e3,
        double filterResistance = 10e3,
        double filterCapacitance = 100e-9,
        double timingCapacitance = 1e-9,
        double r1 = 10e3,
        bool useComparatorTwo = true)
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());
        var supply = circuit.Add(new DcVoltageSource(5.0));

        var signal = circuit.Add(new FunctionGenerator(Waveform.Square, signalHz, 5.0)
        {
            DcOffset = 2.5, EdgeTime = 20e-9,
        });

        var pll = circuit.Add(new Ic4046
        {
            TimingCapacitance = timingCapacitance,
            SeriesResistance = r1,
            UsesComparatorTwo = useComparatorTwo,
        });

        var filter = circuit.Add(new Resistor(filterResistance));
        var hold = circuit.Add(new Capacitor(filterCapacitance));

        // Somewhere for the control pin to sit while comparator II is in its third state and
        // nothing is driving it at all. Very large, because anything smaller would discharge the
        // filter between corrections and drag the loop down on its own.
        var bias = circuit.Add(new Resistor(100e6));

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(signal.Return, gnd.Pin);
        circuit.Connect(pll.Vcc, supply.Positive);
        circuit.Connect(pll.Gnd, gnd.Pin);
        circuit.Connect(pll.Inhibit, gnd.Pin);

        circuit.Connect(signal.Output, pll.SignalInput);
        circuit.Connect(pll.VcoOut, pll.ComparatorInput);

        circuit.Connect(useComparatorTwo ? pll.ComparatorII : pll.ComparatorI, filter.A);
        circuit.Connect(filter.B, pll.ControlVoltage);
        circuit.Connect(pll.ControlVoltage, hold.A);
        circuit.Connect(hold.B, gnd.Pin);
        circuit.Connect(pll.ControlVoltage, bias.A);
        circuit.Connect(bias.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit,
            new SimulationSettings { TimeStep = 200e-9, MaxTimeStep = 1e-6 });

        sim.Reset();
        sim.SolveOperatingPoint();

        return new Rig(sim, pll, signal);
    }

    /// <summary>The VCO's range comes straight from R1 and C1, as the nomograph has it.</summary>
    [Fact]
    public void TheVcoRangeComesFromItsTimingParts()
    {
        var pll = new Ic4046 { SeriesResistance = 10e3, TimingCapacitance = 1e-9 };

        // 1/(10k x 1nF) = 100 kHz.
        Assert.Equal(100e3, pll.MaximumFrequency, 1.0);

        // No R2, so nothing holds the bottom up and it stops dead.
        Assert.Equal(0.0, pll.MinimumFrequency);

        pll.OffsetResistance = 100e3;
        Assert.Equal(10e3, pll.MinimumFrequency, 1.0);
    }

    /// <summary>
    /// The thing itself: given a signal inside its range, the loop drags the VCO onto it and holds
    /// it there.
    /// </summary>
    [Theory]
    [InlineData(20e3)]
    [InlineData(40e3)]
    [InlineData(60e3)]
    public void TheLoopPullsTheVcoOntoTheSignal(double hertz)
    {
        var rig = Build(signalHz: hertz);

        rig.Sim.Run(20e-3);

        Assert.Equal(hertz, rig.Pll.VcoFrequency, hertz * 0.05);
    }

    /// <summary>And says so on pin 1, which is the only lock indication a 4046 has.</summary>
    [Fact]
    public void AndReportsThatItIsLocked()
    {
        var rig = Build(signalHz: 30e3);

        rig.Sim.Run(20e-3);

        Assert.True(rig.Pll.IsLocked, "comparator II should be sitting in its third state");
    }

    /// <summary>
    /// Lock range: once locked, the loop follows the input as it drifts. Moving the signal well
    /// away from where it captured keeps the VCO with it.
    /// </summary>
    [Fact]
    public void OnceLockedItFollowsTheSignalAsItMoves()
    {
        var rig = Build(signalHz: 30e3);
        rig.Sim.Run(20e-3);

        Assert.Equal(30e3, rig.Pll.VcoFrequency, 30e3 * 0.05);

        rig.Signal.Frequency = 55e3;
        rig.Sim.Run(20e-3);

        Assert.Equal(55e3, rig.Pll.VcoFrequency, 55e3 * 0.05);
    }

    /// <summary>
    /// Asked for a frequency the VCO cannot reach, it does not lock — and what it does instead is
    /// sit at the end of its range, which on a scope looks like a PLL that is working.
    /// </summary>
    [Fact]
    public void AsignalOutsideTheVcoRangeIsNeverCaught()
    {
        // 150 kHz against a VCO that tops out at 100 kHz.
        var rig = Build(signalHz: 150e3);

        rig.Sim.Run(20e-3);

        Assert.True(rig.Pll.VcoFrequency < 110e3,
            $"it cannot reach 150 kHz, yet reports {rig.Pll.VcoFrequency / 1e3:0.0} kHz");

        Assert.False(rig.Pll.IsLocked);
    }

    /// <summary>
    /// The distinction the part exists to teach, and it is a property of the <b>comparator</b>
    /// rather than of PLLs in general.
    /// <para>
    /// Comparator II is a phase-<i>frequency</i> detector: it knows which of the two is faster, not
    /// merely how far apart they are, so it will drag the VCO in from anywhere inside its range.
    /// Its capture range is its lock range.
    /// </para>
    /// </summary>
    [Fact]
    public void ThePhaseFrequencyDetectorCapturesFromAnywhereInRange()
    {
        // A long way from where the VCO starts, and it still gets there.
        var rig = Build(signalHz: 85e3, useComparatorTwo: true);

        rig.Sim.Run(40e-3);

        Assert.Equal(85e3, rig.Pll.VcoFrequency, 85e3 * 0.05);
    }

    /// <summary>
    /// Comparator I is an exclusive-OR, and it knows only phase. Out of lock its output is a beat
    /// note that the filter averages to mid-rail, so the VCO sits in the middle of its range and
    /// never finds the signal — while the same loop, walked onto the same frequency, holds it
    /// perfectly.
    /// <para>
    /// That is a capture range narrower than the lock range, and it is why a PLL that will not
    /// start is so often not faulty.
    /// </para>
    /// </summary>
    [Fact]
    public void TheXorDetectorOnlyCapturesWhatIsAlreadyClose()
    {
        // Dropped straight onto 65 kHz, it never finds it: the beat note is too fast for the
        // filter to average into a useful pull, so the VCO sits where it started.
        var cold = Build(signalHz: 65e3, useComparatorTwo: false);
        cold.Sim.Run(40e-3);

        Assert.True(Math.Abs(cold.Pll.VcoFrequency - 65e3) > 65e3 * 0.1,
            $"it should not have captured from cold, yet reached {cold.Pll.VcoFrequency / 1e3:0.0} kHz");

        // The same loop, on the same frequency, walked up in steps small enough to stay captured.
        // Three kilohertz at a time works and five does not, which is the capture range being
        // measured rather than asserted.
        var walked = Build(signalHz: 50e3, useComparatorTwo: false);
        walked.Sim.Run(30e-3);

        foreach (var step in new[] { 53e3, 56e3, 59e3, 62e3, 65e3 })
        {
            walked.Signal.Frequency = step;
            walked.Sim.Run(20e-3);
        }

        Assert.Equal(65e3, walked.Pll.VcoFrequency, 65e3 * 0.08);
    }

    /// <summary>Holding the inhibit pin high stops the oscillator, which is what it is for.</summary>
    [Fact]
    public void InhibitStopsTheOscillator()
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var pll = circuit.Add(new Ic4046 { SeriesResistance = 10e3, TimingCapacitance = 1e-9 });
        var bias = circuit.Add(new Resistor(1e3));

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(pll.Vcc, supply.Positive);
        circuit.Connect(pll.Gnd, gnd.Pin);
        circuit.Connect(pll.Inhibit, supply.Positive);
        circuit.Connect(pll.ControlVoltage, bias.A);
        circuit.Connect(bias.B, supply.Positive);
        circuit.Connect(pll.SignalInput, gnd.Pin);
        circuit.Connect(pll.ComparatorInput, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1e-3);

        Assert.Equal(0.0, pll.VcoFrequency);
    }
}
