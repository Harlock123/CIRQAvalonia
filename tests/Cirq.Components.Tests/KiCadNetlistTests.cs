using Cirq.Components.Eda;
using Cirq.Components.Ics;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;

namespace Cirq.Components.Tests;

/// <summary>
/// The netlist a board is laid out from.
/// <para>
/// What matters about this file is that another program reads it, so the checks are about its
/// shape — the fields KiCad looks for, quoted the way its parser expects — and about the
/// correspondence between what is in it and what is on the drawing. A board wired from a netlist
/// that lost a connection is a board with a wire missing, and nothing about it looks wrong until it
/// is built.
/// </para>
/// </summary>
public class KiCadNetlistTests
{
    /// <summary>A divider with a decoupling capacitor, a named rail, and a footprint on one part.</summary>
    private static Circuit Board()
    {
        var circuit = new Circuit { Title = "Divider" };

        var supply = circuit.Add(new DcVoltageSource(5.0) { Name = "V1" });
        var top = circuit.Add(new Resistor(10e3)
        {
            Name = "R1",
            Footprint = "Resistor_SMD:R_0805_2012Metric",
        });

        var bottom = circuit.Add(new Resistor(10e3) { Name = "R2" });
        var cap = circuit.Add(new Capacitor(100e-9) { Name = "C1" });
        var ground = circuit.Add(new Ground { Name = "GND1" });
        var label = circuit.Add(new NetLabel("VCC") { Name = "L1" });

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.A, label.Pin);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);
        circuit.Connect(cap.A, top.A);
        circuit.Connect(cap.B, ground.Pin);

        return circuit;
    }

    [Fact]
    public void ThePartsAreThereWithTheirValues()
    {
        var result = KiCadNetlist.Write(Board());

        Assert.Equal(["C1", "R1", "R2", "V1"], result.Written);

        Assert.Contains("(comp (ref \"R1\")", result.Netlist);
        Assert.Contains("(value \"10kΩ\")", result.Netlist);
        Assert.Contains("(value \"100nF\")", result.Netlist);
    }

    /// <summary>
    /// A ground symbol is notation for a net rather than a thing to solder, and so are net labels
    /// and annotations. Putting them in the netlist would ask somebody to find a footprint for a
    /// triangle.
    /// </summary>
    [Fact]
    public void NotationIsNotAPart()
    {
        var result = KiCadNetlist.Write(Board());

        Assert.DoesNotContain("GND1", result.Written);
        Assert.DoesNotContain("L1", result.Written);
        Assert.DoesNotContain("(ref \"GND1\")", result.Netlist);
    }

    [Fact]
    public void AFootprintIsWrittenAndAMissingOneIsReported()
    {
        var result = KiCadNetlist.Write(Board());

        Assert.Contains("(footprint \"Resistor_SMD:R_0805_2012Metric\")", result.Netlist);

        // The three that have none are named, so it is known before the import rather than during.
        Assert.Equal(["C1", "R2", "V1"], result.WithoutFootprints);
    }

    [Fact]
    public void EveryNetCarriesThePinsOnIt()
    {
        var result = KiCadNetlist.Write(Board());

        // The rail is named by the label on it; ground is called what every board library calls it.
        Assert.Contains("(name \"VCC\")", result.Netlist);
        Assert.Contains("(name \"GND\")", result.Netlist);

        // R1 pin 1 and C1 pin 1 are both on the rail, which is the connection the board needs.
        var rail = Section(result.Netlist, "\"VCC\"");

        Assert.Contains("(ref \"R1\") (pin \"1\")", rail);
        Assert.Contains("(ref \"C1\") (pin \"1\")", rail);
        Assert.Contains("(ref \"V1\")", rail);
    }

    /// <summary>
    /// A part built from a package numbers its pins the way its datasheet does, and those are the
    /// numbers a board library expects. A 555's discharge pin is pin 7 and nothing else.
    /// </summary>
    [Fact]
    public void APackagedPartKeepsItsDatasheetPinNumbers()
    {
        var circuit = new Circuit();

        var timer = circuit.Add(new Ne555 { Name = "U1" });
        var resistor = circuit.Add(new Resistor(10e3) { Name = "R1" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(timer.Discharge, resistor.A);
        circuit.Connect(resistor.B, ground.Pin);
        circuit.Connect(timer.Gnd, ground.Pin);

        var result = KiCadNetlist.Write(circuit);

        Assert.Contains("(ref \"U1\") (pin \"7\")", result.Netlist);
        Assert.Contains("(pinfunction \"DISCH\")", result.Netlist);
    }

    /// <summary>
    /// A pin joined to nothing is not a net, and neither is a ground symbol on its own: after the
    /// notation is taken out, a net needs two pins to be a connection. Writing the others would put
    /// nets in the file that a board cannot act on.
    /// </summary>
    [Fact]
    public void ANetWithOnePinOnItIsNotAConnection()
    {
        var circuit = new Circuit();

        var first = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var second = circuit.Add(new Resistor(1e3) { Name = "R2" });
        var ground = circuit.Add(new Ground());

        // One real connection, one pin joined to nothing, and a ground with a single part on it.
        circuit.Connect(first.B, second.A);
        circuit.Connect(second.B, ground.Pin);

        var result = KiCadNetlist.Write(circuit);

        Assert.Equal(2, result.Written.Count);
        Assert.Equal(1, Count(result.Netlist, "(net (code"));

        var only = Section(result.Netlist, "(net (code");

        Assert.Contains("(ref \"R1\") (pin \"2\")", only);
        Assert.Contains("(ref \"R2\") (pin \"1\")", only);
    }

    /// <summary>
    /// The file is s-expressions, so a quote inside a value would end the string early and take the
    /// rest of the file with it — and an inch mark is an ordinary thing to type into a name.
    /// </summary>
    [Fact]
    public void AQuoteInAValueDoesNotEndTheFile()
    {
        var circuit = new Circuit { Title = "A \"quoted\" title" };

        var part = circuit.Add(new Resistor(1e3) { Name = "R1", Footprint = "Bracket 1\" long" });
        var other = circuit.Add(new Resistor(1e3) { Name = "R2" });

        circuit.Connect(part.A, other.A);

        var result = KiCadNetlist.Write(circuit);

        Assert.Contains("\\\"quoted\\\"", result.Netlist);
        Assert.Contains("Bracket 1\\\" long", result.Netlist);

        // Still balanced, which is the thing a broken string breaks.
        Assert.Equal(Count(result.Netlist, "("), Count(result.Netlist, ")"));
    }

    [Fact]
    public void TheFileHasTheShapeKiCadReads()
    {
        var text = KiCadNetlist.Write(Board(), "divider.net").Netlist;

        Assert.StartsWith("(export (version \"E\")", text);
        Assert.Contains("(design", text);
        Assert.Contains("(source \"divider.net\")", text);
        Assert.Contains("(tool \"CirqAvalonia\")", text);
        Assert.Contains("(components", text);
        Assert.Contains("(nets", text);
        Assert.EndsWith(")\n", text.Replace("\r\n", "\n", StringComparison.Ordinal));

        Assert.Equal(Count(text, "("), Count(text, ")"));
    }

    [Fact]
    public void AnEmptyCircuitProducesNothingToBuild()
    {
        var result = KiCadNetlist.Write(new Circuit());

        Assert.True(result.IsEmpty);
    }

    private static int Count(string text, string needle)
    {
        var count = 0;

        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>The lines of the net whose name contains this, for asserting about one net.</summary>
    private static string Section(string netlist, string name)
    {
        var lines = netlist.Split('\n');
        var start = Array.FindIndex(lines, l => l.Contains(name, StringComparison.Ordinal));

        Assert.True(start >= 0, $"no net called {name}");

        var end = Array.FindIndex(lines, start + 1, l => l.TrimEnd() == "    )");

        return string.Join('\n', lines[start..(end < 0 ? lines.Length : end)]);
    }
}
