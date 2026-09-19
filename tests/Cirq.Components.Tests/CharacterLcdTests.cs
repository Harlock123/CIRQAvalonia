using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class CharacterLcdTests
{
    private sealed record Panel(
        CircuitSimulator Sim, CharacterLcd Lcd,
        DcVoltageSource Rs, DcVoltageSource E, DcVoltageSource[] Data);

    private static Panel Rig()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var lcd = circuit.Add(new CharacterLcd());
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, lcd.Vcc);
        circuit.Connect(lcd.Gnd, gnd.Pin);
        circuit.Connect(lcd.ReadWrite, gnd.Pin);          // write only, as nearly everyone does

        DcVoltageSource Drive(Terminal pin)
        {
            var source = circuit.Add(new DcVoltageSource(0.0));
            circuit.Connect(source.Negative, gnd.Pin);
            circuit.Connect(source.Positive, pin);
            return source;
        }

        var rs = Drive(lcd.RegisterSelect);
        var e = Drive(lcd.Enable);
        var data = lcd.Data.Select(Drive).ToArray();

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        return new Panel(sim, lcd, rs, e, data);
    }

    private static void Settle(Panel panel)
    {
        panel.Sim.Run(60e-6);
        panel.Sim.Run(60e-6);
    }

    /// <summary>One byte in eight-bit mode: set it up, then pulse E.</summary>
    private static void Send(Panel panel, bool isData, int value)
    {
        panel.Rs.Voltage = isData ? 5.0 : 0.0;
        for (var i = 0; i < 8; i++) panel.Data[i].Voltage = (value & (1 << i)) != 0 ? 5.0 : 0.0;

        panel.E.Voltage = 5.0;
        Settle(panel);
        panel.E.Voltage = 0.0;
        Settle(panel);
    }

    /// <summary>One byte in four-bit mode: the high nibble on D4-D7, then the low one.</summary>
    private static void SendNibbles(Panel panel, bool isData, int value)
    {
        foreach (var nibble in new[] { (value >> 4) & 0x0F, value & 0x0F })
        {
            panel.Rs.Voltage = isData ? 5.0 : 0.0;

            for (var i = 0; i < 4; i++) panel.Data[i].Voltage = 0.0;
            for (var i = 0; i < 4; i++) panel.Data[4 + i].Voltage = (nibble & (1 << i)) != 0 ? 5.0 : 0.0;

            panel.E.Voltage = 5.0;
            Settle(panel);
            panel.E.Voltage = 0.0;
            Settle(panel);
        }
    }

    private static void Text(Panel panel, string text, Action<Panel, bool, int> send)
    {
        foreach (var c in text) send(panel, true, c);
    }

    [Fact]
    public void ItStartsBlankAndSwitchedOff()
    {
        var panel = Rig();

        Assert.False(panel.Lcd.DisplayOn);
        Assert.Equal("                ", panel.Lcd.Line(0));
        Assert.Equal("                ", panel.Lcd.Line(1));
    }

    /// <summary>
    /// The basic transaction: a command turns the display on, then characters go in where the
    /// cursor is.
    /// </summary>
    [Fact]
    public void CharactersGoInWhereTheCursorIs()
    {
        var panel = Rig();

        Send(panel, false, 0x0C);                        // display on, cursor off
        Text(panel, "HELLO", Send);

        Assert.True(panel.Lcd.DisplayOn);
        Assert.Equal("HELLO           ", panel.Lcd.Line(0));
        Assert.Equal((0, 5), panel.Lcd.Cursor);
    }

    /// <summary>
    /// The controller latches on E falling, not on the level. Setting up the data pins with E
    /// already high changes nothing until E comes back down, which is why every driver pulses it.
    /// </summary>
    [Fact]
    public void NothingIsLatchedUntilEnableFalls()
    {
        var panel = Rig();
        Send(panel, false, 0x0C);

        panel.Rs.Voltage = 5.0;
        for (var i = 0; i < 8; i++) panel.Data[i].Voltage = ((int)'X' & (1 << i)) != 0 ? 5.0 : 0.0;

        panel.E.Voltage = 5.0;
        Settle(panel);

        Assert.Equal("                ", panel.Lcd.Line(0));

        panel.E.Voltage = 0.0;
        Settle(panel);

        Assert.Equal("X               ", panel.Lcd.Line(0));
    }

    /// <summary>Clear wipes both lines and puts the cursor back at the start.</summary>
    [Fact]
    public void ClearWipesTheDisplay()
    {
        var panel = Rig();
        Send(panel, false, 0x0C);
        Text(panel, "SOMETHING", Send);

        Send(panel, false, 0x01);

        Assert.Equal("                ", panel.Lcd.Line(0));
        Assert.Equal((0, 0), panel.Lcd.Cursor);
    }

    /// <summary>
    /// The second line is a separate address, not a continuation. Running off the end of line one
    /// does not wrap onto line two, which is the most surprising thing about these modules.
    /// </summary>
    [Fact]
    public void TheSecondLineIsAnAddressAndNotAContinuation()
    {
        var panel = Rig();
        Send(panel, false, 0x0C);

        Text(panel, "ONE", Send);
        Send(panel, false, 0x80 | 0x40);                 // set DDRAM address to line two
        Text(panel, "TWO", Send);

        Assert.Equal("ONE             ", panel.Lcd.Line(0));
        Assert.Equal("TWO             ", panel.Lcd.Line(1));
    }

    [Fact]
    public void TheCursorCanBePlacedAnywhere()
    {
        var panel = Rig();
        Send(panel, false, 0x0C);

        Send(panel, false, 0x80 | 0x05);
        Text(panel, "AT5", Send);

        Assert.Equal("     AT5        ", panel.Lcd.Line(0));
    }

    /// <summary>Turning the display off leaves what is on it in memory, as a real one does.</summary>
    [Fact]
    public void SwitchingItOffKeepsWhatWasWritten()
    {
        var panel = Rig();
        Send(panel, false, 0x0C);
        Text(panel, "KEPT", Send);

        Send(panel, false, 0x08);

        Assert.False(panel.Lcd.DisplayOn);
        Assert.Equal("KEPT            ", panel.Lcd.Line(0));
    }

    /// <summary>
    /// Four-bit mode, which is how almost every real one is wired. A function set with the
    /// data-length bit clear moves it there, and from then on each byte is two latches.
    /// </summary>
    [Fact]
    public void ItTakesBytesAsNibblePairsInFourBitMode()
    {
        var panel = Rig();

        Assert.False(panel.Lcd.IsFourBitMode);

        // The move to four-bit working is itself sent as a single eight-bit latch.
        Send(panel, false, 0x28);
        Assert.True(panel.Lcd.IsFourBitMode);

        SendNibbles(panel, false, 0x0C);
        Text(panel, "NIBBLES", (p, d, v) => SendNibbles(p, d, v));

        Assert.True(panel.Lcd.DisplayOn);
        Assert.Equal("NIBBLES         ", panel.Lcd.Line(0));
    }

    /// <summary>A half-finished byte in four-bit mode is not acted on until its second nibble.</summary>
    [Fact]
    public void AnUnpairedNibbleDoesNothingOnItsOwn()
    {
        var panel = Rig();
        Send(panel, false, 0x28);
        SendNibbles(panel, false, 0x0C);

        // Only the high nibble of 'A'.
        panel.Rs.Voltage = 5.0;
        for (var i = 0; i < 4; i++) panel.Data[i].Voltage = 0.0;
        for (var i = 0; i < 4; i++) panel.Data[4 + i].Voltage = ((0x4) & (1 << i)) != 0 ? 5.0 : 0.0;
        panel.E.Voltage = 5.0;
        Settle(panel);
        panel.E.Voltage = 0.0;
        Settle(panel);

        Assert.Equal("                ", panel.Lcd.Line(0));
    }
}
