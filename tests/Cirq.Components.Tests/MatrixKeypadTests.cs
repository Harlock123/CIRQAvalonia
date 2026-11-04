using Cirq.Components.Passive;
using Cirq.Components.Serialization;
using Cirq.Components.Sources;
using Cirq.Core.Primitives;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The 4×4 keypad is sixteen contacts between four row wires and four column wires, and nothing
/// else — so a pressed key joins its row to its column, and the classic faults of a matrix follow.
/// </summary>
public class MatrixKeypadTests
{
    /// <summary>One row driven to 5 V, every column pulled down through 10 kΩ; returns the column voltages.</summary>
    private static double[] Columns(MatrixKeypad keypad, int drivenRow)
    {
        var circuit = new Circuit();
        var gnd = circuit.Add(new Ground());
        var rail = circuit.Add(new DcVoltageSource(5.0));
        circuit.Add(keypad);

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, keypad.Rows[drivenRow]);

        var pulls = keypad.Columns.Select(column =>
        {
            var pull = circuit.Add(new Resistor(10e3));
            circuit.Connect(column, pull.A);
            circuit.Connect(pull.B, gnd.Pin);
            return pull;
        }).ToList();

        var sim = new CircuitSimulator(circuit);
        sim.SolveOperatingPoint();

        return [.. keypad.Columns.Select(sim.NodeVoltage)];
    }

    [Fact]
    public void APressedKeyJoinsItsRowToItsColumnAndNothingElse()
    {
        var keypad = new MatrixKeypad();
        keypad.SetPressed("6", true);           // row 2, column 3

        var reading = Columns(keypad, drivenRow: 1);

        Assert.Equal([0.0, 0.0, 5.0, 0.0], reading.Select(v => Math.Round(v, 2)));
        Assert.Equal("6", keypad.ValueLabel);
    }

    [Fact]
    public void DrivingAnotherRowFindsNothingUnderThatKey()
    {
        var keypad = new MatrixKeypad();
        keypad.SetPressed("6", true);

        Assert.All(Columns(keypad, drivenRow: 0), v => Assert.True(v < 1e-3));
    }

    /// <summary>
    /// Ghosting: with 1, 2 and 4 held, driving row 2 reaches column 2 by way of 4, 1 and 2 — so the
    /// scanner sees a 5 that nobody pressed. It is why keyboards that matter have a diode per key.
    /// </summary>
    [Fact]
    public void ThreeKeysOnTheCornersOfARectangleMakeAGhostOnTheFourth()
    {
        var keypad = new MatrixKeypad();
        keypad.SetPressed("1", true);
        keypad.SetPressed("2", true);
        keypad.SetPressed("4", true);

        var reading = Columns(keypad, drivenRow: 1);

        Assert.True(reading[1] > 4.9, $"column 2 read {reading[1]:0.00} V");
        Assert.False(keypad.Key5);
    }

    [Theory]
    [InlineData(-42, -42, "1")]
    [InlineData(-14, -14, "5")]
    [InlineData(42, 42, "D")]
    [InlineData(-42, 42, "*")]
    [InlineData(14, 42, "#")]
    public void DoubleClickingAKeyPressesThatKey(double x, double y, string legend)
    {
        var keypad = new MatrixKeypad();

        Assert.True(keypad.InteractAt(new Point(x, y)));
        Assert.Equal(legend, keypad.ValueLabel);
        Assert.StartsWith("Release", keypad.InteractionHintAt(new Point(x, y)));

        // And again lets it go.
        Assert.True(keypad.InteractAt(new Point(x, y)));
        Assert.Equal("none", keypad.ValueLabel);
    }

    [Fact]
    public void BetweenTheKeysADoubleClickPressesNothing()
    {
        var keypad = new MatrixKeypad();

        Assert.False(keypad.InteractAt(new Point(0, 0)));
        Assert.Null(keypad.InteractionHintAt(new Point(0, 0)));
        Assert.Equal("none", keypad.ValueLabel);
    }

    [Fact]
    public void ThePlainInteractionLetsGoOfEverything()
    {
        var keypad = new MatrixKeypad();
        keypad.SetPressed("A", true);
        keypad.SetPressed("0", true);
        Assert.Equal("A0", keypad.ValueLabel);

        keypad.Interact();
        Assert.Equal("none", keypad.ValueLabel);
    }

    [Fact]
    public void EveryKeyIsWhereItsLegendSays()
    {
        var keypad = new MatrixKeypad();

        for (var r = 0; r < 4; r++)
        {
            for (var c = 0; c < 4; c++)
            {
                Assert.Equal((r, c), MatrixKeypad.Find(MatrixKeypad.Legends[r, c]));
                Assert.Equal((r, c), MatrixKeypad.KeyAt(new Point(MatrixKeypad.KeyCentre(c), MatrixKeypad.KeyCentre(r))));
            }
        }

        Assert.Throws<ArgumentException>(() => keypad.SetPressed("E", true));
        Assert.Equal(["R1", "R2", "R3", "R4", "C1", "C2", "C3", "C4"], keypad.Terminals.Select(t => t.Name));
    }

    [Fact]
    public void WhichKeysAreDownSurvivesTheFile()
    {
        var circuit = new Circuit();
        var keypad = circuit.Add(new MatrixKeypad());
        keypad.SetPressed("#", true);
        keypad.SetPressed("7", true);

        var back = CircuitSerializer.FromJson(CircuitSerializer.ToJson(circuit)).Circuit
            .Components.OfType<MatrixKeypad>().Single();

        Assert.True(back.KeyHash && back.Key7);
        Assert.Equal("7#", back.ValueLabel);
    }
}
