using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Passive;

/// <summary>
/// Three-terminal potentiometer: the wiper splits the track into two resistances whose ratio is
/// set by <see cref="Position"/> (0 puts the wiper at <see cref="A"/>, 1 puts it at <see cref="B"/>).
/// </summary>
public partial class Potentiometer : CircuitComponent
{
    /// <summary>Smallest resistance either half of the track may present, avoiding a perfect short.</summary>
    private const double MinimumSegment = 1e-3;

    public Potentiometer(double resistance = 10e3, double position = 0.5)
    {
        Resistance = resistance;
        Position = position;

        A = new Terminal("a", "A", TerminalType.Passive, new Point(-30, 20));
        Wiper = new Terminal("w", "W", TerminalType.Passive, new Point(0, -30));
        B = new Terminal("b", "B", TerminalType.Passive, new Point(30, 20));
        Terminals = [A, Wiper, B];
    }

    public Terminal A { get; }
    public Terminal Wiper { get; }
    public Terminal B { get; }

    /// <summary>End-to-end track resistance in ohms.</summary>
    [ObservableProperty]
    public partial double Resistance { get; set; }

    /// <summary>Wiper tap ratio, clamped to [0, 1].</summary>
    [ObservableProperty]
    [Operable("Wiper", Minimum = 0, Maximum = 1)]
    public partial double Position { get; set; }

    /// <summary>When true the track is logarithmic (audio taper) rather than linear.</summary>
    [ObservableProperty]
    public partial bool IsLogarithmic { get; set; }

    public override string ComponentType => "Potentiometer";

    public override string DesignatorPrefix => "RV";

    public override string ValueLabel => $"{SiPrefix.Format(Resistance, "Ω")} @ {EffectivePosition * 100:0}%";

    /// <summary>Tap ratio after the taper law has been applied.</summary>
    public double EffectivePosition
    {
        get
        {
            var p = Math.Clamp(Position, 0, 1);
            // A 15% offset at half travel is the usual approximation of a log (A) taper.
            return IsLogarithmic ? Math.Pow(p, 2.2) : p;
        }
    }

    /// <summary>Resistance between <see cref="A"/> and the wiper.</summary>
    public double LowerResistance => Math.Max(Resistance * EffectivePosition, MinimumSegment);

    /// <summary>Resistance between the wiper and <see cref="B"/>.</summary>
    public double UpperResistance => Math.Max(Resistance * (1.0 - EffectivePosition), MinimumSegment);

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var na = system.Node(A);
        var nw = system.Node(Wiper);
        var nb = system.Node(B);

        system.StampConductance(na, nw, 1.0 / LowerResistance);
        system.StampConductance(nw, nb, 1.0 / UpperResistance);
    }

    partial void OnPositionChanged(double value)
    {
        if (value is < 0 or > 1) Position = Math.Clamp(value, 0, 1);
        NotifyValueChanged();
    }

    partial void OnResistanceChanged(double value) => NotifyValueChanged();

    partial void OnIsLogarithmicChanged(bool value) => NotifyValueChanged();
}
