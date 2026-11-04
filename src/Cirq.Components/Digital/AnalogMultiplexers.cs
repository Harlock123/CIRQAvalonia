using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

/// <summary>
/// Base for the CMOS analog multiplexers: the 4051 (one of eight), the 4052 (two of four), the 4053
/// (three single-pole changeovers) and the 4067 (one of sixteen). Each section has a common pin and a
/// set of channels, and its address picks which one channel is joined to the common through the switch
/// resistance; the rest are open. INHIBIT, active high, opens everything.
/// <para>
/// The paths are bilateral: real resistances stamped into the analog solve, with no input or output
/// end, so the same part reads several sensors into one ADC pin or fans one signal out to several
/// places, depending only on which end is driven. The address and inhibit pins are the only logic.
/// </para>
/// <para>
/// A real part has VEE for passing signals below ground; here it is a pin to tie off (the 4067 has
/// none), and the switch conducts wherever the signal sits.
/// </para>
/// </summary>
public abstract partial class AnalogMultiplexerIc : DigitalIc
{
    /// <summary>One section: its common pin, its channels in address order, and the address bits that pick among them.</summary>
    protected sealed record MuxSection(Terminal Common, Terminal[] Channels, Terminal[] Address);

    private MuxSection[] _sections = [];
    private int[] _selected = [];

    protected AnalogMultiplexerIc(int pinCount) : base(pinCount)
    {
        PropagationDelay = 200e-9;
        Levels = LogicLevels.Cmos5V;
    }

    /// <summary>Resistance of a selected channel, in ohms.</summary>
    [ObservableProperty]
    public partial double OnResistance { get; set; } = 125.0;

    /// <summary>Resistance of an unselected channel, in ohms.</summary>
    [ObservableProperty]
    public partial double OffResistance { get; set; } = 1e9;

    /// <summary>Active high: disconnects every channel.</summary>
    public Terminal Inhibit { get; private set; } = null!;

    /// <summary>The number of switch sections.</summary>
    public int SectionCount => _sections.Length;

    /// <summary>A section's common pin.</summary>
    public Terminal CommonOf(int section) => _sections[section].Common;

    /// <summary>One of a section's channel pins, in address order.</summary>
    public Terminal ChannelOf(int section, int channel) => _sections[section].Channels[channel];

    /// <summary>The channels a section has.</summary>
    public int ChannelCount(int section) => _sections[section].Channels.Length;

    /// <summary>Which channel a section has joined to its common, or -1 while inhibited.</summary>
    public int SelectedChannelOf(int section) => _selected[section];

    public override string ValueLabel =>
        string.Join(" / ", _selected.Select(s => s < 0 ? "off" : $"ch{s}"));

    /// <summary>Hands the base the sections, the inhibit pin and the address pins in bit order, once the part has set its Terminals.</summary>
    protected void Configure(Terminal inhibit, Terminal[] address, params MuxSection[] sections)
    {
        _sections = sections;
        _selected = [.. sections.Select(_ => -1)];
        Inhibit = inhibit;

        // The address and inhibit pins are the only logic inputs, and there are no logic outputs:
        // the channel paths are stamped as resistances rather than driven, as on the 4066.
        ConfigurePins([.. address, inhibit], []);
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);

        for (var s = 0; s < _sections.Length; s++)
        {
            var section = _sections[s];
            var common = system.Node(section.Common);

            for (var i = 0; i < section.Channels.Length; i++)
            {
                system.StampResistor(
                    common, system.Node(section.Channels[i]),
                    Math.Max(i == _selected[s] ? OnResistance : OffResistance, 1e-3));
            }
        }
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var inhibited = context.ReadInput(Inhibit, Levels) == LogicState.High;

        for (var s = 0; s < _sections.Length; s++)
        {
            if (inhibited)
            {
                _selected[s] = -1;
                continue;
            }

            var address = 0;
            var bits = _sections[s].Address;
            for (var b = 0; b < bits.Length; b++)
                if (context.ReadInput(bits[b], Levels) == LogicState.High) address |= 1 << b;

            _selected[s] = address;
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        Array.Fill(_selected, -1);
    }
}

/// <summary>
/// A 4051 single eight-channel analog multiplexer and demultiplexer.
/// <para>
/// The 4066 with an address decoder in front of it: three address pins pick one of eight channels
/// and connect it to the common pin, and the other seven are left open. Like the 4066 the path is
/// bilateral, so the same part reads eight sensors into one ADC pin or fans one signal out to
/// eight places, depending only on which end you drive.
/// </para>
/// <para>
/// Inhibit is active high and disconnects everything, which is what you use to park it or to gang
/// several of them onto one bus. Exactly one channel is connected at a time, so unlike four
/// separate 4066 switches there is no way to short two sources together by accident.
/// </para>
/// <para>
/// Pinout: 1=CH4, 2=CH6, 3=COM, 4=CH7, 5=CH5, 6=INH, 7=VEE, 8=VSS,
/// 9=C, 10=B, 11=A, 12=CH3, 13=CH0, 14=CH1, 15=CH2, 16=VDD.
/// </para>
/// </summary>
public sealed class Ic4051 : AnalogMultiplexerIc
{
    private readonly Terminal[] _channels = new Terminal[8];

    public Ic4051() : base(16)
    {
        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        _channels[4] = Pin(1, "CH4", TerminalType.Bidirectional);
        _channels[6] = Pin(2, "CH6", TerminalType.Bidirectional);
        Common = Pin(3, "COM", TerminalType.Bidirectional);
        _channels[7] = Pin(4, "CH7", TerminalType.Bidirectional);
        _channels[5] = Pin(5, "CH5", TerminalType.Bidirectional);
        var inhibit = Pin(6, "INH", TerminalType.Input);
        NegativeSupply = Pin(7, "VEE", TerminalType.Passive);
        Gnd = Pin(8, "VSS", TerminalType.Ground);
        C = Pin(9, "C", TerminalType.Input);
        B = Pin(10, "B", TerminalType.Input);
        A = Pin(11, "A", TerminalType.Input);
        _channels[3] = Pin(12, "CH3", TerminalType.Bidirectional);
        _channels[0] = Pin(13, "CH0", TerminalType.Bidirectional);
        _channels[1] = Pin(14, "CH1", TerminalType.Bidirectional);
        _channels[2] = Pin(15, "CH2", TerminalType.Bidirectional);
        Vcc = Pin(16, "VDD", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        Configure(inhibit, [A, B, C], new MuxSection(Common, _channels, [A, B, C]));
    }

    /// <summary>The pin every selected channel connects to.</summary>
    public Terminal Common { get; }

    /// <summary>Address bit 0, the least significant.</summary>
    public Terminal A { get; }

    public Terminal B { get; }

    /// <summary>Address bit 2, the most significant.</summary>
    public Terminal C { get; }

    /// <summary>The negative supply of a real part. Tie it to VSS for single-supply use.</summary>
    public Terminal NegativeSupply { get; }

    public override string PartNumber => "4051";

    /// <summary>One of the eight channel pins, indexed from 0.</summary>
    public Terminal Channel(int index) => _channels[index];

    /// <summary>Which channel is connected to <see cref="Common"/>, or -1 while inhibited.</summary>
    public int SelectedChannel => SelectedChannelOf(0);
}

/// <summary>
/// A 4052 dual four-channel analog multiplexer: two 4051s of four channels each, X and Y, sharing
/// their two address pins — so one address picks a matching pair, which is what a differential or
/// stereo signal wants.
/// <para>
/// Pinout: 1=Y0, 2=Y2, 3=Y, 4=Y3, 5=Y1, 6=INH, 7=VEE, 8=VSS, 9=B, 10=A, 11=X3, 12=X0, 13=X, 14=X1,
/// 15=X2, 16=VDD.
/// </para>
/// </summary>
public sealed class Ic4052 : AnalogMultiplexerIc
{
    public Ic4052() : base(16)
    {
        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        var y = new Terminal[4];
        var x = new Terminal[4];
        y[0] = Pin(1, "Y0", TerminalType.Bidirectional);
        y[2] = Pin(2, "Y2", TerminalType.Bidirectional);
        var commonY = Pin(3, "Y", TerminalType.Bidirectional);
        y[3] = Pin(4, "Y3", TerminalType.Bidirectional);
        y[1] = Pin(5, "Y1", TerminalType.Bidirectional);
        var inhibit = Pin(6, "INH", TerminalType.Input);
        NegativeSupply = Pin(7, "VEE", TerminalType.Passive);
        Gnd = Pin(8, "VSS", TerminalType.Ground);
        B = Pin(9, "B", TerminalType.Input);
        A = Pin(10, "A", TerminalType.Input);
        x[3] = Pin(11, "X3", TerminalType.Bidirectional);
        x[0] = Pin(12, "X0", TerminalType.Bidirectional);
        var commonX = Pin(13, "X", TerminalType.Bidirectional);
        x[1] = Pin(14, "X1", TerminalType.Bidirectional);
        x[2] = Pin(15, "X2", TerminalType.Bidirectional);
        Vcc = Pin(16, "VDD", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        Configure(inhibit, [A, B], new MuxSection(commonX, x, [A, B]), new MuxSection(commonY, y, [A, B]));
    }

    public override string PartNumber => "4052";

    /// <summary>Address bit 0.</summary>
    public Terminal A { get; }

    public Terminal B { get; }

    public Terminal NegativeSupply { get; }
}

/// <summary>
/// A 4053 triple two-channel analog multiplexer: three single-pole changeover switches, each thrown by
/// its own control pin — A for the first, B the second, C the third — and all three opened by INHIBIT.
/// Low joins a section's common to its channel 0, high to channel 1.
/// <para>
/// Pinout: 1=2CH1, 2=2CH0, 3=3CH1, 4=3COM, 5=3CH0, 6=INH, 7=VEE, 8=VSS, 9=C, 10=B, 11=A, 12=1CH0,
/// 13=1CH1, 14=1COM, 15=2COM, 16=VDD.
/// </para>
/// </summary>
public sealed class Ic4053 : AnalogMultiplexerIc
{
    public Ic4053() : base(16)
    {
        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        var s2 = new Terminal[2];
        var s3 = new Terminal[2];
        var s1 = new Terminal[2];
        s2[1] = Pin(1, "2CH1", TerminalType.Bidirectional);
        s2[0] = Pin(2, "2CH0", TerminalType.Bidirectional);
        s3[1] = Pin(3, "3CH1", TerminalType.Bidirectional);
        var common3 = Pin(4, "3COM", TerminalType.Bidirectional);
        s3[0] = Pin(5, "3CH0", TerminalType.Bidirectional);
        var inhibit = Pin(6, "INH", TerminalType.Input);
        NegativeSupply = Pin(7, "VEE", TerminalType.Passive);
        Gnd = Pin(8, "VSS", TerminalType.Ground);
        C = Pin(9, "C", TerminalType.Input);
        B = Pin(10, "B", TerminalType.Input);
        A = Pin(11, "A", TerminalType.Input);
        s1[0] = Pin(12, "1CH0", TerminalType.Bidirectional);
        s1[1] = Pin(13, "1CH1", TerminalType.Bidirectional);
        var common1 = Pin(14, "1COM", TerminalType.Bidirectional);
        var common2 = Pin(15, "2COM", TerminalType.Bidirectional);
        Vcc = Pin(16, "VDD", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        Configure(inhibit, [A, B, C],
            new MuxSection(common1, s1, [A]), new MuxSection(common2, s2, [B]), new MuxSection(common3, s3, [C]));
    }

    public override string PartNumber => "4053";

    /// <summary>Throws the first switch.</summary>
    public Terminal A { get; }

    /// <summary>Throws the second switch.</summary>
    public Terminal B { get; }

    /// <summary>Throws the third switch.</summary>
    public Terminal C { get; }

    public Terminal NegativeSupply { get; }
}

/// <summary>
/// A 4067 sixteen-channel analog multiplexer: the 4051 twice over in a 24-pin package, four address
/// pins and no VEE — so its signals stay between VSS and VDD.
/// <para>
/// Pinout: 1=COM, 2-9=CH7-CH0, 10=A, 11=B, 12=VSS, 13=D, 14=C, 15=INH, 16-23=CH15-CH8, 24=VDD.
/// </para>
/// </summary>
public sealed class Ic4067 : AnalogMultiplexerIc
{
    private readonly Terminal[] _channels = new Terminal[16];

    public Ic4067() : base(24)
    {
        var pins = new Terminal[25];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 24));

        Common = Pin(1, "COM", TerminalType.Bidirectional);
        for (var i = 0; i < 8; i++) _channels[7 - i] = Pin(2 + i, $"CH{7 - i}", TerminalType.Bidirectional);
        A = Pin(10, "A", TerminalType.Input);
        B = Pin(11, "B", TerminalType.Input);
        Gnd = Pin(12, "VSS", TerminalType.Ground);
        D = Pin(13, "D", TerminalType.Input);
        C = Pin(14, "C", TerminalType.Input);
        var inhibit = Pin(15, "INH", TerminalType.Input);
        for (var i = 0; i < 8; i++) _channels[15 - i] = Pin(16 + i, $"CH{15 - i}", TerminalType.Bidirectional);
        Vcc = Pin(24, "VDD", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        Configure(inhibit, [A, B, C, D], new MuxSection(Common, _channels, [A, B, C, D]));
    }

    public override string PartNumber => "4067";

    public Terminal Common { get; }

    /// <summary>Address bit 0.</summary>
    public Terminal A { get; }

    public Terminal B { get; }

    public Terminal C { get; }

    /// <summary>Address bit 3.</summary>
    public Terminal D { get; }

    /// <summary>One of the sixteen channel pins.</summary>
    public Terminal Channel(int index) => _channels[index];

    public int SelectedChannel => SelectedChannelOf(0);
}
