using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

/// <summary>
/// A CD4046 phase-locked loop: a voltage-controlled oscillator, two phase comparators, and
/// nothing else. Everything a PLL does that is interesting happens <b>outside</b> the package, in
/// the loop filter you have to add, which is why the part is so often described as difficult.
/// <para>
/// What a PLL does is simple to say. The VCO runs at whatever frequency its control voltage asks
/// for. A phase comparator looks at the VCO against the incoming signal and puts out an error. The
/// loop filter turns that error into the next control voltage. Wire that round and the VCO is
/// dragged until it matches the input — not merely in frequency but in <i>phase</i>, which is what
/// makes it a different thing from an oscillator you tune by hand.
/// </para>
/// <para>
/// Two numbers people conflate, and this part keeps apart because they are genuinely different:
/// </para>
/// <para>
/// <b>Lock range</b> is how far the input can drift while the loop is already locked and still be
/// followed. It is set by how far the VCO can go, and it is wide.
/// </para>
/// <para>
/// <b>Capture range</b> is how close the input has to be before the loop can grab it in the first
/// place. It is set by the <i>loop filter</i>, and it is narrower — sometimes far narrower. A PLL
/// that will hold a signal perfectly once locked and refuses to lock onto the same signal from
/// cold is not faulty; it is being asked to capture from outside its capture range, and the answer
/// is a wider filter, not a better chip.
/// </para>
/// <para>
/// The two comparators are the other thing worth choosing deliberately. <b>Comparator I</b> is an
/// exclusive-OR: it locks at ninety degrees, tolerates a filthy input, and will happily lock onto
/// a harmonic. <b>Comparator II</b> is edge-triggered and has a third state: it locks at zero
/// degrees, cannot be fooled by a harmonic, and has no output at all once locked — which means it
/// contributes no ripple, and also that it cannot tell you it has lost lock. Hence pin 1.
/// </para>
/// </summary>
public sealed partial class Ic4046 : DigitalIc, IBreakpointSource
{
    private const int ComparatorIOutput = 0;
    private const int ComparatorIIOutput = 1;
    private const int VcoOutput = 2;
    private const int LockOutput = 3;
    private const int DemodulatorOutput = 4;

    /// <summary>Where the VCO is in its cycle, from zero to one.</summary>
    private double _phase;

    private double _frequency;
    private double _lastAdvanceAt;

    // Comparator II is a state machine over the two edges it watches, not a gate.
    private bool _previousSignal;
    private bool _previousVco;
    private int _chargeState;

    private double _drivingSince = double.NegativeInfinity;
    private double _phaseError = 1.0;
    private LogicState _comparatorState = LogicState.HighImpedance;

    public Ic4046() : base(16)
    {
        PropagationDelay = 100e-9;
        Levels = LogicLevels.Cmos5V;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        PhasePulses = Pin(1, "PP", TerminalType.Output);
        ComparatorI = Pin(2, "PC1", TerminalType.Output);
        ComparatorInput = Pin(3, "COMP", TerminalType.Input);
        VcoOut = Pin(4, "VCO", TerminalType.Output);
        Inhibit = Pin(5, "INH", TerminalType.Input);
        CapacitorA = Pin(6, "C1A", TerminalType.Passive);
        CapacitorB = Pin(7, "C1B", TerminalType.Passive);
        Gnd = Pin(8, "VSS", TerminalType.Ground);
        ControlVoltage = Pin(9, "VCOIN", TerminalType.Input);
        Demodulator = Pin(10, "DEMO", TerminalType.Output);
        ResistorOne = Pin(11, "R1", TerminalType.Passive);
        ResistorTwo = Pin(12, "R2", TerminalType.Passive);
        ComparatorII = Pin(13, "PC2", TerminalType.Output);
        SignalInput = Pin(14, "SIG", TerminalType.Input);
        Zener = Pin(15, "ZEN", TerminalType.Passive);
        Vcc = Pin(16, "VDD", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];

        ConfigurePins(
            [SignalInput, ComparatorInput, ControlVoltage, Inhibit],
            [ComparatorI, ComparatorII, VcoOut, PhasePulses, Demodulator]);
    }

    /// <summary>Pin 1: low while comparator II is locked, and the only lock indication there is.</summary>
    public Terminal PhasePulses { get; }

    /// <summary>Pin 2: the exclusive-OR comparator's output.</summary>
    public Terminal ComparatorI { get; }

    /// <summary>Pin 3: the VCO's own output fed back, usually straight from pin 4.</summary>
    public Terminal ComparatorInput { get; }

    /// <summary>Pin 4: the oscillator.</summary>
    public Terminal VcoOut { get; }

    /// <summary>Pin 5: hold the VCO still. High stops it and saves the current.</summary>
    public Terminal Inhibit { get; }

    public Terminal CapacitorA { get; }

    public Terminal CapacitorB { get; }

    /// <summary>Pin 9: the control voltage, and the whole of what sets the frequency.</summary>
    public Terminal ControlVoltage { get; }

    /// <summary>Pin 10: a source follower repeating pin 9, for reading the control voltage.</summary>
    public Terminal Demodulator { get; }

    public Terminal ResistorOne { get; }

    public Terminal ResistorTwo { get; }

    /// <summary>Pin 13: the edge-triggered comparator, which is three-state.</summary>
    public Terminal ComparatorII { get; }

    /// <summary>Pin 14: the signal to lock onto.</summary>
    public Terminal SignalInput { get; }

    public Terminal Zener { get; }

    /// <summary>
    /// The timing capacitor across pins 6 and 7, in farads. With R1 it sets the frequency; it is
    /// given as a property rather than read off a capacitor you wire there, because the
    /// oscillator's charge and discharge are internal to the package.
    /// </summary>
    [ObservableProperty]
    public partial double TimingCapacitance { get; set; } = 1e-9;

    /// <summary>R1, in ohms: the resistor from pin 11 to ground, which sets the frequency range.</summary>
    [ObservableProperty]
    public partial double SeriesResistance { get; set; } = 100e3;

    /// <summary>
    /// R2, in ohms: from pin 12 to ground, and what puts a floor under the frequency. Leave it out
    /// — the default — and the VCO stops dead at zero volts in, which is what makes a loop that
    /// has lost lock take so long to find its way back.
    /// </summary>
    [ObservableProperty]
    public partial double OffsetResistance { get; set; }

    /// <summary>Which comparator's output the loop filter is being driven from, for the lock flag.</summary>
    [ObservableProperty]
    public partial bool UsesComparatorTwo { get; set; } = true;

    public override string PartNumber => "CD4046";

    public override string ValueLabel => SiPrefix.Format(_frequency, "Hz");

    /// <summary>Where the VCO is running at the last solved point, in hertz.</summary>
    public double VcoFrequency => _frequency;

    /// <summary>
    /// The highest the VCO will go, in hertz — at the top of its control range. Roughly
    /// 1/(R1·C1), which is the figure the datasheet's nomograph gives.
    /// </summary>
    public double MaximumFrequency =>
        1.0 / (Math.Max(SeriesResistance, 1.0) * Math.Max(TimingCapacitance, 1e-15));

    /// <summary>
    /// The lowest it will go, set by R2. Zero when R2 is left out, which is the usual case and the
    /// reason a 4046 without one cannot sweep back up from a standstill.
    /// </summary>
    public double MinimumFrequency => OffsetResistance <= 0
        ? 0.0
        : 1.0 / (Math.Max(OffsetResistance, 1.0) * Math.Max(TimingCapacitance, 1e-15));

    /// <summary>True while comparator II says the loop is locked.</summary>
    public bool IsLocked { get; private set; }

    /// <summary>
    /// How far out of phase the loop is, in cycles, averaged over the last few corrections. Zero
    /// is locked; anything approaching one is a loop that has no idea where the signal is.
    /// </summary>
    public double PhaseError => _phaseError;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var supply = context.NodeVoltage(Vcc) - context.NodeVoltage(Gnd);
        var delay = DelayFor(context);

        var signal = context.ReadInput(SignalInput, Levels) == LogicState.High;
        var comparator = context.ReadInput(ComparatorInput, Levels) == LogicState.High;

        AdvanceOscillator(context, supply);

        var vco = _phase < 0.5;
        context.Schedule(this, VcoOutput, vco ? LogicState.High : LogicState.Low, delay);

        // Comparator I: an exclusive-OR, and nothing more. Its average is the phase difference,
        // which is why it settles at ninety degrees rather than at zero.
        context.Schedule(this, ComparatorIOutput,
            signal != comparator ? LogicState.High : LogicState.Low, delay);

        StampComparatorTwo(context, signal, comparator, delay);

        // Pin 10 repeats the control voltage. Modelled as a logic pin rather than a follower,
        // which is enough for it to be watched and not enough to drive anything.
        context.Schedule(this, DemodulatorOutput,
            context.NodeVoltage(ControlVoltage) - context.NodeVoltage(Gnd) > supply * 0.5
                ? LogicState.High : LogicState.Low, delay);

        _previousSignal = signal;
        _previousVco = comparator;
    }

    /// <summary>
    /// Comparator II: edge-triggered, three-state, and the reason a 4046 locks at zero degrees.
    /// <para>
    /// A rising edge on the signal charges; a rising edge on the VCO discharges; whichever came
    /// first holds the output until the other arrives, and once they arrive together the output
    /// lets go entirely. That third state is what makes it contribute no ripple when locked — and
    /// what makes pin 1 the only way to know it is.
    /// </para>
    /// </summary>
    private void StampComparatorTwo(IDigitalContext context, bool signal, bool vco, double delay)
    {
        var signalEdge = signal && !_previousSignal;
        var vcoEdge = vco && !_previousVco;

        // Whichever edge arrives first starts the output driving, and the arrival of the other
        // one stops it. Driving continuously until the next edge — which is what a plain
        // set/reset would do — makes it a comparator that never lets go, and a loop that never
        // settles anywhere.
        if (signalEdge && vcoEdge) _chargeState = 0;
        else if (signalEdge) _chargeState = _chargeState == -1 ? 0 : 1;
        else if (vcoEdge) _chargeState = _chargeState == 1 ? 0 : -1;

        var state = _chargeState switch
        {
            1 => LogicState.High,
            -1 => LogicState.Low,
            _ => LogicState.HighImpedance,
        };

        context.Schedule(this, ComparatorIIOutput, state, delay);

        // Pin 1 pulses high whenever the comparator has to correct, so how wide those pulses are
        // is the phase error — which is what a real lock detector integrates. Measured in cycles
        // rather than seconds, so it means the same thing at any frequency, and averaged, because
        // a locked loop still corrects a little on every cycle and one narrow pulse is not a loss
        // of lock.
        if (state != LogicState.HighImpedance && _comparatorState == LogicState.HighImpedance)
        {
            _drivingSince = context.Time;
        }
        else if (state == LogicState.HighImpedance && _comparatorState != LogicState.HighImpedance
                 && !double.IsNegativeInfinity(_drivingSince))
        {
            var cycles = (context.Time - _drivingSince) * Math.Max(_frequency, 1.0);

            _phaseError += 0.25 * (Math.Min(cycles, 1.0) - _phaseError);
        }

        _comparatorState = state;

        IsLocked = UsesComparatorTwo && _frequency > 0 && _phaseError < 0.15;

        context.Schedule(this, LockOutput,
            state == LogicState.HighImpedance ? LogicState.Low : LogicState.High, delay);
    }

    /// <summary>
    /// Moves the oscillator on to the present moment at whatever frequency the control voltage is
    /// asking for. The phase is carried between calls, so changing the control voltage bends the
    /// waveform from where it is rather than restarting it — which is what an oscillator does and
    /// what makes phase, rather than only frequency, something the loop can correct.
    /// </summary>
    private void AdvanceOscillator(IDigitalContext context, double supply)
    {
        if (context.ReadInput(Inhibit, Levels) == LogicState.High)
        {
            _frequency = 0;
            _lastAdvanceAt = context.Time;
            return;
        }

        var control = Math.Clamp(
            (context.NodeVoltage(ControlVoltage) - context.NodeVoltage(Gnd)) / Math.Max(supply, 1e-6), 0, 1);

        // Linear in the control voltage between the floor R2 sets and the ceiling R1 sets, which
        // is how the datasheet's curves run over the usable middle of the range.
        _frequency = MinimumFrequency + (control * Math.Max(MaximumFrequency - MinimumFrequency, 0.0));

        var elapsed = context.Time - _lastAdvanceAt;
        _lastAdvanceAt = context.Time;

        if (elapsed <= 0 || _frequency <= 0) return;

        _phase = (_phase + (elapsed * _frequency)) % 1.0;
    }

    /// <summary>
    /// The VCO's own edges, so they land where they belong rather than being smeared across
    /// whatever step the rest of the circuit happens to want.
    /// </summary>
    public double? NextBreakpointAfter(double time)
    {
        if (_frequency <= 0) return null;

        var period = 1.0 / _frequency;
        var remaining = _phase < 0.5 ? 0.5 - _phase : 1.0 - _phase;

        return time + (remaining * period);
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        _phase = 0;
        _frequency = 0;
        _lastAdvanceAt = 0;
        _previousSignal = false;
        _previousVco = false;
        _chargeState = 0;
        _drivingSince = double.NegativeInfinity;
        _phaseError = 1.0;
        _comparatorState = LogicState.HighImpedance;
        IsLocked = false;
    }

    partial void OnTimingCapacitanceChanged(double value) => NotifyValueChanged();

    partial void OnSeriesResistanceChanged(double value) => NotifyValueChanged();
}
