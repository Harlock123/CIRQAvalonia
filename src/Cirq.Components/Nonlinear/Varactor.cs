using Cirq.Core.Simulation;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Nonlinear;

/// <summary>
/// A varactor: a diode used for the one property every diode has and almost no circuit wants — the
/// capacitance of its own junction, which falls as the reverse voltage across it rises.
/// <para>
/// Reverse-bias any junction and the depletion layer widens. That layer is an insulator with
/// conducting silicon either side of it, which is a capacitor, and a wider one is a smaller
/// capacitor. A varactor is a diode built to make that relationship large, repeatable and worth
/// using: <c>C = C₀ / (1 + V/V<sub>j</sub>)<sup>m</sup></c>.
/// </para>
/// <para>
/// It is how everything was tuned before synthesisers, and how a great deal still is. Put one
/// across an LC tank, take the tuning knob off the variable capacitor and replace it with a
/// potentiometer feeding a few volts of bias, and the resonance moves. That is a car radio, and it
/// is why the tuning control can be at the front panel while the tuned circuit is at the aerial.
/// </para>
/// <para>
/// Two things decide whether one is any use. <b>How far it swings</b>: the grading coefficient
/// <c>m</c> is about 0.5 for an ordinary junction, which gives a two-to-one capacitance range over
/// a usable bias, and 1 or more for the hyperabrupt doping profiles made for tuning, which give
/// ten to one. And <b>staying reverse-biased</b>: let the signal across it swing the junction into
/// forward conduction and it stops being a capacitor and starts being a diode, which is the usual
/// reason a varactor-tuned oscillator distorts or refuses to start.
/// </para>
/// </summary>
public partial class Varactor : Diode
{
    private double _previousVoltage;
    private double _previousCurrent;
    private double _conductance;
    private double _equivalentCurrent;
    private bool _solved;

    public Varactor()
        : base(DiodeModel.D1N4148)
    {
    }

    /// <summary>
    /// Capacitance with no bias across it, in farads. The number a varactor is sold by, and the
    /// top of its range.
    /// </summary>
    [ObservableProperty]
    public partial double ZeroBiasCapacitance { get; set; } = 100e-12;

    /// <summary>
    /// The junction's built-in potential, in volts. Around 0.7 for silicon, and what sets how
    /// quickly the capacitance starts falling as bias is applied.
    /// </summary>
    [ObservableProperty]
    public partial double JunctionPotential { get; set; } = 0.7;

    /// <summary>
    /// The grading coefficient. About 0.5 for an ordinary abrupt junction; 1 or more for the
    /// hyperabrupt profiles made for tuning, which is what buys a ten-to-one range instead of
    /// two-to-one.
    /// </summary>
    [ObservableProperty]
    public partial double GradingCoefficient { get; set; } = 1.0;

    public override string ComponentType => "Varactor";

    public override string DesignatorPrefix => "D";

    public override string ValueLabel => SiPrefix.Format(Capacitance, "F");

    /// <summary>The reverse voltage across it at the last linearisation point, in volts.</summary>
    public double ReverseVoltage { get; private set; }

    /// <summary>
    /// The capacitance that reverse voltage works out to. This is the whole component: everything
    /// else it does, it does because it is a diode.
    /// </summary>
    public double Capacitance => CapacitanceAt(ReverseVoltage);

    /// <summary>What it would be at a given reverse bias, for choosing a part before wiring one.</summary>
    public double CapacitanceAt(double reverseVolts)
    {
        var potential = Math.Max(JunctionPotential, 1e-3);
        var ratio = 1.0 + (Math.Max(reverseVolts, 0.0) / potential);

        return Math.Max(ZeroBiasCapacitance, 0.0) / Math.Pow(ratio, Math.Max(GradingCoefficient, 0.01));
    }

    /// <summary>
    /// True when the signal has pushed the junction out of reverse bias. A varactor that is not
    /// reverse-biased is not a capacitor any more, and nothing downstream of it means what it
    /// should — which is the usual reason a varactor-tuned oscillator distorts or will not start.
    /// </summary>
    /// <remarks>
    /// Only once something has been solved. A varactor sitting on the canvas with nothing wired to
    /// it has no bias either, and marking every freshly placed one as faulty would make the
    /// warning worth nothing.
    /// </remarks>
    public bool HasLostReverseBias => _solved && ReverseVoltage < 0.05;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);

        var anode = system.Node(A);
        var cathode = system.Node(B);

        // Reverse voltage taken from the linearisation point, so the capacitance follows the bias
        // as Newton settles rather than lagging a step behind it.
        ReverseVoltage = system.IterationVoltageAcross(cathode, anode);

        if (!state.IsTransient)
        {
            _conductance = 0;
            _equivalentCurrent = 0;
            return;
        }

        // Quasi-static: the companion is built from the capacitance the present bias gives. A
        // charge-based model would be more exact across a large swing, but a varactor is only
        // being used properly while the swing across it is small compared with its bias.
        var h = state.TimeStep;
        var c = Math.Max(Capacitance, 1e-18);

        if (state.EffectiveIntegration == IntegrationMethod.Trapezoidal)
        {
            _conductance = 2.0 * c / h;
            _equivalentCurrent = -((_conductance * _previousVoltage) + _previousCurrent);
        }
        else
        {
            _conductance = c / h;
            _equivalentCurrent = -_conductance * _previousVoltage;
        }

        system.StampNorton(anode, cathode, _conductance, _equivalentCurrent);
    }

    /// <summary>The junction capacitance at the bias point, which is the point of the part.</summary>
    public override void StampAc(AcSystem system, SimulationState state)
    {
        base.StampAc(system, state);

        system.StampCapacitance(system.Node(A), system.Node(B), Math.Max(Capacitance, 1e-18));
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        base.CommitTimeStep(system, state);

        var across = system.NodeVoltage(A) - system.NodeVoltage(B);

        _previousCurrent = state.IsTransient ? (_conductance * across) + _equivalentCurrent : 0.0;
        _previousVoltage = across;

        ReverseVoltage = -across;
        _solved = true;
    }

    public override void ResetState()
    {
        base.ResetState();

        _previousVoltage = 0;
        _previousCurrent = 0;
        _conductance = 0;
        _equivalentCurrent = 0;
        ReverseVoltage = 0;
        _solved = false;
    }

    partial void OnZeroBiasCapacitanceChanged(double value) => NotifyValueChanged();
}
