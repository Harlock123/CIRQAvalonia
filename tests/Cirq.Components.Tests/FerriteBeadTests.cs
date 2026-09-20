using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The bead, and the thing that makes it not an inductor. The impedance figures come from the
/// parallel RLC the datasheets give; the ringing comparison comes from running the same filter
/// twice with one part swapped.
/// </summary>
public class FerriteBeadTests
{
    /// <summary>At DC it is a piece of wire, which is why one can sit in a supply rail at all.</summary>
    [Fact]
    public void AtDcItIsJustWire()
    {
        var bead = new FerriteBead(600);

        Assert.True(bead.ImpedanceAt(1.0) < 0.1,
            $"a bead should be nothing at DC, not {bead.ImpedanceAt(1.0):0.###} Ω");
    }

    /// <summary>
    /// And the number on the datasheet is the impedance at one frequency, not a property of the
    /// part. This is the single most common way of fitting a bead that does nothing.
    /// </summary>
    [Fact]
    public void TheDatasheetNumberIsOnlyTrueAtTheDatasheetFrequency()
    {
        var bead = new FerriteBead(600) { PeakFrequency = 100e6 };

        Assert.Equal(600.0, bead.ImpedanceAt(100e6), 30.0);

        // A megahertz of noise sees less than a twentieth of what the label claims.
        Assert.True(bead.ImpedanceAt(1e6) < 30.0,
            $"at 1 MHz it is {bead.ImpedanceAt(1e6):0.#} Ω, not 600");
    }

    /// <summary>Past its band the stray capacitance shorts it out and it stops working again.</summary>
    [Fact]
    public void AboveItsBandItGivesUp()
    {
        var bead = new FerriteBead(600) { PeakFrequency = 100e6 };

        Assert.True(bead.ImpedanceAt(3e9) < bead.ImpedanceAt(100e6) / 4.0,
            "a bead should fall away above its peak, not hold up for ever");
    }

    [Fact]
    public void TheImpedanceRisesToThePeakAndFallsAway()
    {
        var bead = new FerriteBead(600) { PeakFrequency = 100e6 };

        var below = bead.ImpedanceAt(10e6);
        var peak = bead.ImpedanceAt(100e6);
        var above = bead.ImpedanceAt(1e9);

        Assert.True(below < peak, "it should be climbing below the peak");
        Assert.True(above < peak, "and falling above it");
    }

    /// <summary>
    /// Where the line between an inductor and a bead actually is. Below its band the reactance
    /// dominates, and a bead is simply a small inductor storing energy and handing it back; inside
    /// its band the resistance dominates and it absorbs instead. That crossover is the whole of
    /// what "the band it works in" means.
    /// </summary>
    [Fact]
    public void ItIsAnInductorBelowItsBandAndAResistorInsideIt()
    {
        var bead = new FerriteBead(600) { PeakFrequency = 100e6 };

        Assert.True(bead.ReactanceAt(1e6) > bead.ResistanceAt(1e6) * 5,
            "a megahertz below its band it is reactive, which is to say an inductor");

        Assert.True(bead.ResistanceAt(100e6) > Math.Abs(bead.ReactanceAt(100e6)) * 5,
            "at its peak it is resistive, which is to say it is absorbing");
    }

    /// <summary>
    /// And the consequence people get wrong. A bead only damps ringing that is <b>in its band</b>.
    /// Put one in front of a hundred nanofarads and the pair resonate at a few hundred kilohertz,
    /// where the bead is still an inductor and contributes no loss at all — so it rings exactly as
    /// an ordinary inductor would, and "just fit a bead" has achieved nothing.
    /// </summary>
    [Fact]
    public void AgainstALargeCapacitorItRingsJustLikeAnInductor()
    {
        var bead = new FerriteBead(600) { PeakFrequency = 100e6 };

        var withInductor = PeakAfterStep(new Inductor(bead.Inductance), 100e-9, 20e-9, 4e-6);
        var withBead = PeakAfterStep(bead, 100e-9, 20e-9, 4e-6);

        Assert.True(withInductor > 2.3, $"the LC should overshoot, not stop at {withInductor:0.00} V");
        Assert.Equal(withInductor, withBead, 0.15);
    }

    /// <summary>
    /// Used where it is meant to be — against the few picofarads of a fast logic input, ringing up
    /// near its peak — the loss is real and the overshoot goes away.
    /// </summary>
    [Fact]
    public void InsideItsBandItDampsWhereAnInductorRings()
    {
        var bead = new FerriteBead(600) { PeakFrequency = 100e6 };

        var withInductor = PeakAfterStep(new Inductor(bead.Inductance), 3e-12, 20e-12, 40e-9);
        var withBead = PeakAfterStep(bead, 3e-12, 20e-12, 40e-9);

        Assert.True(withInductor > 2.5,
            $"an inductor into 3 pF should ring hard, not stop at {withInductor:0.00} V");

        Assert.True(withBead < withInductor - 0.3,
            $"the bead should absorb it: {withBead:0.00} V against the inductor's {withInductor:0.00} V");
    }

    /// <summary>
    /// The highest the far side of a series part gets after a 2 V step into a capacitor — which is
    /// the overshoot, and so how much the pair is ringing.
    /// </summary>
    private static double PeakAfterStep(CircuitComponent series, double load, double step, double duration)
    {
        var (a, b) = series switch
        {
            FerriteBead bead => (bead.A, bead.B),
            Inductor inductor => (inductor.A, inductor.B),
            _ => throw new ArgumentException("not a two-terminal series part", nameof(series)),
        };

        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());

        var source = circuit.Add(new FunctionGenerator(Waveform.Square, 1.0 / (duration * 4), 2.0)
        {
            DcOffset = 1.0, EdgeTime = step / 10, OutputResistance = 1.0,
        });

        var capacitor = circuit.Add(new Capacitor(load));
        var bleed = circuit.Add(new Resistor(100e3));

        circuit.Add(series);
        circuit.Connect(source.Return, gnd.Pin);
        circuit.Connect(source.Output, a);
        circuit.Connect(b, capacitor.A);
        circuit.Connect(capacitor.B, gnd.Pin);
        circuit.Connect(b, bleed.A);
        circuit.Connect(bleed.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit,
            new SimulationSettings { TimeStep = step, MaxTimeStep = step });

        sim.Reset();
        sim.SolveOperatingPoint();

        var highest = 0.0;
        while (sim.Time < duration)
        {
            sim.Step();
            highest = Math.Max(highest, sim.NodeVoltage(b));
        }

        return highest;
    }

    /// <summary>It passes supply current without dropping anything worth having.</summary>
    [Fact]
    public void ItDropsAlmostNothingAtDc()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());
        var bead = circuit.Add(new FerriteBead(600) { DcResistance = 0.05 });
        var load = circuit.Add(new Resistor(50.0));

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, bead.A);
        circuit.Connect(bead.B, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        // 100 mA through 50 mΩ is five millivolts, and the load sees the rest.
        Assert.Equal(4.995, sim.NodeVoltage(bead.B), 0.001);
        Assert.Equal(0.0999, bead.Current, 0.001);
    }
}
