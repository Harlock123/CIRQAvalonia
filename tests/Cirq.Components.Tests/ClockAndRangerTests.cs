using Cirq.Components.Buses;
using Cirq.Components.Electromechanical;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class Ds1307Tests
{
    private static (CircuitSimulator Sim, I2cMaster Master, Ds1307 Clock) Rig(string script, Ds1307 clock)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(3.3));
        var master = circuit.Add(new I2cMaster { Transactions = script, ClockFrequency = 100e3 });
        var gnd = circuit.Add(new Ground());

        circuit.Add(clock);

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(master.Vcc, rail.Positive);
        circuit.Connect(master.Gnd, gnd.Pin);
        circuit.Connect(clock.Vcc, rail.Positive);
        circuit.Connect(clock.Gnd, gnd.Pin);
        circuit.Connect(clock.Sda, master.Sda);
        circuit.Connect(clock.Scl, master.Scl);

        var sdaPull = circuit.Add(new Resistor(4.7e3));
        var sclPull = circuit.Add(new Resistor(4.7e3));
        circuit.Connect(rail.Positive, sdaPull.A);
        circuit.Connect(sdaPull.B, master.Sda);
        circuit.Connect(rail.Positive, sclPull.A);
        circuit.Connect(sclPull.B, master.Scl);

        var sim = new CircuitSimulator(circuit,
            new SimulationSettings { TimeStep = 1e-6, MaxTimeStep = 2e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, master, clock);
    }

    /// <summary>Binary-coded decimal: each nibble is a digit, which catches everybody once.</summary>
    [Theory]
    [InlineData(0, 0x00)]
    [InlineData(9, 0x09)]
    [InlineData(10, 0x10)]
    [InlineData(59, 0x59)]
    public void TheRegistersAreBinaryCodedDecimal(int value, int encoded)
    {
        Assert.Equal(encoded, Ds1307.ToBcd(value));
        Assert.Equal(value, Ds1307.FromBcd(encoded));
    }

    /// <summary>
    /// The time comes back off the bus, in BCD, from the registers the datasheet says.
    /// </summary>
    [Fact]
    public void TheTimeCanBeReadOverTheBus()
    {
        var clock = new Ds1307 { StartHour = 13, StartMinute = 45, StartSecond = 30, TimeScale = 0 };
        var (sim, master, _) = Rig("w 68 00; r 68 3", clock);

        sim.Run(10e-3);

        Assert.True(master.IsFinished);
        Assert.Equal(3, master.ReceivedBytes.Count);

        Assert.Equal(30, Ds1307.FromBcd(master.ReceivedBytes[0] & 0x7F));
        Assert.Equal(45, Ds1307.FromBcd(master.ReceivedBytes[1]));
        Assert.Equal(13, Ds1307.FromBcd(master.ReceivedBytes[2]));
    }

    /// <summary>
    /// It has something of its own to say, unlike the EEPROM beside it: read it twice and the
    /// answers differ.
    /// </summary>
    [Fact]
    public void TheTimeMovesOnItsOwn()
    {
        var clock = new Ds1307 { StartHour = 0, StartMinute = 0, StartSecond = 0, TimeScale = 1000 };
        var (sim, _, _) = Rig("w 68 00; r 68 1", clock);

        sim.Run(1e-3);
        var first = clock.SecondsSinceMidnight;

        sim.Run(20e-3);
        var later = clock.SecondsSinceMidnight;

        // Twenty milliseconds at a thousand times speed is twenty seconds.
        Assert.Equal(20.0, later - first, 1.0);
    }

    /// <summary>
    /// A new part comes up with the clock halt bit set and the clock stopped, which is why a
    /// first-time DS1307 famously does nothing until something writes to it.
    /// </summary>
    [Fact]
    public void HaltingTheClockStopsIt()
    {
        var clock = new Ds1307 { StartHour = 6, StartMinute = 0, StartSecond = 0, TimeScale = 1000, ClockHalted = true };
        var (sim, _, _) = Rig("w 68 00; r 68 1", clock);

        sim.Run(20e-3);

        Assert.Equal(6 * 3600, clock.SecondsSinceMidnight, 0.001);
        Assert.Contains("stopped", clock.ValueLabel);
    }
}

public class UltrasonicRangerTests
{
    private static (CircuitSimulator Sim, UltrasonicRanger Ranger, DcVoltageSource Trig, Resistor Load)
        Rig(double distance)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var ranger = circuit.Add(new UltrasonicRanger { DistanceCentimetres = distance });
        var trig = circuit.Add(new DcVoltageSource(0.0));
        var load = circuit.Add(new Resistor(10e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(ranger.Vcc, rail.Positive);
        circuit.Connect(ranger.Gnd, gnd.Pin);
        circuit.Connect(trig.Negative, gnd.Pin);
        circuit.Connect(trig.Positive, ranger.Trigger);
        circuit.Connect(ranger.Echo, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit,
            new SimulationSettings { TimeStep = 2e-6, MaxTimeStep = 4e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, ranger, trig, load);
    }

    /// <summary>Measures the echo pulse the way a driver would: by timing the edges.</summary>
    private static double PulseWidth(CircuitSimulator sim, Resistor load, double window)
    {
        double start = -1, end = -1;
        var steps = (int)(window / 4e-6);

        for (var i = 0; i < steps; i++)
        {
            sim.Run(4e-6);

            var high = sim.NodeVoltage(load.A) > 2.5;

            if (high && start < 0) start = sim.Time;
            else if (!high && start > 0 && end < 0) end = sim.Time;
        }

        return end > 0 ? end - start : -1;
    }

    private static void Trigger(CircuitSimulator sim, DcVoltageSource trig)
    {
        trig.Voltage = 5.0;
        sim.Run(12e-6);
        trig.Voltage = 0.0;
        sim.Run(8e-6);
    }

    /// <summary>
    /// The distance is in the length of the pulse and nowhere else — 58 microseconds per
    /// centimetre, out and back at the speed of sound.
    /// </summary>
    [Theory]
    [InlineData(20.0)]
    [InlineData(100.0)]
    [InlineData(200.0)]
    public void TheEchoPulseIsFiftyEightMicrosecondsPerCentimetre(double distance)
    {
        var (sim, _, trig, load) = Rig(distance);

        Trigger(sim, trig);
        var width = PulseWidth(sim, load, 60e-3);

        Assert.Equal(distance * 58e-6, width, distance * 58e-6 * 0.05);
    }

    /// <summary>Nothing happens until it is triggered.</summary>
    [Fact]
    public void ItSaysNothingUntilItIsTriggered()
    {
        var (sim, ranger, _, load) = Rig(50.0);

        sim.Run(10e-3);

        Assert.False(ranger.IsEchoing);
        Assert.True(sim.NodeVoltage(load.A) < 2.5);
    }

    /// <summary>A blip shorter than ten microseconds is ignored, as on the real part.</summary>
    [Fact]
    public void ATriggerPulseThatIsTooShortIsIgnored()
    {
        var (sim, ranger, trig, load) = Rig(50.0);

        trig.Voltage = 5.0;
        sim.Run(4e-6);
        trig.Voltage = 0.0;

        Assert.Equal(-1, PulseWidth(sim, load, 20e-3));
        Assert.False(ranger.IsEchoing);
    }

    /// <summary>
    /// With nothing in range it does not stay quiet: it gives a long pulse and gives up, which is
    /// why code without a timeout reports something absurd rather than hanging.
    /// </summary>
    [Fact]
    public void OutOfRangeItGivesALongPulseRatherThanNone()
    {
        var (sim, ranger, trig, load) = Rig(900.0);

        Assert.False(ranger.IsInRange);
        Assert.Equal("out of range", ranger.ValueLabel);

        Trigger(sim, trig);
        var width = PulseWidth(sim, load, 60e-3);

        Assert.Equal(38e-3, width, 2e-3);
    }

    /// <summary>Double-clicking moves the target, so the reading can be changed while it runs.</summary>
    [Fact]
    public void TheTargetCanBeMoved()
    {
        var ranger = new UltrasonicRanger { DistanceCentimetres = 30, AlternateDistance = 300 };

        ranger.Interact();
        Assert.Equal(300, ranger.DistanceCentimetres);

        ranger.Interact();
        Assert.Equal(30, ranger.DistanceCentimetres);
    }
}
