using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.UI.ViewModels;

/// <summary>
/// The conditions the circuit is run under, as opposed to what is in it.
/// <para>
/// Two things, neither a property of any single part: the temperature everything is at, and how the
/// solver is allowed to step through time. Every semiconductor junction reads the first at once, so
/// changing it moves diode drops, transistor gains and leakage currents together; the second decides
/// whether a run takes the step it was given or one it chose.
/// </para>
/// </summary>
public sealed partial class ConditionsViewModel : ObservableObject
{
    private readonly Circuit _circuit;

    public ConditionsViewModel(Circuit circuit)
    {
        _circuit = circuit;
        AmbientCelsius = circuit.AmbientTemperatureCelsius;
        AdaptiveTimeStep = circuit.AdaptiveTimeStep;
    }

    /// <summary>
    /// The temperature everything is at, in degrees Celsius. 27 °C is what every model in this
    /// library is characterised at, and what a datasheet means by room temperature.
    /// </summary>
    [ObservableProperty]
    public partial double AmbientCelsius { get; set; }

    /// <summary>
    /// Whether the solver chooses its own step.
    /// <para>
    /// Worth a checkbox rather than being simply switched on, because the two behaviours are useful
    /// for different things. A fixed step is repeatable: the same circuit takes the same steps and
    /// gives the same numbers every run, and somebody stepping through an edge by hand gets a step
    /// they can predict. A chosen step is accurate where it matters and quick where it does not —
    /// which on a converter is the difference between a right answer and a plausible one.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial bool AdaptiveTimeStep { get; set; }

    /// <summary>What the step setting means, in a sentence, so nobody has to try it to find out.</summary>
    public string StepNote => AdaptiveTimeStep
        ? "The solver shortens the step where the waveform bends and lengthens it where it does " +
          "not, and retakes a step that stepped over the instant a part switched. An RC charging " +
          "curve costs a thirtieth of the steps for the same accuracy; a converter's oscillator " +
          "comes out at the rate its timing capacitor sets rather than low."
        : "Every step is the one the timebase asks for. Predictable, and the same every run — but " +
          "a fast edge is only as well placed as the step is short, and a long step spent on a " +
          "flat waveform is a long step wasted.";

    /// <summary>Raised when the value changes, so the engine can be rebuilt with it.</summary>
    public event EventHandler? Changed;

    /// <summary>A sentence about what the current setting implies, which saves looking it up.</summary>
    public string Note => AmbientCelsius switch
    {
        < -40 => "Below the range most parts are specified over. The models will still solve, but " +
                 "they are extrapolating.",
        < 0 => "Below freezing. A silicon diode's forward drop is well above its room-temperature " +
               "figure here — about two millivolts a degree higher for every degree down.",
        <= 40 => "Around room temperature, where every model in the library is characterised.",
        <= 85 => "The commercial and industrial range. Junction drops are noticeably lower and " +
                 "leakage is several times its room-temperature value.",
        <= 150 => "Hot. Leakage roughly doubles every ten degrees, so anything that depends on a " +
                  "reverse-biased junction staying off is worth checking here.",
        _ => "Above what silicon is usually rated for. The arithmetic still works; the part would not.",
    };

    /// <summary>Back to the figure the models are characterised at.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public void Reset() => AmbientCelsius = 27.0;

    partial void OnAdaptiveTimeStepChanged(bool value)
    {
        _circuit.AdaptiveTimeStep = value;

        OnPropertyChanged(nameof(StepNote));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnAmbientCelsiusChanged(double value)
    {
        _circuit.AmbientTemperatureCelsius = value;

        OnPropertyChanged(nameof(Note));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
