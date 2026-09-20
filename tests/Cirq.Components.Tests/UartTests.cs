using Cirq.Components.Buses;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// UART is the bus with no clock, so everything here is really about the two ends counting time
/// for themselves and what happens when they count differently.
/// </summary>
public class UartTests
{
    private static (CircuitSimulator Sim, SerialTerminal Terminal, SerialDevice Device) Link(
        double terminalBaud = 9600, double deviceBaud = 9600, string message = "hi")
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(3.3));
        var gnd = circuit.Add(new Ground());

        var terminal = circuit.Add(new SerialTerminal
        {
            BaudRate = terminalBaud,
            Message = message,
            StartDelay = 200e-6,
        });

        var device = circuit.Add(new SerialDevice { BaudRate = deviceBaud, Greeting = "" });

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(terminal.Vcc, rail.Positive);
        circuit.Connect(terminal.Gnd, gnd.Pin);
        circuit.Connect(device.Vcc, rail.Positive);
        circuit.Connect(device.Gnd, gnd.Pin);

        // TX to RX, both ways round. This is the wiring, and it is the thing people get wrong.
        circuit.Connect(terminal.Transmit, device.Receive);
        circuit.Connect(device.Transmit, terminal.Receive);

        var sim = new CircuitSimulator(circuit,
            new SimulationSettings { TimeStep = 2e-6, MaxTimeStep = 5e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, terminal, device);
    }

    /// <summary>A byte goes across and comes out the other side intact.</summary>
    [Fact]
    public void ACharacterCrossesTheWire()
    {
        var (sim, _, device) = Link(message: "A");

        sim.Run(5e-3);

        Assert.Equal([(int)'A'], device.ReceivedBytes);
        Assert.Equal(0, device.FramingErrors);
    }

    [Fact]
    public void AWholeStringCrossesInOrder()
    {
        var (sim, _, device) = Link(message: "Hello");

        sim.Run(12e-3);

        Assert.Equal("Hello", device.ReceivedText);
    }

    /// <summary>The far end answers, so the round trip works in both directions.</summary>
    [Fact]
    public void TheDeviceEchoesBack()
    {
        var (sim, terminal, _) = Link(message: "abc");

        sim.Run(20e-3);

        Assert.Equal("ABC", terminal.ReceivedText);
    }

    /// <summary>
    /// The headline, and the reason the receiver is modelled as counting for itself rather than
    /// being handed the bytes. A wrong rate does not produce silence or noise — it produces
    /// definite wrong bytes, and which way they are wrong follows from the arithmetic.
    /// <para>
    /// Counting at <b>twice</b> the rate, the receiver reads every transmitted bit as two and
    /// finds more bytes than were ever sent. Counting at <b>half</b>, it merges pairs of bits and
    /// finds fewer. Five characters in, seven out at 19200 and two out at 4800 — with framing
    /// errors where the stop bit failed to land high.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(19200, 5)]
    [InlineData(4800, -5)]
    public void AMismatchedBaudRateReadsDefiniteWrongBytes(double deviceBaud, int direction)
    {
        var (sim, _, device) = Link(terminalBaud: 9600, deviceBaud: deviceBaud, message: "Hello");

        sim.Run(40e-3);

        Assert.NotEqual("Hello", device.ReceivedText);
        Assert.NotEmpty(device.ReceivedBytes);

        if (direction > 0)
            Assert.True(device.ReceivedBytes.Count > 5, "counting fast should find more bytes than were sent");
        else
            Assert.True(device.ReceivedBytes.Count < 5, "counting slow should find fewer bytes than were sent");

        Assert.True(device.FramingErrors > 0, "the stop bit should have landed in the wrong place");
        Assert.NotEmpty(device.Violations);
    }

    /// <summary>Whatever comes out of a mismatch, it is repeatable rather than random.</summary>
    [Fact]
    public void TheWrongBytesAreTheSameWrongBytesEveryTime()
    {
        List<string> runs = [];

        for (var i = 0; i < 2; i++)
        {
            var (sim, _, device) = Link(terminalBaud: 9600, deviceBaud: 19200, message: "Hello");
            sim.Run(40e-3);
            runs.Add(string.Join(" ", device.ReceivedBytes));
        }

        Assert.Equal(runs[0], runs[1]);
        Assert.NotEmpty(runs[0]);
    }

    /// <summary>
    /// The usual tolerance: a couple of percent of error is fine, because the receiver only has
    /// to stay inside the bit until the stop.
    /// </summary>
    [Theory]
    [InlineData(1.02)]
    [InlineData(0.98)]
    public void ASmallClockErrorIsToleratedAsOnHardware(double factor)
    {
        var (sim, _, device) = Link(terminalBaud: 9600, deviceBaud: 9600 * factor, message: "Hi");

        sim.Run(12e-3);

        Assert.Equal("Hi", device.ReceivedText);
        Assert.Equal(0, device.FramingErrors);
    }

    /// <summary>
    /// TX to TX is the classic wiring mistake, and what it gives you is nothing: the two outputs
    /// hold the line at each other and no byte reaches either receiver, because neither receiver
    /// is connected to anything that talks.
    /// </summary>
    [Fact]
    public void TransmitWiredToTransmitMovesNoData()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(3.3));
        var gnd = circuit.Add(new Ground());

        var terminal = circuit.Add(new SerialTerminal { Message = "hi", StartDelay = 200e-6 });
        var device = circuit.Add(new SerialDevice { Greeting = "" });

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(terminal.Vcc, rail.Positive);
        circuit.Connect(terminal.Gnd, gnd.Pin);
        circuit.Connect(device.Vcc, rail.Positive);
        circuit.Connect(device.Gnd, gnd.Pin);

        circuit.Connect(terminal.Transmit, device.Transmit);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 2e-6, MaxTimeStep = 5e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(10e-3);

        Assert.Empty(device.ReceivedBytes);
        Assert.Empty(terminal.ReceivedBytes);
    }

    /// <summary>
    /// A receive line sitting at ground is a <b>break</b>, not a byte, and that is what an
    /// unconnected RX pin looks like here. It produces exactly one framing error and then stops:
    /// the receiver needs the line to go back up to idle before it can frame anything again, and
    /// a line held down never does. Real hardware behaves the same way, which is why a dead link
    /// gives you one complaint rather than a stream of them.
    /// </summary>
    [Fact]
    public void AReceiveLineHeldLowIsABreakRatherThanAByte()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(3.3));
        var gnd = circuit.Add(new Ground());

        var device = circuit.Add(new SerialDevice { Greeting = "" });

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(device.Vcc, rail.Positive);
        circuit.Connect(device.Gnd, gnd.Pin);
        circuit.Connect(device.Receive, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 2e-6, MaxTimeStep = 5e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(20e-3);

        Assert.Empty(device.ReceivedBytes);
        Assert.Equal(1, device.FramingErrors);
    }

    /// <summary>A module that announces itself does so without being asked.</summary>
    [Fact]
    public void TheDeviceGreetsOnItsOwn()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(3.3));
        var gnd = circuit.Add(new Ground());

        var terminal = circuit.Add(new SerialTerminal { Message = "", StartDelay = 1.0 });
        var device = circuit.Add(new SerialDevice { Greeting = "OK" });

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(terminal.Vcc, rail.Positive);
        circuit.Connect(terminal.Gnd, gnd.Pin);
        circuit.Connect(device.Vcc, rail.Positive);
        circuit.Connect(device.Gnd, gnd.Pin);
        circuit.Connect(terminal.Transmit, device.Receive);
        circuit.Connect(device.Transmit, terminal.Receive);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 2e-6, MaxTimeStep = 5e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(10e-3);

        Assert.Equal("OK", terminal.ReceivedText);
    }

    /// <summary>Parity has to agree as well, and disagreeing about it is its own error.</summary>
    [Fact]
    public void DisagreeingAboutParityIsReported()
    {
        var (sim, terminal, device) = Link(message: "U");
        terminal.Parity = UartParity.Even;
        device.Parity = UartParity.Odd;

        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(10e-3);

        Assert.True(device.ParityErrors > 0, "the two ends disagreed about parity and nothing said so");
    }

    /// <summary>With matching parity the byte still arrives, extra bit and all.</summary>
    [Fact]
    public void MatchingParityStillCarriesTheByte()
    {
        var (sim, terminal, device) = Link(message: "U");
        terminal.Parity = UartParity.Even;
        device.Parity = UartParity.Even;

        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(10e-3);

        Assert.Equal("U", device.ReceivedText);
        Assert.Equal(0, device.ParityErrors);
    }

    /// <summary>An unpowered port lets go of the line rather than holding it idle-high.</summary>
    [Fact]
    public void AnUnpoweredPortSaysNothing()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(3.3));
        var dead = circuit.Add(new DcVoltageSource(0.0));
        var gnd = circuit.Add(new Ground());

        var terminal = circuit.Add(new SerialTerminal { Message = "hi", StartDelay = 200e-6 });
        var device = circuit.Add(new SerialDevice { Greeting = "OK" });

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(dead.Negative, gnd.Pin);
        circuit.Connect(terminal.Vcc, rail.Positive);
        circuit.Connect(terminal.Gnd, gnd.Pin);

        // The far end has no supply.
        circuit.Connect(device.Vcc, dead.Positive);
        circuit.Connect(device.Gnd, gnd.Pin);
        circuit.Connect(terminal.Transmit, device.Receive);
        circuit.Connect(device.Transmit, terminal.Receive);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 2e-6, MaxTimeStep = 5e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(10e-3);

        Assert.Empty(device.ReceivedBytes);
        Assert.Empty(terminal.ReceivedBytes);
    }
}

/// <summary>
/// A terminal that repeats, so a link has something on it to look at rather than one burst that
/// has scrolled off the scope by the time you glance at it.
/// </summary>
public class UartRepeatTests
{
    [Fact]
    public void ARepeatingTerminalKeepsSending()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(3.3));
        var gnd = circuit.Add(new Ground());

        var terminal = circuit.Add(new SerialTerminal
        {
            Message = "hi",
            StartDelay = 500e-6,
            RepeatInterval = 5e-3,
        });

        var device = circuit.Add(new SerialDevice { Greeting = "", Echo = false });

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(terminal.Vcc, rail.Positive);
        circuit.Connect(terminal.Gnd, gnd.Pin);
        circuit.Connect(device.Vcc, rail.Positive);
        circuit.Connect(device.Gnd, gnd.Pin);
        circuit.Connect(terminal.Transmit, device.Receive);
        circuit.Connect(device.Transmit, terminal.Receive);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 2e-6, MaxTimeStep = 5e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();
        // The first at half a millisecond and then every five, so six go out by twenty-five and a
        // half; two characters at 9600 baud take two milliseconds, so the sixth has landed by
        // twenty-eight and the seventh is not due until thirty.
        sim.Run(28e-3);

        Assert.Equal(6, terminal.MessagesSent);
        Assert.Equal("hihihihihihi", device.ReceivedText);
    }

    /// <summary>Left at zero it says its piece once, as a one-shot should.</summary>
    [Fact]
    public void WithoutARepeatIntervalItSendsOnce()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(3.3));
        var gnd = circuit.Add(new Ground());

        var terminal = circuit.Add(new SerialTerminal { Message = "hi", StartDelay = 500e-6 });
        var device = circuit.Add(new SerialDevice { Greeting = "", Echo = false });

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(terminal.Vcc, rail.Positive);
        circuit.Connect(terminal.Gnd, gnd.Pin);
        circuit.Connect(device.Vcc, rail.Positive);
        circuit.Connect(device.Gnd, gnd.Pin);
        circuit.Connect(terminal.Transmit, device.Receive);
        circuit.Connect(device.Transmit, terminal.Receive);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 2e-6, MaxTimeStep = 5e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(26e-3);

        Assert.Equal(1, terminal.MessagesSent);
        Assert.Equal("hi", device.ReceivedText);
    }
}
