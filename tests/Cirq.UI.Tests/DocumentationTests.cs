using System.Text.RegularExpressions;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// Guards the parts of the documentation that can be checked without judgement.
/// <para>
/// Nothing else reads the README or the guide, so until now the only thing keeping them honest was
/// somebody auditing them by hand — and an audit only happens when somebody thinks to do one. The
/// checks here are deliberately mechanical: a link that resolves, a count that matches, a shortcut
/// that appears. None of them can tell whether a section is <i>well written</i>, and none of them
/// tries. What they catch is the documentation quietly falling out of step with the application,
/// which is the failure that actually happens.
/// </para>
/// </summary>
public partial class DocumentationTests
{
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CirqAvalonia.slnx")))
                directory = directory.Parent;

            Assert.NotNull(directory);
            return directory!.FullName;
        }
    }

    private static string Guide => File.ReadAllText(Path.Combine(Root, "docs", "USER_GUIDE.md"));

    private static string Readme => File.ReadAllText(Path.Combine(Root, "README.md"));

    private static string MainWindowXaml =>
        File.ReadAllText(Path.Combine(Root, "src", "Cirq.UI", "Views", "MainWindow.axaml"));

    /// <summary>
    /// GitHub's anchor rule: lower case, punctuation dropped, spaces to hyphens. Written out
    /// rather than guessed at, because every link in the guide depends on it.
    /// </summary>
    private static string Slug(string heading)
    {
        var text = heading.ToLowerInvariant();

        text = PunctuationPattern().Replace(text, string.Empty);

        return WhitespacePattern().Replace(text.Trim(), "-");
    }

    private static List<(int Number, string Title, string Anchor)> Contents(string markdown) =>
        [.. ContentsPattern().Matches(markdown).Select(m =>
            (int.Parse(m.Groups[1].Value), m.Groups[2].Value, m.Groups[3].Value))];

    /// <summary>Every heading in the document, in the order they appear, with where they start.</summary>
    private static List<(string Title, string Anchor, int At)> Headings(string markdown) =>
        [.. HeadingPattern().Matches(markdown)
            .Select(m => (m.Groups[1].Value.Trim(), Slug(m.Groups[1].Value.Trim()), m.Index))];

    // ---- the guide's own structure -----------------------------------------

    /// <summary>
    /// Every entry in the contents points at a heading that exists. A renamed section leaves a
    /// contents entry that goes nowhere, and nothing about reading the file would show it.
    /// </summary>
    [Fact]
    public void EveryContentsEntryPointsAtARealHeading()
    {
        var guide = Guide;
        var anchors = Headings(guide).Select(h => h.Anchor).ToHashSet();

        var broken = Contents(guide).Where(e => !anchors.Contains(e.Anchor)).ToList();

        Assert.True(broken.Count == 0,
            "contents entries with no heading: " +
            string.Join(", ", broken.Select(e => $"{e.Title} -> #{e.Anchor}")));
    }

    /// <summary>And every cross-reference in the body, which there are far more of.</summary>
    [Fact]
    public void EveryInternalLinkResolves()
    {
        var guide = Guide;
        var anchors = Headings(guide).Select(h => h.Anchor).ToHashSet();

        var broken = InternalLinkPattern().Matches(guide)
            .Select(m => m.Groups[1].Value)
            .Where(a => !anchors.Contains(a))
            .Distinct()
            .ToList();

        Assert.True(broken.Count == 0, "links to nowhere: " + string.Join(", ", broken));
    }

    /// <summary>
    /// The contents runs 1, 2, 3 without a gap or a repeat — which is what happens when a section
    /// is inserted and the numbers below it are not renumbered.
    /// </summary>
    [Fact]
    public void TheContentsIsNumberedWithoutGaps()
    {
        var numbers = Contents(Guide).Select(e => e.Number).ToList();

        Assert.NotEmpty(numbers);
        Assert.Equal(Enumerable.Range(1, numbers.Count), numbers);
    }

    /// <summary>
    /// The contents is in the order the sections actually appear.
    /// <para>
    /// This is the one that catches a section inserted in the wrong place — which is easy to do,
    /// because a section is added by finding a heading to put it before and headings are not
    /// unique in the way they look. A contents that disagrees with the document is the symptom.
    /// </para>
    /// </summary>
    [Fact]
    public void TheContentsIsInDocumentOrder()
    {
        var guide = Guide;
        var at = Headings(guide).GroupBy(h => h.Anchor).ToDictionary(g => g.Key, g => g.First().At);

        var entries = Contents(guide).Where(e => at.ContainsKey(e.Anchor)).ToList();
        var positions = entries.Select(e => at[e.Anchor]).ToList();

        for (var i = 1; i < positions.Count; i++)
        {
            Assert.True(positions[i] > positions[i - 1],
                $"'{entries[i].Title}' is listed after '{entries[i - 1].Title}' but appears before it");
        }
    }

    /// <summary>
    /// No two sections share a heading. Two would share an anchor, and every link to either would
    /// silently go to the first.
    /// </summary>
    [Fact]
    public void NoTwoSectionsShareAnAnchor()
    {
        var duplicates = Headings(Guide)
            .Where(h => h.Title.StartsWith("##", StringComparison.Ordinal) is false)
            .GroupBy(h => h.Anchor)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, "duplicated anchors: " + string.Join(", ", duplicates));
    }

    // ---- the guide against the application ---------------------------------

    /// <summary>
    /// Every keyboard shortcut the menu offers is written down, in the guide and in the
    /// application's own list.
    /// <para>
    /// A shortcut nobody can discover is a shortcut nobody uses, and adding one to the menu while
    /// forgetting the two places that list them is exactly the kind of omission that survives
    /// review — the feature works, so nothing complains.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryMenuShortcutIsDocumented()
    {
        var gestures = GesturePattern().Matches(MainWindowXaml)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.NotEmpty(gestures);

        var guide = Squash(Guide);
        var inApp = Squash(MainWindowViewModel.ShortcutReference);

        var missingFromGuide = gestures.Where(g => !guide.Contains(Squash(g), StringComparison.Ordinal)).ToList();
        var missingFromApp = gestures.Where(g => !inApp.Contains(Squash(g), StringComparison.Ordinal)).ToList();

        Assert.True(missingFromGuide.Count == 0,
            "shortcuts not in the guide: " + string.Join(", ", missingFromGuide));

        Assert.True(missingFromApp.Count == 0,
            "shortcuts not in Help > Keyboard Shortcuts: " + string.Join(", ", missingFromApp));
    }

    /// <summary>
    /// Every window the menu can open is mentioned somewhere in the guide. A new analysis that
    /// nobody has written up is the commonest way for a feature to ship invisible.
    /// </summary>
    [Fact]
    public void EveryDialogTheMenuOpensIsMentionedInTheGuide()
    {
        var guide = Guide;

        var missing = DialogPattern().Matches(MainWindowXaml)
            .Select(m => m.Groups[1].Value.Replace("_", string.Empty))
            .Distinct()
            .Where(name => !guide.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(missing.Count == 0, "windows the guide never mentions: " + string.Join(", ", missing));
    }

    // ---- the parts table ---------------------------------------------------

    /// <summary>
    /// The guide's table of categories and counts is the catalogue's, exactly. It is the one place
    /// a reader is told how much is in the palette, and a part added without touching it makes the
    /// guide quietly wrong about the size of the library.
    /// </summary>
    [Fact]
    public void ThePartsTableMatchesTheCatalogue()
    {
        var guide = Guide;

        foreach (var category in ComponentCatalog.Categories)
        {
            var row = Regex.Match(
                guide,
                $@"^\| {Regex.Escape(category.Name)} \| (\d+) \|",
                RegexOptions.Multiline);

            Assert.True(row.Success, $"'{category.Name}' has no row in the guide's parts table");

            Assert.True(int.Parse(row.Groups[1].Value) == category.Items.Count,
                $"the guide says {category.Name} holds {row.Groups[1].Value}, " +
                $"but the catalogue has {category.Items.Count}");
        }
    }

    /// <summary>
    /// And both documents agree with it on the total, which each of them states in prose where a
    /// reader will meet it before the table.
    /// </summary>
    [Fact]
    public void BothDocumentsAgreeOnHowManyPartsThereAre()
    {
        var total = ComponentCatalog.Categories.Sum(c => c.Items.Count);
        var groups = ComponentCatalog.Categories.Count;

        foreach (var (name, text) in new[] { ("the guide", Guide), ("the README", Readme) })
        {
            Assert.True(text.Contains($"{total} components", StringComparison.Ordinal)
                        || text.Contains($"{total} parts", StringComparison.Ordinal),
                $"{name} never says there are {total} parts");

            Assert.DoesNotContain($"{total - 1} components", text, StringComparison.Ordinal);
            Assert.DoesNotContain($"{total + 1} components", text, StringComparison.Ordinal);
        }

        Assert.Contains($"{groups} categories", Guide, StringComparison.Ordinal);
    }

    // ---- patterns ----------------------------------------------------------

    /// <summary>
    /// Compares a shortcut without caring how it was spaced or decorated. The menu writes
    /// <c>Ctrl+Shift+F7</c>, the guide writes <c>`Ctrl` `Shift` `F7`</c> and the in-app list
    /// writes <c>Ctrl+Shift+F7</c> — all the same key, and none of them worth a test failure.
    /// <para>
    /// <c>OemPlus</c> and <c>OemMinus</c> are the zoom keys as Avalonia names them; no
    /// documentation should ever call them that, so they are translated to the keys they are.
    /// </para>
    /// </summary>
    private static string Squash(string text) =>
        text.Replace("OemPlus", "+", StringComparison.Ordinal)
            .Replace("OemMinus", "-", StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Replace("+", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

    [GeneratedRegex(@"^(\d+)\. \[(.+?)\]\(#(.+?)\)$", RegexOptions.Multiline)]
    private static partial Regex ContentsPattern();

    [GeneratedRegex(@"^#{2,4} (.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"\]\(#([a-z0-9-]+)\)")]
    private static partial Regex InternalLinkPattern();

    [GeneratedRegex(@"InputGesture=""([^""]+)""")]
    private static partial Regex GesturePattern();

    [GeneratedRegex(@"<MenuItem Header=""([^""]+)\.\.\.""")]
    private static partial Regex DialogPattern();

    [GeneratedRegex(@"[^\w\s-]")]
    private static partial Regex PunctuationPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
