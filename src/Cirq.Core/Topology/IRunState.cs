namespace Cirq.Core.Topology;

/// <summary>
/// A part whose state at the end of a run is worth carrying back to the circuit the run was copied
/// from — a processor's registers, a memory's bytes, a display's text.
/// <para>
/// Agents run a serialised copy of the circuit, so without this the parts on the document would
/// know nothing of what the run did: the scope would show the traces while the processor still sat
/// at its reset vector and the RAM was still blank. The copy's state is handed over whole, by
/// reference; the copy is thrown away afterwards, so nothing is shared.
/// </para>
/// </summary>
public interface IRunState
{
    /// <summary>Everything the part would need to be where this one is now.</summary>
    object CaptureRunState();

    /// <summary>Takes on the state another instance of the same part captured.</summary>
    void AdoptRunState(object state);
}
