using Avalonia.Media;
using Avalonia.Media.Immutable;
using Cirq.UI.Services;

namespace Cirq.UI.Rendering;

/// <summary>
/// Brushes, pens and metrics for the schematic renderer, resolved from the active theme.
/// <para>
/// The values are cached because <see cref="Render"/> asks for them many times per frame and the
/// canvas repaints twenty times a second; the cache is dropped whenever the theme changes, so the
/// canvas follows a light/dark switch without needing to know one happened.
/// </para>
/// </summary>
public static class CanvasTheme
{
    private static Palette? _cached;

    static CanvasTheme()
    {
        ThemeManager.ThemeChanged += (_, _) => _cached = null;
    }

    private sealed record Palette(
        IBrush Background,
        IBrush Symbol,
        IBrush SymbolFill,
        IBrush Label,
        IBrush Value,
        IBrush Selection,
        IBrush Wire,
        IBrush Terminal,
        IBrush TerminalHover,
        IBrush Probe,
        IBrush Error,
        Color GridDot,
        Color GridMajor,
        Color SegmentUnlit);

    private static Palette Current => _cached ??= new Palette(
        ThemeManager.Brush("CanvasBackground"),
        ThemeManager.Brush("SymbolStroke"),
        ThemeManager.Brush("SymbolFill"),
        ThemeManager.Brush("SymbolLabel"),
        ThemeManager.Brush("SymbolValue"),
        ThemeManager.Brush("SelectionStroke"),
        ThemeManager.Brush("WireStroke"),
        ThemeManager.Brush("TerminalFill"),
        ThemeManager.Brush("TerminalHover"),
        ThemeManager.Brush("TerminalHover"),
        ThemeManager.Brush("TextError"),
        ThemeManager.Color("CanvasGridDot"),
        ThemeManager.Color("CanvasGridMajor"),
        ThemeManager.Color("SegmentUnlit"));

    /// <summary>Discards the cached palette, forcing the next draw to re-read the theme.</summary>
    public static void Invalidate() => _cached = null;

    /// <summary>
    /// Draws in ink on paper until disposed, whatever the application is wearing.
    /// <para>
    /// Printing a dark theme is two separate problems. The obvious one is that it empties a
    /// cartridge. The one that actually matters is that a dark theme's strokes are <i>light</i>,
    /// so a page cleared to white and drawn with them comes out blank — the circuit is there, in
    /// pale grey, on white paper.
    /// </para>
    /// <para>
    /// The colours are stated here rather than read from the light theme, because what prints
    /// well is its own question: a pale value colour that reads nicely against a light background
    /// on a screen is not necessarily legible on paper through a printer nobody has calibrated.
    /// </para>
    /// <para>
    /// They are <b>immutable</b> brushes, which matters more than it looks. A plain
    /// <see cref="SolidColorBrush"/> is an <c>AvaloniaObject</c>, and an AvaloniaObject checks the
    /// calling thread every time a property is read — so a palette built on one thread and drawn
    /// from another throws on the first stroke. An export is not inherently a UI-thread job, and
    /// the whole point of this palette is that something other than the canvas is drawing.
    /// </para>
    /// </summary>
    public static IDisposable ForPrinting() => new PrintPalette();

    private sealed class PrintPalette : IDisposable
    {
        private readonly Palette? _previous = _cached;

        public PrintPalette()
        {
            var ink = new ImmutableSolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));

            _cached = new Palette(
                Background: Brushes.White,
                Symbol: ink,
                SymbolFill: Brushes.White,
                Label: new ImmutableSolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                Value: new ImmutableSolidColorBrush(Color.FromRgb(0x22, 0x44, 0x88)),
                Selection: ink,
                Wire: ink,
                Terminal: ink,
                TerminalHover: ink,
                Probe: new ImmutableSolidColorBrush(Color.FromRgb(0x88, 0x22, 0x22)),
                Error: new ImmutableSolidColorBrush(Color.FromRgb(0xAA, 0x00, 0x00)),
                GridDot: Color.FromRgb(0xDD, 0xDD, 0xDD),
                GridMajor: Color.FromRgb(0xCC, 0xCC, 0xCC),
                SegmentUnlit: Color.FromRgb(0xE4, 0xE4, 0xE4));
        }

        public void Dispose() => _cached = _previous;
    }

    public static IBrush BackgroundBrush => Current.Background;
    public static IBrush SymbolBrush => Current.Symbol;
    public static IBrush SymbolFill => Current.SymbolFill;
    public static IBrush LabelBrush => Current.Label;
    public static IBrush ValueBrush => Current.Value;
    public static IBrush SelectionBrush => Current.Selection;
    public static IBrush WireBrush => Current.Wire;
    public static IBrush TerminalBrush => Current.Terminal;
    public static IBrush TerminalHoverBrush => Current.TerminalHover;
    public static IBrush ProbeBrush => Current.Probe;

    /// <summary>Used to ring a part that is being used outside its ratings.</summary>
    public static IBrush ErrorBrush => Current.Error;

    public static Color GridDot => Current.GridDot;
    public static Color GridMajor => Current.GridMajor;

    /// <summary>Colour of an unlit seven-segment element, which differs between light and dark.</summary>
    public static Color SegmentUnlit => Current.SegmentUnlit;

    /// <summary>
    /// How strongly something emitting should be drawn, given how hard it is being driven.
    /// <para>
    /// Deliberately not the drive itself. An LED at a tenth of its rated current is plainly,
    /// unmistakably on — nobody looking at the breadboard would call it off — but a tenth of the
    /// way from the unlit colour to the lit one is a smudge you have to go looking for. What a
    /// schematic has to answer first is whether the thing is on at all, and only then how hard.
    /// So anything actually conducting starts half way to full intensity and the rest of the
    /// range separates dim from bright: the ordering stays honest without spending most of the
    /// contrast on currents too small to see.
    /// </para>
    /// </summary>
    public static double Emission(double brightness)
    {
        var driven = Math.Clamp(brightness, 0, 1);

        return driven < 0.02 ? 0.0 : 0.5 + (0.5 * driven);
    }

    /// <summary>The emitted colour of a lit part, adjusted for the canvas it is being drawn on.</summary>
    public static Color Emitted(Color colour) =>
        VisibleAgainst(colour, BackgroundBrush is ISolidColorBrush solid ? solid.Color : Colors.White);

    /// <summary>
    /// An emitted colour, pushed away from the background until it can actually be seen against it.
    /// <para>
    /// A white LED on a light sheet is the case that forces this. Drawn faithfully it is very
    /// nearly the colour of the paper, so a lit one looks like an empty outline — which is to say
    /// like a part that is off, the opposite of what it is trying to show. The same happens to a
    /// deep blue on the dark theme.
    /// </para>
    /// <para>
    /// Nudging it is not much of a lie: a white LED is a blue die behind a phosphor and photographs
    /// distinctly warm, and nobody consults a schematic for the exact hue of the light. Colours
    /// that already stand out are left exactly as they are.
    /// </para>
    /// </summary>
    public static Color VisibleAgainst(Color colour, Color canvas)
    {
        const double wanted = 0.32;

        var sheet = Luminance(canvas);
        var own = Luminance(colour);
        var gap = Math.Abs(own - sheet);

        if (gap >= wanted) return colour;

        // Away from the background: darker on a light canvas, lighter on a dark one.
        var toward = sheet > 0.5 ? 0.0 : 1.0;
        var reach = Math.Abs(toward - own);
        var t = reach < 1e-6 ? 1.0 : Math.Clamp((wanted - gap) / reach, 0, 1);
        var end = (byte)(toward * 255);

        return Color.FromRgb(
            (byte)(colour.R + ((end - colour.R) * t)),
            (byte)(colour.G + ((end - colour.G) * t)),
            (byte)(colour.B + ((end - colour.B) * t)));
    }

    /// <summary>Rough relative luminance, enough to tell light from dark.</summary>
    private static double Luminance(Color colour) =>
        ((0.2126 * colour.R) + (0.7152 * colour.G) + (0.0722 * colour.B)) / 255.0;

    /// <summary>Radius in screen pixels within which a terminal responds to the pointer.</summary>
    public const double TerminalHitRadius = 10.0;

    /// <summary>Drawn radius of a terminal dot, in world units.</summary>
    public const double TerminalRadius = 3.5;

    public const double DefaultGridSize = 10.0;
    public const double MinZoom = 0.1;
    public const double MaxZoom = 5.0;

    public static readonly Typeface LabelTypeface = new("Inter, Segoe UI, sans-serif");

    /// <summary>Builds a pen whose on-screen width stays constant as the canvas zooms.</summary>
    public static IPen Pen(IBrush brush, double screenWidth, double zoom, IDashStyle? dash = null) =>
        new Pen(brush, screenWidth / zoom, dash, PenLineCap.Round, PenLineJoin.Round);
}
