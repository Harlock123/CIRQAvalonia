using System.Collections.ObjectModel;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Core.Verification;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>One choice in a requirement's pickers, with the words to show for it.</summary>
public sealed record SpecOption<T>(T Value, string Label);

/// <summary>One requirement, with whatever the last check made of it.</summary>
public sealed partial class SpecRowViewModel : ObservableObject
{
    public SpecRowViewModel(DesignSpec spec)
    {
        Spec = spec;
    }

    public DesignSpec Spec { get; }

    [ObservableProperty]
    public partial SpecResult? Result { get; set; }

    /// <summary>"Met", "Not met", or a dash when there was nothing to measure.</summary>
    public string Verdict => Result?.Passed switch
    {
        true => "Met",
        false => "Not met",
        _ => "—",
    };

    /// <summary>What the circuit actually did, or why it could not be said.</summary>
    public string Detail => Result is null
        ? "Not checked yet."
        : Result.Explanation;

    /// <summary>How much room is left, as a percentage of the limit.</summary>
    public string Margin => Result?.Margin is { } margin ? $"{margin * 100:0}%" : string.Empty;

    public bool IsMet => Result?.Passed == true;

    public bool IsBroken => Result?.Passed == false;

    public bool IsUnknown => Result is not null && Result.Passed is null;

    partial void OnResultChanged(SpecResult? value)
    {
        foreach (var name in (string[])
                 [nameof(Verdict), nameof(Detail), nameof(Margin), nameof(IsMet), nameof(IsBroken), nameof(IsUnknown)])
        {
            OnPropertyChanged(name);
        }
    }
}

/// <summary>
/// The requirements window: what the circuit is supposed to do, held against what it did.
/// <para>
/// A simulator answers "what does this do". This is the other half — "and is that right" — and
/// until it is written down somewhere the answer lives in whoever last looked at the trace. Every
/// analysis here already produces numbers; a requirement is a name, one of those numbers, and a
/// limit, so the question gets asked again every time anything changes rather than only when
/// somebody thinks to look.
/// </para>
/// </summary>
public sealed partial class SpecsViewModel : ObservableObject
{
    private readonly Circuit _circuit;

    public SpecsViewModel(Circuit circuit)
    {
        _circuit = circuit;

        foreach (var spec in circuit.Specs) Rows.Add(new SpecRowViewModel(spec));

        foreach (var probe in circuit.Probes.Where(p => p.TargetTerminal is not null))
            Traces.Add(probe.Label);

        Check();
    }

    public ObservableCollection<SpecRowViewModel> Rows { get; } = [];

    /// <summary>The probe labels a requirement can be pointed at.</summary>
    public ObservableCollection<string> Traces { get; } = [];

    /// <summary>
    /// The measurements a requirement can be about, written the way somebody would say them
    /// rather than the way they are spelled in the code.
    /// </summary>
    public static IReadOnlyList<SpecOption<SpecQuantity>> Quantities { get; } =
        [.. Enum.GetValues<SpecQuantity>().Select(q => new SpecOption<SpecQuantity>(q, SpecWords.Of(q)))];

    public static IReadOnlyList<SpecOption<SpecComparison>> Comparisons { get; } =
        [.. Enum.GetValues<SpecComparison>().Select(c => new SpecOption<SpecComparison>(c, SpecWords.Of(c)))];

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    public bool HasRows => Rows.Count > 0;

    /// <summary>Raised when a requirement is added, removed or edited, so the circuit is dirty.</summary>
    public event EventHandler? Changed;

    [RelayCommand]
    private void Add()
    {
        var spec = new DesignSpec
        {
            Name = "New requirement",
            Trace = Traces.FirstOrDefault() ?? string.Empty,
        };

        _circuit.Specs.Add(spec);
        Rows.Add(new SpecRowViewModel(spec));

        OnPropertyChanged(nameof(HasRows));
        Changed?.Invoke(this, EventArgs.Empty);

        Check();
    }

    [RelayCommand]
    private void Remove(SpecRowViewModel? row)
    {
        if (row is null) return;

        _circuit.Specs.Remove(row.Spec);
        Rows.Remove(row);

        OnPropertyChanged(nameof(HasRows));
        Changed?.Invoke(this, EventArgs.Empty);

        Check();
    }

    /// <summary>
    /// Holds every requirement against what the probes have recorded.
    /// <para>
    /// Against the <b>whole</b> recording rather than the window on the scope, deliberately. A
    /// requirement is about the circuit, not about what somebody has scrolled to — a ripple limit
    /// that passes because the interesting half is off the left of the screen is not a check.
    /// </para>
    /// </summary>
    [RelayCommand]
    public void Check()
    {
        var results = SpecCheck.EvaluateAll(Rows.Select(r => r.Spec), Measure);

        var byId = results.ToDictionary(r => r.Spec.Id);

        foreach (var row in Rows)
            row.Result = byId.GetValueOrDefault(row.Spec.Id);

        Summary = SpecCheck.Summarise(results);

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private TraceMeasurements? Measure(string label) => Measurement(_circuit)(label);

    /// <summary>
    /// How a requirement gets at what a trace did: across the whole recording rather than the
    /// window on the scope, because a requirement is about the circuit and not about what
    /// somebody has scrolled to.
    /// <para>
    /// Null means there is no such trace at all. A trace that exists but has recorded nothing yet
    /// is a different thing and gets an empty measurement, so the requirement comes back "nothing
    /// to measure yet" rather than "no trace called that" — which would be a lie about the
    /// circuit and would send somebody looking for a probe that is already in front of them.
    /// </para>
    /// </summary>
    public static Func<string, TraceMeasurements?> Measurement(Circuit circuit)
    {
        ArgumentNullException.ThrowIfNull(circuit);

        return label =>
        {
            var probe = circuit.Probes.FirstOrDefault(p => p.Label == label);

            if (probe is null) return null;

            return probe.HistoryBuffer is { } history
                ? TraceMeasurements.OfAll(history.ToArray())
                : TraceMeasurements.None;
        };
    }
}
