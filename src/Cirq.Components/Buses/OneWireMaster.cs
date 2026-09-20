using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Buses;

/// <summary>
/// A 1-Wire master, playing a written-out list of operations.
/// <para>
/// 1-Wire is the odd one of the three buses here, and the reason is in the name: there is no
/// clock. I²C has two wires and SPI has four; this has one, plus a ground, and on a great many
/// parts that one wire carries the power as well. What a bit <i>is</i>, therefore, cannot be a
/// level sampled on a clock edge — there is no edge to sample on. It is a <b>pulse width</b>.
/// </para>
/// <para>
/// Every exchange is the master pulling the line down and letting go of it again, and the length
/// of the pull is the message. Down for six microseconds is a one; down for sixty is a zero; down
/// for half a millisecond is a reset, to which every device on the line answers by pulling it down
/// itself. To read, the master gives the line the briefest tug and then lets go, and whether the
/// line comes back up is the device's answer.
/// </para>
/// <para>
/// All of which makes it fussy in a way the other buses are not. An I²C bus that is a little slow
/// still works, because the clock comes with the data. Here the timing <b>is</b> the data: too
/// large a pull-up on too long a cable rounds the edges, a one starts to look like a zero, and the
/// bus does not degrade — it simply returns nonsense. That is what this part is for showing.
/// </para>
/// <para>
/// Operations, separated by semicolons or newlines, with all bytes in hex:
/// <code>
/// reset          ; the reset pulse, and listen for the presence answer
/// w CC 44        ; write bytes, least significant bit first, as the bus does
/// d 750000       ; wait, in microseconds — a conversion takes most of a second
/// reset
/// w CC BE
/// r 9            ; read nine bytes back
/// </code>
/// </para>
/// </summary>
public sealed partial class OneWireMaster : DigitalComponent, IBreakpointSource
{
    /// <summary>Standard-speed timings, in seconds, as the specification gives them.</summary>
    private const double ResetLow = 480e-6;

    private const double ResetRecovery = 480e-6;
    private const double PresenceSampleAt = 70e-6;
    private const double SlotTotal = 70e-6;
    private const double SlotRecovery = 5e-6;
    private const double ShortLow = 6e-6;
    private const double ZeroLow = 60e-6;
    private const double ReadSampleAt = 9e-6;

    private enum Sampling { None, Presence, ReadBit }

    private readonly record struct Step(double Time, bool Low, Sampling Sample);

    private List<Step> _schedule = [];
    private int _next;
    private string _compiledFrom = string.Empty;

    private int _readBits;
    private int _readValue;

    public OneWireMaster()
    {
        PropagationDelay = 50e-9;
        Levels = LogicLevels.Cmos33V;

        Data = new Terminal("dq", "DQ", TerminalType.Bidirectional, new Point(40, 0));
        Vcc = new Terminal("vcc", "VCC", TerminalType.Power, new Point(-40, -20));
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(-40, 20));

        Terminals = [Vcc, Data, Gnd];
        ConfigurePins([], [Data]);
    }

    /// <summary>The single wire. Pulled down or released, never driven high — as on I²C.</summary>
    public Terminal Data { get; }

    public Terminal Vcc { get; }

    public Terminal Gnd { get; }

    /// <summary>
    /// The operations to run once, from the start of the simulation. The default is the exchange
    /// every DS18B20 example begins with: skip addressing, start a conversion, wait for it, then
    /// read the scratchpad back.
    /// </summary>
    [ObservableProperty]
    public partial string Operations { get; set; } = "reset; w CC 44; d 750000; reset; w CC BE; r 9";

    /// <summary>How long to wait before starting, in seconds.</summary>
    [ObservableProperty]
    public partial double StartDelay { get; set; } = 100e-6;

    /// <summary>
    /// Resistance behind the pin while it is pulling the line down, in ohms. The datasheet figure
    /// is four milliamps at four tenths of a volt, which is this — and it is what decides how
    /// stiff a pull-up the part can still pull down against.
    /// </summary>
    [ObservableProperty]
    public partial double OpenDrainResistance { get; set; } = 100.0;

    protected override double OutputImpedance => Math.Max(OpenDrainResistance, 1e-3);

    public override string ComponentType => "1-Wire Master";

    public override string ValueLabel => IsFinished ? $"{ReceivedBytes.Count} bytes read" : "1-Wire";

    /// <summary>Bytes read back, in order.</summary>
    public List<int> ReceivedBytes { get; } = [];

    /// <summary>
    /// Whether anything answered the last reset. A device that is absent, or wired without a
    /// pull-up, leaves the line high and this stays false — which is the whole of the diagnosis.
    /// </summary>
    public bool PresenceDetected { get; private set; }

    /// <summary>True once the whole list has been played.</summary>
    public bool IsFinished => _schedule.Count > 0 && _next >= _schedule.Count;

    protected override int ReferenceNode(MnaSystem system) => system.Node(Gnd);

    /// <summary>Reads the line as a level rather than as a driven state.</summary>
    private bool IsHigh(IDigitalContext context) =>
        Levels.Classify(context.NodeVoltage(Data) - context.NodeVoltage(Gnd)) != LogicState.Low;

    public override void EvaluateLogic(IDigitalContext context)
    {
        Compile();

        var delay = DelayFor(context);

        if (_next >= _schedule.Count)
        {
            context.Schedule(this, 0, LogicState.HighImpedance, delay);
            return;
        }

        while (_next < _schedule.Count && _schedule[_next].Time <= context.Time)
        {
            var step = _schedule[_next];

            switch (step.Sample)
            {
                case Sampling.Presence:
                    // Something out there pulled the line down when we let go of it.
                    if (!IsHigh(context)) PresenceDetected = true;
                    break;

                case Sampling.ReadBit:
                    // Least significant bit first, which is the other way round from I²C.
                    _readValue |= (IsHigh(context) ? 1 : 0) << _readBits;

                    if (++_readBits == 8)
                    {
                        ReceivedBytes.Add(_readValue);
                        _readBits = 0;
                        _readValue = 0;
                    }

                    break;
            }

            _next++;
        }

        var applied = _next == 0 ? new Step(0, false, Sampling.None) : _schedule[_next - 1];

        context.Schedule(this, 0, applied.Low ? LogicState.Low : LogicState.HighImpedance, delay);
    }

    public double? NextBreakpointAfter(double time)
    {
        Compile();

        foreach (var step in _schedule)
            if (step.Time > time) return step.Time;

        return null;
    }

    /// <summary>Turns the list of operations into line states with times against them.</summary>
    private void Compile()
    {
        if (_compiledFrom == Operations && _schedule.Count > 0) return;

        _compiledFrom = Operations;

        List<Step> steps = [];
        var t = Math.Max(StartDelay, 0.0);

        void Drive(bool low, Sampling sample = Sampling.None) => steps.Add(new Step(t, low, sample));

        void WriteBit(bool one)
        {
            Drive(true);
            t += one ? ShortLow : ZeroLow;
            Drive(false);
            t += SlotTotal - (one ? ShortLow : ZeroLow) + SlotRecovery;
        }

        void ReadBit()
        {
            Drive(true);
            t += ShortLow;
            Drive(false);

            // The device has had the line for a few microseconds by now; whether it let go is the
            // bit. Sampling any later and a slow rise on a long cable turns every one into a zero.
            t += ReadSampleAt - ShortLow;
            steps.Add(new Step(t, false, Sampling.ReadBit));

            t += SlotTotal - ReadSampleAt + SlotRecovery;
        }

        foreach (var line in Operations.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var text = line.Split(';')[0].Trim();
            if (text.Length == 0) continue;

            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            switch (parts[0].ToLowerInvariant())
            {
                case "reset":
                    Drive(true);
                    t += ResetLow;
                    Drive(false);
                    t += PresenceSampleAt;
                    steps.Add(new Step(t, false, Sampling.Presence));
                    t += ResetRecovery - PresenceSampleAt;
                    break;

                case "w":
                    for (var i = 1; i < parts.Length; i++)
                    {
                        if (!int.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out var value))
                            continue;

                        for (var bit = 0; bit < 8; bit++) WriteBit(((value >> bit) & 1) != 0);
                    }

                    break;

                case "r":
                    if (parts.Length > 1 && int.TryParse(parts[1], out var count))
                        for (var i = 0; i < count * 8; i++) ReadBit();

                    break;

                case "d":
                    if (parts.Length > 1 && double.TryParse(parts[1], out var microseconds))
                        t += microseconds * 1e-6;

                    break;
            }
        }

        _schedule = steps;
        _next = 0;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        _next = 0;
        _readBits = 0;
        _readValue = 0;
        ReceivedBytes.Clear();
        PresenceDetected = false;
    }

    partial void OnOperationsChanged(string value)
    {
        _compiledFrom = string.Empty;
        NotifyValueChanged();
    }
}
