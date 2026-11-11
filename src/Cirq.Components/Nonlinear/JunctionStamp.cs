using Cirq.Core.Simulation;

namespace Cirq.Components.Nonlinear;

/// <summary>
/// A plain pn junction inside a bigger part, stamped as its Newton companion with SPICE's step limiting: what the
/// UJT's and PUT's emitters, the LM13700's linearising diodes and a valve rectifier's sharper cousins share.
/// </summary>
public sealed class JunctionStamp
{
    private double _last;

    public bool Limited { get; private set; }

    /// <summary>The current at the last stamp, anode to cathode.</summary>
    public double Current { get; private set; }

    public void Stamp(MnaSystem system, int anode, int cathode, double saturationCurrent, double emission = 1.0)
    {
        ArgumentNullException.ThrowIfNull(system);
        var vt = 0.025852 * emission;
        var v = system.IterationVoltageAcross(anode, cathode);
        var limited = Junction.Limit(v, _last, vt, Junction.CriticalVoltage(saturationCurrent, vt));
        Limited = Math.Abs(limited - v) > 1e-9;
        _last = limited;
        var (i, g) = Junction.Evaluate(limited, saturationCurrent, vt);
        g += 1e-12;
        system.StampNorton(anode, cathode, g, i - (g * limited));
        Current = i;
    }

    public void Reset() => (_last, Limited, Current) = (0, false, 0);
}
