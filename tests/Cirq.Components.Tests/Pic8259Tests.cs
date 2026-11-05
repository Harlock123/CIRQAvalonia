using System.Text;
using Cirq.Components.Digital;
using Cirq.Components.Processors;
using Cirq.Core.Digital;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The 8259A on the 8085 board, at ports $60 and $61, its INT on INTR and /INTA from the
/// processor: the CALL in three acknowledges at intervals of 4 and 8, nesting and the EOIs,
/// automatic EOI, the mask, edge- and level-triggered requests, reading the IRR, ISR and IMR, a
/// poll, priority rotation, a master and a slave in cascade, and — with /INTA from a toggle — the
/// 8086's two-pulse vector and a spurious IR7.
/// </summary>
public class Pic8259Tests
{
    private sealed class Board
    {
        public Cpu8085CircuitTests.Computer Computer { get; }

        public Pic8259 Pic { get; }

        public LogicToggle[] Requests { get; } = new LogicToggle[8];

        public LogicToggle? Acknowledge { get; }

        public CircuitSimulator Sim => Computer.Simulator;

        /// <param name="rom">The program.</param>
        /// <param name="cpuAcknowledges">/INTA from the processor and INT on its INTR; otherwise /INTA from a toggle and INT left alone.</param>
        public Board(byte[] rom, bool cpuAcknowledges = true)
        {
            Computer = new Cpu8085CircuitTests.Computer(rom);
            Pic = Attach(new Pic8259(), Computer.Select60, master: true);
            if (cpuAcknowledges)
            {
                Computer.Circuit.Connect(Pic.Interrupt, Computer.Cpu.Intr);
                Computer.Circuit.Connect(Pic.InterruptAcknowledge, Computer.Cpu.InterruptAcknowledge);
            }
            else
            {
                Acknowledge = Computer.Toggle(Pic.InterruptAcknowledge, true);
            }

            for (var i = 0; i < 8; i++) Requests[i] = Computer.Toggle(Pic.Requests[i], false);
        }

        public Pic8259 Attach(Pic8259 pic, Terminal select, bool master)
        {
            var c = Computer;
            c.Ic(pic);
            for (var i = 0; i < 8; i++) c.Circuit.Connect(pic.Data[i], c.Cpu.AddressData[i]);
            c.Circuit.Connect(pic.Address0, c.LowAddress[0]);
            c.Circuit.Connect(pic.ChipSelect, select);
            c.Circuit.Connect(pic.ReadStrobe, c.Cpu.ReadStrobe);
            c.Circuit.Connect(pic.WriteStrobe, c.Cpu.WriteStrobe);
            c.Circuit.Connect(pic.SlaveProgram, master ? c.Rail.Positive : c.Ground.Pin);
            return pic;
        }

        public byte Ram(int address) => Computer.Ram.ReadByte(address & 0x7FFF);

        /// <summary>The bytes the handlers logged, entries as the IR number and exits with bit 7 set.</summary>
        public byte[] Log()
        {
            var end = Ram(0x801E) | (Ram(0x801F) << 8);
            return [.. Enumerable.Range(0x8020, Math.Max(0, end - 0x8020)).Select(a => Ram(a))];
        }

        /// <summary>The byte the 8259A drives on D0-D7, or null while it lets them go.</summary>
        public int? Driven()
        {
            var value = 0;
            for (var i = 0; i < 8; i++)
            {
                var state = Pic.GetOutputState(i);
                if (state == LogicState.HighImpedance) return null;
                if (state == LogicState.High) value |= 1 << i;
            }

            return value;
        }
    }

    /// <summary>
    /// A program with a handler for each IR that logs its entry and its exit, does EI in between (and
    /// <paramref name="extra"/>), and ends with <paramref name="eoi"/> — a non-specific EOI unless
    /// given; the handlers' table at <paramref name="table"/>, <paramref name="interval"/> bytes apart.
    /// </summary>
    private static byte[] Program(string init, string eoi = "mvi a,$20\n        out $60", Func<int, string>? extra = null,
        int table = 0x0200, int interval = 4, string main = "loop:   jmp loop")
    {
        var source = new StringBuilder();
        source.AppendLine("        jmp start");
        for (var n = 0; n < 8; n++) source.AppendLine($"        org ${table + n * interval:X4}\n        jmp h{n}");
        source.AppendLine("        org $0400");
        for (var n = 0; n < 8; n++)
        {
            source.AppendLine($"""
                h{n}:     push psw
                        push h
                        mvi a,{n}
                        call log
                        ei
                        {extra?.Invoke(n) ?? ""}
                        di
                        mvi a,${0x80 + n:X2}
                        call log
                        {eoi.Replace("N", n.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)}
                        pop h
                        pop psw
                        ei
                        ret
                """);
        }

        source.AppendLine($"""
            log:    lhld $801E
                    mov m,a
                    inx h
                    shld $801E
                    ret
            start:  lxi sp,$0000
                    lxi h,$8020
                    shld $801E
                    {init}
            {main}
            """);
        return Assembler8085.Assemble(source.ToString()).Image(0, 0x8000);
    }

    /// <summary>ICW1 $16 (single, interval 4, edge), ICW2 $02, the mask cleared, interrupts on.</summary>
    private const string Init4 = """
                mvi a,$16
                out $60
                mvi a,$02
                out $61
                xra a
                out $61
                ei
        """;

    private static void RunUntil(Board b, Func<bool> done, double limit = 2e-3)
    {
        for (var t = 0.0; t < limit && !done(); t += 5e-6) b.Sim.Run(5e-6);
    }

    /// <summary>
    /// IR0: three /INTA cycles carry $CD, $00 and $02 — CALL $0200 — the handler runs, and its EOI
    /// takes the level out of service.
    /// </summary>
    [Fact]
    public void TheAcknowledgeIsACallInThreeInta()
    {
        var b = new Board(Program(Init4));
        b.Sim.Run(80e-6);
        Assert.True(b.Pic.Initialised);
        Assert.Equal(0x02, b.Pic.Icw2);

        b.Requests[0].State = true;
        RunUntil(b, () => b.Log().Length >= 2);
        Assert.Equal([0x00, 0x80], b.Log());
        Assert.Equal([0xCD, 0x00, 0x02], b.Computer.Cpu.Core.LastAcknowledgeBytes);
        Assert.Equal([0xCD, 0x00, 0x02], b.Computer.Cpu.BusLog.Where(c => c.Kind == I8085CycleKind.InterruptAcknowledge).Select(c => c.Data).TakeLast(3));
        b.Sim.Run(20e-6);
        Assert.Equal(0, b.Pic.Isr);
        Assert.Equal(1, b.Pic.Acknowledges);
        Assert.Equal(0x0200, b.Pic.CallAddress(0));
        Assert.Equal(0, b.Computer.Cpu.UndefinedReads);
    }

    /// <summary>Interval 8 with A7 from ICW1: IR3 calls $0298.</summary>
    [Fact]
    public void IntervalEightTakesA7AndA6FromIcw1()
    {
        var b = new Board(Program("""
                    mvi a,$92
                    out $60
                    mvi a,$02
                    out $61
                    xra a
                    out $61
                    ei
            """, table: 0x0280, interval: 8));
        b.Sim.Run(80e-6);
        b.Requests[3].State = true;
        RunUntil(b, () => b.Log().Length >= 2);
        Assert.Equal([0x03, 0x83], b.Log());
        Assert.Equal([0xCD, 0x98, 0x02], b.Computer.Cpu.Core.LastAcknowledgeBytes);
        Assert.Equal(8, b.Pic.CallInterval);
    }

    /// <summary>
    /// Fully nested: IR0 breaks into IR2's handler once it has done EI; IR5, lower, waits until
    /// IR2's EOI. The log reads 2, 0, end of 0, end of 2, 5, end of 5.
    /// </summary>
    [Fact]
    public void AHigherLevelNestsAndALowerOneWaits()
    {
        var b = new Board(Program(Init4, extra: n => n == 2 ? "mvi b,80\ndl:     dcr b\n        jnz dl" : ""));
        b.Sim.Run(80e-6);
        b.Requests[2].State = true;
        RunUntil(b, () => b.Log().Length >= 1);
        Assert.Equal(0x04, b.Pic.Isr);
        b.Sim.Run(20e-6);
        b.Requests[5].State = true;
        b.Requests[0].State = true;
        RunUntil(b, () => b.Log().Length >= 6);
        Assert.Equal([0x02, 0x00, 0x80, 0x82, 0x05, 0x85], b.Log());
        b.Sim.Run(20e-6);
        Assert.Equal(0, b.Pic.Isr);
    }

    /// <summary>A specific EOI ends the level it names: one for IR4 leaves IR3 in service; one for IR3 ends it.</summary>
    [Theory]
    [InlineData("mvi a,$64\n        out $60", 0x08)]
    [InlineData("mvi a,$63\n        out $60", 0x00)]
    public void ASpecificEoiEndsTheLevelItNames(string eoi, int isr)
    {
        var b = new Board(Program(Init4, eoi: eoi));
        b.Sim.Run(80e-6);
        b.Requests[3].State = true;
        RunUntil(b, () => b.Log().Length >= 2);
        b.Sim.Run(20e-6);
        Assert.Equal(isr, b.Pic.Isr);
    }

    /// <summary>The handler reads the ISR: its level in service — unless AEOI ended it as the third /INTA did.</summary>
    [Theory]
    [InlineData(false, 0x01)]
    [InlineData(true, 0x00)]
    public void AutomaticEoiEndsTheServiceAtTheThirdInta(bool aeoi, int isr)
    {
        var init = aeoi
            ? "mvi a,$17\n        out $60\n        mvi a,$02\n        out $61\n        mvi a,$02\n        out $61\n        xra a\n        out $61\n        ei"
            : Init4;
        var b = new Board(Program(init, eoi: aeoi ? "" : "mvi a,$20\n        out $60",
            extra: _ => "mvi a,$0B\n        out $60\n        in $60\n        sta $8010"));
        b.Sim.Run(80e-6);
        Assert.Equal(aeoi, b.Pic.AutomaticEoi);
        b.Requests[0].State = true;
        RunUntil(b, () => b.Log().Length >= 2);
        Assert.Equal(isr, b.Ram(0x8010));
        b.Sim.Run(20e-6);
        Assert.Equal(0, b.Pic.Isr);
    }

    /// <summary>OCW1 masks IR1: its request stays in the IRR with INT low until the mask is cleared.</summary>
    [Fact]
    public void TheMaskHoldsARequestOff()
    {
        var b = new Board(Program("""
                    mvi a,$16
                    out $60
                    mvi a,$02
                    out $61
                    mvi a,$02
                    out $61
                    in $61
                    sta $8012
                    ei
            """, main: """
            wait:   lda $8011
                    ora a
                    jz wait
                    xra a
                    out $61
            idle:   jmp idle
            """));
        b.Sim.Run(80e-6);
        Assert.Equal(0x02, b.Ram(0x8012));
        Assert.Equal(0x02, b.Pic.Imr);
        b.Requests[1].State = true;
        b.Sim.Run(60e-6);
        Assert.Empty(b.Log());
        Assert.Equal(0x02, b.Pic.Irr);
        Assert.False(b.Pic.Interrupting);

        b.Computer.Ram.WriteByte(0x0011, 1);
        RunUntil(b, () => b.Log().Length >= 2);
        Assert.Equal([0x01, 0x81], b.Log());
    }

    /// <summary>A request held high is taken once when edge-triggered, again and again after each EOI when level-triggered.</summary>
    [Theory]
    [InlineData(0x16, false)]
    [InlineData(0x1E, true)]
    public void EdgeAndLevelTriggering(int icw1, bool again)
    {
        var b = new Board(Program($"mvi a,${icw1:X2}\n        out $60\n        mvi a,$02\n        out $61\n        xra a\n        out $61\n        ei"));
        b.Sim.Run(80e-6);
        Assert.Equal(again, b.Pic.LevelTriggered);
        b.Requests[4].State = true;
        b.Sim.Run(300e-6);
        var entries = b.Log().Count(x => x == 0x04);
        if (again) Assert.True(entries >= 3, $"{entries} entries");
        else Assert.Equal(1, entries);
    }

    /// <summary>With the processor's interrupts off: OCW3 reads the IRR and the ISR, IN $61 the IMR, and a poll takes the request as /INTA would.</summary>
    [Fact]
    public void RegistersAndAPoll()
    {
        var b = new Board(Program("""
                    mvi a,$16
                    out $60
                    mvi a,$02
                    out $61
                    mvi a,$40
                    out $61
            """, main: """
            wait:   lda $8011
                    ora a
                    jz wait
                    mvi a,$0A
                    out $60
                    in $60
                    sta $8030
                    mvi a,$0C
                    out $60
                    in $60
                    sta $8031
                    mvi a,$0B
                    out $60
                    in $60
                    sta $8032
                    mvi a,$0C
                    out $60
                    in $60
                    sta $8033
                    in $61
                    sta $8034
            idle:   jmp idle
            """));
        b.Sim.Run(80e-6);
        b.Requests[4].State = true;
        b.Requests[6].State = true;     // masked
        b.Sim.Run(10e-6);
        b.Computer.Ram.WriteByte(0x0011, 1);
        b.Sim.Run(150e-6);

        Assert.Equal(0x50, b.Ram(0x8030));      // IRR: IR4 and IR6, masked or not
        Assert.Equal(0x84, b.Ram(0x8031));      // poll: IR4
        Assert.Equal(0x10, b.Ram(0x8032));      // ISR: the poll put it in service
        Assert.Equal(0x00, b.Ram(0x8033));      // nothing else unmasked
        Assert.Equal(0x40, b.Ram(0x8034));
        Assert.Empty(b.Log());
    }

    /// <summary>Set priority makes IR4 the highest, so IR5 beats IR2; a rotating EOI then makes IR5 the lowest, and IR2 wins.</summary>
    [Fact]
    public void PriorityRotates()
    {
        var b = new Board(Program("""
                    mvi a,$16
                    out $60
                    mvi a,$02
                    out $61
                    xra a
                    out $61
            """, main: """
            wait:   lda $8011
                    ora a
                    jz wait
                    mvi a,$C3
                    out $60
                    mvi a,$0C
                    out $60
                    in $60
                    sta $8030
                    mvi a,$A0
                    out $60
                    mvi a,$0C
                    out $60
                    in $60
                    sta $8031
            idle:   jmp idle
            """));
        b.Sim.Run(80e-6);
        b.Requests[2].State = true;
        b.Requests[5].State = true;
        b.Sim.Run(10e-6);
        b.Computer.Ram.WriteByte(0x0011, 1);
        b.Sim.Run(120e-6);

        Assert.Equal(0x85, b.Ram(0x8030));
        Assert.Equal(0x82, b.Ram(0x8031));
        Assert.Equal(5, b.Pic.LowestPriority);
        Assert.Equal(0x04, b.Pic.Isr);
    }

    /// <summary>
    /// A master with a slave on IR2: the slave's IR1 reaches the processor as CALL $0304 — the
    /// master sends the CALL and puts 2 on CAS0-CAS2, the slave the address.
    /// </summary>
    [Fact]
    public void AMasterAndASlaveInCascade()
    {
        var rom = Program("""
                    mvi a,$14
                    out $60
                    mvi a,$02
                    out $61
                    mvi a,$04
                    out $61
                    xra a
                    out $61
                    mvi a,$14
                    out $40
                    mvi a,$03
                    out $41
                    mvi a,$02
                    out $41
                    xra a
                    out $41
                    ei
            """, table: 0x0300, eoi: "mvi a,$20\n        out $40\n        out $60");
        var b = new Board(rom);
        var slave = b.Attach(new Pic8259(), b.Computer.Select40, master: false);
        b.Computer.Circuit.Connect(slave.InterruptAcknowledge, b.Computer.Cpu.InterruptAcknowledge);
        for (var i = 0; i < 3; i++) b.Computer.Circuit.Connect(slave.Cascade[i], b.Pic.Cascade[i]);
        b.Computer.Circuit.Remove(b.Requests[2]);
        b.Computer.Circuit.Connect(slave.Interrupt, b.Pic.Requests[2]);
        var request = b.Computer.Toggle(slave.Requests[1], false);

        b.Sim.Run(100e-6);
        Assert.True(slave.IsSlave);
        Assert.False(b.Pic.IsSlave);
        Assert.True(slave.Initialised && b.Pic.Initialised);

        request.State = true;
        RunUntil(b, () => b.Log().Length >= 2);
        Assert.Equal([0xCD, 0x04, 0x03], b.Computer.Cpu.Core.LastAcknowledgeBytes);
        Assert.Equal([0x01, 0x81], b.Log());
        b.Sim.Run(20e-6);
        Assert.Equal(0, slave.Isr);
        Assert.Equal(0, b.Pic.Isr);
        Assert.Equal(0, b.Computer.Cpu.UndefinedReads);
    }

    /// <summary>The program ends halted with the bus floating, so a toggle can play /INTA.</summary>
    private static byte[] Halting(string init) => Assembler8085.Assemble($"""
                org $0000
                lxi sp,$0000
                {init}
                hlt
        """).Image(0, 0x8000);

    /// <summary>8086 mode: two /INTA pulses, nothing on the bus for the first and the vector — T7-T3 with the level — on the second.</summary>
    [Fact]
    public void Mode8086GivesAVectorOnTheSecondPulse()
    {
        var b = new Board(Halting("""
                    mvi a,$13
                    out $60
                    mvi a,$40
                    out $61
                    mvi a,$01
                    out $61
                    xra a
                    out $61
            """), cpuAcknowledges: false);
        b.Sim.Run(60e-6);
        Assert.True(b.Pic.Mode8086);
        b.Requests[6].State = true;
        b.Sim.Run(1e-6);
        Assert.True(b.Pic.Interrupting);

        b.Acknowledge!.State = false;
        b.Sim.Run(0.5e-6);
        Assert.Null(b.Driven());
        b.Acknowledge.State = true;
        b.Sim.Run(0.5e-6);
        Assert.Equal(0x40, b.Pic.Isr);
        b.Acknowledge.State = false;
        b.Sim.Run(0.5e-6);
        Assert.Equal(0x46, b.Driven());
        b.Acknowledge.State = true;
        b.Sim.Run(0.5e-6);
        Assert.Null(b.Driven());
        Assert.Equal(0x46, b.Pic.Vector(6));
    }

    /// <summary>A request gone before the first /INTA: the 8259A answers as IR7 and puts nothing in service.</summary>
    [Fact]
    public void ARequestGoneIsASpuriousIr7()
    {
        var b = new Board(Halting("""
                    mvi a,$16
                    out $60
                    mvi a,$02
                    out $61
                    xra a
                    out $61
            """), cpuAcknowledges: false);
        b.Sim.Run(60e-6);
        b.Requests[3].State = true;
        b.Sim.Run(1e-6);
        b.Requests[3].State = false;
        b.Sim.Run(1e-6);

        List<int?> bytes = [];
        for (var i = 0; i < 3; i++)
        {
            b.Acknowledge!.State = false;
            b.Sim.Run(0.5e-6);
            bytes.Add(b.Driven());
            b.Acknowledge.State = true;
            b.Sim.Run(0.5e-6);
        }

        Assert.Equal([0xCD, 0x1C, 0x02], bytes);
        Assert.Equal(1, b.Pic.Spurious);
        Assert.Equal(0, b.Pic.Isr);
    }
}
