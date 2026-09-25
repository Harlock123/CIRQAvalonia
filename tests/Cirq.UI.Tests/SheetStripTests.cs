using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Cirq.UI.Tests.Support;
using Cirq.UI.ViewModels;
using Cirq.UI.Views;

namespace Cirq.UI.Tests;

/// <summary>
/// The tab strip, in the main window, actually laid out.
/// <para>
/// The view models are tested elsewhere; this is the part that has to exist on screen. A strip built
/// from a template binding nothing, or a tab that cannot be clicked because it was laid out past the
/// edge of its own container, is invisible from anywhere except a window — and the clicks here go
/// through hit testing for exactly that reason.
/// </para>
/// </summary>
[Collection(WindowCollection.Name)]
public class SheetStripTests(WindowSession session)
{
    private static (MainWindow Window, MainWindowViewModel Model) Editor()
    {
        var model = new MainWindowViewModel();
        var window = new MainWindow { DataContext = model, Width = 1400, Height = 900 };

        window.Show();
        Settle(window);

        return (window, model);
    }

    /// <summary>
    /// Lets the window catch up: a command that changes the tabs is only a change to a collection
    /// until the dispatcher has run the jobs it queued and the layout pass has built the containers.
    /// </summary>
    private static void Settle(Window window)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(1400, 900));
        window.Arrange(new Rect(0, 0, 1400, 900));
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static IReadOnlyList<Button> Tabs(Window window) =>
        [.. window.GetLogicalDescendants()
            .OfType<Button>()
            .Where(b => b.DataContext is SheetTabViewModel)];

    [Fact]
    public void ThereIsNoStripUntilThereArePages() => session.Run(() =>
    {
        var (window, model) = Editor();

        try
        {
            Assert.Empty(Tabs(window));
            Assert.False(model.HasSheets);
        }
        finally
        {
            window.Close();
            model.Dispose();
        }
    });

    [Fact]
    public void AddingAPagePutsTabsOnScreen() => session.Run(() =>
    {
        var (window, model) = Editor();

        try
        {
            model.AddSheetCommand.Execute(null);

            Settle(window);

            var tabs = Tabs(window);

            Assert.Equal(["Sheet 1", "Sheet 2"], tabs.Select(t => ((SheetTabViewModel)t.DataContext!).Name));

            // Every tab is somewhere a pointer can reach, which a strip that overflowed its row
            // would not be.
            Assert.All(tabs, tab => Assert.True(tab.Bounds.Width > 0 && tab.Bounds.Height > 0));

            // The current page is marked as such rather than merely held in a field.
            var current = tabs.Single(t => ((SheetTabViewModel)t.DataContext!).IsCurrent);
            Assert.Contains("current", current.Classes);
        }
        finally
        {
            window.Close();
            model.Dispose();
        }
    });

    [Fact]
    public void ClickingATabGoesToThatPage() => session.Run(() =>
    {
        var (window, model) = Editor();

        try
        {
            model.AddSheetCommand.Execute(null);
            Assert.Equal("Sheet 2", model.CurrentSheet);

            Settle(window);

            var first = Tabs(window)[0];

            window.Click(first);

            Assert.Equal("Sheet 1", model.CurrentSheet);
            Assert.Contains("current", first.Classes);
        }
        finally
        {
            window.Close();
            model.Dispose();
        }
    });

    [Fact]
    public void TheCanvasIsShowingThePageTheStripSays() => session.Run(() =>
    {
        var (window, model) = Editor();

        try
        {
            var canvas = window.GetLogicalDescendants().OfType<Cirq.UI.Controls.CircuitCanvas>().Single();

            model.AddSheetCommand.Execute(null);

            // The binding, not the field: the canvas filters everything it draws and hit tests
            // through this, so a strip the canvas never heard about would be a strip that does
            // nothing at all.
            Assert.Equal("Sheet 2", canvas.Sheet);

            model.ShowSheetCommand.Execute("Sheet 1");

            Assert.Equal("Sheet 1", canvas.Sheet);
        }
        finally
        {
            window.Close();
            model.Dispose();
        }
    });
}
