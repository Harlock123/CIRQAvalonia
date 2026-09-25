using System.Text.RegularExpressions;

namespace Cirq.UI.Tests;

/// <summary>
/// Every dialog has a way out that does not depend on the window having a frame.
/// <para>
/// Most desktops draw a title bar with a close button on it, so a dialog that offers nothing of
/// its own is still closeable and nobody notices it offers nothing. A tiling window manager draws
/// no frame at all, and on one of those the same dialog is a trap: the only way out is the window
/// manager's own close binding, which the application never mentioned.
/// </para>
/// <para>
/// Seventeen of twenty-two were like that, and the reason it went unnoticed for so long is that
/// it is invisible on the desktop most development happens on. So it is checked here rather than
/// left to be noticed again.
/// </para>
/// </summary>
public partial class DialogEscapeTests
{
    private static string Views => Path.Combine(DocPlot.RepoRoot, "src", "Cirq.UI", "Views");

    /// <summary>Every dialog's markup. The main window is not a dialog and is left out.</summary>
    public static TheoryData<string> Dialogs()
    {
        var data = new TheoryData<string>();

        foreach (var path in Directory.EnumerateFiles(Views, "*Window.axaml").Order())
        {
            var name = Path.GetFileNameWithoutExtension(path);

            if (name == "MainWindow") continue;

            data.Add(name);
        }

        return data;
    }

    /// <summary>
    /// Every dialog carries a visible control that dismisses it — a button named
    /// <c>CloseButton</c>, which the shared helper wires up, or one marked <c>IsCancel</c>, which
    /// is what a dialog with a result calls the same thing.
    /// </summary>
    [Theory]
    [MemberData(nameof(Dialogs))]
    public void EveryDialogHasAVisibleWayOut(string dialog)
    {
        var markup = File.ReadAllText(Path.Combine(Views, dialog + ".axaml"));

        var named = markup.Contains("Name=\"CloseButton\"", StringComparison.Ordinal);
        var cancels = markup.Contains("IsCancel=\"True\"", StringComparison.Ordinal);

        Assert.True(named || cancels,
            $"{dialog} has no button that closes it. Give it a Button named \"CloseButton\" — the " +
            "shared helper wires the rest — or mark its cancel button IsCancel. On a desktop with " +
            "no title bars there is otherwise no way out of it at all.");
    }

    /// <summary>
    /// And every dialog anywhere is shown through the helper that binds Escape, rather than
    /// through <c>ShowDialog</c> directly. One call site that forgets is one dialog that traps
    /// somebody.
    /// <para>
    /// The whole of the UI project, not just the main window. The first version of this checked
    /// only <c>MainWindow</c>, and the four dialogs built in code elsewhere — confirm, report,
    /// recover, export — went on being shown the old way, unchecked. The recovery one was then
    /// reported as broken.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryDialogIsShownThroughTheHelper()
    {
        var root = Path.Combine(DocPlot.RepoRoot, "src", "Cirq.UI");

        List<string> offenders = [];

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            // The helper is where the one real call lives.
            if (Path.GetFileName(file) == "Dialog.cs") continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            var code = File.ReadAllText(file);

            if (RawShowDialog().IsMatch(code)) offenders.Add(Path.GetFileName(file));
        }

        Assert.True(offenders.Count == 0,
            "these call ShowDialog directly instead of ShowModal, which binds Escape and wires " +
            "the close button: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// A dialog built in code must not fix its colours at construction.
    /// <para>
    /// Both of the code-built ones are made at startup, before the theme variant has settled:
    /// asked then, the application says Light and becomes Dark a moment later. A brush fetched at
    /// that point is frozen light into a dialog the rest of the application paints dark around,
    /// which is pale buttons on a pale panel and is exactly how this was reported. A binding
    /// follows the theme; a fetch does not.
    /// </para>
    /// </summary>
    [Fact]
    public void CodeBuiltDialogsBindTheirColoursRatherThanFetchingThem()
    {
        var path = Path.Combine(
            DocPlot.RepoRoot, "src", "Cirq.UI", "Services", "StorageProviderFileDialogs.cs");

        var code = File.ReadAllText(path);

        Assert.DoesNotContain("ThemeManager.Brush", code, StringComparison.Ordinal);
        Assert.Contains("DynamicResourceExtension", code, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\.ShowDialog\(")]
    private static partial Regex RawShowDialog();
}
