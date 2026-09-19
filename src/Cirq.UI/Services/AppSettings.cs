using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cirq.UI.Services;

/// <summary>Themes the application offers. <see cref="System"/> follows the operating system.</summary>
public enum AppTheme
{
    /// <summary>Follow whatever the desktop reports, and track it if the user changes it.</summary>
    System,
    Light,
    Dark,

    /// <summary>
    /// Maximum-contrast palette for readability: pure black, white text, and the conventional
    /// yellow and cyan accents. Stored by name, so appending it here cannot disturb anyone's
    /// existing preference.
    /// </summary>
    HighContrast,
}

/// <summary>
/// Preferences that outlive a session. Kept deliberately small and additive: new settings get a
/// default so an older file still loads, and unknown keys in a newer file are ignored.
/// </summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

/// <summary>Reads and writes <see cref="AppSettings"/>.</summary>
public interface ISettingsStore
{
    AppSettings Load();

    void Save(AppSettings settings);

    /// <summary>Where the settings live, for the settings dialog to show.</summary>
    string Location { get; }
}

/// <summary>
/// Settings stored as JSON under the user's config directory.
/// <para>
/// Every failure path returns defaults rather than throwing: a preferences file is never worth
/// stopping the application over, and a corrupt one should cost the user their theme choice, not
/// their ability to launch.
/// </para>
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public JsonSettingsStore(string? path = null)
    {
        Location = path ?? DefaultPath();
    }

    public string Location { get; }

    /// <summary>~/.config/CirqAvalonia/settings.json on Linux, the equivalent elsewhere.</summary>
    public static string DefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.Create),
            "CirqAvalonia",
            "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(Location)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Location), Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged or unreadable preferences file falls back to defaults rather than
            // preventing the application from starting.
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(Location);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Write then move, so an interrupted save cannot leave a truncated file behind.
            var temporary = Location + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
            File.Move(temporary, Location, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a preference is not worth surfacing an error dialog for.
        }
    }
}
