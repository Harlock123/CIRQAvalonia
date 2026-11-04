using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Processors;
using Cirq.Core.Digital;

namespace Cirq.Components.Tests;

/// <summary>
/// The 6510: the 6502's core on its own pinout, with the six-bit port at $0000/$0001 that a write
/// still puts on the bus and a read takes from inside, and AEC letting go of the buses.
/// </summary>
public class Cpu6510Tests
{
    private static Cpu6502CircuitTests.Computer Run(string body, Processor6510? cpu = null)
    {
        var program = Assembler6502.Assemble($"""
                    .org $8000
            reset:  ldx #$FF
                    txs
            {body}
            done:   jmp done
                    .org $FFFC
                    .word reset
                    .word reset
            """, Cpu6502Variant.Nmos6502);
        return new Cpu6502CircuitTests.Computer(program.Rom32K(), cpu ?? new Cpu6510());
    }

    [Fact]
    public void ThePinoutIsTheDatasheets()
    {
        var c = new Cpu6510();
        string Name(int pin) => c.Terminals.Single(t => t.Id == $"p{pin}").Name;

        Assert.Equal(ProcessorPackage.Mos6510, c.Package);
        Assert.Equal(40, c.Terminals.Count);
        Assert.Equal(["PHI0", "RDY", "IRQ", "NMI", "AEC", "VCC"], Enumerable.Range(1, 6).Select(Name));
        Assert.Equal("A0", Name(7));
        Assert.Equal("A13", Name(20));
        Assert.Equal("VSS", Name(21));
        Assert.Equal("A14", Name(22));
        Assert.Equal("A15", Name(23));
        Assert.Equal(["P5", "P4", "P3", "P2", "P1", "P0"], Enumerable.Range(24, 6).Select(Name));
        Assert.Equal("D7", Name(30));
        Assert.Equal("D0", Name(37));
        Assert.Equal(["RW", "PHI2", "RES"], Enumerable.Range(38, 3).Select(Name));

        Assert.Same(c.Clock, c.Terminals[0]);
        Assert.Equal("p5", c.BusEnable!.Id);
        Assert.Null(c.SyncPin);
        Assert.Null(c.SetOverflow);
        Assert.Equal(6, c.Port.Count);
        Assert.Equal("p29", c.Port[0].Id);
        Assert.Equal("/IRQ", c.PinLabel(c.Irq));
        Assert.Equal("R/W", c.PinLabel(c.ReadWrite));

        Assert.Equal("8500", new Cpu8500().PartNumber);
        Assert.Equal(Cpu6502Variant.Nmos6502, new Cpu8500().Variant);
    }

    /// <summary>
    /// The direction register makes pins outputs and the data register drives them; a read of $0001
    /// gives the register for outputs and the pins for inputs — and the RAM underneath took both
    /// writes, because the 6510 still runs them on the bus.
    /// </summary>
    [Fact]
    public void ThePortDrivesItsOutputsAndReadsItsInputsWhileTheBusTakesTheWrites()
    {
        var computer = Run("""
                    lda #$2F        ; P0-P3 and P5 out, P4 in: the C64's setting
                    sta $00
                    lda #$37
                    sta $01
                    lda $01         ; the port, not the RAM
                    sta $0200
                    lda $00
                    sta $0201
            """);
        var cpu = (Cpu6510)computer.Cpu;
        computer.Circuit.Connect(cpu.Port[4], computer.Ground.Pin);   // the cassette sense line, pressed
        var sim = computer.Simulator;
        sim.Run(60e-6);

        Assert.Equal(0x2F, cpu.PortDirection);
        Assert.Equal(0x37, cpu.PortOutput);
        Assert.Equal(0x27, computer.Ram.ReadByte(0x200));   // P4 held low reads as a zero
        Assert.Equal(0x2F, computer.Ram.ReadByte(0x201));
        Assert.Equal(0x2F, computer.Ram.ReadByte(0x00));    // the bus cycles landed in the RAM too
        Assert.Equal(0x37, computer.Ram.ReadByte(0x01));

        bool High(int bit) => sim.NodeVoltage(cpu.Port[bit]) > 2.4;
        Assert.True(High(0) && High(1) && High(2) && High(5));
        Assert.False(High(3));
        Assert.Equal(LogicState.HighImpedance, cpu.GetOutputState(cpu.OutputTerminals.ToList().IndexOf(cpu.Port[4])));

        // The bus log records what the processor took: the port's value, not the RAM's.
        Assert.Contains(cpu.BusLog, c => c.IsRead && c.Address == 0x0001 && c.Data == 0x27);
        Assert.Equal(0, cpu.UndefinedReads);
    }

    /// <summary>Reset clears both registers, so every port pin goes back to being an input.</summary>
    [Fact]
    public void ResetLetsGoOfThePort()
    {
        var computer = Run("""
                    lda #$FF
                    sta $00
                    sta $01
            """);
        var cpu = (Cpu6510)computer.Cpu;
        var button = computer.Circuit.Add(new PushButton());
        computer.Circuit.Connect(button.A, cpu.Reset);
        computer.Circuit.Connect(button.B, computer.Ground.Pin);
        var sim = computer.Simulator;

        sim.Run(40e-6);
        Assert.Equal(0xFF, cpu.PortDirection);
        Assert.True(sim.NodeVoltage(cpu.Port[2]) > 2.4);

        button.IsPressed = true;
        sim.Run(5e-6);
        Assert.Equal(0, cpu.PortDirection);
        Assert.Equal(LogicState.HighImpedance, cpu.GetOutputState(cpu.OutputTerminals.ToList().IndexOf(cpu.Port[2])));
    }

    /// <summary>AEC low lets go of the address bus, R/W and the data bus; high, they are driven again.</summary>
    [Fact]
    public void AecReleasesTheBuses()
    {
        var computer = Run("""
            loop:   inc $10
                    jmp loop
            """);
        var cpu = computer.Cpu;
        var aec = computer.Circuit.Add(new LogicToggle { State = true });
        computer.Circuit.Connect(aec.Out, cpu.BusEnable!);
        var sim = computer.Simulator;
        var outputs = cpu.OutputTerminals.ToList();

        sim.Run(30e-6);
        Assert.NotEqual(LogicState.HighImpedance, cpu.GetOutputState(outputs.IndexOf(cpu.Address[15])));

        aec.State = false;
        sim.Run(3e-6);
        Assert.All(cpu.Address, a => Assert.Equal(LogicState.HighImpedance, cpu.GetOutputState(outputs.IndexOf(a))));
        Assert.All(cpu.Data, d => Assert.Equal(LogicState.HighImpedance, cpu.GetOutputState(outputs.IndexOf(d))));
        Assert.Equal(LogicState.HighImpedance, cpu.GetOutputState(outputs.IndexOf(cpu.ReadWrite)));

        aec.State = true;
        sim.Run(3e-6);
        Assert.NotEqual(LogicState.HighImpedance, cpu.GetOutputState(outputs.IndexOf(cpu.Address[15])));
        Assert.NotEqual(LogicState.HighImpedance, cpu.GetOutputState(outputs.IndexOf(cpu.ReadWrite)));
    }

    /// <summary>The 6502 bus decode reads a 6510's cycles as it does a 6502's: they share the core.</summary>
    [Fact]
    public void TheBusDecoderReadsTheSharedCore()
    {
        var computer = Run("""
                    lda #$07
                    sta $01
            """, new Cpu8500());
        computer.Simulator.Run(30e-6);

        var decoded = Cpu6502BusDecoder.Decode(computer.Cpu.Variant, computer.Cpu.BusLog);
        Assert.Contains(decoded.Spans, s => s.Text == "$8003 LDA #$07");
        Assert.Contains(decoded.Spans, s => s.Text == "$8005 STA $01");
        Assert.Contains(decoded.Spans, s => s.Text == "W $0001 07");
        Assert.Equal(0x07, ((Processor6510)computer.Cpu).PortOutput);
    }
}
