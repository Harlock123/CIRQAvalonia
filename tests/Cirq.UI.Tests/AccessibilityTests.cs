using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Cirq.UI.Tests.Support;

namespace Cirq.UI.Tests;

/// <summary>
/// Everything a person can operate has a name a screen reader can read out.
/// <para>
/// There were none. Not one control in the application carried an
/// <see cref="AutomationProperties"/> name, which meant that sixty-three of its hundred and ten
/// interactive controls announced themselves as nothing at all — every number box in every analysis
/// window, every picker, the temperature slider, the lists of findings. A button with a word on it
/// gets a name from the word; a box you type a frequency into has no word, and the label sitting
/// next to it is a separate piece of text with no connection to it that anything can follow.
/// </para>
/// <para>
/// This is the other half of the keyboard work. Making the sheet operable without a mouse is not
/// much use to somebody who cannot see which control they have landed on.
/// </para>
/// </summary>
[Collection(WindowCollection.Name)]
public class AccessibilityTests(WindowSession session)
{
    /// <summary>
    /// Everything that takes input. A <see cref="TextBlock"/> is not here: it is the label, and it
    /// is read as part of the window rather than operated.
    /// </summary>
    private static bool Operable(Control control) =>
        control is TextBox or ComboBox or Slider or CheckBox or Button or ToggleButton or ListBox;

    /// <summary>
    /// What a screen reader would announce: the name given explicitly, or the words the control
    /// carries as its content.
    /// </summary>
    private static string? Announced(Control control) =>
        AutomationProperties.GetName(control) is { Length: > 0 } given
            ? given
            : (control as ContentControl)?.Content as string;

    [Theory]
    [MemberData(nameof(WindowTests.Windows), MemberType = typeof(WindowTests))]
    public void EveryControlAPersonCanOperateHasAName(string name) => session.Run(() =>
    {
        var window = WindowTests.BuildForTest(name);

        window.Show();

        var nameless = window.GetLogicalDescendants()
            .OfType<Control>()
            .Where(Operable)
            .Where(c => string.IsNullOrWhiteSpace(Announced(c)))
            .Select(c => c.GetType().Name)
            .ToList();

        window.Close();

        Assert.True(nameless.Count == 0,
            $"{name} has {nameless.Count} control(s) a screen reader would announce as nothing: " +
            string.Join(", ", nameless.Distinct()) +
            ". Give each an AutomationProperties.Name saying what it is for.");
    });

    /// <summary>
    /// And the names are worth reading. A name that repeats the control's type — "TextBox",
    /// "ComboBox" — passes the check above and tells somebody nothing, which is worse than failing
    /// it because it looks done.
    /// </summary>
    [Theory]
    [MemberData(nameof(WindowTests.Windows), MemberType = typeof(WindowTests))]
    public void AndTheNamesSaySomething(string name) => session.Run(() =>
    {
        var window = WindowTests.BuildForTest(name);

        window.Show();

        var useless = window.GetLogicalDescendants()
            .OfType<Control>()
            .Where(Operable)
            .Select(Announced)
            .Where(n => n is not null)
            .Where(n => n!.Equals("TextBox", StringComparison.OrdinalIgnoreCase)
                     || n.Equals("ComboBox", StringComparison.OrdinalIgnoreCase)
                     || n.Equals("Slider", StringComparison.OrdinalIgnoreCase)
                     || n.Length < 2)
            .ToList();

        window.Close();

        Assert.True(useless.Count == 0,
            $"{name} names a control after its own type or after nothing: {string.Join(", ", useless)}");
    });

    /// <summary>
    /// The check is worth having only if it would catch the state the application was actually in,
    /// so this is the same walk against a window built without any names.
    /// </summary>
    [Fact]
    public void TheCheckCatchesAnUnnamedControl() => session.Run(() =>
    {
        var window = new Window
        {
            Content = new StackPanel
            {
                Children =
                {
                    new TextBox(),
                    new Button { Content = "Close" },
                },
            },
        };

        window.Show();

        var nameless = window.GetLogicalDescendants()
            .OfType<Control>()
            .Where(Operable)
            .Where(c => string.IsNullOrWhiteSpace(Announced(c)))
            .ToList();

        window.Close();

        // The box is nameless; the button is named by the word on it.
        Assert.Single(nameless);
        Assert.IsType<TextBox>(nameless[0]);
    });
}
