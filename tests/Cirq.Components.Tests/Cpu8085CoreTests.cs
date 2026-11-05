using Cirq.Components.Processors;

namespace Cirq.Components.Tests;

/// <summary>
/// The 8085 core on its own, against a 64K array and a table of ports: every opcode's T-states and
/// machine cycles as Intel's tables give them (both ways for the conditional ones), each instruction
/// group's results and flags — the undocumented V and K and the undocumented instructions included —
/// TRAP, RST 7.5, 6.5 and 5.5 with their masks and priorities, INTR answered with an RST and with a
/// CALL, EI's delay, HLT, RIM and SIM with SID and SOD, READY's wait states, HOLD, reset, the
/// disassembler and the test-side assembler reading each other back, and a self-checking program
/// assembled from source.
/// </summary>
public class Cpu8085CoreTests
{
    /// <summary>A core with its memory and ports, and the machine cycles it has run as <c>F0000 R0001 W8000 I4040 O4141 A B G H x</c>.</summary>
    internal sealed class Machine
    {
        public byte[] Memory { get; } = new byte[0x10000];

        public I8085Core Core { get; } = new();

        /// <summary>What IN reads, by port; $FF where nothing is set.</summary>
        public Dictionary<int, byte> Inputs { get; } = [];

        /// <summary>Every OUT: the address on the bus (the port on both halves) and the byte.</summary>
        public List<(ushort Address, byte Value)> Outputs { get; } = [];

        /// <summary>The bytes INTR acknowledges find on the bus, in order; $FF when they run out.</summary>
        public Queue<byte> Acknowledge { get; } = new();

        /// <summary>Wait states READY asks for in each cycle that samples it.</summary>
        public int WaitStates { get; set; }

        private int _waitsLeft;

        public List<string> Trace { get; } = [];

        public Machine(string source, ushort start = 0x0000)
        {
            var output = Assembler8085.Assemble(source);
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
            if (c.State == I8085TState.T1) _waitsLeft = WaitStates;
            if (c.Kind == I8085CycleKind.MemoryWrite && c.State == I8085TState.T3) Memory[c.Address] = c.DataOut;
            if (c.Kind == I8085CycleKind.IoWrite && c.State == I8085TState.T3) Outputs.Add((c.Address, c.DataOut));
            if (c.SamplesData)
            {
                value = c.Kind switch
                {
                    I8085CycleKind.IoRead => Inputs.GetValueOrDefault(c.Address & 0xFF, (byte)0xFF),
                    I8085CycleKind.InterruptAcknowledge => Acknowledge.Count > 0 ? Acknowledge.Dequeue() : (byte)0xFF,
                    _ => Memory[c.Address],
                };
            }

            if (c.SamplesReady)
            {
                c.ReadyLow = _waitsLeft > 0;
                if (_waitsLeft > 0) _waitsLeft--;
            }

            if (c.State == I8085TState.T1 || c.Kind is I8085CycleKind.Hold or I8085CycleKind.Halt)
            {
                Trace.Add(c.Kind switch
                {
                    I8085CycleKind.OpcodeFetch => $"F{c.Address:X4}",
                    I8085CycleKind.MemoryRead => $"R{c.Address:X4}",
                    I8085CycleKind.MemoryWrite => $"W{c.Address:X4}",
                    I8085CycleKind.IoRead => $"I{c.Address:X4}",
                    I8085CycleKind.IoWrite => $"O{c.Address:X4}",
                    I8085CycleKind.InterruptAcknowledge => "A",
                    I8085CycleKind.RestartAcknowledge => "G",
                    I8085CycleKind.BusIdle => "B",
                    I8085CycleKind.Halt => "H",
                    I8085CycleKind.Hold => "x",
                    _ => "?",
                });
            }

            c.Complete(value);
        }

        /// <summary>One instruction (or interrupt response, or HALT state): its T-states.</summary>
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

    // ---- T-states and machine cycles -----------------------------------------------------

    /// <summary>
    /// Intel's states for every opcode, the conditional ones taken and not: 4 or 6 for the fetch,
    /// 3 for each read, write, I/O and bus idle cycle, HLT 5 to its first HALT state.
    /// </summary>
    private static (int NotTaken, int Taken) Expected(int op)
    {
        var x = op >> 6;
        var y = (op >> 3) & 7;
        var z = op & 7;
        var q = y & 1;
        switch (x)
        {
            case 1:
                return op == 0x76 ? (5, 5) : y == 6 || z == 6 ? (7, 7) : (4, 4);
            case 2:
                return z == 6 ? (7, 7) : (4, 4);
            case 0:
                return z switch
                {
                    0 => y switch { 0 or 4 or 6 => (4, 4), 2 => (7, 7), _ => (10, 10) },   // NOP RIM SIM; ARHL; DSUB RDEL LDHI LDSI
                    1 => (10, 10),                                                          // LXI, DAD
                    2 => (q, y >> 1) switch { (_, 0 or 1) => (7, 7), (_, 2) => (16, 16), _ => (13, 13) },
                    3 => (6, 6),
                    4 or 5 => y == 6 ? (10, 10) : (4, 4),
                    6 => y == 6 ? (10, 10) : (7, 7),
                    _ => (4, 4),
                };
            default:
                return z switch
                {
                    0 => (6, 12),
                    1 => q == 0 ? (10, 10) : (y >> 1) switch { 0 => (10, 10), 1 => (10, 10), _ => (6, 6) },  // POP; RET SHLX PCHL SPHL
                    2 => (7, 10),
                    3 => y switch { 0 => (10, 10), 1 => (6, 12), 2 or 3 => (10, 10), 4 => (16, 16), _ => (4, 4) },
                    4 => (9, 18),
                    5 => q == 0 ? (12, 12) : (y >> 1) switch { 0 => (18, 18), 2 => (10, 10), _ => (7, 10) },
                    6 => (7, 7),
                    _ => (12, 12),
                };
        }
    }

    /// <summary>Every opcode, conditions false then true (for the flags they test), against Intel's table.</summary>
    [Fact]
    public void EveryOpcodeTakesIntelsStates()
    {
        for (var op = 0; op < 256; op++)
        {
            var (notTaken, taken) = Expected(op);
            foreach (var condition in new[] { false, true })
            {
                var m = new Machine((byte)op, 0x00, 0x20);
                m.Core.HL = 0x8000;
                m.Core.DE = 0x9000;
                m.Core.F = 0;

                // Make the instruction's condition hold, or fail, whatever it tests.
                var y = (op >> 3) & 7;
                var tested = op switch
                {
                    0xCB => I8085Core.FlagV,
                    0xDD or 0xFD => I8085Core.FlagK,
                    _ => (y >> 1) switch { 0 => I8085Core.FlagZ, 1 => I8085Core.FlagC, 2 => I8085Core.FlagP, _ => I8085Core.FlagS },
                };
                var wantsSet = op is 0xCB or 0xFD || ((op & 0xC0) == 0xC0 && (op & 7) is 0 or 2 or 4 && (y & 1) == 1);
                var setIt = condition == wantsSet;
                m.Core.F = setIt ? tested : (byte)0;

                var conditional = (op & 0xC7) is 0xC0 or 0xC2 or 0xC4 || op is 0xCB or 0xDD or 0xFD;
                var states = m.Step();
                var expected = conditional && condition ? taken : notTaken;
                Assert.True(states == expected, $"opcode ${op:X2} ({Opcodes8085.Disassemble(0, a => (byte)op).Text}){(conditional ? condition ? " taken" : " not taken" : "")}: {states} T-states, Intel says {expected}");
                if (!conditional) break;
            }
        }
    }

    /// <summary>The machine cycles in their order: fetch, reads, writes, bus idle, I/O, as Intel's chart lists them.</summary>
    [Theory]
    [InlineData("call $1234", "F1000 R1001 R1002 WFEFF WFEFE")]
    [InlineData("push b", "F1000 WFEFF WFEFE")]
    [InlineData("pop b", "F1000 RFF00 RFF01")]
    [InlineData("xthl", "F1000 RFF00 RFF01 WFF01 WFF00")]
    [InlineData("dad b", "F1000 B B")]
    [InlineData("dsub", "F1000 B B")]
    [InlineData("arhl", "F1000 B")]
    [InlineData("ldhi 5", "F1000 R1001 B")]
    [InlineData("shlx", "F1000 W9000 W9001")]
    [InlineData("lhlx", "F1000 R9000 R9001")]
    [InlineData("out $41", "F1000 R1001 O4141")]
    [InlineData("in $40", "F1000 R1001 I4040")]
    [InlineData("shld $2000", "F1000 R1001 R1002 W2000 W2001")]
    [InlineData("lhld $2000", "F1000 R1001 R1002 R2000 R2001")]
    [InlineData("mvi m,$55", "F1000 R1001 W8000")]
    [InlineData("inr m", "F1000 R8000 W8000")]
    [InlineData("rst 3", "F1000 WFEFF WFEFE")]
    [InlineData("jz $1234", "F1000 R1001")]
    [InlineData("cz $1234", "F1000 R1001")]
    public void MachineCyclesComeInIntelsOrder(string instruction, string cycles)
    {
        var m = new Machine(Assembler8085.Assemble($"    org $1000\n    {instruction}\n").Image(0x1000, 4));
        m.Core.HL = 0x8000;
        m.Core.DE = 0x9000;
        m.Core.F = 0;
        m.Step();
        Assert.Equal(cycles, m.Cycles());
    }

    /// <summary>Each machine cycle's T-states: the fetch's T4 (and T5, T6), the reads' T1-T3, bus idle cycles with no ALE.</summary>
    [Fact]
    public void TheStatesOfEachCycleAreNamed()
    {
        var m = new Machine(0x0B, 0x09);     // DCX B; DAD B
        List<(I8085CycleKind, I8085TState, bool Ale)> seen = [];
        for (var i = 0; i < 16; i++)
        {
            seen.Add((m.Core.Kind, m.Core.State, m.Core.AddressLatchEnable));
            m.TState();
        }

        Assert.Equal(
        [
            (I8085CycleKind.OpcodeFetch, I8085TState.T1, true), (I8085CycleKind.OpcodeFetch, I8085TState.T2, false),
            (I8085CycleKind.OpcodeFetch, I8085TState.T3, false), (I8085CycleKind.OpcodeFetch, I8085TState.T4, false),
            (I8085CycleKind.OpcodeFetch, I8085TState.T5, false), (I8085CycleKind.OpcodeFetch, I8085TState.T6, false),
            (I8085CycleKind.OpcodeFetch, I8085TState.T1, true), (I8085CycleKind.OpcodeFetch, I8085TState.T2, false),
            (I8085CycleKind.OpcodeFetch, I8085TState.T3, false), (I8085CycleKind.OpcodeFetch, I8085TState.T4, false),
            (I8085CycleKind.BusIdle, I8085TState.T1, false), (I8085CycleKind.BusIdle, I8085TState.T2, false),
            (I8085CycleKind.BusIdle, I8085TState.T3, false), (I8085CycleKind.BusIdle, I8085TState.T1, false),
            (I8085CycleKind.BusIdle, I8085TState.T2, false), (I8085CycleKind.BusIdle, I8085TState.T3, false),
        ], seen);
    }

    /// <summary>READY low in T2 adds wait states to reads and writes; bus idle cycles ignore it.</summary>
    [Fact]
    public void ReadyAddsWaitStates()
    {
        var m = new Machine("""
                    org $1000
                    lxi h,$8000
                    mov m,a
                    dad b
            """, 0x1000) { WaitStates = 2 };
        Assert.Equal(10 + 3 * 2, m.Step());       // three cycles, two waits each
        Assert.Equal(7 + 2 * 2, m.Step());
        Assert.Equal(10 + 2, m.Step());            // the fetch waits; the bus idle cycles do not
        Assert.Equal(12, m.Core.WaitStates);
    }

    // ---- results and flags -----------------------------------------------------

    [Fact]
    public void LoadsMoveBytesAndWords()
    {
        var m = new Machine("""
                    org $0000
                    mvi a,$41
                    mov b,a
                    lxi h,$8000
                    mov m,b
                    sta $8001
                    lhld $8000
                    lxi b,$1234
                    shld $8002
                    lxi d,$8002
                    ldax d
                    mov c,a
                    xchg
                    mvi m,$99
                    lda $8002
                    stax b
                    sphl
            """);
        m.Run(16);
        Assert.Equal(0x41, m.Memory[0x8000]);
        Assert.Equal(0x41, m.Memory[0x8001]);
        Assert.Equal(0x99, m.Memory[0x8002]);
        Assert.Equal(0x41, m.Memory[0x8003]);
        Assert.Equal(0x99, m.Core.A);
        Assert.Equal(0x99, m.Memory[0x1241]);       // STAX B: B $12, C $41
        Assert.Equal(0x4141, m.Core.DE);
        Assert.Equal(0x8002, m.Core.HL);
        Assert.Equal(0x8002, m.Core.SP);
    }

    /// <summary>ADD and ADC: CY, AC, S, Z, P, and the undocumented V and K (V exclusive-ORed with the sign).</summary>
    [Theory]
    [InlineData(0x80, 0x87, 0x40, false, "SzKapvc", 0xC7)]
    [InlineData(0x80, 0x7F, 0x01, false, "SzkApVc", 0x80)]     // 127 + 1: signed overflow
    [InlineData(0x80, 0xFF, 0x01, false, "sZkAPvC", 0x00)]
    [InlineData(0x88, 0x0F, 0x01, true, "szkAPvc", 0x11)]
    [InlineData(0x80, 0x80, 0x80, false, "sZKaPVC", 0x00)]     // -128 + -128
    public void AdditionsSetEveryFlag(int opcode, int a, int b, bool carry, string flags, int result)
    {
        var m = new Machine((byte)opcode);
        m.Core.A = (byte)a;
        m.Core.B = (byte)b;
        m.Core.F = carry ? I8085Core.FlagC : (byte)0;
        m.Step();
        Assert.Equal(result, m.Core.A);
        Assert.Equal(flags, Cpu8085.Flags(m.Core.F));
    }

    /// <summary>SUB, SBB and CMP: CY the borrow, AC the carry out of bit 3 of A + ~B + 1, V the signed overflow, K set when A is less than B as signed numbers.</summary>
    [Theory]
    [InlineData(0x90, 0x50, 0x70, false, "SzKApvC", 0xE0)]     // 80 - 112
    [InlineData(0x90, 0x50, 0xB0, false, "SzkAPVC", 0xA0)]     // 80 - -80: overflow, and 80 is the larger
    [InlineData(0xB8, 0xD0, 0x70, false, "szKAPVc", 0xD0)]     // CMP: -48 against 112; A kept
    [InlineData(0x98, 0x10, 0x10, true, "SzKaPvC", 0xFF)]      // SBB with a borrow in
    [InlineData(0xB8, 0x42, 0x42, false, "sZkAPvc", 0x42)]     // CMP equal
    public void SubtractionsSetEveryFlag(int opcode, int a, int b, bool carry, string flags, int result)
    {
        var m = new Machine((byte)opcode);
        m.Core.A = (byte)a;
        m.Core.B = (byte)b;
        m.Core.F = carry ? I8085Core.FlagC : (byte)0;
        m.Step();
        Assert.Equal(result, m.Core.A);
        Assert.Equal(flags, Cpu8085.Flags(m.Core.F));
    }

    /// <summary>ANA sets AC on the 8085 (the 8080 ORs bit 3 of the operands); XRA and ORA clear it; all three clear CY and V, so K is the sign.</summary>
    [Theory]
    [InlineData(0xA0, 0xF0, 0x3C, "szkAPvc", 0x30)]
    [InlineData(0xA0, 0x80, 0x80, "SzKApvc", 0x80)]
    [InlineData(0xA8, 0x5A, 0x5A, "sZkaPvc", 0x00)]
    [InlineData(0xB0, 0x01, 0x80, "SzKaPvc", 0x81)]
    [InlineData(0xE6, 0x0F, 0x00, "sZkAPvc", 0x00)]     // ANI 0
    [InlineData(0xEE, 0x0F, 0x00, "szkaPvc", 0x0F)]     // XRI 0
    [InlineData(0xF6, 0x01, 0x00, "szkapvc", 0x01)]     // ORI 0
    public void LogicalOperationsClearTheCarries(int opcode, int a, int b, string flags, int result)
    {
        var m = new Machine((byte)opcode, 0x00);
        m.Core.A = (byte)a;
        m.Core.B = (byte)b;
        m.Core.F = I8085Core.FlagC | I8085Core.FlagV | I8085Core.FlagAC;
        m.Step();
        Assert.Equal(result, m.Core.A);
        Assert.Equal(flags, Cpu8085.Flags(m.Core.F));
    }

    /// <summary>The immediate forms do what the register forms do, a byte later.</summary>
    [Fact]
    public void ImmediatesUseTheByteAfter()
    {
        var m = new Machine("""
                    org $0000
                    mvi a,$40
                    adi $3F
                    aci $00
                    sui $10
                    sbi $0F
                    cpi $70
            """);
        m.Run(3);
        Assert.Equal(0x7F, m.Core.A);
        m.Step();
        Assert.Equal(0x6F, m.Core.A);
        m.Step();
        Assert.Equal(0x60, m.Core.A);
        m.Step();
        Assert.True(m.Flag(I8085Core.FlagC));
        Assert.True(m.Flag(I8085Core.FlagK));       // $60 is less than $70
        Assert.Equal(0x60, m.Core.A);
    }

    /// <summary>INR and DCR: everything but CY; V only from $7F up and $80 down; DCR's AC set unless the low digit was 0.</summary>
    [Theory]
    [InlineData(0x04, 0x7F, true, "SzkApVC", 0x80)]
    [InlineData(0x04, 0xFF, false, "sZkAPvc", 0x00)]
    [InlineData(0x05, 0x80, false, "szKapVc", 0x7F)]
    [InlineData(0x05, 0x00, true, "SzKaPvC", 0xFF)]
    [InlineData(0x05, 0x01, false, "sZkAPvc", 0x00)]
    public void IncrementsAndDecrementsKeepTheCarry(int opcode, int value, bool carry, string flags, int result)
    {
        var m = new Machine((byte)opcode);
        m.Core.B = (byte)value;
        m.Core.F = carry ? I8085Core.FlagC : (byte)0;
        m.Step();
        Assert.Equal(result, m.Core.B);
        Assert.Equal(flags, Cpu8085.Flags(m.Core.F));
    }

    [Fact]
    public void IncrementingMemoryReadsAndWritesIt()
    {
        var m = new Machine(0x34, 0x35, 0x35);       // INR M, DCR M, DCR M
        m.Core.HL = 0x8000;
        m.Memory[0x8000] = 0x0F;
        m.Step();
        Assert.Equal(0x10, m.Memory[0x8000]);
        Assert.True(m.Flag(I8085Core.FlagAC));
        m.Run(2);
        Assert.Equal(0x0E, m.Memory[0x8000]);
    }

    /// <summary>INX and DCX change no documented flag; K is the incrementer's carry: set going through $FFFF to $0000 either way.</summary>
    [Theory]
    [InlineData(0x03, 0xFFFF, 0x0000, true)]
    [InlineData(0x03, 0x7FFF, 0x8000, false)]
    [InlineData(0x0B, 0x0000, 0xFFFF, true)]
    [InlineData(0x0B, 0x8000, 0x7FFF, false)]
    public void PairIncrementsSetKOnlyWhenTheyWrap(int opcode, int before, int after, bool k)
    {
        var m = new Machine((byte)opcode);
        m.Core.BC = (ushort)before;
        const byte others = I8085Core.FlagS | I8085Core.FlagZ | I8085Core.FlagAC | I8085Core.FlagP | I8085Core.FlagV | I8085Core.FlagC;
        m.Core.F = (byte)(others | (k ? 0 : I8085Core.FlagK));
        Assert.Equal(6, m.Step());
        Assert.Equal(after, m.Core.BC);
        Assert.Equal(others | (k ? I8085Core.FlagK : 0), m.Core.F);
    }

    /// <summary>DAD keeps every flag but CY — V and K included, the die not storing them for it.</summary>
    [Fact]
    public void DadSetsOnlyTheCarry()
    {
        var m = new Machine(0x09, 0x29, 0x39);       // DAD B, DAD H, DAD SP
        m.Core.HL = 0x8000;
        m.Core.BC = 0x8001;
        m.Core.F = I8085Core.FlagZ | I8085Core.FlagK | I8085Core.FlagP;
        m.Step();
        Assert.Equal(0x0001, m.Core.HL);
        Assert.Equal(I8085Core.FlagZ | I8085Core.FlagK | I8085Core.FlagP | I8085Core.FlagC, m.Core.F);
        m.Step();
        Assert.Equal(0x0002, m.Core.HL);
        Assert.False(m.Flag(I8085Core.FlagC));
        m.Step();
        Assert.Equal(0xFF02, m.Core.HL);
    }

    /// <summary>DAA after BCD additions: a low digit over 9 or AC adds 6, a high one (or CY) $60, CY kept.</summary>
    [Theory]
    [InlineData(0x15, 0x27, 0x42, false)]
    [InlineData(0x99, 0x01, 0x00, true)]
    [InlineData(0x99, 0x99, 0x98, true)]
    [InlineData(0x50, 0x50, 0x00, true)]
    [InlineData(0x08, 0x08, 0x16, false)]
    public void DaaAdjustsBcdSums(int a, int b, int result, bool carry)
    {
        var m = new Machine(0xC6, (byte)b, 0x27);   // ADI b; DAA
        m.Core.A = (byte)a;
        m.Run(2);
        Assert.Equal(result, m.Core.A);
        Assert.Equal(carry, m.Flag(I8085Core.FlagC));
        Assert.Equal(result == 0, m.Flag(I8085Core.FlagZ));
    }

    /// <summary>
    /// The rotates change CY — and V and K, the die storing them: RLC and RAL add A to itself, so V
    /// is A7 ^ A6 and K comes out as A7; RRC and RAR shift right, so V is clear and K is the result's
    /// bit 7. S, Z, AC and P are kept.
    /// </summary>
    [Theory]
    [InlineData(0x07, 0x85, false, 0x0B, "sZKaPVC")]
    [InlineData(0x0F, 0x01, false, 0x80, "sZKaPvC")]
    [InlineData(0x17, 0x40, true, 0x81, "sZkaPVc")]
    [InlineData(0x1F, 0x01, false, 0x00, "sZkaPvC")]
    public void RotatesSetCarryAndTheUndocumentedFlags(int opcode, int a, bool carry, int result, string flags)
    {
        var m = new Machine((byte)opcode);
        m.Core.A = (byte)a;
        m.Core.F = (byte)(I8085Core.FlagZ | I8085Core.FlagP | (carry ? I8085Core.FlagC : 0));
        m.Step();
        Assert.Equal(result, m.Core.A);
        Assert.Equal(flags, Cpu8085.Flags(m.Core.F));
    }

    [Fact]
    public void CmaStcAndCmc()
    {
        var m = new Machine(0x2F, 0x37, 0x3F, 0x3F);
        m.Core.A = 0x55;
        m.Core.F = I8085Core.FlagZ;
        m.Step();
        Assert.Equal(0xAA, m.Core.A);
        Assert.Equal(I8085Core.FlagZ, m.Core.F);
        m.Step();
        Assert.True(m.Flag(I8085Core.FlagC));
        m.Step();
        Assert.False(m.Flag(I8085Core.FlagC));
        m.Step();
        Assert.True(m.Flag(I8085Core.FlagZ | I8085Core.FlagC));
    }

    /// <summary>PUSH PSW and POP PSW carry every flag but bit 3, which is not a flip-flop and always reads 0.</summary>
    [Fact]
    public void ThePswHasNoBitThree()
    {
        var m = new Machine(0xF1, 0xF5);            // POP PSW, PUSH PSW
        m.Memory[0xFF00] = 0xFF;
        m.Memory[0xFF01] = 0x12;
        m.Step();
        Assert.Equal(0x12, m.Core.A);
        Assert.Equal(0xF7, m.Core.F);
        Assert.Equal("SZKAPVC", Cpu8085.Flags(m.Core.F));
        m.Step();
        Assert.Equal(0xF7, m.Memory[0xFF00]);
        Assert.Equal(0x12, m.Memory[0xFF01]);
    }

    [Fact]
    public void StackInstructionsMoveSpAndPc()
    {
        var m = new Machine("""
                    org $0000
                    lxi sp,$9000
                    lxi b,$1234
                    push b
                    pop d
                    lxi h,$5678
                    xthl
                    call sub
                    jmp $0040
            sub:    pchl
            """);
        m.Run(5);
        Assert.Equal(0x1234, m.Core.DE);
        Assert.Equal(0x8FFE + 2, m.Core.SP);        // the pop moved it back
        m.Memory[0x9000] = 0xCD;
        m.Memory[0x9001] = 0xAB;
        m.Core.SP = 0x9000;
        m.Step();                                    // XTHL
        Assert.Equal(0xABCD, m.Core.HL);
        Assert.Equal(0x78, m.Memory[0x9000]);
        Assert.Equal(0x56, m.Memory[0x9001]);
        m.Step();                                    // CALL sub
        Assert.Equal(0x8FFE, m.Core.SP);
        Assert.Equal(0x0F, m.Memory[0x8FFE]);      // the return address, after the CALL
        m.Step();                                    // PCHL
        Assert.Equal(0xABCD, m.Core.PC);
    }

    /// <summary>Every condition, both ways, on a jump, a call and a return: NZ Z NC C PO PE P M.</summary>
    [Theory]
    [InlineData(0, I8085Core.FlagZ, false)]
    [InlineData(1, I8085Core.FlagZ, true)]
    [InlineData(2, I8085Core.FlagC, false)]
    [InlineData(3, I8085Core.FlagC, true)]
    [InlineData(4, I8085Core.FlagP, false)]
    [InlineData(5, I8085Core.FlagP, true)]
    [InlineData(6, I8085Core.FlagS, false)]
    [InlineData(7, I8085Core.FlagS, true)]
    public void EveryConditionJumpsCallsAndReturns(int cc, byte flag, bool whenSet)
    {
        foreach (var set in new[] { false, true })
        {
            var taken = set == whenSet;
            var f = set ? flag : (byte)0;

            var jump = new Machine((byte)(0xC2 | (cc << 3)), 0x00, 0x20);
            jump.Core.F = f;
            jump.Step();
            Assert.Equal(taken ? 0x2000 : 0x1003, jump.Core.PC);

            var call = new Machine((byte)(0xC4 | (cc << 3)), 0x00, 0x20);
            call.Core.F = f;
            call.Step();
            Assert.Equal(taken ? 0x2000 : 0x1003, call.Core.PC);
            Assert.Equal(taken ? 0xFEFE : 0xFF00, call.Core.SP);

            var ret = new Machine((byte)(0xC0 | (cc << 3)));
            ret.Memory[0xFF00] = 0x34;
            ret.Memory[0xFF01] = 0x12;
            ret.Core.F = f;
            ret.Step();
            Assert.Equal(taken ? 0x1234 : 0x1001, ret.Core.PC);
        }
    }

    /// <summary>IN and OUT put the port on both halves of the address bus.</summary>
    [Fact]
    public void InputAndOutputUseThePortTwice()
    {
        var m = new Machine(0xDB, 0x40, 0xD3, 0x41);
        m.Inputs[0x40] = 0x5A;
        m.Step();
        Assert.Equal(0x5A, m.Core.A);
        Assert.Equal("F1000 R1001 I4040", m.Cycles());
        m.Step();
        Assert.Equal([(0x4141, 0x5A)], m.Outputs);
    }

    // ---- the undocumented instructions -----------------------------------------------

    /// <summary>DSUB: HL - BC, CY the borrow, S, P and AC from the high byte, Z from both — the die ANDs the two zeros.</summary>
    [Theory]
    [InlineData(0x1234, 0x1234, 0x0000, "sZ.APvc")]
    [InlineData(0x1200, 0x0001, 0x11FF, "sz.APvc")]
    [InlineData(0x0100, 0x0001, 0x00FF, "sz.APvc")]
    [InlineData(0x0000, 0x0001, 0xFFFF, "Sz.aPvC")]
    public void DsubSubtractsBcFromHl(int hl, int bc, int result, string flags)
    {
        var m = new Machine(0x08);
        m.Core.HL = (ushort)hl;
        m.Core.BC = (ushort)bc;
        m.Core.F = 0;
        Assert.Equal(10, m.Step());
        Assert.Equal(result, m.Core.HL);
        Assert.Equal(flags.Replace('.', 'k'), Cpu8085.Flags(m.Core.F));
    }

    /// <summary>ARHL: an arithmetic shift right of HL, L0 into CY; V cleared and K the old H0, through the ALU's shift.</summary>
    [Theory]
    [InlineData(0x8003, 0xC001, true, false)]
    [InlineData(0x0101, 0x0080, true, true)]
    [InlineData(0x7FFE, 0x3FFF, false, true)]
    public void ArhlShiftsHlRight(int hl, int result, bool carry, bool k)
    {
        var m = new Machine(0x10);
        m.Core.HL = (ushort)hl;
        m.Core.F = I8085Core.FlagV | I8085Core.FlagZ;
        Assert.Equal(7, m.Step());
        Assert.Equal(result, m.Core.HL);
        Assert.Equal(carry, m.Flag(I8085Core.FlagC));
        Assert.Equal(k, m.Flag(I8085Core.FlagK));
        Assert.False(m.Flag(I8085Core.FlagV));
        Assert.True(m.Flag(I8085Core.FlagZ));
    }

    /// <summary>RDEL: DE rotated left through CY; nothing else changes.</summary>
    [Fact]
    public void RdelRotatesDeLeft()
    {
        var m = new Machine(0x18, 0x18);
        m.Core.DE = 0x8001;
        m.Core.F = I8085Core.FlagC | I8085Core.FlagS;
        m.Step();
        Assert.Equal(0x0003, m.Core.DE);
        Assert.Equal(I8085Core.FlagC | I8085Core.FlagS, m.Core.F);
        m.Step();
        Assert.Equal(0x0007, m.Core.DE);
        Assert.Equal(I8085Core.FlagS, m.Core.F);
    }

    [Fact]
    public void LdhiAndLdsiAddAByteIntoDe()
    {
        var m = new Machine(0x28, 0x05, 0x38, 0x02);
        m.Core.HL = 0x80FE;
        m.Core.SP = 0xFFFF;
        m.Core.F = I8085Core.FlagC;
        m.Step();
        Assert.Equal(0x8103, m.Core.DE);
        m.Step();
        Assert.Equal(0x0001, m.Core.DE);
        Assert.Equal(I8085Core.FlagC, m.Core.F);
    }

    [Fact]
    public void ShlxAndLhlxGoThroughDe()
    {
        var m = new Machine(0xD9, 0x21, 0x00, 0x00, 0xED);
        m.Core.DE = 0x9000;
        m.Core.HL = 0x1234;
        m.Step();
        Assert.Equal(0x34, m.Memory[0x9000]);
        Assert.Equal(0x12, m.Memory[0x9001]);
        m.Step();
        Assert.Equal(0, m.Core.HL);
        m.Step();
        Assert.Equal(0x1234, m.Core.HL);
    }

    /// <summary>RSTV calls $40 when V is set and is a six-state no-operation when not; JNK and JK test K.</summary>
    [Fact]
    public void RstvJnkAndJkTestTheUndocumentedFlags()
    {
        var m = new Machine(0xCB, 0xCB);
        m.Core.F = 0;
        Assert.Equal(6, m.Step());
        Assert.Equal(0x1001, m.Core.PC);
        m.Core.F = I8085Core.FlagV;
        Assert.Equal(12, m.Step());
        Assert.Equal(0x0040, m.Core.PC);
        Assert.Equal(0x02, m.Memory[0xFEFE]);

        // A signed comparison: CMP sets K when A is less, as signed numbers.
        var k = new Machine("""
                    org $0000
                    mvi a,-5
                    cpi 3
                    jk less
                    hlt
            less:   jnk $0000
                    mvi b,1
            """);
        k.Run(3);
        Assert.Equal(0x0008, k.Core.PC);       // JK taken: -5 is less than 3
        k.Run(2);                              // JNK not taken
        Assert.Equal(1, k.Core.B);
    }

    // ---- interrupts ------------------------------------------------------------------

    /// <summary>A program that enables interrupts and loops, with RETs at the restart addresses; an NOP chain to sample on.</summary>
    private static Machine Interruptible(string handlers = "") => new($"""
                org $0000
                jmp start
                org $0024
        trap:   ret
                org $002C
        r55:    ret
                org $0034
        r65:    ret
                org $0038
        rst7:   ret
                org $003C
        r75:    ret
                org $0040
        {handlers}
                org $0100
        start:  lxi sp,$9000
                mvi a,$08
                sim             ; unmask all three
                ei
        loop:   nop
                nop
                jmp loop
        """);

    [Fact]
    public void Rst75LatchesAnEdgeEvenWhileMasked()
    {
        var m = Interruptible();
        m.Core.Masks = 7;
        m.Run(2);                       // LXI SP, MVI A,$08 — still masked
        m.Core.Rst75 = true;
        m.TState();
        m.Core.Rst75 = false;
        m.Step();
        Assert.True(m.Core.Rst75Pending);
        Assert.Equal(0x0105, m.Core.PC);  // nothing taken while masked
        m.Step();                       // SIM unmasks
        m.Step();                       // EI: one more instruction first
        Assert.Equal(0x0107, m.Core.PC);
        m.Step();                       // NOP
        Assert.Equal(12, m.Step());     // the restart: six states with no strobe, two pushes
        Assert.Equal("G W8FFF W8FFE", m.Cycles());
        Assert.Equal(0x003C, m.Core.PC);
        Assert.Equal("RST 7.5", m.Core.LastInterrupt);
        Assert.False(m.Core.Rst75Pending);
        Assert.False(m.Core.InterruptsEnabled);
    }

    [Fact]
    public void SimBitFourForgetsAnRst75()
    {
        var m = new Machine(0x3E, 0x10, 0x30);       // MVI A,$10; SIM
        m.Core.Rst75 = true;
        m.Step();
        Assert.True(m.Core.Rst75Pending);
        m.Step();
        Assert.False(m.Core.Rst75Pending);
    }

    /// <summary>All five at once are taken in order: TRAP, RST 7.5, 6.5, 5.5, INTR — the RET re-enables nothing, so each needs its EI.</summary>
    [Fact]
    public void InterruptsAreTakenByPriority()
    {
        var m = Interruptible();
        m.Run(5);
        m.Core.Trap = m.Core.Rst75 = m.Core.Rst65 = m.Core.Rst55 = m.Core.Intr = true;
        m.Acknowledge.Enqueue(0xFF);
        List<string> taken = [];
        for (var i = 0; i < 60 && taken.Count < 5; i++)
        {
            var before = m.Core.LastInterrupt;
            m.Step();
            if (m.Core.FirstCycle && m.Core.PC is 0x0024 or 0x002C or 0x0034 or 0x0038 or 0x003C && m.Core.LastInterrupt != before || m.Trace.Count > 0 && m.Trace[0] is "G" or "A")
            {
                taken.Add(m.Core.LastInterrupt);
                if (m.Core.LastInterrupt == "RST 5.5") m.Core.Rst55 = false;
                if (m.Core.LastInterrupt == "RST 6.5") m.Core.Rst65 = false;
                if (m.Core.LastInterrupt == "INTR") m.Core.Intr = false;
                m.Core.InterruptsEnabled = true;        // as an EI in the handler would
            }
        }

        Assert.Equal(["TRAP", "RST 7.5", "RST 6.5", "RST 5.5", "INTR"], taken);
    }

    /// <summary>The masks hold RST 5.5 and 6.5 off while their levels stay high; DI holds everything but TRAP off.</summary>
    [Fact]
    public void MasksAndDiHoldInterruptsOff()
    {
        var m = Interruptible();
        m.Run(5);
        m.Core.Masks = 3;               // 5.5 and 6.5 masked
        m.Core.Rst55 = m.Core.Rst65 = true;
        m.Run(6);
        Assert.Equal(string.Empty, m.Core.LastInterrupt);
        m.Core.Masks = 1;
        m.Run(2);
        Assert.Equal("RST 6.5", m.Core.LastInterrupt);

        var d = Interruptible();
        d.Run(5);
        d.Core.InterruptsEnabled = false;
        d.Core.Intr = d.Core.Rst65 = true;
        d.Run(6);
        Assert.Equal(string.Empty, d.Core.LastInterrupt);
        d.Core.Trap = true;
        d.Run(2);
        Assert.Equal("TRAP", d.Core.LastInterrupt);
    }

    /// <summary>TRAP needs its edge and its level: a pulse gone before it is sampled is ignored, a held level is taken once.</summary>
    [Fact]
    public void TrapNeedsAnEdgeAndALevel()
    {
        var m = Interruptible();
        m.Run(5);
        m.Core.Trap = true;
        m.TState();
        m.Core.Trap = false;
        m.Run(4);
        Assert.Equal(string.Empty, m.Core.LastInterrupt);
        Assert.True(m.Core.TrapPending);        // latched, waiting for the level

        var h = Interruptible();
        h.Run(5);
        h.Core.Trap = true;
        var traps = 0;
        for (var i = 0; i < 20; i++)
        {
            h.Step();
            if (h.Trace.Contains("G")) traps++;
        }

        Assert.Equal(1, traps);                 // held high: no second TRAP without a new edge
        h.Core.Trap = false;
        h.Step();
        h.Core.Trap = true;
        for (var i = 0; i < 4; i++)
        {
            h.Step();
            if (h.Trace.Contains("G")) traps++;
        }

        Assert.Equal(2, traps);
    }

    /// <summary>The first RIM after a TRAP gives the interrupt enable from before it; the next gives the enable as it is.</summary>
    [Fact]
    public void RimAfterTrapShowsTheOldEnable()
    {
        var m = Interruptible("""
                    org $0024
                    rim
                    mov b,a
                    rim
                    hlt
            """);
        m.Run(5);
        m.Core.Trap = true;
        m.Run(5);
        Assert.Equal(0x08, m.Core.B & 0x08);
        Assert.Equal(0x00, m.Core.A & 0x08);
    }

    /// <summary>RIM: SID in bit 7, RST 7.5 pending, the 6.5 and 5.5 pins, the enable and the masks.</summary>
    [Fact]
    public void RimReadsPinsMasksAndEnable()
    {
        var m = new Machine(0xFB, 0x20, 0x00);       // EI; RIM; NOP
        m.Core.Masks = 5;
        m.Core.Sid = true;
        m.Core.Rst65 = true;
        m.Core.Rst75 = true;
        m.Run(2);
        Assert.Equal(0x80 | 0x40 | 0x20 | 0x08 | 0x05, m.Core.A);
    }

    /// <summary>SIM with bit 6 sets SOD from bit 7 — in T2 of the next opcode fetch — and leaves it with bit 6 clear.</summary>
    [Fact]
    public void SimSetsSodAtTheNextFetch()
    {
        var m = new Machine(0x3E, 0xC0, 0x30, 0x00, 0x3E, 0x80, 0x30, 0x00, 0x3E, 0x40, 0x30, 0x00);
        m.Run(2);
        Assert.False(m.Core.Sod);          // T1 of the NOP
        m.TState();
        Assert.True(m.Core.Sod);           // its T2
        m.Run(4);
        Assert.True(m.Core.Sod);           // bit 6 clear: no change
        m.Run(3);
        Assert.False(m.Core.Sod);
    }

    /// <summary>EI lets one more instruction run before an interrupt is taken.</summary>
    [Fact]
    public void EiWaitsAnInstruction()
    {
        var m = new Machine(0xFB, 0x00, 0x00);
        m.Core.Intr = true;
        m.Acknowledge.Enqueue(0xC7);           // RST 0
        m.Step();
        Assert.True(m.Core.EiLast);
        m.Step();
        Assert.Equal(0x1002, m.Core.PC);       // the NOP ran
        Assert.Equal(12, m.Step());
        Assert.Equal(0x0000, m.Core.PC);
        Assert.Equal(0x02, m.Memory[0xFEFE]);  // the address of the instruction it interrupted
    }

    /// <summary>INTR answered with a CALL: three /INTA cycles, PC held, the address in the second and third, then the pushes.</summary>
    [Fact]
    public void IntrTakesACallInThreeAcknowledges()
    {
        var m = new Machine(0xFB, 0x00, 0x00);
        m.Run(2);
        m.Core.Intr = true;
        foreach (var b in new byte[] { 0xCD, 0x34, 0x12 }) m.Acknowledge.Enqueue(b);
        m.Step();                              // a NOP while INTR is sampled
        Assert.Equal(18, m.Step());
        Assert.Equal("A A A WFEFF WFEFE", m.Cycles());
        Assert.Equal(0x1234, m.Core.PC);
        Assert.Equal(0x1003, m.Memory[0xFEFE] | (m.Memory[0xFEFF] << 8));
        Assert.Equal([0xCD, 0x34, 0x12], m.Core.LastAcknowledgeBytes);
        Assert.Equal("INTR", m.Core.LastInterrupt);
        Assert.False(m.Core.InterruptsEnabled);
    }

    /// <summary>HLT is five states, then HALT states with the bus floating until an interrupt, which returns after the HLT.</summary>
    [Fact]
    public void HaltWaitsForAnInterrupt()
    {
        var m = new Machine("""
                    org $0000
                    lxi sp,$9000
                    mvi a,$08
                    sim
                    ei
                    hlt
                    mvi b,$11
            done:   jmp done
                    org $0034
                    mvi c,$22
                    ret
            """);
        m.Run(4);
        Assert.Equal(5, m.Step());
        Assert.True(m.Core.Halted);
        for (var i = 0; i < 10; i++) Assert.Equal(1, m.Step());
        Assert.Equal(I8085CycleKind.Halt, m.Core.Kind);
        Assert.True(m.Core.BusFloated);
        m.Core.Rst65 = true;
        m.Run(3);                       // sampled in one HALT state, out after the next, then the response
        Assert.False(m.Core.Halted);
        Assert.Equal(0x0034, m.Core.PC);
        m.Core.Rst65 = false;
        m.Run(4);
        Assert.Equal(0x22, m.Core.C);
        Assert.Equal(0x11, m.Core.B);
    }

    /// <summary>HOLD seen in T2: HLDA for T3, the bus floating from the next T-state through the hold states, then the next cycle.</summary>
    [Fact]
    public void HoldLetsGoOfTheBus()
    {
        var m = new Machine(0x3A, 0x00, 0x80, 0x00);       // LDA $8000; NOP
        m.Memory[0x8000] = 0x77;
        m.TState();                                   // T1
        m.Core.Hold = true;
        m.TState();                                   // T2: HOLD latched
        Assert.True(m.Core.HoldAcknowledged);
        Assert.Equal(I8085TState.T3, m.Core.State);
        Assert.False(m.Core.BusFloated);
        m.TState();                                   // T3
        Assert.Equal(I8085TState.T4, m.Core.State);
        Assert.True(m.Core.BusFloated);
        m.TState();
        Assert.Equal(I8085CycleKind.Hold, m.Core.Kind);
        for (var i = 0; i < 5; i++) m.TState();
        Assert.Equal(I8085CycleKind.Hold, m.Core.Kind);
        m.Core.Hold = false;
        m.TState();
        Assert.False(m.Core.HoldAcknowledged);
        Assert.Equal(I8085CycleKind.MemoryRead, m.Core.Kind);
        m.Run(1);
        Assert.Equal(0x77, m.Core.A);
        Assert.True(m.Core.HoldStates >= 6);
    }

    /// <summary>HOLD while halted: the hold states, then HALT again.</summary>
    [Fact]
    public void HoldInHaltReturnsToHalt()
    {
        var m = new Machine(0x76);
        m.Run(3);
        m.Core.Hold = true;
        m.Run(4);
        Assert.Equal(I8085CycleKind.Hold, m.Core.Kind);
        Assert.True(m.Core.HoldAcknowledged);
        m.Core.Hold = false;
        m.Run(3);
        Assert.Equal(I8085CycleKind.Halt, m.Core.Kind);
        Assert.True(m.Core.Halted);
    }

    /// <summary>RESET IN floats the bus; let go, PC, the enable, the latches and SOD are cleared and the masks set — the registers kept.</summary>
    [Fact]
    public void ResetClearsTheInterruptSystemAndPc()
    {
        var m = new Machine(0xFB, 0x3E, 0xC0, 0x30, 0x00, 0x00);
        m.Core.Rst75 = true;            // latched, though masked
        m.Run(5);
        Assert.True(m.Core.Sod);
        m.Core.B = 0x5A;
        m.Core.ResetAsserted = true;
        for (var i = 0; i < 4; i++) m.TState();
        Assert.Equal(I8085CycleKind.Reset, m.Core.Kind);
        Assert.True(m.Core.BusFloated);
        m.Core.ResetAsserted = false;
        m.TState();
        Assert.Equal(I8085CycleKind.OpcodeFetch, m.Core.Kind);
        Assert.Equal(0x0000, m.Core.Address);
        Assert.Equal(7, m.Core.Masks);
        Assert.False(m.Core.InterruptsEnabled);
        Assert.False(m.Core.Rst75Pending);
        Assert.False(m.Core.Sod);
        Assert.Equal(0x5A, m.Core.B);
        Assert.Equal("RESET", m.Core.LastInterrupt);
    }

    // ---- the disassembler and the assembler --------------------------------------------

    /// <summary>Every opcode disassembles to text the assembler turns back into the same bytes.</summary>
    [Fact]
    public void EveryOpcodeRoundTripsThroughTheAssembler()
    {
        for (var op = 0; op < 256; op++)
        {
            byte[] bytes = [(byte)op, 0x34, 0x12];
            var (text, length) = Opcodes8085.Disassemble(0x2000, a => bytes[a - 0x2000]);
            var again = Assembler8085.Assemble($"    org $2000\n    {text}\n").Image(0x2000, length);
            Assert.True(bytes.Take(length).SequenceEqual(again), $"${op:X2}: '{text}' assembled to {Convert.ToHexString(again)}");
        }

        Assert.Equal(("LDSI $05 *", 2), Opcodes8085.Disassemble(0, a => a == 0 ? (byte)0x38 : (byte)5));
        Assert.Equal(Assembler8085.Assemble("    ldsp 5\n    jnx5 $1234\n    jx5 $1234\n").Image(0, 8),
            Assembler8085.Assemble("    ldsi 5\n    jnk $1234\n    jk $1234\n").Image(0, 8));
    }

    /// <summary>The disassembler's length for each opcode is what the core reads: PC moves on by it.</summary>
    [Fact]
    public void TheDisassemblerAgreesWithTheCoreOnLengths()
    {
        for (var op = 0; op < 256; op++)
        {
            var x = op >> 6;
            var z = op & 7;
            var branches = x == 3 && (z is 0 or 2 or 4 or 7 || op is 0xC9 or 0xC3 or 0xCD or 0xE9 or 0xCB or 0xDD or 0xFD);
            if (branches || op == 0x76) continue;
            var m = new Machine((byte)op, 0x00, 0x00);
            m.Core.HL = 0x8000;
            m.Step();
            Assert.True(m.Core.PC - 0x1000 == Opcodes8085.Lengths[op], $"${op:X2}: PC moved {m.Core.PC - 0x1000}, the disassembler says {Opcodes8085.Lengths[op]}");
        }
    }

    // ---- a self-checking program ---------------------------------------------------------

    /// <summary>
    /// A program that checks a little of everything and leaves 1 at $8000 when it all agrees, or the
    /// number of the first check that failed; it ends in a loop at <c>done</c>. Shared with the
    /// circuit tests, which run it from an EPROM.
    /// </summary>
    internal const string SelfCheck = """
                org $0000
                lxi sp,$0000
                mvi b,1             ; check 1: an addition and its flags
                mvi a,$7F
                adi 1
                jnc ok1
                jmp fail
        ok1:    cpi $80
                jnz fail
                inr b               ; check 2: DAA
                mvi a,$38
                adi $45
                daa
                cpi $83
                jnz fail
                inr b               ; check 3: a 16-bit loop with DCX and JNK
                lxi d,5
                mvi c,0
        loop:   inr c
                dcx d
                jnk loop
                mov a,c
                cpi 6
                jnz fail
                inr b               ; check 4: the stack and a subroutine
                lxi h,$1234
                push h
                call swap
                pop h
                mov a,h
                cpi $34
                jnz fail
                inr b               ; check 5: memory through HL and DE
                lxi h,$8010
                mvi m,$A5
                lxi d,$8010
                lhlx
                mov a,l
                cpi $A5
                jnz fail
                inr b               ; check 6: DSUB and DAD
                lxi h,$5000
                lxi b,$1001
                dsub
                dad b
                mov a,h
                cpi $50
                jnz fail
                mov a,l
                ora a
                jnz fail
                mvi a,1
                sta $8000
        done:   jmp done
        fail:   mov a,b
                sta $8000
                jmp done
        swap:   xthl                ; swaps the halves of the word on the stack below the return address
                xchg
                pop h
                xthl
                mov a,l
                mov l,h
                mov h,a
                xthl
                push h
                xchg
                xthl
                ret
        """;

    [Fact]
    public void TheSelfCheckPasses()
    {
        var m = new Machine(SelfCheck);
        for (var i = 0; i < 400 && m.Core.InstructionAddress != Assembler8085.Assemble(SelfCheck).Label("done"); i++) m.Step();
        Assert.Equal(1, m.Memory[0x8000]);
    }
}
