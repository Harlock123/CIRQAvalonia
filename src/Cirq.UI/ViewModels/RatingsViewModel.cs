using System.Collections.ObjectModel;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using Cirq.Engine.Simulation;
using Cirq.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>One limit, dressed for the list.</summary>
public sealed class FindingViewModel(RatingFinding finding)
{
    public RatingFinding Finding => finding;

    /// <summary>Which limit this is — power, temperature, voltage, current, or the part's own words.</summary>
    public string Kind => finding.Word;

    /// <summary>Where it got to against what it may reach, or the complaint itself.</summary>
    public string Detail => finding.Describe();

    public bool IsOver => finding.Verdict == PowerVerdict.Over;

    public bool IsWarm => finding.Verdict == PowerVerdict.Warm;
}

/// <summary>
/// One part, and everything the circuit has to say about it.
/// <para>
/// Grouped by part rather than listed one limit per row, because a part has several and they are
/// read together. An electrolytic over its voltage says so twice — once as a measurement against
/// its rating and once in its own words about what will happen — and those are two useful
/// sentences about one problem rather than two problems.
/// </para>
/// </summary>
public sealed class PartRatingsViewModel
{
    public PartRatingsViewModel(string name, string type, IEnumerable<RatingFinding> findings)
    {
        Name = name;
        Type = type;
        Findings = [.. findings.Select(f => new FindingViewModel(f))];
    }

    public string Name { get; }

    public string Type { get; }

    public IReadOnlyList<FindingViewModel> Findings { get; }

    public bool IsOver => Findings.Any(f => f.IsOver);

    public bool IsWarm => !IsOver && Findings.Any(f => f.IsWarm);

    /// <summary>The word for the chip at the left of the row.</summary>
    public string Status => IsOver ? "Over" : IsWarm ? "Warm" : "OK";
}

/// <summary>One supply's line.</summary>
public sealed class SupplyRowViewModel(SupplyRow row)
{
    public string Name => row.Name;
    public string Type => row.Type;
    public string Volts => SiPrefix.Format(row.Volts, "V", 3);
    public string Amps => SiPrefix.Format(row.Amps, "A", 3);
    public string Watts => SiPrefix.Format(row.Watts, "W", 3);
}

/// <summary>
/// The power budget: what every part is dissipating, against what it is rated for, and what the
/// whole thing costs to run.
/// <para>
/// It defaults to averaging across a run rather than reading the instant, because the instant is
/// wrong for anything that switches and there is no way for the window to know whether this
/// circuit does. A snapshot of a MOSFET caught mid-edge reports several watts in a part that is
/// dissipating milliwatts, which is the kind of answer that sends somebody looking for a heatsink
/// they do not need — or, with the sign of the luck reversed, does not send them looking for one
/// they do.
/// </para>
/// </summary>
public sealed partial class RatingsViewModel : ObservableObject
{
    private readonly SimulationController _simulation;

    public RatingsViewModel(SimulationController simulation)
    {
        _simulation = simulation;
        Run();
    }

    /// <summary>Every part with something said about it, nearest a limit first.</summary>
    public ObservableCollection<PartRatingsViewModel> Parts { get; } = [];

    /// <summary>Everything delivering power.</summary>
    public ObservableCollection<SupplyRowViewModel> Supplies { get; } = [];

    /// <summary>
    /// How long to average over, in seconds. Zero reads the instant instead, which is the right
    /// answer for a circuit that does not switch and a trap for one that does.
    /// </summary>
    [ObservableProperty]
    public partial double AverageOverSeconds { get; set; } = 0.01;

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>The sentence under the summary that qualifies it — averaged, or an instant.</summary>
    [ObservableProperty]
    public partial string Basis { get; private set; } = string.Empty;

    /// <summary>How long a battery would last at this draw, or empty when there is no answer.</summary>
    [ObservableProperty]
    public partial string BatteryLife { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasBattery { get; private set; }

    /// <summary>True when there is nothing to report, so the window can say so plainly.</summary>
    public bool IsEmpty => Parts.Count == 0 && Supplies.Count == 0;

    /// <summary>
    /// True when anything is delivering power, so the supplies table can be hidden when nothing is.
    /// <para>
    /// A property rather than binding the collection's count straight to visibility: that relies on
    /// a number converting to a boolean, which is the kind of thing that works until it silently
    /// does not.
    /// </para>
    /// </summary>
    public bool HasSupplies => Supplies.Count > 0;

    [RelayCommand]
    public void Run()
    {
        Parts.Clear();
        Supplies.Clear();

        if (_simulation.Simulator is null && !_simulation.Rebuild())
        {
            Summary = _simulation.Status;
            Basis = string.Empty;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasSupplies));
            return;
        }

        var result = Measure();

        // Grouped by part, in the order the findings arrived — which is worst first, so the part
        // with the worst single finding leads.
        foreach (var group in result.Findings.GroupBy(f => f.Name))
            Parts.Add(new PartRatingsViewModel(group.Key, group.First().Type, group));

        foreach (var row in result.Supplies) Supplies.Add(new SupplyRowViewModel(row));

        Summary = result.Summary();

        var ambient = _simulation.Settings.TemperatureKelvin - 273.15;

        Basis = result.IsAveraged
            ? $"Peaks and averages across {SiPrefix.Format(result.Seconds, "s", 3)} of running, at " +
              $"{ambient:0.#} °C ambient. Voltage and current are the worst reached; dissipation " +
              "is the mean, because one is a breakdown and the other is heat."
            : $"At the operating point, at {ambient:0.#} °C ambient. A circuit that switches needs " +
              "a run — put a duration in the box and measure again.";

        Describe(result);

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasSupplies));
    }

    /// <summary>
    /// Runs the budget, leaving the simulation where it found it.
    /// <para>
    /// Averaging advances time, and the window is opened from a circuit somebody is in the middle
    /// of looking at. Putting the clock back is not possible — the circuit's state has genuinely
    /// moved on — but pausing first is, and running a paused circuit forward underneath somebody
    /// is the part that would be surprising.
    /// </para>
    /// </summary>
    private RatingsResult Measure()
    {
        var wasRunning = _simulation.IsRunning;

        if (wasRunning) _simulation.Pause();

        try
        {
            var check = new RatingsCheck(_simulation.Simulator!);

            return AverageOverSeconds > 0 ? check.Over(AverageOverSeconds) : check.At();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ConvergenceException)
        {
            Summary = $"Could not run the circuit that far: {ex.Message}";

            return RatingsResult.Empty;
        }
        finally
        {
            if (wasRunning) _simulation.Play();
        }
    }

    /// <summary>
    /// How long the batteries in the circuit would last at this draw.
    /// <para>
    /// The capacity comes off the cells actually on the sheet rather than from a box to type one
    /// into: the drawing already says what it is powered by, and asking again would be asking
    /// somebody to repeat themselves and then disagree with their own schematic.
    /// </para>
    /// </summary>
    private void Describe(RatingsResult result)
    {
        var capacity = Flattening.Flatten(_simulation.Circuit.Components)
            .OfType<Battery>()
            .Sum(b => b.Model.CapacityMilliampHours);

        HasBattery = capacity > 0;

        if (!HasBattery) { BatteryLife = string.Empty; return; }

        if (result.RunsFor(capacity) is not { } life)
        {
            BatteryLife = "Nothing is being drawn, so there is nothing to run down.";
            return;
        }

        BatteryLife =
            $"{capacity:0} mAh at {SiPrefix.Format(result.SuppliedAmps, "A", 3)} is about " +
            $"{Spell(life)} — arithmetic only: a real cell delivers less at a high current, less " +
            "again when it is cold, and a circuit that sleeps between bursts draws nothing like " +
            "this on average.";
    }

    /// <summary>A duration in the units somebody would say it in.</summary>
    public static string Spell(TimeSpan life) => life.TotalHours switch
    {
        < 1 => $"{life.TotalMinutes:0} minutes",
        < 48 => $"{life.TotalHours:0.#} hours",
        < 24 * 365 => $"{life.TotalDays:0.#} days",
        _ => $"{life.TotalDays / 365.25:0.#} years",
    };
}
