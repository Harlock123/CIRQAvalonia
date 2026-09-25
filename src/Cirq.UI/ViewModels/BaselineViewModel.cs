using System.Collections.ObjectModel;
using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>One measurement that moved, dressed for the list.</summary>
public sealed class ChangeRowViewModel(MeasurementChange change, string unit)
{
    public string Quantity => change.Quantity;

    public string Was => Format(change.Was);

    public string Now => Format(change.Now);

    /// <summary>
    /// The move as a percentage, or the word for what happened when it is not a percentage — a
    /// figure that appeared or went away has no fraction, and printing one would invent a number.
    /// </summary>
    public string Moved => change.Appeared ? "appeared"
        : change.Disappeared ? "gone"
        : double.IsNaN(change.Fraction) ? "—"
        : $"{change.Fraction * 100:+0.#;−0.#;0}%";

    private string Format(double? value) => value is { } number
        ? SiPrefix.Format(number, UnitFor(change.Quantity, unit))
        : "—";

    /// <summary>
    /// The unit a quantity is in, which is not always the trace's own. A rise time is in seconds
    /// whatever the probe measures, and a duty cycle and an overshoot are in nothing at all.
    /// </summary>
    private static string UnitFor(string quantity, string traceUnit) => quantity switch
    {
        "rise time" or "fall time" or "settling time" or "pulse width" => "s",
        "frequency" => "Hz",
        "duty cycle" or "overshoot" => string.Empty,
        _ => traceUnit,
    };
}

/// <summary>One trace's line in the comparison.</summary>
public sealed class BaselineRowViewModel
{
    public BaselineRowViewModel(TraceComparison comparison)
    {
        Comparison = comparison;

        Changes = [.. comparison.Changes.Select(c => new ChangeRowViewModel(c, comparison.Unit))];
    }

    public TraceComparison Comparison { get; }

    public ObservableCollection<ChangeRowViewModel> Changes { get; }

    public string Label => Comparison.Label;

    /// <summary>What happened to this trace, in one word for the chip at the left of the row.</summary>
    public string Status => Comparison.Missing ? "Gone"
        : Comparison.Added ? "New"
        : Comparison.IsUnchanged ? "Same"
        : "Moved";

    public bool IsUnchanged => Comparison.IsUnchanged;

    /// <summary>How far the shapes are apart, and what fraction of the swing that is.</summary>
    public string Deviation => Comparison.WorstDeviation is { } worst
        ? double.IsNaN(Comparison.RelativeDeviation)
            ? SiPrefix.Format(worst, Comparison.Unit)
            : $"{SiPrefix.Format(worst, Comparison.Unit)} ({Comparison.RelativeDeviation * 100:0.#}% of swing)"
        : "—";

    public string Note => Comparison.Missing
        ? "The baseline has this trace and the circuit no longer does."
        : Comparison.Added
            ? "New since the baseline was taken, so nothing has ever been checked against it."
            : string.Empty;

    public bool HasNote => Note.Length > 0;
}

/// <summary>
/// The baseline window: record what the circuit does, and afterwards say what changed.
/// <para>
/// Requirements assert the handful of things somebody thought to assert. This is the rest of the
/// answer, recorded — so that a change three chapters away from whatever was edited turns up as a
/// change rather than as something noticed six weeks later.
/// </para>
/// </summary>
public sealed partial class BaselineViewModel : ObservableObject
{
    private readonly Circuit _circuit;

    public BaselineViewModel(Circuit circuit)
    {
        _circuit = circuit;
        Note = circuit.Baseline.Note;

        Compare();
    }

    /// <summary>One line per trace, on either side of the comparison.</summary>
    public ObservableCollection<BaselineRowViewModel> Rows { get; } = [];

    /// <summary>What somebody wants to say about the baseline they are about to take.</summary>
    [ObservableProperty]
    public partial string Note { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>What was recorded, and when, for the line above the table.</summary>
    [ObservableProperty]
    public partial string Recorded { get; private set; } = string.Empty;

    /// <summary>True once there is a baseline to compare against.</summary>
    public bool HasBaseline => !_circuit.Baseline.IsEmpty;

    public bool HasRows => Rows.Count > 0;

    /// <summary>Raised when the baseline is taken or cleared, so the document can be marked dirty.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Records what the probes are showing now as the answer everything after this is held against.
    /// </summary>
    [RelayCommand]
    public void Record()
    {
        _circuit.Baseline = TraceBaseline.From(Traces(), Note);

        Changed?.Invoke(this, EventArgs.Empty);

        Compare();
    }

    /// <summary>Throws the baseline away, for when the circuit is meant to have changed.</summary>
    [RelayCommand]
    public void Clear()
    {
        _circuit.Baseline = TraceBaseline.Empty;

        Changed?.Invoke(this, EventArgs.Empty);

        Compare();
    }

    [RelayCommand]
    public void Compare()
    {
        Rows.Clear();

        var baseline = _circuit.Baseline;

        if (baseline.IsEmpty)
        {
            Summary = "No baseline yet. Run the circuit until it is doing what it should, then " +
                      "press Record — everything after that is held against it.";

            Recorded = string.Empty;
        }
        else
        {
            var comparison = BaselineCheck.Against(baseline, Traces());

            foreach (var trace in comparison.Traces) Rows.Add(new BaselineRowViewModel(trace));

            Summary = comparison.Summary();

            Recorded = baseline.Note.Length > 0
                ? $"Recorded {baseline.Taken:g} — {baseline.Note}"
                : $"Recorded {baseline.Taken:g}";
        }

        OnPropertyChanged(nameof(HasBaseline));
        OnPropertyChanged(nameof(HasRows));
    }

    /// <summary>
    /// What every probe has recorded, as the baseline wants it.
    /// <para>
    /// The whole history rather than the window on the scope, for the reason the requirements use
    /// the whole history: a comparison that passed because the interesting half was off the left
    /// of the screen would not be a comparison.
    /// </para>
    /// </summary>
    private IEnumerable<(string Label, string Unit, IReadOnlyList<DataPoint> Samples)> Traces() =>
        _circuit.Probes.Select(p =>
            (p.Label, p.Unit, (IReadOnlyList<DataPoint>)p.HistoryBuffer.ToArray()));
}
