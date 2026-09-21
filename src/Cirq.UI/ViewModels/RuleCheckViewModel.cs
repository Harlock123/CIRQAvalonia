using System.Collections.ObjectModel;
using Cirq.Components.Diagnostics;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>One finding, dressed for the list.</summary>
public sealed partial class RuleRowViewModel : ObservableObject
{
    public RuleRowViewModel(RuleFinding finding)
    {
        Finding = finding;
    }

    public RuleFinding Finding { get; }

    /// <summary>"Error" or "Warning", for the chip at the left of the row.</summary>
    public string Severity => Finding.Severity.ToString();

    public bool IsError => Finding.Severity == RuleSeverity.Error;

    public string Message => Finding.Message;

    /// <summary>The parts involved, or "circuit" when it is about the whole thing.</summary>
    public string Where => Finding.Components.Count == 0 ? "circuit" : Finding.Where;
}

/// <summary>
/// The rule-check window: run the checks, list what came back, and take you to the part.
/// <para>
/// Components already report their own violations, and those appear on the canvas as a red ring
/// while the circuit runs. This is the other half — the mistakes no component can see, because
/// they are about how the parts are joined rather than about any one of them. A ground pin wired
/// to a rail, two outputs fighting, a floating input, a mistyped net label: every one of those
/// produces a number rather than an error, and a number is much harder to disbelieve than a red
/// ring.
/// </para>
/// </summary>
public sealed partial class RuleCheckViewModel : ObservableObject
{
    private readonly Circuit _circuit;

    public RuleCheckViewModel(Circuit circuit)
    {
        _circuit = circuit;
        Run();
    }

    /// <summary>What the last run found, errors first.</summary>
    public ObservableCollection<RuleRowViewModel> Rows { get; } = [];

    /// <summary>The row the list has selected, which is the one the canvas will point at.</summary>
    [ObservableProperty]
    public partial RuleRowViewModel? Selected { get; set; }

    /// <summary>A sentence about the run as a whole.</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>True when the check found nothing, so the window can say so plainly.</summary>
    public bool IsClean => Rows.Count == 0;

    /// <summary>Raised when a finding is chosen, so the canvas can select and reveal its parts.</summary>
    public event EventHandler<IReadOnlyList<CircuitComponent>>? RevealRequested;

    [RelayCommand]
    public void Run()
    {
        Rows.Clear();

        foreach (var finding in ElectricalRuleCheck.Run(_circuit))
            Rows.Add(new RuleRowViewModel(finding));

        var errors = Rows.Count(r => r.IsError);
        var warnings = Rows.Count - errors;

        Summary = Rows.Count == 0
            ? "Nothing to report. The wiring passes every check."
            : $"{Count(errors, "error")}, {Count(warnings, "warning")}.";

        OnPropertyChanged(nameof(IsClean));
    }

    /// <summary>Selects the parts a finding is about, so it can be found on a busy canvas.</summary>
    [RelayCommand]
    public void Reveal(RuleRowViewModel? row)
    {
        var target = row ?? Selected;
        if (target is null || target.Finding.Components.Count == 0) return;

        RevealRequested?.Invoke(this, target.Finding.Components);
    }

    private static string Count(int n, string noun) =>
        n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
