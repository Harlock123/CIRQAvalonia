using System.Text.Json;
using System.Text.Json.Nodes;
using Cirq.Components.Passive;
using Cirq.Components.Serialization;
using Cirq.Components.Sources;
using Cirq.Core.Topology;

namespace Cirq.Components.Tests;

/// <summary>
/// A circuit saved by a <i>newer</i> build still opens in this one.
/// <para>
/// This is the property the whole version policy rests on. Adding a field, a part, a parameter or
/// a whole new kind of record must never need <see cref="CircuitSerializer.CurrentVersion"/> to
/// move, because moving it makes every build already in the world refuse every new file — for a
/// change those builds could simply have ignored. So the additions have to be survivable, and
/// that is checked here rather than assumed.
/// </para>
/// <para>
/// Each test takes a real saved circuit and edits the JSON to look like something a later version
/// wrote, which is the only way to test against a future that does not exist yet.
/// </para>
/// </summary>
public class ForwardCompatibilityTests
{
    /// <summary>A small circuit with a part, a wire, a probe and a requirement on it.</summary>
    private static string Saved()
    {
        var circuit = new Circuit { Title = "Divider" };

        var supply = circuit.Add(new DcVoltageSource(10.0) { Name = "V1" });
        var top = circuit.Add(new Resistor(6.8e3) { Name = "R1" });
        var bottom = circuit.Add(new Resistor(3.3e3) { Name = "R2" });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);

        circuit.Probes.Add(new Cirq.Core.Probing.SignalProbe("Out", top.B, default));
        circuit.Specs.Add(new Cirq.Core.Verification.DesignSpec { Name = "Tap", Trace = "Out" });

        return CircuitSerializer.ToJson(circuit);
    }

    /// <summary>Loads, insisting the load was clean rather than merely survived.</summary>
    private static Circuit LoadsCleanly(string json)
    {
        var result = CircuitSerializer.FromJson(json);

        Assert.True(result.IsClean, "warnings: " + string.Join("; ", result.Warnings));

        return result.Circuit;
    }

    /// <summary>
    /// Edits the saved JSON, insisting the edit actually changed it. An injection that quietly
    /// missed — a record renamed, an array that turned out empty — would leave every test here
    /// passing against a file nothing had been added to, which is the shape of a test that guards
    /// nothing.
    /// </summary>
    private static string Edited(Action<JsonObject> edit)
    {
        var original = Saved();
        var document = JsonNode.Parse(original)!.AsObject();

        edit(document);

        var json = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        Assert.NotEqual(
            JsonNode.Parse(original)!.ToJsonString(),
            JsonNode.Parse(json)!.ToJsonString());

        return json;
    }

    /// <summary>A setting this build has never heard of is ignored, not fatal.</summary>
    [Fact]
    public void AnUnknownTopLevelFieldIsIgnored()
    {
        var json = Edited(d =>
        {
            d["ThermalBudgetWatts"] = 12.5;
            d["Sheets"] = new JsonArray("A", "B");
        });

        var circuit = LoadsCleanly(json);

        Assert.Equal(4, circuit.Components.Count);
        Assert.Equal("Divider", circuit.Title);
    }

    /// <summary>
    /// A parameter added to a part in a later version is ignored, and the parameters this build
    /// does know still come back exactly.
    /// </summary>
    [Fact]
    public void AnUnknownParameterOnAKnownPartIsIgnored()
    {
        var json = Edited(d =>
        {
            var record = d["Components"]!.AsArray()
                .Select(c => c!.AsObject())
                .Single(c => c["Name"]?.GetValue<string>() == "R1");

            record["Parameters"]!.AsObject()["SelfInductance"] = 3.2e-9;
            record["Footprint"] = "0805";
        });

        var circuit = LoadsCleanly(json);

        var resistor = circuit.Components.OfType<Resistor>().Single(r => r.Name == "R1");

        Assert.Equal(6.8e3, resistor.Resistance, 1e-9);
    }

    /// <summary>And one added to a wire, a probe or a requirement.</summary>
    [Fact]
    public void UnknownFieldsOnWiresProbesAndRequirementsAreIgnored()
    {
        var json = Edited(d =>
        {
            d["Wires"]!.AsArray().First()!.AsObject()["Width"] = 2;
            d["Probes"]!.AsArray().First()!.AsObject()["Thickness"] = 3;
            d["Specs"]!.AsArray().Single()!.AsObject()["Owner"] = "someone";
        });

        var circuit = LoadsCleanly(json);

        Assert.NotEmpty(circuit.Wires);
        Assert.Single(circuit.Probes);
        Assert.Single(circuit.Specs);
    }

    /// <summary>
    /// A whole new kind of record — something a later version keeps in the file that this one has
    /// no concept of at all — is ignored rather than fatal.
    /// </summary>
    [Fact]
    public void AnEntireUnknownSectionIsIgnored()
    {
        var json = Edited(d =>
        {
            d["Annotations3D"] = new JsonArray(
                new JsonObject { ["Kind"] = "box", ["Height"] = 4.0 },
                new JsonObject { ["Kind"] = "label", ["Text"] = "hello" });
        });

        var circuit = LoadsCleanly(json);

        Assert.Equal(4, circuit.Components.Count);
    }

    /// <summary>
    /// The version itself does not move for any of that. If this ever fails, the question to ask
    /// is whether the change really altered the <i>meaning</i> of existing data — the only thing
    /// that justifies refusing older builds — or whether it merely added something they could
    /// have ignored.
    /// </summary>
    [Fact]
    public void AddingThingsHasNotMovedTheFormatVersion()
    {
        Assert.Equal(1, CircuitSerializer.CurrentVersion);

        var document = JsonNode.Parse(Saved())!.AsObject();

        Assert.Equal(1, document["Version"]!.GetValue<int>());
    }

    /// <summary>
    /// And the gate still works, because the day the meaning of something does change, an old
    /// build reading the file successfully and being confidently wrong is the worse outcome.
    /// </summary>
    [Fact]
    public void AGenuineFormatChangeIsStillRefused()
    {
        var json = Edited(d => d["Version"] = CircuitSerializer.CurrentVersion + 1);

        var ex = Assert.Throws<CircuitFormatException>(() => CircuitSerializer.FromJson(json));

        Assert.Contains("newer version", ex.Message);
    }
}
