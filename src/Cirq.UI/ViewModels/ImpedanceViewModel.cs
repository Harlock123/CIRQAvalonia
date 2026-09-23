using System.Collections.ObjectModel;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using Cirq.Engine.Simulation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>
/// The impedance window: what the circuit looks like from one pair of points.
/// <para>
/// Every bench question about loading is this one. What does this amplifier present to whatever
/// drives it; what can that regulator hold its output against when the load steps; where does the
/// decoupling network resonate, and how much does it actually help at the frequency the chip
/// switches at. A gain plot cannot answer any of them, because gain is a ratio between two points
/// and this is a property of one.
/// </para>
/// </summary>
public sealed partial class ImpedanceViewModel : ObservableObject
{
    private readonly Circuit _circuit;

    public ImpedanceViewModel(Circuit circuit)
    {
        _circuit = circuit;

        foreach (var probe in circuit.Probes.Where(p => p.TargetTerminal is not null))
            Probes.Add(probe);

        Probe = Probes.FirstOrDefault();
    }

    /// <summary>
    /// The points to look into, which are the ones already probed. Somewhere worth measuring the
    /// impedance of is nearly always somewhere already worth watching.
    /// </summary>
    public ObservableCollection<SignalProbe> Probes { get; } = [];

    [ObservableProperty]
    public partial SignalProbe? Probe { get; set; }

    [ObservableProperty]
    public partial double StartHz { get; set; } = 10;

    [ObservableProperty]
    public partial double StopHz { get; set; } = 1e8;

    [ObservableProperty]
    public partial int PointsPerDecade { get; set; } = 25;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Readout { get; set; } = string.Empty;

    public bool HasProbes => Probes.Count > 0;

    public IReadOnlyList<double> Frequencies { get; private set; } = [];

    /// <summary>Magnitude in ohms at each frequency.</summary>
    public IReadOnlyList<double> Ohms { get; private set; } = [];

    /// <summary>Phase in degrees: positive inductive, negative capacitive.</summary>
    public IReadOnlyList<double> Degrees { get; private set; } = [];

    /// <summary>Every frequency where the reactance changed sign, and which kind it is.</summary>
    public IReadOnlyList<(double Hertz, bool Series)> Resonances { get; private set; } = [];

    private (double Hertz, double Ohms)? _minimum;
    private (double Hertz, double Ohms)? _maximum;

    public bool HasResult => Ohms.Count > 0;

    /// <summary>What it looks like at the bottom of the band, which for most things is the DC value.</summary>
    public string LowFrequency => HasResult ? SiPrefix.Format(Ohms[0], "Ω") : "—";

    public string Minimum => _minimum is { } m
        ? $"{SiPrefix.Format(m.Ohms, "Ω")} at {SiPrefix.Format(m.Hertz, "Hz")}"
        : "—";

    public string Maximum => _maximum is { } m
        ? $"{SiPrefix.Format(m.Ohms, "Ω")} at {SiPrefix.Format(m.Hertz, "Hz")}"
        : "—";

    /// <summary>The resonances in words, which is the line people actually read.</summary>
    public string Verdict
    {
        get
        {
            if (!HasResult) return string.Empty;

            if (Resonances.Count == 0)
            {
                return "No resonance in this band: the reactance never changes sign, so nothing " +
                       "here is trading energy with anything else.";
            }

            var parts = Resonances.Select(r => r.Series
                ? $"a series resonance at {SiPrefix.Format(r.Hertz, "Hz")}, where the impedance dips"
                : $"a parallel resonance at {SiPrefix.Format(r.Hertz, "Hz")}, where it peaks");

            return char.ToUpperInvariant(string.Join("; ", parts)[0]) + string.Join("; ", parts)[1..] + ".";
        }
    }

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
                     [nameof(HasResult), nameof(LowFrequency), nameof(Minimum), nameof(Maximum), nameof(Verdict)])
            {
                OnPropertyChanged(name);
            }

            ResultChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Clear()
    {
        Frequencies = [];
        Ohms = [];
        Degrees = [];
        Resonances = [];
        _minimum = null;
        _maximum = null;
    }

    private void Execute()
    {
        Clear();

        if (Probe?.TargetTerminal is not { } terminal)
        {
            Status = "Put a probe on the point you want to look into, then run this again. " +
                     "An impedance is a property of one place in a circuit, so there has to be " +
                     "one to name.";
            return;
        }

        // Its own simulator, as the other analyses use: this sweeps the circuit many times over
        // and must not disturb what the canvas is doing.
        var simulator = new CircuitSimulator(_circuit);

        simulator.Reset();
        simulator.SolveOperatingPoint();

        var sweep = new AcSweepRequest(StartHz, StopHz, Math.Max(PointsPerDecade, 2));

        var result = new ImpedanceAnalysis(simulator).Run(
            new ImpedanceRequest(sweep, terminal, Probe.ReferenceTerminal));

        if (!result.IsUsable)
        {
            Status = result.Problem ?? "The analysis found nothing to report.";
            return;
        }

        Frequencies = result.Frequencies;
        Ohms = [.. Enumerable.Range(0, result.Impedance.Count).Select(result.Ohms)];
        Degrees = [.. Enumerable.Range(0, result.Impedance.Count).Select(result.Degrees)];
        Resonances = result.Resonances;

        _minimum = result.Minimum;
        _maximum = result.Maximum;

        var where = $"Looking into {Probe.Label}" +
                    (Probe.ReferenceTerminal is null ? " against ground" : " differentially");

        // Zero everywhere is a real answer and an unhelpful-looking one. It means the probe is on
        // a point an ideal source is holding, and an ideal source has no output impedance — so
        // say that rather than leaving a flat line at the bottom of the plot looking like a fault.
        if (_maximum is { Ohms: < 1e-9 })
        {
            Status = where + "   ·   zero at every frequency, because this point is held by an " +
                     "ideal source. Probe somewhere the circuit is doing the work, or give the " +
                     "source an output resistance and measure it again.";
            return;
        }

        Status = where + $"   ·   {SiPrefix.Format(Ohms[0], "Ω")} at the bottom of the band";
    }

    private static string Describe(Exception ex) => ex switch
    {
        CircuitTopologyException => $"The circuit will not build: {ex.Message}",
        ConvergenceException =>
            "The circuit has no operating point to linearise about: " + ex.Message,
        _ => $"The analysis failed: {ex.Message}",
    };
}
