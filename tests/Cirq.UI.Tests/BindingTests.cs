using System.Text.RegularExpressions;

namespace Cirq.UI.Tests;

/// <summary>
/// The one thing about a binding that nothing else checks.
/// <para>
/// Avalonia compiles bindings against the <c>x:DataType</c> a view declares, so a binding to a
/// property that was renamed or never existed <b>fails the build</b> with AVLN2000. That is a
/// better check than anything a test could do, and a test that walked the XAML looking for the same
/// mistake was written here and then deleted: two hundred lines to re-answer a question the
/// compiler had already answered, with its own scoping rules to get wrong.
/// </para>
/// <para>
/// What the compiler does <i>not</i> object to is a binding whose type is wrong in a way that has a
/// conversion. <c>IsVisible="{Binding Rows.Count}"</c> builds cleanly. It reads as "show this when
/// there are any", and whether it does depends on a conversion nobody wrote down — so it is the
/// kind of thing that works until it silently does not, in a window nobody opens very often. A
/// named boolean says what it means and cannot be wrong quietly.
/// </para>
/// </summary>
public partial class BindingTests
{
    /// <summary>Every view in the application, whether or not it declares a type.</summary>
    public static TheoryData<string> Views()
    {
        var data = new TheoryData<string>();

        foreach (var file in Directory.EnumerateFiles(ViewDirectory, "*.axaml", SearchOption.AllDirectories))
            data.Add(Path.GetFileName(file));

        return data;
    }

    private static string ViewDirectory =>
        Path.Combine(DocPlot.RepoRoot, "src", "Cirq.UI", "Views");

    [Theory]
    [MemberData(nameof(Views))]
    public void NothingShowsAControlBasedOnACount(string fileName)
    {
        var offenders = CountToBoolPattern()
            .Matches(File.ReadAllText(Path.Combine(ViewDirectory, fileName)))
            .Select(m => m.Value)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{fileName} shows a control based on a number rather than a named boolean: " +
            string.Join(", ", offenders));
    }

    /// <summary>
    /// The pattern catches the thing it is for. Without this the test above passes just as happily
    /// on a regular expression that matches nothing at all.
    /// </summary>
    [Theory]
    [InlineData(@"IsVisible=""{Binding Supplies.Count}""", true)]
    [InlineData(@"IsEnabled=""{Binding Rows.Count}""", true)]
    [InlineData(@"IsVisible=""{CompiledBinding Parts.Count}""", true)]
    [InlineData(@"IsVisible=""{Binding HasSupplies}""", false)]
    [InlineData(@"Text=""{Binding Supplies.Count}""", false)]
    public void ThePatternCatchesACountAndNothingElse(string xaml, bool caught) =>
        Assert.Equal(caught, CountToBoolPattern().IsMatch(xaml));

    [GeneratedRegex(@"(IsVisible|IsEnabled|IsChecked)=""\{(?:Compiled)?Binding\s+[^}]*Count\s*\}""")]
    private static partial Regex CountToBoolPattern();
}
