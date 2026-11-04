using Cirq.Components.Digital;
using Cirq.Components.Nonlinear;
using Cirq.UI.Rendering;
using Cirq.UI.Services;
using Cirq.UI.Tests.Support;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The guide's pictures of the 0.43 logic-family examples, each drawn by the application's own
/// renderer after the example has run long enough to be showing what it is for.
/// </summary>
[Collection(WindowCollection.Name)]
public class LogicFamilyFigureTests(WindowSession session)
{
    /// <summary>In the dark theme, as the other example pictures are.</summary>
    private static void Dark(Action draw)
    {
        var was = ThemeManager.Selected;

        ThemeManager.Apply(AppTheme.Dark);
        CanvasTheme.Invalidate();

        try
        {
            draw();
        }
        finally
        {
            ThemeManager.Apply(was);
            CanvasTheme.Invalidate();
        }
    }

    private static MainWindowViewModel Shoot(string example, string file, double seconds)
    {
        var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        vm.Scope.ClearProbes();
        Examples.All.Single(e => e.Name == example).Build(vm);

        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);
        vm.Simulation.Simulator!.Run(seconds);

        Directory.CreateDirectory(DocPlot.ImageDirectory);
        var path = Path.Combine(DocPlot.ImageDirectory, file);
        CircuitExporter.Export(vm.Circuit, null, path, new ExportOptions(ExportFormat.Png, ExportContent.Schematic, RasterScale: 2.0));

        Assert.True(new FileInfo(path).Length > 2000, $"{file} came out almost empty");
        return vm;
    }

    [Fact]
    public void Alu() => session.Run(() => Dark(() =>
    {
        using var vm = Shoot("4-Bit ALU", "49-alu.png", 1e-3);
        Assert.Equal(14, vm.Circuit.Components.OfType<Ic74181>().Single().Result);
    }));

    [Fact]
    public void PulseStretcher() => session.Run(() => Dark(() =>
    {
        // Part way through the burst, Q held high.
        using var vm = Shoot("Pulse Stretcher", "50-pulse-stretcher.png", 4.7e-3);
        Assert.True(vm.Circuit.Components.OfType<Ic4538>().Single().IsRunning(0));
    }));

    [Fact]
    public void TwoDigitCounter() => session.Run(() => Dark(() =>
    {
        using var vm = Shoot("Two-Digit Counter", "51-two-digit-counter.png", 42.2e-3);
        Assert.All(vm.Circuit.Components.OfType<SevenSegmentDisplay>(), d => Assert.NotNull(d.DisplayedDigit));
    }));

    [Fact]
    public void StereoSelector() => session.Run(() => Dark(() =>
    {
        using var vm = Shoot("Stereo Selector", "52-stereo-selector.png", 1e-3);
        Assert.Equal(0, vm.Circuit.Components.OfType<Ic4052>().Single().SelectedChannelOf(0));
    }));
}
