using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.UI.ViewModels;

/// <summary>
/// The conditions the circuit is run under, as opposed to what is in it.
/// <para>
/// At the moment that means one thing — the ambient temperature — and it is worth a dialog of its
/// own because it is not a property of any single part. Every semiconductor junction in the
/// circuit reads it at once, so changing it moves diode drops, transistor gains and leakage
/// currents together.
/// </para>
/// </summary>
public sealed partial class ConditionsViewModel : ObservableObject
{
    private readonly Circuit _circuit;

    public ConditionsViewModel(Circuit circuit)
    {
        _circuit = circuit;
        AmbientCelsius = circuit.AmbientTemperatureCelsius;
    }

    /// <summary>
    /// The temperature everything is at, in degrees Celsius. 27 °C is what every model in this
    /// library is characterised at, and what a datasheet means by room temperature.
    /// </summary>
    [ObservableProperty]
    public partial double AmbientCelsius { get; set; }

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

    partial void OnAmbientCelsiusChanged(double value)
    {
        _circuit.AmbientTemperatureCelsius = value;

        OnPropertyChanged(nameof(Note));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
