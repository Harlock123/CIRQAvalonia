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
    /// And every dialog is shown through the helper that binds Escape, rather than through
    /// <c>ShowDialog</c> directly. One call site that forgets is one dialog that traps somebody.
    /// </summary>
    [Fact]
    public void EveryDialogIsShownThroughTheHelper()
    {
        var path = Path.Combine(Views, "MainWindow.axaml.cs");
        var code = File.ReadAllText(path);

        var raw = RawShowDialog().Matches(code).Count;

        Assert.True(raw == 0,
            $"MainWindow.axaml.cs calls ShowDialog directly {raw} time(s). Use ShowModal, which " +
            "binds Escape and wires the close button.");

        // And it really is showing them some other way, rather than not showing them at all.
        Assert.True(code.Contains(".ShowModal(this)", StringComparison.Ordinal));
    }

    [GeneratedRegex(@"\.ShowDialog\(")]
    private static partial Regex RawShowDialog();
}
