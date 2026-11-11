using System.Globalization;
using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Processors;

/// <summary>
/// The Atmel AT89C2051, an 8051 in a 20-pin DIP with 2 KB of flash: P1.0-P1.7 and P3.0-P3.5 and P3.7 as quasi-bidirectional
/// ports (a 1 written lets the pin be pulled up through about 50 kΩ, a 0 holds it low), P3.0 and P3.1 as RXD and TXD,
/// P3.2 and P3.3 as INT0 and INT1, P3.4 and P3.5 as T0 and T1, RST active high. Firmware is an Intel HEX file from
/// SDCC, Keil or an assembler, loaded with Load Memory Image and kept with the circuit; it runs at
/// <see cref="ClockFrequency"/> (12 clocks to a machine cycle) the same way the ATmega328P does, ahead of the circuit by
/// a short window and stopping at each change of a pin. The analog comparator on P1.0 and P1.1 isn't modelled.
/// </summary>
public sealed partial class At89c2051 : DigitalIc, IBreakpointSource, IRunState, IFirmwareChip
{
    public const double PullUpResistance = 50e3;

    private readonly I8051Core _core = new();
    private readonly Terminal[] _p1 = new Terminal[8];
    private readonly Terminal[] _p3 = new Terminal[7];
    private static readonly int[] P3Bits = [0, 1, 2, 3, 4, 5, 7];
    private LogicState[] _driving = new LogicState[15];
    private double _cpuTime;
    private bool _fresh = true;

    public At89c2051() : base(20)
    {
        PropagationDelay = 10e-9;
        Levels = LogicLevels.Ttl with { Voh = 4.5, Vol = 0.3 };
        var pins = new Terminal[21];
        Terminal Pin(int n, string name, TerminalType type) => pins[n] = new Terminal($"p{n}", name, type, DipPackage.PinOffset(n, 20));
        Reset = Pin(1, "RST", TerminalType.Input);
        _p3[0] = Pin(2, "P3.0/RXD", TerminalType.Bidirectional);
        _p3[1] = Pin(3, "P3.1/TXD", TerminalType.Bidirectional);
        Pin(4, "XTAL2", TerminalType.Passive);
        Pin(5, "XTAL1", TerminalType.Passive);
        _p3[2] = Pin(6, "P3.2/INT0", TerminalType.Bidirectional);
        _p3[3] = Pin(7, "P3.3/INT1", TerminalType.Bidirectional);
        _p3[4] = Pin(8, "P3.4/T0", TerminalType.Bidirectional);
        _p3[5] = Pin(9, "P3.5/T1", TerminalType.Bidirectional);
        Gnd = Pin(10, "GND", TerminalType.Ground);
        _p3[6] = Pin(11, "P3.7", TerminalType.Bidirectional);
        for (var i = 0; i < 8; i++) _p1[i] = Pin(12 + i, i < 2 ? $"P1.{i}/AIN{i}" : $"P1.{i}", TerminalType.Bidirectional);
        Vcc = Pin(20, "VCC", TerminalType.Power);
        Terminals = [.. pins.Skip(1)];
        ConfigurePins([Reset, .. _p1, .. _p3], [.. _p1, .. _p3]);
        _core.Reset();
    }

    public Terminal Reset { get; }

    public IReadOnlyList<Terminal> Port1 => _p1;

    /// <summary>P3.0-P3.5 and P3.7 (there is no P3.6 pin).</summary>
    public IReadOnlyList<Terminal> Port3 => _p3;

    public I8051Core Core => _core;

    /// <summary>The crystal, in hertz: 11.0592 MHz gives the standard baud rates exactly.</summary>
    [ObservableProperty]
    public partial double ClockFrequency { get; set; } = 11.0592e6;

    /// <summary>The firmware, as Intel HEX text.</summary>
    [ObservableProperty]
    public partial string Firmware { get; set; } = string.Empty;

    public string? FirmwareError { get; private set; }

    partial void OnFirmwareChanged(string value)
    {
        try
        {
            var image = value.Trim().Length == 0 ? new MemoryImage([], "none") : MemoryImage.FromIntelHex(value);
            _core.LoadCode(image.Blocks);
            FirmwareError = null;
        }
        catch (Exception e) when (e is InvalidDataException or FormatException)
        {
            FirmwareError = e.Message;
        }

        _core.Reset();
        NotifyValueChanged();
    }

    public override string PartNumber => "AT89C2051";

    public override string ComponentType => "AT89C2051";

    public override bool LabelsPins => true;

    public override string ValueLabel => FirmwareError is not null ? "bad firmware"
        : Firmware.Trim().Length == 0 ? "no firmware"
        : string.Create(CultureInfo.InvariantCulture, $"PC ${_core.Pc:X4}");

    /// <summary>Loads a firmware image and keeps it as <see cref="Firmware"/>.</summary>
    public string Load(MemoryImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.ByteCount == 0) throw new InvalidDataException("The image has no bytes in it.");
        if (image.Blocks.Any(b => b.Address + b.Bytes.Length > 2048))
            throw new InvalidDataException("The image is bigger than the AT89C2051's 2 KB of flash.");
        var hex = new System.Text.StringBuilder();
        foreach (var (address, bytes) in image.Blocks)
        {
            var text = MemoryImage.ToIntelHex(address, bytes);
            var end = text.LastIndexOf(":00000001FF", StringComparison.Ordinal);
            hex.Append(end >= 0 ? text[..end] : text);
        }

        hex.AppendLine(":00000001FF");
        Firmware = hex.ToString();
        if (FirmwareError is { } error) throw new InvalidDataException(error);
        return $"{image.ByteCount} bytes of firmware";
    }

    private double MachineCycle => 12.0 / Math.Max(ClockFrequency, 1);

    /// <summary>How far ahead it runs between samples: 200 µs, or an eighth of a bit while the UART listens.</summary>
    public double Window => (_core[I8051Core.SCON] & 0x10) != 0 ? Math.Clamp(_core.BitCycles * MachineCycle / 8, 1e-6, 200e-6) : 200e-6;

    public double? NextBreakpointAfter(double time) => _cpuTime > time + 1e-12 ? _cpuTime : time + (_fresh ? 1e-9 : Window);

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);
        var vcc = system.Node(Vcc);
        system.StampResistor(system.Node(Reset), system.Node(Gnd), PullUpResistance);   // RST's internal pull-down
        for (var i = 0; i < 15; i++)
            if (Latch(i)) system.StampResistor(system.Node(Pin(i)), vcc, PullUpResistance);
    }

    private Terminal Pin(int i) => i < 8 ? _p1[i] : _p3[i - 8];

    /// <summary>Whether a pin's latch holds a 1 (P3.1 while sending: the UART's line).</summary>
    private bool Latch(int i) => i < 8 ? ((_core.P1Latch >> i) & 1) != 0
        : i == 9 ? _core.TxLevel
        : ((_core.P3Latch >> P3Bits[i - 8]) & 1) != 0;

    private LogicState Level(int i) => Latch(i) ? LogicState.HighImpedance : LogicState.Low;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var time = context.Time;
        if (context.IsInitializing || context.ReadInput(Reset, Levels).IsHigh())
        {
            if (!context.IsInitializing) _core.Reset();
            _cpuTime = time;
            _fresh = true;
            Drive(context, time);
            return;
        }

        if (_cpuTime > time + 1e-12) return;
        _fresh = false;
        Sample(context);
        var limit = time + Window;
        if (_cpuTime < time - 0.1) _cpuTime = time - 0.1;
        var changed = false;
        while (_cpuTime < limit && !changed)
        {
            _cpuTime += _core.Step() * MachineCycle;
            changed = Enumerable.Range(0, 15).Any(i => Level(i) != _driving[i]);
        }

        Drive(context, time);
        NotifyValueChanged();
    }

    private void Sample(IDigitalContext context)
    {
        byte p1 = 0, p3 = 0x40;
        for (var i = 0; i < 8; i++)
            if (context.ReadInput(_p1[i], Levels).IsHigh()) p1 |= (byte)(1 << i);
        for (var i = 0; i < 7; i++)
            if (context.ReadInput(_p3[i], Levels).IsHigh()) p3 |= (byte)(1 << P3Bits[i]);
        _core.P1Pins = p1;
        _core.P3Pins = p3;
        _core.SampleEdges();
    }

    private void Drive(IDigitalContext context, double now)
    {
        var delay = Math.Max(0, _cpuTime - now);
        for (var i = 0; i < 15; i++)
        {
            var level = Level(i);
            if (level == _driving[i]) continue;
            _driving[i] = level;
            Request(context, i, level, delay);
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _core.Reset();
        _cpuTime = 0;
        _fresh = true;
        _driving = new LogicState[15];
        Array.Fill(_driving, LogicState.Unknown);
    }

    public object CaptureRunState() => (_core.Pc, Enumerable.Range(0, 256).Select(a => _core[a]).ToArray());

    public void AdoptRunState(object state)
    {
        if (state is not (int pc, byte[] memory)) return;
        _core.Pc = pc;
        for (var a = 0; a < 256; a++) _core[a] = memory[a];
        NotifyValueChanged();
    }
}
