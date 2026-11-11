using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Buses;

/// <summary>
/// An SSD1306 128 × 64 OLED display module on I2C (address 0x3C), drawn pixel for pixel.
/// <para>
/// After its address every transfer starts with a control byte: bit 6 says whether what follows is commands (0) or
/// display data (1), bit 7 that only one byte follows before the next control byte. The commands that matter are here:
/// display on and off (0xAF, 0xAE), the memory addressing mode (0x20: horizontal, vertical or page), the column and page
/// ranges (0x21, 0x22), the page-mode start page and column (0xB0-0xB7, 0x00-0x0F, 0x10-0x1F), contrast (0x81), the
/// segment and COM remaps that turn the picture round (0xA0/0xA1, 0xC0/0xC8), inverse (0xA6/0xA7) and all-on (0xA4/0xA5);
/// the rest (the charge pump, multiplex ratio, clock and so on) are taken with their parameters and do nothing, as
/// they would on a panel already powered. The 1 KB of display RAM is eight pages of 128 columns, each byte eight
/// pixels down, the least significant bit at the top.
/// </para>
/// </summary>
public sealed partial class Ssd1306 : I2cSlave
{
    public const int Width = 128, Height = 64;

    private readonly byte[] _ram = new byte[Width * Height / 8];
    private bool _expectControl;
    private bool _data;
    private bool _single;
    private readonly List<int> _command = [];
    private int _mode = 2;
    private int _col, _page, _colStart, _colEnd = Width - 1, _pageStart, _pageEnd = 7;

    public Ssd1306() : base(sda: new Point(30, -80), scl: new Point(10, -80), vcc: new Point(-10, -80), gnd: new Point(-30, -80))
    {
        Address = 0x3C;
        Terminals = [Gnd, Vcc, Scl, Sda];
    }

    public override string ComponentType => "OLED Display";

    public override string DesignatorPrefix => "DS";

    public override string ValueLabel => $"SSD1306 128×64 @ {Address:X2}";

    public bool DisplayOn { get; private set; }

    public bool Inverted { get; private set; }

    public bool AllOn { get; private set; }

    public int Contrast { get; private set; } = 0x7F;

    public bool SegmentRemap { get; private set; }

    public bool ComReversed { get; private set; }

    /// <summary>The display RAM as it stands.</summary>
    public IReadOnlyList<byte> Ram => _ram;

    /// <summary>Whether a pixel is lit, as the panel shows it (the remaps, inverse and all-on applied).</summary>
    public bool Pixel(int x, int y)
    {
        if (!DisplayOn) return false;
        if (AllOn) return true;
        var column = SegmentRemap ? Width - 1 - x : x;
        var row = ComReversed ? Height - 1 - y : y;
        var on = (_ram[((row / 8) * Width) + column] >> (row % 8) & 1) != 0;
        return on != Inverted;
    }

    protected override bool OnAddressed(bool reading)
    {
        _expectControl = true;
        _command.Clear();
        return !reading;
    }

    protected override bool OnByteReceived(int value)
    {
        if (_expectControl)
        {
            _data = (value & 0x40) != 0;
            _single = (value & 0x80) != 0;
            _expectControl = false;
            return true;
        }

        if (_data) Write((byte)value);
        else Command(value);
        if (_single) _expectControl = true;
        NotifyValueChanged();
        return true;
    }

    protected override int OnByteRequested() => 0xFF;

    private void Write(byte value)
    {
        _ram[(_page * Width) + _col] = value;
        switch (_mode)
        {
            case 0:
                if (++_col > _colEnd) { _col = _colStart; if (++_page > _pageEnd) _page = _pageStart; }
                break;
            case 1:
                if (++_page > _pageEnd) { _page = _pageStart; if (++_col > _colEnd) _col = _colStart; }
                break;
            default:
                if (++_col >= Width) _col = Width - 1;
                break;
        }
    }

    /// <summary>How many parameter bytes follow a command.</summary>
    private static int Parameters(int command) => command switch
    {
        0x20 or 0x81 or 0x8D or 0xA8 or 0xD3 or 0xD5 or 0xD9 or 0xDA or 0xDB => 1,
        0x21 or 0x22 or 0xA3 => 2,
        0x26 or 0x27 => 6,
        0x29 or 0x2A => 5,
        _ => 0,
    };

    private void Command(int value)
    {
        _command.Add(value);
        if (_command.Count <= Parameters(_command[0])) return;
        var c = _command[0];
        var p = _command.Skip(1).ToList();
        _command.Clear();
        switch (c)
        {
            case 0xAE: DisplayOn = false; break;
            case 0xAF: DisplayOn = true; break;
            case 0xA6: Inverted = false; break;
            case 0xA7: Inverted = true; break;
            case 0xA4: AllOn = false; break;
            case 0xA5: AllOn = true; break;
            case 0xA0: SegmentRemap = false; break;
            case 0xA1: SegmentRemap = true; break;
            case 0xC0: ComReversed = false; break;
            case 0xC8: ComReversed = true; break;
            case 0x81: Contrast = p[0]; break;
            case 0x20: _mode = p[0] & 3; break;
            case 0x21: (_colStart, _colEnd, _col) = (p[0] & 0x7F, p[1] & 0x7F, p[0] & 0x7F); break;
            case 0x22: (_pageStart, _pageEnd, _page) = (p[0] & 7, p[1] & 7, p[0] & 7); break;
            case >= 0xB0 and <= 0xB7: _page = c & 7; break;
            case <= 0x0F: _col = (_col & 0xF0) | c; break;
            case >= 0x10 and <= 0x1F: _col = (_col & 0x0F) | ((c & 0x07) << 4); break;
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        Array.Clear(_ram);
        (_mode, _col, _page, _colStart, _colEnd, _pageStart, _pageEnd) = (2, 0, 0, 0, Width - 1, 0, 7);
        DisplayOn = Inverted = AllOn = SegmentRemap = ComReversed = false;
        Contrast = 0x7F;
        _command.Clear();
    }
}

/// <summary>
/// A DS3231 real-time clock on I2C (address 0x68): seconds, minutes, hours, day, date, month and year in BCD, its two
/// alarms' registers, control and status, and the temperature its crystal compensation measures, in 0.25 °C steps.
/// <para>
/// It keeps time against the simulation from <see cref="StartTime"/>, <see cref="TimeScale"/> times as fast; writing
/// the time registers (as a sketch does to set it) sets the clock from then on. Reading runs on through the registers
/// from the pointer, the first byte written.
/// </para>
/// </summary>
public sealed partial class Ds3231 : I2cSlave
{
    private const int Count = 0x13;
    private readonly byte[] _registers = new byte[Count];
    private int _pointer;
    private bool _pointerSet;
    private DateTime? _setTo;
    private double _setAt;
    private double _now;
    private readonly HashSet<int> _written = [];

    public Ds3231()
    {
        Address = 0x68;
    }

    /// <summary>Where the clock starts, as yyyy-MM-dd HH:mm:ss.</summary>
    [ObservableProperty]
    public partial string StartTime { get; set; } = "2026-01-01 12:00:00";

    /// <summary>How fast it runs against simulated time: one is real time.</summary>
    [ObservableProperty]
    public partial double TimeScale { get; set; } = 1.0;

    /// <summary>The die temperature it reports, in °C.</summary>
    [ObservableProperty]
    [Cirq.Core.Topology.Operable("Temperature", Minimum = -40, Maximum = 85, Unit = "°C")]
    public partial double Temperature { get; set; } = 24.25;

    public override string ComponentType => "Real-Time Clock";

    public override string ValueLabel => $"DS3231 {Now:yyyy-MM-dd HH:mm:ss}";

    /// <summary>The time the clock reads now.</summary>
    public DateTime Now => (_setTo ?? Start).AddSeconds((_now - _setAt) * Math.Max(TimeScale, 0));

    private DateTime Start => DateTime.TryParse(StartTime, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t) ? t : new DateTime(2026, 1, 1, 12, 0, 0);

    private static byte Bcd(int v) => (byte)(((v / 10) << 4) | (v % 10));

    private static int FromBcd(int v) => ((v >> 4) * 10) + (v & 0x0F);

    public override void EvaluateLogic(Cirq.Core.Digital.IDigitalContext context)
    {
        _now = context.Time;
        var t = Now;
        _registers[0] = Bcd(t.Second);
        _registers[1] = Bcd(t.Minute);
        _registers[2] = Bcd(t.Hour);
        _registers[3] = (byte)((int)t.DayOfWeek + 1);
        _registers[4] = Bcd(t.Day);
        _registers[5] = (byte)(Bcd(t.Month) | (t.Year >= 2100 ? 0x80 : 0));
        _registers[6] = Bcd(t.Year % 100);
        var quarters = (int)Math.Round(Temperature * 4);
        _registers[0x11] = (byte)(sbyte)(quarters >> 2);
        _registers[0x12] = (byte)((quarters & 3) << 6);
        base.EvaluateLogic(context);
    }

    protected override bool OnAddressed(bool reading)
    {
        if (!reading)
        {
            _pointerSet = false;
            _written.Clear();
        }

        return true;
    }

    protected override bool OnByteReceived(int value)
    {
        if (!_pointerSet)
        {
            _pointer = value % Count;
            _pointerSet = true;
            return true;
        }

        if (_pointer < 7) _written.Add(_pointer);
        _registers[_pointer] = (byte)value;
        _pointer = (_pointer + 1) % Count;
        return true;
    }

    protected override void OnTransferEnded()
    {
        if (_written.Count == 0) return;

        // The time registers written: the clock runs on from what they say.
        var r = _registers;
        try
        {
            _setTo = new DateTime(2000 + FromBcd(r[6]), Math.Clamp(FromBcd(r[5] & 0x1F), 1, 12), Math.Clamp(FromBcd(r[4]), 1, 31),
                FromBcd(r[2] & 0x3F) % 24, FromBcd(r[1]) % 60, FromBcd(r[0] & 0x7F) % 60);
            _setAt = _now;
        }
        catch (ArgumentOutOfRangeException)
        {
            // A date that doesn't exist (the 31st of a short month) is left as it was.
        }

        _written.Clear();
        NotifyValueChanged();
    }

    protected override int OnByteRequested()
    {
        var value = _registers[_pointer];
        _pointer = (_pointer + 1) % Count;
        return value;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _setTo = null;
        _setAt = 0;
        _pointer = 0;
        _pointerSet = false;
        _written.Clear();
        _registers[0x0E] = 0x1C;
    }
}

/// <summary>
/// An MPU-6050 accelerometer and gyroscope on I2C (0x68, or 0x69 with AD0 high), tilted and turned by hand.
/// <para>
/// It wakes up asleep (PWR_MGMT_1 = 0x40), reading zeros until a sketch clears the sleep bit. Then ACCEL_XOUT_H
/// (0x3B) onwards reads the acceleration (gravity, from <see cref="Pitch"/> and <see cref="Roll"/>), the temperature
/// (raw / 340 + 36.53 °C) and the rotation rates, big-endian and signed, scaled by the ranges ACCEL_CONFIG and
/// GYRO_CONFIG choose (±2 g is 16,384 to a g, ±250 °/s 131 to a degree a second). WHO_AM_I (0x75) is 0x68.
/// </para>
/// </summary>
public sealed partial class Mpu6050 : I2cSlave
{
    private readonly byte[] _registers = new byte[0x80];
    private int _pointer;
    private bool _pointerSet;

    public Mpu6050()
    {
        Address = 0x68;
        _registers[0x6B] = 0x40;
        _registers[0x75] = 0x68;
    }

    /// <summary>AD0 tied high: address 0x69, so a second one can share the bus.</summary>
    [ObservableProperty]
    public partial bool AddressHigh { get; set; }

    partial void OnAddressHighChanged(bool value) => Address = value ? 0x69 : 0x68;

    [ObservableProperty]
    [Cirq.Core.Topology.Operable("Pitch", Minimum = -90, Maximum = 90, Unit = "°")]
    public partial double Pitch { get; set; }

    [ObservableProperty]
    [Cirq.Core.Topology.Operable("Roll", Minimum = -180, Maximum = 180, Unit = "°")]
    public partial double Roll { get; set; }

    /// <summary>Rotation rates about X, Y and Z, in degrees a second.</summary>
    [ObservableProperty]
    public partial double RateX { get; set; }

    [ObservableProperty]
    public partial double RateY { get; set; }

    [ObservableProperty]
    [Cirq.Core.Topology.Operable("Turn", Minimum = -250, Maximum = 250, Unit = "°/s")]
    public partial double RateZ { get; set; }

    [ObservableProperty]
    public partial double Temperature { get; set; } = 25.0;

    public override string ComponentType => "Accelerometer and Gyroscope";

    public override string ValueLabel => $"MPU-6050 pitch {Pitch:0}° roll {Roll:0}°";

    public bool Asleep => (_registers[0x6B] & 0x40) != 0;

    public override void EvaluateLogic(Cirq.Core.Digital.IDigitalContext context)
    {
        Measure();
        base.EvaluateLogic(context);
    }

    private void Put(int at, double value)
    {
        var v = (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);
        _registers[at] = (byte)(v >> 8);
        _registers[at + 1] = (byte)v;
    }

    private void Measure()
    {
        if (Asleep)
        {
            Array.Clear(_registers, 0x3B, 14);
            return;
        }

        var (pitch, roll) = (Pitch * Math.PI / 180, Roll * Math.PI / 180);
        var perG = 16384.0 / (1 << ((_registers[0x1C] >> 3) & 3));
        var perDegree = 131.0 / (1 << ((_registers[0x1B] >> 3) & 3));
        Put(0x3B, -Math.Sin(pitch) * perG);
        Put(0x3D, Math.Sin(roll) * Math.Cos(pitch) * perG);
        Put(0x3F, Math.Cos(roll) * Math.Cos(pitch) * perG);
        Put(0x41, (Temperature - 36.53) * 340);
        Put(0x43, RateX * perDegree);
        Put(0x45, RateY * perDegree);
        Put(0x47, RateZ * perDegree);
    }

    protected override bool OnAddressed(bool reading)
    {
        if (!reading) _pointerSet = false;
        return true;
    }

    protected override bool OnByteReceived(int value)
    {
        if (!_pointerSet)
        {
            _pointer = value & 0x7F;
            _pointerSet = true;
            return true;
        }

        if (_pointer != 0x75 && (_pointer < 0x3B || _pointer > 0x48)) _registers[_pointer] = (byte)value;
        _pointer = (_pointer + 1) & 0x7F;
        if (_pointer - 1 == 0x6B) NotifyValueChanged();
        return true;
    }

    protected override int OnByteRequested()
    {
        var value = _registers[_pointer];
        _pointer = (_pointer + 1) & 0x7F;
        return value;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        Array.Clear(_registers);
        _registers[0x6B] = 0x40;
        _registers[0x75] = 0x68;
        _pointer = 0;
        _pointerSet = false;
    }
}
