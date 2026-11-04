using Cirq.Core.Digital;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

/// <summary>
/// An HD44780 character LCD — the sixteen-by-two module that has been on the front of everything
/// since the early eighties and is still the first display most people drive.
/// <para>
/// It is a parallel port with a controller behind it. You put a byte on the data pins, say whether
/// it is a <b>command</b> or a <b>character</b> with RS, and pulse E; the controller latches on the
/// falling edge of E and acts on it. Everything else — clearing, moving the cursor, turning the
/// display on — is a command with a particular bit pattern, which is why driving one is mostly a
/// matter of remembering the table.
/// </para>
/// <para>
/// <b>Four-bit mode</b> is what almost everyone uses, because sixteen pins is a lot and the
/// controller will take each byte as two nibbles, high first. It starts in eight-bit mode as a
/// real one does, and a function set with the data-length bit clear moves it to four — so the
/// initialisation dance that every library performs works here for the same reason it works on
/// hardware.
/// </para>
/// <para>
/// <b>Reading</b> works as on the module: with RW high and E high it drives the data pins — RS low
/// gives the busy flag on D7 and the address counter on D0-D6, RS high the character at the cursor
/// (which then moves on as E falls), as two nibbles in four-bit mode. The busy flag is set for
/// 37 µs after each command or character and 1.52 ms after a clear or a home, the datasheet's
/// execution times, which is what polling code like Ben Eater's <c>lcd_wait</c> waits for. Writes
/// made while it is busy are still accepted here, where a real controller would miss them; tie RW
/// low and wait a fixed time, as most driver code does, and reading never comes into it.
/// </para>
/// </summary>
public sealed partial class CharacterLcd : DigitalIc
{
    /// <summary>Characters of DDRAM per line. Only the first sixteen of each are visible.</summary>
    private const int LineCapacity = 40;

    private const int Columns = 16;
    private const int Lines = 2;

    private readonly char[][] _memory =
        [Enumerable.Repeat(' ', LineCapacity).ToArray(), Enumerable.Repeat(' ', LineCapacity).ToArray()];

    private readonly Terminal[] _data = new Terminal[8];

    private bool _previousEnable;
    private bool _fourBitMode;
    private bool _expectingLowNibble;
    private int _latchedHighNibble;

    private int _cursorLine;
    private int _cursorColumn;
    private bool _incrementing = true;

    private double _busyUntil;
    private double _now;
    private bool _readLowNibble;

    public CharacterLcd() : base(16)
    {
        PropagationDelay = 40e-6;          // what a real controller takes to act on a byte
        Levels = LogicLevels.Ttl;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        Gnd = Pin(1, "VSS", TerminalType.Ground);
        Vcc = Pin(2, "VDD", TerminalType.Power);
        Contrast = Pin(3, "V0", TerminalType.Passive);
        RegisterSelect = Pin(4, "RS", TerminalType.Input);
        ReadWrite = Pin(5, "RW", TerminalType.Input);
        Enable = Pin(6, "E", TerminalType.Input);

        for (var i = 0; i < 8; i++) _data[i] = Pin(7 + i, $"D{i}", TerminalType.Bidirectional);

        Backlight = Pin(15, "A", TerminalType.Passive);
        BacklightCathode = Pin(16, "K", TerminalType.Passive);

        Terminals = [.. pins.Skip(1)];
        // The data pins are outputs too, for reading the busy flag and the display memory.
        ConfigurePins([RegisterSelect, ReadWrite, Enable, .. _data], _data);
    }

    /// <summary>Pin 3: contrast. Not modelled electrically, but a real one needs it set.</summary>
    public Terminal Contrast { get; }

    /// <summary>Pin 4: low for a command, high for a character.</summary>
    public Terminal RegisterSelect { get; }

    /// <summary>Pin 5: low to write, high to read the busy flag or the display memory.</summary>
    public Terminal ReadWrite { get; }

    /// <summary>Pin 6: the controller latches on this going low.</summary>
    public Terminal Enable { get; }

    public Terminal Backlight { get; }

    public Terminal BacklightCathode { get; }

    /// <summary>The eight data pins. In four-bit mode only D4 to D7 are used.</summary>
    public IReadOnlyList<Terminal> Data => _data;

    /// <summary>Whether the display is switched on. It starts off, as a real one does.</summary>
    [ObservableProperty]
    public partial bool DisplayOn { get; set; }

    public override string PartNumber => "HD44780";

    public override string ComponentType => "Character LCD";

    public override string ValueLabel => DisplayOn ? $"\"{Line(0).TrimEnd()}\"" : "off";

    /// <summary>True once a function set has put it into four-bit mode.</summary>
    public bool IsFourBitMode => _fourBitMode;

    /// <summary>Where the next character will go, as a line and column.</summary>
    public (int Line, int Column) Cursor => (_cursorLine, _cursorColumn);

    /// <summary>The visible sixteen characters of one line.</summary>
    public string Line(int index) => new(_memory[index], 0, Columns);

    /// <summary>True while the controller is still carrying out the last command or character.</summary>
    public bool IsBusy => _now < _busyUntil;

    /// <summary>The address counter: where in the display memory the cursor is.</summary>
    public int AddressCounter => (_cursorLine * 0x40) + _cursorColumn;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        _now = context.Time;
        var enable = context.ReadInput(Enable, Levels) == LogicState.High;
        var reading = context.ReadInput(ReadWrite, Levels) == LogicState.High;
        var isData = context.ReadInput(RegisterSelect, Levels) == LogicState.High;

        // The controller latches on the falling edge of E, not on the level. Driving the data
        // pins while E is high changes nothing until E comes back down.
        if (_previousEnable && !enable)
        {
            if (reading) FinishRead(isData);
            else Latch(context);
        }

        _previousEnable = enable;

        // A read drives the pins for as long as E is high with RW high.
        var delay = context.IsInitializing ? 0 : 120e-9;
        if (enable && reading)
        {
            var value = isData ? _memory[_cursorLine][_cursorColumn] & 0xFF : (IsBusy ? 0x80 : 0) | (AddressCounter & 0x7F);
            if (_fourBitMode) value = _readLowNibble ? (value & 0x0F) << 4 : value & 0xF0;

            for (var i = 0; i < 8; i++)
            {
                var driven = !_fourBitMode || i >= 4;
                context.Schedule(this, i, !driven ? LogicState.HighImpedance : ((value >> i) & 1) != 0 ? LogicState.High : LogicState.Low, delay);
            }
        }
        else
        {
            for (var i = 0; i < 8; i++) context.Schedule(this, i, LogicState.HighImpedance, context.IsInitializing ? 0 : 10e-9);
        }
    }

    /// <summary>The end of a read: the next nibble in four-bit mode, and the cursor moving on after a data read.</summary>
    private void FinishRead(bool isData)
    {
        if (_fourBitMode)
        {
            _readLowNibble = !_readLowNibble;
            if (_readLowNibble) return;
        }

        if (isData) _cursorColumn = Math.Clamp(_cursorColumn + (_incrementing ? 1 : -1), 0, LineCapacity - 1);
    }

    private void Latch(IDigitalContext context)
    {
        var isData = context.ReadInput(RegisterSelect, Levels) == LogicState.High;

        var bits = 0;
        for (var i = 0; i < 8; i++)
            if (context.ReadInput(_data[i], Levels) == LogicState.High) bits |= 1 << i;

        if (!_fourBitMode)
        {
            Accept(isData, bits);

            // A function set asking for four-bit working is how a real one is put into it, and
            // it is the last thing the initialisation sequence does in eight-bit mode.
            if (!isData && (bits & 0xF0) == 0x20) _fourBitMode = true;
            return;
        }

        // Four-bit mode: two latches make a byte, high nibble first.
        var nibble = (bits >> 4) & 0x0F;

        if (!_expectingLowNibble)
        {
            _latchedHighNibble = nibble;
            _expectingLowNibble = true;
            return;
        }

        _expectingLowNibble = false;
        Accept(isData, (_latchedHighNibble << 4) | nibble);
    }

    /// <summary>One complete byte, once the nibbles have been put back together.</summary>
    private void Accept(bool isData, int value)
    {
        // Clear and home take 1.52 ms; everything else 37 µs.
        _busyUntil = _now + (!isData && value is >= 0x01 and <= 0x03 ? 1.52e-3 : 37e-6);

        if (isData)
        {
            Write((char)(value & 0xFF));
            return;
        }

        switch (value)
        {
            case 0x01:                                  // clear display
                foreach (var line in _memory) Array.Fill(line, ' ');
                _cursorLine = 0;
                _cursorColumn = 0;
                return;

            case 0x02 or 0x03:                          // return home
                _cursorLine = 0;
                _cursorColumn = 0;
                return;
        }

        if ((value & 0xFC) == 0x04)                     // entry mode set
        {
            _incrementing = (value & 0x02) != 0;
            return;
        }

        if ((value & 0xF8) == 0x08)                     // display on/off control
        {
            DisplayOn = (value & 0x04) != 0;
            return;
        }

        if ((value & 0x80) != 0)                        // set DDRAM address
        {
            var address = value & 0x7F;

            _cursorLine = address >= 0x40 ? 1 : 0;
            _cursorColumn = Math.Clamp(address - (_cursorLine * 0x40), 0, LineCapacity - 1);
        }
    }

    private void Write(char character)
    {
        _memory[_cursorLine][_cursorColumn] = character;

        _cursorColumn += _incrementing ? 1 : -1;

        // It runs off the end of the line rather than wrapping to the next one, which is the
        // single most surprising thing about these modules: line two is a separate address.
        _cursorColumn = Math.Clamp(_cursorColumn, 0, LineCapacity - 1);
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        foreach (var line in _memory) Array.Fill(line, ' ');

        _previousEnable = false;
        _fourBitMode = false;
        _expectingLowNibble = false;
        _latchedHighNibble = 0;
        _cursorLine = 0;
        _cursorColumn = 0;
        _incrementing = true;
        _busyUntil = 0;
        _now = 0;
        _readLowNibble = false;
        DisplayOn = false;
    }
}
