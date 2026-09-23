using Cirq.Components.Explaining;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The explainer against every circuit the application ships.
/// <para>
/// Eighty-seven topologies built out of a hundred and eighty-odd part types is a far harder test
/// of a pattern matcher than anything written by hand for it: the recognisers meet structures
/// nobody wrote them for, and have to stay quiet.
/// </para>
/// </summary>
public class ExplainExampleTests
{
    public static TheoryData<string> AllExamples()
    {
        var data = new TheoryData<string>();

        foreach (var example in Examples.All) data.Add(example.Name);

        return data;
    }

    /// <summary>
    /// Nothing throws, and nothing describes a part that is not there. An explainer runs over
    /// arbitrary topologies and a recogniser that assumes two pins where there are three would
    /// take the window down with it.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllExamples))]
    public void EveryExampleCanBeRead(string name)
    {
        var vm = new MainWindowViewModel();

        try
        {
            vm.Circuit.Clear();
            vm.Scope.ClearProbes();
            Examples.All.Single(e => e.Name == name).Build(vm);

            var found = CircuitExplainer.Explain(vm.Circuit);

            foreach (var explanation in found)
            {
                Assert.False(string.IsNullOrWhiteSpace(explanation.Headline));
                Assert.False(string.IsNullOrWhiteSpace(explanation.Detail));
                Assert.NotEmpty(explanation.Parts);

                // Everything named has to actually be on the sheet.
                foreach (var part in explanation.Parts)
                    Assert.Contains(part, Cirq.Core.Topology.Flattening.Flatten(vm.Circuit.Components));
            }
        }
        finally
        {
            vm.Dispose();
        }
    }

    /// <summary>
    /// And it finds something in the examples that were drawn to <i>be</i> the thing it looks
    /// for. A recogniser that never fires passes every safety test there is.
    /// </summary>
    [Theory]
    [InlineData("RC Low-Pass", "low-pass")]
    [InlineData("555 Astable", "astable")]
    [InlineData("Inverting Amplifier", "inverting amplifier")]
    public void ItRecognisesTheExamplesNamedAfterWhatTheyAre(string example, string expected)
    {
        var vm = new MainWindowViewModel();

        try
        {
            vm.Circuit.Clear();
            vm.Scope.ClearProbes();
            Examples.All.Single(e => e.Name == example).Build(vm);

            var found = CircuitExplainer.Explain(vm.Circuit);

            Assert.Contains(found, e =>
                e.Headline.Contains(expected, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            vm.Dispose();
        }
    }
}
