using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Buses;

/// <summary>
/// An SPI master that plays a written-out list of transfers.
/// <para>
/// SPI is the opposite trade to I²C. There is no addressing, no acknowledgement and no open
/// drain — just a clock, a line each way, and a select pin per device. That makes it faster and
/// far simpler to decode, at the cost of a pin for every chip you add. A device only pays
/// attention while its select line is held low, and that is the entire protocol.
/// </para>
/// <para>
/// Each line of the script is one transfer: a select, the bytes, and a deselect. Bytes are hex
/// and separated by spaces. Data is put up while the clock is low and read while it is high,
/// which is mode zero and what nearly everything expects.
/// </para>
/// </summary>
public sealed partial class SpiMaster : DigitalComponent, IBreakpointSource
{
    private readonly record struct Step(double Time, bool Clock, bool Data, bool Select, bool Sample);

    private List<Step> _schedule = [];
    private int _next;
    private string _compiledFrom = string.Empty;

    private int _readBits;
    private int _readValue;

    public SpiMaster()
    {
        PropagationDelay = 20e-9;
        Levels = LogicLevels.Cmos33V;

        Clock = new Terminal("sck", "SCK", TerminalType.Output, new Point(40, -30));
        MasterOut = new Terminal("mosi", "MOSI", TerminalType.Output, new Point(40, -10));
        MasterIn = new Terminal("miso", "MISO", TerminalType.Input, new Point(40, 10));
        ChipSelect = new Terminal("cs", "CS", TerminalType.Output, new Point(40, 30));
        Vcc = new Terminal("vcc", "VCC", TerminalType.Power, new Point(-40, -20));
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(-40, 20));

        Terminals = [Vcc, Clock, MasterOut, MasterIn, ChipSelect, Gnd];
        ConfigurePins([MasterIn], [Clock, MasterOut, ChipSelect]);
    }

    public Terminal Clock { get; }

    /// <summary>Data out of the master and into the device.</summary>
    public Terminal MasterOut { get; }

    /// <summary>Data back from the device.</summary>
    public Terminal MasterIn { get; }

    /// <summary>Active low. A device ignores the bus entirely while this is high.</summary>
    public Terminal ChipSelect { get; }

    public Terminal Vcc { get; }

    public Terminal Gnd { get; }

    /// <summary>Bytes to send, hex, one transfer per line or semicolon.</summary>
    [ObservableProperty]
    public partial string Transactions { get; set; } = "AA 55";

    /// <summary>Clock rate, in hertz.</summary>
    [ObservableProperty]
    public partial double ClockFrequency { get; set; } = 1e6;

    /// <summary>How long to wait before starting, in seconds.</summary>
    [ObservableProperty]
    public partial double StartDelay { get; set; } = 20e-6;

    public override string ComponentType => "SPI Master";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => SiPrefix.Format(ClockFrequency, "Hz");

    /// <summary>Bytes clocked back in from the device, in order.</summary>
    public List<int> ReceivedBytes { get; } = [];

    public bool IsFinished => _schedule.Count > 0 && _next >= _schedule.Count;

    protected override int ReferenceNode(MnaSystem system) => system.Node(Gnd);

    public override void EvaluateLogic(IDigitalContext context)
    {
        Compile();

        var delay = DelayFor(context);

        void Set(int index, bool high) =>
            context.Schedule(this, index, high ? LogicState.High : LogicState.Low, delay);

        if (_schedule.Count == 0 || _next >= _schedule.Count)
        {
            // Idle: clock low, nothing selected.
            Set(0, false);
            Set(1, false);
            Set(2, true);
            return;
        }

        while (_next < _schedule.Count && _schedule[_next].Time <= context.Time)
        {
            var step = _schedule[_next];

            if (step.Sample)
            {
                var level = Levels.Classify(context.NodeVoltage(MasterIn) - context.NodeVoltage(Gnd));
                _readValue = (_readValue << 1) | (level == LogicState.High ? 1 : 0);

                if (++_readBits == 8)
                {
                    ReceivedBytes.Add(_readValue);
                    _readBits = 0;
                    _readValue = 0;
                }
            }

            _next++;
        }

        var applied = _next == 0 ? new Step(0, false, false, true, false) : _schedule[_next - 1];

        Set(0, applied.Clock);
        Set(1, applied.Data);
        Set(2, applied.Select);
    }

    public double? NextBreakpointAfter(double time)
    {
        Compile();

        foreach (var step in _schedule)
            if (step.Time > time) return step.Time;

        return null;
    }

    private void Compile()
    {
        if (_compiledFrom == Transactions && _schedule.Count > 0) return;

        _compiledFrom = Transactions;
        _schedule = [];

        var bit = 1.0 / Math.Max(ClockFrequency, 1.0);
        var half = bit / 2.0;

        var t = Math.Max(StartDelay, 0.0);

        foreach (var line in Transactions.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = line.Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(token => int.TryParse(token, System.Globalization.NumberStyles.HexNumber,
                    null, out var v) ? v : -1)
                .Where(v => v >= 0)
                .ToList();

            if (bytes.Count == 0) continue;

            // Select, and give the device a moment to notice before clocking.
            _schedule.Add(new Step(t, false, false, false, false));
            t += half;

            foreach (var value in bytes)
            {
                for (var i = 7; i >= 0; i--)
                {
                    var high = (value & (1 << i)) != 0;

                    // Data goes up while the clock is low, and is read on the rising edge.
                    _schedule.Add(new Step(t, false, high, false, false));
                    _schedule.Add(new Step(t + half, true, high, false, true));
                    t += bit;
                }
            }

            _schedule.Add(new Step(t, false, false, false, false));
            _schedule.Add(new Step(t + half, false, false, true, false));
            t += bit * 2;
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        _next = 0;
        _readBits = 0;
        _readValue = 0;
        _schedule = [];
        _compiledFrom = string.Empty;
        ReceivedBytes.Clear();
    }

    partial void OnTransactionsChanged(string value) => NotifyValueChanged();
}
