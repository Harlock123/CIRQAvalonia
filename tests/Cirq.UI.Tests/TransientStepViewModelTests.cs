using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The parameter-step window's own logic, driven without a window.
/// <para>
/// Where the DC sweep answers what a circuit settles at, this answers how it gets there — so the
/// thing being checked is that the curves differ in <i>time</i>, which is what no operating point
/// can show.
/// </para>
/// </summary>
public class TransientStepViewModelTests
{
    /// <summary>A step into an RC, whose response is a textbook exponential.</summary>
    private static (Circuit Circuit, Resistor R, Capacitor C) Rig()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(1.0));
        var resistor = circuit.Add(new Resistor(1e3));
        var capacitor = circuit.Add(new Capacitor(1e-6) { InitialVoltage = 0.0 });
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, resistor.A);
        circuit.Connect(resistor.B, capacitor.A);
        circuit.Connect(capacitor.B, ground.Pin);

        circuit.Probes.Add(new SignalProbe("Vc", capacitor.A, default));

        return (circuit, resistor, capacitor);
    }

    // ---- what it offers ----------------------------------------------------

    /// <summary>
    /// A capacitor is the first thing offered, because a reactive value decides the <i>shape</i>
    /// of a transient rather than its size, and that is what anybody opening this window came for.
    /// </summary>
    [Fact]
    public void ItStartsOnSomethingReactive()
    {
        var (circuit, _, _) = Rig();

        var model = new TransientStepViewModel(circuit);

        Assert.Equal(nameof(Capacitor.Capacitance), model.Parameter?.PropertyName);
    }

    /// <summary>Temperature is offered too: it belongs to no part, so it has to be put there.</summary>
    [Fact]
    public void TemperatureIsOnTheList()
    {
        var (circuit, _, _) = Rig();

        Assert.Contains(new TransientStepViewModel(circuit).Options, o => o.IsTemperature);
    }

    /// <summary>
    /// The default range is around wherever the value is now rather than a fixed span. A capacitor
    /// lives in nanofarads and a load resistor in kilohms, and 0 to 1 is wrong for both.
    /// </summary>
    [Fact]
    public void TheRangeStartsAroundWhateverWasPicked()
    {
        var (circuit, resistor, capacitor) = Rig();

        var model = new TransientStepViewModel(circuit);

        Assert.Equal(capacitor.Capacitance / 2, model.Start, 15);
        Assert.Equal(capacitor.Capacitance * 2, model.Stop, 15);

        model.Parameter = model.Options.Single(o =>
            o.Component == resistor && o.PropertyName == nameof(Resistor.Resistance));

        Assert.Equal(resistor.Resistance / 2, model.Start, 9);
        Assert.Equal(resistor.Resistance * 2, model.Stop, 9);
    }

    // ---- running it --------------------------------------------------------

    /// <summary>
    /// The measurement: one curve per value, and each reaching 63 % of the way at its own time
    /// constant. Curves that differ in <i>when</i> is the whole reason this window exists.
    /// </summary>
    [Fact]
    public void EachCurveHasItsOwnTimeConstant()
    {
        var (circuit, resistor, _) = Rig();

        var model = new TransientStepViewModel(circuit)
        {
            Start = 1e-6,
            Stop = 3e-6,
            Count = 3,
            Duration = 30e-3,
        };

        model.Run();

        Assert.True(model.HasCurves);
        Assert.Equal(3, model.Curves.Count);

        foreach (var curve in model.Curves)
        {
            var tau = resistor.Resistance * curve.StepValue;

            var at = curve.Times
                .Select((t, i) => (t, i))
                .MinBy(p => Math.Abs(p.t - tau)).i;

            Assert.Equal(1 - (1 / Math.E), curve.Values[at], 0.02);
        }
    }

    /// <summary>The legend says which value produced which curve, since nothing else can.</summary>
    [Fact]
    public void TheLegendNamesTheValueThatMadeEachCurve()
    {
        var (circuit, _, capacitor) = Rig();

        var model = new TransientStepViewModel(circuit)
        {
            Start = 1e-6, Stop = 2e-6, Count = 2, Duration = 5e-3,
        };

        model.Run();

        Assert.All(model.Curves, c => Assert.Contains(capacitor.Name, c.Label));
        Assert.Equal(2, model.Curves.Select(c => c.Label).Distinct().Count());
    }

    /// <summary>The status line says what was run and where each curve ended up.</summary>
    [Fact]
    public void TheStatusSaysWhatHappened()
    {
        var (circuit, _, _) = Rig();

        var model = new TransientStepViewModel(circuit)
        {
            Start = 1e-6, Stop = 2e-6, Count = 2, Duration = 20e-3,
        };

        model.Run();

        Assert.Contains("2 runs", model.Status);
        Assert.Contains("settles at", model.Status);
    }

    /// <summary>The parameter goes back where it was, so the window costs the circuit nothing.</summary>
    [Fact]
    public void TheCircuitIsUnchangedAfterwards()
    {
        var (circuit, _, capacitor) = Rig();

        var model = new TransientStepViewModel(circuit)
        {
            Start = 1e-6, Stop = 9e-6, Count = 5, Duration = 2e-3,
        };

        model.Run();

        Assert.Equal(1e-6, capacitor.Capacitance, 15);
    }

    // ---- refusing to pretend -----------------------------------------------

    [Fact]
    public void WithNoProbesItSaysSoRatherThanDrawingNothing()
    {
        var (circuit, _, _) = Rig();
        circuit.Probes.Clear();

        var model = new TransientStepViewModel(circuit) { Duration = 1e-3 };
        model.Run();

        Assert.False(model.HasCurves);
        Assert.Contains("put a probe", model.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithNoDurationItSaysSo()
    {
        var (circuit, _, _) = Rig();

        var model = new TransientStepViewModel(circuit) { Duration = 0 };
        model.Run();

        Assert.False(model.HasCurves);
        Assert.Contains("length", model.Status);
    }

    /// <summary>An empty canvas has nothing to step, and the window says that rather than throwing.</summary>
    [Fact]
    public void AnEmptyCanvasSaysThereIsNothingToStep()
    {
        var model = new TransientStepViewModel(new Circuit());
        model.Run();

        Assert.False(model.HasCurves);
        Assert.NotEmpty(model.Status);
    }

    /// <summary>
    /// A ringing circuit is what this is for, and the status line measures the overshoot — which
    /// is the number you are stepping a damping element to change.
    /// </summary>
    [Fact]
    public void AnUnderdampedCircuitReportsItsOvershoot()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(1.0));
        var resistor = circuit.Add(new Resistor(2.0));
        var inductor = circuit.Add(new Inductor(1e-3));
        var capacitor = circuit.Add(new Capacitor(1e-6) { InitialVoltage = 0.0 });
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, resistor.A);
        circuit.Connect(resistor.B, inductor.A);
        circuit.Connect(inductor.B, capacitor.A);
        circuit.Connect(capacitor.B, ground.Pin);

        circuit.Probes.Add(new SignalProbe("Vc", capacitor.A, default));

        // Critical damping is R = 2√(L/C) = 63 Ω, so 2 Ω is well underdamped and 200 Ω is not.
        var model = new TransientStepViewModel(circuit)
        {
            Parameter = new TransientStepViewModel(circuit).Options.Single(o =>
                o.Component == resistor && o.PropertyName == nameof(Resistor.Resistance)),
            Start = 2.0,
            Stop = 200.0,
            Count = 2,
            Duration = 2e-3,
        };

        model.Run();

        var lightly = model.Curves.Single(c => Math.Abs(c.StepValue - 2.0) < 1e-9);
        var heavily = model.Curves.Single(c => Math.Abs(c.StepValue - 200.0) < 1e-9);

        // Underdamped rings past the supply; overdamped never gets there.
        Assert.True(lightly.Values.Max() > 1.5,
            $"2 Ω should ring well past a volt, not {lightly.Values.Max():0.###}");

        Assert.True(heavily.Values.Max() <= 1.01,
            $"200 Ω should not overshoot, but reached {heavily.Values.Max():0.###}");

        Assert.Contains("overshoot", model.Status);
    }
}
