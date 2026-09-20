using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Ics;

/// <summary>
/// An AD633 analog multiplier: two differential inputs in, their product out.
/// <para>
/// <c>W = (X1 − X2)(Y1 − Y2) / 10 V + Z</c>, and the ten volts in the denominator is the whole of
/// what makes the part usable. Without it, two ten-volt inputs would ask for a hundred volts out;
/// with it, full scale in gives full scale out and the arithmetic stays inside the supplies.
/// Everybody meets that divisor as a surprise — a multiplier with both inputs at 5 V gives 2.5 V,
/// not 25 — and it is not an error.
/// </para>
/// <para>
/// One part, and a remarkable number of jobs, all of which are the same job:
/// </para>
/// <list type="bullet">
/// <item><b>Amplitude modulation.</b> Carrier into X, audio into Y, and the output is the one
/// multiplied by the other — which is what AM <i>is</i>, rather than something done to a signal.</item>
/// <item><b>Mixing.</b> Two sines multiplied give their sum and difference frequencies and nothing
/// else, which is every superheterodyne receiver ever built.</item>
/// <item><b>Squaring, and from it RMS.</b> Tie X to Y and the output is the square; average that
/// and take the root and you have true RMS, which is what an RMS meter does inside.</item>
/// <item><b>A voltage-controlled amplifier.</b> Signal into X, gain into Y.</item>
/// <item><b>A phase detector.</b> Multiply two signals of the same frequency and the average of
/// the product is the cosine of the angle between them.</item>
/// </list>
/// <para>
/// The Z input adds straight through to the output, which is what lets one of these be a
/// summing multiplier, or be given the offset a divider configuration needs.
/// </para>
/// </summary>
public partial class AnalogMultiplier : CircuitComponent
{
    public AnalogMultiplier()
    {
        X1 = new Terminal("x1", "X1", TerminalType.Input, new Point(-50, -36));
        X2 = new Terminal("x2", "X2", TerminalType.Input, new Point(-50, -12));
        Y1 = new Terminal("y1", "Y1", TerminalType.Input, new Point(-50, 12));
        Y2 = new Terminal("y2", "Y2", TerminalType.Input, new Point(-50, 36));
        Z = new Terminal("z", "Z", TerminalType.Input, new Point(0, 40));
        Output = new Terminal("w", "W", TerminalType.Output, new Point(50, 0));
        PositiveSupply = new Terminal("v+", "V+", TerminalType.Power, new Point(-20, -40));
        NegativeSupply = new Terminal("v-", "V-", TerminalType.Power, new Point(20, 40));

        Terminals = [X1, X2, Y1, Y2, Z, Output, PositiveSupply, NegativeSupply];
    }

    public Terminal X1 { get; }

    public Terminal X2 { get; }

    public Terminal Y1 { get; }

    public Terminal Y2 { get; }

    /// <summary>Added straight to the output, so one of these can sum as well as multiply.</summary>
    public Terminal Z { get; }

    public Terminal Output { get; }

    public Terminal PositiveSupply { get; }

    public Terminal NegativeSupply { get; }

    /// <summary>
    /// The divisor, in volts. Ten on a real AD633, set by an internal reference, and the reason
    /// full scale in gives full scale out rather than the square of it.
    /// </summary>
    [ObservableProperty]
    public partial double ScaleVoltage { get; set; } = 10.0;

    /// <summary>Differential input resistance on X and Y, in ohms.</summary>
    [ObservableProperty]
    public partial double InputResistance { get; set; } = 10e6;

    /// <summary>Output resistance, in ohms.</summary>
    [ObservableProperty]
    public partial double OutputResistance { get; set; } = 50.0;

    /// <summary>How close the output gets to a supply rail before it stops, in volts.</summary>
    [ObservableProperty]
    public partial double OutputSwingHeadroom { get; set; } = 2.0;

    public override string ComponentType => "Analog Multiplier";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => "AD633";

    /// <summary>Nonlinear: the output is a product of two node voltages, which is the definition.</summary>
    public override bool IsNonlinear => true;

    /// <summary>One branch for the output.</summary>
    public override int VoltageSourceCount => 1;

    /// <summary>The product at the last solved point, in volts.</summary>
    public double Product { get; private set; }

    /// <summary>True when the answer has run into a supply rail rather than being the product.</summary>
    public bool IsClipping { get; private set; }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var x1 = system.Node(X1);
        var x2 = system.Node(X2);
        var y1 = system.Node(Y1);
        var y2 = system.Node(Y2);
        var z = system.Node(Z);
        var branch = system.Branch(this);

        var resistance = Math.Max(InputResistance, 1.0);
        system.StampConductance(x1, x2, 1.0 / resistance);
        system.StampConductance(y1, y2, 1.0 / resistance);

        var scale = Math.Max(ScaleVoltage, 1e-6);

        var vx = system.IterationVoltageAcross(x1, x2);
        var vy = system.IterationVoltageAcross(y1, y2);
        var vz = system.IterationVoltage(z);

        var railHigh = system.IterationVoltage(system.Node(PositiveSupply)) - OutputSwingHeadroom;
        var railLow = system.IterationVoltage(system.Node(NegativeSupply)) + OutputSwingHeadroom;

        var raw = (vx * vy / scale) + vz;
        var clipped = railHigh > railLow ? Math.Clamp(raw, railLow, railHigh) : raw;

        IsClipping = Math.Abs(clipped - raw) > 1e-9;

        // Linearised as the product rule: d(xy) = y·dx + x·dy. Past a rail the derivatives go to
        // zero, which is right — nothing at the input moves the output any more — but they are
        // floored, because a row with nothing in it disconnects the output from its own inputs and
        // leaves Newton with no way back.
        var open = IsClipping ? 1e-6 : 1.0;

        var dx = vy / scale * open;
        var dy = vx / scale * open;
        var dz = open;

        // W = clipped + dx·(vx' − vx) + dy·(vy' − vy) + dz·(vz' − vz), as a branch row.
        system.Add(system.Node(Output), branch, 1.0);
        system.Add(branch, system.Node(Output), 1.0);
        system.Add(branch, branch, -Math.Max(OutputResistance, 1e-6));

        system.Add(branch, x1, -dx);
        system.Add(branch, x2, dx);
        system.Add(branch, y1, -dy);
        system.Add(branch, y2, dy);
        system.Add(branch, z, -dz);

        system.AddRhs(branch, clipped - (dx * vx) - (dy * vy) - (dz * vz));
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state) =>
        Product = system.NodeVoltage(Output);

    public override void ResetState()
    {
        Product = 0;
        IsClipping = false;
    }

    partial void OnScaleVoltageChanged(double value) => NotifyValueChanged();
}
