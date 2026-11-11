using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Processors;

/// <summary>
/// The Motorola MC14500B industrial control unit: a one-bit processor, in CMOS, made to replace relay ladder logic. It
/// has no program counter and no address bus: a counter steps through a ROM whose words are the four-bit instruction on
/// I0-I3 and an I/O address, the address picking an input onto DATA (through a three-state multiplexer) or an output
/// latch that WRITE strobes.
/// <para>
/// The instruction is latched as CLK falls and done while it's low. LD, LDC, AND, ANDC, OR, ORC and XNOR combine DATA
/// (or its complement) into the result register RR, DATA counting as 0 unless IEN was set; STO and STOC drive RR (or its
/// complement) onto DATA and pulse WRITE, if OEN was set; IEN and OEN load their registers from DATA; SKZ skips the next
/// instruction while RR is 0; JMP, RTN, NOPO and NOPF pulse their flag pins (JMP, RTN, FLAG O, FLAG F) for the outside to
/// act on, and RTN skips the next instruction too. RST high clears RR, IEN and OEN. Opcodes: 0 NOPO, 1 LD, 2 LDC, 3 AND,
/// 4 ANDC, 5 OR, 6 ORC, 7 XNOR, 8 STO, 9 STOC, A IEN, B OEN, C JMP, D RTN, E SKZ, F NOPF.
/// </para>
/// </summary>
public sealed partial class Mc14500 : DigitalIc
{
    private const int DataOut = 0, WriteOut = 1, RrOut = 2, FlagFOut = 3, FlagOOut = 4, RtnOut = 5, JmpOut = 6;
    private static readonly string[] Names = ["NOPO", "LD", "LDC", "AND", "ANDC", "OR", "ORC", "XNOR", "STO", "STOC", "IEN", "OEN", "JMP", "RTN", "SKZ", "NOPF"];
    private readonly Terminal[] _i = new Terminal[4];
    private bool _lastClock = true;
    private bool _rr, _ien, _oen, _skip;
    private int _instruction = -1;
    private bool _executing;

    public Mc14500() : base(16)
    {
        PropagationDelay = 100e-9;
        Levels = LogicLevels.Cmos5V;
        var pins = new Terminal[17];
        Terminal Pin(int n, string name, TerminalType type) => pins[n] = new Terminal($"p{n}", name, type, DipPackage.PinOffset(n, 16));
        Write = Pin(1, "WRITE", TerminalType.Output);
        Data = Pin(2, "DATA", TerminalType.Bidirectional);
        _i[3] = Pin(3, "I3", TerminalType.Input);
        _i[2] = Pin(4, "I2", TerminalType.Input);
        _i[1] = Pin(5, "I1", TerminalType.Input);
        _i[0] = Pin(6, "I0", TerminalType.Input);
        Clock = Pin(7, "X1", TerminalType.Input);
        Gnd = Pin(8, "VSS", TerminalType.Ground);
        Pin(9, "X2", TerminalType.Passive);
        Reset = Pin(10, "RST", TerminalType.Input);
        Result = Pin(11, "RR", TerminalType.Output);
        FlagF = Pin(12, "FLAG F", TerminalType.Output);
        FlagO = Pin(13, "FLAG O", TerminalType.Output);
        Return = Pin(14, "RTN", TerminalType.Output);
        Jump = Pin(15, "JMP", TerminalType.Output);
        Vcc = Pin(16, "VDD", TerminalType.Power);
        Terminals = [.. pins.Skip(1)];
        ConfigurePins([Data, Clock, Reset, .. _i], [Data, Write, Result, FlagF, FlagO, Return, Jump]);
    }

    public Terminal Write { get; }
    public Terminal Data { get; }
    public Terminal Clock { get; }
    public Terminal Reset { get; }
    public Terminal Result { get; }
    public Terminal FlagF { get; }
    public Terminal FlagO { get; }
    public Terminal Return { get; }
    public Terminal Jump { get; }

    /// <summary>I0-I3, I0 the least significant.</summary>
    public IReadOnlyList<Terminal> Instruction => _i;

    public bool ResultRegister => _rr;

    public bool InputEnable => _ien;

    public bool OutputEnable => _oen;

    public static string Mnemonic(int opcode) => Names[opcode & 15];

    public override string PartNumber => "MC14500B";

    public override bool LabelsPins => true;

    public override string ValueLabel => $"RR={(_rr ? 1 : 0)} IEN={(_ien ? 1 : 0)} OEN={(_oen ? 1 : 0)}";

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var clock = context.ReadInput(Clock, Levels).IsHigh();
        if (context.ReadInput(Reset, Levels).IsHigh())
        {
            (_rr, _ien, _oen, _skip, _instruction, _executing) = (false, false, false, false, -1, false);
        }
        else if (!clock && _lastClock && !context.IsInitializing)
        {
            var op = 0;
            for (var i = 0; i < 4; i++)
                if (context.ReadInput(_i[i], Levels).IsHigh()) op |= 1 << i;
            var skipped = _skip;
            _skip = false;
            _instruction = skipped ? -1 : op;
            _executing = !skipped;
            if (!skipped)
            {
                var data = context.ReadInput(Data, Levels).IsHigh() && _ien;
                var raw = context.ReadInput(Data, Levels).IsHigh();
                switch (op)
                {
                    case 1: _rr = data; break;
                    case 2: _rr = !data; break;
                    case 3: _rr &= data; break;
                    case 4: _rr &= !data; break;
                    case 5: _rr |= data; break;
                    case 6: _rr |= !data; break;
                    case 7: _rr = _rr == data; break;
                    case 0xA: _ien = raw; break;
                    case 0xB: _oen = raw; break;
                    case 0xD: _skip = true; break;
                    case 0xE: _skip = !_rr; break;
                }
            }

            NotifyValueChanged();
        }
        else if (clock && !_lastClock) _executing = false;

        _lastClock = clock;
        var delay = DelayFor(context);
        var storing = _executing && _oen && _instruction is 8 or 9;
        Request(context, DataOut, storing ? LogicStateExtensions.FromBool(_instruction == 8 ? _rr : !_rr) : LogicState.HighImpedance, delay);
        Request(context, WriteOut, LogicStateExtensions.FromBool(storing), delay);
        Request(context, RrOut, LogicStateExtensions.FromBool(_rr), delay);
        Request(context, FlagFOut, LogicStateExtensions.FromBool(_executing && _instruction == 0xF), delay);
        Request(context, FlagOOut, LogicStateExtensions.FromBool(_executing && _instruction == 0x0), delay);
        Request(context, RtnOut, LogicStateExtensions.FromBool(_executing && _instruction == 0xD), delay);
        Request(context, JmpOut, LogicStateExtensions.FromBool(_executing && _instruction == 0xC), delay);
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        (_rr, _ien, _oen, _skip, _instruction, _executing, _lastClock) = (false, false, false, false, -1, false, true);
    }
}
