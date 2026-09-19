using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class SpiTests
{
    /// <summary>An SPI master clocking bytes into a 74595, which is what they are usually for.</summary>
    private static (CircuitSimulator Sim, SpiMaster Master, Ic74595 Register) Rig(string script)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var master = circuit.Add(new SpiMaster { Transactions = script, ClockFrequency = 500e3 });
        var register = circuit.Add(new Ic74595());
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(master.Vcc, rail.Positive);
        circuit.Connect(master.Gnd, gnd.Pin);
        circuit.Connect(register.Vcc, rail.Positive);
        circuit.Connect(register.Gnd, gnd.Pin);

        // The select line is the latch: the outputs update when the transfer ends, which is
        // exactly what a 595 wants and why the two parts go together so neatly.
        circuit.Connect(master.Clock, register.ShiftClock);
        circuit.Connect(master.MasterOut, register.SerialIn);
        circuit.Connect(master.ChipSelect, register.LatchClock);

        circuit.Connect(register.Clear, rail.Positive);
        circuit.Connect(register.OutputEnable, gnd.Pin);

        foreach (var output in register.Outputs)
        {
            var load = circuit.Add(new Resistor(10e3));
            circuit.Connect(output, load.A);
            circuit.Connect(load.B, gnd.Pin);
        }

        var sim = new CircuitSimulator(circuit,
            new SimulationSettings { TimeStep = 2e-7, MaxTimeStep = 4e-7 });
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, master, register);
    }

    /// <summary>A byte clocked out of the master and found on eight pins at the other end.</summary>
    [Fact]
    public void AByteClockedOutTurnsUpOnTheOutputs()
    {
        var (sim, master, register) = Rig("5A");
        sim.Run(200e-6);

        Assert.True(master.IsFinished);
        Assert.Equal(0x5A, register.Latched);
    }

    /// <summary>Most significant bit first, which is what nearly everything expects.</summary>
    [Fact]
    public void ItClocksTheMostSignificantBitFirst()
    {
        var (sim, _, register) = Rig("80");
        sim.Run(200e-6);

        // 0x80 shifted in first-bit-high ends with that bit at the far end of the register.
        Assert.Equal(0x80, register.Latched);
        Assert.True(sim.NodeVoltage(register.Outputs[0]) > 2.5, "QA should be the first bit sent");
    }

    /// <summary>
    /// The latch is what separates a 595 from a plain shift register: the outputs do not move
    /// while the bits are walking through, only when they are told to.
    /// </summary>
    [Fact]
    public void TheOutputsDoNotMoveUntilTheyAreLatched()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var master = circuit.Add(new SpiMaster { Transactions = "FF", ClockFrequency = 500e3 });
        var register = circuit.Add(new Ic74595());
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(master.Vcc, rail.Positive);
        circuit.Connect(master.Gnd, gnd.Pin);
        circuit.Connect(register.Vcc, rail.Positive);
        circuit.Connect(register.Gnd, gnd.Pin);
        circuit.Connect(master.Clock, register.ShiftClock);
        circuit.Connect(master.MasterOut, register.SerialIn);

        // Latch held low: the bits go in but nothing reaches the pins.
        circuit.Connect(register.LatchClock, gnd.Pin);
        circuit.Connect(register.Clear, rail.Positive);
        circuit.Connect(register.OutputEnable, gnd.Pin);

        var sim = new CircuitSimulator(circuit,
            new SimulationSettings { TimeStep = 2e-7, MaxTimeStep = 4e-7 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(200e-6);

        Assert.Equal(0xFF, register.ShiftRegister);
        Assert.Equal(0x00, register.Latched);
    }

    /// <summary>Several bytes in one transfer, which is how a chain of them is filled.</summary>
    [Fact]
    public void SeveralBytesInATransferLeaveTheLastOneShowing()
    {
        var (sim, _, register) = Rig("0F F0 3C");
        sim.Run(400e-6);

        // Only eight bits fit, so the last byte sent is what is left in the register.
        Assert.Equal(0x3C, register.Latched);
    }

    /// <summary>Output enable releases the pins rather than driving them low.</summary>
    [Fact]
    public void OutputEnableReleasesThePins()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var register = circuit.Add(new Ic74595());
        var pull = circuit.Add(new Resistor(10e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(register.Vcc, rail.Positive);
        circuit.Connect(register.Gnd, gnd.Pin);
        circuit.Connect(register.OutputEnable, rail.Positive);     // disabled
        circuit.Connect(register.Clear, rail.Positive);
        circuit.Connect(register.ShiftClock, gnd.Pin);
        circuit.Connect(register.LatchClock, gnd.Pin);
        circuit.Connect(register.SerialIn, gnd.Pin);

        circuit.Connect(rail.Positive, pull.A);
        circuit.Connect(pull.B, register.Outputs[0]);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1e-6);

        // Released, so the pull-up wins rather than the output holding it down.
        Assert.True(sim.NodeVoltage(register.Outputs[0]) > 4.5);
    }
}
