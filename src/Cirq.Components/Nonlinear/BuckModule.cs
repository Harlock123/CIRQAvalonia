using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Nonlinear;

/// <summary>
/// An LM2596 buck converter module, the little blue board with a trimmer: IN+, OUT+ and the ground pad they share.
/// <para>
/// It is modelled by what it does on average, not switch by switch: the output holds <see cref="OutputVoltage"/> (set by
/// the trimmer) behind a little output resistance, as long as the input is at least the dropout above it, below which
/// it follows the input down; and the input draws the output's power divided by the efficiency, plus a few milliamps
/// of its own, so the supply feeding it sees the current a buck really takes, less than the load's when the input is
/// higher than the output. Ripple and the 150 kHz switching aren't modelled.
/// </para>
/// </summary>
public partial class BuckModule : CircuitComponent
{
    private const double Corner = 0.05;

    public BuckModule()
    {
        Input = new Terminal("in", "IN+", TerminalType.Input, new Point(-40, -14));
        Output = new Terminal("out", "OUT+", TerminalType.Output, new Point(40, -14));
        Ground = new Terminal("gnd", "GND", TerminalType.Ground, new Point(0, 40));
        Terminals = [Input, Output, Ground];
    }

    public Terminal Input { get; }

    public Terminal Output { get; }

    public Terminal Ground { get; }

    /// <summary>What the trimmer is set to, from 1.25 V up to the input less the dropout.</summary>
    [ObservableProperty]
    [Operable("Output", Minimum = 1.25, Maximum = 35, Unit = "V")]
    public partial double OutputVoltage { get; set; } = 5.0;

    /// <summary>The power out over the power in: an LM2596 manages 80 to 90 % at a few hundred milliamps.</summary>
    [ObservableProperty]
    public partial double Efficiency { get; set; } = 0.88;

    /// <summary>How far below the input the output can get, in volts.</summary>
    [ObservableProperty]
    public partial double DropoutVoltage { get; set; } = 1.5;

    /// <summary>The current it takes doing nothing, in amps.</summary>
    [ObservableProperty]
    public partial double QuiescentCurrent { get; set; } = 5e-3;

    public override string ComponentType => "Buck Module";

    public override string DesignatorPrefix => "PS";

    public override string ValueLabel => $"LM2596 {SiPrefix.Format(OutputVoltage, "V")}";

    public override bool IsNonlinear => true;

    public override int VoltageSourceCount => 1;

    /// <summary>The input current at the last solve, in amps.</summary>
    public double InputCurrent { get; private set; }

    /// <summary>The output current at the last solve, in amps.</summary>
    public double OutputCurrent { get; private set; }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var (inNode, outNode, gnd) = (system.Node(Input), system.Node(Output), system.Node(Ground));
        var branch = system.Branch(this);
        var vin = system.IterationVoltage(inNode) - system.IterationVoltage(gnd);
        var set = Math.Max(1.25, OutputVoltage);

        // The smaller of the setting and what the input leaves room for, the corner rounded so Newton sees a slope.
        var room = vin - DropoutVoltage;
        var d = set - room;
        var root = Math.Sqrt((d * d) + (Corner * Corner));
        var target = 0.5 * (set + room - root);
        var slope = 0.5 * (1 + (d / root));     // d(target)/d(vin)

        // v(out) − v(gnd) − R·i − slope·(v(in) − v(gnd)) = target − slope·vin
        const double rout = 0.05;
        system.Add(outNode, branch, 1.0);
        system.Add(gnd, branch, -1.0);
        system.Add(branch, outNode, 1.0);
        system.Add(branch, gnd, -1.0);
        system.Add(branch, branch, -rout);
        system.Add(branch, inNode, -slope);
        system.Add(branch, gnd, slope);
        system.AddRhs(branch, target - (slope * vin));

        // The input pays for the output's power, at the efficiency.
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
