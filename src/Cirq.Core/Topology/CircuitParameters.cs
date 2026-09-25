using Cirq.Core.Probing;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Core.Topology;

/// <summary>
/// A number the circuit gives a name to, so that several parts can be set from one place.
/// </summary>
/// <remarks>
/// The value is an expression rather than a number so that parameters can be written in terms of
/// each other — <c>Rin = Rf / 10</c>, <c>C = 1 / (2 * pi * R * fc)</c>. A design is usually a few
/// choices and a lot of consequences, and writing the consequences down as consequences is what
/// stops them drifting apart when a choice changes.
/// </remarks>
public sealed partial class CircuitParameter : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>What it is called, which is what a part's expression refers to.</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = "R";

    /// <summary>
    /// What it works out to — a number, or arithmetic over the other parameters. SI prefixes are
    /// understood, so <c>4k7</c> and <c>100n</c> are both values.
    /// </summary>
    [ObservableProperty]
    public partial string Expression { get; set; } = "1k";

    /// <summary>What it is for, in the words of whoever wrote it down.</summary>
    [ObservableProperty]
    public partial string Note { get; set; } = string.Empty;
}

/// <summary>What a set of parameters worked out to, and what would not.</summary>
/// <param name="Values">Every parameter that resolved, by name.</param>
/// <param name="Problems">What could not be worked out, and why, by name.</param>
public sealed record ParameterValues(
    IReadOnlyDictionary<string, double> Values,
    IReadOnlyDictionary<string, string> Problems)
{
    public static ParameterValues Empty { get; } =
        new(new Dictionary<string, double>(), new Dictionary<string, string>());

    public bool IsClean => Problems.Count == 0;
}

/// <summary>
/// Works out a circuit's parameters, and applies them to the parts that are bound to them.
/// <para>
/// A value typed into a part is a value in one place. A design is usually a handful of choices and
/// a great many consequences of those choices — four resistors that are all the same feedback
/// resistor, a capacitor that has to track a corner frequency, a divider whose ratio matters and
/// whose absolute impedance does not. Typed as literals, the consequences stop being consequences
/// the first time a choice changes, and nothing in the file says they were ever related.
/// </para>
/// <para>
/// Parameters may be written in terms of each other, so they are resolved in dependency order
/// rather than in the order they appear. A cycle is reported rather than iterated: two parameters
/// defined in terms of each other have no value, and a solver that ran round the loop a hundred
/// times would be inventing one.
/// </para>
/// </summary>
public static class CircuitParameters
{
    /// <summary>
    /// Names that are always available, and cannot be used as parameter names.
    /// <para>
    /// Just the one. <c>pi</c> turns up in most of the formulas anybody would write here — a corner
    /// frequency, a resonance, a time constant against a rate — and typing it out is both tedious
    /// and a chance to get it wrong.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, double> Constants { get; } =
        new Dictionary<string, double> { ["pi"] = Math.PI };

    /// <summary>
    /// Works every parameter out, in whatever order their dependencies require.
    /// </summary>
    public static ParameterValues Resolve(IEnumerable<CircuitParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var all = parameters.ToList();

        Dictionary<string, double> values = new(Constants);
        Dictionary<string, string> problems = [];

        // Names seen twice are a mistake worth naming: one of them is silently not being used, and
        // which one would depend on the order they happen to be in.
        foreach (var duplicate in all.GroupBy(p => p.Name).Where(g => g.Count() > 1))
            problems[duplicate.Key] = $"There is more than one parameter called \"{duplicate.Key}\".";

        foreach (var clash in all.Where(p => Constants.ContainsKey(p.Name)))
            problems[clash.Name] = $"\"{clash.Name}\" is always available and cannot be redefined.";

        // Repeated passes rather than a topological sort: a parameter becomes resolvable once
        // everything it names has resolved, so a pass that resolves nothing new means the rest are
        // waiting on each other or on something that is not there. Bounded by the count, because
        // each pass resolves at least one or stops.
        var pending = all.Where(p => !problems.ContainsKey(p.Name)).ToList();

        for (var pass = 0; pass <= all.Count && pending.Count > 0; pass++)
        {
            var resolved = 0;

            foreach (var parameter in pending.ToList())
            {
                // An SI value first, so "4k7" is a value rather than a name followed by a name.
                if (SiPrefix.TryParse(parameter.Expression, out var literal))
                {
                    values[parameter.Name] = literal;
                    pending.Remove(parameter);
                    resolved++;
                    continue;
                }

                try
                {
                    values[parameter.Name] = TraceExpression.Number(parameter.Expression, values, Noun);
                    pending.Remove(parameter);
                    resolved++;
                }
                catch (ExpressionException)
                {
                    // Maybe it names something that has not resolved yet. If nothing resolves this
                    // pass, it never will, and the loop stops and reports it below.
                }
            }

            if (resolved == 0) break;
        }

        // What is left is either waiting on something that is also stuck — which is a cycle, since
        // nothing else is going to resolve now — or naming something that is not here at all. The
        // two need different sentences: one is a design the author has to untangle, the other is a
        // typo.
        var stuckNames = pending.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Every name that is legal to write, so the expression parses far enough to say what it
        // refers to rather than stopping at the first name that has no value yet.
        var legal = values.Keys.Concat(all.Select(p => p.Name)).Distinct().ToList();

        foreach (var stuck in pending)
        {
            List<string> referenced;

            try
            {
                referenced = [.. TraceExpression.NamesIn(stuck.Expression, legal, Noun)];
            }
            catch (ExpressionException ex)
            {
                // It names something that is not a parameter at all, or it does not parse. Either
                // way there is nothing to untangle and the parser's own message says which — and
                // says it in this vocabulary, because it was told what a name is called here.
                problems[stuck.Name] = $"\"{stuck.Name}\": {ex.Message}";
                continue;
            }

            var waiting = referenced.Where(stuckNames.Contains).Distinct().ToList();

            // Everything it names is legal and everything legal is either resolved or stuck, so a
            // parameter that got this far names at least one stuck one — which, since nothing more
            // is going to resolve, means they are waiting on each other.
            problems[stuck.Name] = waiting.Count > 0
                ? $"\"{stuck.Name}\" depends on {Join(waiting)}, which cannot be worked out — " +
                  "these depend on each other."
                : $"\"{stuck.Name}\" cannot be worked out.";
        }

        // The constants are always available but are not the circuit's own parameters, and a caller
        // listing what the design defines should not be shown pi.
        foreach (var constant in Constants.Keys) values.Remove(constant);

        return new ParameterValues(values, problems);
    }

    /// <summary>
    /// Works the parameters out and writes every bound setting from them.
    /// <para>
    /// Called whenever a parameter changes and whenever a circuit is opened, so that what the
    /// solver sees is always plain numbers. A part whose expression cannot be worked out is left
    /// holding whatever it had, and the reason is reported — clearing it to zero would turn a typo
    /// in one box into a circuit that solves and is wrong.
    /// </para>
    /// </summary>
    /// <returns>What the parameters resolved to, and what did not, including the bindings.</returns>
    public static ParameterValues Apply(
        IEnumerable<CircuitParameter> parameters, IEnumerable<CircuitComponent> components)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(components);

        var resolved = Resolve(parameters);
        var available = Available(resolved);

        Dictionary<string, string> problems = new(resolved.Problems);

        foreach (var component in Flattening.Flatten(components))
        {
            foreach (var (property, expression) in component.Expressions.ToList())
            {
                var target = component.GetType().GetProperty(property);

                if (target is null || !target.CanWrite)
                {
                    problems[$"{component.Name}.{property}"] =
                        $"{component.Name} has no setting called \"{property}\" any more.";

                    continue;
                }

                double value;

                try
                {
                    value = SiPrefix.TryParse(expression, out var literal)
                        ? literal
                        : TraceExpression.Number(expression, available, Noun);
                }
                catch (ExpressionException ex)
                {
                    problems[$"{component.Name}.{property}"] = $"{component.Name}: {ex.Message}";
                    continue;
                }

                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    problems[$"{component.Name}.{property}"] =
                        $"{component.Name}: \"{expression}\" does not work out to a number.";

                    continue;
                }

                if (target.PropertyType == typeof(int))
                    target.SetValue(component, (int)Math.Round(value));
                else
                    target.SetValue(component, value);
            }
        }

        return new ParameterValues(resolved.Values, problems);
    }

    /// <summary>What an unresolvable name is called in a message about parameters.</summary>
    private const string Noun = "parameter";

    private static string Join(List<string> names) =>
        names.Count == 1 ? $"\"{names[0]}\"" : string.Join(", ", names.Select(n => $"\"{n}\""));

    /// <summary>
    /// Everything a set of parameters resolves to, including the constants — which is what an
    /// expression on a part has to be worked out against.
    /// </summary>
    public static IReadOnlyDictionary<string, double> Available(ParameterValues resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        Dictionary<string, double> values = new(Constants);

        foreach (var (name, value) in resolved.Values) values[name] = value;

        return values;
    }
}
