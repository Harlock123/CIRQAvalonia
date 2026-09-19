using Cirq.Components.Digital;
using Cirq.Components.Ics;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>Shared rig for the combinational and sequential 74xx parts.</summary>
public abstract class LogicIcTestBase
{
    protected static SimulationSettings FastSettings => new() { TimeStep = 2e-9, MaxTimeStep = 2e-9 };

    /// <summary>Wires 5 V into an IC's supply pins and returns the rail and ground terminals.</summary>
    protected static (Terminal Rail, Terminal Ground) Power(Circuit circuit, DigitalIc ic)
    {
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());
        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(ic.Vcc, supply.Positive);
        circuit.Connect(ic.Gnd, gnd.Pin);
        return (supply.Positive, gnd.Pin);
    }

    protected static bool IsHigh(CircuitSimulator sim, Terminal t) => sim.NodeVoltage(t) > LogicLevels.Ttl.Vih;

    protected static bool IsLow(CircuitSimulator sim, Terminal t) => sim.NodeVoltage(t) < LogicLevels.Ttl.Vil;
}

public class GateFillInTests : LogicIcTestBase
{
    /// <summary>Drives one gate of a package and returns whether its output went high.</summary>
    private static bool EvaluateGate(DigitalIc ic, IReadOnlyList<Terminal> inputs, Terminal output, bool[] values)
    {
        var circuit = new Circuit();
        circuit.Add(ic);
        Power(circuit, ic);

        for (var i = 0; i < inputs.Count; i++)
            circuit.Connect(circuit.Add(new LogicToggle(values[i])).Out, inputs[i]);

        var sim = new CircuitSimulator(circuit, FastSettings);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(300e-9);

        return IsHigh(sim, output);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void Ic7402ImplementsNor(bool a, bool b, bool expected)
    {
        var ic = new Ic7402();
        var (pinA, pinB, pinY) = ic.Gate(0);
        Assert.Equal(expected, EvaluateGate(ic, [pinA, pinB], pinY, [a, b]));
    }

    [Fact]
    public void Ic7402PutsItsOutputsOnDifferentPinsFromThe7400()
    {
        // The whole reason the 7402 needs its own pin map: pin 1 is an output here, an input there.
        var nor = new Ic7402();
        var nand = new Ic7400();

        Assert.Equal("1Y", nor.Terminals[0].Name);
        Assert.Equal("1A", nand.Terminals[0].Name);
        Assert.Equal(TerminalType.Output, nor.Terminals[0].Type);
        Assert.Equal(TerminalType.Input, nand.Terminals[0].Type);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    public void Ic7486ImplementsExclusiveOr(bool a, bool b, bool expected)
    {
        var ic = new Ic7486();
        var (pinA, pinB, pinY) = ic.Gate(0);
        Assert.Equal(expected, EvaluateGate(ic, [pinA, pinB], pinY, [a, b]));
    }

    [Theory]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, true, true)]
    [InlineData(false, false, false, true)]
    public void Ic7410ImplementsThreeInputNand(bool a, bool b, bool c, bool expected)
    {
        var ic = new Ic7410();
        var (pinA, pinB, pinC, pinY) = ic.Gate(0);
        Assert.Equal(expected, EvaluateGate(ic, [pinA, pinB, pinC], pinY, [a, b, c]));
    }

    [Theory]
    [InlineData(true, true, true, true, false)]
    [InlineData(true, true, true, false, true)]
    [InlineData(false, false, false, false, true)]
    public void Ic7420ImplementsFourInputNand(bool a, bool b, bool c, bool d, bool expected)
    {
        var ic = new Ic7420();
        var (pinA, pinB, pinC, pinD, pinY) = ic.Gate(0);
        Assert.Equal(expected, EvaluateGate(ic, [pinA, pinB, pinC, pinD], pinY, [a, b, c, d]));
    }

    [Fact]
    public void Ic7420LeavesItsUnusedPinsUnconnected()
    {
        var ic = new Ic7420();
        var unconnected = ic.Terminals.Where(t => t.Name == "NC").ToList();

        Assert.Equal(2, unconnected.Count);
        Assert.All(unconnected, t => Assert.Equal(TerminalType.Passive, t.Type));
    }

    [Fact]
    public void EveryGateInAPackageWorksIndependently()
    {
        // All four XOR gates driven with different input pairs at once.
        var circuit = new Circuit();
        var ic = circuit.Add(new Ic7486());
        Power(circuit, ic);

        bool[][] inputs = [[false, false], [true, false], [false, true], [true, true]];
        for (var g = 0; g < 4; g++)
        {
            var (a, b, _) = ic.Gate(g);
            circuit.Connect(circuit.Add(new LogicToggle(inputs[g][0])).Out, a);
            circuit.Connect(circuit.Add(new LogicToggle(inputs[g][1])).Out, b);
        }

        var sim = new CircuitSimulator(circuit, FastSettings);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(300e-9);

        Assert.False(IsHigh(sim, ic.Gate(0).Y));
        Assert.True(IsHigh(sim, ic.Gate(1).Y));
        Assert.True(IsHigh(sim, ic.Gate(2).Y));
        Assert.False(IsHigh(sim, ic.Gate(3).Y));
    }
}

public class Ic74138Tests : LogicIcTestBase
{
    private static (CircuitSimulator Sim, Ic74138 Ic) Decoder(
        bool a, bool b, bool c, bool enabled = true)
    {
        var circuit = new Circuit();
        var ic = circuit.Add(new Ic74138());
        var (rail, ground) = Power(circuit, ic);

        circuit.Connect(circuit.Add(new LogicToggle(a)).Out, ic.SelectA);
        circuit.Connect(circuit.Add(new LogicToggle(b)).Out, ic.SelectB);
        circuit.Connect(circuit.Add(new LogicToggle(c)).Out, ic.SelectC);

        // E1 and E2 are active low, E3 active high.
        circuit.Connect(ic.Enable1, enabled ? ground : rail);
        circuit.Connect(ic.Enable2, ground);
        circuit.Connect(ic.Enable3, rail);

        var sim = new CircuitSimulator(circuit, FastSettings);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(300e-9);
        return (sim, ic);
    }

    [Theory]
    [InlineData(false, false, false, 0)]
    [InlineData(true, false, false, 1)]
    [InlineData(false, true, false, 2)]
    [InlineData(true, true, false, 3)]
    [InlineData(false, false, true, 4)]
    [InlineData(true, false, true, 5)]
    [InlineData(false, true, true, 6)]
    [InlineData(true, true, true, 7)]
    public void ExactlyOneOutputGoesLowForEachAddress(bool a, bool b, bool c, int expected)
    {
        var (sim, ic) = Decoder(a, b, c);

        Assert.Equal(expected, ic.SelectedOutput);
        for (var i = 0; i < 8; i++)
        {
            if (i == expected) Assert.True(IsLow(sim, ic.Outputs[i]), $"Y{i} should be low.");
            else Assert.True(IsHigh(sim, ic.Outputs[i]), $"Y{i} should be high.");
        }
    }

    [Fact]
    public void DisablingThePartReleasesEveryOutputHigh()
    {
        var (sim, ic) = Decoder(true, false, true, enabled: false);

        Assert.Equal(-1, ic.SelectedOutput);
        for (var i = 0; i < 8; i++)
            Assert.True(IsHigh(sim, ic.Outputs[i]), $"Y{i} should be inactive.");
    }
}

public class Ic74151Tests : LogicIcTestBase
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public void TheAddressedInputAppearsOnTheOutput(int address)
    {
        var circuit = new Circuit();
        var ic = circuit.Add(new Ic74151());
        var (rail, ground) = Power(circuit, ic);

        // Give each input a distinct pattern so a wrong address is obvious.
        for (var i = 0; i < 8; i++)
            circuit.Connect(ic.Data[i], i == address ? rail : ground);

        circuit.Connect(circuit.Add(new LogicToggle((address & 1) != 0)).Out, ic.SelectA);
        circuit.Connect(circuit.Add(new LogicToggle((address & 2) != 0)).Out, ic.SelectB);
        circuit.Connect(circuit.Add(new LogicToggle((address & 4) != 0)).Out, ic.SelectC);
        circuit.Connect(ic.Strobe, ground);

        var sim = new CircuitSimulator(circuit, FastSettings);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(300e-9);

        Assert.Equal(address, ic.SelectedInput);
        Assert.True(IsHigh(sim, ic.Output), "Y should follow the addressed input.");
        Assert.True(IsLow(sim, ic.InvertedOutput), "W is the complement of Y.");
    }

    [Fact]
    public void StrobingThePartOffForcesTheOutputsInactive()
    {
        var circuit = new Circuit();
        var ic = circuit.Add(new Ic74151());
        var (rail, ground) = Power(circuit, ic);

        foreach (var d in ic.Data) circuit.Connect(d, rail);
        circuit.Connect(ic.SelectA, ground);
        circuit.Connect(ic.SelectB, ground);
        circuit.Connect(ic.SelectC, ground);
        circuit.Connect(ic.Strobe, rail);   // Strobe is active low, so this disables the part.

        var sim = new CircuitSimulator(circuit, FastSettings);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(300e-9);

        Assert.Equal(-1, ic.SelectedInput);
        Assert.True(IsLow(sim, ic.Output));
        Assert.True(IsHigh(sim, ic.InvertedOutput));
    }
}

public class Ic7476Tests : LogicIcTestBase
{
    private static (CircuitSimulator Sim, Ic7476 Ic, ClockSource Clock, LogicToggle J, LogicToggle K)
        JkFlipFlop(bool j, bool k, double clockHz = 1e6)
    {
        var circuit = new Circuit();
        var ic = circuit.Add(new Ic7476());
        var (rail, _) = Power(circuit, ic);
        var (clock, preset, clear, pinJ, pinK, _, _) = ic.Section(0);

        var clockSource = circuit.Add(new ClockSource(clockHz));
        var toggleJ = circuit.Add(new LogicToggle(j));
        var toggleK = circuit.Add(new LogicToggle(k));

        circuit.Connect(clockSource.Out, clock);
        circuit.Connect(toggleJ.Out, pinJ);
        circuit.Connect(toggleK.Out, pinK);
        circuit.Connect(preset, rail);
        circuit.Connect(clear, rail);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 5e-9, MaxTimeStep = 5e-9 });
        return (sim, ic, clockSource, toggleJ, toggleK);
    }

    [Fact]
    public void SetAndResetFollowTheJkTable()
    {
        var (sim, ic, _, j, k) = JkFlipFlop(j: true, k: false);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(2e-6);

        // J high, K low: set.
        Assert.Equal(LogicState.High, ic.QState(0));

        j.State = false;
        k.State = true;
        sim.Run(3e-6);

        // J low, K high: reset.
        Assert.Equal(LogicState.Low, ic.QState(0));
    }

    [Fact]
    public void HoldingBothInputsLowLeavesTheStateAlone()
    {
        var (sim, ic, _, j, k) = JkFlipFlop(j: true, k: false);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(2e-6);
        Assert.Equal(LogicState.High, ic.QState(0));

        j.State = false;
        k.State = false;
        sim.Run(5e-6);

        Assert.Equal(LogicState.High, ic.QState(0));
    }

    [Fact]
    public void BothInputsHighDividesTheClockByTwo()
    {
        var (sim, ic, _, _, _) = JkFlipFlop(j: true, k: true, clockHz: 1e6);
        sim.Reset();
        sim.SolveOperatingPoint();

        var (_, _, _, _, _, q, _) = ic.Section(0);
        var edges = new List<double>();
        var wasHigh = IsHigh(sim, q);

        while (sim.Time < 20e-6)
        {
            sim.Step();
            var isHigh = IsHigh(sim, q);
            if (isHigh && !wasHigh) edges.Add(sim.Time);
            wasHigh = isHigh;
        }

        Assert.True(edges.Count >= 5, $"Expected a divided clock, saw {edges.Count} edges.");
        var period = (edges[^1] - edges[0]) / (edges.Count - 1);
        Assert.Equal(2e-6, period, 2e-6 * 0.05);
    }

    [Fact]
    public void AsynchronousClearOverridesTheClock()
    {
        var circuit = new Circuit();
        var ic = circuit.Add(new Ic7476());
        var (rail, _) = Power(circuit, ic);
        var (clock, preset, clear, j, k, q, _) = ic.Section(0);

        var clockSource = circuit.Add(new ClockSource(1e6));
        var clearToggle = circuit.Add(new LogicToggle(true));

        circuit.Connect(clockSource.Out, clock);
        circuit.Connect(j, rail);
        circuit.Connect(k, rail);
        circuit.Connect(preset, rail);
        circuit.Connect(clearToggle.Out, clear);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 5e-9, MaxTimeStep = 5e-9 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(5e-6);

        clearToggle.State = false;
        sim.Run(500e-9);

        Assert.Equal(LogicState.Low, ic.QState(0));
        Assert.True(IsLow(sim, q));
    }

    [Fact]
    public void BothSectionsAreIndependent()
    {
        var circuit = new Circuit();
        var ic = circuit.Add(new Ic7476());
        var (rail, ground) = Power(circuit, ic);

        var clockSource = circuit.Add(new ClockSource(1e6));

        // Section 0 set, section 1 reset.
        var s0 = ic.Section(0);
        var s1 = ic.Section(1);

        circuit.Connect(clockSource.Out, s0.Clock);
        circuit.Connect(clockSource.Out, s1.Clock);
        circuit.Connect(s0.J, rail);
        circuit.Connect(s0.K, ground);
        circuit.Connect(s1.J, ground);
        circuit.Connect(s1.K, rail);
        foreach (var pin in new[] { s0.Preset, s0.Clear, s1.Preset, s1.Clear })
            circuit.Connect(pin, rail);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 5e-9, MaxTimeStep = 5e-9 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(3e-6);

        Assert.Equal(LogicState.High, ic.QState(0));
        Assert.Equal(LogicState.Low, ic.QState(1));
    }
}

public class ShiftRegisterTests : LogicIcTestBase
{
    [Fact]
    public void Ic74164ShiftsSerialDataAcrossItsOutputs()
    {
        var circuit = new Circuit();
        var ic = circuit.Add(new Ic74164());
        var (rail, _) = Power(circuit, ic);

        var clock = circuit.Add(new ClockSource(1e6));
        var data = circuit.Add(new LogicToggle(true));

        circuit.Connect(clock.Out, ic.Clock);
        circuit.Connect(data.Out, ic.SerialA);
        circuit.Connect(ic.SerialB, rail);      // Second serial input tied high.
        circuit.Connect(ic.Clear, rail);        // Clear released.

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 5e-9, MaxTimeStep = 5e-9 });
        sim.Reset();
        sim.SolveOperatingPoint();

        // Clock in a single one, then hold the input low and watch it march along.
        sim.Run(1.2e-6);
        data.State = false;
        Assert.Equal(1, ic.Value & 1);

        sim.Run(1e-6);
        Assert.Equal(0b10, ic.Value & 0b11);

        sim.Run(1e-6);
        Assert.Equal(0b100, ic.Value & 0b111);
    }

    [Fact]
    public void Ic74164ClearIsAsynchronous()
    {
        var circuit = new Circuit();
        var ic = circuit.Add(new Ic74164());
        var (rail, _) = Power(circuit, ic);

        var clock = circuit.Add(new ClockSource(1e6));
        var clear = circuit.Add(new LogicToggle(true));

        circuit.Connect(clock.Out, ic.Clock);
        circuit.Connect(ic.SerialA, rail);
        circuit.Connect(ic.SerialB, rail);
        circuit.Connect(clear.Out, ic.Clear);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 5e-9, MaxTimeStep = 5e-9 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(5e-6);
        Assert.True(ic.Value > 0, "The register should have filled with ones.");

        clear.State = false;
        sim.Run(300e-9);

        Assert.Equal(0, ic.Value);
    }

    [Fact]
    public void Ic74165LoadsInParallelThenShiftsOutSerially()
    {
        var circuit = new Circuit();
        var ic = circuit.Add(new Ic74165());
        var (rail, ground) = Power(circuit, ic);

        var clock = circuit.Add(new ClockSource(1e6));
        var shiftLoad = circuit.Add(new LogicToggle(false));   // Low: load.

        circuit.Connect(clock.Out, ic.Clock);
        circuit.Connect(shiftLoad.Out, ic.ShiftLoad);
        circuit.Connect(ic.ClockInhibit, ground);
        circuit.Connect(ic.SerialInput, ground);

        // Load 0b10000000: only H set, so QH is high immediately.
        for (var i = 0; i < 8; i++)
            circuit.Connect(ic.ParallelInputs[i], i == 7 ? rail : ground);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 5e-9, MaxTimeStep = 5e-9 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(500e-9);

        Assert.Equal(0b1000_0000, ic.Value);
        Assert.True(IsHigh(sim, ic.Output), "QH should present the loaded bit.");

        // Release the load and shift: the one moves out and zeros follow.
        shiftLoad.State = true;
        sim.Run(1.5e-6);

        Assert.True(IsLow(sim, ic.Output), "After one shift the high bit should have moved on.");
    }

    [Fact]
    public void Ic74165ClockInhibitBlocksShifting()
    {
        var circuit = new Circuit();
        var ic = circuit.Add(new Ic74165());
        var (rail, ground) = Power(circuit, ic);

        var clock = circuit.Add(new ClockSource(1e6));
        var inhibit = circuit.Add(new LogicToggle(true));   // High: clock blocked.

        circuit.Connect(clock.Out, ic.Clock);
        circuit.Connect(ic.ShiftLoad, rail);                // Not loading.
        circuit.Connect(inhibit.Out, ic.ClockInhibit);
        circuit.Connect(ic.SerialInput, rail);
        foreach (var pin in ic.ParallelInputs) circuit.Connect(pin, ground);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 5e-9, MaxTimeStep = 5e-9 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(5e-6);

        // Nothing shifted in despite many clock edges.
        Assert.Equal(0, ic.Value);

        inhibit.State = false;
        sim.Run(2e-6);

        Assert.True(ic.Value > 0, "Releasing the inhibit should let data shift in.");
    }
}

public class QuadComparatorTests : LogicIcTestBase
{
    [Fact]
    public void EachChannelComparesIndependently()
    {
        var circuit = new Circuit();
        var ic = circuit.Add(new QuadComparator());
        var (rail, ground) = Power(circuit, ic);

        var reference = circuit.Add(new DcVoltageSource(2.5));
        circuit.Connect(reference.Negative, ground);

        // Two channels above the reference, two below.
        double[] inputs = [4.0, 1.0, 3.5, 0.5];
        var pullUps = new Resistor[4];

        for (var i = 0; i < 4; i++)
        {
            var (plus, minus, output) = ic.Channel(i);
            var source = circuit.Add(new DcVoltageSource(inputs[i]));
            circuit.Connect(source.Negative, ground);
            circuit.Connect(source.Positive, plus);
            circuit.Connect(minus, reference.Positive);

            pullUps[i] = circuit.Add(new Resistor(10e3));
            circuit.Connect(pullUps[i].A, rail);
            circuit.Connect(pullUps[i].B, output);
        }

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 100e-9, MaxTimeStep = 100e-9 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(20e-6);

        for (var i = 0; i < 4; i++)
        {
            var above = inputs[i] > 2.5;
            var output = ic.Channel(i).Output;

            Assert.Equal(above, ic.IsChannelHigh(i));
            if (above) Assert.True(IsHigh(sim, output), $"Channel {i} should have released.");
            else Assert.True(IsLow(sim, output), $"Channel {i} should be pulling low.");
        }
    }

    [Fact]
    public void TiedOutputsFormAWiredAnd()
    {
        // The point of open-collector outputs: tie them together and any one pulling low wins.
        var circuit = new Circuit();
        var ic = circuit.Add(new QuadComparator());
        var (rail, ground) = Power(circuit, ic);

        var reference = circuit.Add(new DcVoltageSource(2.5));
        circuit.Connect(reference.Negative, ground);

        var pullUp = circuit.Add(new Resistor(4.7e3));
        circuit.Connect(pullUp.A, rail);
        circuit.Connect(pullUp.B, ic.Channel(0).Output);

        // All four outputs onto one node.
        for (var i = 1; i < 4; i++)
            circuit.Connect(ic.Channel(i).Output, ic.Channel(0).Output);

        var drives = new DcVoltageSource[4];
        for (var i = 0; i < 4; i++)
        {
            var (plus, minus, _) = ic.Channel(i);
            drives[i] = circuit.Add(new DcVoltageSource(4.0));
            circuit.Connect(drives[i].Negative, ground);
            circuit.Connect(drives[i].Positive, plus);
            circuit.Connect(minus, reference.Positive);
        }

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 100e-9, MaxTimeStep = 100e-9 });
        var common = ic.Channel(0).Output;

        // Everything above the reference: all released, so the pull-up wins.
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(20e-6);
        Assert.True(IsHigh(sim, common), "With every channel released the node should pull up.");

        // Drop a single channel below the reference: it alone drags the shared node low.
        drives[2].Voltage = 1.0;
        sim.Run(20e-6);
        Assert.True(IsLow(sim, common), "One channel pulling low should take the whole node low.");
    }

    [Fact]
    public void AnUnpoweredPackageReleasesEveryChannel()
    {
        var circuit = new Circuit();
        var ic = circuit.Add(new QuadComparator());
        var gnd = circuit.Add(new Ground());
        circuit.Connect(ic.Gnd, gnd.Pin);   // GND wired, Vcc left floating.

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 100e-9, MaxTimeStep = 100e-9 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(20e-6);

        for (var i = 0; i < 4; i++)
            Assert.Equal(LogicState.HighImpedance, ic.GetOutputState(i));
    }
}
