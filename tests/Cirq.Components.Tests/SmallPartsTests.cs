using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class DipSwitchTests
{
    [Fact]
    public void EachSectionIsIndependent()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var dip = circuit.Add(new DipSwitch { Position1 = true, Position3 = true });
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);

        var loads = new Resistor[8];
        for (var i = 0; i < 8; i++)
        {
            var (a, b) = dip.Section(i);
            loads[i] = circuit.Add(new Resistor(1e3));

            circuit.Connect(rail.Positive, a);
            circuit.Connect(b, loads[i].A);
            circuit.Connect(loads[i].B, gnd.Pin);
        }

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        for (var i = 0; i < 8; i++)
        {
            var expected = i is 0 or 2;
            var conducting = sim.NodeVoltage(loads[i].A) > 2.5;

            Assert.Equal(expected, conducting);
        }
    }

    /// <summary>Reading it as a number is what firmware does with one.</summary>
    [Fact]
    public void ItReadsAsABinaryNumberWithSectionOneLeastSignificant()
    {
        var dip = new DipSwitch { Position1 = true, Position2 = true, Position5 = true };

        Assert.Equal(0b00010011, dip.Value);
        Assert.Equal("11001000", dip.ValueLabel);
    }

    /// <summary>There is no common rail: the sections share nothing but the package.</summary>
    [Fact]
    public void TheSectionsShareNoPins()
    {
        var dip = new DipSwitch();

        var pins = Enumerable.Range(0, 8)
            .SelectMany(i => new[] { dip.Section(i).A, dip.Section(i).B })
            .ToList();

        Assert.Equal(16, pins.Count);
        Assert.Equal(16, pins.Distinct().Count());
    }
}

public class OscillatorModuleTests
{
    private static (CircuitSimulator Sim, OscillatorModule Osc, Resistor Load) Rig(
        double frequency, bool powered = true, bool? enable = null)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(powered ? 5.0 : 0.0));
        var osc = circuit.Add(new OscillatorModule(frequency));
        var load = circuit.Add(new Resistor(10e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, osc.Vcc);
        circuit.Connect(osc.Gnd, gnd.Pin);
        circuit.Connect(osc.Output, load.A);
        circuit.Connect(load.B, gnd.Pin);

        if (enable is { } level)
        {
            var drive = circuit.Add(new DcVoltageSource(level ? 5.0 : 0.0));
            circuit.Connect(drive.Negative, gnd.Pin);
            circuit.Connect(drive.Positive, osc.Enable);
        }

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, osc, load);
    }

    /// <summary>
    /// It puts out its rated frequency. The edges are registered as breakpoints, so the solver
    /// lands on them rather than wherever the step happens to fall — which is the only way a
    /// megahertz part means anything at these step sizes.
    /// </summary>
    [Theory]
    [InlineData(100e3)]
    [InlineData(1e6)]
    public void ItRunsAtItsRatedFrequency(double frequency)
    {
        var (sim, _, load) = Rig(frequency);

        var window = 200.0 / frequency;
        var edges = 0;
        var last = sim.NodeVoltage(load.A) > 2.5;
        var steps = 20000;

        for (var i = 0; i < steps; i++)
        {
            sim.Run(window / steps);
            var now = sim.NodeVoltage(load.A) > 2.5;
            if (now && !last) edges++;
            last = now;
        }

        Assert.InRange(edges, 194, 206);
    }

    /// <summary>
    /// The enable pin is pulled up inside the can, so leaving it unwired means running. A part
    /// that stopped when its enable floated would be a nuisance and is not what the datasheet says.
    /// </summary>
    [Fact]
    public void LeavingTheEnablePinFloatingLeavesItRunning()
    {
        var (sim, osc, _) = Rig(100e3);
        sim.Run(50e-6);

        Assert.True(osc.IsRunning);
    }

    [Fact]
    public void PullingEnableLowStopsIt()
    {
        var (sim, osc, load) = Rig(100e3, enable: false);
        sim.Run(50e-6);

        Assert.False(osc.IsRunning);

        // Released rather than driven low, as a real can does.
        var settled = sim.NodeVoltage(load.A);
        sim.Run(50e-6);
        Assert.Equal(settled, sim.NodeVoltage(load.A), 1e-6);
    }

    [Fact]
    public void WithoutASupplyItDoesNothing()
    {
        var (sim, osc, _) = Rig(100e3, powered: false);
        sim.Run(50e-6);

        Assert.False(osc.IsRunning);
    }
}

public class SolarCellTests
{
    private static (CircuitSimulator Sim, SolarCell Cell, Resistor Load) Rig(
        double loadOhms, double illumination = 1.0)
    {
        var circuit = new Circuit();
        var cell = circuit.Add(new SolarCell { Illumination = illumination });
        var load = circuit.Add(new Resistor(loadOhms));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(cell.B, gnd.Pin);
        circuit.Connect(cell.A, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, cell, load);
    }

    /// <summary>Six cells at about six tenths of a volt each, with nothing drawing from them.</summary>
    [Fact]
    public void OpenCircuitItSitsNearSixTenthsOfAVoltPerCell()
    {
        var (sim, cell, load) = Rig(1e7);

        Assert.Equal(6 * 0.6, sim.NodeVoltage(load.A), 0.35);
        Assert.True(cell.IsLit);
    }

    /// <summary>Shorted, it gives the current the light is making and no more.</summary>
    [Fact]
    public void ShortedItGivesItsShortCircuitCurrent()
    {
        var (_, cell, _) = Rig(0.01);

        Assert.Equal(cell.ShortCircuitCurrent, cell.OutputCurrent, cell.ShortCircuitCurrent * 0.05);
    }

    /// <summary>
    /// The thing that separates a panel from a battery: brightness moves the current almost
    /// exactly in proportion and barely moves the voltage at all.
    /// </summary>
    [Fact]
    public void LightSetsTheCurrentAndHardlyTouchesTheVoltage()
    {
        var (fullSim, full, fullLoad) = Rig(0.01);
        var (halfSim, half, halfLoad) = Rig(0.01, illumination: 0.5);

        Assert.Equal(2.0, full.OutputCurrent / half.OutputCurrent, 0.1);

        var (brightOpen, _, brightLoad) = Rig(1e7);
        var (dimOpen, _, dimLoad) = Rig(1e7, illumination: 0.5);

        // Halving the light costs n.kT/q.ln(2) per cell and no more, which for six cells at an
        // ideality of 1.4 is about 150 mV out of three and a half volts.
        var drop = brightOpen.NodeVoltage(brightLoad.A) - dimOpen.NodeVoltage(dimLoad.A);
        var predicted = 1.4 * 0.02585 * 6 * Math.Log(2.0);

        Assert.Equal(predicted, drop, predicted * 0.25);
    }

    /// <summary>
    /// The knee. Ask for less than the light is making and the voltage holds up; ask for more and
    /// it collapses, because there is no more current to be had at any voltage.
    /// </summary>
    [Fact]
    public void AskingForMoreCurrentThanTheLightMakesCollapsesTheVoltage()
    {
        // 150 mA in full sun. A 100 ohm load at 3.5 V wants 35 mA and is comfortable.
        var (easySim, _, easyLoad) = Rig(100);
        Assert.True(easySim.NodeVoltage(easyLoad.A) > 3.0);

        // A 10 ohm load would want 350 mA, which it cannot have.
        var (hardSim, _, hardLoad) = Rig(10);
        Assert.True(hardSim.NodeVoltage(hardLoad.A) < 1.6,
            $"{hardSim.NodeVoltage(hardLoad.A):0.00} V — it should have collapsed past the knee");
    }

    /// <summary>There is a best load, and it is neither of the extremes.</summary>
    [Fact]
    public void ThereIsAMaximumPowerPointPartWayDownTheKnee()
    {
        double PowerInto(double ohms) => Rig(ohms).Cell.Power;

        var best = 0.0;
        var bestOhms = 0.0;

        foreach (var ohms in new[] { 5.0, 10.0, 20.0, 25.0, 30.0, 40.0, 60.0, 100.0, 200.0, 1000.0 })
        {
            var p = PowerInto(ohms);
            if (p <= best) continue;
            best = p;
            bestOhms = ohms;
        }

        Assert.True(bestOhms is > 5.0 and < 200.0,
            $"the best load came out at {bestOhms} ohms, which is one of the extremes");
        Assert.True(best > PowerInto(5.0) && best > PowerInto(1000.0));
    }

    /// <summary>In the dark it is a diode and nothing else.</summary>
    [Fact]
    public void InTheDarkItProducesNothing()
    {
        var (sim, cell, load) = Rig(100, illumination: 0.0);

        Assert.False(cell.IsLit);
        Assert.True(sim.NodeVoltage(load.A) < 0.05);
        Assert.Equal("dark", cell.ValueLabel);
    }
}
