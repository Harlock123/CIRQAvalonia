using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The component selects by which way the current is going rather than by frequency, so every
/// test here drives it both ways and compares.
/// </summary>
public class CommonModeChokeTests
{
    /// <summary>
    /// Common mode sees both windings' flux adding; the signal sees them cancelling, leaving only
    /// what the coupling failed to share. On a good choke those two numbers are three orders of
    /// magnitude apart.
    /// </summary>
    [Fact]
    public void TheTwoModesSeeCompletelyDifferentInductances()
    {
        var choke = new CommonModeChoke(1e-3, 0.999);

        Assert.Equal(2e-3, choke.CommonModeInductance, 1e-5);
        Assert.Equal(1e-6, choke.DifferentialInductance, 1e-8);
    }

    /// <summary>And the leakage, which is what the signal actually sees, follows the coupling.</summary>
    [Theory]
    [InlineData(0.999, 1e-6)]
    [InlineData(0.99, 1e-5)]
    [InlineData(0.9, 1e-4)]
    public void TheLeakageIsWhateverTheCouplingMissed(double coupling, double expected)
    {
        var choke = new CommonModeChoke(1e-3, coupling);

        Assert.Equal(expected, choke.DifferentialInductance, expected * 0.01);
    }

    /// <summary>
    /// The picture the component exists for, out of the frequency sweep: at a megahertz it is
    /// kilohms to common-mode current and a fraction of an ohm to the signal.
    /// </summary>
    [Fact]
    public void AtMegahertzItIsKilohmsOneWayAndNothingTheOther()
    {
        var choke = new CommonModeChoke(1e-3, 0.999);

        Assert.True(choke.CommonModeImpedanceAt(1e6) > 10e3,
            $"common mode sees {choke.CommonModeImpedanceAt(1e6):0} ohms");

        Assert.True(choke.DifferentialImpedanceAt(1e6) < 10.0,
            $"the signal sees {choke.DifferentialImpedanceAt(1e6):0.00} ohms");
    }

    private enum Drive { Differential, CommonMode }

    /// <summary>
    /// The choke in a pair, driven either as a signal — one wire out, the other back — or as
    /// common-mode noise, both wires together against ground. Returns how much got through.
    /// </summary>
    private static double Through(Drive mode, double hertz)
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());
        var choke = circuit.Add(new CommonModeChoke(1e-3, 0.999));

        var source = circuit.Add(new FunctionGenerator { OutputResistance = 50.0 });
        var load1 = circuit.Add(new Resistor(50.0));

        circuit.Connect(source.Return, gnd.Pin);

        if (mode == Drive.Differential)
        {
            // Out along one conductor and back along the other: the load is across the pair, and
            // nothing returns through ground at all.
            circuit.Connect(source.Output, choke.A1);
            circuit.Connect(choke.A2, gnd.Pin);
            circuit.Connect(choke.B1, load1.A);
            circuit.Connect(load1.B, choke.B2);
        }
        else
        {
            // The same voltage on both conductors, returning through ground — which is what
            // common-mode noise on a cable is.
            circuit.Connect(source.Output, choke.A1);
            circuit.Connect(source.Output, choke.A2);
            var load2 = circuit.Add(new Resistor(50.0));

            circuit.Connect(choke.B1, load1.A);
            circuit.Connect(load1.B, gnd.Pin);
            circuit.Connect(choke.B2, load2.A);
            circuit.Connect(load2.B, gnd.Pin);
        }

        circuit.Probes.Add(new SignalProbe { TargetTerminal = choke.B1, Label = "Out" });

        var simulator = new CircuitSimulator(circuit);
        simulator.Reset();
        simulator.SolveOperatingPoint();
        simulator.ResolveProbes();

        var sweep = new AcSweep(simulator).Run(new AcSweepRequest(hertz, hertz * 1.01, 2));

        return sweep.Traces[0].Response[0].Magnitude;
    }

    /// <summary>
    /// The whole component, in a circuit: at a megahertz the signal goes through almost untouched
    /// and the common-mode noise is stopped. Nothing about that is a frequency response — the two
    /// measurements are at the <i>same</i> frequency.
    /// </summary>
    [Fact]
    public void TheSignalPassesAndTheNoiseDoesNot()
    {
        var signal = Through(Drive.Differential, 1e6);
        var noise = Through(Drive.CommonMode, 1e6);

        Assert.True(signal > 0.4, $"the signal should get through, not arrive at {signal:0.000}");
        Assert.True(noise < signal / 20.0,
            $"and the noise should not: {noise:0.0000} against the signal's {signal:0.000}");
    }

    /// <summary>
    /// At DC it is a piece of wire in both directions, which is why one can sit in a mains inlet
    /// or a supply rail without dropping anything.
    /// </summary>
    [Fact]
    public void AtDcItIsWireEitherWay()
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());
        var supply = circuit.Add(new DcVoltageSource(12.0));
        var choke = circuit.Add(new CommonModeChoke(1e-3, 0.999) { WindingResistance = 0.05 });
        var load = circuit.Add(new Resistor(100.0));

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, choke.A1);
        circuit.Connect(choke.B1, load.A);
        circuit.Connect(load.B, choke.B2);
        circuit.Connect(choke.A2, gnd.Pin);

        var simulator = new CircuitSimulator(circuit);
        simulator.Reset();
        simulator.SolveOperatingPoint();

        // 12 V across 100 ohms and two 50 milliohm windings.
        Assert.Equal(12.0 * 100.0 / 100.1, simulator.NodeVoltage(load.A) - simulator.NodeVoltage(load.B), 0.01);
    }
}
