using Cirq.Components.Processors;

namespace Cirq.Components.Tests;

/// <summary>
/// The Z80 core on its own, against a 64K array and a table of ports: each instruction group's
/// results and flags as Zilog's manual (and, for X, Y and WZ, the undocumented documentation) gives
/// them, the T-states of every kind of instruction, R, the alternate set, the index registers and
/// their halves, the block instructions, all three interrupt modes, NMI, HALT, /WAIT, /BUSREQ and
/// reset, the disassembler and the test-side assembler reading each other back, and a
/// self-checking program assembled from source.
/// </summary>
public class CpuZ80CoreTests
{
    /// <summary>A core with its memory and ports, and the T-states it has run as <c>F0000 R0001 W8000 I0080 O0081 A - x</c>.</summary>
    internal sealed class Machine
    {
        public byte[] Memory { get; } = new byte[0x10000];

        public Z80Core Core { get; } = new();

        /// <summary>What IN reads, by port (the low byte); $FF where nothing is set.</summary>
        public Dictionary<int, byte> Inputs { get; } = [];

        /// <summary>Every OUT: the whole address (A8-A15 included) and the byte.</summary>
        public List<(ushort Port, byte Value)> Outputs { get; } = [];

        /// <summary>The byte an interrupt acknowledge finds on the bus.</summary>
        public byte Vector { get; set; } = 0xFF;

        /// <summary>Wait states /WAIT asks for in each cycle that samples it.</summary>
        public int WaitStates { get; set; }

        private int _waitsLeft;

        public List<string> Trace { get; } = [];

        public Machine(string source, ushort start = 0x0000)
        {
            var output = AssemblerZ80.Assemble(source);
            foreach (var (address, value) in output.Bytes) Memory[address] = value;
            Core.SP = 0xFF00;
            Core.Load(start);
        }

        public Machine(params byte[] program)
        {
            Array.Copy(program, 0, Memory, 0x1000, program.Length);
            Core.SP = 0xFF00;
            Core.Load(0x1000);
        }

        /// <summary>One T-state.</summary>
        public void TState()
        {
            var c = Core;
            byte value = 0;
            if (c.State == Z80TState.T1) _waitsLeft = WaitStates;
            if (c.Kind == Z80CycleKind.Write && c.State == Z80TState.T3) Memory[c.Address] = c.DataOut;
            if (c.Kind == Z80CycleKind.IoWrite && c.State == Z80TState.T3) Outputs.Add((c.Address, c.DataOut));
            if (c.SamplesData)
            {
                value = c.Kind switch
                {
                    Z80CycleKind.IoRead => Inputs.GetValueOrDefault(c.Address & 0xFF, (byte)0xFF),
                    Z80CycleKind.Acknowledge => Vector,
                    _ => Memory[c.Address],
                };
            }

            if (c.SamplesWait)
            {
                c.WaitAsserted = _waitsLeft > 0;
                if (_waitsLeft > 0) _waitsLeft--;
            }

            if (c.State == Z80TState.T1 || c.Kind is Z80CycleKind.Released)
            {
                Trace.Add(c.Kind switch
                {
                    Z80CycleKind.Fetch => $"F{c.Address:X4}",
                    Z80CycleKind.Read => $"R{c.Address:X4}",
                    Z80CycleKind.Write => $"W{c.Address:X4}",
                    Z80CycleKind.IoRead => $"I{c.Address & 0xFF:X4}",
                    Z80CycleKind.IoWrite => $"O{c.Address & 0xFF:X4}",
                    Z80CycleKind.Acknowledge => "A",
                    _ => "x",
                });
            }

            c.Complete(value);
        }

        /// <summary>One instruction (or interrupt response, or HALT's NOP): its T-states.</summary>
        public int Step()
        {
            Trace.Clear();
            var count = 0;
            do
            {
                TState();
                count++;
            }
            while (!Core.FirstCycle && count < 400);

            return count;
        }

        public string Cycles() => string.Join(" ", Trace);

        public void Run(int instructions)
        {
            for (var i = 0; i < instructions; i++) Step();
        }

        public bool Flag(byte flag) => (Core.F & flag) != 0;
    }

    // ---- results and flags -----------------------------------------------------

    [Fact]
    public void LoadsMoveBytesAndWords()
    {
        var m = new Machine("""
                    org $0000
                    ld a,$41
                    ld b,a
                    ld hl,$8000
                    ld (hl),b
                    ld ($8001),a
                    ld de,($8000)
                    ld bc,$1234
                    ld ($8002),bc
                    ld ix,$8000
                    ld c,(ix+1)
                    ld (ix+3),$99
            """);
        m.Run(11);
        Assert.Equal(0x41, m.Memory[0x8000]);
        Assert.Equal(0x41, m.Memory[0x8001]);
        Assert.Equal(0x4141, m.Core.DE);
        Assert.Equal(0x34, m.Memory[0x8002]);
        Assert.Equal(0x99, m.Memory[0x8003]);
        Assert.Equal(0x41, m.Core.C);
    }

    /// <summary>ADD: H from bit 3, V on a signed overflow, C out of bit 7, X and Y the result's bits 3 and 5.</summary>
    [Theory]
    [InlineData(0x08, 0x08, 0x10, true, false, false)]
    [InlineData(0x7F, 0x01, 0x80, true, true, false)]
    [InlineData(0xFF, 0x01, 0x00, true, false, true)]
    [InlineData(0x80, 0x80, 0x00, false, true, true)]
    [InlineData(0x20, 0x08, 0x28, false, false, false)]
    public void AddSetsHalfCarryOverflowAndCarry(int a, int operand, int result, bool half, bool overflow, bool carry)
    {
        var m = new Machine(0x3E, (byte)a, 0xC6, (byte)operand);    // LD A,a; ADD A,n
        m.Run(2);
        Assert.Equal(result, m.Core.A);
        Assert.Equal(half, m.Flag(Z80Core.FlagH));
        Assert.Equal(overflow, m.Flag(Z80Core.FlagPV));
        Assert.Equal(carry, m.Flag(Z80Core.FlagC));
        Assert.False(m.Flag(Z80Core.FlagN));
        Assert.Equal(result & 0x28, m.Core.F & 0x28);
        Assert.Equal(result == 0, m.Flag(Z80Core.FlagZ));
        Assert.Equal(result >= 0x80, m.Flag(Z80Core.FlagS));
    }

    /// <summary>SUB borrows into C and H and sets N; CP keeps A and takes X and Y from the operand, not the result.</summary>
    [Fact]
    public void SubtractionBorrowsAndCompareTakesXyFromTheOperand()
    {
        var m = new Machine(0x3E, 0x10, 0xD6, 0x01, 0x3E, 0x00, 0xFE, 0x28);   // LD A,$10; SUB 1; LD A,0; CP $28
        m.Run(2);
        Assert.Equal(0x0F, m.Core.A);
        Assert.True(m.Flag(Z80Core.FlagH) && m.Flag(Z80Core.FlagN));
        Assert.False(m.Flag(Z80Core.FlagC));
        Assert.Equal(0x08, m.Core.F & 0x28);
        m.Run(2);
        Assert.Equal(0x00, m.Core.A);
        Assert.True(m.Flag(Z80Core.FlagC));
        Assert.Equal(0x28, m.Core.F & 0x28);      // from the operand $28; the result $D8 has only bit 3
    }

    /// <summary>AND sets H; OR and XOR clear it; all three set P/V for even parity and clear C.</summary>
    [Fact]
    public void LogicalOperationsSetParity()
    {
        var m = new Machine(0x37, 0x3E, 0xF0, 0xE6, 0x30, 0xF6, 0x01, 0xEE, 0xFF);   // SCF; LD A,$F0; AND $30; OR 1; XOR $FF
        m.Run(3);
        Assert.Equal(0x30, m.Core.A);
        Assert.True(m.Flag(Z80Core.FlagH) && m.Flag(Z80Core.FlagPV));
        Assert.False(m.Flag(Z80Core.FlagC));
        m.Run(1);
        Assert.Equal(0x31, m.Core.A);
        Assert.False(m.Flag(Z80Core.FlagH) || m.Flag(Z80Core.FlagPV));
        m.Run(1);
        Assert.Equal(0xCE, m.Core.A);
        Assert.False(m.Flag(Z80Core.FlagPV));
        Assert.True(m.Flag(Z80Core.FlagS));
    }

    /// <summary>INC and DEC leave C alone, and set V at $80 and $7F.</summary>
    [Fact]
    public void IncrementAndDecrementKeepCarry()
    {
        var m = new Machine(0x37, 0x06, 0x7F, 0x04, 0x05, 0x0E, 0x00, 0x0D);   // SCF; LD B,$7F; INC B; DEC B; LD C,0; DEC C
        m.Run(3);
        Assert.Equal(0x80, m.Core.B);
        Assert.True(m.Flag(Z80Core.FlagPV) && m.Flag(Z80Core.FlagH) && m.Flag(Z80Core.FlagC) && m.Flag(Z80Core.FlagS));
        m.Run(1);
        Assert.True(m.Flag(Z80Core.FlagPV) && m.Flag(Z80Core.FlagN) && m.Flag(Z80Core.FlagH));
        m.Run(2);
        Assert.Equal(0xFF, m.Core.C);
        Assert.True(m.Flag(Z80Core.FlagC));
        Assert.False(m.Flag(Z80Core.FlagPV));
    }

    /// <summary>DAA after an addition and after a subtraction gives the BCD answer, with C for a carry out of the hundreds.</summary>
    [Theory]
    [InlineData(0x15, 0x27, false, 0x42, false)]
    [InlineData(0x99, 0x01, false, 0x00, true)]
    [InlineData(0x58, 0x46, false, 0x04, true)]
    [InlineData(0x42, 0x15, true, 0x27, false)]
    [InlineData(0x10, 0x20, true, 0x90, true)]
    public void DaaCorrectsBcd(int a, int b, bool subtract, int result, bool carry)
    {
        var m = new Machine(0x3E, (byte)a, (byte)(subtract ? 0xD6 : 0xC6), (byte)b, 0x27);
        m.Run(3);
        Assert.Equal(result, m.Core.A);
        Assert.Equal(carry, m.Flag(Z80Core.FlagC));
        Assert.Equal(subtract, m.Flag(Z80Core.FlagN));
        Assert.Equal(result == 0, m.Flag(Z80Core.FlagZ));
    }

    /// <summary>16-bit ADD leaves S, Z and P/V; ADC and SBC set them all, from sixteen bits.</summary>
    [Fact]
    public void SixteenBitArithmetic()
    {
        var m = new Machine("""
                    org $0000
                    xor a           ; Z set
                    ld hl,$FFFF
                    ld bc,$0001
                    add hl,bc       ; HL = 0, C set, Z kept from XOR
                    ld hl,$7FFF
                    or a
                    adc hl,bc       ; $8000: S and V
                    ld de,$8000
                    sbc hl,de       ; 0: Z, N
            """);
        m.Run(4);
        Assert.Equal(0, m.Core.HL);
        Assert.True(m.Flag(Z80Core.FlagC) && m.Flag(Z80Core.FlagZ) && m.Flag(Z80Core.FlagH));
        m.Run(3);
        Assert.Equal(0x8000, m.Core.HL);
        Assert.True(m.Flag(Z80Core.FlagS) && m.Flag(Z80Core.FlagPV));
        Assert.False(m.Flag(Z80Core.FlagZ));
        m.Run(2);
        Assert.Equal(0, m.Core.HL);
        Assert.True(m.Flag(Z80Core.FlagZ) && m.Flag(Z80Core.FlagN));
    }

    /// <summary>The CB rotates and shifts, SLL among them, set C from the bit shifted out and P/V for parity.</summary>
    [Theory]
    [InlineData(0x00, 0x81, false, 0x03, true)]     // RLC
    [InlineData(0x08, 0x81, false, 0xC0, true)]     // RRC
    [InlineData(0x10, 0x80, true, 0x01, true)]      // RL
    [InlineData(0x18, 0x01, true, 0x80, true)]      // RR
    [InlineData(0x20, 0x81, false, 0x02, true)]     // SLA
    [InlineData(0x28, 0x81, false, 0xC0, true)]     // SRA
    [InlineData(0x30, 0x81, false, 0x03, true)]     // SLL: a 1 shifted in
    [InlineData(0x38, 0x81, false, 0x40, true)]     // SRL
    public void RotatesAndShifts(int column, int value, bool carryIn, int result, bool carryOut)
    {
        var m = new Machine((byte)(carryIn ? 0x37 : 0x00), 0x06, (byte)value, 0xCB, (byte)column);   // SCF/NOP; LD B,v; op B
        m.Run(3);
        Assert.Equal(result, m.Core.B);
        Assert.Equal(carryOut, m.Flag(Z80Core.FlagC));
        Assert.Equal(System.Numerics.BitOperations.PopCount((uint)result) % 2 == 0, m.Flag(Z80Core.FlagPV));
    }

    /// <summary>BIT: Z for a clear bit, S for bit 7 set, H set; X and Y from the register, or for (HL) from WZ's high byte.</summary>
    [Fact]
    public void BitTakesXyFromTheRegisterOrFromWz()
    {
        var m = new Machine("""
                    org $0000
                    ld b,$A8
                    bit 7,b         ; set: S, X and Y from $A8
                    bit 0,b         ; clear: Z and P/V
                    ld hl,$2800
                    ld a,($2800)    ; WZ = $2801
                    bit 0,(hl)      ; X and Y from $28
            """);
        m.Run(2);
        Assert.True(m.Flag(Z80Core.FlagS) && m.Flag(Z80Core.FlagH));
        Assert.False(m.Flag(Z80Core.FlagZ));
        Assert.Equal(0x28, m.Core.F & 0x28);
        m.Run(1);
        Assert.True(m.Flag(Z80Core.FlagZ) && m.Flag(Z80Core.FlagPV));
        m.Run(3);
        Assert.Equal(0x2801, m.Core.WZ);
        Assert.Equal(0x28, m.Core.F & 0x28);
    }

    /// <summary>SCF and CCF take X and Y from (Q ^ F) | A: Q is the flags if the instruction before changed them, else zero.</summary>
    [Fact]
    public void ScfAndCcfTakeXyFromQ()
    {
        var m = new Machine("""
                    org $0000
                    xor a
                    cp $28          ; X and Y from the operand into F; A still 0; Q = F
                    scf             ; (Q ^ F) | A = 0: X and Y clear
                    xor a
                    cp $28
                    nop             ; Q = 0
                    scf             ; (0 ^ F) | A: X and Y as F has them
                    ld a,$08
                    ccf             ; Q = 0 again after LD: F's Y, and A's X
            """);
        m.Run(3);
        Assert.Equal(0, m.Core.F & 0x28);
        Assert.True(m.Flag(Z80Core.FlagC));
        m.Run(4);
        Assert.Equal(0x28, m.Core.F & 0x28);
        m.Run(2);
        Assert.Equal(0x28, m.Core.F & 0x28);
        Assert.False(m.Flag(Z80Core.FlagC));
        Assert.True(m.Flag(Z80Core.FlagH));
    }

    // ---- registers ---------------------------------------------------------------

    /// <summary>R counts each M1 in its low seven bits and keeps bit 7 as LD R,A left it; LD A,R and LD A,I put IFF2 in P/V.</summary>
    [Fact]
    public void RCountsM1sInSevenBits()
    {
        var m = new Machine("""
                    org $0000
                    ld a,$FE
                    ld r,a          ; R = $FE
                    nop             ; $FF -> wraps in seven bits to $80
                    ei
                    ld a,r
                    di
                    ld a,i
            """);
        m.Run(2);
        Assert.Equal(0xFE, m.Core.R);
        m.Run(1);
        Assert.Equal(0xFF, m.Core.R);
        m.Run(2);
        Assert.Equal(0x82, m.Core.A);    // EI wraps $FF to $80 in seven bits, bit 7 kept; LD A,R's two M1s make $82
        Assert.True(m.Flag(Z80Core.FlagPV));
        Assert.True(m.Flag(Z80Core.FlagS));
        m.Run(2);
        Assert.False(m.Flag(Z80Core.FlagPV));
        Assert.Equal(0, m.Core.A);
        Assert.True(m.Flag(Z80Core.FlagZ));
    }

    [Fact]
    public void ExchangesSwapTheAlternateSet()
    {
        var m = new Machine("""
                    org $0000
                    ld a,$11
                    ld bc,$2233
                    ld de,$4455
                    ld hl,$6677
                    ex af,af'
                    exx
                    ld a,$88
                    ld bc,$99AA
                    exx
                    ex de,hl
            """);
        m.Run(10);
        Assert.Equal(0x11, m.Core.AlternateAF >> 8);
        Assert.Equal(0x2233, m.Core.BC);
        Assert.Equal(0x99AA, m.Core.AlternateBC);
        Assert.Equal(0x6677, m.Core.DE);
        Assert.Equal(0x4455, m.Core.HL);
    }

    /// <summary>The halves of IX and IY (undocumented), and (IX+d) leaving H and L as H and L.</summary>
    [Fact]
    public void IndexRegistersAndTheirHalves()
    {
        var m = new Machine("""
                    org $0000
                    ld ix,$1234
                    ld iy,$8000
                    ld a,ixh
                    ld ixl,a        ; IX = $1212
                    ld h,$55
                    ld (iy+2),h     ; H, not IYH
                    ld l,(iy+2)
                    inc iyl
                    add ix,ix
            """);
        m.Run(9);
        Assert.Equal(0x12, m.Core.A);
        Assert.Equal(0x55, m.Memory[0x8002]);
        Assert.Equal(0x55, m.Core.L);
        Assert.Equal(0x8001, m.Core.IY);
        Assert.Equal(0x2424, m.Core.IX);
    }

    /// <summary>DDCB with a register in bits 0-2 writes the result back to memory and copies it to the register.</summary>
    [Fact]
    public void IndexedBitOperationsCopyTheResult()
    {
        var m = new Machine("""
                    org $0000
                    ld ix,$8000
                    ld (ix+5),$81
                    rlc (ix+5),b    ; undocumented: DD CB 05 00
                    set 6,(ix+5),c  ; DD CB 05 F1
                    bit 6,(ix+5)
            """);
        m.Run(5);
        Assert.Equal(0x43, m.Memory[0x8005]);
        Assert.Equal(0x03, m.Core.B);
        Assert.Equal(0x43, m.Core.C);
        Assert.False(m.Flag(Z80Core.FlagZ));
        Assert.Equal(0x80, m.Core.F & 0x28 | 0x80);
        Assert.Equal(0x8005, m.Core.WZ);
    }

    // ---- timing ------------------------------------------------------------------

    /// <summary>The documented T-states, from Zilog's tables.</summary>
    [Theory]
    [InlineData(new byte[] { 0x00 }, 4)]                            // NOP
    [InlineData(new byte[] { 0x3E, 0x01 }, 7)]                      // LD A,n
    [InlineData(new byte[] { 0x01, 0x34, 0x12 }, 10)]               // LD BC,nn
    [InlineData(new byte[] { 0x7E }, 7)]                            // LD A,(HL)
    [InlineData(new byte[] { 0x36, 0x05 }, 10)]                     // LD (HL),n
    [InlineData(new byte[] { 0x32, 0x00, 0x80 }, 13)]               // LD (nn),A
    [InlineData(new byte[] { 0x22, 0x00, 0x80 }, 16)]               // LD (nn),HL
    [InlineData(new byte[] { 0x03 }, 6)]                            // INC BC
    [InlineData(new byte[] { 0x09 }, 11)]                           // ADD HL,BC
    [InlineData(new byte[] { 0x34 }, 11)]                           // INC (HL)
    [InlineData(new byte[] { 0xC5 }, 11)]                           // PUSH BC
    [InlineData(new byte[] { 0xC1 }, 10)]                           // POP BC
    [InlineData(new byte[] { 0xE3 }, 19)]                           // EX (SP),HL
    [InlineData(new byte[] { 0xCD, 0x00, 0x20 }, 17)]               // CALL nn
    [InlineData(new byte[] { 0xC9 }, 10)]                           // RET
    [InlineData(new byte[] { 0xFF }, 11)]                           // RST $38
    [InlineData(new byte[] { 0x18, 0x00 }, 12)]                     // JR
    [InlineData(new byte[] { 0x20, 0x00 }, 7)]                      // JR NZ, not taken (Z is set by the Load)
    [InlineData(new byte[] { 0xC3, 0x00, 0x20 }, 10)]               // JP nn
    [InlineData(new byte[] { 0xD3, 0x80 }, 11)]                     // OUT (n),A
    [InlineData(new byte[] { 0xDB, 0x80 }, 11)]                     // IN A,(n)
    [InlineData(new byte[] { 0xF9 }, 6)]                            // LD SP,HL
    [InlineData(new byte[] { 0xCB, 0x00 }, 8)]                      // RLC B
    [InlineData(new byte[] { 0xCB, 0x46 }, 12)]                     // BIT 0,(HL)
    [InlineData(new byte[] { 0xCB, 0xC6 }, 15)]                     // SET 0,(HL)
    [InlineData(new byte[] { 0xED, 0x78 }, 12)]                     // IN A,(C)
    [InlineData(new byte[] { 0xED, 0x42 }, 15)]                     // SBC HL,BC
    [InlineData(new byte[] { 0xED, 0x43, 0x00, 0x80 }, 20)]         // LD (nn),BC
    [InlineData(new byte[] { 0xED, 0x44 }, 8)]                      // NEG
    [InlineData(new byte[] { 0xED, 0x57 }, 9)]                      // LD A,I
    [InlineData(new byte[] { 0xED, 0x6F }, 18)]                     // RLD
    [InlineData(new byte[] { 0xED, 0xA0 }, 16)]                     // LDI
    [InlineData(new byte[] { 0xED, 0xA2 }, 16)]                     // INI
    [InlineData(new byte[] { 0xED, 0x00 }, 8)]                      // an undefined ED: NOP NOP
    [InlineData(new byte[] { 0xDD, 0x21, 0x00, 0x80 }, 14)]         // LD IX,nn
    [InlineData(new byte[] { 0xDD, 0x7E, 0x05 }, 19)]               // LD A,(IX+d)
    [InlineData(new byte[] { 0xDD, 0x36, 0x05, 0x01 }, 19)]         // LD (IX+d),n
    [InlineData(new byte[] { 0xDD, 0x34, 0x05 }, 23)]               // INC (IX+d)
    [InlineData(new byte[] { 0xDD, 0xE3 }, 23)]                     // EX (SP),IX
    [InlineData(new byte[] { 0xDD, 0xCB, 0x05, 0x46 }, 20)]         // BIT 0,(IX+d)
    [InlineData(new byte[] { 0xDD, 0xCB, 0x05, 0xC6 }, 23)]         // SET 0,(IX+d)
    [InlineData(new byte[] { 0xDD, 0x00 }, 8)]                      // DD before a NOP: a NOP with a prefix's M1 more
    public void InstructionsTakeTheirDocumentedTStates(byte[] program, int tStates)
    {
        var m = new Machine(program);
        m.Core.F = Z80Core.FlagZ;
        m.Core.HL = 0x8000;
        m.Core.IX = 0x8000;
        m.Core.BC = 0x0102;
        Assert.Equal(tStates, m.Step());
    }

    /// <summary>The machine cycles of some instructions, in order: fetches, reads, writes and I/O, with their addresses.</summary>
    [Fact]
    public void MachineCyclesComeInTheChipsOrder()
    {
        var m = new Machine(0xCD, 0x00, 0x20, 0xDD, 0xCB, 0x05, 0x06, 0xD3, 0x81);   // CALL $2000; RLC (IX+5); OUT ($81),A
        m.Core.IX = 0x8000;
        m.Core.A = 0x12;
        m.Memory[0x2000] = 0xC9;   // RET
        m.Step();
        Assert.Equal("F1000 R1001 R1002 WFEFF WFEFE", m.Cycles());
        m.Step();
        m.Step();
        Assert.Equal("F1003 F1004 R1005 R1006 R8005 W8005", m.Cycles());
        m.Step();
        Assert.Equal("F1007 R1008 O0081", m.Cycles());
        Assert.Equal((ushort)0x1281, m.Outputs.Single().Port);      // A on A8-A15
    }

    /// <summary>A repeating block instruction takes 21 T-states a round and 16 for the last, moving PC back between rounds.</summary>
    [Fact]
    public void LdirRepeatsAndCpirStopsOnAMatch()
    {
        var m = new Machine("""
                    org $0000
                    ld hl,text
                    ld de,$8000
                    ld bc,4
                    ldir
                    ld hl,text
                    ld bc,10
                    ld a,'8'
                    cpir
            text:   db "Z80!"
            """);
        m.Run(3);
        Assert.Equal(21, m.Step());
        Assert.Equal(21, m.Step());
        Assert.Equal(21, m.Step());
        Assert.Equal(16, m.Step());
        Assert.Equal("Z80!", System.Text.Encoding.ASCII.GetString(m.Memory, 0x8000, 4));
        Assert.Equal(0, m.Core.BC);
        Assert.False(m.Flag(Z80Core.FlagPV));
        m.Run(3);
        Assert.Equal(21, m.Step());     // 'Z'
        Assert.Equal(16, m.Step());     // '8' matches
        Assert.True(m.Flag(Z80Core.FlagZ));
        Assert.Equal(8, m.Core.BC);
        Assert.True(m.Flag(Z80Core.FlagPV));
    }

    /// <summary>INIR and OTIR move bytes between memory and a port, B counting, and OUTI's port has the new B on A8-A15.</summary>
    [Fact]
    public void BlockInputAndOutput()
    {
        var m = new Machine("""
                    org $0000
                    ld hl,$8000
                    ld bc,$0380     ; three bytes from port $80
                    inir
                    ld hl,$8000
                    ld bc,$0281     ; two bytes to port $81
                    otir
            """);
        m.Inputs[0x80] = 0x5A;
        m.Run(6);
        Assert.Equal(0x5A, m.Memory[0x8002]);
        Assert.Equal(0, m.Core.B);
        Assert.True(m.Flag(Z80Core.FlagZ));
        m.Run(4);
        Assert.Equal([(ushort)0x0181, (ushort)0x0081], m.Outputs.Select(o => o.Port));
        Assert.All(m.Outputs, o => Assert.Equal(0x5A, o.Value));
    }

    /// <summary>/WAIT adds wait states in T2 of a memory cycle and after the automatic one of an I/O cycle.</summary>
    [Fact]
    public void WaitAddsWaitStates()
    {
        var m = new Machine(0x3A, 0x00, 0x80, 0xDB, 0x80);   // LD A,($8000); IN A,($80)
        m.WaitStates = 2;
        Assert.Equal(13 + 4 * 2, m.Step());       // four memory cycles, two waits each
        Assert.Equal(11 + 3 * 2, m.Step());       // fetch, read, I/O
        Assert.Equal(14, m.Core.WaitStates);
    }

    // ---- interrupts ---------------------------------------------------------------

    /// <summary>Mode 1: the acknowledge with its two automatic waits, then a call to $0038 in 13 T-states.</summary>
    [Fact]
    public void Mode1CallsThirtyEight()
    {
        var m = new Machine(0xFB, 0x00, 0x00);  // EI; NOP; NOP
        m.Core.InterruptMode = 1;
        m.Core.IntAsserted = true;
        m.Step();                               // EI: none taken straight after
        Assert.Equal(4, m.Step());              // the NOP after it runs
        Assert.Equal(13, m.Step());
        Assert.Equal("A WFEFF WFEFE", m.Cycles());
        Assert.Equal(0x0038, m.Core.PC);
        Assert.False(m.Core.Iff1 || m.Core.Iff2);
        Assert.Equal(0x10, m.Memory[0xFEFF]);
        Assert.Equal(0x02, m.Memory[0xFEFE]);
        Assert.Equal("INT (IM 1)", m.Core.LastInterrupt);
    }

    /// <summary>Mode 2: the device's byte and I make a pointer to the handler's address; 19 T-states.</summary>
    [Fact]
    public void Mode2CallsThroughTheVectorTable()
    {
        var m = new Machine(0xED, 0x5E, 0x3E, 0x20, 0xED, 0x47, 0xFB, 0x00, 0x00);  // IM 2; LD A,$20; LD I,A; EI; NOP
        m.Memory[0x2016] = 0x34;
        m.Memory[0x2017] = 0x12;
        m.Vector = 0x16;
        m.Run(4);
        m.Core.IntAsserted = true;
        m.Step();
        Assert.Equal(19, m.Step());
        Assert.Equal("A WFEFF WFEFE R2016 R2017", m.Cycles());
        Assert.Equal(0x1234, m.Core.PC);
        Assert.Equal(0x16, m.Core.LastAcknowledgeByte);
    }

    /// <summary>Mode 0: the byte on the bus is executed — an RST, in 13 T-states.</summary>
    [Fact]
    public void Mode0ExecutesTheByteOnTheBus()
    {
        var m = new Machine(0xFB, 0x00, 0x00);
        m.Vector = 0xD7;    // RST $10
        m.Step();
        m.Core.IntAsserted = true;
        m.Step();
        Assert.Equal(13, m.Step());
        Assert.Equal(0x0010, m.Core.PC);
        Assert.Equal(0x02, m.Memory[0xFEFE]);
    }

    /// <summary>DI holds /INT off; EI lets the instruction after it run first, even with the line low already.</summary>
    [Fact]
    public void EiWaitsOneInstruction()
    {
        var m = new Machine(0xF3, 0x00, 0xFB, 0x3C, 0x3C);   // DI; NOP; EI; INC A; INC A
        m.Core.InterruptMode = 1;
        m.Core.IntAsserted = true;
        m.Core.A = 0;
        m.Run(3);
        Assert.Equal(0x1003, m.Core.PC);
        m.Step();
        Assert.Equal(1, m.Core.A);
        m.Step();
        Assert.Equal(0x0038, m.Core.PC);
        Assert.Equal(1, m.Core.A);
    }

    /// <summary>NMI is an edge, taken ahead of /INT: a fetch thrown away, PC pushed, $0066, IFF1 cleared and IFF2 kept; RETN puts it back.</summary>
    [Fact]
    public void NmiCallsSixtySixAndRetnRestoresIff1()
    {
        var m = new Machine("""
                    org $0000
                    ei
                    nop
                    nop
                    org $0066
                    retn
            """);
        m.Step();
        m.Core.NmiAsserted = true;
        m.Step();
        Assert.Equal(11, m.Step());
        Assert.Equal(0x0066, m.Core.PC);
        Assert.False(m.Core.Iff1);
        Assert.True(m.Core.Iff2);
        Assert.Equal("NMI", m.Core.LastInterrupt);
        m.Step();
        Assert.True(m.Core.Iff1);
        Assert.Equal(0x0002, m.Core.PC);

        // Still low: no second NMI, an edge being what counts.
        m.Step();
        Assert.Equal(0x0003, m.Core.PC);
    }

    /// <summary>HALT runs NOPs at the next address, R counting, until an interrupt; the address pushed is the one after the HALT.</summary>
    [Fact]
    public void HaltRunsNopsUntilAnInterrupt()
    {
        var m = new Machine(0xFB, 0x76, 0x3C);   // EI; HALT; INC A
        m.Core.InterruptMode = 1;
        m.Run(2);
        Assert.True(m.Core.Halted);
        var r = m.Core.R;
        Assert.Equal(4, m.Step());
        Assert.Equal("F1002", m.Cycles());
        Assert.Equal(0x1002, m.Core.PC);
        Assert.Equal((r + 1) & 0x7F, m.Core.R & 0x7F);
        m.Core.IntAsserted = true;
        m.Step();
        m.Step();
        Assert.False(m.Core.Halted);
        Assert.Equal(0x0038, m.Core.PC);
        Assert.Equal(0x02, m.Memory[0xFEFE]);
        Assert.Equal(0x10, m.Memory[0xFEFF]);
    }

    /// <summary>The NMOS part's fault: an interrupt taken straight after LD A,I leaves P/V clear, though IFF2 was set.</summary>
    [Fact]
    public void AnInterruptAfterLdAiClearsParity()
    {
        var quiet = new Machine(0xFB, 0x00, 0xED, 0x57, 0x00);   // EI; NOP; LD A,I; NOP
        quiet.Run(3);
        Assert.True(quiet.Flag(Z80Core.FlagPV));
        Assert.True(quiet.Core.LdAirLast);

        var m = new Machine(0xFB, 0x00, 0xED, 0x57, 0x00);
        m.Core.InterruptMode = 1;
        m.Run(2);
        m.Core.IntAsserted = true;
        m.Run(2);
        Assert.Equal(0x0038, m.Core.PC);
        Assert.False(m.Flag(Z80Core.FlagPV));
    }

    /// <summary>/BUSREQ: the bus let go before the next machine cycle, for as long as it is held.</summary>
    [Fact]
    public void BusRequestReleasesTheBusBetweenCycles()
    {
        var m = new Machine(0x3E, 0x05, 0x3C);   // LD A,5; INC A
        m.Core.BusRequested = true;
        for (var i = 0; i < 12; i++) m.TState();
        Assert.True(m.Core.BusAcknowledged);
        Assert.Equal(Z80CycleKind.Released, m.Core.Kind);
        m.Core.BusRequested = false;
        for (var i = 0; i < 20; i++) m.TState();
        Assert.Equal(6, m.Core.A);
    }

    /// <summary>/RESET: PC, I, R and the interrupt state cleared, AF and SP $FFFF, mode 0, and a start at $0000.</summary>
    [Fact]
    public void ResetClearsAndStartsAtZero()
    {
        var m = new Machine(0xED, 0x5E, 0xFB, 0x00);    // IM 2; EI; NOP
        m.Core.I = 0x12;
        m.Run(3);
        m.Core.ResetAsserted = true;
        m.TState();
        m.TState();
        Assert.Equal(Z80CycleKind.Reset, m.Core.Kind);
        m.Core.ResetAsserted = false;
        m.TState();
        Assert.Equal(Z80CycleKind.Fetch, m.Core.Kind);
        Assert.Equal(0x0000, m.Core.Address);
        Assert.Equal(0, m.Core.InterruptMode);
        Assert.False(m.Core.Iff1);
        Assert.Equal(0, m.Core.I);
        Assert.Equal(0xFFFF, m.Core.SP);
        Assert.Equal(0xFFFF, m.Core.AF);
        Assert.Equal("RESET", m.Core.LastInterrupt);
    }

    // ---- the disassembler and the assembler ------------------------------------------

    [Theory]
    [InlineData(new byte[] { 0x3E, 0x41 }, "LD A,$41")]
    [InlineData(new byte[] { 0x21, 0x34, 0x12 }, "LD HL,$1234")]
    [InlineData(new byte[] { 0xDD, 0x36, 0x05, 0x20 }, "LD (IX+$05),$20")]
    [InlineData(new byte[] { 0xFD, 0x7E, 0xFD }, "LD A,(IY-$03)")]
    [InlineData(new byte[] { 0x20, 0xFE }, "JR NZ,$1000")]
    [InlineData(new byte[] { 0x10, 0x02 }, "DJNZ $1004")]
    [InlineData(new byte[] { 0xDB, 0x80 }, "IN A,($80)")]
    [InlineData(new byte[] { 0xED, 0x79 }, "OUT (C),A")]
    [InlineData(new byte[] { 0x08 }, "EX AF,AF'")]
    [InlineData(new byte[] { 0xFF }, "RST $38")]
    [InlineData(new byte[] { 0xED, 0x5E }, "IM 2")]
    [InlineData(new byte[] { 0xED, 0x4D }, "RETI")]
    [InlineData(new byte[] { 0xCB, 0x7E }, "BIT 7,(HL)")]
    [InlineData(new byte[] { 0xDD, 0xCB, 0x05, 0xC6 }, "SET 0,(IX+$05)")]
    [InlineData(new byte[] { 0xDD, 0xCB, 0x05, 0x00 }, "RLC (IX+$05),B *")]
    [InlineData(new byte[] { 0xDD, 0x26, 0x12 }, "LD IXH,$12 *")]
    [InlineData(new byte[] { 0xCB, 0x30 }, "SLL B *")]
    [InlineData(new byte[] { 0xED, 0x70 }, "IN F,(C) *")]
    [InlineData(new byte[] { 0xED, 0x71 }, "OUT (C),0 *")]
    [InlineData(new byte[] { 0xED, 0x4C }, "NEG *")]
    [InlineData(new byte[] { 0xED, 0x00 }, "NOP *")]
    [InlineData(new byte[] { 0xDD, 0x41 }, "LD B,C *")]
    [InlineData(new byte[] { 0xFD, 0xE9 }, "JP (IY)")]
    public void TheDisassemblerUsesZilogsSyntax(byte[] bytes, string text)
    {
        var (got, length) = OpcodesZ80.Disassemble(0x1000, a => a - 0x1000 < bytes.Length ? bytes[a - 0x1000] : (byte)0);
        Assert.Equal(text, got);
        Assert.Equal(bytes.Length, length);
    }

    private static IEnumerable<byte[]> EveryOpcode()
    {
        for (var op = 0; op < 256; op++)
            if (op is not (0xCB or 0xDD or 0xED or 0xFD)) yield return [(byte)op];
        for (var op = 0; op < 256; op++) yield return [0xCB, (byte)op];
        for (var op = 0; op < 256; op++) yield return [0xED, (byte)op];
        foreach (var prefix in new byte[] { 0xDD, 0xFD })
        {
            for (var op = 0; op < 256; op++)
                if (op is not (0xCB or 0xDD or 0xED or 0xFD)) yield return [prefix, (byte)op];
            for (var op = 0; op < 256; op++) yield return [prefix, 0xCB, 0x05, (byte)op];
        }
    }

    /// <summary>For every opcode in every prefix space, the disassembler's length is how far the core moves PC (jumps aside).</summary>
    [Fact]
    public void TheDisassemblerAndTheCoreAgreeOnEveryLength()
    {
        var checkedCount = 0;
        foreach (var opcode in EveryOpcode())
        {
            byte[] program = [.. opcode, 0x05, 0x06, 0x07, 0x08];
            var (text, length) = OpcodesZ80.Disassemble(0x1000, a => a - 0x1000 < program.Length ? program[a - 0x1000] : (byte)0);
            var mnemonic = text.Split(' ')[0];
            if (mnemonic is "JP" or "JR" or "DJNZ" or "CALL" or "RET" or "RETI" or "RETN" or "RST" or "HALT" ||
                text.StartsWith("LD SP", StringComparison.Ordinal) || text.Contains("IR", StringComparison.Ordinal) || text.Contains("DR", StringComparison.Ordinal))
                continue;

            var m = new Machine(program);
            m.Core.BC = 0;
            m.Step();
            Assert.True(length == m.Core.PC - 0x1000, $"{string.Join(" ", opcode.Select(b => b.ToString("X2")))} ({text}): disassembler {length}, core {m.Core.PC - 0x1000}");
            checkedCount++;
        }

        Assert.True(checkedCount > 1200, $"{checkedCount}");
    }

    /// <summary>
    /// Every opcode with a range of operands disassembles to text that assembles back to its bytes;
    /// the duplicates (a second NEG, RETN or IM, an ED that does nothing, a prefix that changes
    /// nothing, ED's own LD (nn),HL) assemble to the first opcode instead, which disassembles to the
    /// same text.
    /// </summary>
    [Fact]
    public void TheDisassemblyAssemblesBackToTheSameBytes()
    {
        var exact = 0;
        var duplicates = 0;
        foreach (var opcode in EveryOpcode())
        {
            foreach (var operands in new byte[][] { [0x12, 0x34], [0xFE, 0x80], [0x00, 0x00] })
            {
                byte[] bytes = [.. opcode, .. operands];
                if (opcode.Length == 4) bytes = [opcode[0], opcode[1], operands[0], opcode[3]];
                var (text, length) = OpcodesZ80.Disassemble(0x2000, a => a - 0x2000 < bytes.Length ? bytes[a - 0x2000] : (byte)0);
                var assembled = AssemblerZ80.Assemble($"    org $2000\n    {text}\n").Image(0x2000, 8);
                if (bytes.Take(length).SequenceEqual(assembled.Take(length)))
                {
                    exact++;
                    continue;
                }

                // Nothing unprefixed or after CB is a duplicate.
                Assert.True(opcode[0] is 0xDD or 0xED or 0xFD, $"{text} did not assemble back");

                // A duplicate (ED $63 and $6B are Zilog's own second LD (nn),HL and LD HL,(nn)): the
                // assembler gives the first opcode, which reads back as the same instruction.
                var (again, _) = OpcodesZ80.Disassemble(0x2000, a => assembled[(a - 0x2000) & 7]);
                static string Plain(string t) => t.EndsWith(" *", StringComparison.Ordinal) ? t[..^2] : t;
                Assert.Equal(Plain(text), Plain(again));
                duplicates++;
            }
        }

        Assert.True(exact > 3300, $"{exact}");
        Assert.True(duplicates < 2000, $"{duplicates}");
    }

    // ---- a program -------------------------------------------------------------

    /// <summary>
    /// The self-checking program: 5! by recursion through the stack, a string copied with LDIR and
    /// searched with CPIR, a 16-bit multiply by shifts and adds, a BCD sum with DAA, a table summed
    /// through IX, and a jump through a table with JP (HL) — each result compared, and $01 written to
    /// $8000 only if every one is right. The circuit tests run it from ROM.
    /// </summary>
    internal const string SelfCheck = """
                    org $0000
            start:  ld sp,$FF00
                    xor a
                    ld (result),a
            ; 5! by recursion: A in, HL out
                    ld a,5
                    call fact
                    ld de,120
                    or a
                    sbc hl,de
                    jp nz,fail
            ; copy "Z80!" to $8100 and find the '!'
                    ld hl,text
                    ld de,$8100
                    ld bc,4
                    ldir
                    ld hl,$8100
                    ld bc,4
                    ld a,'!'
                    cpir
                    jp nz,fail
                    ld a,c
                    or a
                    jp nz,fail
            ; $1234 * $0056 = $61D78, low sixteen bits $1D78
                    ld de,$1234
                    ld bc,$0056
                    call mul16
                    ld de,$1D78
                    or a
                    sbc hl,de
                    jp nz,fail
            ; 0.38 + 0.47 = 0.85 in BCD
                    ld a,$38
                    add a,$47
                    daa
                    cp $85
                    jp nz,fail
            ; the four bytes of the table summed through IX: 10 + 20 + 30 + 40
                    ld ix,table
                    xor a
                    ld b,4
            sum:    add a,(ix+0)
                    inc ix
                    djnz sum
                    cp 100
                    jp nz,fail
            ; a jump through a table
                    ld hl,(jumps+2)
                    jp (hl)
            fail:   jr fail
            good:   ld a,1
                    ld (result),a
            done:   jr done

            fact:   cp 2
                    jr nc,more
                    ld hl,1
                    ret
            more:   push af
                    dec a
                    call fact       ; HL = (n-1)!
                    pop af
                    ld b,a
                    ex de,hl
                    ld hl,0
            times:  add hl,de       ; HL = n * (n-1)!
                    djnz times
                    ret

            ; HL = DE * BC, sixteen bits kept
            mul16:  ld hl,0
                    ld a,16
            bit:    add hl,hl
                    sla c
                    rl b
                    jr nc,skip
                    add hl,de
            skip:   dec a
                    jr nz,bit
                    ret

            text:   db "Z80!"
            table:  db 10,20,30,40
            jumps:  dw fail,good
            result  equ $8000
            """;

    /// <summary>The self-checking program, on the core alone.</summary>
    [Fact]
    public void ASelfCheckingProgramPasses()
    {
        var m = new Machine(SelfCheck);

        for (var i = 0; i < 2000 && m.Memory[0x8000] == 0; i++) m.Step();
        Assert.Equal(1, m.Memory[0x8000]);
    }
}
