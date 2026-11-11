namespace Cirq.UI.ViewModels;

/// <summary>A reading to take once a lesson's board is built: the red probe on a pin's net, the black on ground, the circuit running.</summary>
/// <param name="Prompt">What to measure, as the status bar asks it.</param>
/// <param name="Pin">The pin whose net the red probe goes on, as "Part.Pin".</param>
/// <param name="Low">The lowest a working circuit reads there, in volts.</param>
/// <param name="High">The highest.</param>
public sealed record MeasureTask(string Prompt, string Pin, double Low, double High);

/// <summary>A guided build: an example built on breadboards step by step, each hole clicked, then measured with the meter.</summary>
/// <param name="Name">What the menu calls it.</param>
/// <param name="Example">The example it builds.</param>
/// <param name="Intro">What it is, in a sentence, for the status bar as it starts.</param>
/// <param name="Measures">The readings to take once it's built.</param>
public sealed record BuildLesson(string Name, string Example, string Intro, IReadOnlyList<MeasureTask> Measures);

/// <summary>The guided builds on offer, in Simulate › Build Lessons.</summary>
public static class BuildLessons
{
    public static IReadOnlyList<BuildLesson> All { get; } =
    [
        new("Build a transistor switch", "Transistor Switch", "A push button switching an LED through an NPN transistor.",
        [
            new("Run it (F5) and measure the supply: the red probe on R2's top lead.", "R2.A", 4.8, 5.2),
            new("Now the transistor's emitter, which should sit on ground.", "Q1.E", -0.05, 0.05),
        ]),
        new("Build a 555 astable", "555 Astable", "A 555 timer flashing away on its own, its timing set by R1, R2 and C1.",
        [
            new("Run it (F5) and measure the 555's supply, pin 8 (VCC).", "U1.VCC", 8.7, 9.3),
            new("Now pin 5 (CTRL), which the 555 holds at two-thirds of its supply.", "U1.CTRL", 5.7, 6.3),
        ]),
        new("Build a regulated supply", "Regulated Supply", "A 7805 making 5 V from 12 V, with its capacitors and a load.",
        [
            new("Run it (F5) and measure the regulator's input, VR1's IN.", "VR1.IN", 11.5, 12.5),
            new("Now its output, VR1's OUT: the 5 V it's there to make.", "VR1.OUT", 4.8, 5.2),
        ]),
    ];

    /// <summary>A guided build's score out of 100: 5 off for each wrong hole, 10 for each wrong reading, 2 for each minute.</summary>
    public static int Score(int wrongHoles, int wrongReadings, TimeSpan time) =>
        Math.Clamp(100 - (5 * wrongHoles) - (10 * wrongReadings) - (2 * (int)time.TotalMinutes), 0, 100);
}
