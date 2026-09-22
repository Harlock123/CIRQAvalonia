using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Sources;

/// <summary>
/// Signal generator producing sine, square, triangle, sawtooth or flat DC output, stamped as a
/// time-varying voltage source. Square and sawtooth edges are given a finite transition time and
/// are registered as solver breakpoints, so the transient loop always places a time point on them.
/// </summary>
public partial class FunctionGenerator : TwoTerminalComponent, IBreakpointSource, IAcExcitation
{
    public FunctionGenerator(Waveform shape = Waveform.Sine, double frequency = 1e3, double amplitudePeakToPeak = 2.0)
        : base("OUT", "GND")
    {
        Shape = shape;
        Frequency = frequency;
        AmplitudePeakToPeak = amplitudePeakToPeak;
    }

    [ObservableProperty]
    public partial Waveform Shape { get; set; }

    /// <summary>Frequency in hertz.</summary>
    [ObservableProperty]
    [Operable("Frequency", Minimum = 1, Maximum = 1e6, Unit = "Hz", IsLogarithmic = true)]
    public partial double Frequency { get; set; }

    /// <summary>Peak-to-peak amplitude in volts.</summary>
    [ObservableProperty]
    [Operable("Amplitude", Minimum = 0, Maximum = 50, Unit = "Vpp")]
    public partial double AmplitudePeakToPeak { get; set; }

    /// <summary>Phase offset in degrees.</summary>
    [ObservableProperty]
    public partial double PhaseDegrees { get; set; }

    /// <summary>DC offset added to the waveform, in volts.</summary>
    [ObservableProperty]
    [Operable("Offset", Minimum = -25, Maximum = 25, Unit = "V")]
    public partial double DcOffset { get; set; }

    /// <summary>High-time fraction for square waves, and rise fraction for triangles. Clamped to (0, 1).</summary>
    [ObservableProperty]
    [Operable("Duty", Minimum = 0.01, Maximum = 0.99)]
    public partial double DutyCycle { get; set; } = 0.5;

    /// <summary>Transition time of square and sawtooth edges, in seconds.</summary>
    [ObservableProperty]
    public partial double EdgeTime { get; set; } = 1e-9;

    /// <summary>Output (source) impedance in ohms; 50 R matches a typical bench generator.</summary>
    [ObservableProperty]
    public partial double OutputResistance { get; set; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    public Terminal Output => A;

    public Terminal Return => B;

    public override string ComponentType => "Function Generator";

    public override string DesignatorPrefix => "FG";

    public override string ValueLabel =>
        Shape == Waveform.Dc
            ? SiPrefix.Format(DcOffset, "V")
            : $"{Shape} {SiPrefix.Format(Frequency, "Hz")} {SiPrefix.Format(AmplitudePeakToPeak, "Vpp")}";

    public override int VoltageSourceCount => 1;

    /// <summary>Peak amplitude (half of peak-to-peak), in volts.</summary>
    public double Amplitude => AmplitudePeakToPeak * 0.5;

    public double Period => Frequency > 0 ? 1.0 / Frequency : double.PositiveInfinity;

    /// <summary>Instantaneous output voltage at time <paramref name="time"/>.</summary>
    public double ValueAt(double time)
    {
        if (!IsEnabled) return 0;
        if (Shape == Waveform.Dc || Frequency <= 0) return DcOffset;

        var duty = Math.Clamp(DutyCycle, 1e-6, 1 - 1e-6);
        var phase = Frac(time * Frequency + PhaseDegrees / 360.0);
        var amp = Amplitude;

        var shaped = Shape switch
        {
            Waveform.Sine => amp * Math.Sin(2.0 * Math.PI * phase),
            Waveform.Square => SquareValue(phase, duty, amp),
            Waveform.Triangle => phase < duty
                ? -amp + 2.0 * amp * (phase / duty)
                : amp - 2.0 * amp * ((phase - duty) / (1.0 - duty)),
            Waveform.Sawtooth => SawtoothValue(phase, amp),
            _ => 0.0,
        };

        return shaped + DcOffset;
    }

    private double SquareValue(double phase, double duty, double amp)
    {
        // Linear ramps of EdgeTime replace the ideal discontinuities at 0 and at the duty point.
        var edgePhase = Math.Min(EdgeTime * Frequency, Math.Min(duty, 1 - duty) * 0.49);
        if (edgePhase <= 0) return phase < duty ? amp : -amp;

        if (phase < edgePhase) return -amp + 2.0 * amp * (phase / edgePhase);
        if (phase < duty) return amp;
        if (phase < duty + edgePhase) return amp - 2.0 * amp * ((phase - duty) / edgePhase);
        return -amp;
    }

    private double SawtoothValue(double phase, double amp)
    {
        var edgePhase = Math.Min(EdgeTime * Frequency, 0.49);
        var rampEnd = 1.0 - edgePhase;
        if (phase < rampEnd) return -amp + 2.0 * amp * (phase / rampEnd);
        return amp - 2.0 * amp * ((phase - rampEnd) / edgePhase);
    }

    private static double Frac(double v)
    {
        var f = v - Math.Floor(v);
        return f < 0 ? f + 1 : f;
    }

    public double? NextBreakpointAfter(double time)
    {
        if (!IsEnabled || Frequency <= 0) return null;
        if (Shape is not (Waveform.Square or Waveform.Sawtooth or Waveform.Triangle)) return null;

        var duty = Math.Clamp(DutyCycle, 1e-6, 1 - 1e-6);
        var period = Period;
        var phaseOffset = PhaseDegrees / 360.0;

        // Candidate phases within a cycle where the slope changes.
        double[] phases = Shape switch
        {
            Waveform.Square => [0.0, Math.Min(EdgeTime * Frequency, duty * 0.49), duty, duty + Math.Min(EdgeTime * Frequency, (1 - duty) * 0.49)],
            Waveform.Triangle => [0.0, duty],
            _ => [0.0, 1.0 - Math.Min(EdgeTime * Frequency, 0.49)],
        };

        var best = double.PositiveInfinity;
        var cycle = Math.Floor(time * Frequency + phaseOffset);
        for (var k = 0; k <= 1; k++)
        {
            foreach (var p in phases)
            {
                var t = (cycle + k + p - phaseOffset) * period;
                if (t > time + 1e-15 && t < best) best = t;
            }
        }

        return double.IsInfinity(best) ? null : best;
    }

    /// <summary>
    /// How hard it drives a frequency sweep, in volts. One by default, unlike the supplies: this
    /// is the signal source, so a sweep with nothing else configured measures the circuit's
    /// response to it and the numbers come out as a plain gain.
    /// </summary>
    [ObservableProperty]
    public partial double AcMagnitude { get; set; } = 1.0;

    /// <summary>Phase of that excitation, in degrees.</summary>
    [ObservableProperty]
    public partial double AcPhaseDegrees { get; set; }

    public override void StampMatrix(MnaSystem system, SimulationState state) =>
        system.StampTheveninSource(
            system.Branch(this), system.Node(A), system.Node(B), ValueAt(state.Time), OutputResistance);

    /// <summary>
    /// The shape, the frequency and the offset all mean nothing here: a sweep asks what the
    /// circuit does to a small sine at each frequency in turn, so all the generator contributes
    /// is how large that sine is.
    /// </summary>
    public override void StampAc(AcSystem system, SimulationState state) =>
        system.AddRhs(system.Branch(this), AcSystem.Phasor(AcMagnitude, AcPhaseDegrees));

    partial void OnShapeChanged(Waveform value) => NotifyValueChanged();

    partial void OnFrequencyChanged(double value) => NotifyValueChanged();

    partial void OnAmplitudePeakToPeakChanged(double value) => NotifyValueChanged();

    partial void OnDcOffsetChanged(double value) => NotifyValueChanged();
}
