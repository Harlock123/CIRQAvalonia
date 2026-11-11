using Cirq.Core.Topology;

namespace Cirq.UI.ViewModels;

/// <summary>
/// One fault-finding lesson: an example with a particular fault in it, and the symptom a person would see on the bench.
/// </summary>
/// <param name="Name">What it is called in the Lessons menu.</param>
/// <param name="Example">The example it is built on.</param>
/// <param name="Symptom">What's wrong, as somebody bringing it to the bench would say it.</param>
/// <param name="Kind">The fault.</param>
/// <param name="Pin">Where: a pin as Part.Pin.</param>
/// <param name="Second">The other pin of a bridge or a reversed part.</param>
public sealed record FaultLesson(string Name, string Example, string Symptom, FaultKind Kind, string Pin, string? Second = null);

/// <summary>The lessons, easiest first: each a working example with one thing wrong in it, and a symptom to start from.</summary>
public static class FaultLessons
{
    public static IReadOnlyList<FaultLesson> All { get; } =
    [
        new("The switch that does nothing", "Transistor Switch", "Pressing SW1 should light LED1. It doesn't.", FaultKind.Open, "Q1.B"),
        new("A timer that won't tick", "555 Astable", "The 555 should make a square wave on OUT. It doesn't.", FaultKind.Open, "C1.A"),
        new("One LED never lights", "LED Chaser", "Ten LEDs should take their turn. One of them never does.", FaultKind.Reversed, "LED3.A", "LED3.K"),
        new("Two at once", "LED Chaser", "Each LED should light on its own. Two of them come on together.", FaultKind.Bridged, "U1.Q0", "U1.Q2"),
        new("No gain", "Inverting Amplifier", "It should invert the input and amplify it ten times. It doesn't.", FaultKind.Open, "R2.A"),
        new("Ripple", "Full-Wave Rectifier", "The output should be smooth DC. It's lumpy.", FaultKind.Open, "C1.A"),
        new("Half the wave", "Full-Wave Rectifier", "The output ripples at the input's frequency, not twice it.", FaultKind.Open, "D2.A"),
        new("A regulator that won't regulate", "Regulated Supply", "The output should be a steady 5 V. It isn't.", FaultKind.Open, "VR1.GND"),
    ];
}
