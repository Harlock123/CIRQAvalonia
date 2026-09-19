namespace Cirq.Components.Nonlinear;

/// <summary>
/// The exponential pn junction shared by every semiconductor model here: the Shockley current, its
/// small-signal conductance, and SPICE's <c>pnjlim</c> step limiter.
/// <para>
/// Keeping this in one place matters because the limiter is what makes Newton-Raphson survive an
/// exponential. An iterate that overshoots by a volt asks for <c>exp(40)</c>; the limiter converts
/// that runaway step into a logarithmic one instead.
/// </para>
/// </summary>
internal static class Junction
{
    /// <summary>Largest exponent evaluated before the model extrapolates along its tangent.</summary>
    public const double MaxExponent = 80.0;

    /// <summary>
    /// Evaluates <c>i = Is·(exp(v/vt) − 1)</c> and <c>di/dv</c>. Beyond
    /// <see cref="MaxExponent"/> the exponential is replaced by its tangent, which keeps the
    /// result finite while still pointing Newton in the right direction.
    /// </summary>
    public static (double Current, double Conductance) Evaluate(double v, double saturationCurrent, double vt)
    {
        var x = v / vt;

        if (x > MaxExponent)
        {
            var e = Math.Exp(MaxExponent);
            var g = saturationCurrent * e / vt;
            return (saturationCurrent * (e - 1.0) + g * (v - MaxExponent * vt), g);
        }

        var exp = Math.Exp(x);
        return (saturationCurrent * (exp - 1.0), saturationCurrent * exp / vt);
    }

    /// <summary>The junction voltage above which the exponential outruns Newton's step.</summary>
    public static double CriticalVoltage(double saturationCurrent, double vt) =>
        vt * Math.Log(vt / (Math.Sqrt(2.0) * Math.Max(saturationCurrent, 1e-30)));

    /// <summary>
    /// SPICE's <c>pnjlim</c>: damps a Newton step that would push the junction far past its
    /// critical voltage, converting a runaway exponential step into a logarithmic one.
    /// <para>
    /// Only the forward direction is limited. Reverse bias needs no help — <c>exp</c> of a large
    /// negative number is simply zero, so the model stays well behaved however far back the
    /// junction is pushed. Clamping it instead would make a diode reverse-biased by a thousand
    /// volts crawl back in small steps and never converge.
    /// </para>
    /// </summary>
    public static double Limit(double vNew, double vOld, double vt, double vCritical)
    {
        if (double.IsNaN(vNew)) return vOld;
        if (vNew <= vCritical || Math.Abs(vNew - vOld) <= 2.0 * vt) return vNew;

        if (vOld > 0)
        {
            var arg = 1.0 + (vNew - vOld) / vt;
            return arg > 0 ? vOld + vt * Math.Log(arg) : vCritical;
        }

        return vNew > 0 ? vt * Math.Log(Math.Max(vNew / vt, 1e-12)) : vCritical;
    }
}
