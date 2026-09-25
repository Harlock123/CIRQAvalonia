using Avalonia.Styling;
using Cirq.UI.Services;
using Cirq.UI.ViewModels;

using Cirq.UI.Tests.Support;

namespace Cirq.UI.Tests;

/// <summary>In-memory store so the settings view model can be driven without touching disk.</summary>
internal sealed class FakeSettingsStore : ISettingsStore
{
    private AppSettings _settings = new();

    public int Saves { get; private set; }

    public string Location => "(memory)";

    public AppSettings Load() => _settings.Clone();

    public void Save(AppSettings settings)
    {
        _settings = settings.Clone();
        Saves++;
    }
}

public class SettingsPersistenceTests : IDisposable
{
    private readonly List<string> _paths = [];

    private string TempPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cirq-settings-{Guid.NewGuid():N}", "settings.json");
        _paths.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _paths)
        {
            var directory = Path.GetDirectoryName(path);
            if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AFreshInstallStartsOnTheSystemTheme()
    {
        var store = new JsonSettingsStore(TempPath());

        Assert.Equal(AppTheme.System, store.Load().Theme);
    }

    [Fact]
    public void AChosenThemeSurvivesRestartingTheApplication()
    {
        var path = TempPath();

        // First session picks a theme.
        var first = new JsonSettingsStore(path);
        var settings = first.Load();
        settings.Theme = AppTheme.Light;
        first.Save(settings);

        // Second session, entirely new objects, reads it back.
        var second = new JsonSettingsStore(path);

        Assert.Equal(AppTheme.Light, second.Load().Theme);
    }

    [Fact]
    public void TheSettingsFileIsReadableJson()
    {
        var path = TempPath();
        var store = new JsonSettingsStore(path);
        store.Save(new AppSettings { Theme = AppTheme.Dark });

        var text = File.ReadAllText(path);

        // Stored by name, not by ordinal, so reordering the enum cannot silently change a setting.
        Assert.Contains("\"Theme\": \"Dark\"", text);
    }

    [Fact]
    public void SavingCreatesTheDirectoryAndLeavesNoTemporaryFile()
    {
        var path = TempPath();
        var store = new JsonSettingsStore(path);

        store.Save(new AppSettings { Theme = AppTheme.Dark });

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void ACorruptFileFallsBackToDefaultsRatherThanThrowing()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not settings");

        var settings = new JsonSettingsStore(path).Load();

        Assert.Equal(AppTheme.System, settings.Theme);
    }

    [Fact]
    public void AnUnknownSettingFromANewerBuildIsIgnored()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "Theme": "Light", "SomethingFromTheFuture": 42 }""");

        var settings = new JsonSettingsStore(path).Load();

        Assert.Equal(AppTheme.Light, settings.Theme);
    }

    [Fact]
    public void AnUnknownThemeNameFallsBackToTheDefault()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "Theme": "Solarized" }""");

        Assert.Equal(AppTheme.System, new JsonSettingsStore(path).Load().Theme);
    }

    [Fact]
    public void TheDefaultLocationSitsUnderTheUsersConfigDirectory()
    {
        var path = JsonSettingsStore.DefaultPath();

        Assert.Contains("CirqAvalonia", path);
        Assert.EndsWith("settings.json", path);
        Assert.True(Path.IsPathRooted(path));
    }
}

/// <summary>
/// The settings dialog's view model.
/// <para>
/// In the window collection, and running on its thread, because choosing a theme calls
/// <see cref="Cirq.UI.Services.ThemeManager"/> — which reads and writes <c>Application.Current</c>.
/// Before this suite had a headless application there was no <c>Application.Current</c> at all, so
/// these tests passed down a null-safe path they were never meant to be exercising. The moment one
/// existed they threw, because an Avalonia object belongs to the thread that made it and xunit had
/// called them on another.
/// </para>
/// </summary>
[Collection(WindowCollection.Name)]
public class SettingsViewModelTests(WindowSession session) : IDisposable
{
    /// <summary>
    /// Puts the application's theme back after each test.
    /// <para>
    /// Choosing a theme here really applies it now, to the one application the whole collection
    /// shares — so a test that picks high contrast and walks away leaves the next one reading the
    /// wrong answer. It passed alone and failed in the suite, which is the signature of exactly
    /// this. Before there was an application to apply anything to, none of these tests changed
    /// anything at all.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        session.Run(() => ThemeManager.Apply(AppTheme.System));

        GC.SuppressFinalize(this);
    }

    private static SettingsViewModel Create(AppTheme theme, out FakeSettingsStore store)
    {
        store = new FakeSettingsStore();
        return new SettingsViewModel(store, new AppSettings { Theme = theme });
    }

    [Fact]
    public void SystemIsOfferedFirst()
    {
        session.Run(() =>
        {
            var vm = Create(AppTheme.System, out _);

            Assert.Equal(AppTheme.System, vm.Choices[0].Theme);
            Assert.Equal("System", vm.Choices[0].Name);
        });
    }

    [Fact]
    public void EveryThemeTheApplicationSupportsIsOffered()
    {
        session.Run(() =>
        {
            var vm = Create(AppTheme.System, out _);

            // Adding an AppTheme without a choice would leave it unreachable from the dialog.
            Assert.Equal(
                Enum.GetValues<AppTheme>().ToHashSet(),
                vm.Choices.Select(c => c.Theme).ToHashSet());
        });
    }

    [Fact]
    public void HighContrastCanBeChosenAndIsStored()
    {
        session.Run(() =>
        {
            var vm = Create(AppTheme.System, out var store);

            vm.SelectedTheme = vm.Choices.Single(c => c.Theme == AppTheme.HighContrast);

            Assert.Equal(AppTheme.HighContrast, store.Load().Theme);
        });
    }

    [Fact]
    public void EveryChoiceIsDescribedAndDistinct()
    {
        session.Run(() =>
        {
            var vm = Create(AppTheme.System, out _);

            Assert.Equal(vm.Choices.Count, vm.Choices.Select(c => c.Theme).Distinct().Count());
            Assert.All(vm.Choices, c => Assert.False(string.IsNullOrWhiteSpace(c.Description)));
        });
    }

    [Fact]
    public void TheDialogOpensOnWhicheverThemeIsStored()
    {
        session.Run(() =>
        {
            var vm = Create(AppTheme.Dark, out _);

            Assert.Equal(AppTheme.Dark, vm.SelectedTheme.Theme);
        });
    }

    [Fact]
    public void OpeningTheDialogDoesNotWriteAnything()
    {
        session.Run(() =>
        {
            // Selecting the stored value on load must not count as a change.
            Create(AppTheme.Light, out var store);

            Assert.Equal(0, store.Saves);
        });
    }

    [Fact]
    public void ChoosingAThemeStoresItImmediately()
    {
        session.Run(() =>
        {
            var vm = Create(AppTheme.System, out var store);

            vm.SelectedTheme = vm.Choices.Single(c => c.Theme == AppTheme.Light);

            Assert.Equal(1, store.Saves);
            Assert.Equal(AppTheme.Light, store.Load().Theme);
        });
    }

    [Fact]
    public void TheSystemNoteAppearsOnlyForTheSystemChoice()
    {
        session.Run(() =>
        {
            var vm = Create(AppTheme.System, out _);
            Assert.False(string.IsNullOrWhiteSpace(vm.SystemThemeNote));

            vm.SelectedTheme = vm.Choices.Single(c => c.Theme == AppTheme.Dark);
            Assert.Equal(string.Empty, vm.SystemThemeNote);
        });
    }

    [Fact]
    public void TheDialogShowsWhereSettingsAreKept()
    {
        session.Run(() =>
        {
            var vm = Create(AppTheme.System, out _);

            Assert.False(string.IsNullOrWhiteSpace(vm.SettingsLocation));
        });
    }
}

/// <summary>
/// <see cref="Cirq.UI.Services.ThemeManager"/> itself, which reads and writes the application's
/// theme — so on the window collection's thread, for the reason above.
/// </summary>
[Collection(WindowCollection.Name)]
public class ThemeManagerTests(WindowSession session)
{
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void ExplicitThemesMapToTheMatchingVariant(AppTheme theme)
    {
        var variant = ThemeManager.ToVariant(theme);

        Assert.Equal(theme == AppTheme.Light ? ThemeVariant.Light : ThemeVariant.Dark, variant);
    }

    [Fact]
    public void EveryThemeMapsToADistinctVariant()
    {
        session.Run(() =>
        {
            // Two themes collapsing onto one variant would make a menu entry silently do nothing.
            var variants = Enum.GetValues<AppTheme>().Select(ThemeManager.ToVariant).ToList();

            Assert.Equal(variants.Count, variants.Distinct().Count());
        });
    }

    [Fact]
    public void HighContrastIsACustomVariantThatFallsBackToDark()
    {
        session.Run(() =>
        {
            var variant = ThemeManager.ToVariant(AppTheme.HighContrast);

            Assert.Equal("HighContrast", variant.Key);

            // Inheriting from Dark is what lets the theme dictionary name only the keys that differ,
            // and leaves the Fluent control chrome resolving instead of coming back empty.
            Assert.Equal(ThemeVariant.Dark, variant.InheritVariant);
        });
    }

    [Fact]
    public void SystemMapsToAvaloniasFollowThePlatformVariant()
    {
        session.Run(() =>
        {
            // Default is what makes Avalonia track the desktop rather than pinning a palette.
            Assert.Equal(ThemeVariant.Default, ThemeManager.ToVariant(AppTheme.System));
        });
    }

    /// <summary>
    /// Asked from somewhere it cannot reach the application, it answers this application's own
    /// default rather than throwing.
    /// <para>
    /// Deliberately <b>not</b> run on the session's thread, which is the whole point of it. An
    /// Avalonia object belongs to the thread that made it, and everything that resolves a theme is
    /// reached from drawing code — which is not inherently a UI-thread job, since an export writes
    /// a PDF with no window involved. Somewhere that cannot ask should get the same answer as
    /// somewhere with nothing to ask, and neither should take the render down.
    /// </para>
    /// <para>
    /// This test used to be called "with no application", and was true because there was no
    /// application anywhere in the suite. Now there is one, a thread away, which is a better test
    /// of the same contract.
    /// </para>
    /// </summary>
    [Fact]
    public void AskedFromAnotherThreadItAnswersTheApplicationsOwnDefault()
    {
        // Which thread the application belongs to, asked from inside it.
        var owner = session.Run(() => Environment.CurrentManagedThreadId);

        // And then a thread that is definitely not that one. Started explicitly rather than
        // assuming xunit's is different: when this test ran first, before anything had touched the
        // session, the dispatcher had not been bound yet and xunit's thread *was* the UI thread.
        ThemeVariant? variant = null;
        Avalonia.Media.IBrush? brush = null;
        var ran = 0;

        var elsewhere = new Thread(() =>
        {
            ran = Environment.CurrentManagedThreadId;
            variant = ThemeManager.Effective(null);
            brush = ThemeManager.Brush("PanelBackground");
        });

        elsewhere.Start();
        elsewhere.Join();

        Assert.NotEqual(owner, ran);

        Assert.Equal(ThemeVariant.Dark, variant);

        // And a brush asked from there is the loud fallback rather than an exception.
        Assert.Equal(Avalonia.Media.Brushes.Magenta, brush);
    }
}

/// <summary>
/// Guards the theme dictionaries themselves. A key that exists in one variant but not another
/// resolves to magenta at runtime rather than throwing, so nothing else would catch it; these read
/// the XAML directly and compare the key sets.
/// </summary>
/// <summary>
/// The theme dictionaries, read through the application — so on its thread, for the reason above.
/// </summary>
[Collection(WindowCollection.Name)]
public class ThemeDictionaryTests(WindowSession session)
{
    private static XElement Themes()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CirqAvalonia.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return XElement.Load(Path.Combine(directory!.FullName, "src", "Cirq.UI", "Views", "Themes.axaml"));
    }

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Each variant's key -> colour map, for the contrast checks.</summary>
    private static Dictionary<string, Dictionary<string, string>> Variants(bool withColours)
    {
        _ = withColours;
        return Themes()
            .Elements().Elements()
            .ToDictionary(
                d => d.Attribute(Xaml + "Key")!.Value,
                d => d.Elements().ToDictionary(
                    b => b.Attribute(Xaml + "Key")!.Value,
                    b => b.Attribute("Color")!.Value));
    }

    private static Dictionary<string, HashSet<string>> Variants()
    {
        var dictionaries = Themes()
            .Elements()                                   // ResourceDictionary.ThemeDictionaries
            .Elements()                                   // one ResourceDictionary per variant
            .ToList();

        return dictionaries.ToDictionary(
            d => d.Attribute(Xaml + "Key")!.Value,
            d => d.Elements()
                  .Select(b => b.Attribute(Xaml + "Key")!.Value)
                  .ToHashSet());
    }

    [Fact]
    public void AllThreeVariantsAreDeclared()
    {
        session.Run(() =>
        {
            var variants = Variants();

            Assert.Contains("Dark", variants.Keys);
            Assert.Contains("Light", variants.Keys);

            // The custom variant has to be keyed with x:Static: Avalonia's type converter handles only
            // the built-in names, and a plain string key would not bind to it.
            Assert.Contains("{x:Static services:AppThemes.HighContrast}", variants.Keys);
        });
    }

    [Fact]
    public void EveryVariantDefinesExactlyTheSameKeys()
    {
        session.Run(() =>
        {
            var variants = Variants();
            var expected = variants["Dark"];

            foreach (var (name, keys) in variants)
            {
                Assert.Empty(keys.Except(expected));     // nothing invented
                Assert.Empty(expected.Except(keys));     // nothing missing, which would render magenta
                Assert.NotEmpty(keys);
                Assert.True(keys.Count == expected.Count, name);
            }
        });
    }


    // ---- contrast ---------------------------------------------------------

    /// <summary>Relative luminance per WCAG 2.1, from an #RRGGBB or #AARRGGBB string.</summary>
    private static double Luminance(string hex)
    {
        var body = hex.TrimStart('#');
        if (body.Length == 8) body = body[2..];   // alpha plays no part in the ratio

        static double Channel(int value)
        {
            var c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        var r = Channel(Convert.ToInt32(body[..2], 16));
        var g = Channel(Convert.ToInt32(body.Substring(2, 2), 16));
        var b = Channel(Convert.ToInt32(body.Substring(4, 2), 16));
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }

    private static double Contrast(string a, string b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// Minimum contrast each canvas element must keep against the canvas it is drawn on.
    /// <para>
    /// The grid is deliberately allowed to be quieter than the rest: it is an alignment aid, not
    /// something to read. <c>SymbolFill</c> is absent on purpose — it is the body of a symbol and
    /// is meant to sit very close to the canvas so it occludes wires passing behind without
    /// becoming a filled blob, so a contrast floor would be exactly the wrong requirement.
    /// </para>
    /// </summary>
    public static TheoryData<string, double> CanvasElements => new()
    {
        { "SymbolStroke", 7.0 },
        { "SymbolLabel", 4.5 },
        { "SymbolValue", 4.5 },
        { "SelectionStroke", 4.5 },
        { "WireStroke", 4.5 },
        { "TerminalFill", 4.5 },
        { "TerminalHover", 4.5 },
        { "CanvasGridMajor", 3.0 },
        { "CanvasGridDot", 2.0 },
    };

    [Theory]
    [MemberData(nameof(CanvasElements))]
    public void EveryCanvasElementIsLegibleAgainstTheCanvasInEveryTheme(string key, double minimum)
    {
        foreach (var (variant, colours) in Variants(withColours: true))
        {
            var actual = Contrast(colours[key], colours["CanvasBackground"]);

            Assert.True(
                actual >= minimum,
                $"{key} on {variant}: contrast {actual:F2}:1 is below the {minimum:F1}:1 floor.");
        }
    }

    [Fact]
    public void HighContrastPutsPureWhiteTextOnPureBlackGrounds()
    {
        session.Run(() =>
        {
            var high = Themes()
                .Elements().Elements()
                .Single(d => d.Attribute(Xaml + "Key")!.Value.Contains("HighContrast"));

            string Colour(string key) => high.Elements()
                .Single(b => b.Attribute(Xaml + "Key")!.Value == key)
                .Attribute("Color")!.Value;

            Assert.Equal("#000000", Colour("AppBackground"));
            Assert.Equal("#000000", Colour("CanvasBackground"));
            Assert.Equal("#000000", Colour("PlotBackground"));

            Assert.Equal("#FFFFFF", Colour("TextPrimary"));
            Assert.Equal("#FFFFFF", Colour("SymbolStroke"));
            Assert.Equal("#FFFFFF", Colour("PanelBorder"));
        });
    }
}
