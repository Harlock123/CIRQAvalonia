using Cirq.Components.Processors;

namespace Cirq.Components.Tests;

/// <summary>
/// The 6809 core on its own, against a 64K array: each instruction group's results and flags as the
/// programming manual gives them, the bus cycles and cycle counts of every addressing mode,
/// interrupts, SYNC, CWAI, /HALT, DMA and reset, the disassembler and the test-side assembler
/// reading each other back, and a self-checking program assembled from source.
/// </summary>
public class Cpu6809CoreTests
{
    /// <summary>A core and its memory, and the cycles it has run as <c>R1000=86</c>, <c>W07FF=12</c>, <c>-</c> (dead) or <c>VFFFE=80</c>.</summary>
    private sealed class Machine
    {
        public byte[] Memory { get; } = new byte[0x10000];

        public M6809Core Core { get; } = new();

        public List<string> Trace { get; } = [];

        public Machine(string source, ushort start = 0x1000, byte cc = M6809Core.FlagI | M6809Core.FlagF)
        {
            var output = Assembler6809.Assemble(source);
            foreach (var (address, value) in output.Bytes) Memory[address] = value;
            Core.Load(start, 0, 0, 0, 0, 0x0700, 0x0800, 0, cc);
        }

        public Machine(params byte[] program)
        {
            Array.Copy(program, 0, Memory, 0x1000, program.Length);
            Core.Load(0x1000, 0, 0, 0, 0, 0x0700, 0x0800, 0, M6809Core.FlagI | M6809Core.FlagF);
        }

        /// <summary>One cycle.</summary>
        public void Cycle()
        {
            var c = Core;
            switch (c.Kind)
            {
                case M6809CycleKind.Released:
                    Trace.Add("x");
                    c.Complete();
                    break;
                case M6809CycleKind.Dead:
                    Assert.Equal(0xFFFF, c.Address);
                    Trace.Add("-");
                    c.Complete(Memory[0xFFFF]);
                    break;
                case M6809CycleKind.Write:
                    Trace.Add($"W{c.Address:X4}={c.DataOut:X2}");
                    Memory[c.Address] = c.DataOut;
                    c.Complete();
                    break;
                default:
                    Trace.Add($"{(c.Kind == M6809CycleKind.Vector ? 'V' : 'R')}{c.Address:X4}={Memory[c.Address]:X2}");
                    c.Complete(Memory[c.Address]);
                    break;
            }
        }

        /// <summary>One instruction (or interrupt sequence); returns its cycles as a string.</summary>
        public string Step()
        {
            Trace.Clear();
            do Cycle();
            while (!Core.FirstCycle && Trace.Count < 100);
            return string.Join(" ", Trace);
        }

        public int Cycles() => Step().Split(' ').Length;

        public void Run(int instructions)
        {
            for (var i = 0; i < instructions; i++) Step();
        }

        public bool Flag(byte flag) => (Core.CC & flag) != 0;
    }

    // ---- results ------------------------------------------------------------

    [Fact]
    public void LoadsAndStoresSetNAndZAndClearV()
    {
        var m = new Machine("""
                    org $1000
                    orcc #$02       ; V set, to see it cleared
                    lda #$80
                    sta $2000
                    ldx #$0000
                    stx $2001
                    ldd #$1234
                    std <$10
            """);
        m.Run(2);
        Assert.Equal(0x80, m.Core.A);
        Assert.True(m.Flag(M6809Core.FlagN));
        Assert.False(m.Flag(M6809Core.FlagV));
        m.Run(3);
        Assert.Equal(0x80, m.Memory[0x2000]);
        Assert.True(m.Flag(M6809Core.FlagZ));
        m.Run(2);
        Assert.Equal(0x12, m.Memory[0x0010]);
        Assert.Equal(0x34, m.Memory[0x0011]);
        Assert.False(m.Flag(M6809Core.FlagZ) || m.Flag(M6809Core.FlagN));
    }

    /// <summary>ADDA: H from bit 3, V on a signed overflow, C out of bit 7.</summary>
    [Theory]
    [InlineData(0x08, 0x08, 0x10, true, false, false)]
    [InlineData(0x7F, 0x01, 0x80, true, true, false)]
    [InlineData(0xFF, 0x01, 0x00, true, false, true)]
    [InlineData(0x80, 0x80, 0x00, false, true, true)]
    public void AddSetsHalfCarryOverflowAndCarry(int a, int operand, int result, bool half, bool overflow, bool carry)
    {
        var m = new Machine(0x86, (byte)a, 0x8B, (byte)operand);   // LDA #a, ADDA #operand
        m.Run(2);
        Assert.Equal(result, m.Core.A);
        Assert.Equal(half, m.Flag(M6809Core.FlagH));
        Assert.Equal(overflow, m.Flag(M6809Core.FlagV));
        Assert.Equal(carry, m.Flag(M6809Core.FlagC));
    }

    /// <summary>SUBA, SBCA and CMPA: C is a borrow; CMPA leaves A alone.</summary>
    [Fact]
    public void SubtractionBorrowsAndCompareKeepsTheRegister()
    {
        var m = new Machine("""
                    org $1000
                    lda #$10
                    suba #$20       ; $F0, borrow
                    sbca #$00       ; $EF: the borrow taken
                    cmpa #$EF
            """);
        m.Run(2);
        Assert.Equal(0xF0, m.Core.A);
        Assert.True(m.Flag(M6809Core.FlagC));
        Assert.True(m.Flag(M6809Core.FlagN));
        m.Step();
        Assert.Equal(0xEF, m.Core.A);
        Assert.False(m.Flag(M6809Core.FlagC));
        m.Step();
        Assert.Equal(0xEF, m.Core.A);
        Assert.True(m.Flag(M6809Core.FlagZ));

        var v = new Machine(0x86, 0x80, 0x80, 0x01);   // LDA #$80, SUBA #1: -128 - 1 overflows
        v.Run(2);
        Assert.Equal(0x7F, v.Core.A);
        Assert.True(v.Flag(M6809Core.FlagV));
    }

    [Fact]
    public void SixteenBitArithmeticSetsTheFlagsFromSixteenBits()
    {
        var m = new Machine("""
                    org $1000
                    ldd #$7FFF
                    addd #$0001     ; $8000: overflow, negative
                    subd #$8001     ; $FFFF, borrow
                    ldx #$1234
                    cmpx #$1234
            """);
        m.Run(2);
        Assert.Equal(0x8000, m.Core.D);
        Assert.True(m.Flag(M6809Core.FlagV) && m.Flag(M6809Core.FlagN));
        Assert.False(m.Flag(M6809Core.FlagC));
        m.Step();
        Assert.Equal(0xFFFF, m.Core.D);
        Assert.True(m.Flag(M6809Core.FlagC));
        m.Run(2);
        Assert.True(m.Flag(M6809Core.FlagZ));
        Assert.Equal(0x1234, m.Core.X);
    }

    /// <summary>The read-modify-write column on memory: results and flags per the manual's tables.</summary>
    [Theory]
    [InlineData(0x00, 0x01, false, 0xFF, "NC")]     // NEG: C is the borrow
    [InlineData(0x00, 0x80, false, 0x80, "NVC")]    // NEG $80 overflows
    [InlineData(0x03, 0x0F, false, 0xF0, "NC")]     // COM: C always set
    [InlineData(0x04, 0x81, false, 0x40, "C")]      // LSR
    [InlineData(0x06, 0x01, true, 0x80, "NC")]      // ROR: C into bit 7, bit 0 into C
    [InlineData(0x07, 0x81, false, 0xC0, "NC")]     // ASR keeps the sign
    [InlineData(0x08, 0x40, false, 0x80, "NV")]     // ASL: V = b7 xor b6
    [InlineData(0x09, 0x80, true, 0x01, "VC")]      // ROL: C into bit 0
    [InlineData(0x0A, 0x80, false, 0x7F, "V")]      // DEC $80 overflows
    [InlineData(0x0C, 0x7F, false, 0x80, "NV")]     // INC $7F overflows
    [InlineData(0x0D, 0x80, false, 0x80, "N")]      // TST
    [InlineData(0x0F, 0x55, true, 0x00, "Z")]       // CLR clears C
    public void ReadModifyWriteMatchesTheManual(int opcode, int value, bool carryIn, int result, string expected)
    {
        var m = new Machine((byte)(carryIn ? 0x1A : 0x1C), (byte)(carryIn ? 0x01 : 0xFE), (byte)opcode, 0x40);
        m.Memory[0x0040] = (byte)value;
        m.Run(2);

        Assert.Equal(result, m.Memory[0x0040]);
        Assert.Equal(expected.Contains('N'), m.Flag(M6809Core.FlagN));
        Assert.Equal(expected.Contains('Z'), m.Flag(M6809Core.FlagZ));
        Assert.Equal(expected.Contains('V'), m.Flag(M6809Core.FlagV));
        Assert.Equal(expected.Contains('C'), m.Flag(M6809Core.FlagC));
    }

    [Theory]
    [InlineData(0x19, 0x28, 0x47, false)]
    [InlineData(0x99, 0x01, 0x00, true)]
    [InlineData(0x58, 0x46, 0x04, true)]
    public void DaaCorrectsABcdSum(int a, int b, int result, bool carry)
    {
        var m = new Machine(0x86, (byte)a, 0x8B, (byte)b, 0x19);   // LDA, ADDA, DAA
        m.Run(3);
        Assert.Equal(result, m.Core.A);
        Assert.Equal(carry, m.Flag(M6809Core.FlagC));
    }

    [Fact]
    public void MulSexAndAbx()
    {
        var m = new Machine("""
                    org $1000
                    lda #200
                    ldb #100
                    mul             ; 20000 = $4E20: C is bit 7 of B
                    ldb #$F0
                    sex             ; A = $FF
                    ldx #$1000
                    ldb #$FF
                    abx             ; unsigned: $10FF
            """);
        m.Run(2);
        Assert.Equal(11, m.Cycles());
        Assert.Equal(0x4E20, m.Core.D);
        Assert.False(m.Flag(M6809Core.FlagC));
        Assert.False(m.Flag(M6809Core.FlagZ));
        m.Run(2);
        Assert.Equal(0xFFF0, m.Core.D);
        Assert.True(m.Flag(M6809Core.FlagN));
        m.Run(2);
        Assert.Equal(3, m.Cycles());
        Assert.Equal(0x10FF, m.Core.X);
    }

    /// <summary>TFR and EXG, the same size and mixed: 8 into 16 is $FF above, 16 into 8 the low byte.</summary>
    [Fact]
    public void TfrAndExgMoveRegisters()
    {
        var m = new Machine("""
                    org $1000
                    ldd #$1234
                    ldx #$ABCD
                    exg d,x
                    tfr x,y
                    tfr a,dp
                    tfr b,u         ; mixed: $FF34
                    tfr x,b         ; mixed: $34
            """);
        m.Run(2);
        Assert.Equal(8, m.Cycles());
        Assert.Equal(0xABCD, m.Core.D);
        Assert.Equal(0x1234, m.Core.X);
        Assert.Equal(6, m.Cycles());
        Assert.Equal(0x1234, m.Core.Y);
        m.Step();
        Assert.Equal(0xAB, m.Core.DP);
        m.Step();
        Assert.Equal(0xFFCD, m.Core.U);
        m.Step();
        Assert.Equal(0x34, m.Core.B);
    }

    /// <summary>PSHS pushes PC, U, Y, X, DP, B, A, CC from the top down; PULS takes them back in the other order.</summary>
    [Fact]
    public void PshsAndPulsKeepTheManualsOrder()
    {
        var m = new Machine("""
                    org $1000
                    ldd #$1122
                    ldx #$3344
                    pshs a,b,x,cc   ; 5 + 5 cycles
                    clra
                    clrb
                    ldx #0
                    puls cc,a,b,x
            """);
        m.Run(2);
        Assert.Equal(10, m.Cycles());
        Assert.Equal(0x07FB, m.Core.S);
        Assert.Equal(new byte[] { 0x50, 0x11, 0x22, 0x33, 0x44 }, m.Memory[0x07FB..0x0800]);
        m.Run(3);
        Assert.Equal(10, m.Cycles());
        Assert.Equal(0x1122, m.Core.D);
        Assert.Equal(0x3344, m.Core.X);
        Assert.Equal(0x0800, m.Core.S);
    }

    [Fact]
    public void LeaSetsZOnlyForXAndY()
    {
        var m = new Machine("""
                    org $1000
                    ldx #1
                    leax -1,x       ; Z
                    leau ,x         ; no flags
                    leay 5,x
            """);
        m.Run(2);
        Assert.True(m.Flag(M6809Core.FlagZ));
        m.Step();
        Assert.True(m.Flag(M6809Core.FlagZ));
        m.Step();
        Assert.False(m.Flag(M6809Core.FlagZ));
        Assert.Equal(5, m.Core.Y);
    }

    /// <summary>The sixteen conditions, each with CC set so it is taken and so it is not.</summary>
    [Theory]
    [InlineData(0x20, 0x00, 0x00)]   // BRA: always
    [InlineData(0x22, 0x00, 0x01)]   // BHI: C and Z clear
    [InlineData(0x23, 0x04, 0x00)]   // BLS
    [InlineData(0x24, 0x00, 0x01)]   // BCC
    [InlineData(0x25, 0x01, 0x00)]   // BCS
    [InlineData(0x26, 0x00, 0x04)]   // BNE
    [InlineData(0x27, 0x04, 0x00)]   // BEQ
    [InlineData(0x28, 0x00, 0x02)]   // BVC
    [InlineData(0x29, 0x02, 0x00)]   // BVS
    [InlineData(0x2A, 0x00, 0x08)]   // BPL
    [InlineData(0x2B, 0x08, 0x00)]   // BMI
    [InlineData(0x2C, 0x0A, 0x08)]   // BGE: N = V
    [InlineData(0x2D, 0x08, 0x0A)]   // BLT
    [InlineData(0x2E, 0x00, 0x04)]   // BGT: Z clear and N = V
    [InlineData(0x2F, 0x02, 0x00)]   // BLE
    public void EachBranchConditionIsTheManuals(int opcode, int taken, int notTaken)
    {
        foreach (var (cc, expectTaken) in new[] { (taken, true), (notTaken, opcode == 0x20) })
        {
            var m = new Machine((byte)opcode, 0x10);
            m.Core.Load(0x1000, 0, 0, 0, 0, 0, 0x800, 0, (byte)(cc | 0x50));
            Assert.Equal(3, m.Cycles());
            Assert.Equal(expectTaken ? 0x1012 : 0x1002, m.Core.PC);
        }

        var never = new Machine(0x21, 0x10);   // BRN
        never.Step();
        Assert.Equal(0x1002, never.Core.PC);
    }

    // ---- bus cycles -----------------------------------------------------------

    /// <summary>The dead cycles are reads of $FFFF; the cycle after a one-byte opcode reads the next byte.</summary>
    [Fact]
    public void ImmediateDirectAndExtendedCyclesAreTheDatasheets()
    {
        var m = new Machine("""
                    org $1000
                    lda #$41
                    lda <$20
                    lda $2000
                    nop
                    jmp $1100
            """);
        m.Memory[0x20] = 0x55;
        Assert.Equal("R1000=86 R1001=41", m.Step());
        Assert.Equal("R1002=96 R1003=20 - R0020=55", m.Step());
        Assert.Equal("R1004=B6 R1005=20 R1006=00 - R2000=00", m.Step());
        Assert.Equal("R1007=12 R1008=7E", m.Step());
        Assert.Equal("R1008=7E R1009=11 R100A=00 -", m.Step());
    }

    /// <summary>
    /// LDA in every indexed mode, against the programming manual's table of extra cycles: 4 plus 0
    /// for ,R; 1 for 5- and 8-bit offsets, A,R, B,R and an 8-bit PC offset; 2 for ,R+ and ,-R; 3 for
    /// ,R++ and ,--R; 4 for 16-bit offsets and D,R; 5 for a 16-bit PC offset and [n]; and the
    /// indirect forms 3 more (6 for [,R++]).
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x84 }, 4)]                 // ,X
    [InlineData(new byte[] { 0x05 }, 5)]                 // 5,X
    [InlineData(new byte[] { 0x88, 0x40 }, 5)]           // $40,X
    [InlineData(new byte[] { 0x89, 0x01, 0x00 }, 8)]     // $0100,X
    [InlineData(new byte[] { 0x86 }, 5)]                 // A,X
    [InlineData(new byte[] { 0x85 }, 5)]                 // B,X
    [InlineData(new byte[] { 0x8B }, 8)]                 // D,X
    [InlineData(new byte[] { 0x80 }, 6)]                 // ,X+
    [InlineData(new byte[] { 0x81 }, 7)]                 // ,X++
    [InlineData(new byte[] { 0x82 }, 6)]                 // ,-X
    [InlineData(new byte[] { 0x83 }, 7)]                 // ,--X
    [InlineData(new byte[] { 0x8C, 0x10 }, 5)]           // n,PCR 8-bit
    [InlineData(new byte[] { 0x8D, 0x01, 0x00 }, 9)]     // n,PCR 16-bit
    [InlineData(new byte[] { 0x94 }, 7)]                 // [,X]
    [InlineData(new byte[] { 0x91 }, 10)]                // [,X++]
    [InlineData(new byte[] { 0x93 }, 10)]                // [,--X]
    [InlineData(new byte[] { 0x98, 0x02 }, 8)]           // [2,X]
    [InlineData(new byte[] { 0x99, 0x00, 0x02 }, 11)]    // [2,X] 16-bit
    [InlineData(new byte[] { 0x96 }, 8)]                 // [A,X]
    [InlineData(new byte[] { 0x9B }, 11)]                // [D,X]
    [InlineData(new byte[] { 0x9C, 0x10 }, 8)]           // [n,PCR] 8-bit
    [InlineData(new byte[] { 0x9D, 0x01, 0x00 }, 12)]    // [n,PCR] 16-bit
    [InlineData(new byte[] { 0x9F, 0x20, 0x00 }, 9)]     // [$2000]
    public void IndexedModesTakeTheManualsCycles(byte[] operand, int cycles)
    {
        var m = new Machine([0xA6, .. operand]);
        m.Core.X = 0x2000;
        Assert.Equal(cycles, m.Cycles());
    }

    [Fact]
    public void IndexedAddressesAndIndirection()
    {
        var m = new Machine("""
                    org $1000
                    ldx #$2000
                    lda ,x+
                    ldb ,x++
                    lda [,x]
                    ldb table,pcr
                    lda [ptr,pcr]
            table:  fcb $99
            ptr:    fdb $3000
            """);
        m.Memory[0x2000] = 0x11;
        m.Memory[0x2001] = 0x22;
        m.Memory[0x2003] = 0x30;
        m.Memory[0x2004] = 0x00;
        m.Memory[0x3000] = 0x77;
        m.Run(3);
        Assert.Equal(0x11, m.Core.A);
        Assert.Equal(0x22, m.Core.B);
        Assert.Equal(0x2003, m.Core.X);
        m.Step();
        Assert.Equal(0x77, m.Core.A);
        m.Step();
        Assert.Equal(0x99, m.Core.B);
        m.Step();
        Assert.Equal(0x77, m.Core.A);
    }

    /// <summary>INC &lt;$10: the operand read, a read of the next byte while it works, the write.</summary>
    [Fact]
    public void ReadModifyWriteBusCycles()
    {
        var m = new Machine(0x0C, 0x10, 0x0D, 0x10);   // INC <$10, TST <$10
        m.Memory[0x10] = 0x41;
        Assert.Equal("R1000=0C R1001=10 - R0010=41 R1002=0D W0010=42", m.Step());
        Assert.Equal("R1002=0D R1003=10 - R0010=42 - -", m.Step());
    }

    [Fact]
    public void SubroutinesStackTheReturnAddress()
    {
        var m = new Machine("""
                    org $1000
                    jsr $1100
                    bsr sub
                    lbsr sub
            sub:    rts
                    org $1100
                    rts
            """);
        Assert.Equal("R1000=BD R1001=11 R1002=00 - R1003=8D - W07FF=03 W07FE=10", m.Step());
        Assert.Equal("R1100=39 R1101=00 R07FE=10 R07FF=03 R0800=00", m.Step());
        Assert.Equal(0x1003, m.Core.PC);
        Assert.Equal(7, m.Cycles());
        Assert.Equal(5, m.Cycles());
        Assert.Equal(9, m.Cycles());
    }

    [Fact]
    public void LongBranchesTakeACycleMoreWhenTaken()
    {
        var m = new Machine("""
                    org $1000
                    lbeq far        ; not taken: Z clear
                    lbne far        ; taken
            far:    lbra *
            """);
        m.Core.CC = 0x50;
        Assert.Equal(5, m.Cycles());
        Assert.Equal(6, m.Cycles());
        Assert.Equal(5, m.Cycles());
    }

    // ---- interrupts -----------------------------------------------------------

    private static Machine WithVectors(string source)
    {
        var m = new Machine(source, cc: 0x00);
        m.Memory[0xFFF2] = 0x30;   // SWI3
        m.Memory[0xFFF4] = 0x40;   // SWI2
        m.Memory[0xFFF6] = 0x50;   // FIRQ
        m.Memory[0xFFF8] = 0x60;   // IRQ
        m.Memory[0xFFFA] = 0x70;   // SWI
        m.Memory[0xFFFC] = 0x80;   // NMI
        m.Memory[0xFFFE] = 0x90;   // RESET
        foreach (var page in new[] { 0x30, 0x40, 0x50, 0x60, 0x70, 0x80 }) m.Memory[page << 8] = 0x3B;   // RTI
        return m;
    }

    /// <summary>IRQ: 19 cycles stacking the entire state with E set, I set, the vector with BS high; RTI pulls it all in 15.</summary>
    [Fact]
    public void IrqStacksTheEntireState()
    {
        var m = WithVectors("""
                    org $1000
                    ldd #$1234
                    nop
                    nop
            """);
        m.Step();
        m.Core.IrqAsserted = true;
        m.Step();   // the NOP during which it is seen
        var sequence = m.Step();
        Assert.Equal(19, sequence.Split(' ').Length);
        Assert.Contains("VFFF8=60 VFFF9=00", sequence, StringComparison.Ordinal);
        Assert.Equal("IRQ", m.Core.LastInterrupt);
        Assert.Equal(0x07F4, m.Core.S);
        Assert.Equal(0x80, m.Memory[0x07F4] & 0x80);   // E in the stacked CC
        Assert.Equal(0x12, m.Memory[0x07F5]);          // A
        Assert.True(m.Flag(M6809Core.FlagI));
        Assert.False(m.Flag(M6809Core.FlagF));

        m.Core.IrqAsserted = false;
        Assert.Equal(15, m.Cycles());
        Assert.Equal(0x1004, m.Core.PC);
        Assert.Equal(0x0800, m.Core.S);
    }

    /// <summary>FIRQ: 10 cycles, only PC and CC stacked with E clear, I and F set; RTI pulls them in 6.</summary>
    [Fact]
    public void FirqStacksOnlyPcAndCc()
    {
        var m = WithVectors("""
                    org $1000
                    nop
                    nop
            """);
        m.Core.FirqAsserted = true;
        m.Step();
        Assert.Equal(10, m.Cycles());
        Assert.Equal(0x5000, m.Core.PC);
        Assert.Equal(0x07FD, m.Core.S);
        Assert.Equal(0, m.Memory[0x07FD] & 0x80);
        Assert.True(m.Flag(M6809Core.FlagI) && m.Flag(M6809Core.FlagF));
        m.Core.FirqAsserted = false;
        Assert.Equal(6, m.Cycles());
        Assert.Equal(0x1001, m.Core.PC);
    }

    /// <summary>NMI is an edge, and not armed after a reset until the program has loaded S.</summary>
    [Fact]
    public void NmiIsAnEdgeArmedByLoadingS()
    {
        var m = WithVectors("""
                    org $9000
                    nop
                    nop
                    lds #$0800
                    nop
                    nop
                    nop
            """);
        var core = new M6809Core();   // power-on: reset, vector at $FFFE
        byte Read(ushort a) => m.Memory[a];
        while (core.Kind != M6809CycleKind.Fetch) core.Complete(Read(core.Address));
        Assert.Equal(0x9000, core.Address);
        Assert.False(core.NmiArmed);

        core.NmiAsserted = true;
        core.Step(Read, (a, v) => m.Memory[a] = v);   // NOP: the edge is ignored, NMI not armed
        core.Step(Read, (a, v) => m.Memory[a] = v);
        Assert.Equal(0x9002, core.PC);
        core.NmiAsserted = false;
        core.Step(Read, (a, v) => m.Memory[a] = v);   // LDS arms it
        Assert.True(core.NmiArmed);

        core.NmiAsserted = true;                       // an edge now
        core.Step(Read, (a, v) => m.Memory[a] = v);
        core.Step(Read, (a, v) => m.Memory[a] = v);
        Assert.Equal("NMI", core.LastInterrupt);
        Assert.Equal(0x8000, core.PC);
        core.Step(Read, (a, v) => m.Memory[a] = v);    // RTI, still low: no second NMI
        core.Step(Read, (a, v) => m.Memory[a] = v);
        Assert.NotEqual(0x8000, core.InstructionAddress);
    }

    [Fact]
    public void MasksAndPriorities()
    {
        var m = WithVectors("""
                    org $1000
                    orcc #$10       ; I: IRQ masked
                    nop
                    nop
                    nop
            """);
        m.Step();
        m.Core.IrqAsserted = true;
        m.Run(2);
        Assert.Equal(0x1004, m.Core.PC);   // masked

        m.Core.FirqAsserted = true;        // FIRQ is not masked by I
        m.Run(2);
        Assert.Equal("FIRQ", m.Core.LastInterrupt);
    }

    [Fact]
    public void SoftwareInterruptsTakeTheirVectors()
    {
        var m = WithVectors("""
                    org $1000
                    swi
                    swi2
                    swi3
            """);
        Assert.Equal(19, m.Cycles());
        Assert.Equal(0x7000, m.Core.PC);
        Assert.True(m.Flag(M6809Core.FlagI) && m.Flag(M6809Core.FlagF));
        m.Step();   // RTI
        Assert.Equal(20, m.Cycles());
        Assert.Equal(0x4000, m.Core.PC);
        Assert.False(m.Flag(M6809Core.FlagI));   // SWI2 masks nothing
        m.Step();
        Assert.Equal(20, m.Cycles());
        Assert.Equal(0x3000, m.Core.PC);
    }

    /// <summary>SYNC lets go of the bus (BA high, BS low) until an interrupt line: masked, it carries on.</summary>
    [Fact]
    public void SyncWaitsWithTheBusReleased()
    {
        var m = WithVectors("""
                    org $1000
                    orcc #$10
                    sync
                    nop
            """);
        m.Step();
        m.Cycle();
        m.Cycle();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(M6809CycleKind.Released, m.Core.Kind);
            Assert.True(m.Core.BusAvailable);
            Assert.False(m.Core.BusStatus);
            Assert.True(m.Core.IsSyncing);
            m.Cycle();
        }

        m.Core.IrqAsserted = true;   // masked: SYNC ends, the program carries on
        m.Cycle();
        m.Cycle();
        Assert.Equal(M6809CycleKind.Fetch, m.Core.Kind);
        Assert.Equal(0x1003, m.Core.Address);
    }

    /// <summary>CWAI clears the mask, stacks everything, then waits; FIRQ arriving finds E set and stacks nothing more.</summary>
    [Fact]
    public void CwaiStacksThenWaits()
    {
        var m = WithVectors("""
                    org $1000
                    cwai #$AF       ; clear I and F
                    nop
            """);
        m.Core.CC = 0x50;
        for (var i = 0; i < 30; i++) m.Cycle();
        Assert.True(m.Core.IsWaiting);
        Assert.Equal(0x07F4, m.Core.S);
        Assert.Equal(0x80, m.Memory[0x07F4] & 0xD0);   // E set, I and F clear in the stacked CC

        m.Core.FirqAsserted = true;
        m.Step();
        Assert.Equal(0x5000, m.Core.PC);
        Assert.Equal(0x07F4, m.Core.S);
        m.Core.FirqAsserted = false;
        Assert.Equal(15, m.Cycles());   // RTI pulls the entire state
        Assert.Equal(0x1002, m.Core.PC);
    }

    [Fact]
    public void ResetMasksInterruptsClearsDpAndFetchesTheVector()
    {
        var core = new M6809Core { DP = 0x55, CC = 0 };
        var memory = new byte[0x10000];
        memory[0xFFFE] = 0xE0;
        memory[0xFFFF] = 0x00;
        core.ResetAsserted = true;
        for (var i = 0; i < 4; i++) core.Complete(0);
        core.ResetAsserted = false;

        List<string> cycles = [];
        while (core.Kind != M6809CycleKind.Fetch)
        {
            cycles.Add(core.Kind == M6809CycleKind.Vector ? $"V{core.Address:X4}" : core.Kind.ToString());
            core.Complete(memory[core.Address]);
        }

        Assert.Equal(["Dead", "Dead", "VFFFE", "VFFFF", "Dead"], cycles);
        Assert.Equal(0xE000, core.Address);
        Assert.Equal(0, core.DP);
        Assert.Equal(M6809Core.FlagI | M6809Core.FlagF, core.CC);
        Assert.False(core.NmiArmed);
    }

    [Fact]
    public void HaltStopsAtTheEndOfTheInstruction()
    {
        var m = new Machine(0x86, 0x01, 0x4C, 0x4C);   // LDA #1, INCA, INCA
        m.Cycle();
        m.Core.HaltAsserted = true;                   // during LDA
        m.Cycle();
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(M6809CycleKind.Released, m.Core.Kind);
            Assert.True(m.Core.BusAvailable && m.Core.BusStatus);
            m.Cycle();
        }

        Assert.Equal(1, m.Core.A);
        m.Core.HaltAsserted = false;
        m.Cycle();
        Assert.Equal(M6809CycleKind.Fetch, m.Core.Kind);
        m.Run(2);
        Assert.Equal(3, m.Core.A);
    }

    /// <summary>/DMA/BREQ: a dead cycle, the bus let go, a cycle back every fifteen, a dead cycle at the end.</summary>
    [Fact]
    public void DmaRequestLetsGoOfTheBus()
    {
        var m = new Machine(0x86, 0x01, 0x4C);
        m.Cycle();
        m.Core.DmaRequested = true;
        m.Cycle();
        Assert.Equal(M6809CycleKind.Dead, m.Core.Kind);
        Assert.True(m.Core.IsGranted);
        m.Trace.Clear();
        for (var i = 0; i < 31; i++) m.Cycle();
        var fourteen = string.Join(" ", Enumerable.Repeat("x", 14));
        Assert.Equal($"- {fourteen} - {fourteen} -", string.Join(" ", m.Trace));

        m.Core.DmaRequested = false;
        m.Trace.Clear();
        m.Cycle();
        m.Cycle();
        m.Cycle();
        Assert.Equal("x - R1002=4C", string.Join(" ", m.Trace));
        Assert.False(m.Core.IsGranted);
    }

    // ---- what Motorola left undefined ---------------------------------------------

    [Fact]
    public void UndefinedOpcodesBehaveAsTheGuideSays()
    {
        var m = new Machine("""
                    org $1000
                    fcb $10,$86,$42 ; page 2 with nothing there: LDA #$42
                    fcb $4E         ; CLRA that keeps C
                    fcb $01,$20     ; NEG <$20
                    fcb $14         ; the test mode: stopped
            """);
        m.Core.CC = 0x51;
        m.Memory[0x20] = 0x05;
        Assert.Equal(3, m.Cycles());
        Assert.Equal(0x42, m.Core.A);
        m.Step();
        Assert.Equal(0, m.Core.A);
        Assert.True(m.Flag(M6809Core.FlagC));
        m.Step();
        Assert.Equal(0xFB, m.Memory[0x20]);
        m.Cycle();
        m.Cycle();
        Assert.True(m.Core.IsStopped);
    }

    // ---- the disassembler and the assembler ---------------------------------------

    private static readonly HashSet<string> FlowChanging =
    [
        "JMP", "JSR", "BSR", "LBSR", "LBRA", "RTS", "RTI", "SWI", "SWI2", "SWI3", "SYNC", "CWAI",
        "BRA", "BHI", "BLS", "BCC", "BCS", "BNE", "BEQ", "BVC", "BVS", "BPL", "BMI", "BGE", "BLT", "BGT", "BLE",
    ];

    /// <summary>For every documented opcode, the PC moves on by the length the disassembler gives.</summary>
    [Fact]
    public void TheDisassemblerAndTheCoreAgreeOnEveryLength()
    {
        var checkedCount = 0;
        foreach (var prefix in new[] { 0, 0x10, 0x11 })
        {
            for (var op = 0; op < 256; op++)
            {
                var e = Opcodes6809.Get(prefix, (byte)op);
                if (e.Undocumented || e.Mode == M6809Mode.Prefix || (prefix != 0 && Opcodes6809.Get(0, (byte)op) == e)) continue;
                var name = e.Mnemonic.StartsWith('L') && e.Mnemonic.Length == 4 && e.Mnemonic[1] == 'B' ? e.Mnemonic[1..] : e.Mnemonic;
                if (FlowChanging.Contains(name) || e.Mnemonic is "PULS" or "PULU" or "TFR" or "EXG") continue;

                foreach (var post in new byte[] { 0x84, 0x88, 0x89, 0x9F, 0x8D, 0x05 })
                {
                    byte[] program = prefix == 0 ? [(byte)op, post, 0x00, 0x10, 0x00] : [(byte)prefix, (byte)op, post, 0x00, 0x10, 0x00];
                    var m = new Machine(program);
                    var (_, length) = Opcodes6809.Disassemble(0x1000, a => m.Memory[a]);
                    m.Step();
                    Assert.True(length == m.Core.PC - 0x1000, $"{prefix:X2} {op:X2} {post:X2} ({e.Mnemonic}): disassembler {length}, core {m.Core.PC - 0x1000}");
                    checkedCount++;
                    if (e.Mode != M6809Mode.Indexed) break;
                }
            }
        }

        Assert.True(checkedCount > 250);
    }

    /// <summary>Every documented opcode with a range of operands disassembles to text that assembles back to its bytes.</summary>
    [Fact]
    public void TheDisassemblyAssemblesBackToTheSameBytes()
    {
        byte[][] operands =
        [
            [0x84, 0x12, 0x34], [0x05, 0, 0], [0x1F, 0, 0], [0x88, 0x05, 0], [0x88, 0xF0, 0], [0x89, 0x00, 0x10], [0x89, 0x12, 0x34],
            [0x86, 0, 0], [0x85, 0, 0], [0x8B, 0, 0], [0x80, 0, 0], [0x81, 0, 0], [0x82, 0, 0], [0x83, 0, 0], [0xA4, 0, 0],
            [0xC1, 0, 0], [0xE3, 0, 0], [0x8C, 0x10, 0], [0x8D, 0x00, 0x10], [0x8D, 0x12, 0x00], [0x94, 0, 0], [0x91, 0, 0],
            [0x98, 0x05, 0], [0x99, 0x01, 0x00], [0x9B, 0, 0], [0x9C, 0x80, 0], [0x9D, 0x7F, 0xFF], [0x9F, 0xC0, 0x00],
        ];
        var count = 0;

        foreach (var prefix in new[] { 0, 0x10, 0x11 })
        {
            for (var op = 0; op < 256; op++)
            {
                var e = Opcodes6809.Get(prefix, (byte)op);
                if (e.Undocumented || e.Mode == M6809Mode.Prefix || (prefix != 0 && Opcodes6809.Get(0, (byte)op) == e)) continue;

                foreach (var operand in e.Mode == M6809Mode.Indexed ? operands : [[0x12, 0x34, 0x56], [0x8E, 0x40, 0x01]])
                {
                    byte[] bytes = prefix == 0 ? [(byte)op, .. operand] : [(byte)prefix, (byte)op, .. operand];
                    var (text, length) = Opcodes6809.Disassemble(0x2000, a => a - 0x2000 < bytes.Length ? bytes[a - 0x2000] : (byte)0);
                    if (text.Contains('?')) continue;

                    var assembled = Assembler6809.Assemble($"    org $2000\n    {text}\n").Image(0x2000, length + 2);
                    Assert.True(bytes.Take(length).SequenceEqual(assembled.Take(length)) && assembled[length] == 0,
                        $"{string.Join(" ", bytes.Take(length).Select(b => b.ToString("X2")))} -> {text} -> {string.Join(" ", assembled.Take(length + 1).Select(b => b.ToString("X2")))}");
                    count++;
                }
            }
        }

        Assert.True(count > 500);
    }

    [Theory]
    [InlineData(new byte[] { 0x86, 0x41 }, "LDA #$41")]
    [InlineData(new byte[] { 0x10, 0x8E, 0x12, 0x34 }, "LDY #$1234")]
    [InlineData(new byte[] { 0x97, 0x10 }, "STA <$10")]
    [InlineData(new byte[] { 0xBD, 0xE1, 0x23 }, "JSR $E123")]
    [InlineData(new byte[] { 0xA6, 0x1F }, "LDA -1,X")]
    [InlineData(new byte[] { 0xEC, 0xA1 }, "LDD ,Y++")]
    [InlineData(new byte[] { 0x10, 0xAE, 0xB8, 0x02 }, "LDY [$02,Y]")]
    [InlineData(new byte[] { 0x26, 0xFE }, "BNE $1000")]
    [InlineData(new byte[] { 0x34, 0x76 }, "PSHS A,B,X,Y,U")]
    [InlineData(new byte[] { 0x1F, 0x89 }, "TFR A,B")]
    [InlineData(new byte[] { 0x11, 0x3F }, "SWI3")]
    [InlineData(new byte[] { 0x87, 0x00 }, "STA #$00 *")]
    public void TheDisassemblerUsesMotorolasSyntax(byte[] bytes, string text)
    {
        var (got, length) = Opcodes6809.Disassemble(0x1000, a => a - 0x1000 < bytes.Length ? bytes[a - 0x1000] : (byte)0);
        Assert.Equal(text, got);
        Assert.Equal(bytes.Length, length);
    }

    // ---- a program -------------------------------------------------------------

    /// <summary>
    /// The self-checking program: a recursive factorial through the stack, a string copied and
    /// counted with auto-increment, a 16-bit multiply from four MULs, and a jump through a table with
    /// [n,X] — each result compared, and $01 written to $0100 only if every one is right. The circuit
    /// tests run it from ROM.
    /// </summary>
    internal const string SelfCheck = """
                    org $1000
            start:  lds #$0800
                    clr result
            ; 5! by recursion: B in, D out
                    ldb #5
                    bsr fact
                    cmpd #120
                    lbne fail
            ; copy "6809" to $0200 and count it
                    ldx #text
                    ldy #$0200
                    clrb
            copy:   lda ,x+
                    sta ,y+
                    beq copied
                    incb
                    bra copy
            copied: cmpb #4
                    lbne fail
                    lda $0203
                    cmpa #'9'
                    lbne fail
            ; $1234 * $0056 = $0061_D78 (low 16 bits $1D78... checked whole: $00061D78)
                    ldd #$1234
                    std <$40
                    ldd #$0056
                    std <$42
                    lbsr mul16
                    ldd <$44
                    cmpd #$0006
                    lbne fail
                    ldd <$46
                    cmpd #$1D78
                    lbne fail
            ; a jump through a table
                    ldx #table
                    jmp [2,x]
            fail:   bra fail
            good:   lda #1
                    sta result
            done:   bra done

            fact:   cmpb #1
                    bhi more
                    ldd #1
                    rts
            more:   pshs b
                    decb
                    bsr fact        ; D = (n-1)!, small enough to be B
                    lda ,s+         ; n
                    mul             ; D = n * (n-1)!
                    rts

            ; $40:$41 times $42:$43 into $44-$47
            mul16:  clr <$44
                    clr <$45
                    lda <$41
                    ldb <$43
                    mul
                    std <$46
                    lda <$40
                    ldb <$43
                    mul
                    addd <$45
                    std <$45
                    bcc m1
                    inc <$44
            m1:     lda <$41
                    ldb <$42
                    mul
                    addd <$45
                    std <$45
                    bcc m2
                    inc <$44
            m2:     lda <$40
                    ldb <$42
                    mul
                    addd <$44
                    std <$44
                    rts

            text:   fcc "6809"
                    fcb 0
            table:  fdb fail,good
            result  equ $0100
            """;

    /// <summary>The self-checking program, on the core alone.</summary>
    [Fact]
    public void ASelfCheckingProgramPasses()
    {
        var m = new Machine(SelfCheck);

        for (var i = 0; i < 400 && m.Memory[0x100] == 0; i++) m.Step();
        Assert.Equal(1, m.Memory[0x100]);
    }
}
