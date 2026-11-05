namespace Cirq.Components.Processors;

/// <summary>What a 6809 bus cycle is, as the processor sees it.</summary>
public enum M6809CycleKind
{
    /// <summary>The first byte of an instruction — the opcode, or the $10 or $11 in front of one.</summary>
    Fetch,

    /// <summary>A read: an operand, a pointer, the stack, or a don't-care read of the instruction stream.</summary>
    Read,

    Write,

    /// <summary>
    /// A cycle the processor spends inside itself. The address bus shows $FFFF with R/W high, so
    /// the memory there is read, and the byte is ignored: Motorola's "VMA" cycle.
    /// </summary>
    Dead,

    /// <summary>A read of an interrupt or reset vector, with BS high.</summary>
    Vector,

    /// <summary>No cycle at all: the buses are let go (BA high) — SYNC, /HALT or a DMA grant.</summary>
    Released,
}

/// <summary>
/// The Motorola 6809 as a sequence of bus cycles, with no circuit attached.
/// <para>
/// Every E cycle a 6809 runs is a bus cycle: it puts an address out and reads or writes a byte.
/// Even the cycles it spends inside itself go on the bus — as a read of $FFFF, the "dead" or VMA
/// cycle the datasheet draws in its cycle-by-cycle tables — and so do the don't-care reads of the
/// byte after a one-byte opcode. This core yields once per cycle, having set <see cref="Address"/>,
/// <see cref="IsRead"/>, <see cref="DataOut"/> and <see cref="Kind"/>; whoever drives it performs
/// the cycle and calls <see cref="Complete"/> with the byte read, as for <see cref="Mos6502Core"/>.
/// Written from Motorola's MC6809 datasheet and the 6809 programming manual: every instruction on
/// its three pages, every addressing mode and every cycle count.
/// </para>
/// <para>
/// <b>Interrupts.</b> /IRQ and /FIRQ are levels, masked by I and F; /NMI is an edge, and is not
/// armed until a program has loaded S, so it cannot strike before there is a stack. Each is sampled
/// as a cycle ends and acted on at an instruction boundary, using the sample from the instruction's
/// next-to-last cycle. NMI and IRQ stack the entire state (E set: CC, A, B, DP, X, Y, U and PC);
/// FIRQ stacks only CC and PC, with E clear, so RTI knows how much to pull. SWI, SWI2 and SWI3 stack
/// the entire state; SWI masks IRQ and FIRQ. SYNC lets go of the bus until an interrupt line is
/// asserted, masked or not; CWAI stacks everything first and waits on the bus.
/// </para>
/// <para>
/// <b>/HALT</b> stops the processor at the end of the instruction with the buses let go (BA and BS
/// high); <b>/DMA/BREQ</b> lets them go at the end of the cycle, after a dead cycle, and takes one
/// cycle back every fifteen to refresh its own registers. <b>/RESET</b> abandons everything; when
/// it is let go the processor masks IRQ and FIRQ, clears DP, disarms NMI and fetches the vector at
/// $FFFE.
/// </para>
/// </summary>
public sealed partial class M6809Core
{
    public const byte FlagC = 0x01, FlagV = 0x02, FlagZ = 0x04, FlagN = 0x08, FlagI = 0x10, FlagH = 0x20, FlagF = 0x40, FlagE = 0x80;

    /// <summary>The vectors, in the top sixteen bytes: SWI3, SWI2, FIRQ, IRQ, SWI, NMI and RESET.</summary>
    public const ushort VectorSwi3 = 0xFFF2, VectorSwi2 = 0xFFF4, VectorFirq = 0xFFF6, VectorIrq = 0xFFF8,
        VectorSwi = 0xFFFA, VectorNmi = 0xFFFC, VectorReset = 0xFFFE;

    /// <summary>A DMA grant lasts this many cycles before the processor takes one back to refresh itself.</summary>
    public const int DmaCyclesBeforeRefresh = 15;

    private IEnumerator<bool> _program;

    private byte _data;
    private int _ea;

    // Interrupt sampling: what was seen as this cycle ended, and as the one before it did.
    private bool _nmiLatched;
    private bool _nmiPrevious;
    private bool _nmiNow, _nmiBefore;
    private bool _firqNow, _firqBefore;
    private bool _irqNow, _irqBefore;

    // A DMA grant in progress: the cycle the program had posted, kept until the grant ends.
    private int _dmaState;
    private int _dmaCount;
    private (ushort Address, bool IsRead, byte DataOut, M6809CycleKind Kind, bool Ba, bool Bs) _stashed;

    public M6809Core()
    {
        _program = Program(skipReset: false).GetEnumerator();
        _program.MoveNext();
    }

    // ---- registers --------------------------------------------------------

    public byte A { get; set; }

    public byte B { get; set; }

    /// <summary>A and B as one 16-bit accumulator, A the high byte.</summary>
    public ushort D
    {
        get => (ushort)((A << 8) | B);
        set
        {
            A = (byte)(value >> 8);
            B = (byte)value;
        }
    }

    public ushort X { get; set; }

    public ushort Y { get; set; }

    /// <summary>The user stack pointer.</summary>
    public ushort U { get; set; }

    /// <summary>The hardware stack pointer: interrupts and subroutine calls use it.</summary>
    public ushort S { get; set; }

    public ushort PC { get; set; }

    /// <summary>The direct page: the high byte of every direct address.</summary>
    public byte DP { get; set; }

    /// <summary>The condition codes, EFHINZVC.</summary>
    public byte CC { get; set; } = FlagI | FlagF;

    private bool Carry => (CC & FlagC) != 0;

    private void Flag(byte flag, bool on) => CC = (byte)(on ? CC | flag : CC & ~flag);

    // ---- the bus cycle on offer -------------------------------------------

    /// <summary>The address of the cycle in progress: $FFFF on a dead cycle.</summary>
    public ushort Address { get; private set; } = 0xFFFF;

    /// <summary>True when the cycle reads (dead cycles included); false when it writes <see cref="DataOut"/>.</summary>
    public bool IsRead { get; private set; } = true;

    /// <summary>The byte being written, on a write cycle.</summary>
    public byte DataOut { get; private set; }

    /// <summary>What the cycle in progress is.</summary>
    public M6809CycleKind Kind { get; private set; } = M6809CycleKind.Dead;

    /// <summary>BA: the buses are let go (SYNC, a halt, a DMA grant).</summary>
    public bool BusAvailable { get; private set; }

    /// <summary>BS: with BA low, a vector fetch; with BA high, a halt or bus grant (low there is SYNC).</summary>
    public bool BusStatus { get; private set; }

    /// <summary>
    /// The 6809E's BUSY: the read and modify cycles of a read-modify-write, the first byte of a
    /// 16-bit operand, and the first byte of an indirect pointer or a vector.
    /// </summary>
    public bool Busy { get; private set; }

    private bool _busyNext;

    /// <summary>The cycle on offer is the first of an instruction, or of an interrupt sequence taken in place of one.</summary>
    public bool FirstCycle { get; private set; }

    private bool _firstNext;

    /// <summary>Cycles completed since power-on: E cycles, dead and released ones included.</summary>
    public long Cycles { get; private set; }

    /// <summary>Instructions started since power-on.</summary>
    public long Instructions { get; private set; }

    /// <summary>The address of the instruction executing (or last fetched), its prefix included.</summary>
    public ushort InstructionAddress { get; private set; }

    /// <summary>The opcode executing, with its page: $3A, or $108E for LDY #.</summary>
    public int Opcode { get; private set; }

    /// <summary>True while SYNC waits for an interrupt line.</summary>
    public bool IsSyncing { get; private set; }

    /// <summary>True while CWAI waits for an interrupt, the state already stacked.</summary>
    public bool IsWaiting { get; private set; }

    /// <summary>True while /HALT holds the processor between instructions.</summary>
    public bool IsHalted { get; private set; }

    /// <summary>True during a DMA grant.</summary>
    public bool IsGranted => _dmaState != 0;

    /// <summary>True after the $14, $15 or $CD test-mode opcodes: only a reset starts it again.</summary>
    public bool IsStopped { get; private set; }

    /// <summary>NMI is armed: a program has loaded S since the last reset.</summary>
    public bool NmiArmed { get; private set; }

    /// <summary>The last interrupt or software interrupt taken: "IRQ", "FIRQ", "NMI", "SWI", "SWI2", "SWI3", "RESET", or empty.</summary>
    public string LastInterrupt { get; private set; } = string.Empty;

    // ---- the pins it samples ------------------------------------------------

    /// <summary>The /IRQ line is low.</summary>
    public bool IrqAsserted { get; set; }

    /// <summary>The /FIRQ line is low.</summary>
    public bool FirqAsserted { get; set; }

    /// <summary>The /NMI line is low. A falling edge is what interrupts.</summary>
    public bool NmiAsserted { get; set; }

    /// <summary>The /HALT line is low: stop at the end of the instruction.</summary>
    public bool HaltAsserted { get; set; }

    /// <summary>The /DMA/BREQ line is low: let go of the bus at the end of the cycle.</summary>
    public bool DmaRequested { get; set; }

    private bool _resetHeld;

    /// <summary>Holds the processor in reset (/RESET low). Letting go runs the reset sequence.</summary>
    public bool ResetAsserted
    {
        get => _resetHeld;
        set
        {
            if (value && !_resetHeld)
            {
                _dmaState = 0;
                _program = Program(skipReset: false).GetEnumerator();
                _program.MoveNext();
            }

            _resetHeld = value;
        }
    }

    /// <summary>Finishes the cycle on offer — with the byte the bus held, on a read — and moves to the next.</summary>
    public void Complete(byte dataIn = 0)
    {
        _data = IsRead ? dataIn : DataOut;
        Cycles++;

        // NMI is an edge, and only once a program has loaded S.
        if (NmiAsserted && !_nmiPrevious && NmiArmed) _nmiLatched = true;
        _nmiPrevious = NmiAsserted;

        _nmiBefore = _nmiNow;
        _nmiNow = _nmiLatched;
        _firqBefore = _firqNow;
        _firqNow = FirqAsserted;
        _irqBefore = _irqNow;
        _irqNow = IrqAsserted;

        if (_dmaState != 0)
        {
            Grant();
            return;
        }

        if (TracksNextCycle && _origin is not null) _fed.Add(_data);
        _program.MoveNext();

        // A DMA request lets go of the bus at the end of the cycle: a dead cycle first, then the grant.
        if (DmaRequested && !_resetHeld && Kind != M6809CycleKind.Released)
        {
            _stashed = (Address, IsRead, DataOut, Kind, BusAvailable, BusStatus);
            _dmaState = 1;
            _dmaCount = 0;
            Post(0xFFFF, true, 0, M6809CycleKind.Dead);
        }
    }

    /// <summary>The cycles of a DMA grant: the bus let go, a cycle back every fifteen, and a dead cycle to end it.</summary>
    private void Grant()
    {
        switch (_dmaState)
        {
            case 1 or 3:
                // The dead cycle before a grant, or a refresh cycle within one.
                if (DmaRequested)
                {
                    _dmaState = 2;
                    _dmaCount = 0;
                    Post(0xFFFF, true, 0, M6809CycleKind.Released, ba: true, bs: true);
                }
                else
                {
                    Resume();
                }

                break;

            case 2:
                _dmaCount++;
                if (!DmaRequested)
                {
                    _dmaState = 4;
                    Post(0xFFFF, true, 0, M6809CycleKind.Dead);
                }
                else if (_dmaCount >= DmaCyclesBeforeRefresh - 1)
                {
                    _dmaState = 3;
                    Post(0xFFFF, true, 0, M6809CycleKind.Dead);
                }

                break;

            default:
                Resume();
                break;
        }
    }

    private void Resume()
    {
        _dmaState = 0;
        (Address, IsRead, DataOut, Kind, BusAvailable, BusStatus) = _stashed;
    }

    // ---- looking a cycle ahead: the 6809E's LIC and AVMA ------------------------

    /// <summary>
    /// Keep what <see cref="PeekNext"/> needs: the state each instruction started from and the bytes
    /// it has read. The 6809E part turns it on; the 6809, with no LIC or AVMA pin, leaves it off.
    /// </summary>
    public bool TracksNextCycle { get; set; }

    private sealed record Origin(ushort PC, byte A, byte B, ushort X, ushort Y, ushort U, ushort S, byte DP, byte CC,
        bool NmiArmed, bool NmiLatched, bool Nmi, bool Firq, bool Irq);

    private Origin? _origin;
    private readonly List<byte> _fed = [];
    private long _boundaries;

    /// <summary>An instruction boundary: where the next instruction, or an interrupt, starts.</summary>
    private void MarkBoundary()
    {
        _boundaries++;
        _firstNext = true;
        if (!TracksNextCycle) return;
        _origin = new Origin(PC, A, B, X, Y, U, S, DP, CC, NmiArmed, _nmiLatched, _nmiBefore, _firqBefore, _irqBefore);
        _fed.Clear();
    }

    /// <summary>A copy started at <paramref name="origin"/>, its interrupt samples as they were then.</summary>
    private M6809Core(Origin origin, M6809Core live)
    {
        (PC, A, B, X, Y, U, S, DP, CC) = (origin.PC, origin.A, origin.B, origin.X, origin.Y, origin.U, origin.S, origin.DP, origin.CC);
        NmiArmed = origin.NmiArmed;
        _nmiLatched = origin.NmiLatched;
        _nmiBefore = origin.Nmi;
        _firqBefore = origin.Firq;
        _irqBefore = origin.Irq;
        (IrqAsserted, FirqAsserted, NmiAsserted) = (live.IrqAsserted, live.FirqAsserted, live.NmiAsserted);
        _nmiPrevious = live._nmiPrevious;
        _program = Program(skipReset: true).GetEnumerator();
        _program.MoveNext();
    }

    /// <summary>
    /// What the cycle after the one on offer will be, before this one has its data: whether it uses
    /// the bus (the 6809E's AVMA) and whether it starts an instruction, making this one the last
    /// (LIC). The chip knows both a cycle ahead because neither depends on the byte in flight; this
    /// replays the instruction so far on a copy of the core and finishes the cycle with a byte of
    /// zero to see. Needs <see cref="TracksNextCycle"/>.
    /// </summary>
    public (bool Valid, bool Last) PeekNext()
    {
        if (_origin is null || _resetHeld || _dmaState != 0 || Kind == M6809CycleKind.Released || IsStopped) return (false, false);

        var copy = new M6809Core(_origin, this);
        foreach (var b in _fed) copy.Complete(b);
        var boundaries = copy._boundaries;
        copy.Complete(0);
        return (copy.Kind is not (M6809CycleKind.Dead or M6809CycleKind.Released), copy._boundaries != boundaries);
    }

    /// <summary>Runs one whole instruction against a memory, for tests: returns the cycles it took.</summary>
    public int Step(Func<ushort, byte> read, Action<ushort, byte> write)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);

        var count = 0;
        do
        {
            if (Kind == M6809CycleKind.Released) Complete();
            else if (IsRead) Complete(read(Address));
            else
            {
                write(Address, DataOut);
                Complete();
            }

            count++;
        }
        while (!FirstCycle && count < 200);

        return count;
    }

    /// <summary>
    /// Puts the processor straight at the start of an instruction at <paramref name="pc"/>, with its
    /// registers as given, NMI armed, and no reset sequence: what a test that sets up a state wants.
    /// </summary>
    public void Load(ushort pc, byte a, byte b, ushort x, ushort y, ushort u, ushort s, byte dp, byte cc)
    {
        PC = pc;
        A = a;
        B = b;
        X = x;
        Y = y;
        U = u;
        S = s;
        DP = dp;
        CC = cc;
        NmiArmed = true;
        IsStopped = IsSyncing = IsWaiting = IsHalted = false;
        _nmiLatched = _nmiNow = _nmiBefore = _nmiPrevious = false;
        _firqNow = _firqBefore = _irqNow = _irqBefore = false;
        _resetHeld = false;
        _dmaState = 0;
        _program = Program(skipReset: true).GetEnumerator();
        _program.MoveNext();
    }

    // ---- posting cycles -------------------------------------------------------

    private bool Post(int address, bool read, byte value, M6809CycleKind kind, bool ba = false, bool bs = false)
    {
        Address = (ushort)address;
        IsRead = read;
        DataOut = value;
        Kind = kind;
        BusAvailable = ba;
        BusStatus = bs;
        Busy = _busyNext;
        _busyNext = false;
        FirstCycle = _firstNext;
        _firstNext = false;
        return true;
    }

    private bool Read(int address) => Post(address, true, 0, M6809CycleKind.Read);

    private bool Write(int address, byte value) => Post(address, false, value, M6809CycleKind.Write);

    /// <summary>A dead cycle: $FFFF on the address bus, read and ignored.</summary>
    private bool Dead() => Post(0xFFFF, true, 0, M6809CycleKind.Dead);

    private bool Vector(int address) => Post(address, true, 0, M6809CycleKind.Vector, bs: true);

    private bool PushS(byte value)
    {
        S--;
        return Write(S, value);
    }

    private bool PushU(byte value)
    {
        U--;
        return Write(U, value);
    }

    // ---- the program ------------------------------------------------------

    private IEnumerable<bool> Program(bool skipReset)
    {
        if (!skipReset)
        {
            foreach (var c in ResetSequence()) yield return c;
        }

        while (true)
        {
            if (IsStopped)
            {
                // The test mode: nothing but a reset gets it back. Shown as dead cycles.
                yield return Dead();
                continue;
            }

            if (HaltAsserted)
            {
                IsHalted = true;
                while (HaltAsserted) yield return Post(0xFFFF, true, 0, M6809CycleKind.Released, ba: true, bs: true);
                IsHalted = false;
            }

            MarkBoundary();

            // An interrupt is taken between instructions, by what was sampled in the next-to-last cycle.
            var nmi = _nmiBefore;
            var firq = _firqBefore && (CC & FlagF) == 0;
            var irq = _irqBefore && (CC & FlagI) == 0;
            if (nmi || firq || irq)
            {
                InstructionAddress = PC;
                foreach (var c in Interrupt(nmi ? 0 : firq ? 1 : 2)) yield return c;
                continue;
            }

            InstructionAddress = PC;
            yield return Post(PC, true, 0, M6809CycleKind.Fetch);
            PC++;
            Instructions++;

            var page = 0;
            var opcode = _data;
            if (opcode is 0x10 or 0x11)
            {
                // The page 2 or 3 opcode is the next byte; a run of prefixes counts as the first.
                page = opcode;
                do
                {
                    yield return Read(PC);
                    PC++;
                    opcode = _data;
                }
                while (opcode is 0x10 or 0x11);
            }

            Opcode = (page << 8) | opcode;
            foreach (var c in Execute(page, opcode)) yield return c;
        }
    }

    private IEnumerable<bool> ResetSequence()
    {
        // Held in reset: dead cycles until the line comes up.
        do
        {
            yield return Dead();
        }
        while (_resetHeld);

        CC |= FlagI | FlagF;
        DP = 0;
        NmiArmed = false;
        IsStopped = IsSyncing = IsWaiting = IsHalted = false;
        _nmiLatched = _nmiNow = _nmiBefore = false;
        LastInterrupt = "RESET";

        yield return Dead();
        _busyNext = true;
        yield return Vector(VectorReset);
        var high = _data;
        yield return Vector(VectorReset + 1);
        PC = (ushort)((high << 8) | _data);
        yield return Dead();
    }

    /// <summary>NMI (0), FIRQ (1) or IRQ (2), taken at an instruction boundary.</summary>
    private IEnumerable<bool> Interrupt(int which)
    {
        // The opcode at PC is fetched and thrown away, twice, and the PC kept for the return.
        yield return Read(PC);
        yield return Read(PC);
        yield return Dead();

        var entire = which != 1;
        Flag(FlagE, entire);
        foreach (var c in Stack(entire)) yield return c;

        if (which == 0) _nmiLatched = false;
        CC |= which == 2 ? FlagI : (byte)(FlagI | FlagF);
        LastInterrupt = which switch { 0 => "NMI", 1 => "FIRQ", _ => "IRQ" };

        foreach (var c in Vectored(which switch { 0 => VectorNmi, 1 => VectorFirq, _ => VectorIrq })) yield return c;
    }

    /// <summary>Pushes the state onto S: PC, then (entire) U, Y, X, DP, B and A, then CC.</summary>
    private IEnumerable<bool> Stack(bool entire)
    {
        yield return PushS((byte)PC);
        yield return PushS((byte)(PC >> 8));
        if (entire)
        {
            yield return PushS((byte)U);
            yield return PushS((byte)(U >> 8));
            yield return PushS((byte)Y);
            yield return PushS((byte)(Y >> 8));
            yield return PushS((byte)X);
            yield return PushS((byte)(X >> 8));
            yield return PushS(DP);
            yield return PushS(B);
            yield return PushS(A);
        }

        yield return PushS(CC);
    }

    /// <summary>A dead cycle, the vector read with BS high, and a dead cycle before the handler's first fetch.</summary>
    private IEnumerable<bool> Vectored(ushort vector, bool leadingDead = true)
    {
        if (leadingDead) yield return Dead();
        _busyNext = true;
        yield return Vector(vector);
        var high = _data;
        yield return Vector(vector + 1);
        PC = (ushort)((high << 8) | _data);
        yield return Dead();
    }
}
