using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Sources;

/// <summary>
/// A noise generator: a two-terminal source that adds a random voltage to whatever it is in series
/// with.
/// <para>
/// Every circuit in this library has so far been perfectly clean, and that is the one way in which
/// none of them resembles a real one. Noise is why half the parts in the palette exist. Put a slow
/// ramp through a comparator and the output changes once; put the same ramp through it with a few
/// millivolts of noise riding on it and the output <b>chatters</b> — a burst of transitions where
/// there should be one. Add hysteresis and it stops. That is what the LM311, the LM393 and the
/// 74HC14 are <i>for</i>, and without a noise source there is no way to show it.
/// </para>
/// <para>
/// Two things about the model matter. It is <b>band-limited</b>: a new sample is drawn at a fixed
/// rate and held between draws, rather than a fresh random number at every time point. Noise that
/// changed every step would not be a signal at all — its character would depend on the solver's
/// step size, so halving the time step would change the answer, and nothing downstream could be
/// filtered or reasoned about.
/// </para>
/// <para>
/// And it is <b>repeatable</b>. The sequence comes from a seed, so the same circuit run twice
/// gives the same noise and therefore the same answer. Random results that cannot be reproduced
/// are not much use for finding out why something misbehaved.
/// </para>
/// </summary>
public sealed partial class NoiseSource : TwoTerminalComponent
{
    private int _state;
    private double _sample;
    private double _sampledAt = double.NegativeInfinity;
    private double _spare = double.NaN;

    public NoiseSource()
    {
        ResetState();
    }

    /// <summary>
    /// Noise amplitude as an RMS voltage. A few millivolts is what a breadboard picks up; tens of
    /// microvolts is a careful layout.
    /// </summary>
    [ObservableProperty]
    [Operable("Noise", Minimum = 0, Maximum = 0.5, Unit = "V")]
    public partial double RmsVoltage { get; set; } = 5e-3;

    /// <summary>
    /// How often a new value is drawn, in hertz — which is what sets the bandwidth of the noise.
    /// Above this the spectrum rolls off, as it does for anything real.
    /// </summary>
    [ObservableProperty]
    public partial double Bandwidth { get; set; } = 100e3;

    /// <summary>
    /// Where the sequence starts. Change it for a different run of noise with the same character;
    /// leave it alone to get the same one back.
    /// </summary>
    [ObservableProperty]
    public partial int Seed { get; set; } = 1;

    /// <summary>Resistance in series with the noise, in ohms.</summary>
    [ObservableProperty]
    public partial double SeriesResistance { get; set; } = 50.0;

    public override string ComponentType => "Noise Source";

    public override string DesignatorPrefix => "N";

    public override string ValueLabel => $"{SiPrefix.Format(RmsVoltage, "V")} rms";

    /// <summary>The voltage it is contributing at the last solved point.</summary>
    public double Value => _sample;

    /// <summary>How long one sample is held, in seconds.</summary>
    public double SampleSeconds => 1.0 / Math.Max(Bandwidth, 1e-3);

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        Advance(state.Time);

        var a = system.Node(A);
        var b = system.Node(B);

        // A Thévenin source as its Norton equivalent: the noise behind its series resistance, so
        // there is no branch current to solve for and the stamp stays well conditioned.
        var conductance = 1.0 / Math.Max(SeriesResistance, 1e-6);

        system.StampConductance(a, b, conductance);
        system.StampCurrentSource(b, a, _sample * conductance);
    }

    /// <summary>
    /// Draws a new sample if the old one has been held long enough. Time can go backwards when
    /// the engine retreats to retry a step, so the sample is keyed on the interval it belongs to
    /// rather than on however much time has passed — the same instant always gets the same noise.
    /// </summary>
    private void Advance(double time)
    {
        var interval = SampleSeconds;
        var index = Math.Floor(Math.Max(time, 0.0) / interval);

        if (Math.Abs(index - _sampledAt) < 0.5) return;

        // Stepping forward one interval at a time keeps the sequence tied to the time axis rather
        // than to how many times the solver happened to ask.
        var target = (long)index;
        var current = double.IsNegativeInfinity(_sampledAt) ? -1L : (long)_sampledAt;

        if (target < current)
        {
            // Gone backwards far enough to matter: start the sequence again and run it forward,
            // so a retried step sees the same noise it saw the first time.
            Restart();
            current = -1L;
        }

        for (var i = current; i < target; i++) _sample = Gaussian() * RmsVoltage;

        _sampledAt = index;
    }

    /// <summary>
    /// A normally distributed sample, by the Box-Muller transform. Noise that is uniform rather
    /// than Gaussian sounds and behaves wrong: it has hard limits, and real noise does not.
    /// </summary>
    private double Gaussian()
    {
        if (!double.IsNaN(_spare))
        {
            var held = _spare;
            _spare = double.NaN;
            return held;
        }

        double u, v, squared;

        do
        {
            u = (2.0 * Next()) - 1.0;
            v = (2.0 * Next()) - 1.0;
            squared = (u * u) + (v * v);
        }
        while (squared >= 1.0 || squared == 0.0);

        var factor = Math.Sqrt(-2.0 * Math.Log(squared) / squared);

        _spare = v * factor;
        return u * factor;
    }

    /// <summary>
    /// A uniform value in [0,1) from a small deterministic generator, rather than
    /// <c>System.Random</c>, so the sequence is the same on every platform and every run.
    /// </summary>
    private double Next()
    {
        // Numerical Recipes' linear congruential constants, which are adequate for this and short.
        _state = unchecked((_state * 1664525) + 1013904223);

        return ((uint)_state >> 8) / 16777216.0;
    }

    private void Restart()
    {
        _state = Seed == 0 ? 1 : Seed;
        _spare = double.NaN;
        _sample = 0;
    }

    public override void ResetState()
    {
        Restart();
        _sampledAt = double.NegativeInfinity;
    }

    partial void OnSeedChanged(int value) => ResetState();

    partial void OnRmsVoltageChanged(double value) => NotifyValueChanged();
}
