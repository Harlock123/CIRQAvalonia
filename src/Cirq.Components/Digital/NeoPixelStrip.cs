using System.Text;
using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

/// <summary>
/// A strip of WS2812B addressable LEDs — NeoPixels: a colour per pixel, set over one data wire, and
/// the reason every hobby project now has a rainbow on it.
/// <para>
/// The protocol is all timing, and it is decoded here the way the chip decodes it rather than taken
/// on trust. Each bit is one high pulse in a slot of about 1.25 µs at 800 kHz, and what makes it a 0
/// or a 1 is only how long the line stays high: about 0.4 µs is a 0, about 0.8 µs a 1, and this
/// strip splits the two at <see cref="BitThreshold"/>. Twenty-four bits make a pixel, sent green,
/// red, blue, most significant bit first. The first pixel keeps the first twenty-four bits it sees
/// and passes everything after them out of DOUT, which is how a strip — or the next strip, wired on
/// to DOUT — is addressed without any addresses. Hold the line low for
/// <see cref="ResetTime"/> (50 µs on the original part) and every pixel shows what it was sent.
/// </para>
/// <para>
/// So nothing appears until the reset, a pulse of the wrong width is the wrong bit, and an input
/// driven from a 3.3 V board may never reach the 0.7 × VDD the part needs to see a high on a 5 V
/// strip — all of which is how real strips fail, and the reason a level shifter turns up in front
/// of so many of them. A pattern generator stepping at 2.5 MHz makes the waveform exactly:
/// <c>100</c> for a 0 and <c>110</c> for a 1, which <see cref="PatternFor"/> writes out.
/// </para>
/// <para>
/// Pins: VDD, DIN and GND on the left, DOUT on the right. The supply draws what the lit pixels
/// would — about 20 mA a channel at full brightness, which is the amp and a half that sixty pixels
/// of white ask of a USB port.
/// </para>
/// </summary>
public sealed partial class NeoPixelStrip : DigitalIc, IBreakpointSource
{
    /// <summary>The most pixels one strip part will draw; chain another on DOUT for more.</summary>
    public const int MaximumPixels = 64;

    /// <summary>Pixels drawn in a row on the canvas before the strip wraps.</summary>
    public const int PixelsPerRow = 8;

    /// <summary>Steps of a pattern generator that make one bit at 2.5 MHz: 0.4 µs each, 1.2 µs a bit.</summary>
    public const double PatternStepRate = 2.5e6;

    /// <summary>Current a pixel draws at full brightness on one channel, in amps.</summary>
    private const double ChannelCurrent = 20e-3;

    /// <summary>Current a pixel draws dark, in amps.</summary>
    private const double IdleCurrent = 1e-3;

    private Color[] _shown = [];
    private Color[] _received = [];

    private LogicState _lastData = LogicState.Unknown;
    private double _roseAt = double.NegativeInfinity;
    private double _fellAt = double.NegativeInfinity;
    private bool _latchPending;
    private bool _forwarding;
    private int _bits;
    private int _shift;

    public NeoPixelStrip() : base(4)
    {
        PropagationDelay = 50e-9;
        Levels = LogicLevels.Cmos5V;
        MinimumSupplyVoltage = 3.5;

        Vcc = new Terminal("vdd", "VDD", TerminalType.Power, new Point(-120, -20));
        DataIn = new Terminal("din", "DIN", TerminalType.Input, new Point(-120, 0));
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(-120, 20));
        DataOut = new Terminal("dout", "DOUT", TerminalType.Output, new Point(120, 0));

        Terminals = [Vcc, DataIn, Gnd, DataOut];
        ConfigurePins([DataIn], [DataOut]);

        Resize();
    }

    public Terminal DataIn { get; }

    public Terminal DataOut { get; }

    /// <summary>How many pixels are on the strip.</summary>
    [ObservableProperty]
    public partial int PixelCount { get; set; } = 8;

    /// <summary>
    /// High time that separates a 0 from a 1, in seconds. The datasheet's 0 is 0.4 µs and its 1 is
    /// 0.8 µs, each ±150 ns, so anything between those windows is read by which side of the middle
    /// it falls on.
    /// </summary>
    [ObservableProperty]
    public partial double BitThreshold { get; set; } = 0.6e-6;

    /// <summary>How long the line has to stay low for the pixels to show what they were sent, in seconds.</summary>
    [ObservableProperty]
    public partial double ResetTime { get; set; } = 50e-6;

    public override string PartNumber => "WS2812B";

    public override string ComponentType => "NeoPixel Strip";

    public override string DesignatorPrefix => "LED";

    public override string ValueLabel => $"{Count} × WS2812B";

    /// <summary>The pixel count, held to what one part can draw.</summary>
    public int Count => Math.Clamp(PixelCount, 1, MaximumPixels);

    /// <summary>What each pixel is showing, as red, green and blue.</summary>
    public IReadOnlyList<Color> Pixels => _shown;

    /// <summary>Bits taken in since the last reset — the frame being sent.</summary>
    public int BitsReceived => _bits;

    /// <summary>How many frames have been latched since the run began.</summary>
    public int FramesShown { get; private set; }

    /// <summary>What the lit pixels draw from VDD, in amps.</summary>
    public double SupplyCurrent => _shown.Sum(p => (p.R + p.G + p.B) / 255.0 * ChannelCurrent) + (Count * IdleCurrent);

    private void Resize()
    {
        if (_shown.Length == Count) return;

        Array.Resize(ref _shown, Count);
        Array.Resize(ref _received, Count);

        for (var i = 0; i < Count; i++)
        {
            _shown[i] = new Color(0, 0, 0);
            _received[i] = new Color(0, 0, 0);
        }
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        Resize();

        var time = context.Time;
        var data = context.ReadInput(DataIn, Levels);

        if (data.IsHigh() && !_lastData.IsHigh())
        {
            _roseAt = time;

            // A pulse after this pixel's share of the frame is somebody else's, and goes on down DOUT.
            _forwarding = _bits >= Count * 24;
        }
        else if (!data.IsHigh() && _lastData.IsHigh())
        {
            _fellAt = time;
            var high = time - _roseAt;

            if (!_forwarding && high > 0.1e-6) Take(high >= BitThreshold);
        }

        if (!data.IsHigh() && _latchPending && time - _fellAt >= ResetTime - 1e-12) Latch();

        _lastData = data;

        // Downstream sees the pulses this strip did not keep, and a low line otherwise.
        context.Schedule(this, 0, _forwarding && data.IsHigh() ? LogicState.High : LogicState.Low, DelayFor(context));
    }

    /// <summary>One bit into the pixel it belongs to, green first, most significant bit first.</summary>
    private void Take(bool one)
    {
        _shift = (_shift << 1) | (one ? 1 : 0);
        _bits++;
        _latchPending = true;

        if (_bits % 24 != 0) return;

        var pixel = (_bits / 24) - 1;
        var g = (byte)((_shift >> 16) & 0xFF);
        var r = (byte)((_shift >> 8) & 0xFF);
        var b = (byte)(_shift & 0xFF);

        _received[pixel] = new Color(r, g, b);
        _shift = 0;
    }

    /// <summary>The reset: every pixel that was sent a whole colour shows it, and the next frame starts.</summary>
    private void Latch()
    {
        var complete = Math.Min(_bits / 24, Count);
        for (var i = 0; i < complete; i++) _shown[i] = _received[i];

        _bits = 0;
        _shift = 0;
        _forwarding = false;
        _latchPending = false;
        FramesShown++;

        OnPropertyChanged(nameof(Pixels));
    }

    /// <summary>The moment a low line becomes a reset, so the solver lands on it.</summary>
    public double? NextBreakpointAfter(double time)
    {
        if (!_latchPending || _lastData.IsHigh()) return null;

        var at = _fellAt + ResetTime;
        return at > time + 1e-15 ? at : null;
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);

        // The pixels' current, as the resistance that draws it from 5 V.
        system.StampConductance(system.Node(Vcc), system.Node(Gnd), SupplyCurrent / 5.0);
    }

    /// <summary>Takes the power away from the pixels as well as the logic: an unpowered strip is dark.</summary>
    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        if (system.NodeVoltage(Vcc) - system.NodeVoltage(Gnd) >= MinimumSupplyVoltage) return;
        if (_shown.All(p => p is { R: 0, G: 0, B: 0 }) && _bits == 0) return;

        Array.Fill(_shown, new Color(0, 0, 0));
        Array.Fill(_received, new Color(0, 0, 0));
        _bits = 0;
        _shift = 0;
        _latchPending = false;
        OnPropertyChanged(nameof(Pixels));
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        _shown = [];
        _received = [];
        Resize();

        _lastData = LogicState.Unknown;
        _roseAt = double.NegativeInfinity;
        _fellAt = double.NegativeInfinity;
        _latchPending = false;
        _forwarding = false;
        _bits = 0;
        _shift = 0;
        FramesShown = 0;
    }

    partial void OnPixelCountChanged(int value)
    {
        Resize();
        NotifyValueChanged();
    }

    /// <summary>
    /// A pattern generator's line for a run of frames: each frame a colour per pixel, sent as
    /// <c>100</c> for a 0 and <c>110</c> for a 1 at <see cref="PatternStepRate"/>, then held low
    /// for the reset.
    /// </summary>
    /// <param name="frames">The frames, in order. Each is a list of pixel colours, first pixel first.</param>
    /// <param name="resetSteps">Low steps after each frame: 150 at 0.4 µs is 60 µs, past the 50 µs reset.</param>
    public static string PatternFor(IEnumerable<IReadOnlyList<Color>> frames, int resetSteps = 150)
    {
        ArgumentNullException.ThrowIfNull(frames);

        var text = new StringBuilder();

        foreach (var frame in frames)
        {
            foreach (var pixel in frame)
            {
                foreach (var channel in new[] { pixel.G, pixel.R, pixel.B })
                {
                    for (var bit = 7; bit >= 0; bit--)
                        text.Append(((channel >> bit) & 1) == 1 ? "110" : "100");
                }
            }

            text.Append('0', Math.Max(resetSteps, 1));
        }

        return text.ToString();
    }
}
