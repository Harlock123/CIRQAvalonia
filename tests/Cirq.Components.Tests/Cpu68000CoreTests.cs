using Cirq.Components.Processors;

namespace Cirq.Components.Tests;

/// <summary>
/// The 68000 core with no circuit: a memory array on its bus. Results and flags, the clocks the
/// user's manual gives, the reset sequence, every exception and its stack frame, interrupts and
/// their mask, STOP, RESET, a self-checking program built by the test-side assembler, and the
/// disassembler — round-tripped through the assembler, and agreeing with the core about which
/// words are instructions at all.
/// </summary>
public class Cpu68000CoreTests
{
    internal sealed class Machine
    {
        public const int Size = 1 << 20;

        public byte[] Memory { get; } = new byte[Size];

        public M68000Core Core { get; } = new();

        public List<(uint Address, bool Write, int Fc, ushort Data)> Cycles { get; } = [];


        /// <summary>The vector an interrupt acknowledge answers with, or null for the autovector.</summary>
        public byte? Vector { get; set; }

        public List<M68000BusAction> Actions { get; } = [];

        public ushort Word(uint address) => (ushort)((Memory[address % Size] << 8) | Memory[(address + 1) % Size]);

        public uint Long(uint address) => (uint)((Word(address) << 16) | Word(address + 2));

        /// <summary>Runs the cycle on offer against the memory: its clocks, four for a bus cycle.</summary>
        public int Cycle()
        {
            var core = Core;
            Actions.Add(core.Action);
            var address = core.Address & 0xFFFFFF;
            switch (core.Action)
            {
                case M68000BusAction.Read:
                    Cycles.Add((address, false, core.FunctionCode, Word(address & ~1u)));
                    core.Complete(Word(address & ~1u));
                    return 4;

                case M68000BusAction.Write:
                    Cycles.Add((address, true, core.FunctionCode, core.DataOut));
                    if (core.IsWord)
                    {
                        Memory[address % Size] = (byte)(core.DataOut >> 8);
                        Memory[(address + 1) % Size] = (byte)core.DataOut;
                    }
                    else
                    {
                        Memory[address % Size] = (byte)core.DataOut;
                    }

                    core.Complete();
                    return 4;

                case M68000BusAction.InterruptAcknowledge:
                    Cycles.Add((address, false, core.FunctionCode, 0));
                    if (Vector is { } vector) core.Complete(vector);
                    else core.CompleteAutovector();
                    return 4;

                case M68000BusAction.Boundary:
                    core.Complete();
                    return 0;

                default:
                    var clocks = core.IdleClocks;
                    core.Complete();
                    return clocks;
            }
        }

        /// <summary>One instruction (the first time, the reset sequence before it): the clocks it took.</summary>
        public int Step()
        {
            if (!Core.PauseBetweenInstructions)
            {
                Core.PauseBetweenInstructions = true;
                while (Core.Action != M68000BusAction.Boundary) Cycle();
            }

            var clocks = 0;
            var count = 0;
            do
            {
                clocks += Cycle();
            }
            while (Core.Action != M68000BusAction.Boundary && ++count < 200000);

            return clocks;
        }

        public void Run(int instructions)
        {
            for (var i = 0; i < instructions; i++) Step();
        }

        public static Machine With(string body, string vectors = "")
        {
            var source = $"""
                        org 0
                        dc.l $8000
                        dc.l start
                {vectors}
                        org $400
                start:
                {body}
                """;
            var assembled = Assembler68000.Assemble(source);
            var machine = new Machine();
            foreach (var (address, value) in assembled.Bytes) machine.Memory[address % Size] = value;
            return machine;
        }
    }

    [Fact]
    public void ResetReadsTheStackPointerAndPcFromTheVectors()
    {
        var m = Machine.With("        nop\n        nop");
        m.Step();

        Assert.Equal(0x8000u, m.Core.Ssp);
        Assert.Equal(0x8000u, m.Core.GetA(7));
        Assert.Equal(0x2700, m.Core.SR);
        Assert.Equal(0x402u, m.Core.PC);

        // The vectors are read in supervisor program space, high word first.
        Assert.Equal([(0u, 6), (2u, 6), (4u, 6), (6u, 6), (0x400u, 6), (0x402u, 6)],
            m.Cycles.Take(6).Select(c => (c.Address, c.Fc)));
    }

    [Theory]
    [InlineData("move.l #$12345678,d0", 0x12345678u, "")]
    [InlineData("moveq #-1,d0", 0xFFFFFFFFu, "N")]
    [InlineData("move.l #$7FFFFFFF,d0\n        addq.l #1,d0", 0x80000000u, "NV")]
    [InlineData("move.l #$FFFFFFFF,d0\n        addq.l #1,d0", 0u, "XZC")]
    [InlineData("moveq #5,d0\n        sub.l #7,d0", 0xFFFFFFFEu, "XNC")]
    [InlineData("move.l #$0001FFFF,d0\n        add.w #1,d0", 0x00010000u, "XZC")]
    [InlineData("move.l #$12345678,d0\n        swap d0", 0x56781234u, "")]
    [InlineData("moveq #-128,d0\n        ext.w d0\n        ext.l d0", 0xFFFFFF80u, "N")]
    [InlineData("move.w #300,d0\n        mulu #300,d0", 90000u, "")]
    [InlineData("move.w #-3,d0\n        muls #7,d0", 0xFFFFFFEBu, "N")]
    [InlineData("move.l #100003,d0\n        divu #10,d0", (3u << 16) | 10000u, "")]
    [InlineData("move.l #-100,d0\n        divs #7,d0", 0xFFFEFFF2u, "N")]
    [InlineData("move.b #$45,d0\n        move.b #$38,d1\n        andi #$EF,ccr\n        abcd d1,d0", 0x83u, "NV")]
    [InlineData("move.l #$80000001,d0\n        asr.l #1,d0", 0xC0000000u, "XNC")]
    [InlineData("move.l #$80000001,d0\n        lsr.l #1,d0", 0x40000000u, "XC")]
    [InlineData("move.l #$80000001,d0\n        rol.l #1,d0", 0x00000003u, "C")]
    [InlineData("moveq #0,d0\n        bset #31,d0", 0x80000000u, "Z")]
    [InlineData("moveq #$40,d0\n        asl.b #1,d0", 0x80u, "NV")]
    public void InstructionsGiveTheirResultsAndFlags(string code, uint d0, string flags)
    {
        var m = Machine.With($"        {code}\n        nop");
        m.Run(code.Split('\n').Length + 1);

        Assert.Equal(d0, m.Core.GetD(0));
        var expected = (flags.Contains('X') ? 0x10 : 0) | (flags.Contains('N') ? 8 : 0) | (flags.Contains('Z') ? 4 : 0)
                       | (flags.Contains('V') ? 2 : 0) | (flags.Contains('C') ? 1 : 0);
        Assert.Equal(expected, m.Core.Ccr);
    }

    /// <summary>The clocks the MC68000 user's manual gives, with /DTACK at once.</summary>
    [Theory]
    [InlineData("nop", 4)]
    [InlineData("move.l d0,d1", 4)]
    [InlineData("move.w (a0),d0", 8)]
    [InlineData("move.l (a0)+,(a1)+", 20)]
    [InlineData("move.w d0,-(a1)", 8)]
    [InlineData("move.l $10(a0),$1000.w", 28)]
    [InlineData("moveq #1,d0", 4)]
    [InlineData("add.l d0,d1", 8)]
    [InlineData("add.w (a0),d0", 8)]
    [InlineData("add.l d0,(a0)", 20)]
    [InlineData("addi.w #1,d0", 8)]
    [InlineData("addq.w #1,(a0)", 12)]
    [InlineData("lea $10(a0),a1", 8)]
    [InlineData("pea (a0)", 12)]
    [InlineData("clr.l d0", 6)]
    [InlineData("lsl.w #4,d0", 14)]
    [InlineData("lsl.l #4,d0", 16)]
    [InlineData("btst #3,d0", 10)]
    [InlineData("bset d1,d0", 6)]
    [InlineData("movem.l d0-d7,-(a7)", 72)]
    [InlineData("movem.w (a7)+,d0-d3", 28)]
    [InlineData("exg d0,d1", 6)]
    [InlineData("swap d0", 4)]
    [InlineData("link a6,#-8", 16)]
    [InlineData("mulu d1,d0", 40)]
    [InlineData("divu d1,d0", 10)]
    [InlineData("move.w d0,sr", 12)]
    public void InstructionsTakeTheManualsClocks(string code, int clocks)
    {
        var m = Machine.With($"""
                    lea $2000,a0
                    lea $3000,a1
                    move.l #$12345678,d0
                    moveq #1,d1
                    move.w #$2700,d0
                    {code}
            """);
        m.Run(5);
        Assert.Equal(clocks, m.Step());
    }

    [Fact]
    public void BranchesAndSubroutinesTakeTheirClocks()
    {
        var m = Machine.With("""
                    moveq #1,d0
                    bra.s skip
                    nop
            skip:   dbra d0,skip
                    jsr sub
                    nop
            sub:    rts
            """);
        m.Step();
        Assert.Equal(10, m.Step());     // BRA
        Assert.Equal(10, m.Step());     // DBRA, counter not out: taken
        Assert.Equal(14, m.Step());     // DBRA, counter expired
        Assert.Equal(20, m.Step());     // JSR to an absolute long address
        Assert.Equal(16, m.Step());     // RTS
        Assert.Equal(0x7FFCu, (uint)m.Core.GetA(7) - 4);
    }

    /// <summary>
    /// A program that checks itself: each block computes something and compares it with the answer
    /// worked out by hand, branching to fail if it is wrong. D7 counts the checks passed and $1000
    /// ends up "PASS".
    /// </summary>
    [Fact]
    public void ASelfCheckingProgramPasses()
    {
        var m = Machine.With("""
            RESULT  equ $1000
                    moveq #0,d7
            ; arithmetic with carry through ADDX: $FFFFFFFF + 1 across two longs
                    move.l #$FFFFFFFF,d0
                    moveq #0,d1
                    moveq #1,d2
                    moveq #0,d3
                    add.l d2,d0
                    addx.l d3,d1
                    tst.l d0
                    bne fail
                    cmp.l #1,d1
                    bne fail
                    addq.l #1,d7
            ; a loop: the sum of 1 to 100
                    moveq #0,d0
                    moveq #99,d1
                    moveq #1,d2
            sum:    add.w d2,d0
                    addq.w #1,d2
                    dbra d1,sum
                    cmp.w #5050,d0
                    bne fail
                    addq.l #1,d7
            ; a subroutine with a frame, and MOVEM saving registers round it
                    move.l #$11111111,d3
                    move.l #$2222,d4
                    bsr square
                    cmp.l #$11111111,d3
                    bne fail
                    cmp.l #$48D0C,d0
                    bne fail
                    addq.l #1,d7
            ; memory: a string copied, then compared byte by byte with CMPM
                    lea text(pc),a0
                    lea $2000,a1
            copy:   move.b (a0)+,(a1)+
                    bne copy
                    lea text(pc),a0
                    lea $2000,a1
                    moveq #4,d0
            match:  cmpm.b (a0)+,(a1)+
                    dbne d0,match
                    bne fail
                    addq.l #1,d7
            ; BCD: 1999 + 1 = 2000, two bytes from the top down
                    move.w #$1999,$3000
                    move.w #$0001,$3010
                    lea $3002,a0
                    lea $3012,a1
                    andi #$EF,ccr
                    abcd -(a1),-(a0)
                    abcd -(a1),-(a0)
                    cmp.w #$2000,$3000
                    bne fail
                    addq.l #1,d7
            ; bits and conditions: Scc, BTST, BCHG, a rotate through X
                    moveq #0,d0
                    bset #5,d0
                    btst #5,d0
                    seq d1
                    tst.b d1
                    bne fail
                    bchg #5,d0
                    tst.l d0
                    bne fail
                    move.l #$80000000,d0
                    roxl.l #1,d0
                    bcc fail
                    roxl.l #1,d0
                    cmp.l #1,d0
                    bne fail
                    addq.l #1,d7
            ; multiply and divide, signed
                    move.w #-1234,d0
                    muls #-56,d0
                    cmp.l #69104,d0
                    bne fail
                    divs #-100,d0
                    cmp.w #-691,d0
                    bne fail
                    swap d0
                    cmp.w #4,d0
                    bne fail
                    addq.l #1,d7
                    move.l #$50415353,RESULT
            done:   bra done
            fail:   move.l #$4641494C,RESULT
                    bra fail

            ; D0 = D4 squared, with D3 and D4 kept and a frame of its own.
            square: link a6,#-4
                    movem.l d3-d4,-(a7)
                    move.w d4,d0
                    mulu d4,d0
                    move.l d0,-4(a6)
                    moveq #0,d3
                    moveq #0,d4
                    movem.l (a7)+,d3-d4
                    move.l -4(a6),d0
                    lsr.l #8,d0
                    unlk a6
                    rts

            text:   dc.b "68000",0
            """);

        for (var i = 0; i < 2000 && m.Long(0x1000) == 0; i++) m.Step();

        Assert.Equal("PASS", System.Text.Encoding.ASCII.GetString(m.Memory, 0x1000, 4));
        Assert.Equal(7u, m.Core.GetD(7));
        Assert.Equal(0x8000u, m.Core.GetA(7));
    }

    private const string Vectors = """
                org 8
                dc.l handler    ; 2 bus error
                dc.l handler    ; 3 address error
                dc.l handler    ; 4 illegal
                dc.l handler    ; 5 zero divide
                dc.l handler    ; 6 CHK
                dc.l handler    ; 7 TRAPV
                dc.l handler    ; 8 privilege
                dc.l handler    ; 9 trace
                dc.l handler    ; 10 line A
                dc.l handler    ; 11 line F
                org $60
                dc.l handler    ; 24 spurious
                dc.l handler, handler, handler, handler, handler, handler, handler
                org $80
                dc.l handler, handler, handler, handler, handler, handler, handler, handler
                dc.l handler, handler, handler, handler, handler, handler, handler, handler
                org $100
                dc.l handler    ; 64, a user vector
                org $300
        handler: nop
                rte
        """;

    /// <summary>The group 1 and 2 exceptions: the PC each stacks, the SR, and the vector.</summary>
    [Theory]
    [InlineData("illegal", 4, 0x400u)]
    [InlineData("dc.w $A123", 10, 0x400u)]
    [InlineData("dc.w $F123", 11, 0x400u)]
    [InlineData("trap #5", 37, 0x402u)]
    [InlineData("divu #0,d0", 5, 0x404u)]
    public void ExceptionsStackThePcAndSrAndTakeTheirVector(string code, int vector, uint stackedPc)
    {
        var m = Machine.With($"        {code}\n        nop", Vectors);
        m.Step();
        Assert.Equal(vector, m.Core.LastException);
        Assert.Equal(0x300u, m.Core.PC);
        Assert.Equal(0x8000u - 6, m.Core.GetA(7));
        Assert.Equal(stackedPc, m.Long(0x8000 - 4));
        Assert.Equal(0x2700, m.Word(0x8000 - 6) & 0xFF00);
    }

    [Fact]
    public void TrapvAndChkTrapOnlyWhenTheyShould()
    {
        var m = Machine.With("""
                    moveq #5,d0
                    chk #10,d0
                    trapv
                    moveq #11,d0
                    chk #10,d0
                    nop
            """, Vectors);
        m.Run(4);
        Assert.Equal(-1, m.Core.LastException);
        m.Step();
        Assert.Equal(6, m.Core.LastException);
        Assert.Equal(0x40Eu, m.Long(0x8000 - 4));       // the instruction after the CHK
    }

    [Fact]
    public void AUserProgramWritingSrIsAPrivilegeViolation()
    {
        var m = Machine.With("""
                    move.l #$6000,a0
                    move.l a0,usp
                    andi #$DFFF,sr          ; to user mode
                    move #$2700,sr          ; not allowed there
                    nop
            """, Vectors);
        m.Run(3);
        Assert.False(m.Core.Supervisor);
        Assert.Equal(0x6000u, m.Core.GetA(7));
        m.Step();
        Assert.Equal(8, m.Core.LastException);
        Assert.True(m.Core.Supervisor);
        Assert.Equal(0x8000u - 6, m.Core.GetA(7));
        Assert.Equal(0x6000u, m.Core.Usp);
        Assert.Equal(0x40Cu, m.Long(0x8000 - 4));       // the offending instruction itself
        Assert.Equal(0, m.Word(0x8000 - 6) & 0x2000);   // stacked SR: user mode
    }

    /// <summary>An odd word address: the seven-word group 0 frame, with the access address and the word saying how.</summary>
    [Fact]
    public void AnOddWordAccessIsAnAddressError()
    {
        var m = Machine.With("""
                    lea $2001,a0
                    move.w (a0),d0
                    nop
            """, Vectors);
        m.Run(2);

        Assert.Equal(3, m.Core.LastException);
        Assert.Equal(0x8000u - 14, m.Core.GetA(7));
        Assert.Equal(0x2001u, m.Long(0x8000 - 12));                 // the address
        Assert.Equal(0x3010, m.Word(0x8000 - 8));                   // the opcode, MOVE.W (A0),D0
        Assert.Equal(0x15, m.Word(0x8000 - 14) & 0x1F);             // a read, supervisor data
    }

    [Fact]
    public void TraceTakesAnExceptionAfterEachInstruction()
    {
        var m = Machine.With("""
                    ori #$8000,sr
                    moveq #1,d0
                    moveq #2,d0
            """, Vectors);
        m.Step();
        m.Step();       // MOVEQ #1, then the trace exception
        Assert.Equal(9, m.Core.LastException);
        Assert.Equal(1u, m.Core.GetD(0));
        Assert.Equal(0x406u, m.Long(0x8000 - 4));
        Assert.Equal(0, m.Core.SR & 0x8000);
    }

    [Fact]
    public void ABusErrorTakesVectorTwo()
    {
        var m = Machine.With("        move.w $2000,d0\n        nop", Vectors);
        m.Core.PauseBetweenInstructions = true;
        while (!(m.Core.Action == M68000BusAction.Read && m.Core.Address == 0x2000)) m.Cycle();
        m.Core.BusError();
        while (m.Core.Action != M68000BusAction.Boundary) m.Cycle();

        Assert.Equal(2, m.Core.LastException);
        Assert.Equal(0x2000u, m.Long(0x8000 - 12));
        Assert.Equal(0x15, m.Word(0x8000 - 14) & 0x1F);
        Assert.Equal(0x300u, m.Core.PC);
    }

    [Fact]
    public void AnInterruptAboveTheMaskIsTakenThroughItsAutovector()
    {
        var m = Machine.With("""
                    move #$2200,sr
                    moveq #1,d0
                    moveq #2,d0
                    moveq #3,d0
            """, Vectors);
        m.Run(2);

        m.Core.InterruptLevel = 2;      // not above the mask of 2
        m.Step();
        Assert.Equal(-1, m.Core.LastException);

        m.Core.InterruptLevel = 5;
        m.Step();
        Assert.Equal(29, m.Core.LastException);     // 24 + 5
        Assert.Equal(5, m.Core.InterruptMask);
        Assert.Equal(0x408u, m.Long(0x8000 - 4));
        Assert.Equal(0x2200, m.Word(0x8000 - 6));
        Assert.Contains(m.Cycles, c => c.Address == 0xFFFFFB && c.Fc == 7);   // the acknowledge: level 5 on A1-A3
    }

    [Fact]
    public void AVectoredInterruptUsesTheVectorOnTheBus()
    {
        var m = Machine.With("        moveq #1,d0\n        moveq #2,d0", Vectors);
        m.Step();
        m.Vector = 64;
        m.Core.InterruptLevel = 6;
        m.Core.SR = 0x2000;
        m.Step();
        Assert.Equal(64, m.Core.LastException);
        Assert.Equal(0x300u, m.Core.PC);
    }

    [Fact]
    public void LevelSevenCannotBeMaskedButIsTakenOncePerRise()
    {
        var m = Machine.With("""
                    moveq #1,d0
                    moveq #2,d0
                    moveq #3,d0
            loop:   bra loop
            """, Vectors);
        m.Step();
        m.Core.InterruptLevel = 7;
        m.Step();
        Assert.Equal(31, m.Core.LastException);
        m.Step();       // NOP in the handler
        m.Step();       // RTE
        m.Step();
        Assert.Equal(1L, m.Core.Exceptions);
        m.Core.InterruptLevel = 0;
        m.Core.InterruptLevel = 7;
        m.Step();
        Assert.Equal(2L, m.Core.Exceptions);
    }

    [Fact]
    public void StopWaitsForAnInterrupt()
    {
        var m = Machine.With("""
                    stop #$2000
                    moveq #1,d0
            """, Vectors);
        m.Core.PauseBetweenInstructions = true;
        while (m.Core.Action != M68000BusAction.Boundary) m.Cycle();
        m.Cycle();
        for (var i = 0; i < 20; i++) m.Cycle();
        Assert.True(m.Core.IsStopped);
        Assert.Equal(M68000BusAction.Stopped, m.Core.Action);
        Assert.Equal(0x2000, m.Core.SR);

        m.Core.InterruptLevel = 1;
        while (m.Core.Action != M68000BusAction.Boundary) m.Cycle();
        Assert.False(m.Core.IsStopped);
        Assert.Equal(25, m.Core.LastException);
        Assert.Equal(0x404u, m.Long(0x8000 - 4));       // returns to the instruction after STOP
    }

    [Fact]
    public void ResetDrivesTheResetLineFor124Clocks()
    {
        var m = Machine.With("        nop\n        reset\n        nop");
        m.Step();
        m.Actions.Clear();
        Assert.Equal(132, m.Step());
        Assert.Contains(M68000BusAction.ResetOut, m.Actions);
        Assert.Equal(124, m.Core.Action == M68000BusAction.ResetOut ? m.Core.IdleClocks : 124);
    }

    /// <summary>The core's own Step, as the 6502 core's tests use theirs: whole instructions against a memory.</summary>
    [Fact]
    public void TheCoresStepRunsWholeInstructions()
    {
        var m = Machine.With("        moveq #7,d0\n        add.l d0,d0\n        nop");
        var memory = m.Memory;
        ushort Read(uint a) => (ushort)((memory[a % Machine.Size] << 8) | memory[(a + 1) % Machine.Size]);
        m.Core.Step(Read, (_, _, _) => { });
        Assert.Equal(7u, m.Core.GetD(0));
        Assert.Equal(8, m.Core.Step(Read, (_, _, _) => { }));
        Assert.Equal(14u, m.Core.GetD(0));
    }

    /// <summary>Instructions assembled and disassembled again come back as they were written.</summary>
    [Theory]
    [InlineData("MOVE.L D0,(A1)+")]
    [InlineData("MOVE.W $10(A0),-(A7)")]
    [InlineData("MOVE.B -$2(A3,D1.W),D2")]
    [InlineData("MOVEA.L #$12345678,A2")]
    [InlineData("MOVEQ #-$1,D3")]
    [InlineData("LEA $1000.W,A0")]
    [InlineData("LEA $F00000,A5")]
    [InlineData("PEA $000410(PC)")]
    [InlineData("ADD.L D2,D3")]
    [InlineData("ADDI.W #$100,$4(A0)")]
    [InlineData("SUBQ.L #8,A7")]
    [InlineData("CMPA.W D0,A1")]
    [InlineData("CMPM.B (A0)+,(A1)+")]
    [InlineData("EOR.L D1,(A2)")]
    [InlineData("ANDI #$EF,CCR")]
    [InlineData("ORI #$700,SR")]
    [InlineData("BTST #7,(A0)")]
    [InlineData("BSET D1,D0")]
    [InlineData("ASL.W #3,D0")]
    [InlineData("ROXR.L D1,D2")]
    [InlineData("LSR.W (A0)")]
    [InlineData("MOVEM.L D0-D7/A0-A6,-(A7)")]
    [InlineData("MOVEM.W (A7)+,D0/D2/A3")]
    [InlineData("MOVEP.L D0,$10(A1)")]
    [InlineData("DIVS.W #$7,D1")]
    [InlineData("MULU.W (A0),D2")]
    [InlineData("ABCD -(A1),-(A0)")]
    [InlineData("EXG D0,A1")]
    [InlineData("LINK A6,#-$8")]
    [InlineData("UNLK A6")]
    [InlineData("MOVE A0,USP")]
    [InlineData("MOVE SR,D0")]
    [InlineData("MOVE (A0),CCR")]
    [InlineData("JSR $000400")]
    [InlineData("JMP (A0)")]
    [InlineData("TRAP #15")]
    [InlineData("STOP #$2000")]
    [InlineData("TAS (A0)")]
    [InlineData("CHK.W D1,D0")]
    [InlineData("SNE D0")]
    [InlineData("NBCD D0")]
    [InlineData("EXT.L D0")]
    [InlineData("SWAP D7")]
    [InlineData("RTE")]
    public void TheDisassemblerReadsBackWhatWasAssembled(string text)
    {
        var assembled = Assembler68000.Assemble($"        org $400\n        {text}");
        var (back, length) = M68000Disassembler.Disassemble(0x400, a => assembled.Word(a));
        Assert.Equal(text, back);
        Assert.Equal(assembled.Bytes.Count, length);
    }

    [Fact]
    public void BranchesDisassembleToTheirTargets()
    {
        var assembled = Assembler68000.Assemble("""
                    org $400
            top:    bne.s top
                    dbra d1,top
                    bra top
                    bsr.s top
            """);
        string At(uint a) => M68000Disassembler.Disassemble(a, w => assembled.Word(w)).Text;
        Assert.Equal("BNE.S $000400", At(0x400));
        Assert.Equal("DBRA D1,$000400", At(0x402));
        Assert.Equal("BRA.W $000400", At(0x406));
        Assert.Equal("BSR.S $000400", At(0x40A));
    }

    /// <summary>
    /// Every one of the 65,536 opcodes: the disassembler calls it an instruction exactly when the
    /// core runs it as one rather than taking the illegal-instruction or line A/F exception.
    /// </summary>
    [Fact]
    public void TheDisassemblerAndTheCoreAgreeOnWhatIsAnInstruction()
    {
        var disagreements = new List<string>();
        var core = new M68000Core { PauseBetweenInstructions = true };
        ushort Read(uint a) => (ushort)(a < 0x100 ? 0x0000 : 0x1000);

        for (var op = 0; op < 0x10000; op++)
        {
            for (var i = 0; i < 8; i++)
            {
                core.SetD(i, 0);
                core.SetA(i, 0x2000);
            }

            core.Load(0x1000, 0x2700, 0x3000, 0x4000, (ushort)op, 0x0000);
            core.Step(Read, (_, _, _) => { });
            var exception = core.LastException;
            var illegalForCore = exception is 4 or 10 or 11;

            var (text, _) = M68000Disassembler.Disassemble(0x1000, a => a == 0x1000 ? (ushort)op : (ushort)0);
            var illegalForText = text.StartsWith("DC.W", StringComparison.Ordinal) || text.StartsWith("LINE", StringComparison.Ordinal) || text == "ILLEGAL";

            if (illegalForCore != illegalForText) disagreements.Add($"{op:X4} core {exception} text {text}");
        }

        Assert.Empty(disagreements.Take(20));
    }
}
