using System.Globalization;
using System.Text;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// Base for the byte-wide parallel memories: the 2K × 8 SRAM and ROM, the 27xx EPROMs, the 28C256
/// EEPROM and the 6116 and 62256 static RAMs.
/// <para>
/// They all work the same way from the outside, which is why one class holds them. Some address
/// lines say which byte, eight tri-state data lines carry it, and three active-low controls say
/// what to do: <c>/CE</c> selects the chip (high, and it ignores everything and lets go of the
/// bus), <c>/OE</c> lets it drive the data lines, and <c>/WE</c>, where there is one, takes the
/// bus into the addressed byte. What differs between them is the size, the pinout, how quickly
/// they answer, and what writing means — nothing at all for an EPROM, a level-sensitive store for
/// a static RAM, and a slow internal cycle for an EEPROM.
/// </para>
/// <para>
/// A part is described by its pin table, so a new memory is a constructor's worth of datasheet
/// rather than a copy of this file. Pins named <c>A0</c>-<c>A14</c> are address lines,
/// <c>D0</c>-<c>D7</c> the data lines, and <c>CE</c>, <c>OE</c>, <c>WE</c>, <c>VCC</c> and
/// <c>GND</c> what they say. <c>VPP</c> and <c>PGM</c> are an EPROM's programming pins: they are
/// there to be wired as the datasheet asks for reading (VPP to the supply, /PGM high) but programming
/// is not simulated. A pin left out of the table is a no-connect and gets no terminal at all.
/// </para>
/// <para>
/// <b>Contents.</b> <see cref="Contents"/> is both the preset and a readout. Type hexadecimal into
/// it and it is loaded; read it back and you get what is in the memory <i>now</i>, the circuit's own
/// writes included. Whitespace-separated bytes, with an optional <c>@addr:</c> to jump somewhere —
/// <c>@000: 3E 01 C3 ... @100: FF 00 ...</c> — and runs of zeros skipped on the way out, which is
/// what keeps a mostly-empty memory from filling the file it is saved in. Resetting the
/// simulation puts the preset back, so a run is repeatable however much the circuit scribbled.
/// A binary or Intel HEX file is loaded with <see cref="Load(MemoryImage)"/>, which writes the
/// image into the preset, so it is saved inside the circuit and the file is not needed again.
/// </para>
/// <para>
/// <b>Timing.</b> The data lines follow the inputs one <see cref="DigitalComponent.PropagationDelay"/>
/// later — set to the part's access time — and let go after <see cref="OutputDisableTime"/>, which
/// is usually shorter. A single figure stands in for the datasheet's address, chip-enable and
/// output-enable access times, which is the right figure to check a design against because it is
/// the slowest of them.
/// </para>
/// </summary>
public abstract partial class ParallelMemory : DigitalIc
{
    private const int Width = 8;

    private readonly Terminal[] _address;
    private readonly Terminal[] _data = new Terminal[Width];

    private readonly byte[] _memory;

    /// <summary>What was typed in or loaded, kept so that a reset can put it back.</summary>
    private string _preset = string.Empty;

    /// <summary>
    /// Builds the package from its pin table.
    /// </summary>
    /// <param name="pinCount">Pins on the package, no-connects included.</param>
    /// <param name="addressBits">Address lines, and so the size: fifteen is thirty-two kilobytes.</param>
    /// <param name="pinout">Each named pin and its number; see the class remarks for the names.</param>
    protected ParallelMemory(int pinCount, int addressBits, params (int Number, string Name)[] pinout)
        : base(pinCount)
    {
        AddressBits = addressBits;
        Size = 1 << addressBits;
        _memory = new byte[Size];
        _address = new Terminal[addressBits];

        List<Terminal> pins = [];
        Terminal? ce = null, oe = null, we = null;
        List<Terminal> extra = [];

        foreach (var (number, name) in pinout.OrderBy(p => p.Number))
        {
            var type = name switch
            {
                "VCC" => TerminalType.Power,
                "GND" => TerminalType.Ground,
                "VPP" => TerminalType.Passive,
                _ when name.StartsWith('D') => TerminalType.Bidirectional,
                _ => TerminalType.Input,
            };

            var pin = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, pinCount));
            pins.Add(pin);

            switch (name)
            {
                case "VCC": Vcc = pin; break;
                case "GND": Gnd = pin; break;
                case "CE": ce = pin; break;
                case "OE": oe = pin; break;
                case "WE": we = pin; break;
                case "VPP": break;
                case "PGM": extra.Add(pin); break;
                default:
                    var index = int.Parse(name[1..], CultureInfo.InvariantCulture);
                    if (name[0] == 'A') _address[index] = pin;
                    else _data[index] = pin;
                    break;
            }
        }

        ChipEnable = ce ?? throw new ArgumentException("A memory needs a CE pin.", nameof(pinout));
        OutputEnable = oe ?? throw new ArgumentException("A memory needs an OE pin.", nameof(pinout));
        WriteEnablePin = we;

        Terminals = [.. pins];

        // The data pins are in both lists: driven while reading, read while writing.
        List<Terminal> inputs = [ChipEnable, OutputEnable];
        if (WriteEnablePin is not null) inputs.Add(WriteEnablePin);
        ConfigurePins([.. inputs, .. extra, .. _address, .. _data], _data);
    }

    /// <summary>How many address lines it has.</summary>
    public int AddressBits { get; }

    /// <summary>How many bytes it holds.</summary>
    public int Size { get; }

    /// <summary>The address inputs, A0 upwards.</summary>
    public IReadOnlyList<Terminal> Address => _address;

    /// <summary>The eight data lines, D0 through D7.</summary>
    public IReadOnlyList<Terminal> Data => _data;

    /// <summary>Active low. High releases the data lines and the part ignores everything.</summary>
    public Terminal ChipEnable { get; }

    /// <summary>Active low. Low presents the addressed byte on the data lines.</summary>
    public Terminal OutputEnable { get; }

    /// <summary>
    /// /WE, active low, where the part has one: null on an EPROM, which cannot be written in a
    /// circuit. The parts that have one also offer it as <c>WriteEnable</c>.
    /// </summary>
    public Terminal? WriteEnablePin { get; }

    public override bool LabelsPins => true;

    public override string PinLabel(Terminal pin) =>
        pin.Name is "CE" or "OE" or "WE" or "PGM" ? "/" + pin.Name : pin.Name;

    /// <summary>Whether a write in the circuit is obeyed. False for a ROM, which ignores it.</summary>
    public virtual bool AcceptsWrites => WriteEnablePin is not null;

    /// <summary>
    /// How long the data lines take to let go once the chip is deselected or its outputs disabled —
    /// the datasheet's t<sub>DF</sub> or t<sub>OHZ</sub>. Never longer than the access time.
    /// </summary>
    protected virtual double OutputDisableTime => PropagationDelay;

    /// <summary>
    /// What is in the memory, in hexadecimal. Setting it loads; reading it gives what is there
    /// <i>now</i>, the circuit's own writes included. See the class remarks for the format.
    /// <para>
    /// Written by hand rather than generated, because the two directions are not symmetric: what
    /// goes in is whatever somebody typed, and what comes out is the state of the memory. A
    /// generated property would hand back the typed string and quietly never show a write.
    /// </para>
    /// </summary>
    public string Contents
    {
        get => Dump();
        set
        {
            _preset = value ?? string.Empty;

            Array.Clear(_memory);
            Parse(_preset);

            OnPropertyChanged(nameof(Contents));
            NotifyValueChanged();
        }
    }

    public override string ValueLabel => LastAddress < 0
        ? Size >= 1024 ? $"{Size / 1024}K × 8" : $"{Size} × 8"
        : $"[{LastAddress.ToString(AddressFormat, CultureInfo.InvariantCulture)}] = {LastData:X2}";

    /// <summary>The address last presented to it, or −1 if it has not been selected yet.</summary>
    public int LastAddress { get; private set; } = -1;

    /// <summary>The byte last read out of it or written into it.</summary>
    public int LastData { get; private set; }

    /// <summary>True while it is driving the data lines.</summary>
    public bool IsReading { get; private set; }

    /// <summary>True while it is taking the data lines into itself.</summary>
    public bool IsWriting { get; private set; }

    /// <summary>How many bytes are not zero, which is the useful measure of how full it is.</summary>
    public int UsedBytes => _memory.Count(b => b != 0);

    /// <summary>One byte, for a test or a caller that wants to look without parsing a dump.</summary>
    public byte ReadByte(int address) => _memory[address & (Size - 1)];

    /// <summary>Writes one byte directly, ignoring whether the part accepts writes.</summary>
    public void WriteByte(int address, byte value) => _memory[address & (Size - 1)] = value;

    /// <summary>A copy of the whole memory as it is now.</summary>
    public byte[] Snapshot() => (byte[])_memory.Clone();

    /// <summary>
    /// Puts an image into the memory and makes it the preset, so it is what every run starts from
    /// and what the circuit file keeps. An address beyond the chip wraps round, as it would with
    /// the upper address lines unconnected — so a 6502 image assembled at $8000 lands at the start of
    /// a 32K ROM — but two bytes wrapping onto the same location is refused, because then the image
    /// is simply bigger than the chip.
    /// </summary>
    /// <returns>A line saying what went where.</returns>
    public string Load(MemoryImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var bytes = new byte[Size];
        var filled = new bool[Size];
        var count = 0;
        var wrapped = false;
        int low = int.MaxValue, high = -1;

        foreach (var (start, data) in image.Blocks)
        {
            for (var i = 0; i < data.Length; i++)
            {
                var address = start + i;
                var at = address & (Size - 1);
                if (at != address) wrapped = true;

                if (filled[at])
                    throw new InvalidDataException(
                        $"The image is bigger than this {Describe(Size)} part: address " +
                        $"${address:X4} lands on ${at:X4}, which the image has already filled.");

                filled[at] = true;
                bytes[at] = data[i];
                count++;
                low = Math.Min(low, at);
                high = Math.Max(high, at);
            }
        }

        if (count == 0) throw new InvalidDataException("The image has no bytes in it.");

        Array.Copy(bytes, _memory, Size);
        _preset = Dump();

        OnPropertyChanged(nameof(Contents));
        NotifyValueChanged();

        var where = $"${low.ToString(AddressFormat, CultureInfo.InvariantCulture)}-${high.ToString(AddressFormat, CultureInfo.InvariantCulture)}";
        return $"{count} bytes at {where}{(wrapped ? ", wrapped into the chip's own address range" : string.Empty)}";
    }

    /// <summary>"32K", "2K", "512 bytes".</summary>
    private static string Describe(int size) => size >= 1024 ? $"{size / 1024}K" : $"{size}-byte";

    /// <summary>Hex digits enough for the highest address: three for 2K and 4K, four above.</summary>
    private string AddressFormat => Size > 0x1000 ? "X4" : "X3";

    /// <summary>The address the inputs are presenting.</summary>
    protected int ReadAddress(IDigitalContext context)
    {
        var address = 0;
        for (var i = 0; i < AddressBits; i++)
            if (context.ReadInput(_address[i], Levels).IsHigh()) address |= 1 << i;

        return address;
    }

    /// <summary>The byte on the data lines, as somebody else is driving them.</summary>
    protected int ReadDataBus(IDigitalContext context)
    {
        var value = 0;
        for (var i = 0; i < Width; i++)
            if (context.ReadInput(_data[i], Levels).IsHigh()) value |= 1 << i;

        return value;
    }

    /// <summary>Drives a byte onto the data lines after the access time.</summary>
    protected void Drive(IDigitalContext context, int value)
    {
        var delay = DelayFor(context);

        for (var i = 0; i < Width; i++)
        {
            var bit = (value & (1 << i)) != 0;
            Request(context, i, bit ? LogicState.High : LogicState.Low, delay);
        }
    }

    /// <summary>Lets go of the data lines after the output disable time.</summary>
    protected void Release(IDigitalContext context)
    {
        var delay = context.IsInitializing ? 0.0 : Math.Min(OutputDisableTime, PropagationDelay);

        for (var i = 0; i < Width; i++)
            Request(context, i, LogicState.HighImpedance, delay);
    }

    /// <summary>Records what the bus is doing, for the value label and the tests.</summary>
    protected void Note(int address, int data, bool reading, bool writing)
    {
        if (address >= 0) LastAddress = address;
        LastData = data;
        IsReading = reading;
        IsWriting = writing;
    }

    /// <summary>The byte stored at an address, for a subclass that decides what to drive itself.</summary>
    protected byte Stored(int address) => _memory[address & (Size - 1)];

    /// <summary>Stores a byte, for a subclass that decides when a write happens.</summary>
    protected void Store(int address, byte value)
    {
        _memory[address & (Size - 1)] = value;
        NotifyValueChanged();
    }

    /// <summary>
    /// A static RAM's logic, and a ROM's with the write left out. Writing is level-sensitive, as it
    /// is on the silicon: while /CE and /WE are both low the addressed byte follows the bus, and what
    /// it holds afterwards is whatever was there when one of them went back up.
    /// </summary>
    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var selected = context.ReadInput(ChipEnable, Levels).IsLow();
        var writeLow = WriteEnablePin is not null && context.ReadInput(WriteEnablePin, Levels).IsLow();
        var reading = selected && !writeLow && context.ReadInput(OutputEnable, Levels).IsLow();
        var writing = selected && writeLow;

        if (!selected)
        {
            Note(-1, LastData, false, false);
            Release(context);
            return;
        }

        var address = ReadAddress(context);

        if (writing)
        {
            // Whatever is on the bus goes in, and the data pins stay released — somebody else is
            // driving them, which is the entire point of the write.
            var value = ReadDataBus(context);
            if (AcceptsWrites) Store(address, (byte)value);

            Note(address, value, false, true);
            Release(context);
            return;
        }

        if (!reading)
        {
            Note(address, LastData, false, false);
            Release(context);
            return;
        }

        var stored = _memory[address];
        Note(address, stored, true, false);
        Drive(context, stored);
        NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        // Back to what was typed in, so a second run of the same circuit gives the same answers
        // however much the first one scribbled on the memory.
        Array.Clear(_memory);
        Parse(_preset);

        LastAddress = -1;
        LastData = 0;
        IsReading = false;
        IsWriting = false;
    }

    /// <summary>
    /// Reads the hex form into memory. Anything that is not a byte or an <c>@addr:</c> is skipped
    /// rather than refused: this is a field somebody types into, and half-finished text should not
    /// empty the memory on the way to being finished.
    /// </summary>
    private void Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var at = 0;

        foreach (var token in text.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith('@'))
            {
                var origin = token[1..].TrimEnd(':');

                if (int.TryParse(origin, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
                    at = parsed & (Size - 1);

                continue;
            }

            if (!byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                continue;

            if (at >= Size) break;

            _memory[at++] = value;
        }
    }

    /// <summary>
    /// The memory as hexadecimal, sixteen bytes to the line, with runs of zeros skipped and an
    /// <c>@addr:</c> wherever the dump jumps. A blank memory comes back as an empty string rather
    /// than thousands of zeros.
    /// </summary>
    public string Dump()
    {
        var builder = new StringBuilder();
        var expecting = -1;
        var format = AddressFormat;
        var indent = new string(' ', format == "X4" ? 7 : 6);

        for (var line = 0; line < Size; line += 16)
        {
            var empty = true;
            for (var i = line; i < line + 16 && empty; i++) empty = _memory[i] == 0;

            if (empty) continue;

            if (line != expecting)
            {
                if (builder.Length > 0) builder.Append('\n');
                builder.Append('@').Append(line.ToString(format, CultureInfo.InvariantCulture)).Append(':');
            }
            else
            {
                builder.Append('\n');
                builder.Append(indent);
            }

            for (var i = line; i < line + 16; i++)
                builder.Append(CultureInfo.InvariantCulture, $" {_memory[i]:X2}");

            expecting = line + 16;
        }

        return builder.ToString();
    }
}
