using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Buses;

/// <summary>
/// A DS18B20: a digital thermometer on a single wire, and the part most people meet 1-Wire through.
/// <para>
/// It is worth comparing with the thermistor elsewhere in this library, because they solve the
/// same problem in opposite ways. A thermistor is a resistance that changes; getting a temperature
/// out of it means a divider, a reference, an ADC and a curve, and every one of those is a place
/// for error to get in. This has all of that inside it already, and hands over a number in degrees
/// — over one wire, from up to tens of metres away, with a dozen of them on the same wire if you
/// like. That is why it is in everything.
/// </para>
/// <para>
/// Three of its behaviours cause nearly all the confusion, and all three are modelled here.
/// </para>
/// <para>
/// <b>Eighty-five degrees means "no reading".</b> The scratchpad powers up holding 0x0550, which
/// is exactly 85.0 °C, and it stays there until a conversion has actually finished. A sketch that
/// reads 85 has not found a hot room; it has read the register before the conversion was done, or
/// the conversion never happened at all. It is the single most reported DS18B20 fault and it is
/// not a fault.
/// </para>
/// <para>
/// <b>A conversion takes most of a second.</b> Three quarters of it at twelve-bit resolution, and
/// the part says nothing while it works — the master simply has to wait. Drop to nine bits and it
/// is ninety milliseconds, at a sixteenth of the resolution. That trade is the <c>config</c> byte,
/// and it is the reason a chain of ten sensors polled at twelve bits cannot be read every second.
/// </para>
/// <para>
/// <b>Parasitic power is a trap with a sharp edge.</b> With the supply pin grounded the part runs
/// off the data line, charging a capacitor inside it while the line is idle high. That works for
/// everything except converting, which needs a milliamp and a half — far more than an ordinary
/// pull-up resistor can pass. Without a strong pull-up across the line for the duration, the chip
/// browns out part way through, the conversion never completes, and the scratchpad still holds 85.
/// This model draws the current, so the sag is real and so is the failure.
/// </para>
/// </summary>
public sealed partial class Ds18b20 : DigitalComponent, IInteractiveComponent, IBreakpointSource
{
    /// <summary>What the scratchpad holds before anything has been measured: 85.0 °C exactly.</summary>
    private const int PowerOnReading = 0x0550;

    private const double ResetLowSeconds = 410e-6;
    private const double PresenceDelay = 30e-6;
    private const double PresenceLength = 120e-6;
    private const double SlaveBitLow = 30e-6;
    private const double BitThreshold = 30e-6;

    private enum Phase { Command, Function, WriteScratchpad, Sending }

    private readonly int[] _scratchpad = new int[9];

    /// <summary>
    /// The ROM code: family 0x28 for this part, then a serial number, then a CRC over both. What
    /// makes a dozen of these on one wire possible, since it is how the master tells them apart.
    /// </summary>
    private readonly int[] _rom = [0x28, 0x1C, 0x4E, 0xB2, 0x0A, 0x00, 0x00, 0x00];

    private int[] _sending = [];

    private bool _previousHigh = true;
    private double _fellAt = double.NegativeInfinity;

    private double _driveFrom = double.PositiveInfinity;
    private double _driveUntil = double.NegativeInfinity;

    private Phase _phase = Phase.Command;
    private int _shift;
    private int _bits;
    private int _sendIndex;
    private int _sendBit;
    private int _sendCount;
    private int _writeIndex;
    private bool _addressed;
    private bool _selfDrove;

    private double _convertingUntil = double.NegativeInfinity;
    private double _lineLowestWhileConverting = double.PositiveInfinity;

    public Ds18b20()
    {
        PropagationDelay = 1e-6;
        Levels = LogicLevels.Cmos33V;

        Data = new Terminal("dq", "DQ", TerminalType.Bidirectional, new Point(40, 0));
        Vdd = new Terminal("vdd", "VDD", TerminalType.Power, new Point(-40, -20));
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(-40, 20));

        Terminals = [Vdd, Data, Gnd];
        ConfigurePins([], [Data]);

        _rom[7] = Crc8(_rom, 7);

        ResetScratchpad();
    }

    /// <summary>The one wire. Open drain: pulled down or let go of, never driven up.</summary>
    public Terminal Data { get; }

    /// <summary>Supply. Ground it and the part runs off the data line instead.</summary>
    public Terminal Vdd { get; }

    public Terminal Gnd { get; }

    /// <summary>The temperature at the sensor, in degrees Celsius.</summary>
    [ObservableProperty]
    [Operable("Temperature", Minimum = -55, Maximum = 125, Unit = "°C")]
    public partial double Temperature { get; set; } = 22.0;

    /// <summary>What it swings to when you double-click it — a hand round the sensor.</summary>
    [ObservableProperty]
    public partial double AlternateTemperature { get; set; } = 36.0;

    /// <summary>Resolution in bits, 9 to 12. Fewer bits is a faster conversion and a coarser answer.</summary>
    [ObservableProperty]
    public partial int Resolution { get; set; } = 12;

    /// <summary>
    /// Current it draws from the line while converting, in amps. The number that decides whether
    /// parasitic power works, and the reason a 4.7 kΩ pull-up on its own does not.
    /// </summary>
    [ObservableProperty]
    public partial double ConversionCurrent { get; set; } = 1.5e-3;

    /// <summary>Supply below which the part browns out, in volts.</summary>
    [ObservableProperty]
    public partial double MinimumSupplyVoltage { get; set; } = 3.0;

    /// <summary>
    /// Resistance behind the pin while it is pulling the line down, in ohms. The datasheet figure
    /// is four milliamps at four tenths of a volt, which is this — and it is what decides how
    /// stiff a pull-up the part can still pull down against.
    /// </summary>
    [ObservableProperty]
    public partial double OpenDrainResistance { get; set; } = 100.0;

    protected override double OutputImpedance => Math.Max(OpenDrainResistance, 1e-3);

    public override string ComponentType => "1-Wire Thermometer";

    public override string ValueLabel => $"{Temperature:0.#} °C";

    public string InteractionHint => "Put a hand round it";

    /// <summary>The scratchpad exactly as a read would return it, nine bytes including the CRC.</summary>
    public IReadOnlyList<int> Scratchpad => _scratchpad;

    /// <summary>
    /// The temperature the scratchpad presently holds, in degrees — 85 until a conversion has
    /// actually finished.
    /// </summary>
    public double ReportedTemperature => (short)(_scratchpad[0] | (_scratchpad[1] << 8)) * 0.0625;

    /// <summary>True while a conversion is under way.</summary>
    public bool IsConverting { get; private set; }

    /// <summary>True once a conversion has completed, so the scratchpad holds a real reading.</summary>
    public bool HasConverted { get; private set; }

    /// <summary>
    /// True when a conversion was started but the line could not hold the part up while it ran.
    /// Parasitic power without a strong pull-up, and the reading stays at 85.
    /// </summary>
    public bool ConversionBrownedOut { get; private set; }

    /// <summary>True when the supply pin is not being fed and it is living off the data line.</summary>
    public bool IsParasiticallyPowered { get; private set; }

    /// <summary>
    /// True once a ROM command has selected it since the last reset. A function command sent
    /// without one is dropped, which is what a sketch missing its 0xCC looks like from outside.
    /// </summary>
    public bool IsAddressed => _addressed;

    /// <summary>The ROM code it answers with, family byte first.</summary>
    public IReadOnlyList<int> RomCode => _rom;

    /// <summary>How long a conversion takes at the current resolution, in seconds.</summary>
    public double ConversionSeconds => Math.Clamp(Resolution, 9, 12) switch
    {
        9 => 93.75e-3,
        10 => 187.5e-3,
        11 => 375e-3,
        _ => 750e-3,
    };

    /// <summary>The step size the current resolution gives, in degrees.</summary>
    public double ResolutionStep => 0.0625 * (1 << (12 - Math.Clamp(Resolution, 9, 12)));

    protected override int ReferenceNode(MnaSystem system) => system.Node(Gnd);

    public void Interact() => (Temperature, AlternateTemperature) = (AlternateTemperature, Temperature);

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);

        // While converting on parasitic power the chip takes its current from the wire, which is
        // what makes the pull-up's value matter rather than merely being good practice.
        if (IsConverting && IsParasiticallyPowered)
            system.StampCurrentSource(system.Node(Data), system.Node(Gnd), Math.Max(ConversionCurrent, 0.0));
    }

    private bool IsHigh(IDigitalContext context) =>
        Levels.Classify(context.NodeVoltage(Data) - context.NodeVoltage(Gnd)) != LogicState.Low;

    public override void EvaluateLogic(IDigitalContext context)
    {
        var now = context.Time;
        var line = context.NodeVoltage(Data) - context.NodeVoltage(Gnd);
        var supply = context.NodeVoltage(Vdd) - context.NodeVoltage(Gnd);

        IsParasiticallyPowered = supply < MinimumSupplyVoltage;

        FinishConversion(now, line);

        var high = IsHigh(context);

        if (!high && _previousHigh) _fellAt = now;
        if (high && !_previousHigh) OnRelease(now, now - _fellAt);

        // A falling edge is the start of every slot, whichever direction the bit is going.
        if (!high && _previousHigh && _phase == Phase.Sending) SendNextBit(now);

        _previousHigh = high;

        var driving = now >= _driveFrom && now < _driveUntil;



        // Remember that the low was partly ours, so the presence pulse and the zero bits this
        // part sends are not read back as though the master had sent them.
        if (driving) _selfDrove = true;

        context.Schedule(this, 0, driving ? LogicState.Low : LogicState.HighImpedance, DelayFor(context));
    }

    /// <summary>Called when the line comes back up, with how long it was held down.</summary>
    private void OnRelease(double now, double lowSeconds)
    {
        // Nothing this part does holds the line anywhere near this long, so a low of half a
        // millisecond can only be the master asking everybody to start again.
        if (lowSeconds >= ResetLowSeconds)
        {
            _phase = Phase.Command;
            _shift = 0;
            _bits = 0;
            _addressed = false;

            // The presence pulse: a pause, then hold the line down so the master can hear it.
            _driveFrom = now + PresenceDelay;
            _driveUntil = now + PresenceDelay + PresenceLength;
            return;
        }

        if (_selfDrove)
        {
            _selfDrove = false;
            return;
        }

        if (_phase == Phase.Sending) return;

        // Otherwise it was a write slot, and the bit is how long the master held it: a short pull
        // is a one, a long one is a zero. There is no clock to tell them apart.
        var bit = lowSeconds < BitThreshold ? 1 : 0;

        _shift |= bit << _bits;

        if (++_bits < 8) return;

        var value = _shift;
        _shift = 0;
        _bits = 0;

        OnByte(now, value);
    }

    private void OnByte(double now, int value)
    {
        if (_phase == Phase.WriteScratchpad)
        {
            // TH, TL and the configuration byte, in that order.
            if (_writeIndex < 3) _scratchpad[2 + _writeIndex] = value & 0xFF;

            if (_writeIndex == 2) Resolution = 9 + ((value >> 5) & 0x03);

            if (++_writeIndex >= 3)
            {
                _phase = Phase.Function;
                UpdateCrc();
            }

            return;
        }

        // Every exchange is a ROM command and then a function command, in that order. A function
        // command sent without one — forgetting the 0xCC — is ignored outright, and a part that
        // answers nothing at all is the symptom.
        if (_phase == Phase.Command)
        {
            switch (value)
            {
                case 0xCC:  // Skip ROM: there is only one of us on this wire.
                    _addressed = true;
                    _phase = Phase.Function;
                    break;

                case 0x33:  // Read ROM: the family code, the serial number and their CRC.
                    _addressed = true;
                    BeginSending(_rom, 8);
                    break;
            }

            return;
        }

        switch (value)
        {
            case 0x44:  // Convert T.
                StartConversion(now);
                break;

            case 0xBE:  // Read scratchpad.
                BeginSending(_scratchpad, 9);
                break;

            case 0x4E:  // Write scratchpad.
                _phase = Phase.WriteScratchpad;
                _writeIndex = 0;
                break;
        }
    }

    private void StartConversion(double now)
    {
        IsConverting = true;
        ConversionBrownedOut = false;
        _convertingUntil = now + ConversionSeconds;
        _lineLowestWhileConverting = double.PositiveInfinity;
    }

    /// <summary>
    /// Watches the line while a conversion runs and finishes it when the time is up. On parasitic
    /// power the current drawn above pulls the line down; if it ever sagged too far, the chip did
    /// not survive to the end and the scratchpad keeps whatever it had.
    /// </summary>
    private void FinishConversion(double now, double line)
    {
        if (!IsConverting) return;

        _lineLowestWhileConverting = Math.Min(_lineLowestWhileConverting, line);

        if (now < _convertingUntil) return;

        IsConverting = false;

        if (IsParasiticallyPowered && _lineLowestWhileConverting < MinimumSupplyVoltage)
        {
            ConversionBrownedOut = true;
            return;
        }

        StoreTemperature();
        HasConverted = true;
    }

    /// <summary>Puts the current temperature into the scratchpad, rounded to the resolution set.</summary>
    private void StoreTemperature()
    {
        var step = ResolutionStep;
        var quantised = Math.Round(Math.Clamp(Temperature, -55, 125) / step) * step;
        var raw = (short)Math.Round(quantised / 0.0625);

        _scratchpad[0] = raw & 0xFF;
        _scratchpad[1] = (raw >> 8) & 0xFF;

        UpdateCrc();
    }

    private void BeginSending(int[] source, int count)
    {
        _phase = Phase.Sending;
        _sending = source;
        _sendIndex = 0;
        _sendCount = count;
        _sendBit = 0;
    }

    /// <summary>
    /// A read slot: the master tugs the line down and lets go, and the device holds it down for a
    /// while longer to say zero, or does nothing at all to say one.
    /// </summary>
    private void SendNextBit(double now)
    {
        if (_sendIndex >= _sendCount)
        {
            _phase = Phase.Function;
            return;
        }

        var bit = (_sending[_sendIndex] >> _sendBit) & 1;

        if (bit == 0)
        {
            _driveFrom = now;
            _driveUntil = now + SlaveBitLow;
        }

        if (++_sendBit == 8)
        {
            _sendBit = 0;
            _sendIndex++;
        }
    }

    private void ResetScratchpad()
    {
        _scratchpad[0] = PowerOnReading & 0xFF;
        _scratchpad[1] = (PowerOnReading >> 8) & 0xFF;
        _scratchpad[2] = 0x4B;   // TH, +75 °C
        _scratchpad[3] = 0x46;   // TL, +70 °C
        _scratchpad[4] = 0x7F;   // config: twelve bits
        _scratchpad[5] = 0xFF;
        _scratchpad[6] = 0x0C;
        _scratchpad[7] = 0x10;

        UpdateCrc();
    }

    private void UpdateCrc() => _scratchpad[8] = Crc8(_scratchpad, 8);

    /// <summary>
    /// The Dallas CRC-8, which is what the ninth byte is for. Worth having right rather than
    /// stubbed: checking it is how a reading corrupted by bad timing is told from a real one, and
    /// a model that always returned zero would make that impossible to demonstrate.
    /// </summary>
    public static int Crc8(IReadOnlyList<int> bytes, int count)
    {
        var crc = 0;

        for (var i = 0; i < count; i++)
        {
            var value = bytes[i] & 0xFF;

            for (var bit = 0; bit < 8; bit++)
            {
                var mix = (crc ^ value) & 0x01;
                crc >>= 1;

                if (mix != 0) crc ^= 0x8C;

                value >>= 1;
            }
        }

        return crc & 0xFF;
    }

    /// <summary>The end of a conversion is a moment nothing else in the circuit knows about.</summary>
    public double? NextBreakpointAfter(double time) =>
        IsConverting && _convertingUntil > time ? _convertingUntil : null;

    public override void ResetLogic()
    {
        base.ResetLogic();

        _previousHigh = true;
        _fellAt = double.NegativeInfinity;
        _driveFrom = double.PositiveInfinity;
        _driveUntil = double.NegativeInfinity;

        _phase = Phase.Command;
        _shift = 0;
        _bits = 0;
        _sendIndex = 0;
        _sendBit = 0;
        _sendCount = 0;
        _writeIndex = 0;
        _addressed = false;
        _selfDrove = false;
        _sending = [];

        IsConverting = false;
        HasConverted = false;
        ConversionBrownedOut = false;
        _convertingUntil = double.NegativeInfinity;
        _lineLowestWhileConverting = double.PositiveInfinity;

        ResetScratchpad();
    }

    partial void OnTemperatureChanged(double value) => NotifyValueChanged();

    partial void OnResolutionChanged(int value)
    {
        _scratchpad[4] = 0x1F | ((Math.Clamp(value, 9, 12) - 9) << 5);
        UpdateCrc();
        NotifyValueChanged();
    }
}
