using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

/// <summary>
/// The timing of one monostable: the real capacitor on the canvas, discharged by the chip when it is
/// triggered and then left to charge back through the real resistor, with the pulse ending when the
/// voltage crosses a threshold.
/// <para>
/// That is how the silicon does it, and it is why the R and C here are parts you wire rather than
/// numbers you type — as the 555's and the 4060's are. The threshold is where the datasheet's formula
/// comes from: a capacitor charging from v₀ towards the supply through R crosses
/// V·(1 − (1 − v₀/V)·e<sup>−K</sup>) after exactly K·R·C, so setting the threshold there makes the pulse
/// the datasheet's t<sub>w</sub> = K·R·C whatever R and C are, and changing either on the canvas
/// changes the pulse in proportion. The discharge itself takes a few tens of ohms' worth of RC, which
/// is why a real one-shot has a minimum pulse width and a recovery time.
/// </para>
/// <para>
/// Retriggering is the same discharge done again in the middle of a pulse, so the pulse runs on for
/// K·R·C from the last trigger.
/// </para>
/// </summary>
internal sealed class MonostableTimer
{
    /// <summary>
    /// Resistance of the chip's discharge transistor, in ohms. A couple of ohms, so the discharge takes
    /// microseconds and the pulse is the charging time the datasheet's formula describes.
    /// </summary>
    public const double DischargeResistance = 2.0;

    /// <summary>The resistance from a CEXT pin to the chip's ground, in ohms.</summary>
    public const double CapacitorPinResistance = 1.0;

    /// <summary>How far down the capacitor has to come, as a fraction of the supply, before timing starts.</summary>
    private const double Discharged = 0.02;

    private enum Phase { Idle, Discharging, Timing }

    private Phase _phase;
    private double _threshold;

    /// <summary>True while the pulse is running: the Q output.</summary>
    public bool IsRunning => _phase != Phase.Idle;

    /// <summary>True while the discharge transistor is on.</summary>
    public bool IsDischarging => _phase == Phase.Discharging;

    /// <summary>Starts a pulse — or, on a retriggerable part, starts it again.</summary>
    public void Trigger(bool retriggerable)
    {
        if (_phase == Phase.Idle || retriggerable) _phase = Phase.Discharging;
    }

    /// <summary>Ends the pulse at once, as a clear or reset does.</summary>
    public void Cancel() => _phase = Phase.Idle;

    /// <summary>Moves the timing on from the voltage on the timing node.</summary>
    /// <param name="volts">Across the capacitor, timing end positive.</param>
    /// <param name="target">What R charges the capacitor towards: the supply, or the 4047's R pin.</param>
    /// <param name="k">The datasheet's K in t<sub>w</sub> = K·R·C.</param>
    public void Update(double volts, double target, double k)
    {
        if (_phase == Phase.Discharging && volts <= Discharged * target)
        {
            var start = Math.Clamp(volts / target, 0.0, 1.0);
            _threshold = target * (1.0 - ((1.0 - start) * Math.Exp(-k)));
            _phase = Phase.Timing;
        }
        else if (_phase == Phase.Timing && volts >= _threshold)
        {
            _phase = Phase.Idle;
        }
    }

    public void Reset()
    {
        _phase = Phase.Idle;
        _threshold = 0;
    }
}

/// <summary>
/// Base for the one-shots with external timing parts: the 74121, the dual 74123 and 74221, and the
/// CMOS 4528 and 4538. Each section has a capacitor between its CEXT pin and its RX/CX pin and a
/// resistor from RX/CX to the supply; triggered, Q goes high for t<sub>w</sub> = K·R·C. See
/// <see cref="MonostableTimer"/> for how the R and C are used — they are the real components on the
/// canvas, not settings.
/// <para>
/// The parts differ in what triggers them and whether a trigger during a pulse restarts it. The
/// <b>retriggerable</b> 74123, 4528 and 4538 start again from each trigger, so a train of triggers
/// closer together than t<sub>w</sub> holds Q high throughout — a missing-pulse detector. The 74121 and
/// 74221 ignore triggers until the pulse has finished, so a train of them gives evenly spaced pulses of
/// exactly t<sub>w</sub> — a pulse shaper. A clear or reset, where there is one, ends the pulse at once
/// and holds off triggers while it is asserted.
/// </para>
/// </summary>
public abstract partial class MonostableIc : DigitalIc
{
    /// <summary>One section's pins and timing.</summary>
    private protected sealed class Section
    {
        public required Terminal[] Triggers;
        public Terminal? Clear;
        public required Terminal Q;
        public required Terminal QNot;
        public required Terminal Capacitor;
        public required Terminal Timing;
        public readonly MonostableTimer Timer = new();

        /// <summary>The trigger signal at the last evaluation, for its rising edge.</summary>
        public bool LastTrigger = true;

        public LogicState LastClear = LogicState.Unknown;
    }

    private Section[] _sections = [];

    protected MonostableIc(int pinCount) : base(pinCount)
    {
    }

    /// <summary>
    /// The K in t<sub>w</sub> = K·R·C, from the datasheet. It is a setting so a part from a different
    /// family can be matched; the R and C themselves are on the canvas.
    /// </summary>
    [ObservableProperty]
    public partial double TimingConstant { get; set; }

    /// <summary>True for the parts a trigger during a pulse restarts.</summary>
    public abstract bool IsRetriggerable { get; }

    /// <summary>Whether a section's pulse is running.</summary>
    public bool IsRunning(int section) => _sections[section].Timer.IsRunning;

    /// <summary>The number of sections, one or two.</summary>
    public int SectionCount => _sections.Length;

    /// <summary>A section's pins: its triggers in datasheet order, clear (null without one), Q, Q', CEXT and RX/CX.</summary>
    public (IReadOnlyList<Terminal> Triggers, Terminal? Clear, Terminal Q, Terminal QNot, Terminal Capacitor, Terminal Timing)
        Pins(int section)
    {
        var s = _sections[section];
        return (s.Triggers, s.Clear, s.Q, s.QNot, s.Capacitor, s.Timing);
    }

    public override string ValueLabel =>
        string.Join(" / ", _sections.Select(s => s.Timer.IsRunning ? "pulse" : "idle"));

    /// <summary>Sets the sections up once the part has set its Terminals, and tells the base which pins are which.</summary>
    private protected void Configure(params Section[] sections)
    {
        _sections = sections;

        ConfigurePins(
            [.. sections.SelectMany(s => s.Triggers.Append(s.Clear)).OfType<Terminal>()],
            [.. sections.SelectMany(s => new[] { s.Q, s.QNot })]);
    }

    /// <summary>
    /// The trigger signal the pins make together; a pulse starts on its rising edge. On the 74121 it is
    /// B·(A1·A2)', on the 74123 and 74221 A'·B, and on the 4528 and 4538 +TR + (−TR)' — which is why a
    /// rising +TR triggers only with −TR high, and a falling −TR only with +TR low.
    /// </summary>
    protected abstract bool TriggerLevel(LogicState[] pins);

    /// <summary>Whether a section's clear or reset is asserted (and so holds it off).</summary>
    protected abstract bool ClearAsserted(LogicState clear);

    /// <summary>Whether a clear being released counts as a trigger, as it does on the 74123.</summary>
    protected virtual bool ClearReleaseTriggers => false;

    /// <summary>An extra internal resistor (the 74121's RINT), stamped by the part that has one.</summary>
    protected virtual void StampInternal(MnaSystem system)
    {
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);

        var gnd = system.Node(Gnd);

        foreach (var s in _sections)
        {
            // CEXT is the chip's own end of the capacitor, held at its ground, so the capacitor charges
            // from the timing node's point of view as if it went to ground.
            system.StampResistor(system.Node(s.Capacitor), gnd, MonostableTimer.CapacitorPinResistance);

            if (s.Timer.IsDischarging)
                system.StampResistor(system.Node(s.Timing), gnd, MonostableTimer.DischargeResistance);
        }

        StampInternal(system);
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        var supply = SupplyVoltage(context);

        for (var i = 0; i < _sections.Length; i++)
        {
            var s = _sections[i];
            var now = s.Triggers.Select(t => context.ReadInput(t, Levels)).ToArray();
            var clear = s.Clear is null ? LogicState.Unknown : context.ReadInput(s.Clear, Levels);
            var cleared = s.Clear is not null && ClearAsserted(clear);

            var trigger = TriggerLevel(now);

            if (cleared)
            {
                s.Timer.Cancel();
            }
            else if (!context.IsInitializing)
            {
                var rose = trigger && !s.LastTrigger;
                var released = ClearReleaseTriggers && ClearAsserted(s.LastClear) && trigger;
                if (rose || released) s.Timer.Trigger(IsRetriggerable);
            }

            // Settling counts as no edge at all: what the pins power up at is where they start from.
            s.LastTrigger = trigger;
            s.LastClear = clear;

            // The capacitor's own voltage, not the timing pin's: while it discharges the two differ by
            // what is dropped across the CEXT end.
            s.Timer.Update(context.NodeVoltage(s.Timing) - context.NodeVoltage(s.Capacitor), supply, TimingConstant);

            var q = LogicStateExtensions.FromBool(s.Timer.IsRunning);
            Request(context, 2 * i, q, delay);
            Request(context, (2 * i) + 1, q.Invert(), delay);
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        foreach (var s in _sections)
        {
            s.Timer.Reset();
            s.LastTrigger = true;
            s.LastClear = LogicState.Unknown;
        }
    }
}

/// <summary>
/// 74121 monostable multivibrator with a Schmitt-trigger input: one section, <b>not retriggerable</b>.
/// It triggers when B rises with either A input low, or when an A input falls with B high (and the
/// other A high) — T = B·(A1·A2)'. t<sub>w</sub> = 0.7·R·C (ln 2): the capacitor between CEXT (pin 10)
/// and REXT/CEXT (pin 11), the resistor from pin 11 to VCC — or pin 9, RINT, tied to VCC to use the
/// internal 2 kΩ instead.
/// <para>
/// Pinout: 1=Q', 2=NC, 3=A1, 4=A2, 5=B, 6=Q, 7=GND, 8=NC, 9=RINT, 10=CEXT, 11=REXT/CEXT, 12=NC,
/// 13=NC, 14=VCC.
/// </para>
/// </summary>
public sealed class Ic74121 : MonostableIc
{
    /// <summary>The internal timing resistor between RINT and REXT/CEXT, in ohms.</summary>
    public const double InternalResistance = 2e3;

    public Ic74121() : base(14)
    {
        PropagationDelay = 45e-9;
        TimingConstant = Math.Log(2);

        var pins = new Terminal[15];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 14));

        var qNot = Pin(1, "Q'", TerminalType.Output);
        foreach (var nc in new[] { 2, 8, 12, 13 }) Pin(nc, "NC", TerminalType.Passive);
        var a1 = Pin(3, "A1", TerminalType.Input);
        var a2 = Pin(4, "A2", TerminalType.Input);
        var b = Pin(5, "B", TerminalType.Input);
        var q = Pin(6, "Q", TerminalType.Output);
        Gnd = Pin(7, "GND", TerminalType.Ground);
        InternalResistor = Pin(9, "RINT", TerminalType.Passive);
        var cext = Pin(10, "CEXT", TerminalType.Passive);
        var timing = Pin(11, "REXT/CEXT", TerminalType.Passive);
        Vcc = Pin(14, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        Configure(new Section { Triggers = [a1, a2, b], Q = q, QNot = qNot, Capacitor = cext, Timing = timing });
    }

    public override string PartNumber => "74121";

    public override bool IsRetriggerable => false;

    /// <summary>Pin 9: one end of the internal 2 kΩ, whose other end is REXT/CEXT. Tie it to VCC to use it.</summary>
    public Terminal InternalResistor { get; }

    protected override bool TriggerLevel(LogicState[] pins) =>
        pins[2].IsHigh() && !(pins[0].IsHigh() && pins[1].IsHigh());

    protected override bool ClearAsserted(LogicState clear) => false;

    protected override void StampInternal(MnaSystem system) =>
        system.StampResistor(system.Node(InternalResistor), system.Node(Pins(0).Timing), InternalResistance);
}

/// <summary>
/// Base for the dual one-shots on the 74123's pinout, the retriggerable 74123 and the non-retriggerable
/// 74221. Each half triggers when A falls with B high or B rises with A low (T = A'·B); CLR low ends
/// the pulse and holds it off. On the 74123 a CLR going high with A low and B high is a trigger too; on
/// the 74221 it is not.
/// <para>
/// Pinout: 1=1A, 2=1B, 3=1CLR, 4=1Q', 5=2Q, 6=2CEXT, 7=2REXT/CEXT, 8=GND, 9=2A, 10=2B, 11=2CLR, 12=2Q',
/// 13=1Q, 14=1CEXT, 15=1REXT/CEXT, 16=VCC.
/// </para>
/// </summary>
public abstract class DualTtlMonostableIc : MonostableIc
{
    protected DualTtlMonostableIc(double k) : base(16)
    {
        PropagationDelay = 33e-9;
        TimingConstant = k;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        var first = new Section
        {
            Triggers = [Pin(1, "1A", TerminalType.Input), Pin(2, "1B", TerminalType.Input)],
            Clear = Pin(3, "1CLR", TerminalType.Input),
            QNot = Pin(4, "1Q'", TerminalType.Output),
            Q = Pin(13, "1Q", TerminalType.Output),
            Capacitor = Pin(14, "1CEXT", TerminalType.Passive),
            Timing = Pin(15, "1REXT/CEXT", TerminalType.Passive),
        };

        var second = new Section
        {
            Q = Pin(5, "2Q", TerminalType.Output),
            Capacitor = Pin(6, "2CEXT", TerminalType.Passive),
            Timing = Pin(7, "2REXT/CEXT", TerminalType.Passive),
            Triggers = [Pin(9, "2A", TerminalType.Input), Pin(10, "2B", TerminalType.Input)],
            Clear = Pin(11, "2CLR", TerminalType.Input),
            QNot = Pin(12, "2Q'", TerminalType.Output),
        };

        Gnd = Pin(8, "GND", TerminalType.Ground);
        Vcc = Pin(16, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        Configure(first, second);
    }

    protected override bool TriggerLevel(LogicState[] pins) => pins[0].IsLow() && pins[1].IsHigh();

    protected override bool ClearAsserted(LogicState clear) => clear.IsLow();
}

/// <summary>
/// 74123 dual retriggerable monostable with clear. t<sub>w</sub> = K·R·C with K about 0.45 for the
/// 74LS123 with C above 1000 pF (the original 74123's formula is 0.28·R·C·(1 + 0.7/R), R in kΩ).
/// </summary>
public sealed class Ic74123() : DualTtlMonostableIc(0.45)
{
    public override string PartNumber => "74123";

    public override bool IsRetriggerable => true;

    protected override bool ClearReleaseTriggers => true;
}

/// <summary>
/// 74221 dual non-retriggerable monostable with Schmitt-trigger B inputs and clear, on the 74123's
/// pinout: t<sub>w</sub> = 0.7·R·C (ln 2), and a trigger during the pulse is ignored.
/// </summary>
public sealed class Ic74221() : DualTtlMonostableIc(Math.Log(2))
{
    public override string PartNumber => "74221";

    public override bool IsRetriggerable => false;
}

/// <summary>
/// Base for the CMOS dual retriggerable, resettable monostables, the 4528 and its precision successor
/// the 4538, on one pinout. +TR rising with −TR high triggers, and so does −TR falling with +TR low;
/// RESET low ends the pulse and holds it off. The capacitor goes between CX (pins 1 and 15, which are
/// VSS inside the chip) and RX/CX (2 and 14), the resistor from RX/CX to VDD.
/// <para>
/// Pinout: 1=1CX, 2=1RX/CX, 3=1RST, 4=1+TR, 5=1−TR, 6=1Q, 7=1Q', 8=VSS, 9=2Q', 10=2Q, 11=2−TR,
/// 12=2+TR, 13=2RST, 14=2RX/CX, 15=2CX, 16=VDD.
/// </para>
/// </summary>
public abstract class DualCmosMonostableIc : MonostableIc
{
    protected DualCmosMonostableIc(double k) : base(16)
    {
        Levels = LogicLevels.Cmos5V;
        PropagationDelay = 250e-9;
        TimingConstant = k;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        var first = new Section
        {
            Capacitor = Pin(1, "1CX", TerminalType.Passive),
            Timing = Pin(2, "1RX/CX", TerminalType.Passive),
            Clear = Pin(3, "1RST", TerminalType.Input),
            Triggers = [Pin(4, "1+TR", TerminalType.Input), Pin(5, "1-TR", TerminalType.Input)],
            Q = Pin(6, "1Q", TerminalType.Output),
            QNot = Pin(7, "1Q'", TerminalType.Output),
        };

        var second = new Section
        {
            QNot = Pin(9, "2Q'", TerminalType.Output),
            Q = Pin(10, "2Q", TerminalType.Output),
            Triggers = [Pin(12, "2+TR", TerminalType.Input), Pin(11, "2-TR", TerminalType.Input)],
            Clear = Pin(13, "2RST", TerminalType.Input),
            Timing = Pin(14, "2RX/CX", TerminalType.Passive),
            Capacitor = Pin(15, "2CX", TerminalType.Passive),
        };

        Gnd = Pin(8, "VSS", TerminalType.Ground);
        Vcc = Pin(16, "VDD", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        Configure(first, second);
    }

    public override bool IsRetriggerable => true;

    protected override bool TriggerLevel(LogicState[] pins) => pins[0].IsHigh() || pins[1].IsLow();

    protected override bool ClearAsserted(LogicState clear) => clear.IsLow();
}

/// <summary>
/// 4538 dual precision retriggerable monostable: t<sub>w</sub> = R·C (K = 1) for C of 10 nF and up,
/// held closely over supply and temperature — the CMOS one-shot to reach for.
/// </summary>
public sealed class Ic4538() : DualCmosMonostableIc(1.0)
{
    public override string PartNumber => "4538";
}

/// <summary>
/// 4528 dual retriggerable monostable, the 4538's predecessor on the same pinout. Its pulse depends on
/// the supply: t<sub>w</sub> ≈ 0.2·R·C·ln(VDD − VSS), so K is about 0.32 at 5 V — the default here, and
/// a setting to change for another rail.
/// </summary>
public sealed class Ic4528() : DualCmosMonostableIc(0.2 * Math.Log(5.0))
{
    public override string PartNumber => "4528";
}

/// <summary>
/// 4047 low-power monostable/astable multivibrator: one RC, two jobs, chosen by pins.
/// <para>
/// The timing parts go where the datasheet puts them — the capacitor from C (pin 1) to RC COMMON
/// (pin 3), the resistor from R (pin 2) to RC COMMON — and they are the real components on the canvas.
/// <b>Astable</b> (ASTABLE, pin 4, high, or ¬ASTABLE, pin 5, low): pins 2 and 1 are two inverters
/// around the timing node, exactly the 4060's oscillator, so the network runs at a period of about
/// 2.2·R·C, brought out on OSC (pin 13); Q and Q' are that divided by two — a square wave with a
/// period of 4.4·R·C and a duty cycle of exactly half, which is what the part is for.
/// <b>Monostable</b> (pin 4 low and pin 5 high): R is held high and C low, and a trigger — +TRIGGER
/// rising with −TRIGGER low, or −TRIGGER falling with +TRIGGER high — discharges the timing node and
/// lets it charge back through R, Q high for t<sub>M</sub> = 2.48·R·C. RETRIGGER rising starts the pulse
/// again; EXTERNAL RESET high ends it, or stops the oscillator.
/// </para>
/// <para>
/// Pinout: 1=C, 2=R, 3=RC, 4=AST, 5=AST', 6=-TR, 7=VSS, 8=+TR, 9=RST, 10=Q, 11=Q', 12=RETR, 13=OSC,
/// 14=VDD.
/// </para>
/// </summary>
public sealed partial class Ic4047 : DigitalIc
{
    private const int ResistorOutput = 0;
    private const int CapacitorOutput = 1;

    private readonly MonostableTimer _timer = new();
    private bool _above;
    private bool _q;
    private bool _lastTrigger = true;
    private bool _lastRetrigger = true;

    public Ic4047() : base(14)
    {
        PropagationDelay = 200e-9;
        Levels = LogicLevels.Cmos5V;

        var pins = new Terminal[15];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 14));

        CapacitorPin = Pin(1, "C", TerminalType.Output);
        ResistorPin = Pin(2, "R", TerminalType.Output);
        Timing = Pin(3, "RC", TerminalType.Input);
        Astable = Pin(4, "AST", TerminalType.Input);
        AstableNot = Pin(5, "AST'", TerminalType.Input);
        NegativeTrigger = Pin(6, "-TR", TerminalType.Input);
        Gnd = Pin(7, "VSS", TerminalType.Ground);
        PositiveTrigger = Pin(8, "+TR", TerminalType.Input);
        ExternalReset = Pin(9, "RST", TerminalType.Input);
        Q = Pin(10, "Q", TerminalType.Output);
        QNot = Pin(11, "Q'", TerminalType.Output);
        Retrigger = Pin(12, "RETR", TerminalType.Input);
        Oscillator = Pin(13, "OSC", TerminalType.Output);
        Vcc = Pin(14, "VDD", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];

        // RC COMMON is listed as an input for its leakage, but it is read against the oscillator's own
        // threshold, as on the 4060, not against VIH and VIL.
        ConfigurePins(
            [Timing, Astable, AstableNot, NegativeTrigger, PositiveTrigger, ExternalReset, Retrigger],
            [ResistorPin, CapacitorPin, Q, QNot, Oscillator]);
    }

    public override string PartNumber => "4047";

    /// <summary>Pin 1: the timing capacitor's driven end.</summary>
    public Terminal CapacitorPin { get; }

    /// <summary>Pin 2: the timing resistor's driven end.</summary>
    public Terminal ResistorPin { get; }

    /// <summary>Pin 3, RC COMMON: where the resistor and capacitor meet.</summary>
    public Terminal Timing { get; }

    /// <summary>Pin 4: high runs it astable.</summary>
    public Terminal Astable { get; }

    /// <summary>Pin 5: low runs it astable.</summary>
    public Terminal AstableNot { get; }

    public Terminal PositiveTrigger { get; }

    public Terminal NegativeTrigger { get; }

    public Terminal Retrigger { get; }

    /// <summary>Pin 9, active high.</summary>
    public Terminal ExternalReset { get; }

    public Terminal Q { get; }

    public Terminal QNot { get; }

    /// <summary>Pin 13: the oscillator itself, twice Q's frequency.</summary>
    public Terminal Oscillator { get; }

    /// <summary>The monostable pulse: t<sub>M</sub> = K·R·C, K = 2.48 on the datasheet.</summary>
    [ObservableProperty]
    public partial double TimingConstant { get; set; } = 2.48;

    /// <summary>Where the oscillator's inverter switches, as a fraction of the supply.</summary>
    [ObservableProperty]
    public partial double SwitchingThresholdFraction { get; set; } = 0.5;

    /// <summary>True while running astable.</summary>
    public bool IsAstable { get; private set; }

    /// <summary>True while Q is high.</summary>
    public bool QHigh => _q;

    public override string ValueLabel => IsAstable ? "astable" : _timer.IsRunning ? "pulse" : "idle";

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);

        if (_timer.IsDischarging)
            system.StampResistor(system.Node(Timing), system.Node(Gnd), MonostableTimer.DischargeResistance);
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        bool High(Terminal pin) => context.ReadInput(pin, Levels).IsHigh();

        var supply = SupplyVoltage(context);
        var volts = context.NodeVoltage(Timing) - context.NodeVoltage(Gnd);
        var wasAstable = IsAstable;
        IsAstable = High(Astable) || !High(AstableNot);

        var trigger = High(PositiveTrigger) && !High(NegativeTrigger);
        var retrigger = High(Retrigger);

        if (High(ExternalReset))
        {
            _timer.Cancel();
            _q = false;
            _above = false;

            // Reset stops the oscillator as well as clearing Q: both timing pins parked low.
            Request(context, ResistorOutput, LogicState.Low, delay);
            Request(context, CapacitorOutput, LogicState.Low, delay);
        }
        else if (IsAstable)
        {
            if (!wasAstable) _timer.Cancel();

            var before = _above;
            var threshold = SwitchingThresholdFraction * supply;
            _above = volts >= threshold;

            // Q divides the oscillator by two, on its rising edge. Not while settling, when an astable
            // cannot hold still and no time passes.
            if (!context.IsInitializing && _above && !before) _q = !_q;

            Request(context, ResistorOutput, _above ? LogicState.Low : LogicState.High, delay);
            Request(context, CapacitorOutput, _above ? LogicState.High : LogicState.Low, delay);
        }
        else
        {
            if (!context.IsInitializing)
            {
                if (trigger && !_lastTrigger) _timer.Trigger(retriggerable: false);
                if (retrigger && !_lastRetrigger) _timer.Trigger(retriggerable: true);
            }

            // Timed on the capacitor itself, charged from the R pin's high to the C pin's low — a whisker
            // under the supply — and judged across it rather than from the timing pin, which during the
            // discharge sits only a little above the C pin's end while the capacitor still holds volts.
            _timer.Update(
                context.NodeVoltage(Timing) - context.NodeVoltage(CapacitorPin),
                OutputVoltage(LogicState.High) - OutputVoltage(LogicState.Low),
                TimingConstant);
            _q = _timer.IsRunning;

            // R charges the node towards VDD; C's end is held at VSS, so the capacitor charges as if to ground.
            Request(context, ResistorOutput, LogicState.High, delay);
            Request(context, CapacitorOutput, LogicState.Low, delay);
        }

        _lastTrigger = trigger;
        _lastRetrigger = retrigger;

        var q = LogicStateExtensions.FromBool(_q);
        Request(context, 2, q, delay);
        Request(context, 3, q.Invert(), delay);
        Request(context, 4, LogicStateExtensions.FromBool(IsAstable && _above), delay);
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _timer.Reset();
        _above = false;
        _q = false;
        _lastTrigger = true;
        _lastRetrigger = true;
        IsAstable = false;
    }
}
