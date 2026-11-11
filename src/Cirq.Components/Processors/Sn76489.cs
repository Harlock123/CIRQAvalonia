using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Processors;

/// <summary>
/// The Texas Instruments SN76489 sound chip of the BBC Micro, the ColecoVision and the Sega Master System: three square
/// tone channels and a noise channel, each with a 4-bit attenuator, mixed onto one AUDIO OUT. It's written, never read:
/// a byte on D0-D7 is taken while /CE and /WE are both low. TI numbered the bus the other way round, so D0 is the most
/// significant bit. A latch byte (top bit 1) holds a register (bits 6-4: channel and whether it's the tone or the
/// attenuation) and four bits of data; a data byte (top bit 0) after it gives a tone the top six bits of its 10-bit
/// period. A tone is clock / (32 · period); attenuation is 2 dB a step, 15 off; the noise (register 6) is white or
/// periodic, its shift rate clock/512, /1024, /2048 or tone 3's. AUDIO OUT is the four channels' levels added, a volt
/// at full on each, behind 1 kΩ. The clock is <see cref="ClockFrequency"/> (3.579545 MHz, an NTSC colourburst crystal);
/// READY stays high.
/// </summary>
public sealed partial class Sn76489 : DigitalIc, IBreakpointSource
{
    private readonly Terminal[] _d = new Terminal[8];
    private readonly int[] _tone = new int[3];
    private readonly int[] _attenuation = [15, 15, 15, 15];
    private readonly int[] _count = new int[4];
    private readonly bool[] _out = new bool[4];
    private int _noise;
    private int _lfsr = 0x8000;
    private int _latched;
    private long _tick;
    private bool _wasWriting;

    public Sn76489() : base(16)
    {
        PropagationDelay = 100e-9;
        var pins = new Terminal[17];
        Terminal Pin(int n, string name, TerminalType type) => pins[n] = new Terminal($"p{n}", name, type, DipPackage.PinOffset(n, 16));
        _d[2] = Pin(1, "D2", TerminalType.Input);
        _d[1] = Pin(2, "D1", TerminalType.Input);
        _d[0] = Pin(3, "D0", TerminalType.Input);
        Ready = Pin(4, "READY", TerminalType.Output);
        WriteEnable = Pin(5, "WE", TerminalType.Input);
        ChipEnable = Pin(6, "CE", TerminalType.Input);
        Audio = Pin(7, "AUDIO OUT", TerminalType.Output);
        Gnd = Pin(8, "GND", TerminalType.Ground);
        Pin(9, "NC", TerminalType.Passive);
        _d[7] = Pin(10, "D7", TerminalType.Input);
        _d[6] = Pin(11, "D6", TerminalType.Input);
        _d[5] = Pin(12, "D5", TerminalType.Input);
        _d[4] = Pin(13, "D4", TerminalType.Input);
        Clock = Pin(14, "CLOCK", TerminalType.Input);
        _d[3] = Pin(15, "D3", TerminalType.Input);
        Vcc = Pin(16, "VCC", TerminalType.Power);
        Terminals = [.. pins.Skip(1)];
        ConfigurePins([WriteEnable, ChipEnable, .. _d], [Ready]);
    }

    public Terminal Ready { get; }
    public Terminal WriteEnable { get; }
    public Terminal ChipEnable { get; }
    public Terminal Audio { get; }
    public Terminal Clock { get; }

    /// <summary>D0-D7 in TI's order: D0 is the most significant bit.</summary>
    public IReadOnlyList<Terminal> Data => _d;

    [ObservableProperty]
    public partial double ClockFrequency { get; set; } = 3.579545e6;

    public override string PartNumber => "SN76489";

    public override bool LabelsPins => true;

    public override string PinLabel(Terminal pin) => pin.Name is "WE" or "CE" ? "/" + pin.Name : pin.Name;

    public override int VoltageSourceCount => base.VoltageSourceCount + 1;

    public int TonePeriod(int channel) => _tone[channel];

    public int Attenuation(int channel) => _attenuation[channel];

    public double ToneFrequency(int channel) => ClockFrequency / (32.0 * Math.Max(_tone[channel], 1));

    /// <summary>The output now, in volts, before its 1 kΩ.</summary>
    public double Level
    {
        get
        {
            var sum = 0.0;
            for (var c = 0; c < 4; c++)
            {
                var high = c < 3 ? (_out[c] || _tone[c] <= 1) : (_lfsr & 1) != 0;
                if (high && _attenuation[c] < 15) sum += Math.Pow(10, -2.0 * _attenuation[c] / 20);
            }

            return sum;
        }
    }

    // A tick is 16 clocks: a tone's counter steps once a tick and its output flips at zero, so a period of N makes clock / (32·N).
    private void Tick()
    {
        for (var c = 0; c < 3; c++)
            if (--_count[c] <= 0)
            {
                _count[c] = Math.Max(_tone[c], 1);
                _out[c] = !_out[c];
                if (c == 2 && (_noise & 3) == 3) Shift();
            }

        if ((_noise & 3) != 3 && --_count[3] <= 0)
        {
            _count[3] = 16 << (_noise & 3);       // /512, /1024, /2048 in ticks of 16 clocks, counted twice a cycle
            _out[3] = !_out[3];
            if (_out[3]) Shift();
        }

        _tick++;
    }

    private void Shift()
    {
        var feedback = (_noise & 4) != 0 ? ((_lfsr & 1) ^ ((_lfsr >> 3) & 1)) : (_lfsr & 1);
        _lfsr = (_lfsr >> 1) | (feedback << 15);
    }

    private void Write(int b)
    {
        if ((b & 0x80) != 0)
        {
            _latched = (b >> 4) & 7;
            var data = b & 0x0F;
            var channel = _latched >> 1;
            if ((_latched & 1) != 0) _attenuation[channel] = data;
            else if (channel < 3) _tone[channel] = (_tone[channel] & 0x3F0) | data;
            else
            {
                _noise = data & 7;
                _lfsr = 0x8000;
            }
        }
        else
        {
            var channel = _latched >> 1;
            if ((_latched & 1) != 0) _attenuation[channel] = b & 0x0F;
            else if (channel < 3) _tone[channel] = (_tone[channel] & 0x0F) | ((b & 0x3F) << 4);
            else _noise = b & 7;
        }

        NotifyValueChanged();
    }

    public double? NextBreakpointAfter(double time)
    {
        var ahead = int.MaxValue;
        for (var c = 0; c < 3; c++)
            if (_attenuation[c] < 15 && _tone[c] > 1) ahead = Math.Min(ahead, _count[c]);
        if (_attenuation[3] < 15) ahead = Math.Min(ahead, (_noise & 3) == 3 ? _count[2] : _count[3]);
        if (ahead == int.MaxValue) return null;
        var at = (_tick + Math.Max(ahead, 1)) * 16 / ClockFrequency;
        return at > time + 1e-12 ? at : time + (16 / ClockFrequency);
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);
        system.StampTheveninSource(system.Branch(this, base.VoltageSourceCount), system.Node(Audio), system.Node(Gnd), Level, 1e3);
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var target = (long)Math.Floor((context.Time * ClockFrequency / 16) + 1e-6);
        for (var guard = 0; _tick < target && guard < 1_000_000; guard++) Tick();
        if (_tick < target) _tick = target;

        var writing = context.ReadInput(ChipEnable, Levels).IsLow() && context.ReadInput(WriteEnable, Levels).IsLow();
        if (writing && !_wasWriting && !context.IsInitializing)
        {
            var b = 0;
            for (var i = 0; i < 8; i++)
                if (context.ReadInput(_d[i], Levels).IsHigh()) b |= 0x80 >> i;
            Write(b);
        }

        _wasWriting = writing;
        Request(context, 0, LogicState.High, DelayFor(context));
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        Array.Clear(_tone);
        Array.Fill(_attenuation, 15);
        Array.Clear(_count);
        Array.Clear(_out);
        (_noise, _lfsr, _latched, _tick, _wasWriting) = (0, 0x8000, 0, 0, false);
    }
}
