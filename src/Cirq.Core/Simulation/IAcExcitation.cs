namespace Cirq.Core.Simulation;

/// <summary>
/// A source that can drive a small-signal sweep.
/// <para>
/// A frequency response wants these: they are the stimulus, and what it measures is what the
/// circuit does to them. A <b>stability</b> sweep does not — it injects at one point of its own
/// and measures a ratio either side of it, and any other source driving the circuit at the same
/// time adds its own response on top. The ratio is then not a loop gain at all.
/// </para>
/// <para>
/// That is worth an interface rather than a convention, because it is silent when it goes wrong:
/// the numbers come out plausible. A follower measured with a function generator still connected
/// read 6 dB of loop gain where it has 106, and nothing about the plot said so until somebody
/// noticed that a follower cannot have 6 dB.
/// </para>
/// </summary>
public interface IAcExcitation
{
    /// <summary>
    /// How hard this source drives a small-signal sweep, in volts or amps. Nothing to do with its
    /// DC value: to a small signal a supply is a short circuit, so a source that is not meant to
    /// be the stimulus leaves this at zero and simply holds its node.
    /// </summary>
    double AcMagnitude { get; set; }
}
