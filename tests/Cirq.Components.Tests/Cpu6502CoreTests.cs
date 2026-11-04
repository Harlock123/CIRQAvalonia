using Cirq.Components.Processors;

namespace Cirq.Components.Tests;

/// <summary>
/// The 6502 core on its own, against a 64K array: instruction results, the exact bus cycles of
/// each addressing mode on both variants, decimal mode, interrupts and reset — and a self-checking
/// program assembled from source.
/// </summary>
public class Cpu6502CoreTests
{
    /// <summary>A core and its memory, and the cycles it has performed as <c>R1234=56</c> or <c>W0100=12</c>.</summary>
    private sealed class Machine
    {
        public byte[] Memory { get; } = new byte[0x10000];

        public Mos6502Core Core { get; }

        public List<string> Trace { get; } = [];

        public Machine(Cpu6502Variant variant, ushort pc, params byte[] program)
        {
            Core = new Mos6502Core(variant);
            Array.Copy(program, 0, Memory, pc, program.Length);
            Core.Load(pc, 0, 0, 0, 0xFD, 0x24);
        }

        public static Machine Nmos(params byte[] program) => new(Cpu6502Variant.Nmos6502, 0x0400, program);

        public static Machine Cmos(params byte[] program) => new(Cpu6502Variant.Wdc65C02, 0x0400, program);

        /// <summary>One cycle.</summary>
        public void Cycle()
        {
            var c = Core;
            if (c.IsRead)
            {
                Trace.Add($"R{c.Address:X4}={Memory[c.Address]:X2}");
                c.Complete(Memory[c.Address]);
            }
            else
            {
                Trace.Add($"W{c.Address:X4}={c.DataOut:X2}");
                Memory[c.Address] = c.DataOut;
                c.Complete();
            }
        }

        /// <summary>One instruction; returns its cycles as a string.</summary>
        public string Step()
        {
            Trace.Clear();
            do Cycle();
            while (!Core.Sync && Trace.Count < 50);
            return string.Join(" ", Trace);
        }
    }

    // ---- results ------------------------------------------------------------

    [Theory]
    [InlineData(0x58, 0x46, false, 0x9E, false, true, true)]    // binary: two positives overflow negative
    [InlineData(0xFF, 0x01, false, 0x00, true, false, false)]
    [InlineData(0x80, 0x80, true, 0x01, true, true, false)]
    public void AdcInBinarySetsCarryAndOverflow(int a, int operand, bool carryIn, int result, bool carry, bool overflow, bool negative)
    {
        var m = Machine.Nmos(0xA9, (byte)a, carryIn ? (byte)0x38 : (byte)0x18, 0x69, (byte)operand);
        m.Step();
        m.Step();
        m.Step();

        Assert.Equal(result, m.Core.A);
        Assert.Equal(carry, (m.Core.P & Mos6502Core.FlagC) != 0);
        Assert.Equal(overflow, (m.Core.P & Mos6502Core.FlagV) != 0);
        Assert.Equal(negative, (m.Core.P & Mos6502Core.FlagN) != 0);
    }

    /// <summary>
    /// 99 + 1 in decimal is 00 carry one on both parts — but the NMOS part's Z comes from the binary
    /// sum ($9A), so it says not-zero, where the 65C02 says zero and takes a cycle longer to.
    /// </summary>
    [Fact]
    public void DecimalAdditionCarriesAndTheVariantsDisagreeAboutZero()
    {
        byte[] program = [0xF8, 0x38, 0xA9, 0x99, 0x69, 0x00];   // SED, SEC, LDA #$99, ADC #$00

        var nmos = Machine.Nmos(program);
        var cmos = Machine.Cmos(program);
        foreach (var m in new[] { nmos, cmos })
        {
            m.Step();
            m.Step();
            m.Step();
        }

        var nmosCycles = nmos.Step().Split(' ').Length;
        var cmosCycles = cmos.Step().Split(' ').Length;

        Assert.Equal(0x00, nmos.Core.A);
        Assert.Equal(0x00, cmos.Core.A);
        Assert.True((nmos.Core.P & Mos6502Core.FlagC) != 0);
        Assert.True((cmos.Core.P & Mos6502Core.FlagC) != 0);
        Assert.False((nmos.Core.P & Mos6502Core.FlagZ) != 0);
        Assert.True((cmos.Core.P & Mos6502Core.FlagZ) != 0);
        Assert.Equal(2, nmosCycles);
        Assert.Equal(3, cmosCycles);
    }

    [Fact]
    public void DecimalSubtractionBorrows()
    {
        var m = Machine.Cmos(0xF8, 0x38, 0xA9, 0x10, 0xE9, 0x01);   // SED, SEC, LDA #$10, SBC #$01
        for (var i = 0; i < 4; i++) m.Step();
        Assert.Equal(0x09, m.Core.A);
        Assert.True((m.Core.P & Mos6502Core.FlagC) != 0);
    }

    // ---- bus cycles -----------------------------------------------------------

    /// <summary>
    /// LDA abs,X across a page: the NMOS part reads the unfixed address first ($04FF + 2 read as
    /// $0401); the 65C02 rereads the instruction's last byte instead. Both take five cycles.
    /// </summary>
    [Fact]
    public void AnIndexedReadAcrossAPageSpendsACycleAndTheVariantsReadDifferentDummies()
    {
        byte[] program = [0xA2, 0x02, 0xBD, 0xFF, 0x04];   // LDX #2, LDA $04FF,X

        var nmos = Machine.Nmos(program);
        nmos.Step();
        Assert.Equal("R0402=BD R0403=FF R0404=04 R0401=02 R0501=00", nmos.Step());

        var cmos = Machine.Cmos(program);
        cmos.Step();
        Assert.Equal("R0402=BD R0403=FF R0404=04 R0404=04 R0501=00", cmos.Step());
    }

    /// <summary>A read-modify-write: the NMOS part writes the old value back before the new; the 65C02 reads it again.</summary>
    [Fact]
    public void ReadModifyWriteHasADummyCycleOfEachKind()
    {
        byte[] program = [0xEE, 0x00, 0x02];   // INC $0200

        var nmos = Machine.Nmos(program);
        nmos.Memory[0x200] = 0x41;
        Assert.Equal("R0400=EE R0401=00 R0402=02 R0200=41 W0200=41 W0200=42", nmos.Step());

        var cmos = Machine.Cmos(program);
        cmos.Memory[0x200] = 0x41;
        Assert.Equal("R0400=EE R0401=00 R0402=02 R0200=41 R0200=41 W0200=42", cmos.Step());
    }

    /// <summary>JMP ($02FF) takes its high byte from $0200 on the NMOS part, and from $0300 — a cycle later — on the 65C02.</summary>
    [Fact]
    public void TheIndirectJumpPageBugIsNmosOnly()
    {
        byte[] program = [0x6C, 0xFF, 0x02];

        var nmos = Machine.Nmos(program);
        nmos.Memory[0x2FF] = 0x34;
        nmos.Memory[0x200] = 0x12;
        nmos.Memory[0x300] = 0x56;
        nmos.Step();
        Assert.Equal(0x1234, nmos.Core.PC);

        var cmos = Machine.Cmos(program);
        Array.Copy(nmos.Memory, cmos.Memory, 0x400);
        Assert.Equal(6, cmos.Step().Split(' ').Length);
        Assert.Equal(0x5634, cmos.Core.PC);
    }

    [Fact]
    public void JsrAndRtsPushAndPullTheReturnAddressMinusOne()
    {
        var m = Machine.Nmos(0x20, 0x00, 0x05);   // JSR $0500
        m.Memory[0x500] = 0x60;                   // RTS

        Assert.Equal("R0400=20 R0401=00 R01FD=00 W01FD=04 W01FC=02 R0402=05", m.Step());
        Assert.Equal(0x0500, m.Core.PC);
        Assert.Equal(0xFB, m.Core.S);

        Assert.Equal("R0500=60 R0501=00 R01FB=00 R01FC=02 R01FD=04 R0402=05", m.Step());
        Assert.Equal(0x0403, m.Core.PC);
    }

    [Fact]
    public void BrkPushesWithBSetAndTheCmosPartClearsDecimal()
    {
        var m = Machine.Cmos(0xF8, 0x00, 0xEA);   // SED, BRK
        m.Memory[0xFFFE] = 0x00;
        m.Memory[0xFFFF] = 0x06;
        m.Step();

        Assert.Equal("R0401=00 R0402=EA W01FD=04 W01FC=03 W01FB=3C RFFFE=00 RFFFF=06", m.Step());
        Assert.Equal(0x0600, m.Core.PC);
        Assert.Equal(0, m.Core.P & Mos6502Core.FlagD);
        Assert.NotEqual(0, m.Core.P & Mos6502Core.FlagI);
    }

    // ---- the 65C02's additions, and the NMOS part's undocumented opcodes ----------

    [Fact]
    public void The65C02InstructionsDoWhatTheDatasheetSays()
    {
        var m = Machine.Cmos(
            0xA9, 0x0F,         // LDA #$0F
            0x04, 0x10,         // TSB $10
            0x64, 0x11,         // STZ $11
            0x87, 0x11,         // SMB0 $11
            0x0F, 0x11, 0x02,   // BBR0 $11,+2 (not taken: bit 0 set)
            0xDA,               // PHX
            0x80, 0x01,         // BRA +1
            0xEA,               // (skipped)
            0x1A);              // INC A
        m.Memory[0x10] = 0xF0;
        m.Memory[0x11] = 0x55;

        for (var i = 0; i < 8; i++) m.Step();

        Assert.Equal(0xFF, m.Memory[0x10]);
        Assert.Equal(0x01, m.Memory[0x11]);
        Assert.Equal(0x10, m.Core.A);
        Assert.Equal(0xFC, m.Core.S);
    }

    [Fact]
    public void TheUndocumentedNmosOpcodesRun()
    {
        var m = Machine.Nmos(
            0xA7, 0x10,         // LAX $10
            0x87, 0x11,         // SAX $11 (A & X)
            0xC7, 0x12);        // DCP $12 (DEC then CMP)
        m.Memory[0x10] = 0x3C;
        m.Memory[0x12] = 0x3D;

        m.Step();
        Assert.Equal(0x3C, m.Core.A);
        Assert.Equal(0x3C, m.Core.X);

        m.Step();
        Assert.Equal(0x3C, m.Memory[0x11]);

        m.Step();
        Assert.Equal(0x3C, m.Memory[0x12]);
        Assert.NotEqual(0, m.Core.P & Mos6502Core.FlagZ);
    }

    // ---- interrupts and reset ------------------------------------------------------

    [Fact]
    public void PowerOnRunsTheResetSequenceToTheVector()
    {
        var core = new Mos6502Core(Cpu6502Variant.Wdc65C02);
        var memory = new byte[0x10000];
        memory[0xFFFC] = 0x00;
        memory[0xFFFD] = 0x80;

        List<ushort> addresses = [];
        while (!core.Sync)
        {
            addresses.Add(core.Address);
            core.Complete(memory[core.Address]);
        }

        Assert.Equal(0x8000, core.Address);
        Assert.Equal([0xFFFC, 0xFFFD], addresses.TakeLast(2).Select(a => (int)a));
        Assert.Equal(8, addresses.Count);   // the reset hold's cycle, then the seven
        Assert.NotEqual(0, core.P & Mos6502Core.FlagI);
    }

    /// <summary>
    /// An IRQ is a level: masked by I, taken at the end of the instruction once I is clear — and
    /// CLI lets one more instruction run first, because the decision was taken during CLI.
    /// </summary>
    [Fact]
    public void AnIrqIsTakenAfterTheInstructionFollowingCli()
    {
        var m = Machine.Nmos(0x78, 0x58, 0xE8, 0xE8);   // SEI, CLI, INX, INX
        m.Memory[0xFFFE] = 0x00;
        m.Memory[0xFFFF] = 0x07;
        m.Core.IrqAsserted = true;

        m.Step();   // SEI
        m.Step();   // CLI
        Assert.Equal(0x0402, m.Core.PC);
        m.Step();   // INX still runs
        Assert.Equal(1, m.Core.X);

        var service = m.Step();
        Assert.Contains("W01FB=20", service);   // P pushed with B clear
        Assert.Equal(0x0700, m.Core.PC);
    }

    /// <summary>NMI is an edge: one falling edge, one interrupt, even with the line held low.</summary>
    [Fact]
    public void AnNmiIsTakenOncePerFallingEdge()
    {
        var m = Machine.Cmos(0xEA, 0xEA, 0xEA, 0xEA);
        m.Memory[0xFFFA] = 0x00;
        m.Memory[0xFFFB] = 0x09;
        m.Memory[0x900] = 0xEA;
        m.Memory[0x901] = 0xEA;

        m.Core.NmiAsserted = true;
        m.Step();   // the NOP during which the edge arrives
        m.Step();   // the interrupt
        Assert.Equal(0x0900, m.Core.PC);
        m.Step();
        m.Step();
        Assert.Equal(0x0902, m.Core.PC);
    }

    [Fact]
    public void WaiSleepsUntilAnInterruptLine()
    {
        var m = Machine.Cmos(0x78, 0xCB, 0xE8);   // SEI, WAI, INX
        m.Step();
        for (var i = 0; i < 10; i++) m.Cycle();
        Assert.True(m.Core.IsWaiting);

        // Masked, so it wakes and simply carries on.
        m.Core.IrqAsserted = true;
        m.Step();
        m.Step();
        Assert.Equal(1, m.Core.X);
    }

    // ---- a whole program --------------------------------------------------------------

    /// <summary>
    /// The program the self-check runs: arithmetic, decimal mode, the stack, a subroutine, an
    /// indirect copy and the shifts, each checked as it goes, ending with $AA (passed) or $FF in
    /// $0200. Documented instructions only, so it runs on both parts.
    /// </summary>
    internal const string SelfCheck = """
                .org $8000
        reset:  ldx #$FF
                txs
                lda #0          ; 10 + 9 + ... + 1 = 55
                ldx #10
                clc
        sum:    stx $10
                adc $10
                dex
                bne sum
                cmp #55
                bne fail
                sed             ; 19 + 28 = 47 in BCD
                clc
                lda #$19
                adc #$28
                cld
                cmp #$47
                bne fail
                lda #$5A        ; the stack and a subroutine
                pha
                jsr double
                cmp #$B4
                bne fail
                pla
                cmp #$5A
                bne fail
                lda #<text      ; copy a string through a zero-page pointer
                sta $20
                lda #>text
                sta $21
                ldy #0
        copy:   lda ($20),y
                beq copied
                sta $0300,y
                iny
                bne copy
        copied: cpy #2
                bne fail
                lda $0301
                cmp #'I'
                bne fail
                lda #$81        ; shifts through the carry
                asl a
                bcc fail
                ror a
                cmp #$81
                bne fail
                lda #$AA
                sta $0200
        done:   jmp done
        fail:   lda #$FF
                sta $0200
        stop:   jmp stop
        double: asl a
                rts
        text:   .asciiz "HI"
                .org $FFFC
                .word reset
                .word reset
        """;

    [Theory]
    [InlineData(Cpu6502Variant.Nmos6502)]
    [InlineData(Cpu6502Variant.Wdc65C02)]
    public void ASelfCheckingProgramPasses(Cpu6502Variant variant)
    {
        var assembled = Assembler6502.Assemble(SelfCheck, variant);
        var memory = new byte[0x10000];
        Array.Copy(assembled.Bytes, 0, memory, assembled.Origin, assembled.Bytes.Length);
        memory[0xFFFC] = 0x00;
        memory[0xFFFD] = 0x80;

        var core = new Mos6502Core(variant);
        var done = assembled.Labels["done"];
        var stop = assembled.Labels["stop"];

        for (var cycle = 0; cycle < 5000 && !(core.Sync && (core.Address == done || core.Address == stop)); cycle++)
        {
            if (core.IsRead) core.Complete(memory[core.Address]);
            else
            {
                memory[core.Address] = core.DataOut;
                core.Complete();
            }
        }

        Assert.Equal(0xAA, memory[0x200]);
        Assert.Equal("HI", $"{(char)memory[0x300]}{(char)memory[0x301]}");
    }

    [Fact]
    public void TheDisassemblerReadsBothVariants()
    {
        byte[] bytes = [0xB2, 0x20, 0x6C, 0x34, 0x12, 0xA7, 0x10, 0xD0, 0xFE];
        byte Read(int a) => a < bytes.Length ? bytes[a] : (byte)0;

        Assert.Equal(("LDA ($20)", 2), Opcodes6502.Disassemble(Cpu6502Variant.Wdc65C02, 0, Read));
        Assert.Equal(("JAM *", 1), Opcodes6502.Disassemble(Cpu6502Variant.Nmos6502, 0, Read));
        Assert.Equal(("JMP ($1234)", 3), Opcodes6502.Disassemble(Cpu6502Variant.Nmos6502, 2, Read));
        Assert.Equal(("LAX $10 *", 2), Opcodes6502.Disassemble(Cpu6502Variant.Nmos6502, 5, Read));
        Assert.Equal(("BNE $0007", 2), Opcodes6502.Disassemble(Cpu6502Variant.Nmos6502, 7, Read));
    }
}
