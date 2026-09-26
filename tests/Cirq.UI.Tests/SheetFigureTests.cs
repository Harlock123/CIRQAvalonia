using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.UI.Rendering;
using Cirq.UI.Services;
using Cirq.UI.Tests.Support;

namespace Cirq.UI.Tests;

/// <summary>
/// The guide's picture of a sheet, drawn by the application's own renderer.
/// <para>
/// Inside the window session, and for a reason: the canvas colours are theme resources, and a
/// renderer with no application to resolve them against draws everything in the placeholder colour —
/// a page of solid magenta, which is what the first attempt at this produced. Running it here means
/// the picture is drawn with the real palette, by the code that draws the screen.
/// </para>
/// </summary>
[Collection(WindowCollection.Name)]
public class SheetFigureTests(WindowSession session)
{
    [Fact]
    public void OffPageLabels() => session.Run(() =>
    {
        var was = ThemeManager.Selected;

        // The guide's other pictures are dark, so this one is too.
        ThemeManager.Apply(AppTheme.Dark);
        CanvasTheme.Invalidate();

        try
        {
            Draw();
        }
        finally
        {
            ThemeManager.Apply(was);
            CanvasTheme.Invalidate();
        }
    });

    private static void Draw()
    {
        var circuit = new Circuit();

        circuit.Sheets.Add("Power");
        circuit.Sheets.Add("Logic");

        // A little supply page: source, series resistor, smoothing capacitor, and a divider
        // whose tap is the sense point. Laid out on one horizontal run so the wires are straight.
        var supply = circuit.Add(new DcVoltageSource(5.0) { Name = "V1", X = 140, Y = 240, Sheet = "Power" });
        var series = circuit.Add(new Resistor(100) { Name = "R1", X = 320, Y = 240, Sheet = "Power" });
        var smoothing = circuit.Add(new Capacitor(100e-6)
        {
            Name = "C1", X = 460, Y = 340, RotationDegrees = 90, Sheet = "Power",
        });

        var top = circuit.Add(new Resistor(10e3)
        {
            Name = "R2", X = 600, Y = 320, RotationDegrees = 90, Sheet = "Power",
        });

        var bottom = circuit.Add(new Resistor(10e3)
        {
            Name = "R3", X = 600, Y = 460, RotationDegrees = 90, Sheet = "Power",
        });

        var ground = circuit.Add(new Ground { Name = "GND1", X = 140, Y = 440, Sheet = "Power" });

        var rail = circuit.Add(new NetLabel("VCC") { Name = "L1", X = 800, Y = 240, Sheet = "Power" });
        var sense = circuit.Add(new NetLabel("SENSE") { Name = "L2", X = 800, Y = 390, Sheet = "Power" });

        // The other page carries VCC as well, and nothing called SENSE.
        circuit.Add(new NetLabel("VCC") { Name = "L3", X = 140, Y = 140, Sheet = "Logic" });

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, series.A);
        circuit.Connect(series.B, smoothing.A);
        circuit.Connect(smoothing.B, ground.Pin);

        circuit.Connect(series.B, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);

        circuit.Connect(series.B, rail.Pin);
        circuit.Connect(top.B, sense.Pin);

        // What the picture is claiming, asserted rather than left to the eye.
        Assert.Equal("also on Logic", SheetCrossings.Describe(circuit, rail));
        Assert.Empty(SheetCrossings.Describe(circuit, sense));

        var path = Path.Combine(DocPlot.ImageDirectory, "32-off-page.png");

        Directory.CreateDirectory(DocPlot.ImageDirectory);

        CircuitExporter.Export(
            circuit, null, path,
            new ExportOptions(ExportFormat.Png, ExportContent.Schematic, RasterScale: 2.0, Sheet: "Power"));

        // Only this page: the Logic label is on the other one and must not be in the picture.
        Assert.True(new FileInfo(path).Length > 2000, "the export produced almost nothing");
    }
}
