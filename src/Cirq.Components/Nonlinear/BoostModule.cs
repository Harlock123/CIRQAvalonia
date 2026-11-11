using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Nonlinear;

/// <summary>
/// An MT3608 boost converter module: VIN+, VOUT+ and their shared ground, stepping 2-24 V up to what its trimmer sets,
/// as high as 28 V. Like the buck module it's modelled by what it does on average: the output holds
/// <see cref="OutputVoltage"/>, or with the setting below the input it passes the input through its diode, half a volt
/// down (a boost can't step down); and the input draws the output's power over the efficiency, so a 5 V supply feeding
/// 12 V at 0.2 A gives about half an amp. The 1.2 MHz switching isn't modelled.
/// </summary>
public partial class BoostModule : CircuitComponent
{
    private const double Corner = 0.05;

    public BoostModule()
    {
        Input = new Terminal("in", "VIN+", TerminalType.Input, new Point(-40, -14));
        Output = new Terminal("out", "VOUT+", TerminalType.Output, new Point(40, -14));
        Ground = new Terminal("gnd", "GND", TerminalType.Ground, new Point(0, 40));
        Terminals = [Input, Output, Ground];
    }

    public Terminal Input { get; }

    public Terminal Output { get; }

    public Terminal Ground { get; }

    [ObservableProperty]
    [Operable("Output", Minimum = 2, Maximum = 28, Unit = "V")]
    public partial double OutputVoltage { get; set; } = 12.0;

    [ObservableProperty]
    public partial double Efficiency { get; set; } = 0.93;

    [ObservableProperty]
    public partial double QuiescentCurrent { get; set; } = 2e-3;

    public override string ComponentType => "Boost Module";

    public override string DesignatorPrefix => "PS";

    public override string ValueLabel => $"MT3608 {SiPrefix.Format(OutputVoltage, "V")}";

    public override bool IsNonlinear => true;

    public override int VoltageSourceCount => 1;

    public double InputCurrent { get; private set; }

    public double OutputCurrent { get; private set; }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var (inNode, outNode, gnd) = (system.Node(Input), system.Node(Output), system.Node(Ground));
        var branch = system.Branch(this);
        var vin = system.IterationVoltage(inNode) - system.IterationVoltage(gnd);
        var set = Math.Clamp(OutputVoltage, 2, 28);

        // The larger of the setting and the input through the diode, the corner rounded so Newton sees a slope.
        var floor = vin - 0.5;
        var d = set - floor;
        var root = Math.Sqrt((d * d) + (Corner * Corner));
        var target = 0.5 * (set + floor + root);
        var slope = 0.5 * (1 - (d / root));
        if (vin < 1.8) (target, slope) = (Math.Max(floor, 0), vin > 0.5 ? 1 : 0);

        const double rout = 0.08;
        system.Add(outNode, branch, 1.0);
        system.Add(gnd, branch, -1.0);
        system.Add(branch, outNode, 1.0);
        system.Add(branch, gnd, -1.0);
        system.Add(branch, branch, -rout);
        system.Add(branch, inNode, -slope);
        system.Add(branch, gnd, slope);
        system.AddRhs(branch, target - (slope * vin));

        var iout = -system.IterationValue(branch);
        var vout = system.IterationVoltage(outNode) - system.IterationVoltage(gnd);
        var draw = QuiescentCurrent + (vin > 0.5 ? Math.Max(0, vout * iout) / (Math.Clamp(Efficiency, 0.05, 1) * vin) : 0);
        system.StampCurrentSource(inNode, gnd, draw);
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        base.CommitTimeStep(system, state);
        OutputCurrent = -system.BranchCurrent(this, 0);
        var vin = system.NodeVoltage(Input) - system.NodeVoltage(Ground);
        var vout = system.NodeVoltage(Output) - system.NodeVoltage(Ground);
        InputCurrent = QuiescentCurrent + (vin > 0.5 ? Math.Max(0, vout * OutputCurrent) / (Math.Clamp(Efficiency, 0.05, 1) * vin) : 0);
    }

    partial void OnOutputVoltageChanged(double value) => NotifyValueChanged();
}
