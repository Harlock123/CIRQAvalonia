using Cirq.Components.Hierarchy;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.UI.Rendering;
using Cirq.UI.Services;
using Cirq.UI.Tests.Support;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The guide's picture of a drawn symbol: the same block before and after, drawn by the renderer
/// that draws the canvas. Inside the window session, because the canvas colours are theme resources
/// and a renderer with no application to resolve them against draws everything in the placeholder
/// colour.
/// </summary>
[Collection(WindowCollection.Name)]
public class SymbolFigureTests(WindowSession session)
{
    [Fact]
    public void BeforeAndAfter() => session.Run(() =>
    {
        var was = ThemeManager.Selected;

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

    /// <summary>
    /// A gain stage — an op-amp with its input and feedback resistors — grouped into a block, with
    /// everything it talks to left outside so that grouping brings the pins out.
    /// </summary>
    private static Subcircuit Stage(Circuit circuit, double x, double y)
    {
        var amplifier = circuit.Add(new Cirq.Components.Ics.OperationalAmplifier { X = x, Y = y });
        var input = circuit.Add(new Resistor(10e3) { X = x - 130, Y = y });
        var feedback = circuit.Add(new Resistor(100e3) { X = x, Y = y - 90 });

        circuit.Connect(input.B, amplifier.Inverting);
        circuit.Connect(feedback.A, amplifier.Inverting);
        circuit.Connect(feedback.B, amplifier.Output);

        // Outside the group, so each of these becomes a pin on the block.
        var source = circuit.Add(new FunctionGenerator(Waveform.Sine, 1e3, 0.4) { X = x - 280, Y = y });
        var load = circuit.Add(new Resistor(10e3) { X = x + 170, Y = y + 70, RotationDegrees = 90 });
        var positive = circuit.Add(new DcVoltageSource(15) { X = x - 60, Y = y - 190 });
        var negative = circuit.Add(new DcVoltageSource(-15) { X = x + 60, Y = y + 190 });
        var ground = circuit.Add(new Ground { X = x - 280, Y = y + 120 });

        circuit.Connect(source.A, input.A);
        circuit.Connect(source.B, ground.Pin);
        circuit.Connect(amplifier.NonInverting, ground.Pin);
        circuit.Connect(amplifier.Output, load.A);
        circuit.Connect(load.B, ground.Pin);
        circuit.Connect(amplifier.PositiveSupply, positive.Positive);
        circuit.Connect(positive.Negative, ground.Pin);
        circuit.Connect(amplifier.NegativeSupply, negative.Negative);
        circuit.Connect(negative.Positive, ground.Pin);

        var block = Grouping.Group(circuit, [amplifier, input, feedback], "Gain x10")!;

        block.X = x;
        block.Y = y;

        return block;
    }

    private static void Draw()
    {
        var circuit = new Circuit { Title = "Symbols" };

        // Left: the block as it arrives, pins alternating down the sides.
        var plain = Stage(circuit, 200, 260);

        // Right: the same block with a symbol drawn for it.
        var drawn = Stage(circuit, 620, 260);

        var vm = new SymbolViewModel(drawn) { Label = "Gain ×10", Width = 200, Height = 170 };

        // Inputs on the left, the output on the right, the supplies top and bottom — the way a part
        // is drawn rather than the way a block arranges itself.
        (string Name, PinSide Side, double Along)[] wanted =
        [
            ("IN", PinSide.Left, -30),
            ("REF", PinSide.Left, 30),
            ("OUT", PinSide.Right, 0),
            ("V+", PinSide.Top, 0),
            ("V-", PinSide.Bottom, 0),
        ];

        for (var i = 0; i < vm.Pins.Count && i < wanted.Length; i++)
        {
            vm.Pins[i].Name = wanted[i].Name;
            vm.Pins[i].Side = wanted[i].Side;
            vm.Pins[i].Along = wanted[i].Along;
        }

        Assert.True(drawn.HasOwnSymbol);
        Assert.False(plain.HasOwnSymbol);

        var path = Path.Combine(DocPlot.ImageDirectory, "34-symbol.png");

        Directory.CreateDirectory(DocPlot.ImageDirectory);

        CircuitExporter.Export(
            circuit, null, path,
            new ExportOptions(ExportFormat.Png, ExportContent.Schematic, RasterScale: 2.0));

        Assert.True(new FileInfo(path).Length > 2000, "the export produced almost nothing");
    }
}
