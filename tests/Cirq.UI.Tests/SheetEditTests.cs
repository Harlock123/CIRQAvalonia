using Cirq.Components.Passive;
using Cirq.Components.Nonlinear;
using Cirq.Core.Topology;
using Cirq.UI.Services;

namespace Cirq.UI.Tests;

/// <summary>
/// Changing several parts at once, and turning what they were typed with into a parameter.
/// </summary>
public class SheetEditTests
{
    private static (Circuit Circuit, List<CircuitComponent> Parts) Four(double ohms = 10e3)
    {
        var circuit = new Circuit();

        List<CircuitComponent> parts =
        [
            circuit.Add(new Resistor(ohms) { Name = "R1" }),
            circuit.Add(new Resistor(ohms) { Name = "R2" }),
            circuit.Add(new Resistor(ohms) { Name = "R3" }),
            circuit.Add(new Resistor(ohms) { Name = "R4" }),
        ];

        return (circuit, parts);
    }

    [Fact]
    public void SettingOnePropertyChangesEveryPart()
    {
        var (_, parts) = Four();

        var result = SheetEdit.Set(parts, "Resistance", "12k");

        Assert.Equal(4, result.Changed);
        Assert.All(parts, p => Assert.Equal(12e3, ((Resistor)p).Resistance, 1e-9));
        Assert.Contains("4 parts changed", result.Summary(), StringComparison.Ordinal);
    }

    /// <summary>The value goes through the same parser the properties panel uses.</summary>
    [Theory]
    [InlineData("4k7", 4700)]
    [InlineData("100", 100)]
    [InlineData("2.2M", 2.2e6)]
    public void ValuesAreReadTheWayTheyAreWrittenOnParts(string typed, double expected)
    {
        var (_, parts) = Four();

        SheetEdit.Set(parts, "Resistance", typed);

        Assert.Equal(expected, ((Resistor)parts[0]).Resistance, 1e-6);
    }

    /// <summary>Nonsense changes nothing and says so, rather than setting everything to zero.</summary>
    [Fact]
    public void NonsenseChangesNothing()
    {
        var (_, parts) = Four();

        var result = SheetEdit.Set(parts, "Resistance", "banana");

        Assert.Equal(0, result.Changed);
        Assert.All(parts, p => Assert.Equal(10e3, ((Resistor)p).Resistance, 1e-9));
    }

    /// <summary>
    /// A part without the setting is named rather than counted. "Three were skipped" is the
    /// beginning of a question; naming them is the end of one.
    /// </summary>
    [Fact]
    public void APartWithoutTheSettingIsNamed()
    {
        var (circuit, parts) = Four();

        parts.Add(circuit.Add(new Diode { Name = "D1" }));

        var result = SheetEdit.Set(parts, "Resistance", "12k");

        Assert.Equal(4, result.Changed);
        Assert.Contains(result.Skipped, s => s.Contains("D1", StringComparison.Ordinal));
    }

    // ---- binding -----------------------------------------------------------

    [Fact]
    public void BindingPointsEveryPartAtAParameter()
    {
        var (circuit, parts) = Four();

        circuit.Parameters.Add(new CircuitParameter { Name = "Rf", Expression = "22k" });

        var result = SheetEdit.Bind(circuit, parts, "Resistance", "Rf");

        Assert.Equal(4, result.Changed);
        Assert.All(parts, p => Assert.Equal(22e3, ((Resistor)p).Resistance, 1e-9));
        Assert.All(parts, p => Assert.Equal("Rf", p.Expressions["Resistance"]));
    }

    /// <summary>And an expression over it, not only the bare name.</summary>
    [Fact]
    public void BindingTakesArithmetic()
    {
        var (circuit, parts) = Four();

        circuit.Parameters.Add(new CircuitParameter { Name = "Rf", Expression = "20k" });

        SheetEdit.Bind(circuit, parts, "Resistance", "Rf / 4");

        Assert.All(parts, p => Assert.Equal(5e3, ((Resistor)p).Resistance, 1e-9));
    }

    /// <summary>
    /// Typing a value over a bound part breaks the binding, which is the only way to undo one and
    /// the way somebody would expect to.
    /// </summary>
    [Fact]
    public void SettingAValueBreaksTheBinding()
    {
        var (circuit, parts) = Four();

        circuit.Parameters.Add(new CircuitParameter { Name = "Rf", Expression = "22k" });
        SheetEdit.Bind(circuit, parts, "Resistance", "Rf");

        SheetEdit.Set(parts, "Resistance", "1k");

        Assert.All(parts, p => Assert.False(p.Expressions.ContainsKey("Resistance")));
        Assert.All(parts, p => Assert.Equal(1e3, ((Resistor)p).Resistance, 1e-9));
    }

    // ---- extracting --------------------------------------------------------

    /// <summary>
    /// The migration in one step: four parts that are all 10k become a parameter and four bindings
    /// to it. Without this, parameters only ever help circuits started after they existed.
    /// </summary>
    [Fact]
    public void ExtractingNamesWhatTheyAllShareAndBindsThemToIt()
    {
        var (circuit, parts) = Four(4700);

        var result = SheetEdit.Extract(circuit, parts, "Resistance", "Rbias");

        Assert.Equal(4, result.Changed);

        var parameter = Assert.Single(circuit.Parameters);

        Assert.Equal("Rbias", parameter.Name);
        Assert.All(parts, p => Assert.Equal("Rbias", p.Expressions["Resistance"]));

        // And they still read what they read before.
        Assert.All(parts, p => Assert.Equal(4700, ((Resistor)p).Resistance, 1e-6));

        // Changing the parameter now moves all four, which is the point of having done it.
        parameter.Expression = "6k8";
        CircuitParameters.Apply(circuit.Parameters, circuit.Components);

        Assert.All(parts, p => Assert.Equal(6800, ((Resistor)p).Resistance, 1e-6));
    }

    /// <summary>
    /// Refused when they are not all the same, because a name for a number they do not share would
    /// be a name for a number that is not there.
    /// </summary>
    [Fact]
    public void ExtractingIsRefusedWhenTheyDisagree()
    {
        var (circuit, parts) = Four();

        ((Resistor)parts[2]).Resistance = 22e3;

        var result = SheetEdit.Extract(circuit, parts, "Resistance", "Rbias");

        Assert.Equal(0, result.Changed);
        Assert.Empty(circuit.Parameters);
        Assert.Contains("not all set to the same", result.Summary(), StringComparison.Ordinal);
    }

    /// <summary>And when the name is already taken, rather than making a second one.</summary>
    [Fact]
    public void ExtractingWillNotTakeANameThatIsTaken()
    {
        var (circuit, parts) = Four();

        circuit.Parameters.Add(new CircuitParameter { Name = "Rbias", Expression = "1k" });

        var result = SheetEdit.Extract(circuit, parts, "Resistance", "Rbias");

        Assert.Equal(0, result.Changed);
        Assert.Single(circuit.Parameters);
        Assert.Contains("already a parameter", result.Summary(), StringComparison.Ordinal);
    }

    // ---- what can be changed -----------------------------------------------

    /// <summary>
    /// Only the settings every chosen part has, so a picker cannot offer one that would skip half
    /// of them.
    /// </summary>
    [Fact]
    public void OnlyTheSettingsTheyAllShareAreOffered()
    {
        var (circuit, parts) = Four();

        Assert.Contains("Resistance", SheetEdit.SharedProperties(parts));

        parts.Add(circuit.Add(new Capacitor(1e-6) { Name = "C1" }));

        var shared = SheetEdit.SharedProperties(parts);

        Assert.DoesNotContain("Resistance", shared);
        Assert.DoesNotContain("Capacitance", shared);

        // Tolerance is on both, so it survives the intersection.
        Assert.Contains("Tolerance", shared);
    }

    /// <summary>Placement is not a setting, and varying it would just move the part.</summary>
    [Fact]
    public void PlacementIsNotOffered()
    {
        var (_, parts) = Four();

        var shared = SheetEdit.SharedProperties(parts);

        Assert.DoesNotContain("X", shared);
        Assert.DoesNotContain("Y", shared);
        Assert.DoesNotContain("RotationDegrees", shared);
    }
}
