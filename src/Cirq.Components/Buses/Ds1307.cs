using Cirq.Core.Digital;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Buses;

/// <summary>
/// A DS1307 real-time clock: the other chip everyone puts on an I²C bus.
/// <para>
/// Unlike the EEPROM beside it, this one has something of its own to say — its registers change
/// whether you talk to it or not, which is what a sensor bus is actually for. Read it twice a
/// second apart and you get two different answers.
/// </para>
/// <para>
/// Its registers are <b>binary-coded decimal</b>, which is the thing that catches everyone. The
/// seconds register holding 0x59 means fifty-nine, not eighty-nine: each nibble is a decimal digit.
/// That made sense when the other end was a seven-segment display and it has been confusing
/// programmers ever since, so it is modelled rather than quietly converted.
/// </para>
/// <para>
/// Register 0 also carries the <b>clock halt</b> bit at the top. A brand new part comes up with it
/// set and the clock stopped, which is why a first-time DS1307 famously "does not work" until
/// something writes to it.
/// </para>
/// </summary>
public sealed partial class Ds1307 : I2cSlave
{
    private const int Registers = 64;

    private readonly byte[] _memory = new byte[Registers];

    private int _pointer;
    private bool _pointerReceived;
    private double _now;

    public Ds1307()
    {
        Address = 0x68;
    }

    /// <summary>Hour the clock starts at.</summary>
    [ObservableProperty]
    public partial int StartHour { get; set; } = 12;

    [ObservableProperty]
    public partial int StartMinute { get; set; }

    [ObservableProperty]
    public partial int StartSecond { get; set; }

    /// <summary>
    /// How fast the clock runs against simulated time. Left at one it keeps real time, which at
    /// the rate these simulations advance means nothing visible ever happens — so wind it up.
    /// </summary>
    [ObservableProperty]
    public partial double TimeScale { get; set; } = 1000.0;

    /// <summary>The clock halt bit. A new part comes up stopped, as a real one does.</summary>
    [ObservableProperty]
    public partial bool ClockHalted { get; set; }

    public override string ComponentType => "I2C Clock";

    public override string ValueLabel =>
        ClockHalted ? "DS1307 stopped" : $"{Hour:00}:{Minute:00}:{Second:00}";

    /// <summary>Seconds since midnight, as the clock currently reads.</summary>
    public double SecondsSinceMidnight => _now;

    public int Hour => (int)(_now / 3600) % 24;

    public int Minute => (int)(_now / 60) % 60;

    public int Second => (int)_now % 60;

    /// <summary>A value as the registers hold it: each nibble one decimal digit.</summary>
    public static int ToBcd(int value) => ((value / 10) << 4) | (value % 10);

    /// <summary>The other way, for reading a register back.</summary>
    public static int FromBcd(int value) => ((value >> 4) * 10) + (value & 0x0F);

    public override void EvaluateLogic(IDigitalContext context)
    {
        var start = (StartHour * 3600) + (StartMinute * 60) + StartSecond;

        _now = ClockHalted
            ? start
            : (start + (context.Time * Math.Max(TimeScale, 0.0))) % 86400.0;

        _memory[0x00] = (byte)(ToBcd(Second) | (ClockHalted ? 0x80 : 0x00));
        _memory[0x01] = (byte)ToBcd(Minute);
        _memory[0x02] = (byte)ToBcd(Hour);

        base.EvaluateLogic(context);
    }

    protected override bool OnAddressed(bool reading)
    {
        if (!reading) _pointerReceived = false;
        return true;
    }

    protected override bool OnByteReceived(int value)
    {
        // The first byte of a write is always where to put the rest, which is the shape of
        // nearly every I²C device with registers.
        if (!_pointerReceived)
        {
            _pointer = value % Registers;
            _pointerReceived = true;
            return true;
        }

        if (_pointer == 0x00) ClockHalted = (value & 0x80) != 0;

        _memory[_pointer] = (byte)value;
        _pointer = (_pointer + 1) % Registers;
        return true;
    }

    protected override int OnByteRequested()
    {
        var value = _memory[_pointer];
        _pointer = (_pointer + 1) % Registers;
        return value;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        Array.Clear(_memory);
        _pointer = 0;
        _pointerReceived = false;
        _now = (StartHour * 3600) + (StartMinute * 60) + StartSecond;
    }

    partial void OnClockHaltedChanged(bool value) => NotifyValueChanged();
}
