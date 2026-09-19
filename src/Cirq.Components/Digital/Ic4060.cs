using Cirq.Components.Nonlinear;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

/// <summary>
/// A 4060 fourteen-stage ripple counter with an on-chip oscillator.
/// <para>
/// The oscillator is the reason to reach for this instead of a 4040. Hang a resistor and a
/// capacitor off three pins and the part clocks itself, so a single chip takes you from nothing to
/// a divided-down output — which is why almost every long-delay timer ever built is a 4060 and two
/// passives.
/// </para>
/// <para>
/// The oscillator is not a frequency setting here; it is built out of what the datasheet says is
/// inside. <c>RS</c> is the input of an inverter, <c>REXT</c> is that inverter's output, and
/// <c>CEXT</c> is the output of a second one behind it. Those three pins plus your Rt and Ct are
/// the whole circuit, so the frequency falls out of the network rather than being announced: the
/// timing node charges towards REXT through Rt, and each time it crosses a threshold CEXT flips and
/// shoves it a supply's worth in the other direction through Ct. Change a resistor on the canvas
/// and the frequency changes because the circuit changed.
/// </para>
/// <para>
/// Wire it as the datasheet does — Rt from REXT, Ct from CEXT and Rs from RS, all three meeting at
/// one node — and it lands on the datasheet's <c>f = 1/(2.3·Rt·Ct)</c>. Rs wants to be several
/// times Rt: its job is to keep the input protection diodes, which are modelled, from having a say
/// in the frequency. Leave it out and they get one, exactly as on a breadboard.
/// </para>
/// <para>
/// Three things about this part regularly cost an afternoon. The counter advances on the
/// <b>falling</b> edge. Master reset is active <b>high</b>, and it stops the oscillator as well as
/// clearing the count, so a floating MR pin gets you a chip that does nothing at all. And the first
/// three stages are not brought out, nor is the eleventh: the outputs run Q4 to Q10 and then jump
/// to Q12, so the divisions available are ÷16 up to ÷1024 and then ÷4096 to ÷16384.
/// </para>
/// <para>
/// Pinout: 1=Q12, 2=Q13, 3=Q14, 4=Q6, 5=Q5, 6=Q7, 7=Q4, 8=VSS,
/// 9=CEXT, 10=REXT, 11=RS, 12=MR, 13=Q9, 14=Q8, 15=Q10, 16=VDD.
/// </para>
/// </summary>
public sealed partial class Ic4060 : DigitalIc
{
    /// <summary>Which counter stages reach a pin. Stages 1-3 and 11 do not.</summary>
    private static readonly int[] StageNumbers = [4, 5, 6, 7, 8, 9, 10, 12, 13, 14];

    /// <summary>Index into the scheduled outputs of the two oscillator pins.</summary>
    private const int RextOutput = 10;
    private const int CextOutput = 11;

    private readonly Terminal[] _outputs = new Terminal[10];

    /// <summary>What the oscillator input was last called, which is the hysteresis.</summary>
    private bool _above;

    private bool _lastClock;
    private int _count;

    private double _previousUpperClamp;
    private double _previousLowerClamp;
    private bool _limitedThisIteration;

    public Ic4060() : base(16)
    {
        PropagationDelay = 200e-9;
        Levels = LogicLevels.Cmos5V;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        _outputs[7] = Pin(1, "Q12", TerminalType.Output);
        _outputs[8] = Pin(2, "Q13", TerminalType.Output);
        _outputs[9] = Pin(3, "Q14", TerminalType.Output);
        _outputs[2] = Pin(4, "Q6", TerminalType.Output);
        _outputs[1] = Pin(5, "Q5", TerminalType.Output);
        _outputs[3] = Pin(6, "Q7", TerminalType.Output);
        _outputs[0] = Pin(7, "Q4", TerminalType.Output);
        Gnd = Pin(8, "VSS", TerminalType.Ground);
        CapacitorPin = Pin(9, "CEXT", TerminalType.Output);
        ResistorPin = Pin(10, "REXT", TerminalType.Output);
        ClockOscillator = Pin(11, "RS", TerminalType.Input);
        MasterReset = Pin(12, "MR", TerminalType.Input);
        _outputs[5] = Pin(13, "Q9", TerminalType.Output);
        _outputs[4] = Pin(14, "Q8", TerminalType.Output);
        _outputs[6] = Pin(15, "Q10", TerminalType.Output);
        Vcc = Pin(16, "VDD", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];

        // RS is listed as an input so it gets the family's input leakage, but it is read against
        // the oscillator's own threshold rather than VIH/VIL: the whole part turns on where that
        // inverter decides the node has crossed over.
        ConfigurePins([ClockOscillator, MasterReset], [.. _outputs, ResistorPin, CapacitorPin]);
    }

    /// <summary>The ten stage outputs that reach pins: Q4-Q10 and Q12-Q14.</summary>
    public IReadOnlyList<Terminal> Outputs => _outputs;

    /// <summary>Pin 9, CEXT: the second inverter's output, where the timing capacitor goes.</summary>
    public Terminal CapacitorPin { get; }

    /// <summary>Pin 10, REXT: the first inverter's output, where the timing resistor goes.</summary>
    public Terminal ResistorPin { get; }

    /// <summary>Pin 11, RS: the oscillator input, and the clock input if you drive it yourself.</summary>
    public Terminal ClockOscillator { get; }

    /// <summary>Pin 12, MR: active high, clears the counter and stops the oscillator.</summary>
    public Terminal MasterReset { get; }

    /// <summary>Where the oscillator's inverter switches, as a fraction of the supply.</summary>
    [ObservableProperty]
    public partial double SwitchingThresholdFraction { get; set; } = 0.5;

    /// <summary>
    /// Hysteresis on the oscillator input, as a fraction of the supply.
    /// <para>
    /// A plain mid-rail threshold with none at all puts the oscillator at <c>1/(2.2·Rt·Ct)</c>,
    /// which is the textbook figure for two inverters and an RC. The datasheet quotes
    /// <c>1/(2.3·Rt·Ct)</c>, and this is the band that accounts for the difference — a couple of
    /// hundred millivolts at 5 V.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial double HysteresisFraction { get; set; } = 0.037;

    public override string PartNumber => "4060";

    /// <summary>The internal count, 0 through 16383 — more stages than reach pins.</summary>
    public int Count => _count;

    /// <summary>The stage number each entry of <see cref="Outputs"/> carries.</summary>
    public static IReadOnlyList<int> Stages => StageNumbers;

    /// <summary>True while the oscillator input is above its threshold.</summary>
    public bool OscillatorHigh => _above;

    // The protection diodes make this a nonlinear device, which a logic part is not usually.
    public override bool IsNonlinear => true;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);

        _limitedThisIteration = false;

        var rs = system.Node(ClockOscillator);

        // The input protection diodes, which on this part are not a detail. The timing node is
        // driven a whole supply past the rail twice a cycle by the capacitor, and on a real chip
        // these are what catches it. Rs is what stops them dragging the frequency around, so
        // leaving Rs out has a visible consequence here rather than none.
        StampClamp(system, state, rs, system.Node(Vcc), ref _previousUpperClamp);
        StampClamp(system, state, system.Node(Gnd), rs, ref _previousLowerClamp);
    }

    private void StampClamp(
        MnaSystem system, SimulationState state, int anode, int cathode, ref double previous)
    {
        const double saturation = 1e-12;

        var vt = state.ThermalVoltage;
        var raw = system.IterationVoltageAcross(anode, cathode);
        var limited = Junction.Limit(raw, previous, vt, Junction.CriticalVoltage(saturation, vt));

        if (Math.Abs(limited - raw) > 1e-12) _limitedThisIteration = true;
        previous = limited;

        var (current, conductance) = Junction.Evaluate(limited, saturation, vt);
        system.StampNorton(anode, cathode, conductance, current - (conductance * limited));
    }

    public override bool HasConverged(MnaSystem system, SimulationState state) => !_limitedThisIteration;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);

        if (context.ReadInput(MasterReset, Levels) == LogicState.High)
        {
            _count = 0;
            _above = false;
            _lastClock = false;

            // Reset stops the oscillator as well as clearing the count. Parking both pins low
            // drains the timing node, so nothing restarts until reset is released.
            context.Schedule(this, RextOutput, LogicState.Low, delay);
            context.Schedule(this, CextOutput, LogicState.Low, delay);
            DriveStages(context, delay);
            return;
        }

        var supply = SupplyVoltage(context);
        var reference = context.NodeVoltage(Gnd);
        var volts = context.NodeVoltage(ClockOscillator) - reference;

        var half = HysteresisFraction / 2.0;
        var upper = (SwitchingThresholdFraction + half) * supply;
        var lower = (SwitchingThresholdFraction - half) * supply;

        if (volts >= upper) _above = true;
        else if (volts <= lower) _above = false;

        // The counter advances on the HIGH-to-LOW transition, whether that comes from the
        // oscillator or from a clock you drive into RS yourself.
        //
        // Not while the logic is being settled, though. Settling asks every device to hold still,
        // and an astable cannot: each pass round the loop flips the oscillator, re-solves, and
        // flips it back, so the counter would arrive at its first real time point already dozens
        // of counts in. No time passes during the settle, so no edges have happened.
        if (!context.IsInitializing && _lastClock && !_above) _count = (_count + 1) & 0x3FFF;

        _lastClock = _above;

        // REXT inverts RS and CEXT inverts REXT. With Rt and Ct outside the package that is the
        // oscillator: REXT charges the timing node, and CEXT kicks it past the rail on every flip.
        context.Schedule(this, RextOutput, _above ? LogicState.Low : LogicState.High, delay);
        context.Schedule(this, CextOutput, _above ? LogicState.High : LogicState.Low, delay);

        DriveStages(context, delay);
    }

    private void DriveStages(IDigitalContext context, double delay)
    {
        for (var i = 0; i < StageNumbers.Length; i++)
        {
            var bit = (_count & (1 << (StageNumbers[i] - 1))) != 0;
            context.Schedule(this, i, bit ? LogicState.High : LogicState.Low, delay);
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _count = 0;
        _above = false;
        _lastClock = false;
        _previousUpperClamp = 0;
        _previousLowerClamp = 0;
        _limitedThisIteration = false;
    }
}
