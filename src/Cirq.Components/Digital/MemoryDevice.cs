using System.Globalization;
using System.Text;
using Cirq.Core.Digital;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

/// <summary>
/// A 2K × 8 static memory of the 6116 sort: eleven address lines, eight tri-state data lines, and
/// three control pins.
/// <para>
/// This is the part that turns the logic in the palette into a <b>system</b>. A counter on the
/// address lines and a transceiver on the data lines is, in the sense that matters for
/// understanding one, a computer: something walks through addresses, something else answers with
/// what is stored there, and the bus carries the answers one at a time. Until the 74245 arrived
/// there was no way to build that here, because there was no output that could let go of a wire.
/// </para>
/// <para>
/// <b>Reading and presetting.</b> <see cref="Contents"/> is both. Type bytes into it and they are
/// loaded; read it back and you get what is in the memory <i>now</i>, including anything the
/// circuit has written since. It is hexadecimal, whitespace-separated, with an optional
/// <c>@addr:</c> to jump somewhere — so a program at zero and a table at 0x100 is written
/// <c>@000: 3E 01 C3 ... @100: FF 00 ...</c> and read back in the same form. Runs of zeros are
/// skipped on the way out, which is what keeps a mostly-empty 2K from filling the file it is
/// saved in.
/// </para>
/// <para>
/// Resetting the simulation puts the preset back, so a run is repeatable however much the circuit
/// scribbled on the memory last time.
/// </para>
/// <para>
/// Set <see cref="IsReadOnly"/> and it is a ROM: writes are ignored rather than obeyed, which is
/// the difference between a part that holds a program and one that holds a variable.
/// </para>
/// <para>
/// Pinout follows the 6116: 1-8 = A7-A0, 9-11 and 13-17 = the data lines, 12 = GND, 18 = /CE,
/// 19 = A10, 20 = /OE, 21 = /WE, 22 = A9, 23 = A8, 24 = VCC. All three controls are active low.
/// </para>
/// </summary>
public sealed partial class MemoryDevice : DigitalIc
{
    /// <summary>Address lines, and so the size: eleven of them is two kilobytes.</summary>
    public const int AddressLines = 11;

    /// <summary>How many bytes it holds.</summary>
    public const int Capacity = 1 << AddressLines;

    private const int Width = 8;

    private readonly Terminal[] _address = new Terminal[AddressLines];
    private readonly Terminal[] _data = new Terminal[Width];

    private readonly byte[] _memory = new byte[Capacity];

    /// <summary>What was typed in, kept so that a reset can put it back.</summary>
    private string _preset = string.Empty;

    public MemoryDevice() : base(24)
    {
        PropagationDelay = 70e-9;

        var pins = new Terminal[25];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 24));

        // A7 down to A0 along the top of the left-hand side, as the real part has them.
        for (var i = 0; i < 8; i++) _address[7 - i] = Pin(1 + i, $"A{7 - i}", TerminalType.Input);

        _data[0] = Pin(9, "D0", TerminalType.Bidirectional);
        _data[1] = Pin(10, "D1", TerminalType.Bidirectional);
        _data[2] = Pin(11, "D2", TerminalType.Bidirectional);
        Gnd = Pin(12, "GND", TerminalType.Ground);
        _data[3] = Pin(13, "D3", TerminalType.Bidirectional);
        _data[4] = Pin(14, "D4", TerminalType.Bidirectional);
        _data[5] = Pin(15, "D5", TerminalType.Bidirectional);
        _data[6] = Pin(16, "D6", TerminalType.Bidirectional);
        _data[7] = Pin(17, "D7", TerminalType.Bidirectional);

        ChipEnable = Pin(18, "CE", TerminalType.Input);
        _address[10] = Pin(19, "A10", TerminalType.Input);
        OutputEnable = Pin(20, "OE", TerminalType.Input);
        WriteEnable = Pin(21, "WE", TerminalType.Input);
        _address[9] = Pin(22, "A9", TerminalType.Input);
        _address[8] = Pin(23, "A8", TerminalType.Input);
        Vcc = Pin(24, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];

        // The data pins are in both lists: driven while reading, read while writing.
        ConfigurePins([ChipEnable, OutputEnable, WriteEnable, .. _address, .. _data], _data);
    }

    /// <summary>The eleven address inputs, A0 through A10.</summary>
    public IReadOnlyList<Terminal> Address => _address;

    /// <summary>The eight data lines, D0 through D7.</summary>
    public IReadOnlyList<Terminal> Data => _data;

    /// <summary>Active low. High releases the data lines and the part ignores everything.</summary>
    public Terminal ChipEnable { get; }

    /// <summary>Active low. Low presents the addressed byte on the data lines.</summary>
    public Terminal OutputEnable { get; }

    /// <summary>Active low. Low takes whatever is on the data lines into the addressed byte.</summary>
    public Terminal WriteEnable { get; }

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
            Load(_preset);

            OnPropertyChanged(nameof(Contents));
            NotifyValueChanged();
        }
    }

    /// <summary>When true, writes are ignored — which makes it a ROM rather than a RAM.</summary>
    [ObservableProperty]
    public partial bool IsReadOnly { get; set; }

    public override string PartNumber => IsReadOnly ? "ROM 2Kx8" : "SRAM 2Kx8";

    public override string ComponentType => IsReadOnly ? "ROM" : "SRAM";

    public override string ValueLabel => LastAddress < 0
        ? $"{Capacity / 1024}K × 8"
        : $"[{LastAddress:X3}] = {LastData:X2}";

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
    public byte ReadByte(int address) => _memory[address & (Capacity - 1)];

    /// <summary>Writes one byte directly, ignoring <see cref="IsReadOnly"/>.</summary>
    public void WriteByte(int address, byte value) => _memory[address & (Capacity - 1)] = value;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);

        var selected = context.ReadInput(ChipEnable, Levels).IsLow();
        var reading = selected && context.ReadInput(OutputEnable, Levels).IsLow()
                               && context.ReadInput(WriteEnable, Levels).IsHigh();
        var writing = selected && context.ReadInput(WriteEnable, Levels).IsLow();

        IsReading = reading;
        IsWriting = writing;

        if (!selected)
        {
            Release(context, delay);
            return;
        }

        var address = 0;
        for (var i = 0; i < AddressLines; i++)
            if (context.ReadInput(_address[i], Levels).IsHigh()) address |= 1 << i;

        LastAddress = address;

        if (writing)
        {
            // Whatever is on the bus goes in, and the data pins stay released — somebody else is
            // driving them, which is the entire point of the write.
            var value = 0;
            for (var i = 0; i < Width; i++)
                if (context.ReadInput(_data[i], Levels).IsHigh()) value |= 1 << i;

            if (!IsReadOnly)
            {
                _memory[address] = (byte)value;
                NotifyValueChanged();
            }

            LastData = value;
            Release(context, delay);
            return;
        }

        if (!reading)
        {
            Release(context, delay);
            return;
        }

        var stored = _memory[address];
        LastData = stored;

        for (var i = 0; i < Width; i++)
        {
            var bit = (stored & (1 << i)) != 0;
            context.Schedule(this, i, bit ? LogicState.High : LogicState.Low, delay);
        }

        NotifyValueChanged();
    }

    private void Release(IDigitalContext context, double delay)
    {
        for (var i = 0; i < Width; i++)
            context.Schedule(this, i, LogicState.HighImpedance, delay);
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        // Back to what was typed in, so a second run of the same circuit gives the same answers
        // however much the first one scribbled on the memory.
        Array.Clear(_memory);
        Load(_preset);

        LastAddress = -1;
        LastData = 0;
        IsReading = false;
        IsWriting = false;
    }

    partial void OnIsReadOnlyChanged(bool value) => NotifyValueChanged();

    /// <summary>
    /// Reads the hex form into memory. Anything that is not a byte or an <c>@addr:</c> is skipped
    /// rather than refused: this is a field somebody types into, and half-finished text should not
    /// empty the memory on the way to being finished.
    /// </summary>
    private void Load(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var at = 0;

        foreach (var token in text.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith('@'))
            {
                var origin = token[1..].TrimEnd(':');

                if (int.TryParse(origin, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
                    at = parsed & (Capacity - 1);

                continue;
            }

            if (!byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                continue;

            if (at >= Capacity) break;

            _memory[at++] = value;
        }
    }

    /// <summary>
    /// The memory as hexadecimal, sixteen bytes to the line, with runs of zeros skipped and an
    /// <c>@addr:</c> wherever the dump jumps. A blank memory comes back as an empty string rather
    /// than two thousand zeros.
    /// </summary>
    public string Dump()
    {
        var builder = new StringBuilder();
        var expecting = -1;

        for (var line = 0; line < Capacity; line += 16)
        {
            var empty = true;
            for (var i = line; i < line + 16 && empty; i++) empty = _memory[i] == 0;

            if (empty) continue;

            if (line != expecting)
            {
                if (builder.Length > 0) builder.Append('\n');
                builder.Append(CultureInfo.InvariantCulture, $"@{line:X3}:");
            }
            else
            {
                builder.Append('\n');
                builder.Append("      ");
            }

            for (var i = line; i < line + 16; i++)
                builder.Append(CultureInfo.InvariantCulture, $" {_memory[i]:X2}");

            expecting = line + 16;
        }

        return builder.ToString();
    }
}
