using Cirq.Components.Passive;
using Cirq.Components.Serialization;
using Cirq.Core.Topology;

namespace Cirq.Components.Tests;

/// <summary>
/// Numbers the circuit gives names to, and the parts that are set from them.
/// <para>
/// The thing being tested throughout is that a <i>consequence</i> stays a consequence: change the
/// choice, and everything derived from it moves. A value typed into four boxes stops being related
/// to itself the first time three of them are updated.
/// </para>
/// </summary>
public class ParameterTests
{
    private static CircuitParameter P(string name, string expression) =>
        new() { Name = name, Expression = expression };

    [Fact]
    public void APlainValueResolvesToItself()
    {
        var resolved = CircuitParameters.Resolve([P("Rf", "4k7")]);

        Assert.True(resolved.IsClean);
        Assert.Equal(4700.0, resolved.Values["Rf"], 1e-9);
    }

    /// <summary>One parameter written in terms of another is the whole point.</summary>
    [Fact]
    public void OneParameterCanBeWrittenInTermsOfAnother()
    {
        var resolved = CircuitParameters.Resolve([P("Rf", "10k"), P("Rin", "Rf / 10")]);

        Assert.True(resolved.IsClean);
        Assert.Equal(1000.0, resolved.Values["Rin"], 1e-9);
    }

    /// <summary>
    /// And in whatever order they happen to be written in. A file is a list and a design is a
    /// graph, and insisting the list be sorted would be insisting the author sort it.
    /// </summary>
    [Fact]
    public void TheOrderTheyAreWrittenInDoesNotMatter()
    {
        var resolved = CircuitParameters.Resolve([
            P("C", "1 / (2 * pi * R * fc)"),
            P("fc", "1k"),
            P("R", "10k"),
        ]);

        Assert.True(resolved.IsClean);

        // 1/(2π·10k·1k) is about 15.9 nF.
        Assert.Equal(1.0 / (2 * Math.PI * 10e3 * 1e3), resolved.Values["C"], 1e-15);
    }

    /// <summary>Pi is always there, because most of the formulas anybody writes here need it.</summary>
    [Fact]
    public void PiIsAlwaysAvailable()
    {
        var resolved = CircuitParameters.Resolve([P("halfTurn", "pi")]);

        Assert.Equal(Math.PI, resolved.Values["halfTurn"], 1e-12);

        // And it is not listed as one of the circuit's own, because the circuit did not define it.
        Assert.DoesNotContain("pi", resolved.Values.Keys);
    }

    /// <summary>
    /// A cycle is reported rather than iterated. Two parameters defined in terms of each other have
    /// no value, and running round the loop a hundred times would be inventing one.
    /// </summary>
    [Fact]
    public void ACycleIsReportedRatherThanIterated()
    {
        var resolved = CircuitParameters.Resolve([P("a", "b + 1"), P("b", "a + 1")]);

        Assert.False(resolved.IsClean);
        Assert.Equal(2, resolved.Problems.Count);
        Assert.All(resolved.Problems.Values, p => Assert.Contains("depends on", p, StringComparison.Ordinal));
    }

    /// <summary>Two parameters with one name is a mistake worth naming.</summary>
    [Fact]
    public void ADuplicateNameIsReported()
    {
        var resolved = CircuitParameters.Resolve([P("R", "1k"), P("R", "2k")]);

        Assert.False(resolved.IsClean);
        Assert.Contains("more than one", resolved.Problems["R"], StringComparison.Ordinal);
    }

    /// <summary>
    /// A typo and a cycle get different sentences, because they are different problems: one is a
    /// design to untangle and the other is a name to correct.
    /// </summary>
    [Fact]
    public void ATypoAndACycleReadDifferently()
    {
        var typo = CircuitParameters.Resolve([P("R", "1k"), P("C", "Rf / 10")]);
        var cycle = CircuitParameters.Resolve([P("a", "b + 1"), P("b", "a + 1")]);

        Assert.Contains("no parameter called 'Rf'", typo.Problems["C"], StringComparison.Ordinal);

        Assert.Contains("depend on each other", cycle.Problems["a"], StringComparison.Ordinal);
    }

    /// <summary>
    /// And neither calls a parameter a trace. The expression language is shared with the scope's
    /// trace arithmetic; the vocabulary in the error message is not, because "there is no trace
    /// called Rf" is a confusing thing to be told about a circuit parameter.
    /// </summary>
    [Fact]
    public void NothingCallsAParameterATrace()
    {
        var resolved = CircuitParameters.Resolve([P("C", "Rf / 10")]);

        Assert.DoesNotContain("trace", resolved.Problems["C"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("parameter", resolved.Problems["C"], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>And a name that is already taken by a constant.</summary>
    [Fact]
    public void AConstantCannotBeRedefined()
    {
        var resolved = CircuitParameters.Resolve([P("pi", "3")]);

        Assert.False(resolved.IsClean);
        Assert.Contains("cannot be redefined", resolved.Problems["pi"], StringComparison.Ordinal);
    }

    /// <summary>One broken parameter does not take the others down with it.</summary>
    [Fact]
    public void OneBadParameterLeavesTheRestAlone()
    {
        var resolved = CircuitParameters.Resolve([P("good", "1k"), P("bad", "nonsense * 2")]);

        Assert.Equal(1000.0, resolved.Values["good"], 1e-9);
        Assert.Contains("bad", resolved.Problems.Keys);
    }

    // ---- applied to parts --------------------------------------------------

    private static (Circuit Circuit, Resistor R1, Resistor R2) Divider()
    {
        var circuit = new Circuit();

        circuit.Parameters.Add(P("Rf", "10k"));

        var r1 = circuit.Add(new Resistor(1.0) { Name = "R1" });
        var r2 = circuit.Add(new Resistor(1.0) { Name = "R2" });

        r1.Expressions["Resistance"] = "Rf";
        r2.Expressions["Resistance"] = "Rf / 4";

        return (circuit, r1, r2);
    }

    [Fact]
    public void ABoundSettingIsWrittenFromItsExpression()
    {
        var (circuit, r1, r2) = Divider();

        var result = CircuitParameters.Apply(circuit.Parameters, circuit.Components);

        Assert.True(result.IsClean);
        Assert.Equal(10e3, r1.Resistance, 1e-9);
        Assert.Equal(2500.0, r2.Resistance, 1e-9);
    }

    /// <summary>Change the choice, and every consequence moves. This is the feature.</summary>
    [Fact]
    public void ChangingTheParameterMovesEverythingDerivedFromIt()
    {
        var (circuit, r1, r2) = Divider();

        CircuitParameters.Apply(circuit.Parameters, circuit.Components);

        circuit.Parameters[0].Expression = "22k";

        CircuitParameters.Apply(circuit.Parameters, circuit.Components);

        Assert.Equal(22e3, r1.Resistance, 1e-9);
        Assert.Equal(5500.0, r2.Resistance, 1e-9);
    }

    /// <summary>
    /// A binding that cannot be worked out leaves the part holding what it had, and says why.
    /// Clearing it to zero would turn a typo in one box into a circuit that solves and is wrong.
    /// </summary>
    [Fact]
    public void ABrokenBindingLeavesTheValueAloneAndSaysSo()
    {
        var (circuit, r1, _) = Divider();

        CircuitParameters.Apply(circuit.Parameters, circuit.Components);

        r1.Expressions["Resistance"] = "Rf / nothing";

        var result = CircuitParameters.Apply(circuit.Parameters, circuit.Components);

        Assert.Equal(10e3, r1.Resistance, 1e-9);
        Assert.Contains("R1.Resistance", result.Problems.Keys);
    }

    /// <summary>A binding to a setting that no longer exists is reported by name.</summary>
    [Fact]
    public void ABindingToASettingThatIsGoneIsReported()
    {
        var circuit = new Circuit();

        var r = circuit.Add(new Resistor(1e3) { Name = "R1" });

        r.Expressions["NoSuchSetting"] = "1k";

        var result = CircuitParameters.Apply(circuit.Parameters, circuit.Components);

        Assert.Contains("R1.NoSuchSetting", result.Problems.Keys);
    }

    // ---- through the file --------------------------------------------------

    [Fact]
    public void ParametersAndBindingsSurviveARoundTrip()
    {
        var (circuit, _, _) = Divider();

        CircuitParameters.Apply(circuit.Parameters, circuit.Components);

        var back = CircuitSerializer.FromJson(CircuitSerializer.ToJson(circuit)).Circuit;

        var parameter = Assert.Single(back.Parameters);

        Assert.Equal("Rf", parameter.Name);
        Assert.Equal("10k", parameter.Expression);

        var r2 = back.Components.OfType<Resistor>().Single(r => r.Name == "R2");

        Assert.Equal("Rf / 4", r2.Expressions["Resistance"]);
        Assert.Equal(2500.0, r2.Resistance, 1e-9);
    }

    /// <summary>
    /// The plain value is written out as well as the expression, so a build that has never heard of
    /// parameters opens the file and gets a working circuit with the right numbers in it.
    /// </summary>
    [Fact]
    public void ThePlainValueIsWrittenOutToo()
    {
        var (circuit, _, _) = Divider();

        CircuitParameters.Apply(circuit.Parameters, circuit.Components);

        var json = CircuitSerializer.ToJson(circuit);

        Assert.Contains("\"Resistance\": 2500", json, StringComparison.Ordinal);
        Assert.Contains("Rf / 4", json, StringComparison.Ordinal);
    }

    /// <summary>A circuit that uses none of this writes none of it.</summary>
    [Fact]
    public void ACircuitWithoutParametersIsUnchangedOnDisk()
    {
        var circuit = new Circuit();

        circuit.Add(new Resistor(1e3) { Name = "R1" });

        var json = CircuitSerializer.ToJson(circuit);

        Assert.DoesNotContain("\"parameters\": [", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"expressions\"", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A file written by a newer version whose binding refers to a parameter this one does not have
    /// opens, keeps its value, and says what it could not work out.
    /// </summary>
    [Fact]
    public void AFileWhoseBindingCannotBeResolvedStillOpens()
    {
        var circuit = new Circuit();

        var r = circuit.Add(new Resistor(4700.0) { Name = "R1" });
        r.Expressions["Resistance"] = "SomethingThisFileDoesNotDefine";

        var result = CircuitSerializer.FromJson(CircuitSerializer.ToJson(circuit));

        var back = result.Circuit.Components.OfType<Resistor>().Single();

        Assert.Equal(4700.0, back.Resistance, 1e-9);
        Assert.NotEmpty(result.Warnings);
    }
}
