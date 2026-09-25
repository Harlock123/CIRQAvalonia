using Cirq.Components.Digital;
using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Serialization;
using Cirq.Components.Sources;
using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Core.Verification;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class SerializationTests
{
    /// <summary>Saves and reloads a circuit through JSON, asserting the load was clean.</summary>
    private static Circuit RoundTrip(Circuit circuit)
    {
        var result = CircuitSerializer.FromJson(CircuitSerializer.ToJson(circuit));
        Assert.True(result.IsClean, $"Load warnings: {string.Join("; ", result.Warnings)}");
        return result.Circuit;
    }


    /// <summary>
    /// A circuit's written-down requirements travel with it. A requirement that lives in one
    /// person's head is not a requirement, and one that does not survive a save is worse — it
    /// looks like the design has none.
    /// </summary>
    [Fact]
    public void RequirementsTravelWithTheCircuit()
    {
        var circuit = new Circuit();

        circuit.Add(new Resistor(1e3) { Name = "R1" });

        circuit.Specs.Add(new DesignSpec
        {
            Name = "Rail ripple",
            Trace = "Rail",
            Quantity = SpecQuantity.PeakToPeak,
            Comparison = SpecComparison.AtMost,
            Limit = 0.05,
            Unit = "V",
        });

        circuit.Specs.Add(new DesignSpec
        {
            Name = "Output regulation",
            Trace = "Out",
            Quantity = SpecQuantity.Mean,
            Comparison = SpecComparison.Within,
            Limit = 5.0,
            Tolerance = 0.1,
            IsEnabled = false,
        });

        var loaded = RoundTrip(circuit);

        Assert.Equal(2, loaded.Specs.Count);

        var ripple = loaded.Specs[0];

        Assert.Equal("Rail ripple", ripple.Name);
        Assert.Equal("Rail", ripple.Trace);
        Assert.Equal(SpecQuantity.PeakToPeak, ripple.Quantity);
        Assert.Equal(SpecComparison.AtMost, ripple.Comparison);
        Assert.Equal(0.05, ripple.Limit, 1e-12);
        Assert.True(ripple.IsEnabled);

        var regulation = loaded.Specs[1];

        Assert.Equal(SpecComparison.Within, regulation.Comparison);
        Assert.Equal(0.1, regulation.Tolerance, 1e-12);
        Assert.False(regulation.IsEnabled);
    }

    /// <summary>A circuit with no requirements writes no requirements section at all.</summary>
    [Fact]
    public void ACircuitWithoutRequirementsIsUnchangedOnDisk()
    {
        var circuit = new Circuit();

        circuit.Add(new Resistor(1e3) { Name = "R1" });

        Assert.DoesNotContain("\"specs\"", CircuitSerializer.ToJson(circuit), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A file written by a newer version, measuring something this one has never heard of, keeps
    /// the requirement and says so rather than silently measuring the wrong thing.
    /// <para>
    /// The name is deliberately one nobody will ever implement. This test used to forge
    /// <c>SettlingTime</c>, on the reasonable grounds that there was no such quantity — and then a
    /// release added one, and the test failed for the best possible reason: the future it was
    /// pretending to read had arrived. A plausible name is the wrong choice here, because the point
    /// is to stand in for whatever comes next rather than to guess it.
    /// </para>
    /// </summary>
    [Fact]
    public void AnUnknownQuantityIsKeptAndReported()
    {
        var circuit = new Circuit();

        circuit.Add(new Resistor(1e3) { Name = "R1" });
        circuit.Specs.Add(new DesignSpec { Name = "Something later", Trace = "Out" });

        const string fromTheFuture = "AQuantityThisVersionHasNeverHeardOf";

        var json = CircuitSerializer.ToJson(circuit)
            .Replace("\"PeakToPeak\"", $"\"{fromTheFuture}\"", StringComparison.Ordinal);

        var result = CircuitSerializer.FromJson(json);

        var spec = Assert.Single(result.Circuit.Specs);

        Assert.Equal("Something later", spec.Name);
        Assert.False(spec.IsEnabled);
        Assert.Contains(result.Warnings, w => w.Contains(fromTheFuture, StringComparison.Ordinal));
    }

    /// <summary>Every component type the palette can place, instantiated through its own factory.</summary>
    public static TheoryData<string> AllComponentTypes()
    {
        var data = new TheoryData<string>();
        foreach (var type in typeof(Resistor).Assembly.GetTypes()
                     .Where(t => t is { IsAbstract: false, IsClass: true })
                     .Where(t => typeof(CircuitComponent).IsAssignableFrom(t))
                     .OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            data.Add(type.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllComponentTypes))]
    public void EveryComponentTypeSurvivesARoundTrip(string typeName)
    {
        var type = typeof(Resistor).Assembly.GetTypes().First(t => t.Name == typeName);
        var original = ComponentReflection.Instantiate(type);
        original.Name = "X1";
        original.X = 120;
        original.Y = -40;
        original.RotationDegrees = 90;

        var circuit = new Circuit();
        circuit.Components.Add(original);

        var loaded = RoundTrip(circuit);

        var restored = Assert.Single(loaded.Components);
        Assert.Equal(type, restored.GetType());
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal("X1", restored.Name);
        Assert.Equal(120, restored.X);
        Assert.Equal(-40, restored.Y);
        Assert.Equal(90, restored.RotationDegrees);

        // Pins must come back identically, or wires would have nothing to reattach to.
        Assert.Equal(
            original.Terminals.Select(t => t.Id),
            restored.Terminals.Select(t => t.Id));
    }

    [Theory]
    [MemberData(nameof(AllComponentTypes))]
    public void EveryEditableParameterSurvivesARoundTrip(string typeName)
    {
        var type = typeof(Resistor).Assembly.GetTypes().First(t => t.Name == typeName);
        var original = ComponentReflection.Instantiate(type);

        var circuit = new Circuit();
        circuit.Components.Add(original);
        var restored = RoundTrip(circuit).Components.Single();

        foreach (var property in ComponentReflection.EditableProperties(type))
        {
            var before = property.GetValue(original);
            var after = property.GetValue(restored);

            if (before is null)
            {
                Assert.Null(after);
                continue;
            }

            // Device models are resolved back to the very same library instance.
            Assert.Equal(before.ToString(), after?.ToString());
        }
    }

    [Fact]
    public void EditedValuesComeBackExactly()
    {
        var circuit = new Circuit();
        var resistor = circuit.Add(new Resistor(4700));
        var capacitor = circuit.Add(new Capacitor(100e-9) { InitialVoltage = 2.5 });
        var generator = circuit.Add(new FunctionGenerator(Waveform.Triangle, 2500, 7.5)
        {
            DcOffset = 1.25,
            DutyCycle = 0.3,
            PhaseDegrees = 45,
        });

        var loaded = RoundTrip(circuit);

        Assert.Equal(4700, loaded.Components.OfType<Resistor>().Single().Resistance);

        var loadedCap = loaded.Components.OfType<Capacitor>().Single();
        Assert.Equal(100e-9, loadedCap.Capacitance);
        Assert.Equal(2.5, loadedCap.InitialVoltage);

        var loadedGen = loaded.Components.OfType<FunctionGenerator>().Single();
        Assert.Equal(Waveform.Triangle, loadedGen.Shape);
        Assert.Equal(2500, loadedGen.Frequency);
        Assert.Equal(7.5, loadedGen.AmplitudePeakToPeak);
        Assert.Equal(1.25, loadedGen.DcOffset);
        Assert.Equal(0.3, loadedGen.DutyCycle);
        Assert.Equal(45, loadedGen.PhaseDegrees);

        Assert.Equal(generator.Shape, loadedGen.Shape);
    }

    [Fact]
    public void ANullableParameterLeftUnsetStaysUnset()
    {
        var circuit = new Circuit();
        circuit.Add(new Capacitor(1e-6));

        var loaded = RoundTrip(circuit).Components.OfType<Capacitor>().Single();

        Assert.Null(loaded.InitialVoltage);
    }

    [Fact]
    public void DeviceModelsResolveBackToTheirLibraryInstance()
    {
        var circuit = new Circuit();
        circuit.Add(new Diode(DiodeModel.D1N4001));
        circuit.Add(new BipolarTransistor(BjtModel.P2N3906));
        circuit.Add(new Mosfet(MosfetModel.Irf9540));
        circuit.Add(new VoltageRegulator(RegulatorModel.Lm317));
        circuit.Add(new OperationalAmplifier(OpAmpModel.Tl081));
        circuit.Add(new Comparator(ComparatorModel.Tlv3501));

        var loaded = RoundTrip(circuit);

        Assert.Same(DiodeModel.D1N4001, loaded.Components.OfType<Diode>().First().Model);
        Assert.Same(BjtModel.P2N3906, loaded.Components.OfType<BipolarTransistor>().Single().Model);
        Assert.Same(MosfetModel.Irf9540, loaded.Components.OfType<Mosfet>().Single().Model);
        Assert.Same(RegulatorModel.Lm317, loaded.Components.OfType<VoltageRegulator>().Single().Model);
        Assert.Same(OpAmpModel.Tl081, loaded.Components.OfType<OperationalAmplifier>().First().Model);
        Assert.Same(ComparatorModel.Tlv3501, loaded.Components.OfType<Comparator>().Single().Model);
    }

    [Fact]
    public void AGatesInputCountIsRebuiltBecauseItShapesThePins()
    {
        // Input count is a constructor argument, not a property, so it has to be carried
        // separately or a three-input gate comes back with two pins.
        var circuit = new Circuit();
        circuit.Add(new LogicGate(GateFunction.Nor, 3));
        circuit.Add(new LogicGate(GateFunction.Not));

        var loaded = RoundTrip(circuit);
        var gates = loaded.Components.OfType<LogicGate>().ToList();

        var nor = gates.Single(g => g.Function == GateFunction.Nor);
        Assert.Equal(3, nor.InputTerminals.Count);

        var not = gates.Single(g => g.Function == GateFunction.Not);
        Assert.Single(not.InputTerminals);
    }

    [Fact]
    public void WiresReconnectToTheSamePins()
    {
        var circuit = new Circuit();
        var source = circuit.Add(new DcVoltageSource(9.0));
        var resistor = circuit.Add(new Resistor(2200));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(source.Positive, resistor.A);
        circuit.Connect(resistor.B, gnd.Pin);
        circuit.Connect(source.Negative, gnd.Pin);

        var loaded = RoundTrip(circuit);

        Assert.Equal(3, loaded.Wires.Count);
        Assert.All(loaded.Wires, w =>
        {
            Assert.NotNull(w.SourceTerminal);
            Assert.NotNull(w.TargetTerminal);
            // Terminals must be owned by components in the loaded circuit, not orphans.
            Assert.Contains(w.SourceTerminal.Owner, loaded.Components);
            Assert.Contains(w.TargetTerminal.Owner, loaded.Components);
        });

        // The netlist must come out the same shape.
        var before = circuit.BuildNetlist();
        var after = loaded.BuildNetlist();
        Assert.Equal(before.NodeCount, after.NodeCount);
    }

    [Fact]
    public void WireWaypointsArePreserved()
    {
        var circuit = new Circuit();
        var a = circuit.Add(new Resistor());
        var b = circuit.Add(new Resistor());

        var wire = circuit.Connect(a.B, b.A);
        wire.Waypoints.Add(new Point(40, 10));
        wire.Waypoints.Add(new Point(40, 60));

        var loaded = RoundTrip(circuit).Wires.Single();

        Assert.Equal(2, loaded.Waypoints.Count);
        Assert.Equal(new Point(40, 10), loaded.Waypoints[0]);
        Assert.Equal(new Point(40, 60), loaded.Waypoints[1]);
    }

    [Fact]
    public void ProbesComeBackAttachedToTheirTerminal()
    {
        var circuit = new Circuit();
        var resistor = circuit.Add(new Resistor());
        circuit.Probes.Add(new SignalProbe("Output", resistor.B, Color.Cyan)
        {
            Kind = ProbeKind.Current,
            AcCoupled = true,
            IsVisible = false,
        });

        var loaded = RoundTrip(circuit);
        var probe = Assert.Single(loaded.Probes);

        Assert.Equal("Output", probe.Label);
        Assert.Equal(ProbeKind.Current, probe.Kind);
        Assert.True(probe.AcCoupled);
        Assert.False(probe.IsVisible);
        Assert.Equal(Color.Cyan, probe.TraceColor);
        Assert.Equal("b", probe.TargetTerminal!.Id);
        Assert.Same(loaded.Components.Single(), probe.TargetTerminal.Owner);
    }

    [Fact]
    public void TheTitleIsPreserved()
    {
        var circuit = new Circuit { Title = "My amplifier" };
        Assert.Equal("My amplifier", RoundTrip(circuit).Title);
    }

    [Fact]
    public void ALoadedCircuitStillSimulates()
    {
        // The real test of a round trip: the reloaded circuit has to solve to the same answer.
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(10.0));
        var top = circuit.Add(new Resistor(1000));
        var bottom = circuit.Add(new Resistor(3000));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, gnd.Pin);
        circuit.Connect(supply.Negative, gnd.Pin);

        var loaded = RoundTrip(circuit);

        var loadedTop = loaded.Components.OfType<Resistor>().First(r => r.Resistance == 1000);
        var sim = new CircuitSimulator(loaded);
        sim.SolveOperatingPoint();

        // 10 V across 1k + 3k puts 7.5 V on the divider node.
        Assert.Equal(7.5, sim.NodeVoltage(loadedTop.B), 1e-6);
    }

    [Fact]
    public void AFileRoundTripsThroughDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cirq-test-{Guid.NewGuid():N}{CircuitSerializer.FileExtension}");

        try
        {
            var circuit = new Circuit { Title = "Disk test" };
            var resistor = circuit.Add(new Resistor(8200));
            var gnd = circuit.Add(new Ground());
            circuit.Connect(resistor.B, gnd.Pin);

            CircuitSerializer.Save(circuit, path);
            Assert.True(File.Exists(path));

            var result = CircuitSerializer.Load(path);

            Assert.True(result.IsClean);
            Assert.Equal("Disk test", result.Circuit.Title);
            Assert.Equal(8200, result.Circuit.Components.OfType<Resistor>().Single().Resistance);
            Assert.Single(result.Circuit.Wires);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SavingLeavesNoTemporaryFileBehind()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cirq-test-{Guid.NewGuid():N}{CircuitSerializer.FileExtension}");

        try
        {
            CircuitSerializer.Save(new Circuit(), path);
            Assert.False(File.Exists(path + ".tmp"), "The atomic-write temporary file was not cleaned up.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public void TheSavedFormatIsReadableJson()
    {
        var circuit = new Circuit { Title = "Readable" };
        circuit.Add(new Resistor(4700) { Name = "R1" });

        var json = CircuitSerializer.ToJson(circuit);

        Assert.Contains("\"Title\": \"Readable\"", json);
        Assert.Contains("\"Type\": \"Resistor\"", json);
        Assert.Contains("\"Name\": \"R1\"", json);
        Assert.Contains("4700", json);
    }

    // ---- damaged and unfamiliar files ------------------------------------

    [Fact]
    public void MalformedJsonIsReportedRatherThanCrashing()
    {
        var ex = Assert.Throws<CircuitFormatException>(() => CircuitSerializer.FromJson("{ not json"));
        Assert.Contains("valid circuit JSON", ex.Message);
    }

    [Fact]
    public void AFileFromANewerFormatIsRefusedWithAClearMessage()
    {
        var json = $$"""{ "Version": {{CircuitSerializer.CurrentVersion + 1}}, "Title": "Future" }""";

        var ex = Assert.Throws<CircuitFormatException>(() => CircuitSerializer.FromJson(json));
        Assert.Contains("newer version", ex.Message);
    }

    [Fact]
    public void AnUnknownComponentTypeIsSkippedWithAWarning()
    {
        var json = """
        {
          "Version": 1,
          "Title": "Partly unknown",
          "Components": [
            { "Id": "11111111-1111-1111-1111-111111111111", "Type": "Resistor", "Name": "R1",
              "Parameters": { "Resistance": 1000 } },
            { "Id": "22222222-2222-2222-2222-222222222222", "Type": "FluxCapacitor", "Name": "U1",
              "Parameters": {} }
          ]
        }
        """;

        var result = CircuitSerializer.FromJson(json);

        // The known part still loads; only the unknown one is dropped.
        Assert.Single(result.Circuit.Components);
        Assert.False(result.IsClean);
        Assert.Contains(result.Warnings, w => w.Contains("FluxCapacitor"));
    }

    [Fact]
    public void AWireToAMissingComponentIsDroppedWithAWarning()
    {
        var json = """
        {
          "Version": 1,
          "Components": [
            { "Id": "11111111-1111-1111-1111-111111111111", "Type": "Resistor", "Name": "R1",
              "Parameters": {} }
          ],
          "Wires": [
            { "From": { "Component": "11111111-1111-1111-1111-111111111111", "Terminal": "a" },
              "To":   { "Component": "99999999-9999-9999-9999-999999999999", "Terminal": "b" } }
          ]
        }
        """;

        var result = CircuitSerializer.FromJson(json);

        Assert.Empty(result.Circuit.Wires);
        Assert.False(result.IsClean);
    }

    [Fact]
    public void AnUnknownDeviceModelFallsBackToTheDefault()
    {
        var json = """
        {
          "Version": 1,
          "Components": [
            { "Id": "11111111-1111-1111-1111-111111111111", "Type": "Diode", "Name": "D1",
              "Parameters": { "Model": "1N9999-Unobtainium" } }
          ]
        }
        """;

        var result = CircuitSerializer.FromJson(json);
        var diode = result.Circuit.Components.OfType<Diode>().Single();

        Assert.NotNull(diode.Model);
        Assert.False(result.IsClean);
        Assert.Contains(result.Warnings, w => w.Contains("Unobtainium"));
    }

    [Fact]
    public void AnUnknownParameterNameIsIgnored()
    {
        // A file written by a later build may carry parameters this one has never heard of.
        var json = """
        {
          "Version": 1,
          "Components": [
            { "Id": "11111111-1111-1111-1111-111111111111", "Type": "Resistor", "Name": "R1",
              "Parameters": { "Resistance": 2200, "Sparkliness": 11 } }
          ]
        }
        """;

        var result = CircuitSerializer.FromJson(json);

        Assert.Equal(2200, result.Circuit.Components.OfType<Resistor>().Single().Resistance);
        Assert.True(result.IsClean);
    }

    [Fact]
    public void LoadingAMissingFileSaysSo() =>
        Assert.Throws<FileNotFoundException>(() =>
            CircuitSerializer.Load(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.cirq")));
}
