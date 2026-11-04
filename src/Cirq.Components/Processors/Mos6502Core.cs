namespace Cirq.Components.Processors;

/// <summary>
/// A 6502 processor as a sequence of bus cycles, NMOS or 65C02, with no circuit attached.
/// <para>
/// A 6502 does exactly one thing per clock: it puts an address on the bus and either reads a byte
/// from it or writes one to it. Every instruction is a fixed sequence of those cycles — including
/// the "dummy" reads and writes the silicon does while it is busy with something else, which are
/// real bus cycles a memory-mapped device sees. This core is written as that sequence. Each
/// instruction is an iterator that yields once per cycle, having set <see cref="Address"/>,
/// <see cref="IsRead"/> and (for a write) <see cref="DataOut"/>; whoever drives it performs the
/// cycle and calls <see cref="Complete"/> with the byte read, and the iterator carries on to the
/// next cycle. A circuit part drives it from the PHI2 clock pin; a test drives it from an array.
/// </para>
/// <para>
/// <b>Interrupts</b> are sampled at the end of every cycle and acted on at an instruction boundary,
/// using the sample from the instruction's second-to-last cycle — which is why an interrupt arriving
/// in an instruction's final cycle waits for the next one, and why CLI lets one more instruction
/// run before a pending IRQ is taken. IRQ is a level, masked by I; NMI is an edge. The 6502's
/// quirks of interrupt timing around branches and the NMOS BRK/NMI collision are not modelled.
/// </para>
/// <para>
/// <b>Reset</b> holds the processor while the line is low; when it goes high the seven-cycle
/// sequence runs — three stack reads where a push would be, then the vector at $FFFC — with I set,
/// and on the 65C02 D cleared.
/// </para>
/// </summary>
public sealed class Mos6502Core
{
    public const byte FlagC = 0x01, FlagZ = 0x02, FlagI = 0x04, FlagD = 0x08, FlagB = 0x10, FlagU = 0x20, FlagV = 0x40, FlagN = 0x80;

    private readonly IReadOnlyList<Opcodes6502.Entry> _table;
    private IEnumerator<bool> _program;

    private byte _data;
    private int _ea;

    // Interrupt sampling: what was seen at the end of this cycle and of the one before it.
    private bool _pendingNow;
    private bool _pendingBefore;
    private bool _nmiLatched;
    private bool _nmiPrevious;
    private bool _nmiNow;
    private bool _nmiBefore;

    public Mos6502Core(Cpu6502Variant variant)
    {
        Variant = variant;
        _table = Opcodes6502.For(variant);
        _program = Program(powerOn: true).GetEnumerator();
        _program.MoveNext();
    }

    public Cpu6502Variant Variant { get; }

    private bool Cmos => Variant == Cpu6502Variant.Wdc65C02;

    // ---- registers --------------------------------------------------------

    public byte A { get; set; }

    public byte X { get; set; }

    public byte Y { get; set; }

    /// <summary>The stack pointer: the stack is $0100 + S, growing down.</summary>
    public byte S { get; set; } = 0xFD;

    /// <summary>The status register. Bit 5 always reads as one; B exists only in a pushed copy.</summary>
    public byte P { get; set; } = FlagU | FlagI;

    public ushort PC { get; set; }

    private bool C { get => (P & FlagC) != 0; set => Flag(FlagC, value); }

    private bool D => (P & FlagD) != 0;

    private void Flag(byte flag, bool on) => P = (byte)(on ? P | flag : P & ~flag);

    private void Nz(int value)
    {
        Flag(FlagZ, (value & 0xFF) == 0);
        Flag(FlagN, (value & 0x80) != 0);
    }

    // ---- the bus cycle on offer -------------------------------------------

    /// <summary>The address of the cycle in progress.</summary>
    public ushort Address { get; private set; }

    /// <summary>True when the cycle in progress reads; false when it writes <see cref="DataOut"/>.</summary>
    public bool IsRead { get; private set; } = true;

    /// <summary>The byte being written, on a write cycle.</summary>
    public byte DataOut { get; private set; }

    /// <summary>High on an opcode fetch: the SYNC pin.</summary>
    public bool Sync { get; private set; }

    /// <summary>True while an interrupt or reset vector is being read: the 65C02's VPB pin, active low there.</summary>
    public bool VectorPull { get; private set; }

    /// <summary>True during the read-modify-write cycles of an instruction: the 65C02's MLB pin, active low there.</summary>
    public bool MemoryLock { get; private set; }

    /// <summary>Cycles completed since power-on.</summary>
    public long Cycles { get; private set; }

    /// <summary>Instructions started since power-on.</summary>
    public long Instructions { get; private set; }

    /// <summary>True after STP on a 65C02, or a JAM opcode on the NMOS part: only reset starts it again.</summary>
    public bool IsStopped { get; private set; }

    /// <summary>True while a 65C02 is in WAI, waiting for an interrupt.</summary>
    public bool IsWaiting { get; private set; }

    /// <summary>The address of the instruction executing (or last fetched).</summary>
    public ushort InstructionAddress { get; private set; }

    /// <summary>The opcode executing.</summary>
    public byte Opcode { get; private set; }

    // ---- the pins it samples ------------------------------------------------

    /// <summary>The /IRQ line is low: an interrupt is being requested.</summary>
    public bool IrqAsserted { get; set; }

    /// <summary>The /NMI line is low. A falling edge is what interrupts.</summary>
    public bool NmiAsserted { get; set; }

    private bool _resetHeld;

    /// <summary>
    /// Holds the processor in reset (the /RES line low). Releasing it starts the reset sequence at
    /// the next cycle.
    /// </summary>
    public bool ResetAsserted
    {
        get => _resetHeld;
        set
        {
            if (value && !_resetHeld)
            {
                // Abandon whatever was going on: the next cycle starts the reset hold.
                _program = Program(powerOn: false).GetEnumerator();
                _program.MoveNext();
            }

            _resetHeld = value;
        }
    }

    /// <summary>
    /// Finishes the cycle on offer — with the byte the bus held, on a read — and moves to the next.
    /// </summary>
    public void Complete(byte dataIn = 0)
    {
        _data = IsRead ? dataIn : DataOut;
        Cycles++;

        // NMI is an edge: one falling edge, one interrupt, however long the line stays low.
        if (NmiAsserted && !_nmiPrevious) _nmiLatched = true;
        _nmiPrevious = NmiAsserted;

        _nmiBefore = _nmiNow;
        _nmiNow = _nmiLatched;
        _pendingBefore = _pendingNow;
        _pendingNow = _nmiLatched || (IrqAsserted && (P & FlagI) == 0);

        _program.MoveNext();
    }

    /// <summary>Runs one whole instruction against a memory, for tests: returns the cycles it took.</summary>
    public int Step(Func<ushort, byte> read, Action<ushort, byte> write)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);

        var count = 0;
        do
        {
            if (IsRead) Complete(read(Address));
            else
            {
                write(Address, DataOut);
                Complete();
            }

            count++;
        }
        while (!Sync && count < 100);

        return count;
    }

    /// <summary>
    /// Puts the processor straight at the start of an instruction at <paramref name="pc"/>, with its
    /// registers as given and no reset sequence: what a test that sets up a state wants.
    /// </summary>
    public void Load(ushort pc, byte a, byte x, byte y, byte s, byte p)
    {
        PC = pc;
        A = a;
        X = x;
        Y = y;
        S = s;
        P = (byte)(p | FlagU);
        IsStopped = false;
        _jammed = false;
        IsWaiting = false;
        _nmiLatched = _pendingNow = _pendingBefore = _nmiNow = _nmiBefore = _nmiPrevious = false;
        _resetHeld = false;
        _program = Program(powerOn: false, skipReset: true).GetEnumerator();
        _program.MoveNext();
    }

    // ---- posting cycles -------------------------------------------------------

    private bool Read(int address)
    {
        Address = (ushort)address;
        IsRead = true;
        return true;
    }

    private bool Write(int address, byte value)
    {
        Address = (ushort)address;
        IsRead = false;
        DataOut = value;
        return true;
    }

    private bool Push(byte value)
    {
        Write(0x100 | S, value);
        S--;
        return true;
    }

    // ---- the program ------------------------------------------------------

    private IEnumerable<bool> Program(bool powerOn, bool skipReset = false)
    {
        if (!skipReset)
        {
            // Held in reset: the bus reads, going nowhere in particular, until the line comes up.
            do
            {
                yield return Read(PC);
            }
            while (_resetHeld);

            foreach (var c in ResetSequence()) yield return c;
        }

        while (true)
        {
            if (IsStopped)
            {
                yield return Read(_jammed ? 0xFFFF : PC);
                continue;
            }

            var interrupt = _pendingBefore;
            var nmi = _nmiBefore;

            // The opcode fetch, with SYNC high.
            InstructionAddress = PC;
            Sync = true;
            yield return Read(PC);
            Sync = false;

            if (interrupt)
            {
                // The fetched byte is thrown away and the PC left where it was, so the interrupted
                // instruction runs when the handler returns.
                foreach (var c in Interrupt(nmi, isBreak: false)) yield return c;
                continue;
            }

            Opcode = _data;
            PC++;
            Instructions++;

            foreach (var c in Execute(Opcode)) yield return c;
        }
    }

    private IEnumerable<bool> ResetSequence()
    {
        VectorPull = false;
        yield return Read(PC);
        yield return Read(PC);

        // Three pushes that the reset turns into reads, which is why S comes out three lower.
        for (var i = 0; i < 3; i++)
        {
            yield return Read(0x100 | S);
            S--;
        }

        P |= FlagI | FlagU;
        if (Cmos) P &= unchecked((byte)~FlagD);

        VectorPull = true;
        yield return Read(0xFFFC);
        var low = _data;
        yield return Read(0xFFFD);
        VectorPull = false;
        PC = (ushort)(low | (_data << 8));

        IsStopped = false;
        _jammed = false;
        IsWaiting = false;
        _nmiLatched = false;
        _pendingNow = _pendingBefore = _nmiNow = _nmiBefore = false;
    }

    /// <summary>BRK, IRQ and NMI: push the PC and P, take the vector.</summary>
    private IEnumerable<bool> Interrupt(bool nmi, bool isBreak)
    {
        if (isBreak) yield return Read(PC++);
        else yield return Read(PC);

        yield return Push((byte)(PC >> 8));
        yield return Push((byte)PC);

        // The NMI is decided at this point, as late as the silicon decides it.
        var vector = nmi || _nmiLatched ? 0xFFFA : 0xFFFE;
        if (vector == 0xFFFA) _nmiLatched = false;

        yield return Push((byte)(P | FlagU | (isBreak ? FlagB : 0)));

        P |= FlagI;
        if (Cmos) P &= unchecked((byte)~FlagD);

        VectorPull = true;
        yield return Read(vector);
        var low = _data;
        yield return Read(vector + 1);
        VectorPull = false;
        PC = (ushort)(low | (_data << 8));

        // The sample taken during the sequence is for the interrupt being serviced, not a new one.
        _pendingNow = _nmiLatched;
        _nmiNow = _nmiLatched;
    }

    // ---- addressing -------------------------------------------------------

    /// <summary>What an instruction does with its operand: read it, write it, or read, change and write it back.</summary>
    private enum Access { Read, Write, Modify }

    /// <summary>
    /// The cycles that work out the effective address, into <see cref="_ea"/>, for a read, a write or
    /// a read-modify-write: everything up to (not including) the cycle that uses it.
    /// </summary>
    private IEnumerable<bool> Resolve(AddressMode mode, Access access)
    {
        switch (mode)
        {
            case AddressMode.ZeroPage:
                yield return Read(PC++);
                _ea = _data;
                break;

            case AddressMode.ZeroPageX or AddressMode.ZeroPageY:
            {
                yield return Read(PC++);
                var zp = _data;
                yield return Read(zp);
                _ea = (zp + (mode == AddressMode.ZeroPageX ? X : Y)) & 0xFF;
                break;
            }

            case AddressMode.Absolute:
            {
                yield return Read(PC++);
                var low = _data;
                yield return Read(PC++);
                _ea = low | (_data << 8);
                break;
            }

            case AddressMode.AbsoluteX or AddressMode.AbsoluteY:
            {
                yield return Read(PC++);
                var low = _data;
                yield return Read(PC++);
                var high = _data;
                var index = mode == AddressMode.AbsoluteX ? X : Y;
                var unfixed = (high << 8) | ((low + index) & 0xFF);
                _ea = ((high << 8) | low) + index & 0xFFFF;
                var crossed = unfixed != _ea;

                // The high byte is fixed up a cycle later: a read that did not cross a page needs no
                // fixing and is done; everything else spends a cycle on it.
                if (access == Access.Read && !crossed) break;

                if (Cmos && access == Access.Modify && !crossed && IsShiftOrRotate(Opcode)) break;

                // The NMOS part reads the address before its high byte is fixed; the 65C02 rereads the
                // last byte of the instruction instead, so a device on the bus never sees a stray read.
                yield return Read(Cmos ? PC - 1 : unfixed);
                break;
            }

            case AddressMode.IndexedIndirect:
            {
                yield return Read(PC++);
                var zp = _data;
                yield return Read(zp);
                var pointer = (zp + X) & 0xFF;
                yield return Read(pointer);
                var low = _data;
                yield return Read((pointer + 1) & 0xFF);
                _ea = low | (_data << 8);
                break;
            }

            case AddressMode.IndirectIndexed:
            {
                yield return Read(PC++);
                var zp = _data;
                yield return Read(zp);
                var low = _data;
                yield return Read((zp + 1) & 0xFF);
                var high = _data;
                var unfixed = (high << 8) | ((low + Y) & 0xFF);
                _ea = ((high << 8) | low) + Y & 0xFFFF;
                var crossed = unfixed != _ea;

                if (access == Access.Read && !crossed) break;

                yield return Read(Cmos ? PC - 1 : unfixed);
                break;
            }

            case AddressMode.ZeroPageIndirect:
            {
                yield return Read(PC++);
                var zp = _data;
                yield return Read(zp);
                var low = _data;
                yield return Read((zp + 1) & 0xFF);
                _ea = low | (_data << 8);
                break;
            }

            default:
                throw new InvalidOperationException($"{mode} has no operand address.");
        }
    }

    private static bool IsShiftOrRotate(byte opcode) => opcode is 0x1E or 0x3E or 0x5E or 0x7E;

    /// <summary>An instruction that reads its operand and does something with it.</summary>
    private IEnumerable<bool> ReadOperation(AddressMode mode, Action<byte> operation, bool decimalExtra = false)
    {
        if (mode == AddressMode.Immediate)
        {
            yield return Read(PC++);
        }
        else
        {
            foreach (var c in Resolve(mode, Access.Read)) yield return c;
            yield return Read(_ea);
        }

        var operand = _data;

        // The 65C02 spends a cycle putting decimal arithmetic right, and the flags are valid after it.
        // The cycle rereads the operand; for an immediate operand the WDC part's bus shows a read of
        // $007F for ADC and $0000 for SBC, which is what the published per-cycle captures record.
        if (decimalExtra && Cmos && D)
            yield return Read(mode != AddressMode.Immediate ? _ea : Opcode == 0x69 ? 0x007F : 0x0000);

        operation(operand);
    }

    private IEnumerable<bool> WriteOperation(AddressMode mode, Func<byte> value)
    {
        foreach (var c in Resolve(mode, Access.Write)) yield return c;
        yield return Write(_ea, value());
    }

    private IEnumerable<bool> ModifyOperation(AddressMode mode, Func<byte, byte> change)
    {
        if (mode == AddressMode.Accumulator)
        {
            yield return Read(PC);
            A = change(A);
            yield break;
        }

        foreach (var c in Resolve(mode, Access.Modify)) yield return c;

        MemoryLock = true;
        yield return Read(_ea);
        var old = _data;

        // NMOS writes the old value back while it works out the new one; the 65C02 reads it again.
        if (Cmos) yield return Read(_ea);
        else yield return Write(_ea, old);

        yield return Write(_ea, change(old));
        MemoryLock = false;
    }

    // ---- execution -------------------------------------------------------------

    private IEnumerable<bool> Execute(byte opcode)
    {
        var entry = _table[opcode];
        var mode = entry.Mode;
        var m = entry.Mnemonic;

        switch (m)
        {
            // Loads, stores, logic and arithmetic.
            case "LDA": return ReadOperation(mode, v => { A = v; Nz(A); });
            case "LDX": return ReadOperation(mode, v => { X = v; Nz(X); });
            case "LDY": return ReadOperation(mode, v => { Y = v; Nz(Y); });
            case "ORA": return ReadOperation(mode, v => { A |= v; Nz(A); });
            case "AND": return ReadOperation(mode, v => { A &= v; Nz(A); });
            case "EOR": return ReadOperation(mode, v => { A ^= v; Nz(A); });
            case "ADC": return ReadOperation(mode, Adc, decimalExtra: true);
            case "SBC": return ReadOperation(mode, Sbc, decimalExtra: true);
            case "CMP": return ReadOperation(mode, v => Compare(A, v));
            case "CPX": return ReadOperation(mode, v => Compare(X, v));
            case "CPY": return ReadOperation(mode, v => Compare(Y, v));
            case "BIT": return ReadOperation(mode, v => Bit(v, immediate: mode == AddressMode.Immediate));

            case "STA": return WriteOperation(mode, () => A);
            case "STX": return WriteOperation(mode, () => X);
            case "STY": return WriteOperation(mode, () => Y);
            case "STZ": return WriteOperation(mode, () => 0);

            case "ASL": return ModifyOperation(mode, v => { C = (v & 0x80) != 0; var r = (byte)(v << 1); Nz(r); return r; });
            case "LSR": return ModifyOperation(mode, v => { C = (v & 1) != 0; var r = (byte)(v >> 1); Nz(r); return r; });
            case "ROL": return ModifyOperation(mode, Rol);
            case "ROR": return ModifyOperation(mode, Ror);
            case "INC": return ModifyOperation(mode, v => { var r = (byte)(v + 1); Nz(r); return r; });
            case "DEC": return ModifyOperation(mode, v => { var r = (byte)(v - 1); Nz(r); return r; });
            case "TSB": return ModifyOperation(mode, v => { Flag(FlagZ, (A & v) == 0); return (byte)(v | A); });
            case "TRB": return ModifyOperation(mode, v => { Flag(FlagZ, (A & v) == 0); return (byte)(v & ~A); });

            // Register to register, flags, and the do-nothing.
            case "TAX": return Implied(() => { X = A; Nz(X); });
            case "TAY": return Implied(() => { Y = A; Nz(Y); });
            case "TXA": return Implied(() => { A = X; Nz(A); });
            case "TYA": return Implied(() => { A = Y; Nz(A); });
            case "TSX": return Implied(() => { X = S; Nz(X); });
            case "TXS": return Implied(() => S = X);
            case "INX": return Implied(() => { X++; Nz(X); });
            case "INY": return Implied(() => { Y++; Nz(Y); });
            case "DEX": return Implied(() => { X--; Nz(X); });
            case "DEY": return Implied(() => { Y--; Nz(Y); });
            case "CLC": return Implied(() => Flag(FlagC, false));
            case "SEC": return Implied(() => Flag(FlagC, true));
            case "CLI": return Implied(() => Flag(FlagI, false));
            case "SEI": return Implied(() => Flag(FlagI, true));
            case "CLD": return Implied(() => Flag(FlagD, false));
            case "SED": return Implied(() => Flag(FlagD, true));
            case "CLV": return Implied(() => Flag(FlagV, false));

            case "NOP": return Nop(opcode, mode);

            // The stack.
            case "PHA": return PushRegister(() => A);
            case "PHX": return PushRegister(() => X);
            case "PHY": return PushRegister(() => Y);
            case "PHP": return PushRegister(() => (byte)(P | FlagB | FlagU));
            case "PLA": return PullRegister(v => { A = v; Nz(A); });
            case "PLX": return PullRegister(v => { X = v; Nz(X); });
            case "PLY": return PullRegister(v => { Y = v; Nz(Y); });
            case "PLP": return PullRegister(v => P = (byte)((v & ~FlagB) | FlagU));

            // Flow.
            case "JMP": return Jump(mode);
            case "JSR": return JumpSubroutine();
            case "RTS": return ReturnFromSubroutine();
            case "RTI": return ReturnFromInterrupt();
            case "BRK": return Interrupt(nmi: false, isBreak: true);
            case "BPL": return Branch((P & FlagN) == 0);
            case "BMI": return Branch((P & FlagN) != 0);
            case "BVC": return Branch((P & FlagV) == 0);
            case "BVS": return Branch((P & FlagV) != 0);
            case "BCC": return Branch((P & FlagC) == 0);
            case "BCS": return Branch((P & FlagC) != 0);
            case "BNE": return Branch((P & FlagZ) == 0);
            case "BEQ": return Branch((P & FlagZ) != 0);
            case "BRA": return Branch(true);

            case "WAI": return WaitForInterrupt();
            case "STP": return Stop();
            case "JAM": return Jam();
        }

        // The 65C02's bit instructions.
        if (m.Length == 4 && m[3] is >= '0' and <= '7')
        {
            var bit = m[3] - '0';
            return m[..3] switch
            {
                "RMB" => BitModify(v => (byte)(v & ~(1 << bit))),
                "SMB" => BitModify(v => (byte)(v | (1 << bit))),
                "BBR" => BitBranch(bit, set: false),
                _ => BitBranch(bit, set: true),
            };
        }

        return Undocumented(m, mode);
    }

    private IEnumerable<bool> Implied(Action action)
    {
        yield return Read(PC);
        action();
    }

    private IEnumerable<bool> Nop(byte opcode, AddressMode mode)
    {
        switch (mode)
        {
            case AddressMode.Implied:
                // The 65C02's unassigned xx11 and xx3 columns are NOPs that take one cycle and one byte.
                if (Cmos && (opcode & 0x03) == 0x03) yield break;
                yield return Read(PC);
                break;

            case AddressMode.Immediate:
                yield return Read(PC++);
                break;

            case AddressMode.Absolute when Cmos:
            {
                // $5C, $DC and $FC: three bytes, and a reread of the last of them rather than a read
                // of the address they spell.
                yield return Read(PC++);
                yield return Read(PC++);
                yield return Read(PC - 1);
                break;
            }

            default:
                foreach (var c in Resolve(mode, Access.Read)) yield return c;
                yield return Read(_ea);
                break;
        }
    }

    private IEnumerable<bool> PushRegister(Func<byte> value)
    {
        yield return Read(PC);
        yield return Push(value());
    }

    private IEnumerable<bool> PullRegister(Action<byte> store)
    {
        yield return Read(PC);
        yield return Read(0x100 | S);
        S++;
        yield return Read(0x100 | S);
        store(_data);
    }

    private IEnumerable<bool> Jump(AddressMode mode)
    {
        yield return Read(PC++);
        var low = _data;

        if (mode == AddressMode.Absolute)
        {
            yield return Read(PC);
            PC = (ushort)(low | (_data << 8));
            yield break;
        }

        yield return Read(PC++);
        var high = _data;
        var pointer = low | (high << 8);

        if (mode == AddressMode.AbsoluteIndexedIndirect)
        {
            yield return Read(PC - 2);
            pointer = (pointer + X) & 0xFFFF;
            yield return Read(pointer);
            var target = _data;
            yield return Read((pointer + 1) & 0xFFFF);
            PC = (ushort)(target | (_data << 8));
            yield break;
        }

        if (Cmos)
        {
            // The 65C02 spends a cycle fixing the page-wrap bug the NMOS part has: it reads where the
            // NMOS part would have, then the right place, and takes the high byte from that.
            yield return Read(pointer);
            var target = _data;
            yield return Read((pointer & 0xFF00) | ((pointer + 1) & 0xFF));
            yield return Read((pointer + 1) & 0xFFFF);
            PC = (ushort)(target | (_data << 8));
        }
        else
        {
            // JMP ($xxFF) takes its high byte from $xx00, not the next page: the famous NMOS bug.
            yield return Read(pointer);
            var target = _data;
            yield return Read((pointer & 0xFF00) | ((pointer + 1) & 0xFF));
            PC = (ushort)(target | (_data << 8));
        }
    }

    private IEnumerable<bool> JumpSubroutine()
    {
        yield return Read(PC++);
        var low = _data;
        yield return Read(0x100 | S);
        yield return Push((byte)(PC >> 8));
        yield return Push((byte)PC);
        yield return Read(PC);
        PC = (ushort)(low | (_data << 8));
    }

    private IEnumerable<bool> ReturnFromSubroutine()
    {
        yield return Read(PC);
        yield return Read(0x100 | S);
        S++;
        yield return Read(0x100 | S);
        var low = _data;
        S++;
        yield return Read(0x100 | S);
        PC = (ushort)(low | (_data << 8));
        yield return Read(PC);
        PC++;
    }

    private IEnumerable<bool> ReturnFromInterrupt()
    {
        yield return Read(PC);
        yield return Read(0x100 | S);
        S++;
        yield return Read(0x100 | S);
        P = (byte)((_data & ~FlagB) | FlagU);
        S++;
        yield return Read(0x100 | S);
        var low = _data;
        S++;
        yield return Read(0x100 | S);
        PC = (ushort)(low | (_data << 8));
    }

    private IEnumerable<bool> Branch(bool taken)
    {
        yield return Read(PC++);
        var offset = (sbyte)_data;
        if (!taken) yield break;

        yield return Read(PC);
        var target = (ushort)(PC + offset);

        if ((target & 0xFF00) != (PC & 0xFF00))
            yield return Read((PC & 0xFF00) | (target & 0xFF));

        PC = target;
    }

    private IEnumerable<bool> BitModify(Func<byte, byte> change)
    {
        yield return Read(PC++);
        _ea = _data;
        yield return Read(_ea);
        var old = _data;
        yield return Read(_ea);
        yield return Write(_ea, change(old));
    }

    private IEnumerable<bool> BitBranch(int bit, bool set)
    {
        yield return Read(PC++);
        var zp = _data;
        yield return Read(zp);
        var value = _data;
        yield return Read(zp);
        yield return Read(PC++);
        var offset = (sbyte)_data;

        if (((value >> bit) & 1) != (set ? 1 : 0)) yield break;

        yield return Read(PC);
        var target = (ushort)(PC + offset);
        if ((target & 0xFF00) != (PC & 0xFF00)) yield return Read(PC);
        PC = target;
    }

    private IEnumerable<bool> WaitForInterrupt()
    {
        yield return Read(PC);
        yield return Read(PC);
        IsWaiting = true;

        // Asleep until an interrupt line goes active — taken if unmasked, and simply woken from
        // otherwise, carrying on with the next instruction.
        while (!(_nmiLatched || IrqAsserted)) yield return Read(PC);

        IsWaiting = false;
    }

    /// <summary>
    /// The NMOS JAM opcodes lock the processor up: after a few reads of the vectors' addresses the bus
    /// sits reading $FFFF, and only reset brings it back.
    /// </summary>
    private IEnumerable<bool> Jam()
    {
        yield return Read(PC);
        yield return Read(0xFFFF);
        yield return Read(0xFFFE);
        yield return Read(0xFFFE);
        _jammed = true;
        IsStopped = true;
    }

    private bool _jammed;

    private IEnumerable<bool> Stop()
    {
        yield return Read(PC);
        yield return Read(PC);
        IsStopped = true;
    }

    // ---- arithmetic ------------------------------------------------------------

    private void Compare(byte register, byte value)
    {
        var r = register - value;
        C = register >= value;
        Nz(r);
    }

    private void Bit(byte value, bool immediate)
    {
        Flag(FlagZ, (A & value) == 0);
        if (immediate) return;
        Flag(FlagN, (value & 0x80) != 0);
        Flag(FlagV, (value & 0x40) != 0);
    }

    private byte Rol(byte v)
    {
        var r = (byte)((v << 1) | (C ? 1 : 0));
        C = (v & 0x80) != 0;
        Nz(r);
        return r;
    }

    private byte Ror(byte v)
    {
        var r = (byte)((v >> 1) | (C ? 0x80 : 0));
        C = (v & 1) != 0;
        Nz(r);
        return r;
    }

    private void Adc(byte value)
    {
        var carry = C ? 1 : 0;

        if (!D)
        {
            var sum = A + value + carry;
            Flag(FlagV, (~(A ^ value) & (A ^ sum) & 0x80) != 0);
            C = sum > 0xFF;
            A = (byte)sum;
            Nz(A);
            return;
        }

        // Decimal: each digit corrected for going past nine. The flags are where the two parts differ.
        var low = (A & 0x0F) + (value & 0x0F) + carry;
        if (low >= 0x0A) low = ((low + 0x06) & 0x0F) + 0x10;

        var result = (A & 0xF0) + (value & 0xF0) + low;
        var signed = (sbyte)(A & 0xF0) + (sbyte)(value & 0xF0) + low;
        var negative = (result & 0x80) != 0;
        Flag(FlagV, signed is < -128 or > 127);

        if (result >= 0xA0) result += 0x60;
        C = result >= 0x100;

        var binaryZero = ((A + value + carry) & 0xFF) == 0;
        A = (byte)result;

        if (Cmos)
        {
            Nz(A);
        }
        else
        {
            // The NMOS part takes Z from the binary sum and N from before the high digit is fixed.
            Flag(FlagZ, binaryZero);
            Flag(FlagN, negative);
        }
    }

    private void Sbc(byte value)
    {
        var borrow = C ? 0 : 1;
        var difference = A - value - borrow;

        // C and V are the binary subtraction's on both parts.
        var overflow = ((A ^ value) & (A ^ difference) & 0x80) != 0;
        var carry = difference >= 0;

        if (!D)
        {
            Flag(FlagV, overflow);
            C = carry;
            A = (byte)difference;
            Nz(A);
            return;
        }

        int result;
        var low = (A & 0x0F) - (value & 0x0F) - borrow;

        if (Cmos)
        {
            result = difference;
            if (result < 0) result -= 0x60;
            if (low < 0) result -= 0x06;
        }
        else
        {
            if (low < 0) low = ((low - 0x06) & 0x0F) - 0x10;
            result = (A & 0xF0) - (value & 0xF0) + low;
            if (result < 0) result -= 0x60;
        }

        Flag(FlagV, overflow);
        C = carry;

        if (Cmos)
        {
            A = (byte)result;
            Nz(A);
        }
        else
        {
            // N and Z from the binary difference.
            Nz(difference);
            A = (byte)result;
        }
    }

    // ---- the NMOS undocumented opcodes --------------------------------------------

    private IEnumerable<bool> Undocumented(string mnemonic, AddressMode mode)
    {
        switch (mnemonic)
        {
            case "SLO": return ModifyOperation(mode, v => { C = (v & 0x80) != 0; var r = (byte)(v << 1); A |= r; Nz(A); return r; });
            case "RLA": return ModifyOperation(mode, v => { var r = Rol(v); A &= r; Nz(A); return r; });
            case "SRE": return ModifyOperation(mode, v => { C = (v & 1) != 0; var r = (byte)(v >> 1); A ^= r; Nz(A); return r; });
            case "RRA": return ModifyOperation(mode, v => { var r = Ror(v); Adc(r); return r; });
            case "DCP": return ModifyOperation(mode, v => { var r = (byte)(v - 1); Compare(A, r); return r; });
            case "ISC": return ModifyOperation(mode, v => { var r = (byte)(v + 1); Sbc(r); return r; });
            case "SAX": return WriteOperation(mode, () => (byte)(A & X));
            case "LAX": return ReadOperation(mode, v => { A = X = v; Nz(A); });
            case "ANC": return ReadOperation(mode, v => { A &= v; Nz(A); C = (A & 0x80) != 0; });
            case "ALR": return ReadOperation(mode, v => { var t = (byte)(A & v); C = (t & 1) != 0; A = (byte)(t >> 1); Nz(A); });
            case "ARR": return ReadOperation(mode, Arr);
            case "SBX": return ReadOperation(mode, v => { var t = (A & X) - v; C = (A & X) >= v; X = (byte)t; Nz(X); });
            case "ANE": return ReadOperation(mode, v => { A = (byte)((A | 0xEE) & X & v); Nz(A); });
            case "LXA": return ReadOperation(mode, v => { A = X = (byte)((A | 0xEE) & v); Nz(A); });
            case "LAS": return ReadOperation(mode, v => { A = X = S = (byte)(v & S); Nz(A); });
            case "TAS": return Unstable(mode, () => { S = (byte)(A & X); return S; });
            case "SHA": return Unstable(mode, () => (byte)(A & X));
            case "SHX": return Unstable(mode, () => X);
            case "SHY": return Unstable(mode, () => Y);
        }

        throw new InvalidOperationException($"No implementation for {mnemonic}.");
    }

    /// <summary>
    /// ARR: AND, then ROR, with the carry and overflow taken from the result's top bits — and, in
    /// decimal mode, the digits adjusted as ADC would.
    /// </summary>
    private void Arr(byte value)
    {
        var t = A & value;
        var carryIn = C ? 0x80 : 0;
        var r = (t >> 1) | carryIn;

        if (!D)
        {
            A = (byte)r;
            Nz(A);
            C = (A & 0x40) != 0;
            Flag(FlagV, (((A >> 6) ^ (A >> 5)) & 1) != 0);
            return;
        }

        Flag(FlagN, carryIn != 0);
        Flag(FlagZ, (r & 0xFF) == 0);
        Flag(FlagV, ((t ^ r) & 0x40) != 0);

        if ((t & 0x0F) + (t & 0x01) > 0x05) r = (r & 0xF0) | ((r + 0x06) & 0x0F);
        var high = (t & 0xF0) + (t & 0x10) > 0x50;
        C = high;
        if (high) r = (r + 0x60) & 0xFF;

        A = (byte)r;
    }

    /// <summary>
    /// SHA, SHX, SHY and TAS: the register written ANDed with the high byte of the address plus one,
    /// and on a page crossing that value becoming the high byte of the address as well.
    /// </summary>
    private IEnumerable<bool> Unstable(AddressMode mode, Func<byte> register)
    {
        int baseHigh;

        if (mode == AddressMode.IndirectIndexed)
        {
            yield return Read(PC++);
            var zp = _data;
            yield return Read(zp);
            var low = _data;
            yield return Read((zp + 1) & 0xFF);
            baseHigh = _data;
            _ea = ((baseHigh << 8) | low) + Y & 0xFFFF;
            yield return Read((baseHigh << 8) | ((low + Y) & 0xFF));
        }
        else
        {
            yield return Read(PC++);
            var low = _data;
            yield return Read(PC++);
            baseHigh = _data;
            var index = mode == AddressMode.AbsoluteX ? X : Y;
            _ea = ((baseHigh << 8) | low) + index & 0xFFFF;
            yield return Read((baseHigh << 8) | ((low + index) & 0xFF));
        }

        var value = (byte)(register() & (baseHigh + 1));
        var crossed = (_ea >> 8) != baseHigh;
        var address = crossed ? (value << 8) | (_ea & 0xFF) : _ea;
        yield return Write(address, value);
    }
}
