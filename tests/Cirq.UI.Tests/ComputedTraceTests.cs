using Cirq.Components.Passive;
using Cirq.Core.Topology;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// Traces worked out from the recorded ones, driven through the scope rather than the evaluator.
/// </summary>
public class ComputedTraceTests
{
    private static ScopeViewModel Rig()
    {
        var circuit = new Circuit();
        var scope = new ScopeViewModel(circuit);

        foreach (var (label, scale) in new[] { ("In", 1.0), ("Out", 10.0) })
        {
            var resistor = circuit.Add(new Resistor(1e3));
            var probe = scope.AddProbe(resistor.A, label);

            for (var i = 0; i < 20; i++) probe.Record(i * 1e-4, scale * (i + 1));
        }

        return scope;
    }

    [Fact]
    public void ThereAreNoneUntilOneIsAdded()
    {
        var scope = Rig();

        Assert.False(scope.HasComputed);
        Assert.Empty(scope.Computed);
    }

    /// <summary>The commonest expression there is: the ratio of two traces, which is a gain.</summary>
    [Fact]
    public void ARatioOfTwoTracesIsAGain()
    {
        var scope = Rig();

        scope.ExpressionText = "Out / In";
        scope.AddComputedCommand.Execute(null);

        var computed = Assert.Single(scope.Computed);

        Assert.True(scope.HasComputed);

        var samples = scope.Samples(computed);

        Assert.Equal(20, samples.Count);
        Assert.All(samples, s => Assert.Equal(10.0, s.Value, 9));
    }

    /// <summary>The box clears itself on success, so the next one can be typed straight in.</summary>
    [Fact]
    public void AddingClearsTheBox()
    {
        var scope = Rig();

        scope.ExpressionText = "Out - In";
        scope.AddComputedCommand.Execute(null);

        Assert.Equal(string.Empty, scope.ExpressionText);
        Assert.False(scope.HasExpressionProblem);
    }

    /// <summary>Unnamed, it is called what it is.</summary>
    [Fact]
    public void WithoutALabelTheExpressionIsTheLabel()
    {
        var scope = Rig();

        scope.ExpressionText = "Out / In";
        scope.AddComputedCommand.Execute(null);

        Assert.Equal("Out / In", scope.Computed[0].Label);
        Assert.Contains("Out / In", scope.Computed[0].LegendText);
    }

    [Fact]
    public void ALabelIsUsedWhenOneIsGiven()
    {
        var scope = Rig();

        scope.ExpressionText = "Out / In";
        scope.ExpressionLabel = "Gain";
        scope.AddComputedCommand.Execute(null);

        Assert.Equal("Gain", scope.Computed[0].Label);
        Assert.Contains("Gain = Out / In", scope.Computed[0].LegendText);
    }

    // ---- refusing rather than drawing nothing ------------------------------

    /// <summary>
    /// A bad expression is refused rather than added. A computed trace that cannot be worked out
    /// would be an empty line on the plot with nothing to say why.
    /// </summary>
    [Fact]
    public void ABadExpressionIsRefusedAndSaysWhy()
    {
        var scope = Rig();

        scope.ExpressionText = "Out / Nope";
        scope.AddComputedCommand.Execute(null);

        Assert.Empty(scope.Computed);
        Assert.True(scope.HasExpressionProblem);
        Assert.Contains("Nope", scope.ExpressionProblem);
    }

    [Fact]
    public void AnEmptyExpressionIsRefused()
    {
        var scope = Rig();

        scope.AddComputedCommand.Execute(null);

        Assert.Empty(scope.Computed);
        Assert.True(scope.HasExpressionProblem);
    }

    /// <summary>
    /// It checks as it is typed, so the box goes red before the button is pressed rather than
    /// after.
    /// </summary>
    [Fact]
    public void TypingIsCheckedAsItGoes()
    {
        var scope = Rig();

        scope.ExpressionText = "Out / Wrong";
        Assert.True(scope.HasExpressionProblem);

        scope.ExpressionText = "Out / In";
        Assert.False(scope.HasExpressionProblem);

        scope.ExpressionText = "";
        Assert.False(scope.HasExpressionProblem);
    }

    [Fact]
    public void OneCanBeRemovedAgain()
    {
        var scope = Rig();

        scope.ExpressionText = "Out / In";
        scope.AddComputedCommand.Execute(null);

        scope.RemoveComputedCommand.Execute(scope.Computed[0]);

        Assert.False(scope.HasComputed);
        Assert.Empty(scope.Computed);
    }

    /// <summary>
    /// A computed trace whose input has gone since comes back empty rather than throwing. A probe
    /// can be removed after the expression was added, and the scope still has to draw.
    /// </summary>
    [Fact]
    public void ATraceWhoseInputHasGoneIsEmptyRatherThanAnError()
    {
        var scope = Rig();

        scope.ExpressionText = "Out / In";
        scope.AddComputedCommand.Execute(null);

        var computed = scope.Computed[0];

        scope.RemoveProbeCommand.Execute(scope.Probes.Single(p => p.Label == "In"));

        Assert.Empty(scope.Samples(computed));
    }

    /// <summary>Several can be added, and each gets a colour no probe is using.</summary>
    [Fact]
    public void SeveralCanBeAddedAndTheyAreNotProbeColours()
    {
        var scope = Rig();

        foreach (var expression in new[] { "Out / In", "Out - In", "Out * In" })
        {
            scope.ExpressionText = expression;
            scope.AddComputedCommand.Execute(null);
        }

        Assert.Equal(3, scope.Computed.Count);

        var probeColours = scope.Probes.Select(p => p.TraceColor).ToHashSet();

        Assert.All(scope.Computed, c => Assert.DoesNotContain(c.Color, probeColours));
    }
}
