using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Nonlinear;

/// <summary>
/// A 10-segment LED bar graph in a DIP-20: ten LEDs side by side, anodes on pins 1-10 and their cathodes opposite on
/// 20-11 — what an LM3914 is usually driving. <see cref="Colour"/> picks the LEDs' colour.
/// </summary>
public sealed partial class LedBarGraph : MultiLed
{
    private IReadOnlyList<Junction> _junctions = [];

    public LedBarGraph() : base(10)
    {
        var pins = new Terminal[20];
        for (var n = 1; n <= 20; n++)
            pins[n - 1] = new Terminal($"p{n}", n <= 10 ? $"A{n}" : $"K{21 - n}", TerminalType.Passive, Digital.DipPackage.PinOffset(n, 20));
        Terminals = pins;
        Build();
    }

    /// <summary>Red, Green, Yellow or Blue.</summary>
    [ObservableProperty]
    public partial string Colour { get; set; } = "Red";

    partial void OnColourChanged(string value) => Build();

    private void Build()
    {
        var (model, hex) = Colour switch
        {
            "Green" => (DiodeModel.LedGreen, "#2ECC40"),
            "Yellow" => (DiodeModel.LedYellow, "#FFDC00"),
            "Blue" => (DiodeModel.LedBlue, "#3D9BFF"),
            _ => (DiodeModel.LedRed, "#FF4136"),
        };
        _junctions = [.. Enumerable.Range(0, 10).Select(i => new Junction(i, 19 - i, model, Color.FromHex(hex)))];
        NotifyValueChanged();
    }

    protected override IReadOnlyList<Junction> Junctions => _junctions;

    /// <summary>Segment n's anode (1-10).</summary>
    public Terminal Anode(int n) => Terminals[n - 1];

    /// <summary>Segment n's cathode (1-10).</summary>
    public Terminal Cathode(int n) => Terminals[20 - n];

    public override string ComponentType => "LED Bar Graph";

    public override string ValueLabel => $"10-segment {Colour.ToLowerInvariant()}";
}

/// <summary>
/// A four-digit seven-segment display made to be multiplexed, in the usual 12-pin package: the eight segment lines
/// (A-G and DP) shared by all four digits, and a common pin each, D1-D4 — cathodes on the common-cathode part, anodes
/// with <see cref="CommonAnode"/>. Light one digit at a time, fast enough, and the eye sees all four: each segment's
/// glow here is averaged over <see cref="Persistence"/>, as an eye averages it, so a digit lit a quarter of the time
/// looks lit, a quarter as bright. Pinout: 1 E, 2 D, 3 DP, 4 C, 5 G, 6 D4, 7 B, 8 D3, 9 D2, 10 F, 11 A, 12 D1.
/// </summary>
public sealed partial class QuadSevenSegment : MultiLed
{
    private static readonly string[] Segments = ["A", "B", "C", "D", "E", "F", "G", "DP"];
    private static readonly int[] SegmentPin = [11, 7, 4, 2, 1, 10, 5, 3];
    private static readonly int[] DigitPin = [12, 9, 8, 6];
    private IReadOnlyList<Junction> _junctions = [];
    private readonly double[] _seen = new double[32];
    private double _lastTime;

    public QuadSevenSegment() : base(32)
    {
        var names = new string[13];
        for (var s = 0; s < 8; s++) names[SegmentPin[s]] = Segments[s];
        for (var d = 0; d < 4; d++) names[DigitPin[d]] = $"D{d + 1}";
        Terminals = [.. Enumerable.Range(1, 12).Select(n => new Terminal($"p{n}", names[n], TerminalType.Passive,
            new Point(-75 + (((n - 1) % 6) * 30), n <= 6 ? 40 : -40)))];
        Build();
    }

    /// <summary>The digit pins are common anodes rather than common cathodes.</summary>
    [ObservableProperty]
    public partial bool CommonAnode { get; set; }

    /// <summary>How long the eye's average runs over, in seconds.</summary>
    [ObservableProperty]
    public partial double Persistence { get; set; } = 20e-3;

    partial void OnCommonAnodeChanged(bool value) => Build();

    private void Build()
    {
        var junctions = new List<Junction>();
        for (var d = 0; d < 4; d++)
            for (var s = 0; s < 8; s++)
            {
                var (segment, digit) = (SegmentPin[s] - 1, DigitPin[d] - 1);
                junctions.Add(CommonAnode
                    ? new Junction(digit, segment, DiodeModel.LedRed, Color.FromHex("#FF4136"))
                    : new Junction(segment, digit, DiodeModel.LedRed, Color.FromHex("#FF4136")));
            }

        _junctions = junctions;
        NotifyValueChanged();
    }

    protected override IReadOnlyList<Junction> Junctions => _junctions;

    public Terminal Segment(string name) => Terminals[SegmentPin[Array.IndexOf(Segments, name)] - 1];

    public Terminal Digit(int d) => Terminals[DigitPin[d] - 1];

    /// <summary>How lit a digit's segment looks, 0 to 1, averaged over the persistence; segment 0-7 is A-G and DP.</summary>
    public double Glow(int digit, int segment) => _seen[(digit * 8) + segment];

    /// <summary>
    /// A digit's segments as a seven-segment byte, A in bit 0: those glowing at least half as brightly as the display's
    /// brightest segment (and visibly at all), as an eye reads a multiplexed display.
    /// </summary>
    public int Pattern(int digit)
    {
        var top = _seen.Max();
        if (top < 0.02) return 0;
        return Enumerable.Range(0, 8).Where(s => Glow(digit, s) > top / 2).Aggregate(0, (v, s) => v | (1 << s));
    }

    public override string ComponentType => "4-Digit Display";

    public override string ValueLabel => CommonAnode ? "4-digit, common anode" : "4-digit, common cathode";

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        base.CommitTimeStep(system, state);
        var dt = Math.Max(state.Time - _lastTime, 0);
        _lastTime = state.Time;
        var a = Math.Clamp(dt / Math.Max(Persistence, 1e-6), 0, 1);
        for (var i = 0; i < 32; i++) _seen[i] += (Brightness[i] - _seen[i]) * a;
    }

    public override void ResetState()
    {
        base.ResetState();
        Array.Clear(_seen);
        _lastTime = 0;
    }
}
