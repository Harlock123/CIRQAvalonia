using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Cirq.UI.Services;

/// <summary>
/// Theme variants beyond Avalonia's built-in Light and Dark.
/// <para>
/// A custom variant has to be a static instance: Avalonia's XAML type converter handles only the
/// built-in names, so the theme dictionary keys it with <c>x:Static</c> against this. Inheriting
/// from Dark means anything not overridden — the Fluent control chrome, mostly — still resolves
/// rather than coming back empty.
/// </para>
/// </summary>
public static class AppThemes
{
    public static readonly ThemeVariant HighContrast = new("HighContrast", ThemeVariant.Dark);
}

/// <summary>
/// Applies the chosen theme and tells the rest of the application when it changes.
/// <para>
/// The canvas and the scope are drawn in code rather than declared in XAML, so they cannot simply
/// bind to a resource. They read their colours through <see cref="Brush"/> here instead, which
/// resolves against the active theme dictionary — one palette, two consumers.
/// </para>
/// </summary>
public static class ThemeManager
{
    /// <summary>Raised after the active theme changes, so code-drawn surfaces can repaint.</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>The theme the user selected, which may be <see cref="AppTheme.System"/>.</summary>
    public static AppTheme Selected { get; private set; } = AppTheme.System;

    /// <summary>Translates a stored preference into an Avalonia variant.</summary>
    public static ThemeVariant ToVariant(AppTheme theme) => theme switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.Dark => ThemeVariant.Dark,
        AppTheme.HighContrast => AppThemes.HighContrast,
        // Default is Avalonia's "follow the platform", and it keeps tracking the OS afterwards.
        _ => ThemeVariant.Default,
    };

    /// <summary>
    /// The variant actually in force. With <see cref="AppTheme.System"/> this is whatever the
    /// desktop reports, so it is what the settings dialog shows the user.
    /// </summary>
    public static ThemeVariant Effective(Application? application = null)
    {
        application ??= Application.Current;
        if (application is null) return ThemeVariant.Dark;

        // Avalonia's own resolved variant, rather than re-deriving one from PlatformSettings.
        // Deriving it separately let the two drift: GetColorValues() answers Light until the
        // desktop portal has replied, so the settings dialog announced "the desktop currently
        // reports light" while every themed brush had already resolved dark around it. Asking
        // Avalonia means there is one answer, and it keeps tracking the platform on its own.
        return application.ActualThemeVariant;
    }

    /// <summary>True when the desktop actually reports a preference we can follow.</summary>
    public static bool IsSystemThemeDiscoverable(Application? application = null)
    {
        application ??= Application.Current;
        return application?.PlatformSettings is not null;
    }

    public static void Apply(AppTheme theme, Application? application = null)
    {
        application ??= Application.Current;
        if (application is null) return;

        Selected = theme;
        application.RequestedThemeVariant = ToVariant(theme);
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Re-raises the change notification, used when the platform theme itself moves.</summary>
    public static void NotifyChanged() => ThemeChanged?.Invoke(null, EventArgs.Empty);

    /// <summary>
    /// Resolves a semantic brush from the active theme. Falls back to magenta rather than throwing,
    /// so a missing key is loudly visible during development instead of crashing a render pass.
    /// </summary>
    public static IBrush Brush(string key)
    {
        var application = Application.Current;
        if (application is not null &&
            application.TryGetResource(key, Effective(application), out var value) &&
            value is IBrush brush)
        {
            return brush;
        }

        return Brushes.Magenta;
    }

    /// <summary>The colour behind a semantic brush, for the places that need a colour directly.</summary>
    public static Color Color(string key) =>
        Brush(key) is ISolidColorBrush solid ? solid.Color : Colors.Magenta;
}
