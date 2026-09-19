using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

public class ScopeViewModelTests
{
    private static (Circuit Circuit, ScopeViewModel Scope, Resistor R) Fixture()
    {
        var circuit = new Circuit();
        var r = circuit.Add(new Resistor(1e3));
        return (circuit, new ScopeViewModel(circuit), r);
    }

    [Fact]
    public void ProbingTheSameTerminalTwiceReusesTheExistingTrace()
    {
        var (_, scope, r) = Fixture();

        var first = scope.AddProbe(r.A, "Node A");
        var second = scope.AddProbe(r.A, "Node A again");

        Assert.Same(first, second);
        Assert.Single(scope.Probes);
    }

    [Fact]
    public void EachNewProbeTakesTheNextColourFromThePalette()
    {
        var (circuit, scope, r) = Fixture();
        var r2 = circuit.Add(new Resistor());

        var a = scope.AddProbe(r.A);
        var b = scope.AddProbe(r2.A);

        Assert.NotEqual(a.TraceColor, b.TraceColor);
    }

    [Theory]
    [InlineData(1e-3, 1, 2e-3)]
    [InlineData(2e-3, 1, 5e-3)]
    [InlineData(5e-3, 1, 1e-2)]
    [InlineData(1e-3, -1, 5e-4)]
    [InlineData(2e-3, -1, 1e-3)]
    public void TimebaseFollowsTheOneTwoFiveSequence(double start, int direction, double expected)
    {
        var (_, scope, _) = Fixture();
        scope.TimebasePerDivision = start;

        if (direction > 0) scope.ZoomTimeOutCommand.Execute(null);
        else scope.ZoomTimeInCommand.Execute(null);

        Assert.Equal(expected, scope.TimebasePerDivision, expected * 1e-6);
    }

    [Fact]
    public void SampleIntervalTracksTheTimebase()
    {
        var (_, scope, _) = Fixture();

        scope.TimebasePerDivision = 1e-3;
        var slow = scope.SuggestedSampleInterval;

        scope.TimebasePerDivision = 1e-6;
        var fast = scope.SuggestedSampleInterval;

        // A thousand-fold faster timebase must sample a thousand times more finely.
        Assert.Equal(1000.0, slow / fast, 1.0);
        Assert.Equal(scope.WindowSeconds / 4000.0, fast, fast * 1e-9);
    }

    [Fact]
    public void AcCoupleAllAppliesToEveryTrace()
    {
        var (circuit, scope, r) = Fixture();
        var r2 = circuit.Add(new Resistor());
        scope.AddProbe(r.A);
        scope.AddProbe(r2.A);

        scope.AcCoupleAll = true;

        Assert.All(scope.Probes, p => Assert.True(p.AcCoupled));
    }

    [Fact]
    public void RemovingATraceClearsTheSelection()
    {
        var (_, scope, r) = Fixture();
        var probe = scope.AddProbe(r.A);

        scope.RemoveProbeCommand.Execute(probe);

        Assert.Empty(scope.Probes);
        Assert.Null(scope.SelectedProbe);
    }
}

public class InspectorViewModelTests
{
    [Fact]
    public void ResistanceIsEditableInEngineeringNotation()
    {
        var resistor = new Resistor(1e3) { Name = "R1" };
        var inspector = new InspectorViewModel { Component = resistor };

        var resistance = inspector.Parameters
            .OfType<NumericParameterViewModel>()
            .Single(p => p.Label == "Resistance");

        resistance.Text = "4k7";

        Assert.Equal(4700, resistor.Resistance, 1e-6);
        Assert.False(resistance.HasError);
    }

    [Fact]
    public void AnUnparseableValueIsFlaggedAndLeavesTheComponentAlone()
    {
        var resistor = new Resistor(1e3);
        var inspector = new InspectorViewModel { Component = resistor };
        var resistance = inspector.Parameters.OfType<NumericParameterViewModel>().Single(p => p.Label == "Resistance");

        resistance.Text = "not a number";

        Assert.True(resistance.HasError);
        Assert.Equal(1e3, resistor.Resistance);
    }

    [Fact]
    public void WaveformIsOfferedAsAChoiceAndAppliesWhenPicked()
    {
        var generator = new FunctionGenerator();
        var inspector = new InspectorViewModel { Component = generator };

        var shape = inspector.Parameters.OfType<OptionParameterViewModel>().Single(p => p.Label == "Shape");
        Assert.Equal(Enum.GetValues<Waveform>().Length, shape.Options.Count);

        shape.Selected = Waveform.Triangle;

        Assert.Equal(Waveform.Triangle, generator.Shape);
    }

    [Fact]
    public void DeviceModelsAreOfferedFromTheirLibrary()
    {
        var diode = new Cirq.Components.Nonlinear.Diode();
        var inspector = new InspectorViewModel { Component = diode };

        var model = inspector.Parameters.OfType<OptionParameterViewModel>().Single(p => p.Label == "Model");
        Assert.Equal(Cirq.Components.Nonlinear.DiodeModel.Library.Count, model.Options.Count);

        model.Selected = Cirq.Components.Nonlinear.DiodeModel.D1N4001;

        Assert.Equal("1N4001", diode.Model.Name);
    }

    [Fact]
    public void PlacementAndPinsAreListedSeparatelyFromParameters()
    {
        var capacitor = new Capacitor(100e-9) { Name = "C7", X = 40, Y = -20 };
        var inspector = new InspectorViewModel { Component = capacitor };

        Assert.Equal("C7", inspector.Title);
        Assert.Equal("Capacitor", inspector.Subtitle);
        Assert.Equal(4, inspector.Placement.Count);           // Designator, X, Y, Rotation.
        Assert.Equal(2, inspector.Terminals.Count);
        Assert.DoesNotContain(inspector.Parameters, p => p.Label is "X" or "Y" or "Designator");
    }

    [Fact]
    public void ClearingTheSelectionEmptiesThePanel()
    {
        var inspector = new InspectorViewModel { Component = new Resistor() };
        Assert.NotEmpty(inspector.Parameters);

        inspector.Component = null;

        Assert.Empty(inspector.Parameters);
        Assert.Empty(inspector.Placement);
        Assert.Equal("Nothing selected", inspector.Title);
    }

    [Fact]
    public void EveryPlaceableComponentProducesAWorkingInspector()
    {
        // Guards the reflection-driven editor against a component whose properties it cannot map.
        foreach (var item in ComponentCatalog.AllItems)
        {
            var component = item.Create();
            var inspector = new InspectorViewModel { Component = component };

            Assert.Equal(component.ComponentType, inspector.Subtitle);
            Assert.Equal(component.Terminals.Count, inspector.Terminals.Count);
            Assert.All(inspector.Parameters, p => Assert.False(string.IsNullOrWhiteSpace(p.Label)));
        }
    }
}

public class ScopeVerticalRangeTests
{
    [Fact]
    public void ThePlottedRangeIsLargerThanTheNominalSpan()
    {
        var scope = new ScopeViewModel(new Cirq.Core.Topology.Circuit()) { VoltsPerDivision = 1.0 };

        Assert.Equal(8.0, scope.VoltageSpan);
        Assert.True(scope.DisplayVoltageSpan > scope.VoltageSpan,
            "A trace at full scale would sit exactly on the axis frame with no headroom.");
        Assert.Equal(scope.DisplayVoltageSpan / 2.0, scope.DisplayVoltageHalfSpan);
    }

    [Fact]
    public void AFullScaleSignalFitsInsideThePlottedRange()
    {
        // The case that was clipping: the RC example's generator is 4 Vpp on a 2 V offset, so it
        // peaks at exactly 4 V, and 1 V/div over eight divisions gives limits of exactly +/-4 V.
        using var vm = new MainWindowViewModel();
        var generator = vm.Circuit.Components.OfType<Cirq.Components.Sources.FunctionGenerator>().Single();

        var peak = generator.DcOffset + generator.Amplitude;
        Assert.Equal(4.0, peak);
        Assert.Equal(4.0, vm.Scope.VoltageSpan / 2.0);

        // The peak must land strictly inside the plotted range, not on its boundary.
        Assert.True(peak < vm.Scope.DisplayVoltageHalfSpan,
            $"Peak {peak} V is not inside the plotted range of +/-{vm.Scope.DisplayVoltageHalfSpan} V.");
    }

    [Fact]
    public void HeadroomScalesWithTheVoltsPerDivisionSetting()
    {
        var scope = new ScopeViewModel(new Cirq.Core.Topology.Circuit());

        foreach (var voltsPerDivision in new[] { 0.1, 1.0, 5.0, 20.0 })
        {
            scope.VoltsPerDivision = voltsPerDivision;
            var nominalPeak = scope.VoltageSpan / 2.0;

            Assert.True(scope.DisplayVoltageHalfSpan > nominalPeak,
                $"No headroom at {voltsPerDivision} V/div.");

            // Headroom stays a small proportion, so the setting still means what it says.
            Assert.InRange(scope.DisplayVoltageHalfSpan / nominalPeak, 1.01, 1.10);
        }
    }
}

public class ScopeAutoScaleTests
{
    private static ScopeViewModel Scope() => new(new Cirq.Core.Topology.Circuit());

    [Fact]
    public void AutoScalingIsOnByDefaultAndCentredOnZero()
    {
        var scope = Scope();

        Assert.True(scope.AutoScaleVertical);
        Assert.Equal(0, scope.VerticalCenter);
    }

    [Fact]
    public void ASignalThatOutgrowsTheRangeMakesItGrow()
    {
        // The reported case: the default circuit shows 0..4 V, then the amplitude is raised so it
        // swings -0.5..4.5 V and no longer fits.
        var scope = Scope();
        scope.VoltsPerDivision = 1.0;
        scope.VerticalCenter = 0;

        Assert.False(scope.AutoScaleTo(0, 4), "0..4 V already fits, so nothing should move.");
        Assert.True(scope.AutoScaleTo(-0.5, 4.5), "-0.5..4.5 V does not fit and should trigger a refit.");

        Assert.True(scope.DisplayMinimum < -0.5, "The bottom of the signal is off screen.");
        Assert.True(scope.DisplayMaximum > 4.5, "The top of the signal is off screen.");
    }

    [Fact]
    public void ASignalThatShrinksReclaimsTheScreen()
    {
        var scope = Scope();
        scope.AutoScaleTo(-50, 50);
        var coarse = scope.VoltsPerDivision;

        scope.AutoScaleTo(-0.4, 0.4);

        Assert.True(scope.VoltsPerDivision < coarse,
            $"Expected the range to tighten from {coarse} V/div, but it stayed there.");
        Assert.True(scope.DisplayMaximum > 0.4);
    }

    [Fact]
    public void RefittingIsStickySoTheAxisDoesNotHunt()
    {
        // Once fitted, small changes must not keep moving the axis, or the display flickers
        // between two neighbouring settings at the redraw rate.
        var scope = Scope();
        scope.AutoScaleTo(-3, 3);

        var settled = scope.VoltsPerDivision;
        var centre = scope.VerticalCenter;

        for (var i = 0; i < 20; i++)
        {
            Assert.False(scope.AutoScaleTo(-3 + i * 0.001, 3 - i * 0.001),
                "A trace that still fits should not cause a refit.");
        }

        Assert.Equal(settled, scope.VoltsPerDivision);
        Assert.Equal(centre, scope.VerticalCenter);
    }

    [Fact]
    public void AnOffsetSignalIsCentredRatherThanWastingHalfTheScreen()
    {
        var scope = Scope();
        scope.AutoScaleTo(9.0, 11.0);

        // A 9..11 V signal should sit in the middle, not clinging to the top of a 0-centred view.
        Assert.InRange(scope.VerticalCenter, 9.0, 11.0);
        Assert.True(scope.DisplayMinimum < 9.0);
        Assert.True(scope.DisplayMaximum > 11.0);
    }

    [Fact]
    public void UsingTheVerticalButtonsHandsControlBackToTheUser()
    {
        var scope = Scope();
        Assert.True(scope.AutoScaleVertical);

        scope.ZoomVoltsInCommand.Execute(null);

        Assert.False(scope.AutoScaleVertical);
        // And from then on the range is left alone.
        Assert.False(scope.AutoScaleTo(-100, 100));
    }

    [Theory]
    [InlineData(0.7, 1.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.3, 2.0)]
    [InlineData(2.0, 2.0)]
    [InlineData(3.0, 5.0)]
    [InlineData(6.0, 10.0)]
    [InlineData(0.03, 0.05)]
    public void RangesSnapToTheOneTwoFiveSequence(double wanted, double expected) =>
        Assert.Equal(expected, ScopeViewModel.NextNiceStep(wanted), expected * 1e-9);

    [Fact]
    public void AFlatTraceStillGetsAUsableRange()
    {
        var scope = Scope();

        scope.AutoScaleTo(2.5, 2.5);

        Assert.True(scope.DisplayMaximum > scope.DisplayMinimum);
        Assert.InRange(2.5, scope.DisplayMinimum, scope.DisplayMaximum);
    }
}
