using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Nonlinear;

/// <summary>
/// An ICL7660 switched-capacitor voltage inverter: a negative rail made from a positive one, with
/// no inductor and no transformer.
/// <para>
/// Everything else in the Power group makes a positive voltage out of a larger positive voltage.
/// This makes a negative one, which matters because several of the op-amps want a supply either
/// side of ground and a single-supply circuit has nowhere to get one.
/// </para>
/// <para>
/// It does it by moving charge rather than by regulating. A capacitor is charged across the
/// supply, then disconnected, turned round, and dumped onto the output — so the output ends up at
/// roughly minus the input. That is genuinely what is modelled here: four switches that change
/// over at the oscillator rate, with your capacitor between them.
/// </para>
/// <para>
/// Because it is charge per cycle rather than a regulator, its output impedance is about
/// <c>1/(f·C)</c>, and that is where the surprises come from. A small pump capacitor or a slow
/// oscillator gives a negative rail that sags the moment anything draws from it, and no amount of
/// smoothing on the output fixes it. There is no regulation at all: the output follows the input,
/// so a supply that droops takes the negative rail with it.
/// </para>
/// <para>
/// Pinout: 1=NC, 2=CAP+, 3=GND, 4=CAP-, 5=VOUT, 6=LV, 7=OSC, 8=V+.
/// </para>
/// </summary>
public partial class ChargePump : CircuitComponent
{
    public ChargePump()
    {
        Supply = new Terminal("v+", "V+", TerminalType.Power, new Point(-40, -30));
        Ground = new Terminal("gnd", "GND", TerminalType.Ground, new Point(-40, 30));
        CapacitorPositive = new Terminal("cap+", "CAP+", TerminalType.Passive, new Point(40, -30));
        CapacitorNegative = new Terminal("cap-", "CAP-", TerminalType.Passive, new Point(40, 0));
        Output = new Terminal("out", "VOUT", TerminalType.Output, new Point(40, 30));

        Terminals = [Supply, CapacitorPositive, Ground, CapacitorNegative, Output];
    }

    public Terminal Supply { get; }

    public Terminal Ground { get; }

    /// <summary>The pump capacitor goes between this and <see cref="CapacitorNegative"/>.</summary>
    public Terminal CapacitorPositive { get; }

    public Terminal CapacitorNegative { get; }

    /// <summary>The negative rail. It needs a reservoir capacitor to ground.</summary>
    public Terminal Output { get; }

    /// <summary>Rate the switches change over, in hertz.</summary>
    [ObservableProperty]
    public partial double OscillatorFrequency { get; set; } = 10e3;

    /// <summary>
    /// Resistance of each internal switch, in ohms. Four of them are in the charge path across
    /// the two phases, and together with the charge-per-cycle term they set the output impedance
    /// — chosen here so that lands near the 55 ohms the datasheet quotes at 10 kHz with 10 uF.
    /// </summary>
    [ObservableProperty]
    public partial double SwitchResistance { get; set; } = 8.0;

    /// <summary>Resistance of an open switch, in ohms.</summary>
    [ObservableProperty]
    public partial double OffResistance { get; set; } = 1e9;

    public override string ComponentType => "ICL7660";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => $"{SiPrefix.Format(OutputVoltage, "V")}";

    /// <summary>Which half of the cycle it is in: true while the pump capacitor is charging.</summary>
    public bool IsCharging { get; private set; }

    /// <summary>The negative rail at the last solved point, relative to ground.</summary>
    public double OutputVoltage { get; private set; }

    /// <summary>Supply voltage at the last solved point.</summary>
    public double InputVoltage { get; private set; }

    /// <summary>
    /// Output impedance implied by the pump capacitance and the rate, in ohms. Nothing else about
    /// the part matters as much: it is why the rail sags.
    /// </summary>
    public double OutputImpedanceFor(double pumpCapacitance) =>
        1.0 / (Math.Max(OscillatorFrequency, 1e-9) * Math.Max(pumpCapacitance, 1e-18));

    public IReadOnlyList<string> Violations
    {
        get
        {
            if (InputVoltage < 1.5) return [];

            // No regulation: if the output is not close to minus the input, something is loading
            // it harder than the charge per cycle can supply.
            var expected = -InputVoltage;
            if (OutputVoltage > expected * 0.6)
            {
                return [$"the output is {SiPrefix.Format(OutputVoltage, "V")} where it should be " +
                        $"near {SiPrefix.Format(expected, "V")} — it is being loaded harder than " +
                        "the pump capacitor can carry. A charge pump has no regulation; make the " +
                        "capacitor bigger or the oscillator faster"];
            }

            return [];
        }
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        // Two phases, alternating at the oscillator rate. Which one we are in is a function of
        // time rather than of state, so a rejected step cannot leave the switches out of step.
        var halfCycles = state.Time * 2.0 * Math.Max(OscillatorFrequency, 1e-9);
        IsCharging = (long)Math.Floor(halfCycles) % 2 == 0;

        var on = Math.Max(SwitchResistance, 1e-3);
        var off = Math.Max(OffResistance, 1.0);

        var vplus = system.Node(Supply);
        var gnd = system.Node(Ground);
        var cp = system.Node(CapacitorPositive);
        var cn = system.Node(CapacitorNegative);
        var vout = system.Node(Output);

        // Charging: the capacitor sits across the supply.
        system.StampResistor(vplus, cp, IsCharging ? on : off);
        system.StampResistor(gnd, cn, IsCharging ? on : off);

        // Pumping: it is turned round, its top to ground and its bottom to the output, which
        // drags the output below ground by whatever it was charged to.
        system.StampResistor(gnd, cp, IsCharging ? off : on);
        system.StampResistor(vout, cn, IsCharging ? off : on);
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var reference = system.NodeVoltage(Ground);

        InputVoltage = system.NodeVoltage(Supply) - reference;
        OutputVoltage = system.NodeVoltage(Output) - reference;

        NotifyValueChanged();
    }

    public override void ResetState()
    {
        OutputVoltage = 0;
        InputVoltage = 0;
        IsCharging = false;
    }
}
