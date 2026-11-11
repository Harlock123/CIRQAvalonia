using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Processors;

/// <summary>
/// The General Instrument AY-3-8910 programmable sound generator: three square-wave tone channels, a noise generator, a
/// mixer and an envelope, in a 40-pin DIP, the sound of the ZX Spectrum 128, the MSX and a great many arcade boards.
/// <para>
/// The bus is DA0-DA7 with BDIR, BC2 and BC1 saying what's happening on it: latch an address (BDIR, BC2, BC1 = 1 1 1, or
/// 0 0 1, or 1 0 0), write the register latched (1 1 0) or read it (0 1 1); the rest is inactive. A register is written
/// as BDIR falls at the end of a write. With BC2 tied high, as it usually is, BDIR and BC1 alone do it all.
/// </para>
/// <para>
/// Registers 0-5 are the three channels' 12-bit tone periods, f = clock / (16 · period); 6 the 5-bit noise period; 7 the
/// mixer (bits 0-2 turn the tones off, 3-5 the noise, a 1 off); 8-10 the channels' 4-bit levels, or bit 4 for the
/// envelope; 11 and 12 the 16-bit envelope period (a step every 256 · period / clock, sixteen to a ramp); 13 the
/// envelope's shape (CONTINUE, ATTACK, ALTERNATE, HOLD), starting it again when written; 14 and 15 the I/O ports, held
/// but not brought out to their pins. Each output is a 16-step logarithmic DAC, 3 dB a step, one volt at full level
/// behind 1 kΩ. The clock is <see cref="ClockFrequency"/>: the oscillator on CLOCK is taken as read.
/// </para>
/// </summary>
public sealed partial class Ay38910 : DigitalIc, IBreakpointSource
{
    private static readonly double[] Levels16 = [.. Enumerable.Range(0, 16).Select(i => i == 0 ? 0 : Math.Pow(Math.Sqrt(0.5), 15 - i))];

    private readonly Terminal[] _data = new Terminal[8];
    private readonly byte[] _registers = new byte[16];
    private int _address;
    private bool _selected = true;
    private int _mode;              // 0 inactive, 1 latch, 2 write, 3 read
    private byte _writing;

    // The generators, counted in ticks of eight clocks.
    private long _tick;
    private readonly int[] _toneCount = new int[3];
    private readonly bool[] _toneOut = new bool[3];
    private int _noiseCount;
    private int _lfsr = 1;
    private bool _noiseOut;
    private int _envCount, _envStep;
    private bool _envAttack, _envHold, _envAlternate;
    private int _envLevel;

    public Ay38910() : base(40)
    {
        PropagationDelay = 50e-9;
        var pins = new Terminal[41];
        Terminal Pin(int n, string name, TerminalType type) => pins[n] = new Terminal($"p{n}", name, type, DipPackage.PinOffset(n, 40));
        Gnd = Pin(1, "VSS", TerminalType.Ground);
        Pin(2, "NC", TerminalType.Passive);
        ChannelB = Pin(3, "CHB", TerminalType.Output);
        ChannelA = Pin(4, "CHA", TerminalType.Output);
        Pin(5, "NC", TerminalType.Passive);
        for (var i = 0; i < 8; i++) Pin(6 + i, $"IOB{7 - i}", TerminalType.Passive);
        for (var i = 0; i < 8; i++) Pin(14 + i, $"IOA{7 - i}", TerminalType.Passive);
        Clock = Pin(22, "CLOCK", TerminalType.Input);
        Reset = Pin(23, "RESET", TerminalType.Input);
        Pin(24, "A9", TerminalType.Input);
        Pin(25, "A8", TerminalType.Input);
        Pin(26, "TEST2", TerminalType.Passive);
        Bdir = Pin(27, "BDIR", TerminalType.Input);
        Bc2 = Pin(28, "BC2", TerminalType.Input);
        Bc1 = Pin(29, "BC1", TerminalType.Input);
        for (var i = 0; i < 8; i++) _data[7 - i] = Pin(30 + i, $"DA{7 - i}", TerminalType.Bidirectional);
        ChannelC = Pin(38, "CHC", TerminalType.Output);
        Pin(39, "TEST1", TerminalType.Passive);
        Vcc = Pin(40, "VCC", TerminalType.Power);
        Terminals = [.. pins.Skip(1)];
        ConfigurePins([Bdir, Bc2, Bc1, Reset, .. _data], [.. _data]);
        ResetChip();
    }

    public Terminal ChannelA { get; }
    public Terminal ChannelB { get; }
    public Terminal ChannelC { get; }
    public Terminal Clock { get; }
    public Terminal Reset { get; }
    public Terminal Bdir { get; }
    public Terminal Bc2 { get; }
    public Terminal Bc1 { get; }

    /// <summary>DA0-DA7, DA0 first.</summary>
    public IReadOnlyList<Terminal> Data => _data;

    /// <summary>The clock on CLOCK, in hertz: 1 MHz to 2 MHz on most boards.</summary>
    [ObservableProperty]
    public partial double ClockFrequency { get; set; } = 1e6;

    public override string PartNumber => "AY-3-8910";

    public override string ComponentType => "AY-3-8910";

    public override bool LabelsPins => true;

    public override int VoltageSourceCount => base.VoltageSourceCount + 3;

    public IReadOnlyList<byte> Registers => _registers;

    /// <summary>A channel's tone frequency, in hertz, as its period register sets it.</summary>
    public double ToneFrequency(int channel) => ClockFrequency / (16.0 * TonePeriod(channel));

    private int TonePeriod(int channel) => Math.Max(1, _registers[channel * 2] | ((_registers[(channel * 2) + 1] & 0x0F) << 8));

    private int NoisePeriod => Math.Max(1, _registers[6] & 0x1F);

    private int EnvelopePeriod => Math.Max(1, _registers[11] | (_registers[12] << 8));

    /// <summary>A channel's output level, 0 to 1.</summary>
    public double Level(int channel)
    {
        var mixer = _registers[7];
        var tone = _toneOut[channel] || (mixer & (1 << channel)) != 0;
        var noise = _noiseOut || (mixer & (8 << channel)) != 0;
        if (!(tone && noise)) return 0;
        var amplitude = _registers[8 + channel];
        return Levels16[(amplitude & 0x10) != 0 ? _envLevel : amplitude & 0x0F];
    }

    private void ResetChip()
    {
        Array.Clear(_registers);
        _registers[7] = 0xFF;
        _address = 0;
        Array.Clear(_toneCount);
        Array.Clear(_toneOut);
        (_noiseCount, _lfsr, _noiseOut, _envCount, _tick) = (0, 1, false, 0, 0);
        StartEnvelope();
    }

    private void StartEnvelope()
    {
        _envStep = 0;
        _envCount = 0;
        _envAttack = (_registers[13] & 0x04) != 0;
        _envHold = false;
        _envAlternate = false;
        _envLevel = _envAttack ? 0 : 15;
    }

    /// <summary>Eight clocks on: the tone and noise counters, and the envelope's.</summary>
    private void Tick()
    {
        for (var c = 0; c < 3; c++)
            if (++_toneCount[c] >= TonePeriod(c))
            {
                _toneCount[c] = 0;
                _toneOut[c] = !_toneOut[c];
            }

        if (++_noiseCount >= NoisePeriod * 2)
        {
            _noiseCount = 0;
            var bit = (_lfsr ^ (_lfsr >> 3)) & 1;
            _lfsr = (_lfsr >> 1) | (bit << 16);
            _noiseOut = (_lfsr & 1) != 0;
        }

        if (++_envCount >= EnvelopePeriod * 32)
        {
            _envCount = 0;
            StepEnvelope();
        }

        _tick++;
    }

    private void StepEnvelope()
    {
        if (_envHold) return;
        var shape = _registers[13];
        if (++_envStep < 16)
        {
            var rising = _envAttack != _envAlternate;
            _envLevel = rising ? _envStep : 15 - _envStep;
            return;
        }

        // The end of a ramp: CONTINUE, ATTACK, ALTERNATE and HOLD say what comes next.
        _envStep = 0;
        if ((shape & 0x08) == 0)
        {
            _envHold = true;
            _envLevel = 0;
            return;
        }

        if ((shape & 0x01) != 0)
        {
            _envHold = true;
            var rising = _envAttack != _envAlternate;
            _envLevel = ((shape & 0x02) != 0) == rising ? 0 : 15;
            return;
        }

        if ((shape & 0x02) != 0) _envAlternate = !_envAlternate;
        var up = _envAttack != _envAlternate;
        _envLevel = up ? 0 : 15;
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);
        var gnd = system.Node(Gnd);
        var channels = new[] { ChannelA, ChannelB, ChannelC };
        for (var c = 0; c < 3; c++)
            system.StampTheveninSource(system.Branch(this, base.VoltageSourceCount + c), system.Node(channels[c]), gnd, Level(c), 1e3);
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        if (context.ReadInput(Reset, Levels).IsLow())
        {
            ResetChip();
            foreach (var (_, i) in _data.Select((d, i) => (d, i))) Request(context, i, LogicState.HighImpedance, 0);
            return;
        }

        // The generators, up to now.
        var target = (long)Math.Floor((context.Time * ClockFrequency / 8) + 1e-6);
        for (var guard = 0; _tick < target && guard < 1_000_000; guard++) Tick();
        if (_tick < target) _tick = target;

        var bdir = context.ReadInput(Bdir, Levels).IsHigh();
        var bc2 = context.ReadInput(Bc2, Levels).IsHigh();
        var bc1 = context.ReadInput(Bc1, Levels).IsHigh();
        var mode = (bdir, bc2, bc1) switch
        {
            (true, true, true) or (false, false, true) or (true, false, false) => 1,
            (true, true, false) => 2,
            (false, true, true) => 3,
            _ => 0,
        };

        var bus = 0;
        for (var i = 0; i < 8; i++)
            if (context.ReadInput(_data[i], Levels).IsHigh()) bus |= 1 << i;

        if (mode == 1)
        {
            _selected = (bus & 0xF0) == 0;
            _address = bus & 0x0F;
        }
        else if (mode == 2) _writing = (byte)bus;
        if (_mode == 2 && mode != 2 && _selected) Write(_address, _writing);
        _mode = mode;

        var reading = mode == 3 && _selected;
        for (var i = 0; i < 8; i++)
            Request(context, i, !reading ? LogicState.HighImpedance : ((_registers[_address] >> i) & 1) != 0 ? LogicState.High : LogicState.Low, DelayFor(context));
    }

    private void Write(int register, byte value)
    {
        _registers[register] = register switch
        {
            1 or 3 or 5 or 13 => (byte)(value & 0x0F),
            6 => (byte)(value & 0x1F),
            8 or 9 or 10 => (byte)(value & 0x1F),
            _ => value,
        };
        if (register == 13) StartEnvelope();
        NotifyValueChanged();
    }

    /// <summary>The next tick at which an output can change: a tone toggling, noise shifting, the envelope stepping.</summary>
    public double? NextBreakpointAfter(double time)
    {
        var mixer = _registers[7];
        long ahead = long.MaxValue;
        for (var c = 0; c < 3; c++)
            if ((mixer & (1 << c)) == 0 && (_registers[8 + c] & 0x1F) != 0) ahead = Math.Min(ahead, TonePeriod(c) - _toneCount[c]);
        if ((mixer & 0x38) != 0x38) ahead = Math.Min(ahead, (NoisePeriod * 2) - _noiseCount);
        if (_registers[8..11].Any(a => (a & 0x10) != 0) && !_envHold) ahead = Math.Min(ahead, (EnvelopePeriod * 32) - _envCount);
        if (ahead == long.MaxValue) return null;
        var at = (_tick + Math.Max(ahead, 1)) * 8 / ClockFrequency;
        return at > time + 1e-12 ? at : time + (8 / ClockFrequency);
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        ResetChip();
        _mode = 0;
        _selected = true;
    }
}
