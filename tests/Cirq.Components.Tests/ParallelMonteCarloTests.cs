using Cirq.Components.Analysis;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The tolerance analysis, split across threads.
/// <para>
/// The only thing that really matters here is that splitting it changes nothing: the same seed on
/// one thread and on twelve has to give the same readings in the same order, or the analysis is
/// reporting something about the machine rather than about the circuit. Everything else — that it
/// is faster, that the circuit is left alone — is secondary to that.
/// </para>
/// </summary>
public class ParallelMonteCarloTests
{
    private static (Circuit Circuit, Resistor Top, Resistor Bottom) Divider(double tolerance = 0.05)
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(10.0));
        var top = circuit.Add(new Resistor(10e3) { Name = "R1", Tolerance = tolerance });
        var bottom = circuit.Add(new Resistor(10e3) { Name = "R2", Tolerance = tolerance });
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);

        circuit.Probes.Add(new SignalProbe("Mid", bottom.A, default));

        return (circuit, top, bottom);
    }

    [Fact]
    public void SplittingItChangesNothing()
    {
        var (circuit, _, _) = Divider();

        var request = new MonteCarloRequest(Trials: 400, Seed: 7);

        var serial = new MonteCarlo(circuit).Run(request);
        var parallel = ParallelMonteCarlo.Run(circuit, request, workers: 8);

        Assert.Equal(serial.Trials, parallel.Trials);
        Assert.Equal(serial.Failed, parallel.Failed);

        // Reading for reading, in order — not merely the same statistics.
        Assert.Equal(serial.Traces[0].Values, parallel.Traces[0].Values);
        Assert.Equal(serial.Traces[0].Nominal, parallel.Traces[0].Nominal, 1e-12);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(16)]
    public void HowManyThreadsDoesNotShowUpInTheAnswer(int workers)
    {
        var (circuit, _, _) = Divider();

        var request = new MonteCarloRequest(Trials: 300, Seed: 3);

        var once = ParallelMonteCarlo.Run(circuit, request, workers: 1);
        var many = ParallelMonteCarlo.Run(circuit, request, workers: workers);

        Assert.Equal(once.Traces[0].Values, many.Traces[0].Values);
    }

    /// <summary>
    /// A chunk boundary that does not divide evenly is where an off-by-one would live: 401 trials
    /// across 8 workers is seven of fifty and one of fifty-one.
    /// </summary>
    [Theory]
    [InlineData(48)]
    [InlineData(49)]
    [InlineData(401)]
    [InlineData(1000)]
    public void EveryTrialIsRunExactlyOnce(int trials)
    {
        var (circuit, _, _) = Divider();

        var request = new MonteCarloRequest(Trials: trials, Seed: 11);

        var result = ParallelMonteCarlo.Run(circuit, request, workers: 8);

        Assert.Equal(trials, result.Trials + result.Failed);
        Assert.Equal(trials, result.Traces[0].Values.Count);

        // And no trial twice: the same circuit twice would show as a repeated reading, which a
        // uniform draw over a continuum does not otherwise produce.
        Assert.Equal(trials, result.Traces[0].Values.Distinct().Count());
    }

    [Fact]
    public void TheCircuitIsLeftExactlyAsItWasFound()
    {
        var (circuit, top, bottom) = Divider();

        ParallelMonteCarlo.Run(circuit, new MonteCarloRequest(Trials: 200, Seed: 5), workers: 4);

        // The workers used copies, so the drawing on screen never moved at all.
        Assert.Equal(10e3, top.Resistance, 1e-9);
        Assert.Equal(10e3, bottom.Resistance, 1e-9);
    }

    [Fact]
    public void ASmallRunIsNotWorthSplitting()
    {
        var (circuit, _, _) = Divider();

        // Under the threshold it runs serially — and gives the same answer, which is the only way
        // anybody would notice.
        var request = new MonteCarloRequest(Trials: 20, Seed: 2);

        Assert.Equal(
            new MonteCarlo(circuit).Run(request).Traces[0].Values,
            ParallelMonteCarlo.Run(circuit, request, workers: 8).Traces[0].Values);
    }

    [Fact]
    public void ACircuitWithNothingToVarySaysSoTheSameWay()
    {
        var (circuit, top, bottom) = Divider(tolerance: 0);

        var result = ParallelMonteCarlo.Run(circuit, new MonteCarloRequest(Trials: 200), workers: 4);

        Assert.True(result.IsEmpty);
        Assert.Equal(10e3, top.Resistance, 1e-9);
        Assert.Equal(10e3, bottom.Resistance, 1e-9);
    }
}
