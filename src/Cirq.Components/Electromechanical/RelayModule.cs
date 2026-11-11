using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Electromechanical;

/// <summary>
/// A one-channel 5 V relay module, the blue board with the screw terminals: VCC, GND and IN on one side, COM, NO and NC
/// on the other. Its own transistor, flyback diode and indicator LED drive the coil, so a pin can switch it directly.
/// Most are triggered low (<see cref="ActiveHigh"/> off): IN pulled down below about 1.5 V energises it, IN high or
/// left open releases it, and IN takes a couple of milliamps through the opto's LED to do it. The coil takes 70 mA from
/// VCC; the contact closes 10 ms after the coil's energised and opens 5 ms after it's let go.
/// </summary>
public sealed partial class RelayModule : CircuitComponent, IBreakpointSource
{
    private double _changeAt = double.NaN;
    private bool _driven;

    public RelayModule()
    {
        Vcc = new Terminal("vcc", "VCC", TerminalType.Power, new Point(-50, -20));
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(-50, 0));
        Input = new Terminal("in", "IN", TerminalType.Input, new Point(-50, 20));
        Common = new Terminal("com", "COM", TerminalType.Passive, new Point(50, 0));
        NormallyOpen = new Terminal("no", "NO", TerminalType.Passive, new Point(50, -20));
        NormallyClosed = new Terminal("nc", "NC", TerminalType.Passive, new Point(50, 20));
        Terminals = [Vcc, Gnd, Input, NormallyOpen, Common, NormallyClosed];
    }

    public Terminal Vcc { get; }
    public Terminal Gnd { get; }
    public Terminal Input { get; }
    public Terminal Common { get; }
    public Terminal NormallyOpen { get; }
    public Terminal NormallyClosed { get; }

    /// <summary>A module set (by its jumper) to trigger on a high IN rather than a low one.</summary>
    [ObservableProperty]
    public partial bool ActiveHigh { get; set; }

    public bool IsEnergised { get; private set; }

    /// <summary>Whether COM is on NO.</summary>
    public bool ContactClosed { get; private set; }

    public override string ComponentType => "Relay Module";

    public override string DesignatorPrefix => "K";

    public override string ValueLabel => ContactClosed ? "relay: COM-NO" : "relay: COM-NC";

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var (vcc, gnd, input) = (system.Node(Vcc), system.Node(Gnd), system.Node(Input));

        // IN: the opto's LED and its resistor, about 1.5 kΩ to VCC for an active-low board, to GND for an active-high one.
        system.StampResistor(input, ActiveHigh ? gnd : vcc, 1.5e3);
        system.StampResistor(vcc, gnd, IsEnergised ? 70 : 100e3);
        var (com, no, nc) = (system.Node(Common), system.Node(NormallyOpen), system.Node(NormallyClosed));
        system.StampResistor(com, no, ContactClosed ? 0.05 : 1e9);
        system.StampResistor(com, nc, ContactClosed ? 1e9 : 0.05);
    }

    public double? NextBreakpointAfter(double time) => !double.IsNaN(_changeAt) && _changeAt > time + 1e-12 ? _changeAt : null;

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var supply = system.NodeVoltage(Vcc) - system.NodeVoltage(Gnd);
        var vin = system.NodeVoltage(Input) - system.NodeVoltage(Gnd);
        var driven = supply > 3.5 && (ActiveHigh ? vin > 2.5 : supply - vin > supply - 1.5 && vin < 1.5);
        if (driven != _driven)
        {
            _driven = driven;
            IsEnergised = driven;
            _changeAt = state.Time + (driven ? 10e-3 : 5e-3);
        }

        if (!double.IsNaN(_changeAt) && state.Time >= _changeAt - 1e-12)
        {
            ContactClosed = _driven;
            _changeAt = double.NaN;
            NotifyValueChanged();
        }
    }

    public override void ResetState()
    {
        (_changeAt, _driven, IsEnergised, ContactClosed) = (double.NaN, false, false, false);
    }
}
