using Cirq.Core.Primitives;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Nonlinear;

/// <summary>
/// A diode that also reports how brightly it is lit, so the canvas can render an illuminated
/// symbol. Brightness is the forward current normalised against the rated current.
/// </summary>
public partial class Led : Diode
{
    public Led(DiodeModel? model = null, Color? colour = null)
        : base(model ?? DiodeModel.LedRed)
    {
        EmittedColor = colour ?? Color.FromHex("#FF4136");
        EnableBreakdown = true;
    }

    /// <summary>Current at which the LED is considered fully lit, in amps.</summary>
    [ObservableProperty]
    public partial double RatedCurrent { get; set; } = 20e-3;

    [ObservableProperty]
    public partial Color EmittedColor { get; set; }

    /// <summary>Normalised brightness in [0, 1], following a perceptual square-root response.</summary>
    [ObservableProperty]
    public partial double Brightness { get; set; }

    public override string ComponentType => "LED";

    public override string DesignatorPrefix => "LED";

    /// <summary>
    /// The colours offered in the palette, each pairing a forward-voltage model with the light it
    /// actually emits. Forward drop really does track colour: red is around 1.9 V because its
    /// bandgap is smaller, blue and white sit near 3 V.
    /// </summary>
    public static IReadOnlyList<(string Name, DiodeModel Model, string Hex)> Colours { get; } =
    [
        ("Red", DiodeModel.LedRed, "#FF4136"),
        ("Amber", DiodeModel.LedAmber, "#FF8C1A"),
        ("Yellow", DiodeModel.LedYellow, "#FFD733"),
        ("Green", DiodeModel.LedGreen, "#2ECC40"),
        ("Blue", DiodeModel.LedBlue, "#3D9BFF"),
        ("White", DiodeModel.LedWhite, "#F2F6FF"),
    ];

    /// <summary>Builds an LED of a named colour from <see cref="Colours"/>.</summary>
    public static Led OfColour(string name)
    {
        var (_, model, hex) = Colours.FirstOrDefault(c => c.Name == name, Colours[0]);
        return new Led(model, Color.FromHex(hex));
    }

    public override void CommitTimeStep(Cirq.Core.Simulation.MnaSystem system, Cirq.Core.Simulation.SimulationState state)
    {
        base.CommitTimeStep(system, state);
        var ratio = Math.Clamp(Current / Math.Max(RatedCurrent, 1e-9), 0, 1);
        // Perceived brightness tracks roughly the square root of drive current.
        Brightness = Math.Sqrt(ratio);
    }

    public override void ResetState()
    {
        base.ResetState();
        Brightness = 0;
    }
}
