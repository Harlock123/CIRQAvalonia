using Avalonia.Media;
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
