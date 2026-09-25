using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;

namespace Cirq.UI.Views;

/// <summary>
/// The behaviour every dialog in this application shares: a way out that does not depend on the
/// window having a frame.
/// <para>
/// Most desktops draw a title bar with a close button on it, and a dialog that offers nothing of
/// its own is still closeable there. A tiling window manager draws no frame at all — Hyprland,
/// Sway, i3 — and on one of those a dialog with no button and no key binding is a trap: the only
/// way out is the window manager's own close command, which is not something the application ever
/// told anybody about.
/// </para>
/// <para>
/// So every dialog closes on <b>Escape</b>, and every dialog carries a visible <b>Close</b>
/// button as well. Escape is the convention and costs nothing; the button is what somebody can
/// see, which on a frameless desktop is the difference between a dialog and a trap.
/// </para>
/// </summary>
public static class Dialog
{
    /// <summary>
    /// Shows a window modally over its owner, with Escape closing it.
    /// <para>
    /// Wired here rather than in each window so that a dialog added later cannot forget. There is
    /// a test that no dialog is shown any other way.
    /// </para>
    /// </summary>
    public static Task ShowModal(this Window dialog, Window owner)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(owner);

        CloseOnEscape(dialog);
        WireCloseButton(dialog);

        return dialog.ShowDialog(owner);
    }

    /// <summary>
    /// Wires up a button called <c>CloseButton</c>, if the window has one.
    /// <para>
    /// Centrally, so that a dialog's markup is the whole of what it has to say about closing —
    /// one named button and no code-behind. Seventeen windows would otherwise each carry the same
    /// three lines, and the eighteenth would forget them.
    /// </para>
    /// </summary>
    private static void WireCloseButton(Window dialog)
    {
        // Walked rather than looked up by name. FindControl needs a XAML name scope and throws
        // outright when there is none, which is every window built in code — so the lookup that
        // works for twenty-two dialogs takes down the four that are assembled by hand.
        var button = dialog.GetLogicalDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => b.Name == "CloseButton");

        if (button is null) return;

        button.Click += (_, _) => dialog.Close();
    }

    /// <summary>
    /// Escape closes the window, unless something inside it is using the key first — a combo box
    /// with its list open, or a text box being edited with a suggestion showing, both want Escape
    /// and should not have the whole dialog shut underneath them.
    /// </summary>
    public static void CloseOnEscape(Window dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || e.Handled) return;

            e.Handled = true;
            dialog.Close();
        };
    }
}
