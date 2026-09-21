using Cirq.Components.Diagnostics;
using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;

namespace Cirq.Components.Tests;

/// <summary>
/// The electrical rule check. Every rule here is one that has actually caught something, and each
/// test is built around the mistake it exists to find.
/// </summary>
public class ElectricalRuleCheckTests
{
    private static IReadOnlyList<RuleFinding> Of(Circuit circuit) => ElectricalRuleCheck.Run(circuit);

    private static bool Has(IReadOnlyList<RuleFinding> findings, string rule) =>
        findings.Any(f => f.Rule == rule);

    /// <summary>A circuit with nothing wrong with it, which must produce nothing.</summary>
    private static Circuit GoodDivider()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var top = circuit.Add(new Resistor(1e3));
        var bottom = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);

        return circuit;
    }

    [Fact]
    public void AGoodCircuitProducesNothing()
    {
        Assert.Empty(Of(GoodDivider()));
    }

    [Fact]
    public void AnEmptyCanvasIsNotAnError()
    {
        Assert.Empty(Of(new Circuit()));
    }

    [Fact]
    public void AMissingGroundIsReportedInWordsRatherThanAsASingularMatrix()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var load = circuit.Add(new Resistor(1e3));

        circuit.Connect(supply.Positive, load.A);
        circuit.Connect(load.B, supply.Negative);

        var findings = Of(circuit);

        Assert.True(Has(findings, "no-ground"));
        Assert.Contains(findings, f => f.Severity == RuleSeverity.Error);
        Assert.Contains("Ground", findings.First(f => f.Rule == "no-ground").Message);
    }

    [Fact]
    public void ASourceShortedAcrossItselfIsAnError()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, ground.Pin);

        var findings = Of(circuit);

        Assert.True(Has(findings, "shorted-source"));
        Assert.Contains(supply.Name, findings.First(f => f.Rule == "shorted-source").Where);
    }

    /// <summary>
    /// The ULN2003 mistake: the part has no supply pin, so its Vcc terminal is really its ground
    /// pin, and wiring it to a rail shorts the rail. Silent until the matrix goes singular.
    /// </summary>
    [Fact]
    public void ASupplyPinOnTheGroundNetIsCaught()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var gate = circuit.Add(new Ic7400());
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(gate.Vcc, ground.Pin);
        circuit.Connect(gate.Gnd, ground.Pin);

        var findings = Of(circuit);

        Assert.True(Has(findings, "supply-on-ground"));
        Assert.Equal(RuleSeverity.Error, findings.First(f => f.Rule == "supply-on-ground").Severity);
    }

    [Fact]
    public void APackageWithAnUnwiredSupplyPinIsCaught()
    {
        var circuit = new Circuit();
        var gate = circuit.Add(new Ic7400());
        var ground = circuit.Add(new Ground());

        circuit.Connect(gate.Gnd, ground.Pin);

        var findings = Of(circuit);

        Assert.True(Has(findings, "unpowered"));
        Assert.Contains(gate.Name, findings.First(f => f.Rule == "unpowered").Where);
    }

    [Fact]
    public void AFloatingInputIsAnErrorAndAFloatingPassiveOnlyAWarning()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var gate = circuit.Add(new Ic7400());
        var dangling = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(gate.Vcc, supply.Positive);
        circuit.Connect(gate.Gnd, ground.Pin);
        circuit.Connect(dangling.A, supply.Positive);

        var findings = Of(circuit);

        var floating = findings.Where(f => f.Rule == "floating").ToList();

        // The gate's unwired inputs, which have no defined level.
        Assert.Contains(floating, f => f.Severity == RuleSeverity.Error);

        // And the resistor's loose end, which is merely pointless.
        Assert.Contains(floating, f =>
            f.Severity == RuleSeverity.Warning && f.Where.Contains(dangling.Name));
    }

    /// <summary>
    /// Two bus transceivers whose enables were wired together, which is the fault the Shared Bus
    /// example is built around.
    /// </summary>
    [Fact]
    public void TwoDrivenOutputsOnOneNetAreFlagged()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var first = circuit.Add(new Ic7400());
        var second = circuit.Add(new Ic7400());
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);

        foreach (var gate in new[] { first, second })
        {
            circuit.Connect(gate.Vcc, supply.Positive);
            circuit.Connect(gate.Gnd, ground.Pin);

            foreach (var pin in gate.Terminals.Where(t => t.Type == TerminalType.Input))
                circuit.Connect(pin, ground.Pin);
        }

        // The two packages' first outputs, tied together.
        var a = first.Terminals.First(t => t.Type == TerminalType.Output);
        var b = second.Terminals.First(t => t.Type == TerminalType.Output);
        circuit.Connect(a, b);

        var findings = Of(circuit);

        Assert.True(Has(findings, "output-clash"));

        var clash = findings.First(f => f.Rule == "output-clash");

        Assert.Equal(RuleSeverity.Warning, clash.Severity);
        Assert.Contains(first.Name, clash.Where);
        Assert.Contains(second.Name, clash.Where);
    }

    // ---- net labels --------------------------------------------------------

    [Fact]
    public void ALabelNobodyElseUsesIsProbablyATypo()
    {
        var circuit = GoodDivider();
        var stray = circuit.Add(new NetLabel("VCC1"));

        circuit.Connect(stray.Pin, circuit.Components.OfType<Resistor>().First().A);

        var findings = Of(circuit);

        Assert.True(Has(findings, "label-alone"));
        Assert.Contains("VCC1", findings.First(f => f.Rule == "label-alone").Message);
    }

    [Fact]
    public void APairOfLabelsThatMatchIsNotReported()
    {
        var circuit = GoodDivider();
        var resistors = circuit.Components.OfType<Resistor>().ToList();

        var a = circuit.Add(new NetLabel("MID"));
        var b = circuit.Add(new NetLabel("MID"));

        circuit.Connect(a.Pin, resistors[0].B);
        circuit.Connect(b.Pin, resistors[1].A);

        Assert.False(Has(Of(circuit), "label-alone"));
    }

    [Fact]
    public void ABlankLabelIsReportedBecauseItJoinsNothing()
    {
        var circuit = GoodDivider();
        var blank = circuit.Add(new NetLabel(string.Empty));

        circuit.Connect(blank.Pin, circuit.Components.OfType<Resistor>().First().A);

        Assert.True(Has(Of(circuit), "label-blank"));
    }

    [Fact]
    public void TwoDifferentNamesOnOneNetIsAContradiction()
    {
        var circuit = GoodDivider();
        var midpoint = circuit.Components.OfType<Resistor>().First().B;

        // Both on the same point, disagreeing about what it is called. Each also has a partner
        // elsewhere so the "alone" rule does not fire instead.
        circuit.Connect(circuit.Add(new NetLabel("MID")).Pin, midpoint);
        circuit.Connect(circuit.Add(new NetLabel("TAP")).Pin, midpoint);
        circuit.Connect(circuit.Add(new NetLabel("MID")).Pin, circuit.Components.OfType<Resistor>().Last().B);
        circuit.Connect(circuit.Add(new NetLabel("TAP")).Pin, circuit.Components.OfType<Resistor>().Last().B);

        var findings = Of(circuit);

        Assert.True(Has(findings, "label-conflict"));
        Assert.Contains("more than one name", findings.First(f => f.Rule == "label-conflict").Message);
    }

    [Fact]
    public void ErrorsAreListedAheadOfWarnings()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var dangling = circuit.Add(new Resistor(1e3));

        circuit.Connect(supply.Positive, dangling.A);

        var findings = Of(circuit);

        Assert.NotEmpty(findings);

        // Sorted most serious first, so the list reads top-down in the order things matter.
        for (var i = 1; i < findings.Count; i++)
            Assert.True(findings[i - 1].Severity <= findings[i].Severity);
    }
}
