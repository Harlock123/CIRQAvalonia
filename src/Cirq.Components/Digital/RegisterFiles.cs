using System.Globalization;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// Base for the small four-bit-wide memories, the 74189 (sixteen words) and the 74670 (four): a word
/// array whose <see cref="Contents"/> is its preset as hexadecimal digits, one per word, word 0 first,
/// as the byte-wide memories' is in bytes. Setting it loads the memory; reading it gives what is in the
/// memory now, the circuit's own writes included; a reset puts the preset back.
/// </summary>
public abstract class NibbleMemoryIc : DigitalIc
{
    private readonly int[] _words;
    private int[] _preset;

    protected NibbleMemoryIc(int pinCount, int words) : base(pinCount)
    {
        _words = new int[words];
        _preset = new int[words];
    }

    /// <summary>The number of four-bit words.</summary>
    public int WordCount => _words.Length;

    /// <summary>
    /// The words as hex digits, word 0 first ("0123456789ABCDEF" fills a 74189). Spaces are ignored and
    /// a short string leaves the rest zero.
    /// </summary>
    public string Contents
    {
        get => string.Concat(_words.Select(w => w.ToString("X", CultureInfo.InvariantCulture)));
        set
        {
            var digits = (value ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray();
            var preset = new int[_words.Length];

            for (var i = 0; i < Math.Min(digits.Length, preset.Length); i++)
            {
                if (!int.TryParse(digits[i].ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var nibble))
                    throw new FormatException($"'{digits[i]}' is not a hex digit.");
                preset[i] = nibble;
            }

            _preset = preset;
            Array.Copy(preset, _words, preset.Length);
            OnPropertyChanged();
            NotifyValueChanged();
        }
    }

    /// <summary>The word at an address.</summary>
    public int ReadWord(int address) => _words[address];

    /// <summary>Writes a word, as the circuit would.</summary>
    protected void WriteWord(int address, int value)
    {
        if (_words[address] == (value & 0xF)) return;
        _words[address] = value & 0xF;
        OnPropertyChanged(nameof(Contents));
    }

    /// <summary>A four-bit number off four pins, bit 0 first.</summary>
    protected int Read(IDigitalContext context, IReadOnlyList<Terminal> pins) =>
        Enumerable.Range(0, pins.Count).Sum(i => context.ReadInput(pins[i], Levels).IsHigh() ? 1 << i : 0);

    public override void ResetLogic()
    {
        base.ResetLogic();
        Array.Copy(_preset, _words, _words.Length);
    }
}

/// <summary>
/// 74189 64-bit RAM, sixteen words of four bits, with tri-state outputs that are the <b>complement</b> of
/// what is stored — write 0101 and read back 1010, which every circuit built on one either inverts again
/// or is designed round.
/// <para>
/// /CS low selects it. With /WE high it reads: the addressed word, inverted, on Q1-Q4. With /WE low it
/// writes: the word follows D1-D4 for as long as both are low, as a static RAM's does, and the outputs
/// let go of the bus meanwhile. Deselected, the outputs let go too.
/// </para>
/// <para>
/// Pinout: 1=A0, 2=/CS, 3=/WE, 4=D1, 5=/Q1, 6=D2, 7=/Q2, 8=GND, 9=/Q3, 10=D3, 11=/Q4, 12=D4, 13=A3,
/// 14=A2, 15=A1, 16=VCC.
/// </para>
/// </summary>
public sealed class Ic74189 : NibbleMemoryIc
{
    private readonly Terminal[] _address = new Terminal[4];
    private readonly Terminal[] _d = new Terminal[4];
    private readonly Terminal[] _q = new Terminal[4];

    public Ic74189() : base(16, 16)
    {
        PropagationDelay = 35e-9;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        _address[0] = Pin(1, "A0", TerminalType.Input);
        ChipSelect = Pin(2, "CS", TerminalType.Input);
        WriteEnable = Pin(3, "WE", TerminalType.Input);
        _d[0] = Pin(4, "D1", TerminalType.Input);
        _q[0] = Pin(5, "Q1", TerminalType.Bidirectional);
        _d[1] = Pin(6, "D2", TerminalType.Input);
        _q[1] = Pin(7, "Q2", TerminalType.Bidirectional);
        Gnd = Pin(8, "GND", TerminalType.Ground);
        _q[2] = Pin(9, "Q3", TerminalType.Bidirectional);
        _d[2] = Pin(10, "D3", TerminalType.Input);
        _q[3] = Pin(11, "Q4", TerminalType.Bidirectional);
        _d[3] = Pin(12, "D4", TerminalType.Input);
        _address[3] = Pin(13, "A3", TerminalType.Input);
        _address[2] = Pin(14, "A2", TerminalType.Input);
        _address[1] = Pin(15, "A1", TerminalType.Input);
        Vcc = Pin(16, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([.. _address, ChipSelect, WriteEnable, .. _d], _q);
    }

    public override string PartNumber => "74189";

    /// <summary>The symbol names its pins, with a slash on the active-low ones.</summary>
    public override bool LabelsPins => true;

    public override string PinLabel(Terminal pin) =>
        pin == ChipSelect || pin == WriteEnable || _q.Contains(pin) ? "/" + pin.Name : pin.Name;

    /// <summary>A0 to A3.</summary>
    public IReadOnlyList<Terminal> Address => _address;

    /// <summary>D1 to D4, the data written.</summary>
    public IReadOnlyList<Terminal> Data => _d;

    /// <summary>/Q1 to /Q4: the addressed word, inverted, while reading.</summary>
    public IReadOnlyList<Terminal> Outputs => _q;

    /// <summary>Active low.</summary>
    public Terminal ChipSelect { get; }

    /// <summary>Active low: write while low.</summary>
    public Terminal WriteEnable { get; }

    public override string ValueLabel => Contents;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        var selected = context.ReadInput(ChipSelect, Levels).IsLow();
        var writing = selected && context.ReadInput(WriteEnable, Levels).IsLow();
        var address = Read(context, _address);

        if (writing) WriteWord(address, Read(context, _d));

        var word = ReadWord(address);
        for (var i = 0; i < 4; i++)
        {
            var bit = LogicStateExtensions.FromBool((word & (1 << i)) == 0);
            Request(context, i, selected && !writing ? bit : LogicState.HighImpedance, delay);
        }
    }
}

/// <summary>
/// 74670 4 × 4 register file: four words of four bits with separate addresses, enables and pins for
/// reading and for writing, so one word can be read while another is written — the register bank of a
/// small processor in one package.
/// <para>
/// /GW low writes D1-D4 into the word WB WA picks, the word following the inputs while /GW is low and
/// keeping them as it rises. /GR low puts the word RB RA picks on Q1-Q4; high lets go of them
/// (tri-state). Reading the word being written shows the inputs going through.
/// </para>
/// <para>
/// Pinout: 1=D2, 2=D3, 3=D4, 4=RB, 5=RA, 6=Q4, 7=Q3, 8=GND, 9=Q2, 10=Q1, 11=/GR, 12=/GW, 13=WB, 14=WA,
/// 15=D1, 16=VCC.
/// </para>
/// </summary>
public sealed class Ic74670 : NibbleMemoryIc
{
    private readonly Terminal[] _d = new Terminal[4];
    private readonly Terminal[] _q = new Terminal[4];

    public Ic74670() : base(16, 4)
    {
        PropagationDelay = 30e-9;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        _d[1] = Pin(1, "D2", TerminalType.Input);
        _d[2] = Pin(2, "D3", TerminalType.Input);
        _d[3] = Pin(3, "D4", TerminalType.Input);
        ReadB = Pin(4, "RB", TerminalType.Input);
        ReadA = Pin(5, "RA", TerminalType.Input);
        _q[3] = Pin(6, "Q4", TerminalType.Bidirectional);
        _q[2] = Pin(7, "Q3", TerminalType.Bidirectional);
        Gnd = Pin(8, "GND", TerminalType.Ground);
        _q[1] = Pin(9, "Q2", TerminalType.Bidirectional);
        _q[0] = Pin(10, "Q1", TerminalType.Bidirectional);
        ReadEnable = Pin(11, "GR", TerminalType.Input);
        WriteEnable = Pin(12, "GW", TerminalType.Input);
        WriteB = Pin(13, "WB", TerminalType.Input);
        WriteA = Pin(14, "WA", TerminalType.Input);
        _d[0] = Pin(15, "D1", TerminalType.Input);
        Vcc = Pin(16, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([ReadA, ReadB, WriteA, WriteB, ReadEnable, WriteEnable, .. _d], _q);
    }

    public override string PartNumber => "74670";

    public override bool LabelsPins => true;

    public override string PinLabel(Terminal pin) =>
        pin == ReadEnable || pin == WriteEnable ? "/" + pin.Name : pin.Name;

    public IReadOnlyList<Terminal> Data => _d;

    public IReadOnlyList<Terminal> Outputs => _q;

    public Terminal ReadA { get; }
    public Terminal ReadB { get; }
    public Terminal WriteA { get; }
    public Terminal WriteB { get; }

    /// <summary>Active low.</summary>
    public Terminal ReadEnable { get; }

    /// <summary>Active low.</summary>
    public Terminal WriteEnable { get; }

    public override string ValueLabel => Contents;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        bool High(Terminal pin) => context.ReadInput(pin, Levels).IsHigh();

        if (!High(WriteEnable))
            WriteWord((High(WriteA) ? 1 : 0) | (High(WriteB) ? 2 : 0), Read(context, _d));

        var reading = !High(ReadEnable);
        var word = ReadWord((High(ReadA) ? 1 : 0) | (High(ReadB) ? 2 : 0));

        for (var i = 0; i < 4; i++)
        {
            var bit = LogicStateExtensions.FromBool((word & (1 << i)) != 0);
            Request(context, i, reading ? bit : LogicState.HighImpedance, delay);
        }
    }
}
