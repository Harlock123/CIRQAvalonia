using System.Collections.ObjectModel;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using Cirq.Engine.Simulation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>One probe's spread, dressed for the list and the plot.</summary>
public sealed class MonteCarloRowViewModel(MonteCarloTrace trace, double band)
{
    public MonteCarloTrace Trace { get; } = trace;

    public string Label => Trace.Label;

    private string Unit => Trace.Kind switch
    {
        ProbeKind.Current => "A",
        ProbeKind.Power => "W",
        ProbeKind.Logic => "",
        _ => "V",
    };

    public string Nominal => SiPrefix.Format(Trace.Nominal, Unit);

    public string Range => $"{SiPrefix.Format(Trace.Minimum, Unit)} … {SiPrefix.Format(Trace.Maximum, Unit)}";

    public string Sigma => SiPrefix.Format(Trace.StandardDeviation, Unit);

    /// <summary>The headline: how far out it got, as a percentage of where it was designed to be.</summary>
    public string Worst => Trace.Nominal == 0
        ? SiPrefix.Format(Math.Max(Math.Abs(Trace.Maximum), Math.Abs(Trace.Minimum)), Unit)
        : $"±{Trace.WorstFractionalError * 100:0.##} %";

    /// <summary>How many trials missed a band around nominal, which is the yield question.</summary>
    public string Yield
    {
        get
        {
            if (Trace.Values.Count == 0 || Trace.Nominal == 0) return "—";

            var outside = Trace.OutsideBand(band);
            var inside = Trace.Values.Count - outside;

            return $"{inside * 100.0 / Trace.Values.Count:0.#} %";
        }
    }
}

/// <summary>One part's contribution to a measurement's spread, dressed for the list.</summary>
public sealed class SensitivityRowViewModel(SensitivityEntry entry, double nominal)
{
    public SensitivityEntry Entry { get; } = entry;

    public string Part => Entry.Part;

    /// <summary>The band it was varied over.</summary>
    public string Tolerance => $"±{Entry.Tolerance * 100:0.#} %";

    /// <summary>Its share of the variance, which is what says where to spend the money.</summary>
    public string Share => $"{Entry.Share * 100:0.#} %";

    /// <summary>
    /// How far the measurement moves for a given fractional move in the part. A half means the
    /// output moves half as far, proportionally, as the part does.
    /// </summary>
    public string Elasticity => $"{Entry.Elasticity(nominal):0.##}";

    /// <summary>A bar width from nought to one, for the share column.</summary>
    public double Weight => Math.Clamp(Entry.Share, 0, 1);
}

/// <summary>
/// The Monte Carlo window: build the circuit a few hundred times with its parts drawn from their
/// tolerance bands, and see what the answer did.
/// <para>
/// It is the only analysis here that asks whether a circuit works with the parts you can buy
/// rather than with the ones in the drawing. Everything else uses the value written on the
/// schematic, and no resistor has ever had the value written on it.
/// </para>
/// </summary>
public sealed partial class MonteCarloViewModel : ObservableObject
{
    private readonly Circuit _circuit;

    public MonteCarloViewModel(Circuit circuit)
    {
        _circuit = circuit;
    }

    /// <summary>How many circuits to build and solve.</summary>
    [ObservableProperty]
    public partial int Trials { get; set; } = 500;

    /// <summary>Where the sequence starts. The same seed gives the same run, exactly.</summary>
    [ObservableProperty]
    public partial int Seed { get; set; } = 1;

    /// <summary>
    /// The band the yield column is measured against, as a percentage of nominal. This is the
    /// specification you are holding the circuit to, and it is the thing worth arguing about
    /// rather than the tolerances themselves.
    /// </summary>
    [ObservableProperty]
    public partial double AcceptableBandPercent { get; set; } = 5.0;

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>What was varied, so the report says what it actually did.</summary>
    public ObservableCollection<string> Varied { get; } = [];

    /// <summary>One row per probe.</summary>
    public ObservableCollection<MonteCarloRowViewModel> Rows { get; } = [];

    /// <summary>Which row's histogram is on the plot.</summary>
    [ObservableProperty]
    public partial MonteCarloRowViewModel? Selected { get; set; }

    public bool HasRows => Rows.Count > 0;

    /// <summary>
    /// Which part is responsible for the selected trace's spread, worst first.
    /// <para>
    /// The other half of the question. The spread says whether the design works; this says where
    /// a tighter part would actually help, which is not always where the loosest one is — a part
    /// the output barely depends on can have the widest band and matter least.
    /// </para>
    /// </summary>
    public ObservableCollection<SensitivityRowViewModel> Sensitivity { get; } = [];

    public bool HasSensitivity => Sensitivity.Count > 0;

    /// <summary>Raised when new results are ready, so the view can redraw the histogram.</summary>
    public event EventHandler? ResultsChanged;

    partial void OnSelectedChanged(MonteCarloRowViewModel? value)
    {
        ShowSensitivityFor(value);
        ResultsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The ranking for one trace, computed once per run and shown for whichever is chosen.</summary>
    private IReadOnlyList<SensitivityResult> _sensitivity = [];

    private void ShowSensitivityFor(MonteCarloRowViewModel? row)
    {
        Sensitivity.Clear();

        if (row is not null)
        {
            var ranking = _sensitivity.FirstOrDefault(r => r.Label == row.Label);

            if (ranking is not null)
                foreach (var entry in ranking.Entries)
                    Sensitivity.Add(new SensitivityRowViewModel(entry, ranking.Nominal));
        }

        OnPropertyChanged(nameof(HasSensitivity));
    }

    partial void OnAcceptableBandPercentChanged(double value) => Run();

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
            Rows.Clear();
            Summary = ex is CircuitTopologyException
                ? $"The circuit will not build: {ex.Message}"
                : $"The analysis failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasRows));
            ResultsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Execute()
    {
        Rows.Clear();
        Varied.Clear();
        Sensitivity.Clear();
        _sensitivity = [];

        if (!MonteCarlo.Targets(_circuit).Any())
        {
            Summary = "Nothing in this circuit has a tolerance. Give a resistor, capacitor or " +
                      "inductor one in the properties panel — 5 % is an ordinary resistor and " +
                      "20 % an ordinary ceramic — and run it again.";
            return;
        }

        if (_circuit.Probes.Count == 0)
        {
            Summary = "Nothing to measure — put a probe on whatever the circuit is supposed to " +
                      "get right.";
            return;
        }

        var band = Math.Max(AcceptableBandPercent, 0) / 100.0;

        var result = new MonteCarlo(_circuit).Run(new MonteCarloRequest(Math.Max(Trials, 2), Seed));

        // Exact rather than sampled: each part is taken to each end of its own band with
        // everything else at nominal, which is two solves and no randomness at all.
        _sensitivity = new Cirq.Engine.Simulation.Sensitivity(_circuit).Run();

        foreach (var varied in result.Varied) Varied.Add(varied);
        foreach (var trace in result.Traces) Rows.Add(new MonteCarloRowViewModel(trace, band));

        Selected = Rows.FirstOrDefault();

        var worst = Rows.Count == 0 ? 0 : Rows.Max(r => r.Trace.WorstFractionalError);

        Summary =
            $"{result.Trials} trials, varying {result.Varied.Count} part(s). " +
            $"Worst departure from nominal {worst * 100:0.##} %." +
            (result.Failed > 0 ? $" {result.Failed} would not converge and were discarded." : string.Empty);
    }
}
