using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The About box is only worth having if what it says is true, and the way it stays true is by
/// reading everything rather than repeating it. These check it is still reading.
/// </summary>
public class AboutTests
{
    private readonly AboutViewModel _about = new();

    /// <summary>
    /// Every version comes off a loaded assembly. A hard-coded list would pass a test that only
    /// asserted the strings were non-empty, so this asserts they look like versions.
    /// </summary>
    [Fact]
    public void EveryLibraryReportsARealVersion()
    {
        Assert.NotEmpty(_about.Libraries);

        foreach (var library in _about.Libraries)
        {
            Assert.NotEqual("unknown", library.Version);
            Assert.Matches(@"^\d+\.\d+", library.Version);
            Assert.NotEmpty(library.Name);
            Assert.NotEmpty(library.Purpose);
        }
    }

    /// <summary>The four the application is actually built on.</summary>
    [Theory]
    [InlineData("Avalonia")]
    [InlineData("ScottPlot")]
    [InlineData("SkiaSharp")]
    [InlineData("CommunityToolkit.Mvvm")]
    public void TheLibrariesTheApplicationIsBuiltOnAreListed(string name)
    {
        Assert.Contains(_about.Libraries, l => l.Name == name);
    }

    /// <summary>
    /// The versions have to agree with what is really loaded, which is the whole point of reading
    /// them. Avalonia is the one that would be noticed first if it drifted.
    /// </summary>
    [Fact]
    public void TheReportedVersionIsTheOneActuallyLoaded()
    {
        var avalonia = _about.Libraries.Single(l => l.Name == "Avalonia");
        var loaded = typeof(Avalonia.Application).Assembly.GetName().Version;

        Assert.NotNull(loaded);
        Assert.StartsWith($"{loaded.Major}.{loaded.Minor}", avalonia.Version);
    }

    /// <summary>
    /// A build from a working copy has no version stamped on it, and saying "1.0.0" would be a
    /// lie that looks like a release.
    /// </summary>
    [Fact]
    public void AnUnstampedBuildSaysSoRatherThanClaimingAVersion()
    {
        Assert.NotEqual("1.0.0", _about.Version);
        Assert.NotEmpty(_about.Version);
    }

    /// <summary>The palette is counted, not quoted, so the figure cannot go stale.</summary>
    [Fact]
    public void TheContentsAreCountedFromTheCatalogue()
    {
        var components = ComponentCatalog.Categories.Sum(c => c.Items.Count);

        Assert.Contains(components.ToString(), _about.Contents);
        Assert.Contains(ComponentCatalog.Categories.Count.ToString(), _about.Contents);
        Assert.Contains(Examples.All.Count.ToString(), _about.Contents);
    }

    /// <summary>
    /// What lands on the clipboard has to carry everything a bug report needs, because the reason
    /// the button exists is that people transcribe version numbers wrongly.
    /// </summary>
    [Fact]
    public void TheCopiedTextCarriesEverythingWorthPasting()
    {
        var text = _about.CopyText;

        Assert.Contains(_about.ApplicationName, text);
        Assert.Contains(_about.Version, text);
        Assert.Contains(_about.RepositoryUrl, text);
        Assert.Contains(_about.Runtime, text);

        foreach (var library in _about.Libraries)
        {
            Assert.Contains(library.Name, text);
            Assert.Contains(library.Version, text);
        }
    }

    /// <summary>
    /// Linux reports its whole kernel build line for the OS, which is three times the width of
    /// the dialog. It is shortened rather than allowed to stretch the window.
    /// </summary>
    [Fact]
    public void ThePlatformLineStaysShortEnoughForTheDialog()
    {
        Assert.NotEmpty(_about.Platform);
        Assert.True(_about.Platform.Length <= 72, $"'{_about.Platform}' is too long for the dialog");
    }
}
