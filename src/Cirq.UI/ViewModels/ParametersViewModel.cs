using System.Collections.ObjectModel;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>One named number, dressed for the list.</summary>
public sealed partial class ParameterRowViewModel(CircuitParameter parameter) : ObservableObject
{
    public CircuitParameter Parameter => parameter;

    /// <summary>What it works out to, or why it does not.</summary>
    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasProblem { get; set; }

    /// <summary>Which parts are set from it, for the line under the row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUsed))]
    public partial string UsedBy { get; set; } = string.Empty;

    public bool IsUsed => UsedBy.Length > 0;
}

/// <summary>
/// The parameters window: the numbers a circuit gives names to, and what they work out to.
/// <para>
/// A design is usually a handful of choices and a great many consequences of those choices. Typed
/// as literals into a dozen boxes, the consequences stop being consequences the first time a choice
/// changes, and nothing in the file records that they were ever related.
/// </para>
/// </summary>
public sealed partial class ParametersViewModel : ObservableObject
{
    private readonly Circuit _circuit;

    public ParametersViewModel(Circuit circuit)
    {
        _circuit = circuit;

        foreach (var parameter in circuit.Parameters) Rows.Add(new ParameterRowViewModel(parameter));

        Apply();
    }

    public ObservableCollection<ParameterRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    public bool HasRows => Rows.Count > 0;

    /// <summary>Raised when anything changes, so the circuit is marked edited and redrawn.</summary>
    public event EventHandler? Changed;

    [RelayCommand]
    private void Add()
    {
        var parameter = new CircuitParameter { Name = NextName(), Expression = "1k" };

        _circuit.Parameters.Add(parameter);
        Rows.Add(new ParameterRowViewModel(parameter));

        Apply();
    }

    /// <summary>A name not already taken, so adding two in a row does not make a duplicate.</summary>
    private string NextName()
    {
        for (var i = 1; ; i++)
        {
            var name = $"P{i}";

            if (!_circuit.Parameters.Any(p => p.Name == name)) return name;
        }
    }

    [RelayCommand]
    private void Remove(ParameterRowViewModel? row)
    {
        if (row is null) return;

        _circuit.Parameters.Remove(row.Parameter);
        Rows.Remove(row);

        Apply();
    }

    /// <summary>
    /// Works every parameter out and writes it into the parts bound to it.
    /// <para>
    /// Run on every edit rather than on a button, because the point of a parameter is that changing
    /// it changes the circuit — and a change that waits for a second click is a change somebody
    /// forgets to make.
    /// </para>
    /// </summary>
    [RelayCommand]
    public void Apply()
    {
        var result = CircuitParameters.Apply(_circuit.Parameters, _circuit.Components);

        foreach (var row in Rows)
        {
            var name = row.Parameter.Name;

            if (result.Problems.TryGetValue(name, out var problem))
            {
                row.Value = problem;
                row.HasProblem = true;
            }
            else if (result.Values.TryGetValue(name, out var value))
            {
                row.Value = SiPrefix.Format(value);
                row.HasProblem = false;
            }
            else
            {
                row.Value = "—";
                row.HasProblem = false;
            }

            row.UsedBy = string.Join(", ", UsersOf(name));
        }

        var broken = result.Problems.Count;

        Summary = Rows.Count == 0
            ? "No parameters yet. Add one, then set a part from it by typing = and its name into " +
              "any number — “=Rf”, or “=Rf / 10”."
            : broken == 0
                ? $"{Rows.Count} parameter{(Rows.Count == 1 ? "" : "s")}, all worked out."
                : $"{broken} of {Rows.Count} could not be worked out.";

        OnPropertyChanged(nameof(HasRows));

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The parts whose settings name this parameter, so a row says what it is holding up.
    /// <para>
    /// Matched by parsing each expression rather than by looking for the text, so <c>Rf</c> does
    /// not match a parameter called <c>Rf2</c> — and a part bound to <c>Rf / 10</c> is found by
    /// both halves of that.
    /// </para>
    /// </summary>
    private IEnumerable<string> UsersOf(string name)
    {
        var known = _circuit.Parameters.Select(p => p.Name)
            .Concat(CircuitParameters.Constants.Keys)
            .ToList();

        foreach (var component in Flattening.Flatten(_circuit.Components))
        {
            foreach (var (property, expression) in component.Expressions)
            {
                List<string> names;

                try
                {
                    names = [.. Cirq.Core.Probing.TraceExpression.NamesIn(expression, known, "parameter")];
                }
                catch (Cirq.Core.Probing.ExpressionException)
                {
                    continue;
                }

                if (names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    yield return $"{component.Name}.{ParameterNaming.Humanise(property)}";
            }
        }
    }
}
