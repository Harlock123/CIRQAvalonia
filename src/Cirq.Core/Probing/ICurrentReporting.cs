using Cirq.Core.Simulation;
using Cirq.Core.Topology;

namespace Cirq.Core.Probing;

/// <summary>
/// Implemented by components that can say how much current is flowing into one of their pins.
/// <para>
/// A current probe has to be told which way round it is clamped, so the convention here is the
/// one a real clamp meter uses: <b>positive means current flowing into the component through the
/// probed terminal</b>. Probing the two ends of a resistor therefore gives equal and opposite
/// readings, which is what you would see on a bench.
/// </para>
/// <para>
/// Components stamped with a branch of their own — inductors, transformers, motors, driven logic
/// outputs — already have a current the solver knows about, and the simulator falls back to that.
/// This interface is for everything stamped as a conductance, where the current is not a solved
/// unknown but something the device has to work out from the voltage across it.
/// </para>
/// </summary>
public interface ICurrentReporting
{
    /// <summary>Current flowing into the component through <paramref name="terminal"/>, in amps.</summary>
    double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state);
}
