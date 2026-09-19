using Avalonia.Styling;
using Cirq.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.UI.ViewModels;

/// <summary>One selectable theme, with the description the settings dialog shows beneath it.</summary>
public sealed record ThemeChoice(AppTheme Theme, string Name, string Description);

/// <summary>
/// The settings dialog's state. Changes apply immediately and are written straight to the store,
/// so there is no OK/Cancel to get wrong and nothing to lose if the application is closed from the
/// window manager rather than the menu.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore _store;
    private readonly AppSettings _settings;
    private bool _loading;

    public SettingsViewModel(ISettingsStore store, AppSettings settings)
    {
        _store = store;
        _settings = settings;

        _loading = true;
        SelectedTheme = Choices.FirstOrDefault(c => c.Theme == settings.Theme) ?? Choices[0];
        _loading = false;

        // What the desktop reports can arrive after the dialog is already on screen — the portal
        // answers asynchronously at startup, and the user can change the system theme while the
        // dialog is open. Without this the note would keep announcing whatever was true at the
        // moment the dialog happened to be built.
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) =>
        OnPropertyChanged(nameof(SystemThemeNote));

    public void Dispose() => ThemeManager.ThemeChanged -= OnThemeChanged;

    /// <summary>
    /// System comes first deliberately: matching the desktop is the choice most people want, and
    /// the one that keeps working when they change their mind at the OS level.
    /// </summary>
    public IReadOnlyList<ThemeChoice> Choices { get; } =
    [
        new(AppTheme.System, "System", "Follow the desktop's light or dark setting"),
        new(AppTheme.Light, "Light", "Always use the light palette"),
        new(AppTheme.Dark, "Dark", "Always use the dark palette"),
        new(AppTheme.HighContrast, "High contrast", "Black and white with strong accents, for maximum legibility"),
    ];

    [ObservableProperty]
    public partial ThemeChoice SelectedTheme { get; set; }

    /// <summary>Where the preferences file lives, shown so the user can find or delete it.</summary>
    public string SettingsLocation => _store.Location;

    /// <summary>
    /// What "System" currently resolves to, or a note that the desktop is not telling us. Worth
    /// showing: on a bare window manager there may be no portal to ask, and silently falling back
    /// to dark would otherwise look like the setting had been ignored.
    /// </summary>
    public string SystemThemeNote
    {
        get
        {
            if (SelectedTheme.Theme != AppTheme.System) return string.Empty;

            if (!ThemeManager.IsSystemThemeDiscoverable())
                return "The desktop does not report a theme here, so the dark palette is used.";

            var effective = ThemeManager.Effective();
            var name = effective == ThemeVariant.Light ? "light" : "dark";
            return $"The desktop currently reports {name}.";
        }
    }

    partial void OnSelectedThemeChanged(ThemeChoice value)
    {
        OnPropertyChanged(nameof(SystemThemeNote));
        if (_loading) return;

        _settings.Theme = value.Theme;
        ThemeManager.Apply(value.Theme);
        _store.Save(_settings);
    }
}
