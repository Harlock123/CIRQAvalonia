namespace Cirq.Core.Topology;

/// <summary>What is wrong with a connection that looks right.</summary>
public enum FaultKind
{
    /// <summary>A lead that isn't making contact: a bent pin, a cracked joint, a breadboard hole that doesn't grip.</summary>
    Open,

    /// <summary>Two pins joined that shouldn't be: a solder bridge, a stray strand, a lead pushed into the wrong strip.</summary>
    Bridged,

    /// <summary>A polarised part fitted the wrong way round: an LED, a diode or an electrolytic backwards.</summary>
    Reversed,
}

/// <summary>
/// A fault hidden in a circuit for somebody to find: the schematic still shows the circuit as it was meant, and only the
/// simulation sees it.
/// <para>
/// It lives on the circuit while the window is open and is never saved, so a puzzle can't be spoiled by reading the
/// file. Only the simulator's netlist (<see cref="Circuit.BuildSimulationNetlist"/>) applies it; the drawing, the rule
/// check, the board layout and the exports all describe the circuit as drawn, which is the point.
/// </para>
/// </summary>
/// <param name="Kind">What is wrong.</param>
/// <param name="First">The pin it is at.</param>
/// <param name="Second">The other pin of a bridge or of a part fitted backwards.</param>
public sealed record CircuitFault(FaultKind Kind, Terminal First, Terminal? Second = null)
{
    /// <summary>The parts it is at.</summary>
    public IEnumerable<CircuitComponent> Parts =>
        new[] { First.Owner, Second?.Owner }.Where(o => o is not null).Distinct()!;

    /// <summary>The fault in a sentence, for when it is revealed.</summary>
    public string Describe() => Kind switch
    {
        FaultKind.Open => $"{First} isn't making contact: the pin is in the circuit on the drawing, but not on the bench.",
        FaultKind.Bridged => $"{First} and {Second} are bridged: something joins two pins that should be apart.",
        _ => $"{First.Owner?.Name} is in backwards: {First.Name} and {Second?.Name} are swapped.",
    };
}
