using Cirq.Engine.Simulation;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The sweeps and spectra the User Guide tells people to run, with the figures it quotes. The
/// guide gives specific numbers; these are here so the numbers cannot quietly stop being true.
/// </summary>
public class DocumentedAnalysisTests
{
    private static MainWindowViewModel Load(string name)
    {
        var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        vm.Scope.ClearProbes();
        Examples.All.Single(e => e.Name == name).Build(vm);
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);
        return vm;
    }

    private static DcSweepViewModel Sweep(
        MainWindowViewModel vm, string part, string property, double start, double stop, int points = 41)
    {
        var model = new DcSweepViewModel(vm.Circuit);

        model.Sweep = model.Options.Single(o =>
            o.Component?.Name == part && o.PropertyName == property);

        model.Start = start;
        model.Stop = stop;
        model.Points = points;
        model.Run();

        return model;
    }

    /// <summary>
    /// The guide's table of how far each part's output reaches on a single 5 V supply. One sweep
    /// says what three paragraphs of prose about headroom say.
    /// </summary>
    [Fact]
    public void TheRailToRailSweepGivesTheHeadroomFiguresTheGuideQuotes()
    {
        var model = Sweep(Load("Rail to Rail"), "FG1", "DcOffset", -3.0, 9.0);

        Assert.Equal(4, model.Curves.Count);

        (double Low, double High) Range(string label)
        {
            var curve = model.Curves.Single(c => c.Label == label);
            return (curve.Y.Min(), curve.Y.Max());
        }

        var lm741 = Range("LM741");
        var lm358 = Range("LM358");
        var mcp = Range("MCP6002");

        // The 741 cannot use the bottom volt and a half or the top volt and a half.
        Assert.Equal(1.49, lm741.Low, 0.1);
        Assert.Equal(3.48, lm741.High, 0.1);

        // The LM358 reaches the bottom rail and still stops short of the top.
        Assert.Equal(0.02, lm358.Low, 0.05);
        Assert.Equal(3.47, lm358.High, 0.1);

        // And the rail-to-rail part reaches both.
        Assert.Equal(0.02, mcp.Low, 0.05);
        Assert.Equal(4.93, mcp.High, 0.1);

        // Which is the ordering the whole example exists to show.
        Assert.True(lm741.Low > lm358.Low);
        Assert.True(mcp.High > lm358.High);
    }

    /// <summary>
    /// A DC sweep goes one way, so a hysteresis loop takes two of them — and the point is that the
    /// answer depends on which way you came from.
    /// </summary>
    [Fact]
    public void SweepingAComparatorUpAndThenDownSwitchesInTwoDifferentPlaces()
    {
        double SwitchesAt(double start, double stop)
        {
            var model = Sweep(Load("Noise and Hysteresis"), "FG1", "DcOffset", start, stop);
            var curve = model.Curves.Single(c => c.Label == "Output");

            var middle = (curve.Y.Min() + curve.Y.Max()) / 2.0;

            for (var i = 1; i < curve.Y.Count; i++)
                if ((curve.Y[i - 1] < middle) != (curve.Y[i] < middle))
                    return curve.X[i];

            return double.NaN;
        }

        var rising = SwitchesAt(0.0, 5.0);
        var falling = SwitchesAt(5.0, 0.0);

        Assert.False(double.IsNaN(rising));
        Assert.False(double.IsNaN(falling));

        // Different places, and in the direction hysteresis actually goes: a Schmitt trigger
        // needs the input to climb higher to switch it on than it needs to fall to switch it off,
        // which is exactly what stops it chattering on a noisy edge.
        Assert.True(rising > falling,
            $"it switched at {rising:F3} going up and {falling:F3} coming down, which is backwards");

        // And the gap is the hysteresis the feedback resistor put there.
        Assert.InRange(rising - falling, 0.05, 1.5);
    }

    [Fact]
    public void TheSolarPanelSweepTradesVoltageAgainstCurrentAcrossTheLoad()
    {
        var model = Sweep(Load("Solar Panel"), "RV1", "Position", 0.02, 1.0);

        var voltage = model.Curves.Single(c => c.Label == "Panel").Y;
        var current = model.Curves.Single(c => c.Label.StartsWith("Shunt")).Y;

        // More load resistance is more voltage and less current — which is the I-V curve, walked
        // from the short-circuit end towards the open-circuit end.
        Assert.True(voltage[^1] > voltage[0] * 5);
        Assert.True(current[0] > current[^1] * 3);
    }

    /// <summary>
    /// The guide says the half-wave output's largest ripple component is at the line frequency and
    /// the full-wave one's is at twice it. That is the whole of "easier to filter", measured.
    /// </summary>
    [Theory]
    [InlineData("Half-Wave Rectifier", "DC out", 50.0)]
    [InlineData("Full-Wave Rectifier", "Rectified", 100.0)]
    public void TheRectifiersRippleLandsWhereTheGuideSaysItDoes(
        string example, string trace, double expected)
    {
        var vm = Load(example);
        vm.Simulation.Simulator!.Run(0.1);

        var model = new SpectrumViewModel(vm.Circuit) { Size = 8192 };
        model.Run();

        var curve = model.Curves.Single(c => c.Label == trace);

        // Past the DC term and its skirt, which on a rectified supply is the biggest thing there.
        var best = -1;
        for (var i = 0; i < curve.Frequencies.Count; i++)
        {
            if (curve.Frequencies[i] < 30) continue;
            if (best < 0 || curve.Magnitudes[i] > curve.Magnitudes[best]) best = i;
        }

        Assert.True(best >= 0);
        Assert.Equal(expected, curve.Frequencies[best], 15.0);
    }

    /// <summary>
    /// The sidebands that are the whole of what AM is, and which the time-domain trace only shows
    /// as an envelope you have to take on trust.
    /// </summary>
    [Fact]
    public void TheModulatedCarrierHasSidebandsSpacedByTheAudioFrequency()
    {
        var vm = Load("Amplitude Modulation");
        vm.Simulation.Simulator!.Run(4e-3);

        var model = new SpectrumViewModel(vm.Circuit)
        {
            Size = 16384,
            Window = SpectrumWindow.BlackmanHarris,
        };

        model.Run();

        var curve = model.Curves.Single(c => c.Label == "Modulated");

        double At(double hertz)
        {
            var centre = 0;
            for (var i = 1; i < curve.Frequencies.Count; i++)
                if (Math.Abs(curve.Frequencies[i] - hertz) < Math.Abs(curve.Frequencies[centre] - hertz))
                    centre = i;

            var peak = 0.0;
            for (var i = Math.Max(0, centre - 3); i <= Math.Min(curve.Magnitudes.Count - 1, centre + 3); i++)
                peak = Math.Max(peak, curve.Magnitudes[i]);

            return peak;
        }

        var carrier = At(100e3);
        var lower = At(98e3);
        var upper = At(102e3);

        Assert.True(carrier > 1.0, $"the carrier only reached {carrier:G3}");

        // A pair, matched, and well clear of the noise around them.
        Assert.Equal(lower, upper, carrier * 0.15);
        Assert.True(lower > carrier * 0.15, $"the sidebands only reached {lower:G3} against {carrier:G3}");

        // And nothing at three times the spacing, which would be distortion rather than modulation.
        Assert.True(At(106e3) < lower * 0.35);
    }
}
