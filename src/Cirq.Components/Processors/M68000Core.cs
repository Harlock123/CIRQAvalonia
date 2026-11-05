namespace Cirq.Components.Processors;

/// <summary>What the bus cycle a <see cref="M68000Core"/> has on offer is.</summary>
public enum M68000BusAction
{
    /// <summary>No bus cycle: the processor is busy inside for <see cref="M68000Core.IdleClocks"/> clocks.</summary>
    Idle,

    /// <summary>A read of a word, or of the byte the address picks.</summary>
    Read,

    /// <summary>A write of a word, or of a byte (put on both halves of the data bus).</summary>
    Write,

    /// <summary>An interrupt acknowledge: FC = 111, A1-A3 the level, a byte read on D0-D7 for the vector.</summary>
    InterruptAcknowledge,

    /// <summary>The RESET instruction: /RESET driven low for <see cref="M68000Core.IdleClocks"/> clocks.</summary>
    ResetOut,

    /// <summary>After STOP: no bus cycles until an interrupt, a trace or a reset. Two clocks at a time.</summary>
    Stopped,

    /// <summary>Halted after a double bus fault: /HALT driven low until a reset.</summary>
    Halted,

    /// <summary>Held in reset by /RESET and /HALT. Two clocks at a time.</summary>
    HeldInReset,

    /// <summary>
    /// Only with <see cref="M68000Core.PauseBetweenInstructions"/>: one instruction has finished and
    /// the next has not begun. No clocks; <see cref="M68000Core.Complete"/> carries on.
    /// </summary>
    Boundary,
}

/// <summary>
/// A Motorola 68000 as a sequence of bus cycles, with no circuit attached: the instruction set and its
/// exceptions, written from Motorola's M68000 Programmer's Reference Manual and the MC68000 user's
/// manual.
/// <para>
/// <b>Bus cycles.</b> A 68000 bus cycle is four clocks long when /DTACK comes back at once — eight
/// states, S0 to S7 — and between cycles the processor is often busy inside for a few clocks of its
/// own. Every instruction here is an iterator that yields once per bus cycle, having set
/// <see cref="Action"/>, <see cref="Address"/>, the byte strobes, the function code and, for a write,
/// <see cref="DataOut"/>; or once per run of internal clocks, as <see cref="M68000BusAction.Idle"/>
/// with <see cref="IdleClocks"/>. Whoever drives it performs the cycle — on a schematic, state by
/// state, with as many wait states as /DTACK asks for — and calls <see cref="Complete"/> with the
/// word read. The order of the cycles, reads and writes and the prefetch among them, and the number
/// of clocks between them, are the silicon's.
/// </para>
/// <para>
/// <b>The prefetch.</b> The 68000 reads ahead: while an instruction runs, the next word is already in
/// its prefetch register (IRC), and every extension word it uses is replaced by a read of the one
/// after. The core keeps that queue — the opcode being run (IRD) and IRC — and the address the next
/// read comes from, so a branch, a write to SR or an exception refills it with exactly the reads the
/// chip does.
/// </para>
/// <para>
/// <b>Exceptions.</b> Reset, bus error, address error (a word or long access at an odd address),
/// illegal instruction, divide by zero, CHK, TRAPV, privilege violation, trace, line A and line F,
/// spurious and autovectored interrupts and TRAP #0-15, through the vector table at address 0 with
/// the supervisor stack. Interrupts come in at seven levels, the level being what /IPL0-/IPL2 encode
/// (sampled by the caller into <see cref="InterruptLevel"/>), taken at the end of an instruction
/// when above the mask in SR; level 7 cannot be masked and is taken once per rising level.
/// </para>
/// </summary>
public sealed partial class M68000Core
{
    public const ushort FlagC = 0x0001, FlagV = 0x0002, FlagZ = 0x0004, FlagN = 0x0008, FlagX = 0x0010;
    public const ushort FlagS = 0x2000, FlagT = 0x8000;

    /// <summary>The bits of SR that exist on a 68000: T, S, I2-I0 and the five condition codes.</summary>
    public const ushort StatusMask = 0xA71F;

    private readonly uint[] _d = new uint[8];

    // A0-A6 and the active stack pointer; the other stack pointer is kept aside.
    private readonly uint[] _a = new uint[8];
    private uint _inactiveSp;
    private ushort _sr = 0x2700;

    // The prefetch queue: the opcode being executed, the word after it, and that word's address.
    private ushort _ird;
    private ushort _irc;
    private uint _pc;

    private IEnumerator<bool> _program;
    private ushort _data;
    private ushort _taken;

    private bool _resetHeld;
    private int _lastLevel;
    private bool _nmiLatched;

    // The words of the instruction being run: its opcode and the extension words it has used.
    private readonly ushort[] _words = new ushort[5];
    private int _wordCount;

    public M68000Core()
    {
        _program = Program(Start.PowerOn, null).GetEnumerator();
        Advance();
    }

    // ---- registers --------------------------------------------------------------

    /// <summary>A data register, D0-D7.</summary>
    public uint GetD(int n) => _d[n];

    public void SetD(int n, uint value) => _d[n] = value;

    /// <summary>An address register, A0-A7; A7 is whichever stack pointer the S bit selects.</summary>
    public uint GetA(int n) => _a[n];

    public void SetA(int n, uint value) => _a[n] = value;

    /// <summary>The user stack pointer.</summary>
    public uint Usp
    {
        get => Supervisor ? _inactiveSp : _a[7];
        set
        {
            if (Supervisor) _inactiveSp = value;
            else _a[7] = value;
        }
    }

    /// <summary>The supervisor stack pointer.</summary>
    public uint Ssp
    {
        get => Supervisor ? _a[7] : _inactiveSp;
        set
        {
            if (Supervisor) _a[7] = value;
            else _inactiveSp = value;
        }
    }

    /// <summary>The status register. Writing S swaps the stack pointers, as on the chip.</summary>
    public ushort SR
    {
        get => _sr;
        set
        {
            value &= StatusMask;
            if (((value ^ _sr) & FlagS) != 0) (_a[7], _inactiveSp) = (_inactiveSp, _a[7]);
            _sr = value;
        }
    }

    /// <summary>The condition codes, SR's low byte: X N Z V C.</summary>
    public byte Ccr => (byte)(_sr & 0x1F);

    public bool Supervisor => (_sr & FlagS) != 0;

    /// <summary>The interrupt mask, SR bits 8-10: levels at or below it wait.</summary>
    public int InterruptMask => (_sr >> 8) & 7;

    /// <summary>
    /// The program counter as the programmer sees it: the address of the next instruction, whose
    /// opcode is in the prefetch already, at an instruction boundary.
    /// </summary>
    public uint PC => _pc - 2;

    /// <summary>The prefetch queue: the opcode about to run (IRD) and the word after it (IRC).</summary>
    public (ushort Ird, ushort Irc) PrefetchQueue => (_ird, _irc);

    // ---- the bus cycle on offer ---------------------------------------------------

    public M68000BusAction Action { get; private set; }

    /// <summary>The address of the cycle on offer, all 32 bits; A1-A23 are its pins, and A0 picks the byte.</summary>
    public uint Address { get; private set; }

    /// <summary>True for a word cycle (both strobes); false for a byte, on the half <see cref="Address"/>'s bit 0 picks.</summary>
    public bool IsWord { get; private set; }

    /// <summary>/UDS: the upper byte, D8-D15, at the even address.</summary>
    public bool UpperStrobe => IsWord || (Address & 1) == 0;

    /// <summary>/LDS: the lower byte, D0-D7, at the odd address.</summary>
    public bool LowerStrobe => IsWord || (Address & 1) != 0;

    /// <summary>FC0-FC2: 1 user data, 2 user program, 5 supervisor data, 6 supervisor program, 7 interrupt acknowledge.</summary>
    public int FunctionCode { get; private set; }

    /// <summary>The word being written; a byte goes out on both halves.</summary>
    public ushort DataOut { get; private set; }

    /// <summary>For an idle stretch or RESET's pulse, how many clocks it lasts.</summary>
    public int IdleClocks { get; private set; }

    /// <summary>True for both halves of TAS's indivisible read-modify-write: /AS stays low between them.</summary>
    public bool ReadModifyWrite { get; private set; }

    /// <summary>True while the cycle on offer is a program fetch: FC = 2 or 6.</summary>
    public bool IsProgramFetch => (FunctionCode & 3) == 2 && Action == M68000BusAction.Read;

    // ---- what the caller sees --------------------------------------------------------

    /// <summary>Clocks run, counting every bus cycle as the four it takes with /DTACK at once.</summary>
    public long Clocks { get; private set; }

    /// <summary>Instructions started.</summary>
    public long Instructions { get; private set; }

    /// <summary>The address of the instruction executing (or last started).</summary>
    public uint InstructionAddress { get; private set; }

    /// <summary>The opcode executing.</summary>
    public ushort Opcode => _opcode;

    private ushort _opcode;

    /// <summary>True after STOP, until an interrupt or a trace.</summary>
    public bool IsStopped { get; private set; }

    /// <summary>True after a double bus fault: only a reset starts it again.</summary>
    public bool IsHalted { get; private set; }

    /// <summary>The words of the last instruction that finished: its opcode and extension words.</summary>
    public IReadOnlyList<ushort> LastInstructionWords { get; private set; } = [];

    /// <summary>The address <see cref="LastInstructionWords"/> came from.</summary>
    public uint LastInstructionAddress { get; private set; }

    /// <summary>Raised as each instruction finishes, with its address and words: what a bus log annotates.</summary>
    public event Action<uint, ushort[]>? InstructionFinished;

    /// <summary>The last exception taken, by vector number, or −1.</summary>
    public int LastException { get; private set; } = -1;

    /// <summary>How many exceptions (including interrupts and traps) have been taken.</summary>
    public long Exceptions { get; private set; }

    // ---- the pins it samples -------------------------------------------------------

    /// <summary>
    /// The interrupt level /IPL0-/IPL2 encode, 0 to 7 — the lines are active low, so all three low
    /// is level 7. Set by the caller as it samples them.
    /// </summary>
    public int InterruptLevel
    {
        get;
        set
        {
            value = Math.Clamp(value, 0, 7);
            if (value == 7 && _lastLevel != 7) _nmiLatched = true;
            _lastLevel = value;
            field = value;
        }
    }

    /// <summary>
    /// /RESET and /HALT held low together: the processor stops at once. Releasing it runs the reset
    /// sequence — SR to $2700, the supervisor stack pointer from address 0 and the PC from 4.
    /// </summary>
    public bool ResetAsserted
    {
        get => _resetHeld;
        set
        {
            if (value && !_resetHeld)
            {
                _program = Program(Start.Reset, null).GetEnumerator();
                _resetHeld = true;
                Advance();
                return;
            }

            _resetHeld = value;
        }
    }

    /// <summary>
    /// Finishes the cycle on offer — with the word the bus held, on a read (both halves; a byte read
    /// takes the half its address picks) — and moves to the next.
    /// </summary>
    public void Complete(ushort dataIn = 0)
    {
        Clocks += Action switch
        {
            M68000BusAction.Read or M68000BusAction.Write or M68000BusAction.InterruptAcknowledge => 4,
            _ => IdleClocks,
        };

        if (Action is M68000BusAction.Read or M68000BusAction.InterruptAcknowledge)
            _data = IsWord ? dataIn : (Address & 1) == 0 ? (ushort)(dataIn >> 8) : (ushort)(dataIn & 0xFF);

        _autovector = false;
        _spurious = false;
        Advance();
    }

    private bool _autovector;
    private bool _spurious;

    /// <summary>Ends an interrupt acknowledge with /VPA instead of /DTACK: the level's autovector, 24 + level.</summary>
    public void CompleteAutovector()
    {
        Clocks += 4;
        _autovector = true;
        Advance();
    }

    /// <summary>
    /// Ends the cycle on offer with /BERR: a bus error, exception 2 — or, during an interrupt
    /// acknowledge, a spurious interrupt, exception 24.
    /// </summary>
    public void BusError()
    {
        Clocks += 4;
        if (Action == M68000BusAction.InterruptAcknowledge)
        {
            _spurious = true;
            Advance();
            return;
        }

        var fault = new Fault(2, Address, Action == M68000BusAction.Read, FunctionCode, IsProgramFetch);
        _program = Program(Start.Fault, fault).GetEnumerator();
        Advance();
    }

    /// <summary>Moves the program on to its next cycle, turning a fault thrown inside it into its exception.</summary>
    private void Advance()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                _program.MoveNext();
                return;
            }
            catch (AddressErrorException e)
            {
                // A fault while the last one is being stacked is a double bus fault: the chip halts.
                var halt = _group0 || attempt > 0;
                _program = Program(halt ? Start.Halt : Start.Fault, halt ? null : e.Fault).GetEnumerator();
            }
        }
    }

    /// <summary>
    /// Stop between instructions with a <see cref="M68000BusAction.Boundary"/>, so a test can look at
    /// the registers after one instruction before any of the next has run. A circuit leaves it off.
    /// </summary>
    public bool PauseBetweenInstructions { get; set; }

    /// <summary>Runs one whole instruction against a memory, for tests: returns the clocks it took.</summary>
    public int Step(Func<uint, ushort> readWord, Action<uint, ushort, bool> write)
    {
        ArgumentNullException.ThrowIfNull(readWord);
        ArgumentNullException.ThrowIfNull(write);

        if (!PauseBetweenInstructions)
        {
            PauseBetweenInstructions = true;
            while (Action != M68000BusAction.Boundary) RunCycle(readWord, write);
        }

        var before = Clocks;
        var count = 0;
        do
        {
            RunCycle(readWord, write);
            count++;
        }
        while (Action != M68000BusAction.Boundary && count < 100000);

        return (int)(Clocks - before);
    }

    private void RunCycle(Func<uint, ushort> readWord, Action<uint, ushort, bool> write)
    {
        switch (Action)
        {
            case M68000BusAction.Read:
                Complete(readWord(Address & 0xFFFFFE));
                break;
            case M68000BusAction.Write:
                write(Address & 0xFFFFFF, DataOut, IsWord);
                Complete();
                break;
            case M68000BusAction.InterruptAcknowledge:
                CompleteAutovector();
                break;
            default:
                Complete();
                break;
        }
    }

    /// <summary>
    /// Puts the processor at the start of an instruction, with its registers and prefetch queue as
    /// given and no reset sequence: what a test that sets up a state wants. <paramref name="pc"/> is
    /// the address of <paramref name="ird"/>; <paramref name="irc"/> is the word after it.
    /// </summary>
    public void Load(uint pc, ushort sr, uint usp, uint ssp, ushort ird, ushort irc)
    {
        _sr = (ushort)(sr & StatusMask);
        if (Supervisor)
        {
            _a[7] = ssp;
            _inactiveSp = usp;
        }
        else
        {
            _a[7] = usp;
            _inactiveSp = ssp;
        }

        _ird = ird;
        _opcode = ird;
        _irc = irc;
        _pc = pc + 2;
        IsStopped = false;
        IsHalted = false;
        _resetHeld = false;
        _nmiLatched = false;
        _group0 = false;
        LastException = -1;
        _program = Program(Start.Run, null).GetEnumerator();
        Advance();
    }

    // ---- posting cycles ----------------------------------------------------------------

    private int DataSpace => Supervisor ? 5 : 1;

    private int ProgramSpace => Supervisor ? 6 : 2;

    private bool Idle(int clocks)
    {
        Action = M68000BusAction.Idle;
        IdleClocks = clocks;
        ReadModifyWrite = false;
        return true;
    }

    private bool ReadWord(uint address, int fc, bool program = false)
    {
        if ((address & 1) != 0) throw new AddressErrorException(new Fault(3, address, true, fc, program));
        return Post(M68000BusAction.Read, address, fc, true, 0);
    }

    private bool ReadByte(uint address, int fc) => Post(M68000BusAction.Read, address, fc, false, 0);

    private bool WriteWord(uint address, ushort value, int fc)
    {
        if ((address & 1) != 0) throw new AddressErrorException(new Fault(3, address, false, fc, false));
        return Post(M68000BusAction.Write, address, fc, true, value);
    }

    private bool WriteByte(uint address, byte value, int fc) =>
        Post(M68000BusAction.Write, address, fc, false, (ushort)((value << 8) | value));

    private bool Post(M68000BusAction action, uint address, int fc, bool word, ushort data)
    {
        Action = action;
        Address = address;
        FunctionCode = fc;
        IsWord = word;
        DataOut = data;
        ReadModifyWrite = false;
        return true;
    }

    // ---- the program ---------------------------------------------------------------

    private enum Start { PowerOn, Reset, Run, Fault, Halt }

    /// <summary>An exception that interrupts a bus access: its vector, the address, which way, the function code.</summary>
    private sealed record Fault(int Vector, uint Address, bool Read, int FunctionCode, bool Instruction);

    private sealed class AddressErrorException(Fault fault) : Exception("68000 address error")
    {
        public Fault Fault { get; } = fault;
    }

    // True while a group 0 exception (reset, bus or address error) is being stacked.
    private bool _group0;

    private IEnumerable<bool> Program(Start start, Fault? fault)
    {
        switch (start)
        {
            case Start.PowerOn or Start.Reset:
                foreach (var c in ResetSequence()) yield return c;
                break;

            case Start.Halt:
                IsHalted = true;
                _group0 = false;
                while (true) yield return Idle2(M68000BusAction.Halted);

            case Start.Fault:
                foreach (var c in Group0(fault!)) yield return c;
                break;
        }

        while (true)
        {
            // An interrupt above the mask — or level 7, once each time it rises — is taken between
            // instructions.
            if (InterruptPending())
            {
                foreach (var c in Interrupt()) yield return c;
                continue;
            }

            if (IsStopped)
            {
                yield return Idle2(M68000BusAction.Stopped);
                continue;
            }

            if (PauseBetweenInstructions)
            {
                Action = M68000BusAction.Boundary;
                IdleClocks = 0;
                yield return true;
                if (InterruptPending() || IsStopped) continue;
            }

            InstructionAddress = PC;
            Instructions++;
            _opcode = _ird;
            _words[0] = _ird;
            _words[1] = _irc;
            _wordCount = 2;
            var tracing = (_sr & FlagT) != 0;
            _exceptionTaken = false;

            foreach (var c in Execute(_ird)) yield return c;

            Finish();

            // Trace: after an instruction run with T set, unless it took an exception of its own.
            if (tracing && !_exceptionTaken)
            {
                foreach (var c in Exception(9, PC, 4)) yield return c;
            }
        }
    }

    private bool Idle2(M68000BusAction action)
    {
        Action = action;
        IdleClocks = 2;
        ReadModifyWrite = false;
        return true;
    }

    /// <summary>Notes the instruction just finished, for the bus log and the disassembly of the latest.</summary>
    private void Finish()
    {
        var words = _words.AsSpan(0, _wordCount).ToArray();
        LastInstructionWords = words;
        LastInstructionAddress = InstructionAddress;
        InstructionFinished?.Invoke(InstructionAddress, words);
    }

    private bool InterruptPending()
    {
        if (_nmiLatched) return true;
        return InterruptLevel > InterruptMask;
    }

    private bool _exceptionTaken;

    /// <summary>
    /// Reset: held while /RESET and /HALT are low, then SR = $2700, the supervisor stack pointer read
    /// from $000000 and the PC from $000004 (supervisor program space), and the queue filled from there.
    /// </summary>
    private IEnumerable<bool> ResetSequence()
    {
        while (_resetHeld) yield return Idle2(M68000BusAction.HeldInReset);

        IsHalted = false;
        IsStopped = false;
        _group0 = true;
        _nmiLatched = false;
        SR = 0x2700;

        yield return Idle(16);
        yield return ReadWord(0, 6);
        var high = _data;
        yield return ReadWord(2, 6);
        _a[7] = (uint)((high << 16) | _data);
        yield return ReadWord(4, 6);
        high = _data;
        yield return ReadWord(6, 6);
        var pc = (uint)((high << 16) | _data);
        _group0 = false;

        foreach (var c in Refill(pc, 2)) yield return c;
    }

    /// <summary>
    /// Bus or address error: the seven-word frame — the PC, SR, the opcode, the address and a word
    /// saying read or write, instruction or not and the function code — then the vector.
    /// </summary>
    private IEnumerable<bool> Group0(Fault fault)
    {
        _group0 = true;
        _exceptionTaken = true;
        IsStopped = false;
        Exceptions++;
        LastException = fault.Vector;

        var saved = _sr;
        var pc = _pc - 2;
        var status = (ushort)((_opcode & 0xFFE0) | (fault.Read ? 0x10 : 0) | (fault.Instruction ? 0x08 : 0) | (fault.FunctionCode & 7));

        SR = (ushort)((_sr | FlagS) & ~FlagT);

        yield return Idle(4);
        var sp = _a[7];
        yield return WriteWord(sp - 2, (ushort)pc, 5);
        yield return WriteWord(sp - 6, saved, 5);
        yield return WriteWord(sp - 4, (ushort)(pc >> 16), 5);
        yield return WriteWord(sp - 8, _opcode, 5);
        yield return WriteWord(sp - 10, (ushort)fault.Address, 5);
        yield return WriteWord(sp - 14, status, 5);
        yield return WriteWord(sp - 12, (ushort)(fault.Address >> 16), 5);
        _a[7] = sp - 14;

        yield return ReadWord((uint)fault.Vector * 4, 5);
        var high = _data;
        yield return ReadWord((uint)fault.Vector * 4 + 2, 5);
        var target = (uint)((high << 16) | _data);

        foreach (var c in Refill(target, 2)) yield return c;
        _group0 = false;
    }

    /// <summary>
    /// A group 1 or 2 exception: <paramref name="idle"/> clocks inside, the PC and SR pushed, the
    /// vector read, and the queue filled from where it points.
    /// </summary>
    private IEnumerable<bool> Exception(int vector, uint pc, int idle)
    {
        _exceptionTaken = true;
        IsStopped = false;
        Exceptions++;
        LastException = vector;

        var saved = _sr;
        SR = (ushort)((_sr | FlagS) & ~FlagT);

        if (idle > 0) yield return Idle(idle);
        var sp = _a[7];
        yield return WriteWord(sp - 2, (ushort)pc, 5);
        yield return WriteWord(sp - 6, saved, 5);
        yield return WriteWord(sp - 4, (ushort)(pc >> 16), 5);
        _a[7] = sp - 6;

        yield return ReadWord((uint)vector * 4, 5);
        var high = _data;
        yield return ReadWord((uint)vector * 4 + 2, 5);
        var target = (uint)((high << 16) | _data);

        foreach (var c in Refill(target, 2)) yield return c;
    }

    /// <summary>
    /// An interrupt: the acknowledge cycle for the vector (or the autovector, with /VPA; or the
    /// spurious interrupt's, with /BERR), the frame, and the handler. The mask rises to the level.
    /// </summary>
    private IEnumerable<bool> Interrupt()
    {
        var level = _nmiLatched ? 7 : InterruptLevel;
        _nmiLatched = false;
        IsStopped = false;
        _exceptionTaken = true;
        Exceptions++;

        var saved = _sr;
        var pc = PC;
        SR = (ushort)(((_sr | FlagS) & ~FlagT & ~0x0700) | (level << 8));

        yield return Idle(6);
        var sp = _a[7];
        yield return WriteWord(sp - 2, (ushort)pc, 5);

        yield return Post(M68000BusAction.InterruptAcknowledge, 0xFFFFF1 | ((uint)level << 1), 7, false, 0);
        var vector = _spurious ? 24 : _autovector ? 24 + level : _data & 0xFF;
        LastException = vector;

        yield return Idle(4);
        yield return WriteWord(sp - 6, saved, 5);
        yield return WriteWord(sp - 4, (ushort)(pc >> 16), 5);
        _a[7] = sp - 6;

        yield return ReadWord((uint)vector * 4, 5);
        var high = _data;
        yield return ReadWord((uint)vector * 4 + 2, 5);
        var target = (uint)((high << 16) | _data);

        foreach (var c in Refill(target, 2)) yield return c;
    }

    /// <summary>
    /// Fills the queue from <paramref name="target"/>: the word there into IRD and the next into IRC,
    /// with <paramref name="gap"/> clocks between the two reads.
    /// </summary>
    private IEnumerable<bool> Refill(uint target, int gap)
    {
        // The queue is emptied and read again from the target: as far as an address error is
        // concerned, the PC has moved to the word before it.
        _pc = target - 2;
        yield return ReadWord(target, ProgramSpace, program: true);
        _irc = _data;
        _pc = target;
        if (gap > 0) yield return Idle(gap);
        foreach (var c in Prefetch()) yield return c;
    }

    /// <summary>
    /// One word of the queue moves on: the word in IRC is taken (into <see cref="_taken"/>: an
    /// extension word, or the next opcode) and the one after it is read in its place.
    /// </summary>
    private IEnumerable<bool> Fetch()
    {
        var at = _pc + 2;
        yield return ReadWord(at, ProgramSpace, program: true);
        _taken = _irc;
        _irc = _data;
        _pc = at;
        NoteWord(at);
    }

    /// <summary>The final fetch of an instruction: IRC becomes the next opcode and the queue reads on.</summary>
    private IEnumerable<bool> Prefetch()
    {
        var at = _pc + 2;
        yield return ReadWord(at, ProgramSpace, program: true);
        _ird = _irc;
        _irc = _data;
        _pc = at;
        NoteWord(at);
    }

    /// <summary>Keeps a word read from just past the instruction's last one, as one of its words if it needs it.</summary>
    private void NoteWord(uint at)
    {
        if (_wordCount < _words.Length && at == InstructionAddress + 2u * (uint)_wordCount) _words[_wordCount++] = _data;
    }
}
