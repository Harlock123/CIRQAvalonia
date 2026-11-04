using Cirq.Components.Digital;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.UI.Rendering;
using Cirq.UI.Services;
using Cirq.UI.Tests.Support;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The guide's pictures of the 0.41 parts, each in the example that shows it, drawn by the
/// application's own renderer after the example has been run long enough to be doing its thing —
/// a lamp lit, a strip showing a frame, a counter partway through its count.
/// <para>
/// Inside the window session for the same reason as <see cref="SheetFigureTests"/>: the canvas
/// colours are theme resources, and without an application to resolve them against the picture
/// comes out in the placeholder colour.
/// </para>
/// </summary>
[Collection(WindowCollection.Name)]
public class ToolboxFigureTests(WindowSession session)
{
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

    /// <summary>Builds an example, runs it for a while, and draws its schematic into the guide's images.</summary>
    private static MainWindowViewModel Shoot(string example, string file, double seconds, Action<MainWindowViewModel>? before = null)
    {
        var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        vm.Scope.ClearProbes();
        Examples.All.Single(e => e.Name == example).Build(vm);
        before?.Invoke(vm);

        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);
        vm.Simulation.Simulator!.Run(seconds);

        Directory.CreateDirectory(DocPlot.ImageDirectory);
        var path = Path.Combine(DocPlot.ImageDirectory, file);

        CircuitExporter.Export(vm.Circuit, null, path, new ExportOptions(ExportFormat.Png, ExportContent.Schematic, RasterScale: 2.0));

        Assert.True(new FileInfo(path).Length > 2000, $"{file} came out almost empty");
        return vm;
    }

    [Fact]
    public void DarlingtonLampDimmer() => session.Run(() => Dark(() =>
    {
        using var vm = Shoot("Darlington Lamp Dimmer", "38-lamp-dimmer.png", 0.15);
        Assert.True(vm.Circuit.Components.OfType<IncandescentLamp>().Single().Brightness > 0.1);
    }));

    [Fact]
    public void RippleCounter() => session.Run(() => Dark(() =>
    {
        // Eleven counted: 1011, so three of the four LEDs lit.
        using var vm = Shoot("Ripple Counter", "39-ripple-counter.png", 11.25e-3);
        Assert.Equal(3, vm.Circuit.Components.OfType<Led>().Count(l => l.Brightness > 0.3));
    }));

    [Fact]
    public void NeoPixelChaser() => session.Run(() => Dark(() =>
    {
        // Five frames in: the head on pixel 4, its tail on 3 and 2.
        using var vm = Shoot("NeoPixel Chaser", "40-neopixel-chaser.png", 1.5e-3);
        Assert.Equal(5, vm.Circuit.Components.OfType<NeoPixelStrip>().Single().FramesShown);
    }));

    [Fact]
    public void KeypadScanner() => session.Run(() => Dark(() =>
        Shoot("Keypad Scanner", "41-keypad-scanner.png", 1.5e-3)));

    [Fact]
    public void AnalogThermometer() => session.Run(() => Dark(() =>
        Shoot("Analog Thermometer", "45-analog-thermometer.png", 0.5e-3)));

    [Fact]
    public void PicoGpio() => session.Run(() => Dark(() =>
        Shoot("Pico GPIO", "42-pico-gpio.png", 2.2e-3)));

    [Fact]
    public void Esp32Gpio() => session.Run(() => Dark(() =>
        Shoot("ESP32 GPIO", "43-esp32-gpio.png", 0.5e-3)));

    [Fact]
    public void FourBitAdder() => session.Run(() => Dark(() =>
    {
        using var vm = Shoot("4-Bit Adder", "44-four-bit-adder.png", 1e-6);
        Assert.Equal(12, vm.Circuit.Components.OfType<Ic74283>().Single().Result);
    }));

    /// <summary>
    /// The lamp's inrush as a plot: the current spiking to ten times its running value as a 12 V
    /// lamp is switched on cold, and the light coming up behind it as the filament heats.
    /// </summary>
    [Fact]
    public void LampInrush()
    {
        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        vm.Scope.ClearProbes();
        Examples.All.Single(e => e.Name == "Lamp Inrush").Build(vm);
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var sim = vm.Simulation.Simulator!;
        var lamp = vm.Circuit.Components.OfType<IncandescentLamp>().Single();

        List<double> times = [];
        List<double> amps = [];
        List<double> light = [];

        while (sim.Time < 0.2)
        {
            // Every step through the spike itself, which is over in a few milliseconds.
            if (sim.Time < 5e-3) sim.Step();
            else sim.Run(0.25e-3);

            times.Add(sim.Time * 1e3);
            amps.Add(lamp.Current);
            light.Add(Math.Min(lamp.LightOutput, 1.2));
        }

        Assert.True(amps.Max() > 8 * amps[^1], $"the inrush peaked at {amps.Max():0.00} A against {amps[^1]:0.00} A");

        DocPlot.Stack("37-lamp-inrush.png", (current, output) =>
        {
            DocPlot.Style(current, string.Empty, "Lamp current (A)");
            DocPlot.Line(current, times, amps, 0);
            DocPlot.Reference(current, lamp.RatedPower / lamp.RatedVoltage, null);
            DocPlot.Title(current, "A 12 V 5 W lamp switched on cold: over 4 A for a moment, settling to 0.42 A");

            DocPlot.Style(output, "Time (ms)", "Light (fraction of rated)");
            DocPlot.Line(output, times, light, 3);
        });
    }
}
