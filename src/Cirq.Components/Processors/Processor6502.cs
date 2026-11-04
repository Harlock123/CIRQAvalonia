using System.Globalization;
using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Processors;

/// <summary>One bus cycle a processor performed: when it ended, where, what, which way.</summary>
/// <param name="Time">The PHI2 falling edge that ended it, in seconds.</param>
/// <param name="Address">The address on A0-A15.</param>
/// <param name="Data">The byte read or written.</param>
/// <param name="IsRead">True for a read.</param>
/// <param name="Sync">True for an opcode fetch.</param>
public readonly record struct BusCycle(double Time, ushort Address, byte Data, bool IsRead, bool Sync);

/// <summary>Which 40-pin package a <see cref="Processor6502"/> comes in: the pins differ, the core does not.</summary>
public enum ProcessorPackage
{
    /// <summary>The WDC W65C02S: VPB, MLB, BE, SYNC, SOB.</summary>
    W65C02S,

    /// <summary>The MOS 6502: Φ0 in, Φ1 and Φ2 out, SYNC and SO.</summary>
    Mos6502,

    /// <summary>The MOS 6510 and 8500: the 6502 with AEC and a six-bit I/O port, and no SYNC, SO or Φ1.</summary>
    Mos6510,
}

/// <summary>
/// A 6502 as a 40-pin package on the schematic, driven by its clock pin one bus cycle at a time.
/// <para>
/// <b>The model is cycle-level.</b> The instruction set runs in <see cref="Mos6502Core"/>, which
/// does exactly what the silicon does on each clock — including the dummy reads and writes — and
/// this part puts each of those cycles on its pins with the datasheet's timing relative to PHI2. A
/// cycle starts when PHI2 falls: the address, R/W and SYNC change a <see cref="AddressSetupTime"/>
/// later. On a write the data goes out <see cref="WriteDataDelay"/> after PHI2 rises and is held
/// until <see cref="WriteDataHold"/> after it falls. On a read the data bus is sampled at the
/// instant PHI2 falls, which is the end of the cycle — so a memory too slow for the clock really
/// does hand the processor the wrong byte. The internal gate delays inside a cycle are not
/// modelled; only the pins are.
/// </para>
/// <para>
/// <b>Control inputs.</b> RESB low holds the processor; letting it go runs the seven-cycle reset
/// sequence and fetches the vector at $FFFC. Power-on runs it too, as though the reset line had
/// been pulsed, so a circuit starts without a reset button being pressed. IRQB is a level and NMIB
/// an edge, both sampled as PHI2 falls. RDY low stops the processor at the end of the cycle (on the
/// NMOS part only during a read cycle, as the original ignored it on writes), holding the bus as it
/// is. SOB falling sets V. On the 65C02, BE low lets go of the address bus, R/W and the data bus.
/// The control pins are not pulled up inside, so tie the ones you are not using high.
/// </para>
/// </summary>
public abstract partial class Processor6502 : DigitalIc, IRunState
{
    /// <summary>How many bus cycles the bus log keeps, newest last.</summary>
    public const int BusLogCapacity = 1 << 16;

    private readonly Terminal[] _address = new Terminal[16];
    private readonly Terminal[] _data = new Terminal[8];
    private readonly BusCycle[] _log = new BusCycle[BusLogCapacity];
    private int _logStart;
    private int _logCount;

    private readonly int _rwIndex;
    private readonly int _syncIndex = -1;
    private readonly Terminal[] _port = [];
    private readonly List<(int Index, Func<LogicState> Level)> _statusOutputs = [];
    private readonly List<(int Index, bool Inverted)> _clockOutputs = [];

    private LogicState _lastClock = LogicState.Unknown;
    private bool _lastSo = true;
    private bool _driving;

    protected Processor6502(Cpu6502Variant variant)
        : this(variant, variant == Cpu6502Variant.Wdc65C02 ? ProcessorPackage.W65C02S : ProcessorPackage.Mos6502)
    {
    }

    private protected Processor6502(Cpu6502Variant variant, ProcessorPackage package) : base(40)
    {
        Variant = variant;
        Package = package;
        Core = new Mos6502Core(variant);
        PropagationDelay = 30e-9;

        var pins = new Terminal[41];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 40));

        if (package == ProcessorPackage.Mos6510)
        {
            for (var i = 0; i < 14; i++) _address[i] = Pin(7 + i, $"A{i}", TerminalType.Output);
            for (var i = 14; i < 16; i++) _address[i] = Pin(8 + i, $"A{i}", TerminalType.Output);
            for (var i = 0; i < 8; i++) _data[i] = Pin(37 - i, $"D{i}", TerminalType.Bidirectional);
        }
        else
        {
            for (var i = 0; i < 12; i++) _address[i] = Pin(9 + i, $"A{i}", TerminalType.Output);
            for (var i = 12; i < 16; i++) _address[i] = Pin(10 + i, $"A{i}", TerminalType.Output);
            for (var i = 0; i < 8; i++) _data[i] = Pin(33 - i, $"D{i}", TerminalType.Bidirectional);
        }

        List<Terminal> inputs = [];
        List<Terminal> outputs = [.. _address, .. _data];

        Terminal Output(int number, string name)
        {
            var t = Pin(number, name, TerminalType.Output);
            outputs.Add(t);
            return t;
        }

        Terminal Input(int number, string name)
        {
            var t = Pin(number, name, TerminalType.Input);
            inputs.Add(t);
            return t;
        }

        if (package == ProcessorPackage.Mos6510)
        {
            // The MOS 6510: Φ0 in on 1, AEC on 5, the address bus moved up to pins 7-23 round the
            // supply pins, the port P5-P0 on 24-29 and the data bus on 30-37. No SYNC, SO or Φ1.
            Clock = Input(1, "PHI0");
            Ready = Input(2, "RDY");
            Irq = Input(3, "IRQ");
            Nmi = Input(4, "NMI");
            BusEnable = Input(5, "AEC");
            Vcc = Pin(6, "VCC", TerminalType.Power);
            Gnd = Pin(21, "VSS", TerminalType.Ground);
            Output(38, "RW");
            _rwIndex = outputs.Count - 1;
            Output(39, "PHI2");
            _clockOutputs.Add((outputs.Count - 1, false));
            Reset = Input(40, "RES");

            _port = new Terminal[6];
            PortOutputIndex = outputs.Count;
            for (var i = 0; i < 6; i++)
            {
                _port[i] = Pin(29 - i, $"P{i}", TerminalType.Bidirectional);
                outputs.Add(_port[i]);
                inputs.Add(_port[i]);
            }
        }
        else if (variant == Cpu6502Variant.Wdc65C02)
        {
            // The W65C02S: VPB on pin 1 where the NMOS part had a ground, BE on 36, PHI2 in on 37.
            Output(1, "VPB");
            _statusOutputs.Add((outputs.Count - 1, () => Core.VectorPull ? LogicState.Low : LogicState.High));
            Ready = Input(2, "RDY");
            Output(3, "PHI1O");
            _clockOutputs.Add((outputs.Count - 1, true));
            Irq = Input(4, "IRQB");
            Output(5, "MLB");
            _statusOutputs.Add((outputs.Count - 1, () => Core.MemoryLock ? LogicState.Low : LogicState.High));
            Nmi = Input(6, "NMIB");
            Output(7, "SYNC");
            _syncIndex = outputs.Count - 1;
            Vcc = Pin(8, "VDD", TerminalType.Power);
            Gnd = Pin(21, "VSS", TerminalType.Ground);
            Output(34, "RWB");
            _rwIndex = outputs.Count - 1;
            BusEnable = Input(36, "BE");
            Clock = Input(37, "PHI2");
            SetOverflow = Input(38, "SOB");
            Output(39, "PHI2O");
            _clockOutputs.Add((outputs.Count - 1, false));
            Reset = Input(40, "RESB");
        }
        else
        {
            // The MOS 6502: grounds on 1 and 21, clock in on 37 (Φ0) and out on 3 (Φ1) and 39 (Φ2).
            Gnd = Pin(21, "VSS", TerminalType.Ground);
            Pin(1, "VSS1", TerminalType.Ground);
            Ready = Input(2, "RDY");
            Output(3, "PHI1");
            _clockOutputs.Add((outputs.Count - 1, true));
            Irq = Input(4, "IRQ");
            Nmi = Input(6, "NMI");
            Output(7, "SYNC");
            _syncIndex = outputs.Count - 1;
            Vcc = Pin(8, "VCC", TerminalType.Power);
            Output(34, "RW");
            _rwIndex = outputs.Count - 1;
            Clock = Input(37, "PHI0");
            SetOverflow = Input(38, "SO");
            Output(39, "PHI2");
            _clockOutputs.Add((outputs.Count - 1, false));
            Reset = Input(40, "RES");
        }

        Terminals = [.. pins.Skip(1).Where(t => t is not null)];
        ConfigurePins([.. inputs, .. _data], outputs);
    }

    public Cpu6502Variant Variant { get; }

    /// <summary>Which package: the 65C02's, the 6502's or the 6510's pinout.</summary>
    public ProcessorPackage Package { get; }

    /// <summary>The 6510's I/O port, P0-P5. Empty on the other packages.</summary>
    public IReadOnlyList<Terminal> Port => _port;

    /// <summary>Where P0 is among the output pins, on a part with a port.</summary>
    private protected int PortOutputIndex { get; } = -1;

    /// <summary>The processor inside: registers, flags and the cycle in progress.</summary>
    public Mos6502Core Core { get; private set; }

    /// <summary>A0-A15.</summary>
    public IReadOnlyList<Terminal> Address => _address;

    /// <summary>D0-D7.</summary>
    public IReadOnlyList<Terminal> Data => _data;

    /// <summary>The clock input: PHI2 on the 65C02, Φ0 on the NMOS part. A cycle ends as it falls.</summary>
    public Terminal Clock { get; }

    /// <summary>Reset, active low.</summary>
    public Terminal Reset { get; }

    /// <summary>Interrupt request, active low and level-sensitive.</summary>
    public Terminal Irq { get; }

    /// <summary>Non-maskable interrupt, active low, taken on the falling edge.</summary>
    public Terminal Nmi { get; }

    /// <summary>Ready: low stops the processor at the end of the cycle.</summary>
    public Terminal Ready { get; }

    /// <summary>Set overflow, active low: a falling edge sets V. Null on the 6510, which has no SO pin.</summary>
    public Terminal? SetOverflow { get; }

    /// <summary>
    /// The bus enable — the 65C02's BE or the 6510's AEC: low releases the address and data buses and
    /// R/W. Null on the 6502, which has neither.
    /// </summary>
    public Terminal? BusEnable { get; }

    /// <summary>R/W: high to read, low to write.</summary>
    public Terminal ReadWrite => OutputTerminals[_rwIndex];

    /// <summary>SYNC: high during an opcode fetch. Null on the 6510, which has no SYNC pin.</summary>
    public Terminal? SyncPin => _syncIndex < 0 ? null : OutputTerminals[_syncIndex];

    /// <summary>After this much of PHI2 falling, the address, R/W and SYNC have changed.</summary>
    public double AddressSetupTime => PropagationDelay;

    /// <summary>After PHI2 rises, a write's data is on the bus.</summary>
    public virtual double WriteDataDelay => 25e-9;

    /// <summary>After PHI2 falls, a write's data is still held this long.</summary>
    public virtual double WriteDataHold => 30e-9;

    public override bool LabelsPins => true;

    public override string PinLabel(Terminal pin) => pin.Name switch
    {
        "IRQ" or "NMI" or "RES" or "SO" => "/" + pin.Name,
        "RW" => "R/W",
        "RWB" => "R/WB",
        _ => pin.Name,
    };

    public override string ValueLabel => Core.IsStopped
        ? $"stopped at ${Core.PC:X4}"
        : Core.IsWaiting
            ? $"waiting at ${Core.PC:X4}"
            : $"PC ${Core.InstructionAddress:X4} A ${Core.A:X2}";

    /// <summary>The bus cycles performed, oldest first, up to <see cref="BusLogCapacity"/> of the most recent.</summary>
    public IReadOnlyList<BusCycle> BusLog
    {
        get
        {
            var list = new BusCycle[_logCount];
            for (var i = 0; i < _logCount; i++) list[i] = _log[(_logStart + i) % BusLogCapacity];
            return list;
        }
    }

    /// <summary>Read cycles that found a data line at no logic level: two drivers fighting, or nobody driving.</summary>
    public int UndefinedReads { get; private set; }

    /// <summary>The address of the first read that found the data bus undefined, or −1.</summary>
    public int FirstUndefinedRead { get; private set; } = -1;

    /// <summary>What is wrong with how this part is being run, if anything.</summary>
    public IReadOnlyList<string> Violations => UndefinedReads > 0
        ?
        [
            $"{UndefinedReads} read(s) found a data line at no logic level, first at ${FirstUndefinedRead:X4} — " +
            "two outputs fighting on the data bus, or nothing selected there at all",
        ]
        : [];

    /// <summary>The latest instruction fetched, disassembled from the bus.</summary>
    public string CurrentInstruction
    {
        get
        {
            for (var i = _logCount - 1; i >= 0; i--)
            {
                var cycle = _log[(_logStart + i) % BusLogCapacity];
                if (!cycle.Sync) continue;

                // The latest fetch whose operands have all been read: the one just fetched may not
                // have got that far yet.
                var text = Cpu6502BusDecoder.Instruction(Variant, [.. Enumerable.Range(i, Math.Min(8, _logCount - i)).Select(k => _log[(_logStart + k) % BusLogCapacity])]);
                if (!text.EndsWith(" ?", StringComparison.Ordinal)) return text;
            }

            return string.Empty;
        }
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var clock = context.ReadInput(Clock, Levels);
        var previous = _lastClock;
        _lastClock = clock;

        var busOn = BusEnable is null || !context.ReadInput(BusEnable, Levels).IsLow();

        // SO is an edge, and so is NMI, but NMI's edge is the core's business.
        var so = SetOverflow is null || !context.ReadInput(SetOverflow, Levels).IsLow();
        if (_lastSo && !so) Core.P |= Mos6502Core.FlagV;
        _lastSo = so;

        if (context.IsInitializing)
        {
            Present(context, busOn, 0);
            DriveClocks(context, clock);
            return;
        }

        if (clock == previous || !clock.IsDriven() || !previous.IsDriven())
        {
            // No edge: only BE can change what is on the pins between edges.
            if (BusEnable is not null) ApplyBusEnable(context, busOn);
            return;
        }

        DriveClocks(context, clock);

        if (clock.IsHigh())
        {
            // PHI2 has risen: a write cycle puts its data on the bus now.
            if (!Core.IsRead && busOn)
            {
                var value = Core.DataOut;
                for (var i = 0; i < 8; i++)
                    Request(context, 16 + i, (value & (1 << i)) != 0 ? LogicState.High : LogicState.Low, WriteDataDelay);
                _driving = true;
            }

            return;
        }

        // PHI2 has fallen: the end of a cycle.
        Core.ResetAsserted = context.ReadInput(Reset, Levels).IsLow();
        Core.IrqAsserted = context.ReadInput(Irq, Levels).IsLow();
        Core.NmiAsserted = context.ReadInput(Nmi, Levels).IsLow();

        var ready = !context.ReadInput(Ready, Levels).IsLow();
        var holds = !ready && (Variant == Cpu6502Variant.Wdc65C02 || Core.IsRead);

        if (Core.ResetAsserted) ResetPort(context);
        EndCycle(context);

        if (!holds)
        {
            // The 6510's port is inside the processor: a read of $0000 or $0001 takes the port's
            // register whatever the bus says, and a write lands in it as well as going out on the bus.
            byte value;
            if (Core.IsRead) value = ReadInternal(context, Core.Address) ?? SampleData(context);
            else
            {
                value = Core.DataOut;
                WriteInternal(context, Core.Address, value);
            }

            Record(new BusCycle(context.Time, Core.Address, value, Core.IsRead, Core.Sync));
            var instructionsBefore = Core.Instructions;
            Core.Complete(value);
            if (Core.Instructions != instructionsBefore || Core.Sync) NotifyValueChanged();
        }

        // The write's data is held a moment past the edge, then the bus is let go of — unless RDY is
        // holding a write cycle, when it stays on the bus until the cycle can finish.
        if (_driving && !(holds && !Core.IsRead))
        {
            for (var i = 0; i < 8; i++) Request(context, 16 + i, LogicState.HighImpedance, WriteDataHold);
            _driving = false;
        }

        Present(context, busOn, AddressSetupTime);
    }

    /// <summary>The byte on the data bus at the end of a read, and a note of any line at no level.</summary>
    private byte SampleData(IDigitalContext context)
    {
        var value = 0;
        var undefined = false;

        for (var i = 0; i < 8; i++)
        {
            var level = context.ReadInput(_data[i], Levels);
            if (level.IsHigh()) value |= 1 << i;
            else if (!level.IsLow()) undefined = true;
        }

        if (undefined)
        {
            if (UndefinedReads == 0) FirstUndefinedRead = Core.Address;
            UndefinedReads++;
        }

        return (byte)value;
    }

    /// <summary>Puts the cycle on offer onto the address pins, R/W and the status outputs.</summary>
    private void Present(IDigitalContext context, bool busOn, double delay)
    {
        var address = Core.Address;

        for (var i = 0; i < 16; i++)
            Request(context, i, !busOn ? LogicState.HighImpedance : (address & (1 << i)) != 0 ? LogicState.High : LogicState.Low, delay);

        Request(context, _rwIndex, !busOn ? LogicState.HighImpedance : Core.IsRead ? LogicState.High : LogicState.Low, delay);
        if (_syncIndex >= 0) Request(context, _syncIndex, Core.Sync ? LogicState.High : LogicState.Low, delay);
        if (context.IsInitializing) PresentPort(context);

        foreach (var (index, level) in _statusOutputs) Request(context, index, level(), delay);

        if (context.IsInitializing || !_driving)
        {
            for (var i = 0; i < 8; i++) Request(context, 16 + i, LogicState.HighImpedance, delay);
        }
    }

    /// <summary>
    /// The byte a read of <paramref name="address"/> takes from inside the processor rather than from
    /// the bus — the 6510's port registers — or null to read the bus.
    /// </summary>
    private protected virtual byte? ReadInternal(IDigitalContext context, ushort address) => null;

    /// <summary>A write the processor also keeps for itself (the 6510's port registers). It still goes out on the bus.</summary>
    private protected virtual void WriteInternal(IDigitalContext context, ushort address, byte value)
    {
    }

    /// <summary>Drives a port's pins as its registers say, at power-on.</summary>
    private protected virtual void PresentPort(IDigitalContext context)
    {
    }

    /// <summary>Called as every cycle ends, before it is completed: where a port looks at its pins.</summary>
    private protected virtual void EndCycle(IDigitalContext context)
    {
    }

    /// <summary>What /RES does to a port: every pin an input.</summary>
    private protected virtual void ResetPort(IDigitalContext? context)
    {
    }

    /// <summary>A port's registers, for a run to hand back; null without one.</summary>
    private protected virtual object? CapturePort() => null;

    private protected virtual void AdoptPort(object? state)
    {
    }

    private void ApplyBusEnable(IDigitalContext context, bool busOn)
    {
        if (busOn == (GetOutputState(0) != LogicState.HighImpedance)) return;
        Present(context, busOn, AddressSetupTime);

        if (!busOn)
        {
            for (var i = 0; i < 8; i++) Request(context, 16 + i, LogicState.HighImpedance, AddressSetupTime);
            _driving = false;
        }
    }

    private void DriveClocks(IDigitalContext context, LogicState clock)
    {
        foreach (var (index, inverted) in _clockOutputs)
            Request(context, index, inverted ? clock.Invert() : clock, DelayFor(context) / 3);
    }

    private void Record(BusCycle cycle)
    {
        if (_logCount < BusLogCapacity)
        {
            _log[(_logStart + _logCount) % BusLogCapacity] = cycle;
            _logCount++;
        }
        else
        {
            _log[_logStart] = cycle;
            _logStart = (_logStart + 1) % BusLogCapacity;
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        Core = new Mos6502Core(Variant);
        _lastClock = LogicState.Unknown;
        _lastSo = true;
        _driving = false;
        _logStart = 0;
        _logCount = 0;
        UndefinedReads = 0;
        FirstUndefinedRead = -1;
        ResetPort(null);
    }

    private sealed record RunState(Mos6502Core Core, BusCycle[] Log, int Start, int Count, int UndefinedReads, int FirstUndefinedRead, object? Port);

    public object CaptureRunState() =>
        new RunState(Core, (BusCycle[])_log.Clone(), _logStart, _logCount, UndefinedReads, FirstUndefinedRead, CapturePort());

    public void AdoptRunState(object state)
    {
        if (state is not RunState run) return;

        Core = run.Core;
        Array.Copy(run.Log, _log, BusLogCapacity);
        _logStart = run.Start;
        _logCount = run.Count;
        UndefinedReads = run.UndefinedReads;
        FirstUndefinedRead = run.FirstUndefinedRead;
        AdoptPort(run.Port);
        NotifyValueChanged();
    }

    /// <summary>The registers as a line: <c>PC=8012 A=48 X=00 Y=00 S=FD P=nv-bdIzc</c>.</summary>
    public string Registers =>
        string.Create(CultureInfo.InvariantCulture,
            $"PC={Core.PC:X4} A={Core.A:X2} X={Core.X:X2} Y={Core.Y:X2} S={Core.S:X2} P={Flags(Core.P)}");

    /// <summary>The status register as letters, capital when set: <c>NV-BDIZC</c>.</summary>
    public static string Flags(byte p)
    {
        const string names = "NV-BDIZC";
        var chars = new char[8];
        for (var i = 0; i < 8; i++)
        {
            var set = (p & (0x80 >> i)) != 0;
            chars[i] = i == 2 ? '-' : set ? names[i] : char.ToLowerInvariant(names[i]);
        }

        return new string(chars);
    }
}

/// <summary>
/// The WDC W65C02S — the CMOS 6502 in Ben Eater's computer, and in every hobby 6502 build since —
/// on its DIP-40 pinout.
/// <para>
/// Everything the NMOS part does, with its bugs fixed and its gaps filled: the (zp) mode, BRA, PHX,
/// PHY, PLX, PLY, STZ, TSB, TRB, INC A and DEC A, BIT immediate and indexed, JMP (abs,X), the bit
/// instructions RMB, SMB, BBR and BBS, and WAI and STP. JMP ($xxFF) reads its pointer correctly,
/// decimal mode sets N and Z properly (at the cost of a cycle), BRK and interrupts clear D, and
/// every unused opcode is a NOP of a known length rather than something undocumented.
/// </para>
/// <para>
/// Pinout: 1=VPB, 2=RDY, 3=PHI1O, 4=IRQB, 5=MLB, 6=NMIB, 7=SYNC, 8=VDD, 9-20=A0-A11, 21=VSS,
/// 22-25=A12-A15, 26-33=D7-D0, 34=RWB, 35=NC, 36=BE, 37=PHI2, 38=SOB, 39=PHI2O, 40=RESB. RDY is an
/// input here: the real pin is bidirectional and is pulled low during WAI, which is not modelled.
/// </para>
/// </summary>
public sealed class Cpu65C02() : Processor6502(Cpu6502Variant.Wdc65C02)
{
    public override string PartNumber => "W65C02S";
}

/// <summary>
/// The MOS Technology 6502 — the NMOS original, on its DIP-40 pinout, with the undocumented opcodes
/// the silicon happens to have.
/// <para>
/// Every documented instruction with its exact cycle-by-cycle bus behaviour, dummy cycles and all,
/// the decimal mode's NMOS flag quirks, the JMP ($xxFF) page-wrap bug, and the undocumented opcodes
/// — SLO, RLA, SRE, RRA, SAX, LAX, DCP, ISC, ANC, ALR, ARR, SBX, the extra NOPs, the unstable ANE,
/// LXA, LAS, TAS, SHA, SHX and SHY as commonly characterised, and JAM, which locks it up until reset.
/// </para>
/// <para>
/// Pinout: 1=VSS, 2=RDY, 3=Φ1 out, 4=/IRQ, 5=NC, 6=/NMI, 7=SYNC, 8=VCC, 9-20=A0-A11, 21=VSS,
/// 22-25=A12-A15, 26-33=D7-D0, 34=R/W, 35-36=NC, 37=Φ0 in, 38=SO, 39=Φ2 out, 40=/RES.
/// </para>
/// </summary>
public sealed class Cpu6502() : Processor6502(Cpu6502Variant.Nmos6502)
{
    public override string PartNumber => "6502";
}
