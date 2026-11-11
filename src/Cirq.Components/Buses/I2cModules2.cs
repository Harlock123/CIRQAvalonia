using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Buses;

/// <summary>
/// A BME280 temperature, pressure and humidity sensor on I2C (0x76, or 0x77 with SDO high), or with <see cref="Bmp280"/>
/// set the BMP280 without the humidity. The registers are the datasheet's: chip ID 0x60 (0x58) at 0xD0, the trimming
/// parameters at 0x88-0xA1 and 0xE1-0xE7, ctrl_hum 0xF2, status 0xF3, ctrl_meas 0xF4, config 0xF5, and the raw
/// readings at 0xF7-0xFE. A library reads the trimming parameters and runs Bosch's compensation on the raw values; the
/// raw values here are worked back from <see cref="Temperature"/>, <see cref="Pressure"/> and <see cref="Humidity"/>
/// through those same formulas, so the library gets what's set. It measures while ctrl_meas's mode is forced or normal,
/// and powers up asleep with the raw registers at 0x80000.
/// </summary>
public sealed partial class Bme280 : I2cSlave
{
    // Trimming parameters: the BMP280 datasheet's worked example, and a real part's humidity set.
    private const ushort T1 = 27504;
    private const short T2 = 26435, T3 = -1000;
    private const ushort P1 = 36477;
    private const short P2 = -10685, P3 = 3024, P4 = 2855, P5 = 140, P6 = -7, P7 = 15500, P8 = -14600, P9 = 6000;
    private const byte H1 = 75;
    private const short H2 = 362;
    private const byte H3 = 0;
    private const short H4 = 313, H5 = 50;
    private const sbyte H6 = 30;

    private readonly byte[] _registers = new byte[256];
    private int _pointer;
    private bool _pointerSet;

    public Bme280()
    {
        Address = 0x76;
        ResetRegisters();
    }

    /// <summary>A BMP280: no humidity, chip ID 0x58.</summary>
    [ObservableProperty]
    public partial bool Bmp280 { get; set; }

    [ObservableProperty]
    [Operable("Temperature", Minimum = -40, Maximum = 85, Unit = "°C")]
    public partial double Temperature { get; set; } = 21.0;

    /// <summary>The air pressure, in pascals (101,325 at sea level).</summary>
    [ObservableProperty]
    public partial double Pressure { get; set; } = 101_325;

    /// <summary>Relative humidity, in percent.</summary>
    [ObservableProperty]
    public partial double Humidity { get; set; } = 45.0;

    public override string ComponentType => Bmp280 ? "Pressure Sensor" : "Environment Sensor";

    public override string ValueLabel => Bmp280
        ? $"BMP280 @ {Address:X2} {Temperature:0.0} °C {Pressure / 100:0} hPa"
        : $"BME280 @ {Address:X2} {Temperature:0.0} °C {Pressure / 100:0} hPa {Humidity:0} %";

    public IReadOnlyList<byte> Registers => _registers;

    private bool Measuring => (_registers[0xF4] & 3) != 0;

    // ───── Bosch's integer compensation, exactly as the datasheet gives it ─────

    public static int TFine(int adcT)
    {
        var var1 = (((adcT >> 3) - (T1 << 1)) * T2) >> 11;
        var var2 = (((((adcT >> 4) - T1) * ((adcT >> 4) - T1)) >> 12) * T3) >> 14;
        return var1 + var2;
    }

    /// <summary>The temperature in hundredths of a degree from a raw reading.</summary>
    public static int CompensateTemperature(int adcT) => ((TFine(adcT) * 5) + 128) >> 8;

    /// <summary>The pressure in 1/256 Pa from a raw reading and t_fine.</summary>
    public static uint CompensatePressure(int adcP, int tFine)
    {
        long var1 = tFine - 128000L;
        var var2 = var1 * var1 * P6;
        var2 += (var1 * P5) << 17;
        var2 += (long)P4 << 35;
        var1 = ((var1 * var1 * P3) >> 8) + ((var1 * P2) << 12);
        var1 = (((1L << 47) + var1) * P1) >> 33;
        if (var1 == 0) return 0;
        long p = 1048576 - adcP;
        p = (((p << 31) - var2) * 3125) / var1;
        var1 = (P9 * (p >> 13) * (p >> 13)) >> 25;
        var2 = (P8 * p) >> 19;
        p = ((p + var1 + var2) >> 8) + ((long)P7 << 4);
        return (uint)p;
    }

    /// <summary>The humidity in 1/1024 %RH from a raw reading and t_fine.</summary>
    public static uint CompensateHumidity(int adcH, int tFine)
    {
        unchecked
        {
            var v = tFine - 76800;
            v = ((((adcH << 14) - (H4 << 20) - (H5 * v)) + 16384) >> 15)
                * (((((((v * H6) >> 10) * (((v * H3) >> 11) + 32768)) >> 10) + 2097152) * H2 + 8192) >> 14);
            v -= ((((v >> 15) * (v >> 15)) >> 7) * H1) >> 4;
            v = Math.Clamp(v, 0, 419430400);
            return (uint)(v >> 12);
        }
    }

    /// <summary>The raw readings the set values come from: a search along each formula, which is monotonic.</summary>
    public (int AdcT, int AdcP, int AdcH) RawFor(double celsius, double pascals, double percent)
    {
        int Search(Func<int, double> f, double target, bool rising, int top)
        {
            int lo = 0, hi = top;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                var below = rising ? f(mid) < target : f(mid) > target;
                if (below) lo = mid + 1; else hi = mid;
            }

            return lo;
        }

        var adcT = Search(a => CompensateTemperature(a) / 100.0, celsius, true, (1 << 20) - 1);
        var tFine = TFine(adcT);
        var adcP = Search(a => CompensatePressure(a, tFine) / 256.0, pascals, false, (1 << 20) - 1);
        var adcH = Search(a => CompensateHumidity(a, tFine) / 1024.0, percent, true, 65535);
        return (adcT, adcP, adcH);
    }

    private void ResetRegisters()
    {
        Array.Clear(_registers);
        _registers[0xD0] = (byte)(Bmp280 ? 0x58 : 0x60);
        void Word(int at, int value) => (_registers[at], _registers[at + 1]) = ((byte)value, (byte)(value >> 8));
        Word(0x88, T1); Word(0x8A, T2); Word(0x8C, T3);
        Word(0x8E, P1); Word(0x90, P2); Word(0x92, P3); Word(0x94, P4); Word(0x96, P5);
        Word(0x98, P6); Word(0x9A, P7); Word(0x9C, P8); Word(0x9E, P9);
        if (!Bmp280)
        {
            _registers[0xA1] = H1;
            Word(0xE1, H2);
            _registers[0xE3] = H3;
            _registers[0xE4] = (byte)(H4 >> 4);
            _registers[0xE5] = (byte)((H4 & 0x0F) | ((H5 & 0x0F) << 4));
            _registers[0xE6] = (byte)(H5 >> 4);
            _registers[0xE7] = (byte)H6;
        }

        _registers[0xF7] = 0x80;
        _registers[0xFA] = 0x80;
        _registers[0xFD] = 0x80;
    }

    private void Measure()
    {
        var (t, p, h) = RawFor(Temperature, Pressure, Humidity);
        (_registers[0xF7], _registers[0xF8], _registers[0xF9]) = ((byte)(p >> 12), (byte)(p >> 4), (byte)((p & 0x0F) << 4));
        (_registers[0xFA], _registers[0xFB], _registers[0xFC]) = ((byte)(t >> 12), (byte)(t >> 4), (byte)((t & 0x0F) << 4));
        if (!Bmp280) (_registers[0xFD], _registers[0xFE]) = ((byte)(h >> 8), (byte)h);

        // Forced mode measures once and goes back to sleep.
        if ((_registers[0xF4] & 3) is 1 or 2) _registers[0xF4] &= 0xFC;
    }

    protected override bool OnAddressed(bool reading)
    {
        if (!reading) _pointerSet = false;
        else if (Measuring) Measure();
        return true;
    }

    protected override bool OnByteReceived(int value)
    {
        // Writes come in register-and-value pairs; the first byte also sets where a read starts.
        if (!_pointerSet)
        {
            _pointer = value;
            _pointerSet = true;
            return true;
        }

        switch (_pointer)
        {
            case 0xE0 when value == 0xB6: ResetRegisters(); break;
            case 0xF2: _registers[0xF2] = (byte)(value & 7); break;
            case 0xF4 or 0xF5: _registers[_pointer] = (byte)value; break;
        }

        _pointerSet = false;
        return true;
    }

    protected override int OnByteRequested()
    {
        var value = _registers[_pointer & 0xFF];
        _pointer = (_pointer + 1) & 0xFF;
        return value;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        ResetRegisters();
        (_pointer, _pointerSet) = (0, false);
    }

    partial void OnBmp280Changed(bool value) => ResetRegisters();
}

/// <summary>
/// A PCA9685 16-channel, 12-bit PWM driver on I2C (0x40), the board that runs sixteen servos or LEDs from two wires.
/// <para>
/// Its 25 MHz oscillator counts 0-4095 at a rate PRE_SCALE (0xFE) sets: f = 25 MHz / (4096 · (PRE_SCALE + 1)), 200 Hz
/// at the default 0x1E and 50 Hz, for servos, at 121. Each channel's four registers from 0x06 (LEDn_ON_L, ON_H, OFF_L,
/// OFF_H) say at which counts its output goes high and low; bit 4 of ON_H holds it fully on, of OFF_H fully off;
/// 0xFA-0xFD write all sixteen at once. MODE1 (0x00) powers up with SLEEP set and the oscillator off, so a library
/// clears it first; its AI bit makes the register pointer step on by itself. PRE_SCALE takes a write only while asleep.
/// MODE2's INVRT and OUTDRV (totem pole, the default, or open drain) shape the outputs; /OE high turns them all off.
/// </para>
/// </summary>
public sealed partial class Pca9685 : I2cSlave, IBreakpointSource
{
    public const double Oscillator = 25e6;
    private readonly Terminal[] _pins = new Terminal[16];
    private readonly byte[] _registers = new byte[256];
    private int _pointer;
    private bool _pointerSet;
    private double _started = double.NaN;

    public Pca9685()
        : base(new Point(-40, -60), new Point(-40, -40), new Point(-40, 40), new Point(-40, 60))
    {
        Address = 0x40;
        OutputEnable = new Terminal("oe", "OE", TerminalType.Input, new Point(-40, 0));
        for (var i = 0; i < 16; i++) _pins[i] = new Terminal($"pwm{i}", $"PWM{i}", TerminalType.Output, new Point(40, -150 + (i * 20)));
        Terminals = [Sda, Scl, Vcc, Gnd, OutputEnable, .. _pins];
        ConfigurePins([Scl, OutputEnable], [Sda, .. _pins]);
        ResetRegisters();
    }

    public Terminal OutputEnable { get; }

    public IReadOnlyList<Terminal> Outputs => _pins;

    public IReadOnlyList<byte> Registers => _registers;

    public bool Asleep => (_registers[0] & 0x10) != 0;

    /// <summary>The PWM frequency PRE_SCALE sets, in hertz.</summary>
    public double Frequency => Oscillator / (4096.0 * (_registers[0xFE] + 1));

    /// <summary>A channel's high time in counts of 4096.</summary>
    public (int On, int Off, bool FullOn, bool FullOff) Channel(int i)
    {
        var at = 6 + (i * 4);
        return (_registers[at] | ((_registers[at + 1] & 0x0F) << 8), _registers[at + 2] | ((_registers[at + 3] & 0x0F) << 8),
            (_registers[at + 1] & 0x10) != 0, (_registers[at + 3] & 0x10) != 0);
    }

    public override string ComponentType => "PWM Driver";

    public override string ValueLabel => Asleep ? $"PCA9685 @ {Address:X2} asleep" : $"PCA9685 @ {Address:X2} {Frequency:0.#} Hz";

    private void ResetRegisters()
    {
        Array.Clear(_registers);
        _registers[0x00] = 0x11;
        _registers[0x01] = 0x04;
        _registers[0xFE] = 0x1E;
        for (var i = 0; i < 16; i++) _registers[9 + (i * 4)] = 0x10;    // every channel fully off
    }

    private double Tick => (_registers[0xFE] + 1) / Oscillator;

    private int Count(double t) => double.IsNaN(_started) ? 0 : (int)(Math.Floor(((t - _started) / Tick) + 1e-6) % 4096);

    private bool Level(int i, int count)
    {
        var (on, off, fullOn, fullOff) = Channel(i);
        if (fullOff) return false;
        if (fullOn) return true;
        if (on == off) return false;
        return on < off ? count >= on && count < off : count >= on || count < off;
    }

    public double? NextBreakpointAfter(double time)
    {
        if (Asleep || double.IsNaN(_started)) return null;
        var tick = Tick;
        var elapsed = Math.Floor(((time - _started) / tick) + 1e-6);
        var count = (int)(elapsed % 4096);
        var best = int.MaxValue;
        for (var i = 0; i < 16; i++)
        {
            var (on, off, fullOn, fullOff) = Channel(i);
            if (fullOn || fullOff || on == off) continue;
            foreach (var edge in new[] { on, off }) best = Math.Min(best, ((edge - count - 1 + 4096) % 4096) + 1);
        }

        return best == int.MaxValue ? null : _started + ((elapsed + best) * tick);
    }

    public override void EvaluateLogic(IDigitalContext context)
    {
        base.EvaluateLogic(context);
        if (Asleep) _started = double.NaN;
        else if (double.IsNaN(_started)) _started = context.Time;
        var count = Count(context.Time);
        var enabled = !context.ReadInput(OutputEnable, Levels).IsHigh();
        var invert = (_registers[1] & 0x10) != 0;
        var totemPole = (_registers[1] & 0x04) != 0;
        for (var i = 0; i < 16; i++)
        {
            var high = enabled && !Asleep && Level(i, count);
            if (invert) high = !high && enabled;
            var state = high ? (totemPole ? LogicState.High : LogicState.HighImpedance) : LogicState.Low;
            if (!enabled) state = totemPole ? LogicState.Low : LogicState.HighImpedance;
            context.Schedule(this, i + 1, state, 0);
        }
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
            _pointer = value & 0xFF;
            _pointerSet = true;
            return true;
        }

        Write(_pointer, (byte)value);
        if ((_registers[0] & 0x20) != 0) _pointer = (_pointer + 1) & 0xFF;
        return true;
    }

    private void Write(int register, byte value)
    {
        if (register == 0xFE)
        {
            if (Asleep) _registers[0xFE] = (byte)Math.Max(value, (byte)3);
        }
        else if (register is >= 0xFA and <= 0xFD)
        {
            for (var i = 0; i < 16; i++) _registers[6 + (i * 4) + (register - 0xFA)] = value;
        }
        else if (register < 0x46 || register is 0x00 or 0x01) _registers[register] = value;

        NotifyValueChanged();
    }

    protected override int OnByteRequested()
    {
        var value = _registers[_pointer];
        if ((_registers[0] & 0x20) != 0) _pointer = (_pointer + 1) & 0xFF;
        return value;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        ResetRegisters();
        (_pointer, _pointerSet, _started) = (0, false, double.NaN);
    }
}

/// <summary>
/// An MCP23017 16-bit I/O expander on I2C (0x20-0x27): ports A and B, GPA0-7 and GPB0-7, in a DIP-28, with the register
/// map at its power-up IOCON.BANK = 0 (A and B registers paired: IODIRA 0x00, IODIRB 0x01, IPOL 0x02, GPINTEN 0x04,
/// DEFVAL 0x06, INTCON 0x08, IOCON 0x0A, GPPU 0x0C, INTF 0x0E, INTCAP 0x10, GPIO 0x12, OLAT 0x14). Each pin is an input
/// (IODIR 1, the default) with an optional 100 kΩ pull-up (GPPU), or an output driving its OLAT bit. Interrupt on change
/// (GPINTEN; INTCON compares with DEFVAL instead of the last value) sets INTF, captures the port in INTCAP and pulls
/// INTA or INTB low until GPIO or INTCAP is read. Sequential addressing steps the register pointer on.
/// </summary>
public sealed partial class Mcp23017 : I2cSlave
{
    private readonly Terminal[] _a = new Terminal[8];
    private readonly Terminal[] _b = new Terminal[8];
    private readonly byte[] _r = new byte[0x16];
    private int _pointer;
    private bool _pointerSet;
    private readonly int[] _last = [-1, -1];

    public Mcp23017()
        : base(DipOffset(13), DipOffset(12), DipOffset(9), DipOffset(10))
    {
        Address = 0x20;
        var pins = new Terminal[29];
        for (var i = 0; i < 8; i++)
        {
            _b[i] = pins[1 + i] = new Terminal($"gpb{i}", $"GPB{i}", TerminalType.Bidirectional, DipOffset(1 + i));
            _a[i] = pins[21 + i] = new Terminal($"gpa{i}", $"GPA{i}", TerminalType.Bidirectional, DipOffset(21 + i));
        }

        pins[9] = Vcc;
        pins[10] = Gnd;
        pins[11] = new Terminal("nc11", "NC", TerminalType.Passive, DipOffset(11));
        pins[12] = Scl;
        pins[13] = Sda;
        pins[14] = new Terminal("nc14", "NC", TerminalType.Passive, DipOffset(14));
        pins[15] = new Terminal("a0", "A0", TerminalType.Passive, DipOffset(15));
        pins[16] = new Terminal("a1", "A1", TerminalType.Passive, DipOffset(16));
        pins[17] = new Terminal("a2", "A2", TerminalType.Passive, DipOffset(17));
        Reset = pins[18] = new Terminal("reset", "RESET", TerminalType.Input, DipOffset(18));
        IntB = pins[19] = new Terminal("intb", "INTB", TerminalType.Output, DipOffset(19));
        IntA = pins[20] = new Terminal("inta", "INTA", TerminalType.Output, DipOffset(20));
        Terminals = [.. pins.Skip(1)];
        ConfigurePins([Scl, Reset, .. _a, .. _b], [Sda, .. _a, .. _b, IntA, IntB]);
        ResetRegisters();
    }

    private static Point DipOffset(int pin) => Digital.DipPackage.PinOffset(pin, 28);

    public Terminal Reset { get; }
    public Terminal IntA { get; }
    public Terminal IntB { get; }

    public IReadOnlyList<Terminal> PortA => _a;

    public IReadOnlyList<Terminal> PortB => _b;

    public IReadOnlyList<byte> Registers => _r;

    public override string ComponentType => "I2C Expander";

    public override string ValueLabel => $"MCP23017 @ {Address:X2}";

    private void ResetRegisters()
    {
        Array.Clear(_r);
        _r[0] = _r[1] = 0xFF;
        _last[0] = _last[1] = -1;
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);
        var vcc = system.Node(Vcc);
        for (var port = 0; port < 2; port++)
            for (var i = 0; i < 8; i++)
                if ((_r[0x0C + port] & (1 << i)) != 0 && (_r[port] & (1 << i)) != 0)
                    system.StampResistor(system.Node(port == 0 ? _a[i] : _b[i]), vcc, 100e3);
    }

    private int ReadPort(IDigitalContext context, int port)
    {
        var pins = port == 0 ? _a : _b;
        var value = 0;
        for (var i = 0; i < 8; i++)
            if (context.ReadInput(pins[i], Levels).IsHigh()) value |= 1 << i;
        return value;
    }

    private readonly int[] _pins = new int[2];

    public override void EvaluateLogic(IDigitalContext context)
    {
        if (context.ReadInput(Reset, Levels).IsLow())
        {
            ResetRegisters();
        }

        for (var port = 0; port < 2; port++)
        {
            var value = ReadPort(context, port);
            _pins[port] = value;
            var reference = _last[port] < 0 ? value : _last[port];
            for (var i = 0; i < 8; i++)
            {
                var bit = 1 << i;
                if ((_r[0x04 + port] & bit) == 0 || (_r[0x0E + port] & bit) != 0) continue;
                var changed = (_r[0x08 + port] & bit) != 0 ? (value & bit) != (_r[0x06 + port] & bit) : (value & bit) != (reference & bit);
                if (changed)
                {
                    _r[0x0E + port] |= (byte)bit;
                    _r[0x10 + port] = (byte)value;
                }
            }

            _last[port] = value;
        }

        base.EvaluateLogic(context);
        var delay = DelayFor(context);
        for (var port = 0; port < 2; port++)
            for (var i = 0; i < 8; i++)
            {
                var output = (_r[port] & (1 << i)) == 0;
                var state = !output ? LogicState.HighImpedance : (_r[0x14 + port] & (1 << i)) != 0 ? LogicState.High : LogicState.Low;
                context.Schedule(this, 1 + (port * 8) + i, state, delay);
            }

        var mirror = (_r[0x0A] & 0x40) != 0;
        var intA = _r[0x0E] != 0 || (mirror && _r[0x0F] != 0);
        var intB = _r[0x0F] != 0 || (mirror && _r[0x0E] != 0);
        var activeHigh = (_r[0x0A] & 0x02) != 0;
        context.Schedule(this, 17, LogicStateExtensions.FromBool(intA == activeHigh), delay);
        context.Schedule(this, 18, LogicStateExtensions.FromBool(intB == activeHigh), delay);
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
            _pointer = value % 0x16;
            _pointerSet = true;
            return true;
        }

        switch (_pointer)
        {
            case 0x0E or 0x0F or 0x10 or 0x11: break;                          // INTF and INTCAP are read only
            case 0x12 or 0x13: _r[_pointer + 2] = (byte)value; break;          // a GPIO write is an OLAT write
            case 0x0A or 0x0B: _r[0x0A] = _r[0x0B] = (byte)(value & 0x7E); break;
            default: _r[_pointer] = (byte)value; break;
        }

        Step();
        NotifyValueChanged();
        return true;
    }

    private void Step()
    {
        if ((_r[0x0A] & 0x20) == 0) _pointer = (_pointer + 1) % 0x16;
    }

    protected override int OnByteRequested()
    {
        int value = _pointer switch
        {
            0x12 or 0x13 => _pins[_pointer - 0x12] ^ _r[0x02 + (_pointer - 0x12)],
            _ => _r[_pointer],
        };

        // Reading GPIO or INTCAP clears the port's interrupt.
        if (_pointer is 0x10 or 0x11 or 0x12 or 0x13) _r[0x0E + (_pointer & 1)] = 0;
        Step();
        return value;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        ResetRegisters();
        (_pointer, _pointerSet) = (0, false);
    }
}
