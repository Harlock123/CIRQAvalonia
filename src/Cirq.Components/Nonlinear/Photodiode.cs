using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Nonlinear;

/// <summary>
/// A photodiode: a junction that turns light into a current, with none of the gain a
/// phototransistor has and none of the delay that gain costs.
/// <para>
/// The palette has three light sensors now and choosing between them is the whole point of having
/// three. An <b>LDR</b> is a resistance that falls as it is lit — cheap, enormous range, and far
/// too slow for anything that changes. A <b>phototransistor</b> is a photodiode with a transistor
/// built round it: the same current multiplied by a few hundred, which is convenient, but the
/// gain comes from stored charge in a base and so does the microsecond it takes to get rid of it.
/// A <b>photodiode</b> has no gain at all and is correspondingly quick — nanoseconds — and linear
/// over six or seven decades of light where a phototransistor's gain drifts with current and
/// temperature.
/// </para>
/// <para>
/// That makes it the part you measure light with rather than merely notice it with, and it is why
/// every optical receiver, pulse oximeter, laser rangefinder and camera exposure meter has one.
/// The price is that the current is <i>tiny</i> — hundreds of nanoamps in a lit room where a
/// phototransistor gives you hundreds of microamps — so it is useless into a load resistor and
/// wants a <b>transimpedance amplifier</b>: one of the op-amps in this library with a large
/// feedback resistor and the diode across its inputs, which holds the diode at zero volts and
/// turns its current straight into a voltage.
/// </para>
/// <para>
/// Which way round it is wired decides what kind of sensor it is. <b>Reverse biased</b>
/// (photoconductive) the junction capacitance falls and it gets fast, at the cost of a dark
/// current that sets the noise floor. At <b>zero bias</b> (photovoltaic) there is no dark current
/// at all, which is what a precision light meter wants, and it is slower. Both are here, because
/// both fall out of the same model — it is the same device either way, and only the bias differs.
/// </para>
/// </summary>
public partial class Photodiode : TwoTerminalComponent, IInteractiveComponent, ICurrentReporting
{
    private double _previousVoltage;
    private bool _limitedThisIteration;

    public Photodiode() : base("A", "K")
    {
    }

    /// <summary>The anode.</summary>
    public Terminal Anode => A;

    /// <summary>The cathode — the end that goes to the positive rail when reverse biased.</summary>
    public Terminal Cathode => B;

    /// <summary>
    /// Light falling on it, in lux. Ordinary room lighting is a few hundred; an overcast day
    /// outdoors is ten thousand, and direct sun a hundred thousand.
    /// </summary>
    [ObservableProperty]
    [Operable("Light", Minimum = 0.1, Maximum = 1e5, Unit = "lx", IsLogarithmic = true)]
    public partial double Illuminance { get; set; } = 300.0;

    /// <summary>What it swings to when you double-click it.</summary>
    [ObservableProperty]
    public partial double AlternateIlluminance { get; set; } = 5.0;

    /// <summary>
    /// Current per lux, in amps. A nanoamp per lux is typical of a small silicon photodiode —
    /// about a thousandth of what the phototransistor beside it gives, which is the gain it does
    /// not have.
    /// </summary>
    [ObservableProperty]
    public partial double Responsivity { get; set; } = 1e-9;

    /// <summary>
    /// Reverse leakage in the dark, in amps. It is what sets the smallest signal worth measuring,
    /// and it is why a precision light meter runs the diode at zero bias, where there is none.
    /// </summary>
    [ObservableProperty]
    public partial double DarkCurrent { get; set; } = 1e-9;

    /// <summary>Junction saturation current, in amps.</summary>
    [ObservableProperty]
    public partial double SaturationCurrent { get; set; } = 1e-12;

    /// <summary>Junction ideality.</summary>
    [ObservableProperty]
    public partial double Ideality { get; set; } = 1.0;

    /// <summary>Resistance across the junction, in ohms.</summary>
    [ObservableProperty]
    public partial double ShuntResistance { get; set; } = 1e9;

    /// <summary>
    /// Junction capacitance at zero bias, in farads. It is the whole speed story: reverse bias
    /// widens the depletion region, the capacitance falls, and the RC with the load falls with it.
    /// </summary>
    [ObservableProperty]
    public partial double ZeroBiasCapacitance { get; set; } = 50e-12;

    public override string ComponentType => "Photodiode";

    public override string DesignatorPrefix => "D";

    public override string ValueLabel => $"{Illuminance:0} lx · {SiPrefix.Format(PhotoCurrent, "A")}";

    public override bool IsNonlinear => true;

    public string InteractionHint => "Light and shade it";

    /// <summary>The current the light is making, in amps, before the junction has any say.</summary>
    public double PhotoCurrent =>
        DarkCurrent + (Math.Max(Illuminance, 0.0) * Math.Max(Responsivity, 0.0));

    /// <summary>Anode-to-cathode voltage at the last solved point. Negative means reverse biased.</summary>
    public double Voltage { get; private set; }

    /// <summary>
    /// Current into the cathode at the last solved point, in amps — the photocurrent, less
    /// whatever the junction has taken back once it is forward biased.
    /// </summary>
    public double Current { get; private set; }

    /// <summary>True when the cathode is the more positive end, which is the fast way to use it.</summary>
    public bool IsReverseBiased => Voltage < -0.05;

    /// <summary>
    /// Junction capacitance at the present bias, in farads, by the usual square-root law. Worth
    /// reading: it is the number that decides how fast the diode can be, and it is why a
    /// transimpedance amplifier's feedback capacitor is chosen against it.
    /// </summary>
    public double Capacitance =>
        ZeroBiasCapacitance / Math.Sqrt(1.0 + Math.Max(-Voltage, 0.0) / 0.7);

    public void Interact() =>
        (Illuminance, AlternateIlluminance) = (AlternateIlluminance, Illuminance);

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var anode = system.Node(A);
        var cathode = system.Node(B);

        _limitedThisIteration = false;

        system.StampResistor(anode, cathode, Math.Max(ShuntResistance, 1.0));

        // Light makes a current from cathode to anode inside the device, which is the same
        // direction a solar cell's does — a photodiode and a solar cell are the same thing, one
        // optimised to measure and the other to collect. Injected into the anode, so it
        // forward-biases the junction rather than driving the diode further into reverse: that is
        // what makes an unloaded one sit at its photovoltaic voltage instead of running away.
        system.StampCurrentSource(cathode, anode, PhotoCurrent);

        var vt = state.ThermalVoltage * Math.Max(Ideality, 0.1);

        var raw = system.IterationVoltageAcross(anode, cathode);
        var limited = Junction.Limit(raw, _previousVoltage, vt,
            Junction.CriticalVoltage(SaturationCurrent, vt));

        if (Math.Abs(limited - raw) > 1e-12) _limitedThisIteration = true;
        _previousVoltage = limited;

        var (current, conductance) = Junction.Evaluate(limited, SaturationCurrent, vt);

        system.StampNorton(anode, cathode, conductance, current - (conductance * limited));
    }

    public override bool HasConverged(MnaSystem system, SimulationState state) => !_limitedThisIteration;

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        Voltage = system.NodeVoltage(A) - system.NodeVoltage(B);

        var vt = state.ThermalVoltage * Math.Max(Ideality, 0.1);
        var (junction, _) = Junction.Evaluate(Voltage, SaturationCurrent, vt);

        // What the light made, less whatever the junction and the shunt took back.
        Current = PhotoCurrent - junction - (Voltage / Math.Max(ShuntResistance, 1.0));
    }

    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state) =>
        ReferenceEquals(terminal, B) ? Current : -Current;

    public override void ResetState()
    {
        Voltage = 0;
        Current = 0;
        _previousVoltage = 0;
        _limitedThisIteration = false;
    }
}
