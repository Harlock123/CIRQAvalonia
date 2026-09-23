using System.Collections.ObjectModel;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using Cirq.Components.Hierarchy;
using Cirq.Engine.Simulation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>One root as the window lists it.</summary>
/// <param name="Kind">"Pole" or "Zero".</param>
/// <param name="Root">The root itself.</param>
public sealed record RootRow(string Kind, Root Root)
{
    public string Frequency => SiPrefix.Format(Root.Hertz, "Hz");

    /// <summary>Real and imaginary parts, in radians per second, as the root is written down.</summary>
    public string Position => Root.IsOscillatory
        ? $"{SiPrefix.Format(Root.S.Real, string.Empty)} ± j{SiPrefix.Format(Math.Abs(Root.S.Imaginary), string.Empty)}"
        : SiPrefix.Format(Root.S.Real, string.Empty);

    /// <summary>Q for a mode that rings, and a time constant for one that only decays.</summary>
    public string Shape => Root.Q is { } q
        ? $"Q {q:0.##}"
        : Root.TimeConstant is { } tau ? $"τ {SiPrefix.Format(Math.Abs(tau), "s")}" : "—";

    public bool IsUnstable => Root.IsUnstable;
}

/// <summary>
/// The pole-zero window: a circuit's own natural frequencies, and the zeros of one path through it.
/// <para>
/// A stability sweep says a loop has eight degrees of phase margin. This says <i>why</i>: there is
/// a conjugate pair at a hundred and forty-five kilohertz with a Q of seven. A transient shows a
/// circuit ringing; this gives the frequency it rings at and how many cycles it takes to stop,
/// without having to measure either off a trace.
/// </para>
/// </summary>
public sealed partial class PoleZeroViewModel : ObservableObject
{
    private readonly Circuit _circuit;

    public PoleZeroViewModel(Circuit circuit)
    {
        _circuit = circuit;

        foreach (var probe in circuit.Probes.Where(p => p.TargetTerminal is not null))
            Outputs.Add(probe);

        foreach (var source in Flattening.Flatten(circuit.Components).OfType<IAcExcitation>())
            Inputs.Add((CircuitComponent)source);

        Output = Outputs.FirstOrDefault();
        Input = Inputs.FirstOrDefault();
    }

    /// <summary>Where a transfer function comes out. Only needed for the zeros.</summary>
    public ObservableCollection<SignalProbe> Outputs { get; } = [];

    /// <summary>Where it goes in. Only needed for the zeros.</summary>
    public ObservableCollection<CircuitComponent> Inputs { get; } = [];

    [ObservableProperty]
    public partial SignalProbe? Output { get; set; }

    [ObservableProperty]
    public partial CircuitComponent? Input { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Poles and zeros together, poles first, for the list beside the plot.</summary>
    public ObservableCollection<RootRow> Roots { get; } = [];

    public IReadOnlyList<Root> Poles { get; private set; } = [];

    public IReadOnlyList<Root> Zeros { get; private set; } = [];

    public bool HasResult => Poles.Count > 0 || Zeros.Count > 0;

    public string Verdict { get; private set; } = string.Empty;

    /// <summary>The slowest mode, which decides how long the circuit takes to settle.</summary>
    public string Dominant { get; private set; } = "—";

    public string PoleCount => Poles.Count == 1 ? "1 pole" : $"{Poles.Count} poles";

    public string ZeroCount => Zeros.Count == 1 ? "1 zero" : $"{Zeros.Count} zeros";

    public event EventHandler? ResultChanged;

    [RelayCommand]
    public void Run()
    {
        IsBusy = true;

        try
        {
            Execute();
        }
        catch (Exception ex)
        {
            Clear();
            Status = Describe(ex);
        }
        finally
        {
            IsBusy = false;

            foreach (var name in (string[])
                     [nameof(HasResult), nameof(Verdict), nameof(Dominant), nameof(PoleCount), nameof(ZeroCount)])
            {
                OnPropertyChanged(name);
            }

            ResultChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Clear()
    {
        Poles = [];
        Zeros = [];
        Roots.Clear();
        Verdict = string.Empty;
        Dominant = "—";
    }

    private void Execute()
    {
        Clear();

        var simulator = new CircuitSimulator(_circuit);

        simulator.Reset();
        simulator.SolveOperatingPoint();

        var result = new PoleZeroAnalysis(simulator).Run(
            new PoleZeroRequest(Output?.TargetTerminal, Input));

        if (!result.IsUsable)
        {
            Status = result.Problem ?? "The analysis found nothing to report.";
            return;
        }

        // Slowest first: the pole nearest the imaginary axis is the one that decides how long
        // everything takes, and is what somebody is looking for when they open this.
        Poles = [.. result.Poles.OrderBy(p => Math.Abs(p.S.Real))];
        Zeros = [.. result.Zeros.OrderBy(z => z.S.Magnitude)];

        foreach (var pole in Poles) Roots.Add(new RootRow("Pole", pole));
        foreach (var zero in Zeros) Roots.Add(new RootRow("Zero", zero));

        Verdict = result.Verdict;

        if (result.Dominant is { } dominant)
        {
            Dominant = dominant.IsOscillatory
                ? $"{SiPrefix.Format(dominant.Hertz, "Hz")} pair"
                : $"τ {SiPrefix.Format(Math.Abs(dominant.TimeConstant ?? 0), "s")}";
        }

        if (Zeros.Count == 0 && Output is null)
        {
            Status = "Poles only. Name an output and an input to get the zeros of a transfer " +
                     "function through the circuit as well — a pole belongs to the circuit, a " +
                     "zero to one path.";
            return;
        }

        // A pole and a zero in the same place are a cancellation, and drawn on top of each other
        // they look like a fault. They are not: they mean the path between these two points has
        // no dynamics of its own, which is exactly what probing a node an ideal source is holding
        // gives you — the ratio is one at every frequency.
        var cancelled = Poles.Count(p => Zeros.Any(z => Close(p, z)));

        Status = cancelled == 0
            ? $"{PoleCount}, {ZeroCount}."
            : $"{PoleCount}, {ZeroCount} — and {(cancelled == 1 ? "one pair cancels" : $"{cancelled} pairs cancel")}. " +
              "A pole and a zero in the same place leave nothing behind: between these two points " +
              "the circuit does the same thing at every frequency.";
    }

    /// <summary>
    /// Whether two roots are the same place to within rounding. Relative, because a pair at a
    /// megaradian and a pair at a millihertz both have to be judged on their own scale.
    /// </summary>
    private static bool Close(Root pole, Root zero)
    {
        var scale = Math.Max(pole.S.Magnitude, zero.S.Magnitude);

        return scale < 1e-30 || (pole.S - zero.S).Magnitude / scale < 1e-6;
    }

    private static string Describe(Exception ex) => ex switch
    {
        CircuitTopologyException => $"The circuit will not build: {ex.Message}",
        ConvergenceException =>
            "The circuit has no operating point to linearise about: " + ex.Message,
        _ => $"The analysis failed: {ex.Message}",
    };
}
