namespace Cirq.Components.Sources;

public enum Waveform
{
    Sine,
    Square,
    Triangle,
    Sawtooth,
    /// <summary>Constant output at the DC offset only.</summary>
    Dc,
}
