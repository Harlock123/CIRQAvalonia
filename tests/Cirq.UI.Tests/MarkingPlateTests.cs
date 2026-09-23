using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Cirq.Components.Passive;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using Cirq.UI.Rendering;
using Cirq.UI.Services;
using SkiaSharp;

namespace Cirq.UI.Tests;

/// <summary>
/// Draws the guide's picture of the colour codes, using the code that draws them on screen.
/// <para>
/// The same argument as <see cref="DocPlot"/>: an illustration of a feature that is drawn by some
/// second piece of code made for the documentation is free to drift away from the feature. This
/// one calls <see cref="MarkingArt"/> and <see cref="ComponentMarkings"/>, so the plate in the
/// guide is the hover card's own drawing of the hover card's own arithmetic — and each row
/// asserts what it is there to show, so a row that stopped being true fails rather than quietly
/// becoming a misleading picture.
/// </para>
/// </summary>
public class MarkingPlateTests
{
    private const double RowHeight = 42;
    private const double Margin = 18;
    private const double LabelSize = 12;
    private const double ReadingSize = 11;

    /// <summary>
    /// Drawn at twice the size it is laid out at. The card is small on purpose — it sits beside a
    /// part on a schematic — but a picture in a guide is looked at rather than glanced at, and a
    /// six-pixel band reproduced at its screen size is not a band anybody can read a colour off.
    /// </summary>
    private const double Scale = 2;

    private static readonly ImmutableSolidColorBrush Value = new(Color.FromRgb(0x4C, 0xC9, 0xF0));
    private static readonly ImmutableSolidColorBrush Words = new(Color.FromRgb(0x9F, 0xAF, 0xBF));

    /// <summary>A part to show, and the bands it should come out with.</summary>
    private sealed record Row(string Caption, CircuitComponent Part, string Expected);

    private static List<Row> Plate() =>
    [
        // The one everybody meets first, and the one everybody remembers.
        new("A 4k7 at 5 %", new Resistor(4.7e3) { Tolerance = 0.05 },
            "yellow violet red gold"),

        new("10k at 5 %", new Resistor(10e3) { Tolerance = 0.05 },
            "brown black orange gold"),

        // Gold as a multiplier divides, which is the half of the table people forget exists.
        new("4R7 — gold divides", new Resistor(4.7) { Tolerance = 0.05 },
            "yellow violet gold gold"),

        // Tight enough to need three figures, so it grows a band.
        new("1k21 at 1 % — five bands", new Resistor(1.21e3) { Tolerance = 0.01 },
            "brown red brown brown brown"),

        // Counted in microhenries, not henries.
        new("100 µH at 10 % — read in µH", new Inductor(100e-6) { Tolerance = 0.1 },
            "brown black brown silver"),

        // And the printed ones, which are counted in picofarads however large the part is.
        new("100 nF at 20 % — read in pF", new Capacitor(100e-9) { Tolerance = 0.2 }, "104M"),

        new("10 nF at 5 %", new Capacitor(10e-9) { Tolerance = 0.05 }, "103J"),
    ];

    [Fact]
    public void TheGuidesPlateOfCodesIsDrawnFromTheCardsOwnDrawing()
    {
        var rows = Plate();

        var markings = rows
            .Select(r => (r.Caption, r.Expected, Marking: ComponentMarkings.For(r.Part)))
            .ToList();

        // Every row has something to show. A part that lost its marking would otherwise leave a
        // blank line in the guide rather than fail.
        Assert.All(markings, m => Assert.NotNull(m.Marking));

        // And each shows what it was put there to show.
        foreach (var (caption, expected, marking) in markings)
        {
            var actual = marking!.Kind == MarkingKind.Bands
                ? string.Join(" ", marking.Bands.Select(b => b.Colour.ToString().ToLowerInvariant()))
                : marking.Code!.Code;

            Assert.Equal(expected, actual);

            // Nothing on the plate is an approximation: every one of these is a value the code can
            // spell exactly, which is what makes it a reference rather than a set of examples.
            Assert.Null(marking.Note);
            Assert.False(string.IsNullOrWhiteSpace(caption));
        }

        Draw(markings.Select(m => (m.Caption, m.Marking!)).ToList());
    }

    private static void Draw(List<(string Caption, ComponentMarking Marking)> rows)
    {
        var captionWidth = rows.Max(r => SkiaSymbolCanvas.Measure(r.Caption, LabelSize));
        var artWidth = rows.Max(r => MarkingArt.Width(r.Marking));
        var readingWidth = rows.Max(r => SkiaSymbolCanvas.Measure(r.Marking.Readout, ReadingSize));

        var artColumn = Margin + captionWidth + 22;
        var readingColumn = artColumn + artWidth + 22;

        var width = readingColumn + readingWidth + Margin;
        var height = (rows.Count * RowHeight) + (Margin * 2);

        using var surface = SKSurface.Create(new SKImageInfo(
            (int)Math.Ceiling(width * Scale), (int)Math.Ceiling(height * Scale)));

        surface.Canvas.Clear(new SKColor(0x0E, 0x11, 0x16));

        using (var canvas = new SkiaSymbolCanvas(surface.Canvas))
        using (canvas.PushTransform(Matrix.CreateScale(Scale, Scale)))
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var (caption, marking) = rows[i];

                var top = Margin + (i * RowHeight);
                var middle = top + (RowHeight / 2);

                canvas.DrawText(
                    caption, new Point(Margin, middle), LabelSize, Value, SymbolTextAlign.Left);

                MarkingArt.Draw(
                    canvas, marking, new Point(artColumn, middle - (MarkingArt.Height / 2)));

                canvas.DrawText(
                    marking.Readout, new Point(readingColumn, middle),
                    ReadingSize, Words, SymbolTextAlign.Left);
            }
        }

        Directory.CreateDirectory(DocPlot.ImageDirectory);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(Path.Combine(DocPlot.ImageDirectory, "25-colour-codes.png"));

        data.SaveTo(file);
    }
}
