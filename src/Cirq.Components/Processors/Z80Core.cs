namespace Cirq.Components.Processors;

/// <summary>What a Z80 machine cycle is, as the processor sees it.</summary>
public enum Z80CycleKind
{
    /// <summary>
    /// An M1 cycle: an opcode fetch (a prefix included) with /M1, /MREQ and /RD, the byte taken as
    /// T3 begins, then the refresh in T3 and T4.
    /// </summary>
    Fetch,

    /// <summary>A memory read: /MREQ and /RD, the byte taken in the middle of T3.</summary>
    Read,

    /// <summary>A memory write: /MREQ, then /WR from the middle of T2 to the middle of T3.</summary>
    Write,

    /// <summary>An I/O read: /IORQ and /RD from T2, an automatic wait state, the byte taken in T3.</summary>
    IoRead,

    /// <summary>An I/O write: /IORQ and /WR from T2, and an automatic wait state.</summary>
    IoWrite,

    /// <summary>
    /// An interrupt acknowledge: an M1 cycle with /IORQ in place of /MREQ and /RD, two automatic
    /// wait states, the byte on the data bus (a vector, or an instruction in mode 0) taken as T3
    /// begins, then a refresh.
    /// </summary>
    Acknowledge,

    /// <summary>A T-state the processor spends inside itself: no request, the address bus holding what it last had.</summary>
    Internal,

    /// <summary>The bus let go on /BUSREQ: /BUSACK low, the buses and /MREQ, /IORQ, /RD and /WR floating.</summary>
    Released,

    /// <summary>/RESET held low: the buses floating, the controls inactive.</summary>
    Reset,
}

/// <summary>Which T-state of its machine cycle a Z80 is in.</summary>
public enum Z80TState
{
    T1,
    T2,

    /// <summary>A wait state: one the processor puts in itself (I/O, acknowledge), or one /WAIT asked for.</summary>
    Tw,

    T3,
    T4,

    /// <summary>A T-state with no bus request: internal, released or held in reset.</summary>
    Ti,
}

/// <summary>
/// The Zilog Z80 as a sequence of T-states, with no circuit attached.
/// <para>
/// A Z80 instruction is a run of machine cycles — an opcode fetch (M1), memory reads and writes, I/O
/// reads and writes — each of three to six T-states, with internal T-states between them. This core
/// yields once per T-state, having set <see cref="Kind"/>, <see cref="State"/>, <see cref="Address"/>
/// and, on a write, <see cref="DataOut"/>; whoever drives it calls <see cref="Complete"/> as the
/// T-state ends, with the byte on the data bus when the T-state is one that takes it
/// (<see cref="SamplesData"/>). Written from Zilog's Z80 CPU User Manual (UM0080) and the Z80 CPU
/// datasheet, with the undocumented behaviour — the IXH, IXL, IYH and IYL halves, SLL, the DDCB
/// register copies, the ED duplicates, the X and Y flags (bits 3 and 5) and the internal MEMPTR
/// (WZ) register that shows in them — as published in "The Undocumented Z80 Documented" and the
/// MEMPTR notes that follow it.
/// </para>
/// <para>
/// <b>/WAIT</b> is sampled in T2 of a memory cycle, in the automatic wait state of an I/O cycle and
/// in the second one of an acknowledge (<see cref="SamplesWait"/>): set <see cref="WaitAsserted"/>
/// before completing such a T-state and a wait state follows, and another, until it is let go.
/// </para>
/// <para>
/// <b>Interrupts.</b> /INT is a level, sampled as the last T-state of an instruction begins and
/// taken if IFF1 is set — but never straight after EI, which lets one more instruction run. The
/// acknowledge reads a byte from the data bus: in mode 0 it is executed as an instruction (an RST,
/// usually), in mode 1 it is ignored and the processor calls $0038, and in mode 2 it is the low byte
/// of a pointer whose high byte is I, through which the processor calls. /NMI is an edge, latched
/// whenever it comes and taken at the next boundary, ahead of /INT: an opcode fetch thrown away, then
/// a call to $0066 with IFF1 cleared and IFF2 kept for RETN to restore. HALT fetches the byte after
/// itself as a NOP, again and again, with the HALT pin low, until an interrupt is taken.
/// </para>
/// <para>
/// <b>/BUSREQ</b> is sampled as the last T-state of a machine cycle begins; if low, the processor lets
/// go of the bus before its next bus cycle, with /BUSACK low, and takes it back the T-state /BUSREQ is
/// seen high. <b>/RESET</b> held low floats the bus; let go, the processor clears PC, I, R, IFF1 and
/// IFF2, sets interrupt mode 0, sets AF and SP to $FFFF, and fetches from $0000.
/// </para>
/// </summary>
public sealed partial class Z80Core
{
    public const byte FlagC = 0x01, FlagN = 0x02, FlagPV = 0x04, FlagX = 0x08, FlagH = 0x10, FlagY = 0x20, FlagZ = 0x40, FlagS = 0x80;

    /// <summary>Where mode 1 interrupts and RST $38 go.</summary>
    public const ushort InterruptAddress = 0x0038;

    /// <summary>Where an NMI goes.</summary>
    public const ushort NmiAddress = 0x0066;

    private IEnumerator<bool> _program;

    private byte _data;

    // A T-state as posted: everything the pins and the bus log need.
    private struct Posting
    {
        public Z80CycleKind Kind;
        public Z80TState State;
        public ushort Address;
        public byte DataOut;
        public bool SamplesWait;
        public bool SamplesData;
        public bool AutomaticWait;
        public bool Refresh;
        public bool First;
    }

    private Posting _now = new() { Kind = Z80CycleKind.Internal, State = Z80TState.Ti };
    private Posting _stashed;
    private bool _firstNext;

    // What the inputs were as the T-state on offer began.
    private bool _intAtPost, _nmiAtPost, _busRequestAtPost;
    private bool _nmiLatched, _nmiPrevious;

    // The boundary's housekeeping: what the instruction just finished did.
    private bool _flagsChanged;
    private bool _eiNow;
    private bool _ldAirNow;
    private bool _skipHousekeeping;

    public Z80Core()
    {
        PowerOn();
        _program = Program().GetEnumerator();
        _program.MoveNext();
    }

    // ---- registers --------------------------------------------------------

    public byte A { get; set; } = 0xFF;

    private byte _f = 0xFF;

    /// <summary>The flags: S Z Y H X P/V N C, bit 7 to bit 0. Y and X are bits 5 and 3 of something, as each instruction decides.</summary>
    public byte F
    {
        get => _f;
        set => _f = value;
    }

    public byte B { get; set; } = 0xFF;

    public byte C { get; set; } = 0xFF;

    public byte D { get; set; } = 0xFF;

    public byte E { get; set; } = 0xFF;

    public byte H { get; set; } = 0xFF;

    public byte L { get; set; } = 0xFF;

    public ushort AF
    {
        get => (ushort)((A << 8) | _f);
        set
        {
            A = (byte)(value >> 8);
            _f = (byte)value;
        }
    }

    public ushort BC
    {
        get => (ushort)((B << 8) | C);
        set
        {
            B = (byte)(value >> 8);
            C = (byte)value;
        }
    }

    public ushort DE
    {
        get => (ushort)((D << 8) | E);
        set
        {
            D = (byte)(value >> 8);
            E = (byte)value;
        }
    }

    public ushort HL
    {
        get => (ushort)((H << 8) | L);
        set
        {
            H = (byte)(value >> 8);
            L = (byte)value;
        }
    }

    /// <summary>The alternate set, swapped in by EX AF,AF' and EXX.</summary>
    public ushort AlternateAF { get; set; } = 0xFFFF;

    public ushort AlternateBC { get; set; } = 0xFFFF;

    public ushort AlternateDE { get; set; } = 0xFFFF;

    public ushort AlternateHL { get; set; } = 0xFFFF;

    public ushort IX { get; set; } = 0xFFFF;

    public ushort IY { get; set; } = 0xFFFF;

    public ushort SP { get; set; } = 0xFFFF;

    public ushort PC { get; set; }

    /// <summary>The interrupt page: the high byte of a mode 2 pointer, and of the refresh address.</summary>
    public byte I { get; set; }

    /// <summary>The refresh counter: its low seven bits count each M1; bit 7 is only ever what LD R,A put there.</summary>
    public byte R { get; set; }

    /// <summary>
    /// MEMPTR, the internal register Zilog calls WZ: the address an instruction last worked out. It
    /// is never read directly, but BIT n,(HL) puts its high byte's bits 5 and 3 in the flags.
    /// </summary>
    public ushort WZ { get; set; }

    /// <summary>The interrupt enable flip-flop: /INT is taken while it is set.</summary>
    public bool Iff1 { get; set; }

    /// <summary>IFF1's copy: an NMI clears IFF1 and keeps this, for RETN to restore; LD A,I and LD A,R read it into P/V.</summary>
    public bool Iff2 { get; set; }

    /// <summary>The interrupt mode, 0, 1 or 2.</summary>
    public int InterruptMode { get; set; }

    /// <summary>
    /// The flags the last instruction left if it changed them, zero if it did not: SCF and CCF take
    /// their X and Y from it on an NMOS Z80.
    /// </summary>
    public byte Q { get; set; }

    /// <summary>The instruction just finished was EI: no interrupt is taken before the next one.</summary>
    public bool EiLast { get; private set; }

    /// <summary>The instruction just finished was LD A,I or LD A,R: an interrupt taken now clears P/V, as on the NMOS part.</summary>
    public bool LdAirLast { get; private set; }

    private bool FlagSet(byte flag) => (_f & flag) != 0;

    /// <summary>Writes the flags, and marks them changed for <see cref="Q"/>.</summary>
    private void SetFlags(int value)
    {
        _f = (byte)value;
        _flagsChanged = true;
    }

    // ---- the T-state on offer -----------------------------------------------

    /// <summary>The machine cycle the T-state on offer belongs to.</summary>
    public Z80CycleKind Kind => _now.Kind;

    /// <summary>Which T-state of it.</summary>
    public Z80TState State => _now.State;

    /// <summary>The address bus: the cycle's address, the refresh address in T3 and T4 of an M1, or what it last held.</summary>
    public ushort Address => _now.Address;

    /// <summary>The byte a write cycle puts on the data bus.</summary>
    public byte DataOut => _now.DataOut;

    /// <summary>/WAIT is sampled in this T-state: T2 of a memory cycle, the automatic wait of an I/O cycle, the second of an acknowledge, and any wait after them.</summary>
    public bool SamplesWait => _now.SamplesWait;

    /// <summary>The data bus is taken in this T-state: at its end for an M1 or an acknowledge, in its middle for a read.</summary>
    public bool SamplesData => _now.SamplesData;

    /// <summary>The wait state on offer is one the processor inserted itself (an I/O cycle's, or an acknowledge's two).</summary>
    public bool AutomaticWait => _now.AutomaticWait;

    /// <summary>T3 or T4 of an M1 or an acknowledge: the refresh address on the bus, /RFSH low.</summary>
    public bool Refresh => _now.Refresh;

    /// <summary>The T-state on offer is the first of an instruction, or of an interrupt response taken in place of one.</summary>
    public bool FirstCycle => _now.First;

    /// <summary>T-states completed since power-on, wait states and released ones included.</summary>
    public long TStates { get; private set; }

    /// <summary>Wait states /WAIT has asked for.</summary>
    public long WaitStates { get; private set; }

    /// <summary>Instructions started since power-on.</summary>
    public long Instructions { get; private set; }

    /// <summary>The address of the instruction executing (or last fetched), its prefixes included.</summary>
    public ushort InstructionAddress { get; private set; }

    /// <summary>The opcode executing with its prefixes: $3E, $ED4D, $DD21, $DDCB06 (the displacement left out).</summary>
    public int Opcode { get; private set; }

    /// <summary>True after HALT until an interrupt: the processor fetches NOPs and the HALT pin is low.</summary>
    public bool Halted { get; private set; }

    /// <summary>True while the bus is let go on /BUSREQ (/BUSACK low).</summary>
    public bool BusAcknowledged => _now.Kind == Z80CycleKind.Released;

    /// <summary>The last interrupt taken: "NMI", "INT (IM 0)", "INT (IM 1)", "INT (IM 2)", "RESET", or empty.</summary>
    public string LastInterrupt { get; private set; } = string.Empty;

    /// <summary>The byte the last acknowledge read: a vector in mode 2, an instruction in mode 0.</summary>
    public byte LastAcknowledgeByte { get; private set; }

    // ---- the pins it samples ------------------------------------------------

    /// <summary>The /INT line is low.</summary>
    public bool IntAsserted { get; set; }

    /// <summary>The /NMI line is low. A falling edge is what interrupts.</summary>
    public bool NmiAsserted { get; set; }

    /// <summary>The /WAIT line was low when it was sampled in the T-state on offer.</summary>
    public bool WaitAsserted { get; set; }

    /// <summary>The /BUSREQ line is low.</summary>
    public bool BusRequested { get; set; }

    private bool _resetHeld;

    /// <summary>Holds the processor in reset (/RESET low). Letting go clears it and starts it at $0000.</summary>
    public bool ResetAsserted
    {
        get => _resetHeld;
        set
        {
            if (value && !_resetHeld)
            {
                _resetHeld = true;
                Halted = false;
                _program = ResetProgram().GetEnumerator();
                _program.MoveNext();
            }

            _resetHeld = value;
        }
    }

    /// <summary>Finishes the T-state on offer — with the data bus's byte, when it is one that takes it — and moves to the next.</summary>
    public void Complete(byte dataIn = 0)
    {
        if (_now.SamplesData) _data = dataIn;
        TStates++;

        // NMI is an edge, latched whenever it comes.
        if (NmiAsserted && !_nmiPrevious) _nmiLatched = true;
        _nmiPrevious = NmiAsserted;

        if (_now.Kind == Z80CycleKind.Released)
        {
            // Released until /BUSREQ is seen high as a T-state begins; then the cycle that was due.
            if (!BusRequested) _now = _stashed;
            Snapshot();
            return;
        }

        var busRequest = _busRequestAtPost;
        _program.MoveNext();

        // /BUSREQ seen as the last T-state of a cycle began: let go before the next bus cycle.
        if (busRequest && BusRequested && !_resetHeld && _now.State == Z80TState.T1)
        {
            _stashed = _now;
            _now = new Posting { Kind = Z80CycleKind.Released, State = Z80TState.Ti, Address = _now.Address };
        }
    }

    /// <summary>What the inputs are as a T-state begins, for the decisions taken in its last one.</summary>
    private void Snapshot()
    {
        _intAtPost = IntAsserted;
        _nmiAtPost = _nmiLatched;
        _busRequestAtPost = BusRequested;
    }

    /// <summary>
    /// Runs one whole instruction (or interrupt response, or HALT's NOP) against a memory and an I/O
    /// space, for tests: returns the T-states it took. An acknowledge reads <paramref name="acknowledge"/>.
    /// </summary>
    public int Step(Func<ushort, byte> read, Action<ushort, byte> write, Func<ushort, byte>? input = null,
        Action<ushort, byte>? output = null, Func<byte>? acknowledge = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);

        var count = 0;
        do
        {
            byte value = 0;
            if (_now.State is Z80TState.T3 && _now.Kind == Z80CycleKind.Write) write(Address, DataOut);
            if (_now.State is Z80TState.T3 && _now.Kind == Z80CycleKind.IoWrite) output?.Invoke(Address, DataOut);
            if (_now.SamplesData)
            {
                value = _now.Kind switch
                {
                    Z80CycleKind.IoRead => input?.Invoke(Address) ?? 0xFF,
                    Z80CycleKind.Acknowledge => acknowledge?.Invoke() ?? 0xFF,
                    _ => read(Address),
                };
            }

            Complete(value);
            count++;
        }
        while (!FirstCycle && count < 400);

        return count;
    }

    /// <summary>
    /// Puts the processor straight at the start of an instruction at <paramref name="pc"/> with its
    /// registers as they are now, interrupts as given, and no reset: what a test that sets up a state
    /// wants. <paramref name="q"/>, <paramref name="eiLast"/> and <paramref name="ldAirLast"/> are what
    /// the instruction before would have left.
    /// </summary>
    public void Load(ushort pc, byte q = 0, bool eiLast = false, bool ldAirLast = false)
    {
        PC = pc;
        Q = q;
        EiLast = eiLast;
        LdAirLast = ldAirLast;
        Halted = false;
        _resetHeld = false;
        _nmiLatched = _nmiPrevious = false;
        _skipHousekeeping = true;
        _program = Program().GetEnumerator();
        _program.MoveNext();
    }

    // ---- posting T-states -------------------------------------------------------

    private bool Post(Z80CycleKind kind, Z80TState state, int address, byte data = 0, bool samplesWait = false,
        bool samplesData = false, bool automatic = false, bool refresh = false)
    {
        _now = new Posting
        {
            Kind = kind,
            State = state,
            Address = (ushort)address,
            DataOut = data,
            SamplesWait = samplesWait,
            SamplesData = samplesData,
            AutomaticWait = automatic,
            Refresh = refresh,
            First = _firstNext,
        };
        _firstNext = false;
        Snapshot();
        return true;
    }

    /// <summary>A T-state inside the processor: the address bus keeps what it had.</summary>
    private bool Internal() => Post(Z80CycleKind.Internal, Z80TState.Ti, _now.Address);

    private IEnumerable<bool> Internal(int count)
    {
        for (var i = 0; i < count; i++) yield return Internal();
    }

    /// <summary>
    /// An M1 cycle at <paramref name="address"/>: T1, T2 and any wait states, the byte taken as T3
    /// begins, then T3 and T4 with I and R on the bus for the refresh, R counting on.
    /// </summary>
    private IEnumerable<bool> FetchCycle(int address)
    {
        yield return Post(Z80CycleKind.Fetch, Z80TState.T1, address);
        yield return Post(Z80CycleKind.Fetch, Z80TState.T2, address, samplesWait: true, samplesData: true);
        while (WaitAsserted)
        {
            WaitStates++;
            yield return Post(Z80CycleKind.Fetch, Z80TState.Tw, address, samplesWait: true, samplesData: true);
        }

        var opcode = _data;
        var refresh = (I << 8) | R;
        R = (byte)((R & 0x80) | ((R + 1) & 0x7F));
        yield return Post(Z80CycleKind.Fetch, Z80TState.T3, refresh, refresh: true);
        yield return Post(Z80CycleKind.Fetch, Z80TState.T4, refresh, refresh: true);
        _data = opcode;
    }

    /// <summary>A memory read: T1, T2, any wait states, T3; the byte in <see cref="_data"/>.</summary>
    private IEnumerable<bool> ReadCycle(int address)
    {
        yield return Post(Z80CycleKind.Read, Z80TState.T1, address);
        yield return Post(Z80CycleKind.Read, Z80TState.T2, address, samplesWait: true);
        while (WaitAsserted)
        {
            WaitStates++;
            yield return Post(Z80CycleKind.Read, Z80TState.Tw, address, samplesWait: true);
        }

        yield return Post(Z80CycleKind.Read, Z80TState.T3, address, samplesData: true);
    }

    private IEnumerable<bool> WriteCycle(int address, byte value)
    {
        yield return Post(Z80CycleKind.Write, Z80TState.T1, address, value);
        yield return Post(Z80CycleKind.Write, Z80TState.T2, address, value, samplesWait: true);
        while (WaitAsserted)
        {
            WaitStates++;
            yield return Post(Z80CycleKind.Write, Z80TState.Tw, address, value, samplesWait: true);
        }

        yield return Post(Z80CycleKind.Write, Z80TState.T3, address, value);
    }

    /// <summary>An I/O read: T1, T2, the automatic wait state and any /WAIT adds, T3.</summary>
    private IEnumerable<bool> InputCycle(int port)
    {
        yield return Post(Z80CycleKind.IoRead, Z80TState.T1, port);
        yield return Post(Z80CycleKind.IoRead, Z80TState.T2, port);
        yield return Post(Z80CycleKind.IoRead, Z80TState.Tw, port, samplesWait: true, automatic: true);
        while (WaitAsserted)
        {
            WaitStates++;
            yield return Post(Z80CycleKind.IoRead, Z80TState.Tw, port, samplesWait: true);
        }

        yield return Post(Z80CycleKind.IoRead, Z80TState.T3, port, samplesData: true);
    }

    private IEnumerable<bool> OutputCycle(int port, byte value)
    {
        yield return Post(Z80CycleKind.IoWrite, Z80TState.T1, port, value);
        yield return Post(Z80CycleKind.IoWrite, Z80TState.T2, port, value);
        yield return Post(Z80CycleKind.IoWrite, Z80TState.Tw, port, value, samplesWait: true, automatic: true);
        while (WaitAsserted)
        {
            WaitStates++;
            yield return Post(Z80CycleKind.IoWrite, Z80TState.Tw, port, value, samplesWait: true);
        }

        yield return Post(Z80CycleKind.IoWrite, Z80TState.T3, port, value);
    }

    /// <summary>
    /// The interrupt acknowledge: an M1 at PC with two automatic wait states, /IORQ low from the
    /// middle of the first, /WAIT sampled in the second, the byte taken as T3 begins; then the refresh.
    /// </summary>
    private IEnumerable<bool> AcknowledgeCycle()
    {
        yield return Post(Z80CycleKind.Acknowledge, Z80TState.T1, PC);
        yield return Post(Z80CycleKind.Acknowledge, Z80TState.T2, PC);
        yield return Post(Z80CycleKind.Acknowledge, Z80TState.Tw, PC, automatic: true);
        yield return Post(Z80CycleKind.Acknowledge, Z80TState.Tw, PC, samplesWait: true, samplesData: true, automatic: true);
        while (WaitAsserted)
        {
            WaitStates++;
            yield return Post(Z80CycleKind.Acknowledge, Z80TState.Tw, PC, samplesWait: true, samplesData: true);
        }

        var vector = _data;
        var refresh = (I << 8) | R;
        R = (byte)((R & 0x80) | ((R + 1) & 0x7F));
        yield return Post(Z80CycleKind.Acknowledge, Z80TState.T3, refresh, refresh: true);
        yield return Post(Z80CycleKind.Acknowledge, Z80TState.T4, refresh, refresh: true);
        _data = vector;
    }

    /// <summary>Two writes down the stack: the high byte first, at SP-1.</summary>
    private IEnumerable<bool> Push(ushort value)
    {
        SP--;
        foreach (var c in WriteCycle(SP, (byte)(value >> 8))) yield return c;
        SP--;
        foreach (var c in WriteCycle(SP, (byte)value)) yield return c;
    }

    // ---- the program ------------------------------------------------------

    private void PowerOn()
    {
        AF = 0xFFFF;
        SP = 0xFFFF;
        PC = 0;
        I = 0;
        R = 0;
        WZ = 0;
        Iff1 = Iff2 = false;
        InterruptMode = 0;
        Q = 0;
        EiLast = LdAirLast = false;
        Halted = false;
    }

    /// <summary>/RESET low: the bus floats until it is let go, then the registers are cleared and the program starts.</summary>
    private IEnumerable<bool> ResetProgram()
    {
        do
        {
            yield return Post(Z80CycleKind.Reset, Z80TState.Ti, _now.Address);
        }
        while (_resetHeld);

        PowerOn();
        LastInterrupt = "RESET";
        foreach (var c in Program()) yield return c;
    }

    private IEnumerable<bool> Program()
    {
        while (true)
        {
            // The boundary: what the instruction just finished leaves behind.
            if (!_skipHousekeeping)
            {
                Q = _flagsChanged ? _f : (byte)0;
                EiLast = _eiNow;
                LdAirLast = _ldAirNow;
            }

            _skipHousekeeping = false;
            _flagsChanged = _eiNow = _ldAirNow = false;
            _index = 0;
            _firstNext = true;

            // Interrupts, as sampled when the last T-state began: NMI first, /INT if IFF1 allows and EI did not just run.
            if (_nmiAtPost)
            {
                foreach (var c in TakeNmi()) yield return c;
                continue;
            }

            if (_intAtPost && Iff1 && !EiLast)
            {
                foreach (var c in TakeInterrupt()) yield return c;
                continue;
            }

            if (Halted)
            {
                // HALT: the byte at PC fetched and ignored, PC left where it is.
                foreach (var c in FetchCycle(PC)) yield return c;
                continue;
            }

            InstructionAddress = PC;
            Instructions++;
            foreach (var c in FetchCycle(PC)) yield return c;
            PC++;
            foreach (var c in Execute(_data)) yield return c;
        }
    }

    /// <summary>An NMI: an opcode fetch thrown away, a T-state, PC pushed, and a call to $0066 with IFF1 cleared.</summary>
    private IEnumerable<bool> TakeNmi()
    {
        Halted = false;
        _nmiLatched = false;
        LastInterrupt = "NMI";
        InstructionAddress = PC;
        foreach (var c in FetchCycle(PC)) yield return c;
        yield return Internal();
        Iff1 = false;
        foreach (var c in Push(PC)) yield return c;
        PC = NmiAddress;
        WZ = PC;
    }

    /// <summary>A maskable interrupt in the current mode, after the acknowledge.</summary>
    private IEnumerable<bool> TakeInterrupt()
    {
        Halted = false;

        // The NMOS part's fault: an interrupt straight after LD A,I or LD A,R leaves P/V clear.
        if (LdAirLast) _f = (byte)(_f & ~FlagPV);
        Iff1 = Iff2 = false;
        LastInterrupt = $"INT (IM {InterruptMode})";
        InstructionAddress = PC;

        foreach (var c in AcknowledgeCycle()) yield return c;
        var value = _data;
        LastAcknowledgeByte = value;

        switch (InterruptMode)
        {
            case 0:
                // The byte is an instruction — an RST, usually — run with PC where it was.
                Instructions++;
                foreach (var c in Execute(value)) yield return c;
                break;

            case 1:
                yield return Internal();
                foreach (var c in Push(PC)) yield return c;
                PC = InterruptAddress;
                WZ = PC;
                break;

            default:
            {
                // Mode 2: the byte and I make a pointer to the handler's address. All eight bits are used.
                yield return Internal();
                foreach (var c in Push(PC)) yield return c;
                var pointer = (I << 8) | value;
                foreach (var c in ReadCycle(pointer)) yield return c;
                var low = _data;
                foreach (var c in ReadCycle((pointer + 1) & 0xFFFF)) yield return c;
                PC = (ushort)((_data << 8) | low);
                WZ = PC;
                break;
            }
        }
    }
}
