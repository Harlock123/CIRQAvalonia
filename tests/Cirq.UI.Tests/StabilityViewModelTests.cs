using Cirq.Components.Ics;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>The stability window's own logic, driven without a window.</summary>
public class StabilityViewModelTests
{
    /// <summary>A gain-of-ten amplifier with the loop broken at the inverting input.</summary>
    private static Circuit Amplifier(double loadFarads = 0.0)
    {
        var circuit = new Circuit();

        var positive = circuit.Add(new DcVoltageSource(15.0));
        var negative = circuit.Add(new DcVoltageSource(-15.0));

        var amp = circuit.Add(new OperationalAmplifier { Name = "U1", Model = OpAmpModel.Lm741 });
        var feedback = circuit.Add(new Resistor(9e3));
        var gain = circuit.Add(new Resistor(1e3));
        var probe = circuit.Add(new LoopProbe { Name = "LP1" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(positive.Negative, ground.Pin);
        circuit.Connect(negative.Positive, ground.Pin);
        circuit.Connect(amp.PositiveSupply, positive.Positive);
        circuit.Connect(amp.NegativeSupply, negative.Negative);
        circuit.Connect(amp.NonInverting, ground.Pin);

        circuit.Connect(amp.Output, feedback.A);
        circuit.Connect(feedback.B, gain.A);
        circuit.Connect(gain.B, ground.Pin);
        circuit.Connect(gain.A, probe.From);
        circuit.Connect(probe.To, amp.Inverting);

        if (loadFarads > 0)
        {
            var load = circuit.Add(new Capacitor(loadFarads));

            circuit.Connect(amp.Output, load.A);
            circuit.Connect(load.B, ground.Pin);
        }

        return circuit;
    }

    [Fact]
    public void ItFindsTheProbeAndMeasuresTheLoop()
    {
        var model = new StabilityViewModel(Amplifier());

        Assert.True(model.HasProbes);
        Assert.NotNull(model.Probe);

        model.Run();

        Assert.True(model.HasResult, model.Status);
        Assert.Equal(model.Frequencies.Count, model.Decibels.Count);
        Assert.Equal(model.Frequencies.Count, model.Degrees.Count);
    }

    /// <summary>
    /// A single-pole loop: ninety degrees of phase margin, crossing over at the gain-bandwidth
    /// product times the feedback fraction, and no gain margin because it never inverts.
    /// </summary>
    [Fact]
    public void ASinglePoleLoopIsComfortable()
    {
        var model = new StabilityViewModel(Amplifier());
        model.Run();

        Assert.NotNull(model.PhaseMarginDegrees);
        Assert.Equal(90.0, model.PhaseMarginDegrees.Value, 3.0);

        Assert.NotNull(model.CrossoverHz);
        Assert.Equal(1e6 * 0.1, model.CrossoverHz.Value, 1e4);

        Assert.Null(model.GainMarginDb);
        Assert.Equal("—", model.GainMargin);

        Assert.Contains("Comfortable", model.Verdict);
    }

    /// <summary>
    /// A capacitive load makes a second pole inside the loop, which is what eats phase margin —
    /// and is why a follower driving a cable can start to sing.
    /// </summary>
    [Fact]
    public void ACapacitiveLoadCostsMarginAndTheVerdictChanges()
    {
        var clean = new StabilityViewModel(Amplifier());
        clean.Run();

        var loaded = new StabilityViewModel(Amplifier(loadFarads: 100e-6)) { StopHz = 1e8 };
        loaded.Run();

        Assert.NotNull(clean.PhaseMarginDegrees);
        Assert.NotNull(loaded.PhaseMarginDegrees);

        Assert.True(loaded.PhaseMarginDegrees < clean.PhaseMarginDegrees - 30,
            $"{loaded.PhaseMarginDegrees:0.#}° against {clean.PhaseMarginDegrees:0.#}°");

        Assert.NotEqual(clean.Verdict, loaded.Verdict);
    }

    /// <summary>The window says the margins in figures as well as drawing them.</summary>
    [Fact]
    public void TheMarginsArePrintedAsWellAsPlotted()
    {
        var model = new StabilityViewModel(Amplifier());
        model.Run();

        Assert.EndsWith("°", model.PhaseMargin, StringComparison.Ordinal);
        Assert.Contains("Hz", model.Crossover);
        Assert.Contains("dB of loop gain", model.Status);
    }

    // ---- refusing rather than pretending -----------------------------------

    /// <summary>
    /// With no probe there is nowhere to break the loop, and the message says what to do about it
    /// rather than reporting an empty plot.
    /// </summary>
    [Fact]
    public void WithNoProbeItSaysWhereToPutOne()
    {
        var circuit = Amplifier();

        foreach (var probe in circuit.Components.OfType<LoopProbe>().ToList())
            circuit.Components.Remove(probe);

        var model = new StabilityViewModel(circuit);
        model.Run();

        Assert.False(model.HasProbes);
        Assert.False(model.HasResult);
        Assert.Contains("Loop Probe", model.Status);
    }

    [Fact]
    public void AnEmptyCanvasIsAMessage()
    {
        var model = new StabilityViewModel(new Circuit());
        model.Run();

        Assert.False(model.HasResult);
        Assert.NotEmpty(model.Status);
    }

    /// <summary>
    /// A band that stops short of the crossover cannot report a margin, and says so rather than
    /// reporting whatever the last point happened to be.
    /// </summary>
    [Fact]
    public void ANarrowBandSaysThereIsNoCrossoverInIt()
    {
        var model = new StabilityViewModel(Amplifier()) { StartHz = 0.1, StopHz = 10 };
        model.Run();

        Assert.Null(model.CrossoverHz);
        Assert.Equal("—", model.PhaseMargin);
        Assert.Contains("never reaches one", model.Verdict);
    }

    // ---- the example -------------------------------------------------------

    /// <summary>
    /// The Loop Stability example is set up for this window: a probe in the right place, ninety
    /// degrees of margin as drawn, and a capacitive load on a switch that takes it away. The whole
    /// lesson in one circuit, so it has to keep working.
    /// </summary>
    [Fact]
    public void TheLoopStabilityExampleShowsTheMarginGoingWhenTheLoadIsSwitchedIn()
    {
        using var vm = new MainWindowViewModel();

        vm.Circuit.Clear();
        vm.Scope.ClearProbes();
        Examples.All.Single(e => e.Name == "Loop Stability").Build(vm);

        var open = new StabilityViewModel(vm.Circuit);
        open.Run();

        Assert.True(open.HasProbes);
        Assert.True(open.HasResult, open.Status);

        // A follower has the most feedback of any configuration, so the most loop gain and the
        // furthest-out crossover — right at the gain-bandwidth product, where more phase has
        // already gone than at the 100 kHz a gain-of-ten stage crosses at. Hence sixty degrees
        // rather than the ninety of a loop that crosses a decade earlier, which is exactly why a
        // follower is the configuration people have trouble with.
        Assert.NotNull(open.PhaseMarginDegrees);
        Assert.InRange(open.PhaseMarginDegrees.Value, 45.0, 95.0);

        // Close the switch and the capacitor makes a second pole inside the loop.
        var switched = vm.Circuit.Components.OfType<ToggleSwitch>().Single();
        switched.IsClosed = true;

        var loaded = new StabilityViewModel(vm.Circuit);
        loaded.Run();

        Assert.NotNull(loaded.PhaseMarginDegrees);
        Assert.True(loaded.PhaseMarginDegrees < open.PhaseMarginDegrees - 20,
            $"switching the load in should cost margin: {loaded.PhaseMarginDegrees:0.#}° " +
            $"against {open.PhaseMarginDegrees:0.#}°");
    }
}
