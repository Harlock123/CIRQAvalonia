using Cirq.Components.Buses;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class I2cTests
{
    private sealed record Bus(CircuitSimulator Sim, I2cMaster Master, Circuit Circuit);

    /// <summary>
    /// A master, whatever slaves you hand it, and the two pull-ups without which none of it
    /// works. Everything on an I²C bus shares the same two wires.
    /// </summary>
    private static Bus Wire(string script, bool pullUps, params I2cDevice[] slaves)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(3.3));
        var master = circuit.Add(new I2cMaster { Transactions = script, ClockFrequency = 100e3 });
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(master.Vcc, rail.Positive);
        circuit.Connect(master.Gnd, gnd.Pin);

        foreach (var slave in slaves)
        {
            circuit.Add(slave);
            circuit.Connect(slave.Vcc, rail.Positive);
            circuit.Connect(slave.Gnd, gnd.Pin);
            circuit.Connect(slave.Sda, master.Sda);
            circuit.Connect(slave.Scl, master.Scl);
        }

        if (pullUps)
        {
            var sdaPull = circuit.Add(new Resistor(4.7e3));
            var sclPull = circuit.Add(new Resistor(4.7e3));

            circuit.Connect(rail.Positive, sdaPull.A);
            circuit.Connect(sdaPull.B, master.Sda);
            circuit.Connect(rail.Positive, sclPull.A);
            circuit.Connect(sclPull.B, master.Scl);
        }

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-6, MaxTimeStep = 2e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();
        return new Bus(sim, master, circuit);
    }

    /// <summary>
    /// A byte written over two wires and found in the device at the other end. This is the whole
    /// point: start, address, acknowledge, data, stop, all generated and decoded rather than
    /// handed across.
    /// </summary>
    [Fact]
    public void AByteWrittenOverTheBusArrivesInTheDevice()
    {
        var eeprom = new I2cEeprom();
        var bus = Wire("w 50 00 00 A5", pullUps: true, eeprom);

        bus.Sim.Run(5e-3);

        Assert.True(bus.Master.IsFinished);
        Assert.True(bus.Master.LastTransferAcknowledged);
        Assert.Equal(0xA5, eeprom.Read(0x0000));
    }

    [Fact]
    public void SeveralBytesGoInAtConsecutiveAddresses()
    {
        var eeprom = new I2cEeprom();
        var bus = Wire("w 50 01 00 DE AD BE EF", pullUps: true, eeprom);

        bus.Sim.Run(8e-3);

        Assert.Equal(0xDE, eeprom.Read(0x0100));
        Assert.Equal(0xAD, eeprom.Read(0x0101));
        Assert.Equal(0xBE, eeprom.Read(0x0102));
        Assert.Equal(0xEF, eeprom.Read(0x0103));
    }

    /// <summary>Written, then read back off the same two wires.</summary>
    [Fact]
    public void WhatWasWrittenCanBeReadBack()
    {
        var eeprom = new I2cEeprom();
        var bus = Wire("w 50 00 00 12 34 56; w 50 00 00; r 50 3", pullUps: true, eeprom);

        bus.Sim.Run(20e-3);

        Assert.True(bus.Master.IsFinished);
        Assert.Equal([0x12, 0x34, 0x56], bus.Master.ReceivedBytes);
    }

    /// <summary>
    /// Addressing is the acknowledge and nothing else. Talk to an address nobody answers to and
    /// the line stays high, which is the only symptom a real bus gives you.
    /// </summary>
    [Fact]
    public void AnAddressNobodyAnswersToIsNotAcknowledged()
    {
        var eeprom = new I2cEeprom { Address = 0x50 };
        var bus = Wire("w 51 00 00 A5", pullUps: true, eeprom);

        bus.Sim.Run(5e-3);

        Assert.False(bus.Master.LastTransferAcknowledged);
        Assert.Equal(0x00, eeprom.Read(0x0000));
    }

    /// <summary>Two devices, two addresses, one pair of wires — which is the point of a bus.</summary>
    [Fact]
    public void TwoDevicesShareTheBusAndOnlyTheAddressedOneListens()
    {
        var first = new I2cEeprom { Address = 0x50 };
        var second = new I2cEeprom { Address = 0x51 };

        var bus = Wire("w 50 00 00 11; w 51 00 00 22", pullUps: true, first, second);
        bus.Sim.Run(12e-3);

        Assert.Equal(0x11, first.Read(0));
        Assert.Equal(0x22, second.Read(0));
        Assert.True(bus.Master.LastTransferAcknowledged);
    }

    /// <summary>
    /// Nothing on an I²C bus ever drives a line high, so without pull-ups there is no high level
    /// to make and the bus does not work at all. That is the classic first mistake and it fails
    /// here the way it fails on a breadboard.
    /// </summary>
    [Fact]
    public void WithoutPullUpsTheBusDoesNothing()
    {
        var eeprom = new I2cEeprom();
        var bus = Wire("w 50 00 00 A5", pullUps: false, eeprom);

        bus.Sim.Run(5e-3);

        Assert.Equal(0x00, eeprom.Read(0x0000));
    }

    /// <summary>A port expander: a byte over the bus turns up on eight pins.</summary>
    [Fact]
    public void AByteWrittenToAnExpanderAppearsOnItsPins()
    {
        var expander = new Pcf8574 { Address = 0x20 };
        var bus = Wire("w 20 5A", pullUps: true, expander);

        // The port pins need somewhere to go, since a released pin is an open drain.
        var gnd = bus.Circuit.Components.OfType<Ground>().First();
        var rail = bus.Circuit.Components.OfType<DcVoltageSource>().First();
        var loads = new Resistor[8];

        for (var i = 0; i < 8; i++)
        {
            loads[i] = bus.Circuit.Add(new Resistor(10e3));
            bus.Circuit.Connect(rail.Positive, loads[i].A);
            bus.Circuit.Connect(loads[i].B, expander.Pins[i]);
        }

        var sim = new CircuitSimulator(bus.Circuit,
            new SimulationSettings { TimeStep = 1e-6, MaxTimeStep = 2e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(5e-3);

        Assert.Equal(0x5A, expander.Port);

        // 0x5A is 01011010: a one releases the pin to its pull-up, a zero pulls it down.
        for (var i = 0; i < 8; i++)
        {
            var expected = (0x5A & (1 << i)) != 0;
            Assert.Equal(expected, sim.NodeVoltage(expander.Pins[i]) > 1.6);
        }
    }
}
