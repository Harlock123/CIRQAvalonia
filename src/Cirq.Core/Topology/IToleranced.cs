namespace Cirq.Core.Topology;

/// <summary>
/// A part whose value is only nominal — it is made to a tolerance, and the one in front of you is
/// somewhere inside it.
/// <para>
/// Every simulation in this library so far has assumed perfect components, and that is the one way
/// in which none of them resembles anything anybody has built. A divider made of two 5 % resistors
/// does not divide by exactly two; a filter made of a 20 % capacitor does not turn over exactly
/// where it was designed to. Whether that matters is the question this interface exists to let
/// somebody ask.
/// </para>
/// </summary>
public interface IToleranced
{
    /// <summary>
    /// How far the real part may be from its marked value, as a fraction — 0.05 for a five
    /// percent part. Zero means treat it as exact.
    /// </summary>
    double Tolerance { get; set; }

    /// <summary>The name of the property the tolerance applies to.</summary>
    string TolerancedProperty { get; }
}
