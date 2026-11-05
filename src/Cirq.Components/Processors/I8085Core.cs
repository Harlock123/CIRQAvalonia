namespace Cirq.Components.Processors;

/// <summary>What an 8085 machine cycle is, as its status lines IO/M, S1 and S0 and its strobes say.</summary>
public enum I8085CycleKind
{
    /// <summary>An opcode fetch (IO/M 0, S1 1, S0 1): /RD, the opcode taken in T3, then T4 (and T5, T6) inside the processor.</summary>
    OpcodeFetch,

    /// <summary>A memory read (0, 1, 0): /RD, the byte taken in T3.</summary>
    MemoryRead,

    /// <summary>A memory write (0, 0, 1): the byte on AD0-AD7 and /WR from T2.</summary>
    MemoryWrite,

    /// <summary>An I/O read (1, 1, 0): the port on both AD0-AD7 and A8-A15.</summary>
    IoRead,

    /// <summary>An I/O write (1, 0, 1).</summary>
    IoWrite,

    /// <summary>
    /// An interrupt acknowledge for INTR (1, 1, 1): /INTA in place of /RD, the device's byte taken
    /// in T3, and PC not counting on. An RST is one such cycle; a CALL is three.
    /// </summary>
    InterruptAcknowledge,

    /// <summary>
    /// The opcode fetch the processor makes up for TRAP, RST 7.5, 6.5 or 5.5 (1, 1, 1): six
    /// T-states with ALE but no strobe, the restart generated inside.
    /// </summary>
    RestartAcknowledge,

    /// <summary>A bus idle machine cycle (0, 1, 0, no ALE and no strobe): the second and third of DAD and its relatives.</summary>
    BusIdle,

    /// <summary>HALT: the buses, /RD, /WR and IO/M floating, S1 and S0 low.</summary>
    Halt,

    /// <summary>The bus let go on HOLD: HLDA high, the buses, /RD, /WR and IO/M floating.</summary>
    Hold,

    /// <summary>RESET IN low: RESET OUT high, the buses floating.</summary>
    Reset,
}

/// <summary>Which state of its machine cycle an 8085 is in.</summary>
public enum I8085TState
{
    T1,
    T2,

    /// <summary>A wait state READY asked for, between T2 and T3.</summary>
    Tw,

    T3,
    T4,
    T5,
    T6,

    /// <summary>A T-state halted.</summary>
    Thalt,

    /// <summary>A T-state with the bus let go on HOLD.</summary>
    Thold,

    /// <summary>A T-state held in reset.</summary>
    Treset,
}

/// <summary>
/// The Intel 8085 (8085A, 8085AH) as a sequence of T-states, with no circuit attached.
/// <para>
/// An instruction is a run of machine cycles — an opcode fetch of four or six T-states, memory and
/// I/O reads and writes of three, bus idle cycles of three — and this core yields once per T-state,
/// having set <see cref="Kind"/>, <see cref="State"/>, <see cref="Address"/> and, on a write,
/// <see cref="DataOut"/>; whoever drives it calls <see cref="Complete"/> as the T-state ends, with
/// the byte on the data bus when the T-state is one that takes it (<see cref="SamplesData"/>).
/// Written from Intel's 8080/8085 Assembly Language Programming Manual, the MCS-80/85 Family User's
/// Manual (chapter 2's state transitions and machine cycles) and the 8085AH datasheet, with the
/// undocumented instructions as the CA80C85B datasheet lists them and the undocumented V and K flags
/// as the reverse-engineered die shows them (Ken Shirriff's analysis of the visual6502 team's 8085).
/// </para>
/// <para>
/// <b>READY</b> is sampled in T2 of a read, write or acknowledge, and in each wait state
/// (<see cref="SamplesReady"/>): set <see cref="ReadyLow"/> before completing such a T-state and a
/// wait state follows. Bus idle cycles and the restart acknowledge ignore it. <b>HOLD</b> is sampled
/// in T2 (and the wait states), in T4 of a six-state fetch, and in every HALT and HOLD state: seen
/// high, HLDA goes high for the next T-state and the bus floats from the one after, the machine
/// cycle's internal states run on, and the next machine cycle waits in hold states until HOLD is
/// seen low.
/// </para>
/// <para>
/// <b>Interrupts</b> are sampled in the next-to-last T-state of each instruction (and in every HALT
/// and HOLD state), and taken in priority order: TRAP (a rising edge latched, and the level still
/// high), RST 7.5 (a rising edge latched, even while masked), RST 6.5 and RST 5.5 (levels), each
/// unless masked by SIM, then INTR — the last four only while interrupts are enabled, and never
/// straight after EI. TRAP and the RSTs call their restart addresses ($24, $3C, $34, $2C) after a
/// six-state acknowledge with no strobe; INTR is answered by /INTA, the device putting an instruction
/// on the bus — an RST, or a CALL whose two address bytes come in two more acknowledges — run with
/// PC held. Taking any interrupt disables the rest until EI; TRAP keeps the enable it found for the
/// first RIM after it. HLT floats the bus until an interrupt (or HOLD, for the length of the hold).
/// <b>RESET IN</b> held low floats the bus; let go, the processor clears PC, disables interrupts,
/// masks all three RSTs, clears the RST 7.5 and TRAP latches and SOD, and fetches from $0000 — the
/// other registers keep what they had.
/// </para>
/// </summary>
public sealed partial class I8085Core
{
    public const byte FlagC = 0x01, FlagV = 0x02, FlagP = 0x04, FlagAC = 0x10, FlagK = 0x20, FlagZ = 0x40, FlagS = 0x80;

    /// <summary>The restart addresses: TRAP, RST 7.5, RST 6.5, RST 5.5, and RSTV's.</summary>
    public const ushort TrapAddress = 0x0024, Rst75Address = 0x003C, Rst65Address = 0x0034, Rst55Address = 0x002C, OverflowAddress = 0x0040;

    private IEnumerator<bool> _program;

    private byte _data;

    // A T-state as posted: everything the pins and the bus log need.
    private struct Posting
    {
        public I8085CycleKind Kind;
        public I8085TState State;
        public ushort Address;
        public byte DataOut;
        public bool SamplesReady;
        public bool SamplesData;
        public bool SamplesHold;
        public bool Ale;
        public bool Floated;
        public bool First;
    }

    private Posting _now = new() { Kind = I8085CycleKind.Reset, State = I8085TState.Treset };
    private bool _firstNext;

    // The interrupt inputs as sampled in a T-state: this one, and the one before.
    private readonly record struct Sample(bool Trap, bool Rst75, bool Rst65, bool Rst55, bool Intr);

    private Sample _sample, _previousSample;
    private bool _rst75Was, _trapWas;
    private bool _trapLatch;

    private bool _hldaFlipFlop;
    private int _hldaAge;

    // The boundary's housekeeping.
    private bool _eiNow;
    private bool _skipHousekeeping;
    private int? _sodPending;
    private bool _acknowledging;
    private bool _ieBeforeTrap;
    private bool _trapRimPending;

    public I8085Core()
    {
        PowerOn();
        _program = Program().GetEnumerator();
        _program.MoveNext();
    }

    // ---- registers --------------------------------------------------------

    public byte A { get; set; }

    private byte _f;

    /// <summary>The flags: S Z K AC 0 P V CY, bit 7 to bit 0. Bit 3 is always 0.</summary>
    public byte F
    {
        get => _f;
        set => _f = (byte)(value & 0xF7);
    }

    public byte B { get; set; }

    public byte C { get; set; }

    public byte D { get; set; }

    public byte E { get; set; }

    public byte H { get; set; }

    public byte L { get; set; }

    /// <summary>A and the flags, as PUSH PSW puts them on the stack.</summary>
    public ushort Psw
    {
        get => (ushort)((A << 8) | _f);
        set
        {
            A = (byte)(value >> 8);
            F = (byte)value;
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

    public ushort SP { get; set; }

    public ushort PC { get; set; }

    /// <summary>The interrupt enable flip-flop (INTE): set by EI, cleared by DI, by any interrupt taken and by reset.</summary>
    public bool InterruptsEnabled { get; set; }

    /// <summary>The RST masks SIM sets: bit 0 RST 5.5, bit 1 RST 6.5, bit 2 RST 7.5; a 1 masks it out. Reset sets all three.</summary>
    public int Masks { get; set; } = 7;

    /// <summary>The RST 7.5 flip-flop: set by a rising edge on its pin, masked or not; cleared when it is taken, by SIM with bit 4, or by reset.</summary>
    public bool Rst75Pending { get; set; }

    /// <summary>The TRAP flip-flop: set by a rising edge on TRAP, cleared when the TRAP is taken.</summary>
    public bool TrapPending => _trapLatch;

    /// <summary>The SOD latch, as SIM last set it.</summary>
    public bool Sod { get; private set; }

    /// <summary>The instruction just finished was EI: no interrupt is taken before the next one.</summary>
    public bool EiLast { get; private set; }

    private bool FlagSet(byte flag) => (_f & flag) != 0;

    // ---- the T-state on offer -----------------------------------------------

    /// <summary>The machine cycle the T-state on offer belongs to.</summary>
    public I8085CycleKind Kind => _now.Kind;

    /// <summary>Which T-state of it.</summary>
    public I8085TState State => _now.State;

    /// <summary>The address: AD0-AD7 in T1 and A8-A15 throughout; for I/O the port on both halves.</summary>
    public ushort Address => _now.Address;

    /// <summary>The byte a write puts on AD0-AD7 from T2.</summary>
    public byte DataOut => _now.DataOut;

    /// <summary>READY is sampled in this T-state: T2 of a read, a write or an acknowledge, and every wait state.</summary>
    public bool SamplesReady => _now.SamplesReady;

    /// <summary>The data bus is taken in this T-state: T3 of a fetch, a read or an acknowledge.</summary>
    public bool SamplesData => _now.SamplesData;

    /// <summary>ALE pulses in this T-state: T1 of every machine cycle but a bus idle one.</summary>
    public bool AddressLatchEnable => _now.Ale;

    /// <summary>The bus is let go in this T-state: HOLD granted (HLDA high a T-state already), halted, or held in reset.</summary>
    public bool BusFloated => _now.Floated || _now.Kind is I8085CycleKind.Hold or I8085CycleKind.Halt or I8085CycleKind.Reset;

    /// <summary>HLDA: HOLD has been granted. High from the T-state after HOLD was seen until the T-state it is seen low.</summary>
    public bool HoldAcknowledged => _hldaFlipFlop;

    /// <summary>The T-state on offer is the first of an instruction, of an interrupt response, or of one HALT state.</summary>
    public bool FirstCycle => _now.First;

    /// <summary>T-states completed since power-on, wait, hold and halt states included.</summary>
    public long TStates { get; private set; }

    /// <summary>Wait states READY has asked for.</summary>
    public long WaitStates { get; private set; }

    /// <summary>Hold states the bus has been let go for.</summary>
    public long HoldStates { get; private set; }

    /// <summary>Instructions started since power-on, those an INTR acknowledge put on the bus included.</summary>
    public long Instructions { get; private set; }

    /// <summary>The address of the instruction executing (or last fetched).</summary>
    public ushort InstructionAddress { get; private set; }

    /// <summary>The opcode executing.</summary>
    public byte Opcode { get; private set; }

    /// <summary>True after HLT until an interrupt or reset.</summary>
    public bool Halted { get; private set; }

    /// <summary>The last interrupt taken: "TRAP", "RST 7.5", "RST 6.5", "RST 5.5", "INTR", "RESET", or empty.</summary>
    public string LastInterrupt { get; private set; } = string.Empty;

    /// <summary>The bytes the last INTR acknowledge read off the bus: an RST, or a CALL and its address.</summary>
    public IReadOnlyList<byte> LastAcknowledgeBytes { get; private set; } = [];

    // ---- the pins it samples ------------------------------------------------

    /// <summary>INTR is high.</summary>
    public bool Intr { get; set; }

    /// <summary>RST 5.5 is high (a level).</summary>
    public bool Rst55 { get; set; }

    /// <summary>RST 6.5 is high (a level).</summary>
    public bool Rst65 { get; set; }

    /// <summary>RST 7.5 is high: a rising edge sets its flip-flop.</summary>
    public bool Rst75 { get; set; }

    /// <summary>TRAP is high: a rising edge, and the level until it is taken.</summary>
    public bool Trap { get; set; }

    /// <summary>HOLD is high.</summary>
    public bool Hold { get; set; }

    /// <summary>READY was low when it was sampled in the T-state on offer.</summary>
    public bool ReadyLow { get; set; }

    /// <summary>The SID pin is high: RIM reads it into bit 7.</summary>
    public bool Sid { get; set; }

    private bool _resetHeld;

    /// <summary>Holds the processor in reset (RESET IN low). Letting go starts it at $0000.</summary>
    public bool ResetAsserted
    {
        get => _resetHeld;
        set
        {
            if (value && !_resetHeld)
            {
                _resetHeld = true;
                Halted = false;
                _hldaFlipFlop = false;
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

        SenseEdges();

        // The interrupts as sampled in this T-state; the decision at a boundary uses the one before the last.
        _previousSample = _sample;
        _sample = new Sample(_trapLatch && Trap, Rst75Pending, Rst65, Rst55, Intr);

        if (_hldaFlipFlop) _hldaAge++;
        if (_now.SamplesHold && Hold && !_hldaFlipFlop && !_resetHeld)
        {
            _hldaFlipFlop = true;
            _hldaAge = 0;
        }

        if (_now.Kind == I8085CycleKind.Hold) HoldStates++;
        _program.MoveNext();
    }

    /// <summary>
    /// Latches a rising edge on RST 7.5 or TRAP, from <see cref="Rst75"/> and <see cref="Trap"/> as
    /// they stand: done as each T-state ends, and by the pins whenever either changes, so a pulse
    /// between two clocks is not missed.
    /// </summary>
    public void SenseEdges()
    {
        if (Rst75 && !_rst75Was) Rst75Pending = true;
        _rst75Was = Rst75;
        if (Trap && !_trapWas) _trapLatch = true;
        _trapWas = Trap;
    }

    /// <summary>
    /// Runs one whole instruction (or interrupt response, or HALT state) against a memory and an I/O
    /// space, for tests: returns the T-states it took. Each INTR acknowledge reads <paramref name="acknowledge"/>.
    /// </summary>
    public int Step(Func<ushort, byte> read, Action<ushort, byte> write, Func<byte, byte>? input = null,
        Action<byte, byte>? output = null, Func<byte>? acknowledge = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);

        var count = 0;
        do
        {
            byte value = 0;
            if (_now.State == I8085TState.T3 && _now.Kind == I8085CycleKind.MemoryWrite) write(Address, DataOut);
            if (_now.State == I8085TState.T3 && _now.Kind == I8085CycleKind.IoWrite) output?.Invoke((byte)Address, DataOut);
            if (_now.SamplesData)
            {
                value = _now.Kind switch
                {
                    I8085CycleKind.IoRead => input?.Invoke((byte)Address) ?? 0xFF,
                    I8085CycleKind.InterruptAcknowledge => acknowledge?.Invoke() ?? 0xFF,
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
    /// registers as they are now, out of reset, halt and hold: what a test that sets up a state
    /// wants. <paramref name="eiLast"/> is whether the instruction before was EI.
    /// </summary>
    public void Load(ushort pc, bool eiLast = false)
    {
        PC = pc;
        EiLast = eiLast;
        Halted = false;
        _resetHeld = false;
        _hldaFlipFlop = false;
        _acknowledging = false;
        _sodPending = null;
        _sample = _previousSample = default;
        _skipHousekeeping = true;
        _program = Program().GetEnumerator();
        _program.MoveNext();
    }

    // ---- posting T-states -------------------------------------------------------

    private bool Post(I8085CycleKind kind, I8085TState state, int address, byte data = 0, bool ready = false, bool samplesData = false,
        bool samplesHold = false, bool ale = false, bool floated = false)
    {
        var first = _firstNext && kind != I8085CycleKind.Hold;
        if (first) _firstNext = false;
        _now = new Posting
        {
            Kind = kind,
            State = state,
            Address = (ushort)address,
            DataOut = data,
            SamplesReady = ready,
            SamplesData = samplesData,
            SamplesHold = samplesHold,
            Ale = ale,
            Floated = floated,
            First = first,
        };
        return true;
    }

    /// <summary>The bus floats from the T-state after HLDA went high.</summary>
    private bool Granted => _hldaFlipFlop && _hldaAge >= 1;

    /// <summary>
    /// Before a machine cycle: if HOLD was granted, hold states until it is seen low — the bus
    /// floating and HLDA high — then the cycle.
    /// </summary>
    private IEnumerable<bool> HoldBefore()
    {
        while (_hldaFlipFlop)
        {
            yield return Post(I8085CycleKind.Hold, I8085TState.Thold, _now.Address, samplesHold: true, floated: true);
            if (!Hold) _hldaFlipFlop = false;
        }
    }

    /// <summary>T1, T2, the wait states READY asks for, and T3, of a cycle that moves a byte.</summary>
    private IEnumerable<bool> Transfer(I8085CycleKind kind, int address, byte data, bool reads)
    {
        foreach (var c in HoldBefore()) yield return c;
        yield return Post(kind, I8085TState.T1, address, data, ale: true);

        // A SIM's SOD reaches the pin in T2 of the next opcode fetch.
        if (kind == I8085CycleKind.OpcodeFetch && _sodPending is { } sod)
        {
            Sod = sod != 0;
            _sodPending = null;
        }

        yield return Post(kind, I8085TState.T2, address, data, ready: true, samplesHold: true);
        while (ReadyLow)
        {
            WaitStates++;
            yield return Post(kind, I8085TState.Tw, address, data, ready: true, samplesHold: true);
        }

        yield return Post(kind, I8085TState.T3, address, data, samplesData: reads);
    }

    /// <summary>
    /// An opcode fetch (or, with <paramref name="kind"/> the acknowledge, an INTR's first /INTA): T1
    /// to T3, the opcode in <see cref="_data"/> by T4, and T4 — sampling HOLD if T5 and T6 follow.
    /// </summary>
    private IEnumerable<bool> FetchCycle(I8085CycleKind kind, int address)
    {
        foreach (var c in Transfer(kind, address, 0, reads: true)) yield return c;
        var opcode = _data;
        yield return Post(kind, I8085TState.T4, address, samplesHold: SixStateFetch[opcode], floated: Granted);
        _data = opcode;
    }

    /// <summary>T5 and T6 of a six-state opcode fetch: inside the processor, the bus floating if HOLD has been granted.</summary>
    private IEnumerable<bool> Extend()
    {
        var kind = _now.Kind;
        var address = _now.Address;
        yield return Post(kind, I8085TState.T5, address, floated: Granted);
        yield return Post(kind, I8085TState.T6, address, floated: Granted);
    }

    private IEnumerable<bool> ReadCycle(int address) => Transfer(I8085CycleKind.MemoryRead, address, 0, reads: true);

    private IEnumerable<bool> WriteCycle(int address, byte value) => Transfer(I8085CycleKind.MemoryWrite, address, value, reads: false);

    /// <summary>An I/O read: the port on AD0-AD7 and A8-A15 both.</summary>
    private IEnumerable<bool> InputCycle(byte port) => Transfer(I8085CycleKind.IoRead, (port << 8) | port, 0, reads: true);

    private IEnumerable<bool> OutputCycle(byte port, byte value) => Transfer(I8085CycleKind.IoWrite, (port << 8) | port, value, reads: false);

    /// <summary>A bus idle machine cycle: three T-states with no ALE, no strobe, and READY ignored.</summary>
    private IEnumerable<bool> BusIdle()
    {
        foreach (var c in HoldBefore()) yield return c;
        var address = _now.Address;
        yield return Post(I8085CycleKind.BusIdle, I8085TState.T1, address);
        yield return Post(I8085CycleKind.BusIdle, I8085TState.T2, address, samplesHold: true);
        yield return Post(I8085CycleKind.BusIdle, I8085TState.T3, address);
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
        A = 0;
        _f = 0;
        B = C = D = E = H = L = 0;
        SP = 0;
        ResetState();
    }

    /// <summary>What RESET IN clears and sets: PC, INTE, the RST 7.5 and TRAP latches and SOD cleared, the three masks set.</summary>
    private void ResetState()
    {
        PC = 0;
        InterruptsEnabled = false;
        Masks = 7;
        Rst75Pending = false;
        _trapLatch = false;
        Sod = false;
        _sodPending = null;
        _hldaFlipFlop = false;
        _acknowledging = false;
        _trapRimPending = false;
        EiLast = _eiNow = false;
        Halted = false;
    }

    /// <summary>RESET IN low: the bus floats until it is let go, then PC and the interrupt system are cleared and the program starts.</summary>
    private IEnumerable<bool> ResetProgram()
    {
        do
        {
            yield return Post(I8085CycleKind.Reset, I8085TState.Treset, _now.Address, floated: true);
        }
        while (_resetHeld);

        ResetState();
        LastInterrupt = "RESET";
        _skipHousekeeping = true;
        foreach (var c in Program()) yield return c;
    }

    /// <summary>The interrupt the sample from the next-to-last T-state calls for, by priority, or null.</summary>
    private (string Name, ushort Address)? ValidInterrupt()
    {
        var s = _previousSample;
        if (s.Trap && _trapLatch) return ("TRAP", TrapAddress);
        if (!InterruptsEnabled || EiLast) return null;
        if (s.Rst75 && Rst75Pending && (Masks & 4) == 0) return ("RST 7.5", Rst75Address);
        if (s.Rst65 && (Masks & 2) == 0) return ("RST 6.5", Rst65Address);
        if (s.Rst55 && (Masks & 1) == 0) return ("RST 5.5", Rst55Address);
        if (s.Intr) return ("INTR", 0);
        return null;
    }

    private IEnumerable<bool> Program()
    {
        while (true)
        {
            // The boundary: what the instruction just finished leaves behind.
            if (!_skipHousekeeping) EiLast = _eiNow;
            _skipHousekeeping = false;
            _eiNow = false;
            _acknowledging = false;
            _firstNext = true;

            if (ValidInterrupt() is { } interrupt)
            {
                foreach (var c in TakeInterrupt(interrupt.Name, interrupt.Address)) yield return c;
                continue;
            }

            if (Halted)
            {
                // One HALT state; HOLD lets go of the bus for as long as it lasts.
                foreach (var c in HoldBefore()) yield return c;
                yield return Post(I8085CycleKind.Halt, I8085TState.Thalt, _now.Address, samplesHold: true, floated: true);
                continue;
            }

            InstructionAddress = PC;
            Instructions++;
            foreach (var c in FetchCycle(I8085CycleKind.OpcodeFetch, PC)) yield return c;
            PC++;
            foreach (var c in Execute(_data)) yield return c;
        }
    }

    /// <summary>
    /// An interrupt: TRAP and the RSTs a six-state acknowledge with no strobe and a call to their
    /// restart address; INTR the instruction the device puts on the bus with /INTA, run with PC held.
    /// </summary>
    private IEnumerable<bool> TakeInterrupt(string name, ushort address)
    {
        Halted = false;
        LastInterrupt = name;
        InstructionAddress = PC;

        if (name == "TRAP")
        {
            _trapLatch = false;
            _ieBeforeTrap = InterruptsEnabled;
            _trapRimPending = true;
        }
        else
        {
            _trapRimPending = false;
        }

        InterruptsEnabled = false;

        if (name != "INTR")
        {
            if (name == "RST 7.5") Rst75Pending = false;
            foreach (var c in HoldBefore()) yield return c;
            yield return Post(I8085CycleKind.RestartAcknowledge, I8085TState.T1, PC, ale: true);
            yield return Post(I8085CycleKind.RestartAcknowledge, I8085TState.T2, PC, samplesHold: true);
            yield return Post(I8085CycleKind.RestartAcknowledge, I8085TState.T3, PC);
            yield return Post(I8085CycleKind.RestartAcknowledge, I8085TState.T4, PC, samplesHold: true, floated: Granted);
            yield return Post(I8085CycleKind.RestartAcknowledge, I8085TState.T5, PC, floated: Granted);
            yield return Post(I8085CycleKind.RestartAcknowledge, I8085TState.T6, PC, floated: Granted);
            foreach (var c in Push(PC)) yield return c;
            PC = address;
            yield break;
        }

        // INTR: the instruction comes off the bus, its operand bytes in further acknowledges.
        _acknowledging = true;
        _acknowledged.Clear();
        Instructions++;
        foreach (var c in FetchCycle(I8085CycleKind.InterruptAcknowledge, PC)) yield return c;
        _acknowledged.Add(_data);
        LastAcknowledgeBytes = [.. _acknowledged];
        foreach (var c in Execute(_data)) yield return c;
        LastAcknowledgeBytes = [.. _acknowledged];
        _acknowledging = false;
    }

    private readonly List<byte> _acknowledged = [];
}
