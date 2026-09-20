using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Cirq.UI.ViewModels;

/// <summary>One of the libraries the application is built on.</summary>
/// <param name="Name">The package name, as it appears on NuGet.</param>
/// <param name="Version">The version actually loaded, read off the assembly rather than a list.</param>
/// <param name="Licence">Its licence, taken from the package's own metadata.</param>
/// <param name="Purpose">What it does here, so the list means something to a reader.</param>
public sealed record LibraryInfo(string Name, string Version, string Licence, string Purpose);

/// <summary>
/// What the About box knows.
/// <para>
/// Every version is read from the loaded assembly rather than written down, because a list of
/// versions maintained by hand is a list of versions that is wrong. Upgrade a package and this
/// follows without anybody remembering to edit it.
/// </para>
/// </summary>
public sealed class AboutViewModel
{
    public string ApplicationName => "CirqAvalonia";

    public string Tagline => "Electronic circuit analyzer and mixed-signal simulator";

    /// <summary>
    /// The application's version. Released builds are stamped by the publish script; a build from
    /// a working copy has no version to stamp, and says so rather than claiming 1.0.
    /// </summary>
    public string Version { get; } = ReadVersion(Assembly.GetEntryAssembly()) is { } version
                                     && version != "1.0.0"
        ? version
        : "development build";

    public string Runtime => RuntimeInformation.FrameworkDescription;

    public string Platform =>
        $"{Describe(RuntimeInformation.OSDescription)} · {RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";

    public string RepositoryUrl => "https://github.com/Harlock123/CIRQAvalonia";

    /// <summary>
    /// What is actually in the palette, counted rather than quoted — the same reason the versions
    /// are read from the assemblies.
    /// </summary>
    public string Contents
    {
        get
        {
            var categories = ComponentCatalog.Categories.Count;
            var components = ComponentCatalog.Categories.Sum(c => c.Items.Count);

            return $"{components} components in {categories} categories · {Examples.All.Count} worked examples";
        }
    }

    /// <summary>The libraries the application is built on, in the order they matter to it.</summary>
    public IReadOnlyList<LibraryInfo> Libraries { get; } =
    [
        new("Avalonia", ReadVersion(typeof(Avalonia.Application).Assembly) ?? "unknown", "MIT",
            "the cross-platform UI the editor is drawn with"),
        new("ScottPlot", ReadVersion(typeof(ScottPlot.Plot).Assembly) ?? "unknown", "MIT",
            "the oscilloscope's plotting"),
        new("SkiaSharp", ReadVersion(typeof(SkiaSharp.SKCanvas).Assembly) ?? "unknown", "MIT",
            "rendering, and the SVG and PDF export"),
        new("CommunityToolkit.Mvvm", ReadVersion(typeof(CommunityToolkit.Mvvm.ComponentModel.ObservableObject).Assembly) ?? "unknown", "MIT",
            "observable properties and commands"),
    ];

    /// <summary>
    /// The whole thing as plain text, for pasting into a bug report. An About box that cannot be
    /// copied out of makes the reader transcribe version numbers by hand, and they get them wrong.
    /// </summary>
    public string CopyText
    {
        get
        {
            var text = new StringBuilder();

            text.AppendLine($"{ApplicationName} {Version}");
            text.AppendLine(Runtime);
            text.AppendLine(Platform);
            text.AppendLine(Contents);
            text.AppendLine();

            var width = Libraries.Max(l => l.Name.Length);

            foreach (var library in Libraries)
                text.AppendLine($"{library.Name.PadRight(width)}  {library.Version}  ({library.Licence})");

            text.AppendLine();
            text.AppendLine(RepositoryUrl);

            return text.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// The informational version, with any build metadata trimmed off. Released builds carry a
    /// plain "0.16.0"; a local build appends "+" and a commit hash, which is noise in a dialog.
    /// </summary>
    private static string? ReadVersion(Assembly? assembly)
    {
        if (assembly is null) return null;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrEmpty(informational))
        {
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString();
    }

    /// <summary>
    /// Shortens the operating system string. Linux reports its whole kernel build line, which is
    /// three times the width of the dialog and tells the reader nothing they wanted.
    /// </summary>
    private static string Describe(string osDescription)
    {
        var text = osDescription.Trim();
        var hash = text.IndexOf('#');

        if (hash > 0) text = text[..hash].Trim();

        return text.Length > 48 ? text[..48].TrimEnd() + "…" : text;
    }
}
