using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Annotations;

/// <summary>
/// A block of text on the schematic.
/// <para>
/// It stamps nothing and connects to nothing. Its entire purpose is that the explanation and the
/// circuit are on the same piece of paper — "this divider sets the threshold", "probe here",
/// "R7 is deliberately ten times the others". Exports carry it, so a schematic exported as a PNG
/// or a PDF arrives with its own commentary rather than needing a caption written elsewhere.
/// </para>
/// </summary>
public sealed partial class SchematicNote : CircuitComponent, IAnnotation
{
    public SchematicNote()
    {
        Terminals = [];
    }

    public SchematicNote(string text) : this()
    {
        Text = text;
    }

    /// <summary>What it says. Long text wraps at <see cref="WrapWidth"/>.</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = "Note";

    /// <summary>Height of the lettering in world units.</summary>
    [ObservableProperty]
    public partial double TextSize { get; set; } = 13.0;

    /// <summary>How wide the text may run before it wraps, in world units.</summary>
    [ObservableProperty]
    public partial double WrapWidth { get; set; } = 220.0;

    /// <summary>
    /// True to draw it as a heading — larger and heavier. A schematic with three sections reads
    /// far better with three words on it than with a legend somewhere else.
    /// </summary>
    [ObservableProperty]
    public partial bool IsHeading { get; set; }

    public override string ComponentType => "Note";

    public override string DesignatorPrefix => "NOTE";

    public override string ValueLabel => Text;

    /// <summary>Lettering as drawn, which a heading makes half as large again.</summary>
    public double EffectiveTextSize => Math.Max(TextSize, 4.0) * (IsHeading ? 1.5 : 1.0);

    /// <summary>
    /// The text broken into the lines it will be drawn as, wrapping on words.
    /// <para>
    /// Measured by counting characters rather than by asking a font, because the layout has to be
    /// the same whether it is being drawn to a screen, an SVG or a PDF page, and the extents have
    /// to be known before any of those exist.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Lines()
    {
        var source = (Text ?? string.Empty).Replace("\r\n", "\n").Split('\n');

        // Roughly half the text size per character for a proportional face.
        var perLine = Math.Max((int)(WrapWidth / Math.Max(EffectiveTextSize * 0.52, 1.0)), 4);

        List<string> lines = [];

        foreach (var paragraph in source)
        {
            if (paragraph.Length <= perLine) { lines.Add(paragraph); continue; }

            var current = string.Empty;

            foreach (var word in paragraph.Split(' '))
            {
                if (current.Length == 0) { current = word; continue; }
                if (current.Length + 1 + word.Length <= perLine) { current += " " + word; continue; }

                lines.Add(current);
                current = word;
            }

            if (current.Length > 0) lines.Add(current);
        }

        return lines.Count == 0 ? [string.Empty] : lines;
    }

    public double HalfWidth => Math.Max(WrapWidth, 40.0) / 2.0;

    public double HalfHeight =>
        Math.Max(Lines().Count * EffectiveTextSize * 1.25, EffectiveTextSize * 1.25) / 2.0;

    /// <summary>A note is not in the circuit, so it contributes nothing to the matrix.</summary>
    public override void StampMatrix(MnaSystem system, SimulationState state) { }

    partial void OnTextChanged(string value) => NotifyValueChanged();

    partial void OnIsHeadingChanged(bool value) => NotifyValueChanged();
}
