using Cirq.Components.Buses;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The 1-Wire bus, end to end: a master pulling a single line about and a thermometer answering in
/// pulse widths. Nothing here is asserted against a recorded waveform — the numbers are the ones
/// in the DS18B20 datasheet, including the famous one.
/// </summary>
public class OneWireTests
{
    private sealed record Rig(CircuitSimulator Sim, OneWireMaster Master, Ds18b20 Sensor);

    private static Rig Build(
        double temperature = 22.0,
        double pullUp = 4.7e3,
        bool externalPower = true,
        string? operations = null,
        int resolution = 12,
        double cableCapacitance = 0.0,
        double maxStep = 5e-6)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());
        var resistor = circuit.Add(new Resistor(pullUp));
        var master = circuit.Add(new OneWireMaster());
        var sensor = circuit.Add(new Ds18b20 { Temperature = temperature, Resolution = resolution });

        if (operations is not null) master.Operations = operations;

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(master.Vcc, rail.Positive);
        circuit.Connect(master.Gnd, gnd.Pin);
        circuit.Connect(sensor.Gnd, gnd.Pin);

        // Parasitic power is the supply pin tied to ground rather than left unconnected.
        circuit.Connect(sensor.Vdd, externalPower ? rail.Positive : gnd.Pin);

        circuit.Connect(resistor.A, rail.Positive);
        circuit.Connect(resistor.B, master.Data);
        circuit.Connect(master.Data, sensor.Data);

        // What a long run of cable adds: with the pull-up it sets how fast the line can come back
        // up, and on this bus that is the same thing as how fast it can be read.
        if (cableCapacitance > 0)
        {
            var cable = circuit.Add(new Capacitor(cableCapacitance));
            circuit.Connect(cable.A, master.Data);
            circuit.Connect(cable.B, gnd.Pin);
        }

        var sim = new CircuitSimulator(circuit,
            new SimulationSettings { TimeStep = Math.Min(2e-6, maxStep), MaxTimeStep = maxStep });

        sim.Reset();
        sim.SolveOperatingPoint();

        return new Rig(sim, master, sensor);
    }

    private static double ReadBack(Rig rig)
    {
        rig.Sim.Run(1.0);

        var bytes = rig.Master.ReceivedBytes;
        if (bytes.Count < 2) return double.NaN;

        return (short)(bytes[0] | (bytes[1] << 8)) * 0.0625;
    }

    [Fact]
    public void EverybodyOnTheLineAnswersAReset()
    {
        var rig = Build();
        rig.Sim.Run(1e-3);

        Assert.True(rig.Master.PresenceDetected, "nothing answered the reset pulse");
    }

    /// <summary>
    /// The whole exchange: reset, skip addressing, convert, wait, reset, read the scratchpad. The
    /// number that comes back has travelled over one wire as a series of pulse widths.
    /// </summary>
    [Fact]
    public void ATemperatureComesBackOverOneWire()
    {
        var rig = Build(temperature: 22.0);

        Assert.Equal(22.0, ReadBack(rig), 0.07);
        Assert.Equal(9, rig.Master.ReceivedBytes.Count);
    }

    [Theory]
    [InlineData(-10.5)]
    [InlineData(0.0)]
    [InlineData(37.25)]
    [InlineData(99.9375)]
    public void AndSoDoTheAwkwardOnes(double temperature)
    {
        Assert.Equal(temperature, ReadBack(Build(temperature)), 0.07);
    }

    /// <summary>
    /// The most reported DS18B20 fault, which is not a fault. The scratchpad powers up holding
    /// exactly 85.0 °C and keeps it until a conversion has actually finished, so a read that comes
    /// back 85 means the conversion never happened — not that the room is hot.
    /// </summary>
    [Fact]
    public void ReadingBeforeConvertingGivesEightyFive()
    {
        // Straight to the scratchpad with no Convert T, and no waiting.
        var rig = Build(temperature: 22.0, operations: "reset; w CC BE; r 9");

        Assert.Equal(85.0, ReadBack(rig), 0.01);
        Assert.False(rig.Sensor.HasConverted);
    }

    /// <summary>And the same if the wait is too short for the conversion to have finished.</summary>
    [Fact]
    public void SoDoesNotWaitingLongEnough()
    {
        var rig = Build(temperature: 22.0, operations: "reset; w CC 44; d 1000; reset; w CC BE; r 9");

        Assert.Equal(85.0, ReadBack(rig), 0.01);
    }

    /// <summary>
    /// A function command has to be preceded by a ROM command. Forget the 0xCC and the part simply
    /// does not answer — which looks exactly like a dead sensor.
    /// </summary>
    [Fact]
    public void AFunctionCommandWithoutAddressingIsIgnored()
    {
        var rig = Build(temperature: 22.0, operations: "reset; w 44; d 750000; reset; w BE; r 9");

        rig.Sim.Run(1.0);

        Assert.False(rig.Sensor.HasConverted, "it converted without having been addressed");
        Assert.False(rig.Sensor.IsAddressed);
    }

    /// <summary>
    /// Fewer bits is a faster conversion, and the reading comes back rounded to match. This is the
    /// trade that decides whether a string of sensors can be read once a second.
    /// </summary>
    [Theory]
    [InlineData(12, 0.0625, 750e-3)]
    [InlineData(11, 0.125, 375e-3)]
    [InlineData(10, 0.25, 187.5e-3)]
    [InlineData(9, 0.5, 93.75e-3)]
    public void ResolutionTradesPrecisionForTime(int bits, double step, double seconds)
    {
        var sensor = new Ds18b20 { Resolution = bits };

        Assert.Equal(step, sensor.ResolutionStep, 6);
        Assert.Equal(seconds, sensor.ConversionSeconds, 6);
    }

    [Fact]
    public void ACoarseResolutionRoundsTheAnswer()
    {
        // 22.3 at nine bits can only be 22.0 or 22.5.
        var reading = ReadBack(Build(temperature: 22.3, resolution: 9));

        Assert.Equal(22.5, reading, 0.01);
    }

    /// <summary>
    /// Parasitic power, and the sharp edge on it. Converting needs a milliamp and a half, which an
    /// ordinary pull-up cannot pass: the line sags, the chip browns out part way through, and the
    /// scratchpad still holds 85 — with nothing anywhere saying why.
    /// </summary>
    [Fact]
    public void ParasiticPowerWithAnOrdinaryPullUpFailsToConvert()
    {
        var rig = Build(temperature: 22.0, externalPower: false, pullUp: 4.7e3);

        Assert.Equal(85.0, ReadBack(rig), 0.01);
        Assert.True(rig.Sensor.ConversionBrownedOut, "the conversion should have browned out");
    }

    /// <summary>And the fix: a pull-up stiff enough to feed it while it works.</summary>
    [Fact]
    public void AStrongPullUpMakesParasiticPowerWork()
    {
        var rig = Build(temperature: 22.0, externalPower: false, pullUp: 1e3);

        Assert.Equal(22.0, ReadBack(rig), 0.07);
        Assert.False(rig.Sensor.ConversionBrownedOut);
    }

    /// <summary>
    /// The ninth byte is a CRC over the other eight, and checking it is how a reading corrupted by
    /// bad timing is told from a real one. Against a known scratchpad from the datasheet, so the
    /// implementation is checked rather than merely being self-consistent.
    /// </summary>
    [Fact]
    public void TheScratchpadCrcIsTheDallasOne()
    {
        int[] scratchpad = [0x50, 0x05, 0x4B, 0x46, 0x7F, 0xFF, 0x0C, 0x10];

        Assert.Equal(0x1C, Ds18b20.Crc8(scratchpad, 8));
    }

    [Fact]
    public void AndTheOneItSendsChecksOut()
    {
        var rig = Build(temperature: 22.0);
        rig.Sim.Run(1.0);

        var bytes = rig.Master.ReceivedBytes;

        Assert.Equal(9, bytes.Count);
        Assert.Equal(bytes[8], Ds18b20.Crc8(bytes, 8));
    }

    /// <summary>
    /// What actually goes wrong on a long cable, and why 1-Wire is fussier than the other two
    /// buses. The pull-up has to charge the cable's capacitance, and on this bus the shape of the
    /// rising edge <i>is</i> the data: too weak a pull-up on too much cable and a one has not
    /// climbed back by the time the master looks. It does not degrade — it returns nonsense.
    /// <para>
    /// Same part, same pull-up, same everything except the length of the wire: a couple of metres
    /// is fine and a hundred is not.
    /// </para>
    /// <para>
    /// Read straight out of the scratchpad with no conversion, because resolving a twenty
    /// nanosecond edge across the three quarters of a second a conversion takes is not something a
    /// transient simulation can afford. Eighty-five is the right answer here, so anything else
    /// coming back means the bits were mangled on the way.
    /// </para>
    /// </summary>
    [Fact]
    public void TooWeakAPullUpOnTooMuchCableReturnsRubbish()
    {
        // About a hundred metres of cable at the hundred picofarads a metre that twisted pair runs to.
        var slow = Build(pullUp: 4.7e3, cableCapacitance: 10e-9,
            operations: "reset; w CC BE; r 2", maxStep: 20e-9);

        slow.Sim.Run(4e-3);

        Assert.NotEqual(85.0, Reading(slow), 0.5);
    }

    /// <summary>A couple of metres of the same cable, on the same pull-up, is perfect.</summary>
    [Fact]
    public void TheUsualFourPointSevenKilohmsCopesWithIt()
    {
        var fine = Build(pullUp: 4.7e3, cableCapacitance: 220e-12,
            operations: "reset; w CC BE; r 2", maxStep: 20e-9);

        fine.Sim.Run(4e-3);

        Assert.Equal(85.0, Reading(fine), 0.01);
    }

    /// <summary>The first two bytes of whatever came back, as a temperature.</summary>
    private static double Reading(Rig rig) =>
        rig.Master.ReceivedBytes.Count < 2
            ? double.NaN
            : (short)(rig.Master.ReceivedBytes[0] | (rig.Master.ReceivedBytes[1] << 8)) * 0.0625;


    /// <summary>The ROM code identifies the part: family 0x28 for this one, and its own CRC.</summary>
    [Fact]
    public void TheRomCodeSaysWhatKindOfPartItIs()
    {
        var sensor = new Ds18b20();

        Assert.Equal(0x28, sensor.RomCode[0]);
        Assert.Equal(sensor.RomCode[7], Ds18b20.Crc8(sensor.RomCode, 7));
    }
}
