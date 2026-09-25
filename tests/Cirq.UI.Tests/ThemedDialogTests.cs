using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Cirq.UI.Services;
using Cirq.UI.Tests.Support;

namespace Cirq.UI.Tests;

/// <summary>
/// The dialogs built in code wear the theme the application is wearing.
/// <para>
/// This is the recovery prompt's bug, which shipped. Those two dialogs are constructed at startup,
/// <b>before the theme variant has settled</b>: asked at that moment the application answers Light,
/// and it becomes Dark a moment later once the windows are realised. A brush fetched there was
/// frozen light into a dialog the rest of the application then painted dark around — pale grey
/// buttons on a pale grey panel, invisible, with the message the only legible thing in the window.
/// </para>
/// <para>
/// The fix was to bind the colour rather than fetch it. What follows is a test of that fix and not
/// of the symptom: it builds a dialog, changes the theme underneath it, and asks what colour it is
/// now. A fetched brush cannot pass this; a bound one cannot fail it.
/// </para>
/// </summary>
[Collection(WindowCollection.Name)]
public class ThemedDialogTests(WindowSession session)
{
    [Theory]
    [InlineData("Discard changes", "Discard", "Cancel")]
    [InlineData("Recover unsaved work", "Recover", "Discard")]
    [InlineData("Something went wrong", "OK", null)]
    public void ACodeBuiltDialogFollowsTheThemeItIsBuiltBefore(
        string title, string confirm, string? cancel) => session.Run(() =>
    {
        var application = Application.Current!;
        var was = application.RequestedThemeVariant;

        try
        {
            // Built while the application says Light, which is exactly the moment these dialogs
            // are really built — at startup, before the desktop has answered.
            application.RequestedThemeVariant = ThemeVariant.Light;

            var built = StorageProviderFileDialogs.BuildDialog(title, "a message", confirm, cancel);
            var dialog = built.Window;

            dialog.Show();

            var light = Background(dialog);

            // And now the desktop answers, the way it does a moment after startup.
            application.RequestedThemeVariant = ThemeVariant.Dark;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var dark = Background(dialog);

            Assert.True(light != dark,
                $"\"{title}\" stayed {light} when the application went dark, so its colour was " +
                "fetched once rather than bound — which is the whole of the bug that shipped.");

            // And the dark one is actually dark, rather than merely different.
            Assert.True(Luminance(dark) < Luminance(light),
                $"\"{title}\" went from {light} to {dark}, which is not the way round it should be");

            dialog.Close();
        }
        finally
        {
            application.RequestedThemeVariant = was;
        }
    });

    /// <summary>
    /// And its buttons can be read against it, whichever theme it settled on. The dialog being the
    /// right colour is only half of it — what was reported was buttons that could not be seen.
    /// </summary>
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void ItsButtonsCanBeReadAgainstIt(string variant) => session.Run(() =>
    {
        var application = Application.Current!;
        var was = application.RequestedThemeVariant;

        try
        {
            application.RequestedThemeVariant = variant == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

            var dialog = StorageProviderFileDialogs
                .BuildDialog("Recover unsaved work", "a message", "Recover", "Discard")
                .Window;

            dialog.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var ground = Background(dialog);

            var buttons = dialog.GetLogicalDescendants().OfType<Button>().ToList();

            Assert.Equal(2, buttons.Count);

            foreach (var button in buttons)
            {
                // A button draws its own chrome, so what has to be legible is the button against
                // the panel — which is what was not, when both were the same pale grey.
                var face = Background(button) ?? ground;

                Assert.True(Math.Abs(Luminance(face) - Luminance(ground)) > 0.01
                            || !ReferenceEquals(button.Background, dialog.Background),
                    $"the {button.Content} button is {face} on a {ground} panel, which cannot be seen");
            }

            dialog.Close();
        }
        finally
        {
            application.RequestedThemeVariant = was;
        }
    });

    private static Color Background(Visual visual) =>
        visual switch
        {
            Window w when w.Background is ISolidColorBrush b => b.Color,
            Button c when c.Background is ISolidColorBrush b => b.Color,
            _ => Colors.Transparent,
        };

    private static Color? Background(Button button) =>
        button.Background is ISolidColorBrush b ? b.Color : null;

    private static double Luminance(Color? c) => c is null ? 0
        : ((0.2126 * c.Value.R) + (0.7152 * c.Value.G) + (0.0722 * c.Value.B)) / 255.0;
}
