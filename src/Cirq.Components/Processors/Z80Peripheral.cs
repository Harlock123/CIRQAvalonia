using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Processors;

/// <summary>
/// What the Z80 family's peripherals — the PIO, the CTC, the SIO and the DART — share: an I/O port
/// on the Z80's bus, and a place in the interrupt daisy chain.
/// <para>
/// <b>The bus.</b> There is no /WR pin: with /CE and /IORQ low and /M1 high, /RD low is a read and
/// /RD high a write. A read drives the data lines while it lasts; a write's byte is taken, and a
/// read's side effects done, as the access ends — /IORQ (or /CE) going high.
/// </para>
/// <para>
/// <b>The daisy chain.</b> Each chip's interrupt sources come in a fixed order of priority, and
/// the chips themselves in the order they are wired, IEO of one to IEI of the next, the first one's
/// IEI tied high. A chip pulls /INT (open drain) low for its highest pending source only while its
/// IEI is high and none of its own sources of that priority or above is under service. IEO is high
/// only while IEI is high and the chip has nothing under service — and, during an M1, nothing
/// pending either, so that by the time the acknowledge comes (/M1 with /IORQ, two wait states in)
/// exactly one chip in the chain has IEI high and something to say: it puts its vector on the data
/// bus, and that source goes under service. A chip watches the opcodes fetched for RETI, ED 4D: when
/// it sees the ED, a chip with an interrupt pending lets IEO up again for the next fetch, so the
/// chip under service further down sees IEI high when the 4D comes and ends its service — the
/// highest-priority source of the one chip whose IEI is high.
/// </para>
/// </summary>
public abstract class Z80Peripheral : DigitalIc
{
    private readonly Terminal[] _data = new Terminal[8];
    private readonly bool[] _inService;

    private bool _edSeen;
    private bool _fetching;
    private byte _fetched;
    private bool _m1Was;

    private bool _acknowledgeSeen;
    private int _acknowledging = -1;
    private byte _vector;

    private bool _accessWas;
    private bool _accessRead;
    private int _accessRegister;
    private byte _accessData;

    private protected Z80Peripheral(int pinCount, int sources) : base(pinCount)
    {
        _inService = new bool[sources];
    }

    /// <summary>The data lines, D0 to D7. Output indices 0 to 7.</summary>
    public IReadOnlyList<Terminal> Data => _data;

    /// <summary>/CE: low selects the chip.</summary>
    public Terminal ChipEnable { get; private protected set; } = null!;

    /// <summary>/M1: with /IORQ, the interrupt acknowledge; alone, an opcode fetch to watch for RETI.</summary>
    public Terminal M1 { get; private protected set; } = null!;

    public Terminal IoRequest { get; private protected set; } = null!;

    public Terminal ReadStrobe { get; private protected set; } = null!;

    /// <summary>IEI: high lets this chip interrupt. Tie it high on the first chip of the chain.</summary>
    public Terminal InterruptEnableIn { get; private protected set; } = null!;

    /// <summary>IEO: to the next chip's IEI.</summary>
    public Terminal InterruptEnableOut => OutputTerminals[IeoIndex];

    /// <summary>/INT: open drain, to the Z80's /INT with a pull-up.</summary>
    public Terminal Interrupt => OutputTerminals[IntIndex];

    /// <summary>The system clock, the Z80's CLK.</summary>
    public Terminal Clock { get; private protected set; } = null!;

    private protected abstract int IntIndex { get; }

    private protected abstract int IeoIndex { get; }

    /// <summary>The interrupt sources, highest priority first, as names for the state readouts.</summary>
    public abstract IReadOnlyList<string> InterruptSources { get; }

    /// <summary>Which sources are under service: acknowledged and not yet ended by a RETI.</summary>
    public IReadOnlyList<bool> InService => _inService;

    /// <summary>Which sources want an interrupt now, whether or not the chain lets them.</summary>
    public IReadOnlyList<bool> PendingSources => [.. Enumerable.Range(0, _inService.Length).Select(Pending)];

    /// <summary>The /INT line is being pulled low.</summary>
    public bool Interrupting { get; private set; }

    /// <summary>Vectors put on the bus in acknowledges, for tests and readouts.</summary>
    public int Acknowledges { get; private set; }

    /// <summary>RETIs that ended a service here.</summary>
    public int Returns { get; private set; }

    private protected void SetDataPin(int index, Terminal pin) => _data[index] = pin;

    /// <summary>Source <paramref name="source"/> wants an interrupt.</summary>
    private protected abstract bool Pending(int source);

    /// <summary>The byte the acknowledge puts on the bus for <paramref name="source"/>.</summary>
    private protected abstract byte VectorFor(int source);

    /// <summary>The source has been acknowledged and is now under service. The PIO and CTC forget the request; the SIO keeps its cause until it is dealt with.</summary>
    private protected virtual void Acknowledged(int source)
    {
    }

    /// <summary>Which register an access selects, from the select pins.</summary>
    private protected abstract int SelectedRegister(IDigitalContext context);

    /// <summary>What a read of <paramref name="register"/> gives, with no side effects.</summary>
    private protected abstract byte ReadRegister(int register);

    /// <summary>A read of <paramref name="register"/> has ended: its side effects.</summary>
    private protected virtual void AfterRead(int register)
    {
    }

    private protected abstract void WriteRegister(int register, byte value);

    /// <summary>A RETI ends the service of the highest-priority source under service; the SIO's "return from interrupt" command does the same.</summary>
    private protected void EndService()
    {
        for (var i = 0; i < _inService.Length; i++)
        {
            if (!_inService[i]) continue;
            _inService[i] = false;
            Returns++;
            NotifyValueChanged();
            return;
        }
    }

    /// <summary>The highest-priority source the chain would let interrupt now, or −1.</summary>
    private int Requesting()
    {
        for (var i = 0; i < _inService.Length; i++)
        {
            if (_inService[i]) return -1;
            if (Pending(i)) return i;
        }

        return -1;
    }

    private protected bool AnyInService => _inService.Any(s => s);

    /// <summary>The register an access in progress selects, or −1 between accesses.</summary>
    private protected int CurrentAccess => _accessWas ? _accessRegister : -1;

    /// <summary>The access in progress is a read.</summary>
    private protected bool CurrentAccessReads => _accessWas && _accessRead;

    /// <summary>
    /// The bus and the chain, every time the chip is evaluated: the opcode watch, the acknowledge,
    /// a register access, the data lines, /INT and IEO.
    /// </summary>
    private protected void EvaluateBus(IDigitalContext context)
    {
        var m1 = context.ReadInput(M1, Levels).IsLow();
        var iorq = context.ReadInput(IoRequest, Levels).IsLow();
        var rd = context.ReadInput(ReadStrobe, Levels).IsLow();
        var ce = context.ReadInput(ChipEnable, Levels).IsLow();
        var iei = context.ReadInput(InterruptEnableIn, Levels).IsHigh();

        if (!context.IsInitializing)
        {
            // An opcode fetch: /M1 with /RD. Its byte is taken while it is on the bus, and looked at as /M1 ends.
            if (m1 && rd && !iorq && ReadBus(context) is { } opcode)
            {
                _fetching = true;
                _fetched = opcode;
            }

            if (_m1Was && !m1)
            {
                if (_fetching)
                {
                    if (_edSeen && _fetched == 0x4D && iei) EndService();
                    _edSeen = _fetched == 0xED;
                }

                _fetching = false;
            }

            // The acknowledge: /M1 with /IORQ. Decided once, as /IORQ falls.
            if (m1 && iorq)
            {
                if (!_acknowledgeSeen)
                {
                    _acknowledgeSeen = true;
                    var source = iei ? Requesting() : -1;
                    if (source >= 0)
                    {
                        _vector = VectorFor(source);
                        _acknowledging = source;
                        _inService[source] = true;
                        Acknowledges++;
                        Acknowledged(source);
                        NotifyValueChanged();
                    }
                }
            }
            else
            {
                _acknowledgeSeen = false;
                _acknowledging = -1;
            }

            // A register access: /CE and /IORQ with /M1 high. A write's byte and a read's side effects as it ends.
            var access = ce && iorq && !m1;
            if (access)
            {
                _accessRead = rd;
                _accessRegister = SelectedRegister(context);
                if (!rd && ReadBus(context) is { } value) _accessData = value;
            }
            else if (_accessWas)
            {
                if (_accessRead) AfterRead(_accessRegister);
                else WriteRegister(_accessRegister, _accessData);
                NotifyValueChanged();
            }

            _accessWas = access;
            _m1Was = m1;
        }

        byte? drive = _acknowledging >= 0 ? _vector : _accessWas && _accessRead && ce && iorq && rd ? ReadRegister(_accessRegister) : null;
        var delay = DelayFor(context);
        for (var i = 0; i < 8; i++)
            Request(context, i, drive is { } d ? ((d >> i) & 1) != 0 ? LogicState.High : LogicState.Low : LogicState.HighImpedance, delay);

        var requesting = Requesting();
        Interrupting = iei && requesting >= 0;
        Request(context, IntIndex, Interrupting ? LogicState.Low : LogicState.HighImpedance, delay);

        var ieo = iei && !AnyInService && !(requesting >= 0 && m1 && !_edSeen);
        Request(context, IeoIndex, ieo ? LogicState.High : LogicState.Low, delay);
    }

    /// <summary>The data bus, or null if any line is at no logic level.</summary>
    private byte? ReadBus(IDigitalContext context)
    {
        var value = 0;
        for (var i = 0; i < 8; i++)
        {
            var level = context.ReadInput(_data[i], Levels);
            if (level.IsHigh()) value |= 1 << i;
            else if (!level.IsLow()) return null;
        }

        return (byte)value;
    }

    /// <summary>Forgets every service, acknowledge and access: a reset.</summary>
    private protected void ResetChain()
    {
        Array.Clear(_inService);
        _edSeen = _fetching = _m1Was = _acknowledgeSeen = _accessWas = false;
        _acknowledging = -1;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        ResetChain();
        Acknowledges = 0;
        Returns = 0;
        Interrupting = false;
    }

    public override bool LabelsPins => true;

    private protected sealed record ChainState(bool[] InService, int Acknowledges, int Returns);

    private protected ChainState CaptureChain() => new([.. _inService], Acknowledges, Returns);

    private protected void AdoptChain(ChainState state)
    {
        Array.Copy(state.InService, _inService, _inService.Length);
        Acknowledges = state.Acknowledges;
        Returns = state.Returns;
    }
}
