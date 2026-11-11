using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Electromechanical;

/// <summary>
/// An ACS712 Hall-effect current sensor module: the current goes through IP+ to IP− (1.2 mΩ of copper, isolated from
/// the rest), and OUT sits at half VCC plus 185 mV an amp for the 5 A part, 100 mV for the 20 A and 66 mV for the 30 A,
/// either way round. An Arduino reads OUT on an analog pin and takes away 512.
/// </summary>
public sealed partial class Acs712 : CircuitComponent
{
    public const double PathResistance = 1.2e-3;

    public Acs712()
    {
        CurrentIn = new Terminal("ip+", "IP+", TerminalType.Passive, new Point(-40, -20));
        CurrentOut = new Terminal("ip-", "IP-", TerminalType.Passive, new Point(-40, 20));
        Vcc = new Terminal("vcc", "VCC", TerminalType.Power, new Point(40, -20));
        Output = new Terminal("out", "OUT", TerminalType.Output, new Point(40, 0));
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(40, 20));
        Terminals = [CurrentIn, CurrentOut, Vcc, Output, Gnd];
    }

    public Terminal CurrentIn { get; }
    public Terminal CurrentOut { get; }
    public Terminal Vcc { get; }
    public Terminal Output { get; }
    public Terminal Gnd { get; }

    /// <summary>The part's range in amps: 5, 20 or 30, which sets the sensitivity.</summary>
    [ObservableProperty]
    public partial int Range { get; set; } = 5;

    /// <summary>Volts out an amp through it.</summary>
    public double Sensitivity => Range switch { >= 30 => 0.066, >= 20 => 0.100, _ => 0.185 };

    /// <summary>The current through IP+ to IP− at the last accepted point, in amps.</summary>
    public double Current { get; private set; }

    public override string ComponentType => "Current Sensor";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => $"ACS712-{Range}A {Cirq.Core.Units.SiPrefix.Format(Current, "A")}";

    public override int VoltageSourceCount => 1;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var (plus, minus, vcc, output, gnd) = (system.Node(CurrentIn), system.Node(CurrentOut), system.Node(Vcc), system.Node(Output), system.Node(Gnd));
        system.StampResistor(plus, minus, PathResistance);

        // OUT − GND − ½(VCC − GND) − (S/R)(IP+ − IP−) − Rout·i = 0, through 2 Ω.
        var branch = system.Branch(this, 0);
        var k = Sensitivity / PathResistance;
        system.Add(output, branch, 1.0);
        system.Add(gnd, branch, -1.0);
        system.Add(branch, output, 1.0);
        system.Add(branch, gnd, -1.0);
        system.Add(branch, vcc, -0.5);
        system.Add(branch, gnd, 0.5);
        system.Add(branch, plus, -k);
        system.Add(branch, minus, k);
        system.Add(branch, branch, -2.0);
        system.StampResistor(vcc, gnd, 500);    // its 10 mA
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        Current = (system.NodeVoltage(CurrentIn) - system.NodeVoltage(CurrentOut)) / PathResistance;
        NotifyValueChanged();
    }

    partial void OnRangeChanged(int value) => NotifyValueChanged();
}
