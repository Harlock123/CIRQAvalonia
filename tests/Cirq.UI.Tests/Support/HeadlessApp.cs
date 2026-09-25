using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Cirq.UI;

namespace Cirq.UI.Tests.Support;

/// <summary>
/// The application window tests run inside, and the session that runs them.
/// <para>
/// Everything in this suite up to now tested view models, which is most of the application and not
/// the part that has ever shipped broken. Two dialogs went out unusable in consecutive releases —
/// one with no way to close it, one drawn in the light palette with its buttons invisible against
/// the dark window around them — and neither was a view model problem. A card that says a button
/// exists is not a button anybody can press, and until this existed nothing in three thousand tests
/// had ever constructed a window.
/// </para>
/// <para>
/// It is the real <see cref="App"/>, with the real styles and the real themes, on a platform that
/// draws into memory instead of onto a screen. That matters: a harness that built its own
/// stripped-down application would be testing a second application, and the bugs worth catching are
/// in what the real one does on the way up.
/// </para>
/// </summary>
public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
        .WithInterFont();
}

/// <summary>
/// One Avalonia session, shared by every test that needs a window.
/// <para>
/// Shared because Avalonia is a single-instance affair: its dispatcher, its styles and its theme
/// are static, and starting a second session inside the same process is not a second application
/// but a fight over the first. So it is a collection fixture, which xunit creates once and hands to
/// every class in the collection — and those classes therefore do not run in parallel with each
/// other, which is the same constraint stated a different way.
/// </para>
/// <para>
/// The base <c>Avalonia.Headless</c> package rather than <c>Avalonia.Headless.XUnit</c>: from 12.0
/// that one depends on xunit v3, and this suite is written against v2. Driving the session by hand
/// is the dozen lines below.
/// </para>
/// </summary>
public sealed class WindowSession : IDisposable
{
    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));

    /// <summary>
    /// Runs something on the UI thread and waits for it.
    /// <para>
    /// Everything touching a control has to be on that thread — Avalonia checks, and throws — so a
    /// test body goes through here rather than running where xunit called it.
    /// </para>
    /// </summary>
    public void Run(Action body) => _session.Dispatch(body, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>The same, for a body that produces an answer to assert on outside.</summary>
    public T Run<T>(Func<T> body) => _session.Dispatch(body, CancellationToken.None).GetAwaiter().GetResult();

    public void Dispose() => _session.Dispose();
}

/// <summary>Marks the classes that share the one session. See <see cref="WindowSession"/>.</summary>
[CollectionDefinition(Name)]
public sealed class WindowCollection : ICollectionFixture<WindowSession>
{
    public const string Name = "Windows";
}

/// <summary>Pressing things in a window the way a person would.</summary>
public static class Clicking
{
    /// <summary>
    /// Clicks a control by putting the pointer on it, pressing and releasing.
    /// <para>
    /// A real click rather than a raised <c>Click</c> event, and the difference matters twice
    /// over. A raised event does not invoke a button's <c>Command</c> — Avalonia runs that from
    /// its own click handling — so a test that raises one passes against a button bound to a
    /// command that would never fire. And a real click goes through hit testing, so a button
    /// covered by something else, or laid out past the edge of its window, fails here and cannot
    /// fail anywhere earlier.
    /// </para>
    /// </summary>
    public static void Click(this Window window, Control control)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(control);

        window.Measure(window.ClientSize);
        window.Arrange(new Rect(window.ClientSize));

        var centre = control.TranslatePoint(
            new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);

        Assert.True(centre is not null, "the control is not in this window's visual tree");

        window.MouseDown(centre!.Value, MouseButton.Left);
        window.MouseUp(centre.Value, MouseButton.Left);

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The first visible button whose caption is this, or null.</summary>
    public static Button? ButtonSaying(this Window window, string caption) =>
        window.GetLogicalDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => b.IsVisible && (b.Content as string) == caption);
}
