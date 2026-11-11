using Cirq.Components.Passive;
using Cirq.Components.Serialization;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Protoboard;

/// <summary>
/// The multimeter's resistance range, as a real one does it: on the circuit with its power off, a small known current
/// pushed between the probes and the voltage it takes read back.
/// <para>
/// It works on a copy, so the circuit on screen is never touched: the supplies, batteries and generators taken off the
/// bench (they're what "power off" means), a 1 mA test current put between the probes, and the operating point solved. A
/// diode in the path reads its drop at that current, as a meter's resistance range does; anything over 40 MΩ is "open".
/// Faults hidden for fault finding are in the copy, so a pin that isn't making contact reads open, as it would.
/// </para>
/// </summary>
public static class Ohmmeter
{
    public const double TestCurrent = 1e-3;

    /// <summary>Above this, the meter reads OL: no path, or as good as none.</summary>
    public const double Open = 40e6;

    /// <summary>Below this, continuity: the meter beeps.</summary>
    public const double Continuity = 30;

    /// <summary>
    /// The resistance between the nets of two pins, in ohms; <see cref="double.PositiveInfinity"/> when there's no path,
    /// null when the circuit can't be solved for it.
    /// </summary>
    public static double? Between(Circuit circuit, Terminal red, Terminal black)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(red);
        ArgumentNullException.ThrowIfNull(black);
        try
        {
            var netlist = circuit.BuildSimulationNetlist();
            if (ReferenceEquals(netlist.NetOf(red), netlist.NetOf(black))) return 0;
        }
        catch (CircuitTopologyException)
        {
            return null;
        }

        var copy = CircuitSerializer.FromJson(CircuitSerializer.ToJson(circuit)).Circuit;
        copy.RestoreFaults(circuit.FaultMarks());
        Terminal? Find(Terminal t) =>
            t.Owner is not { } owner ? null
            : Flattening.Flatten(copy.Components).FirstOrDefault(c => c.Id == owner.Id)?.Terminals.FirstOrDefault(x => x.Id == t.Id);
        if (Find(red) is not { } a || Find(black) is not { } b) return null;

        // Power off: every source of energy taken away.
        foreach (var source in copy.Components.Where(c => c is DcVoltageSource or DcCurrentSource or Battery or FunctionGenerator or WaveformSource
                                                         or NoiseSource or SolarCell or BehaviouralSource).ToList())
            copy.Remove(source);

        // The meter: its test current between the probes, its black probe the reference, and a meter's own input
        // resistance on each probe so a net with nothing else on it still has an answer.
        var meter = copy.Add(new DcCurrentSource(TestCurrent));
        copy.Connect(meter.Terminals[0], a);
        copy.Connect(meter.Terminals[1], b);
        var reference = copy.Add(new Ground());
        var leak = copy.Add(new Resistor(1e10));
        copy.Connect(leak.Terminals[0], b);
        copy.Connect(leak.Terminals[1], reference.Terminals[0]);
        var leak2 = copy.Add(new Resistor(1e10));
        copy.Connect(leak2.Terminals[0], a);
        copy.Connect(leak2.Terminals[1], reference.Terminals[0]);

        try
        {
            var simulator = new CircuitSimulator(copy, new Cirq.Core.Simulation.SimulationSettings());
            simulator.Reset();
            simulator.SolveOperatingPoint();
            var volts = Math.Abs(simulator.NodeVoltage(a) - simulator.NodeVoltage(b));
            var ohms = volts / TestCurrent;
            return ohms > Open || !double.IsFinite(ohms) ? double.PositiveInfinity : ohms;
        }
        catch (Exception e) when (e is CircuitTopologyException or ConvergenceException or Cirq.Engine.Numerics.SingularMatrixException)
        {
            return null;
        }
    }

    /// <summary>A resistance as a meter shows it: 0.5, 4.70k, 1.20M, or OL.</summary>
    public static string Display(double ohms) =>
        double.IsPositiveInfinity(ohms) ? "OL"
        : ohms >= 1e6 ? (ohms / 1e6).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "M"
        : ohms >= 1e3 ? (ohms / 1e3).ToString(ohms >= 1e5 ? "0.0" : "0.00", System.Globalization.CultureInfo.InvariantCulture) + "k"
        : ohms.ToString(ohms >= 100 ? "0.0" : "0.00", System.Globalization.CultureInfo.InvariantCulture);
}
