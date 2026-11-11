using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

/// <summary>
/// A DHT22 (or DHT11) temperature and humidity sensor on its one data wire: open drain, pulled up by a resistor, the
/// host and the sensor taking turns to pull it low.
/// <para>
/// The host asks by holding DATA low (at least 1 ms for a DHT22, 18 ms for a DHT11) and letting it go. 20-40 µs later
/// the sensor answers: 80 µs low, 80 µs high, then 40 bits, each a 50 µs low followed by a high of 26-28 µs for a 0 or
/// 70 µs for a 1, and a last 50 µs low before it lets go. The bits are the humidity, the temperature and a checksum
/// (the low byte of the other four bytes' sum): a DHT22 sends tenths (a temperature's top bit its sign), a DHT11 whole
/// numbers. <see cref="Temperature"/> and <see cref="Humidity"/> are what it reads; change them as it runs.
/// </para>
/// </summary>
public sealed partial class DhtSensor : DigitalIc, IBreakpointSource
{
    private readonly List<(double Time, bool Low)> _schedule = [];
    private int _next;
    private double _lowSince = double.NaN;
    private bool _answering;

    public DhtSensor() : base(4)
    {
        PropagationDelay = 0;
        Vcc = new Terminal("vcc", "VCC", TerminalType.Power, new Point(-30, -30));
        Data = new Terminal("data", "DATA", TerminalType.Bidirectional, new Point(-10, -30));
        var nc = new Terminal("nc", "NC", TerminalType.Passive, new Point(10, -30));
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(30, -30));
        Terminals = [Vcc, Data, nc, Gnd];
        ConfigurePins([Data], [Data]);
    }

    public Terminal Data { get; }

    /// <summary>A DHT11 (blue, whole degrees and percent, a slower start) rather than a DHT22 (white, tenths).</summary>
    [ObservableProperty]
    public partial bool Dht11 { get; set; }

    [ObservableProperty]
    [Operable("Temperature", Minimum = -40, Maximum = 80, Unit = "°C")]
    public partial double Temperature { get; set; } = 21.5;

    [ObservableProperty]
    [Operable("Humidity", Minimum = 0, Maximum = 100, Unit = "%")]
    public partial double Humidity { get; set; } = 45.2;

    public override string PartNumber => Dht11 ? "DHT11" : "DHT22";

    public override string ComponentType => "Temperature and Humidity Sensor";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => $"{PartNumber} {Temperature:0.#} °C {Humidity:0.#} %";

    /// <summary>How many times it has answered.</summary>
    public int Answers { get; private set; }

    /// <summary>The five bytes it sends: humidity, temperature, checksum.</summary>
    public byte[] Bytes()
    {
        byte[] b;
        if (Dht11)
        {
            var h = (int)Math.Clamp(Math.Round(Humidity), 0, 100);
            var t = (int)Math.Clamp(Math.Round(Temperature), 0, 60);
            b = [(byte)h, 0, (byte)t, 0, 0];
        }
        else
        {
            var h = (int)Math.Clamp(Math.Round(Humidity * 10), 0, 1000);
            var t = (int)Math.Round(Math.Abs(Math.Clamp(Temperature, -40, 80)) * 10) | (Temperature < 0 ? 0x8000 : 0);
            b = [(byte)(h >> 8), (byte)h, (byte)(t >> 8), (byte)t, 0];
        }

        b[4] = (byte)(b[0] + b[1] + b[2] + b[3]);
        return b;
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var time = context.Time;
        if (context.IsInitializing)
        {
            Request(context, 0, LogicState.HighImpedance, 0);
            return;
        }

        if (_answering)
        {
            var any = false;
            var low = false;
            while (_next < _schedule.Count && _schedule[_next].Time <= time + 1e-12)
            {
                low = _schedule[_next].Low;
                any = true;
                _next++;
            }

            if (any) Request(context, 0, low ? LogicState.Low : LogicState.HighImpedance, 0);
            if (_next >= _schedule.Count)
            {
                _answering = false;
                _lowSince = double.NaN;
            }

            return;
        }

        // Listening for the host's start: the line held low long enough, then let go.
        var line = context.ReadInput(Data, Levels);
        if (line.IsLow())
        {
            if (double.IsNaN(_lowSince)) _lowSince = time;
            return;
        }

        if (!double.IsNaN(_lowSince) && line.IsHigh())
        {
            var held = time - _lowSince;
            _lowSince = double.NaN;
            if (held >= (Dht11 ? 10e-3 : 0.8e-3)) Answer(time);
        }
    }

    private void Answer(double time)
    {
        _schedule.Clear();
        var t = time + 30e-6;
        void Low(double length) { _schedule.Add((t, true)); t += length; }
        void High(double length) { _schedule.Add((t, false)); t += length; }
        Low(80e-6);
        High(80e-6);
        foreach (var value in Bytes())
            for (var bit = 7; bit >= 0; bit--)
            {
                Low(50e-6);
                High(((value >> bit) & 1) != 0 ? 70e-6 : 27e-6);
            }

        Low(50e-6);
        High(0);
        _next = 0;
        _answering = true;
        Answers++;
    }

    public double? NextBreakpointAfter(double time)
    {
        if (!_answering) return null;
        foreach (var (t, _) in _schedule)
            if (t > time + 1e-12) return t;
        return null;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _schedule.Clear();
        _next = 0;
        _answering = false;
        _lowSince = double.NaN;
        Answers = 0;
    }

    partial void OnTemperatureChanged(double value) => NotifyValueChanged();

    partial void OnHumidityChanged(double value) => NotifyValueChanged();
}
