using Cirq.Components.Nonlinear;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;

namespace Cirq.Components.Ics;

/// <summary>
/// The LM13700 dual operational transconductance amplifier, the voltage-controlled heart of a great many synthesiser
/// filters and VCAs. Each half's output is a current, not a voltage: Iout = Iabc · tanh((V+ − V−) / 2VT), so about
/// 19.2 · Iabc siemens for a small input, where Iabc is the current pushed into its AMP BIAS pin (which sits two diode
/// drops above V−). With the linearising diodes biased (a current into DIODE BIAS) the inputs take a wider signal. Each
/// half also has a Darlington follower, BUF OUT about 1.4 V below BUF IN, which wants a resistor to V− to pull it down.
/// Pinout: 1 and 16 AMP BIAS, 2 and 15 DIODE BIAS, 3 and 14 IN+, 4 and 13 IN−, 5 and 12 OUT, 6 V−, 7 and 10 BUF IN,
/// 8 and 9 BUF OUT, 11 V+.
/// </summary>
public sealed partial class Lm13700 : CircuitComponent
{
    private const double Vt = 0.025852;
    private readonly (Terminal Bias, Terminal Diode, Terminal Plus, Terminal Minus, Terminal Out, Terminal BufIn, Terminal BufOut)[] _halves;
    private readonly JunctionStamp[] _diodes = [new(), new(), new(), new()];
    private readonly double[] _iabc = new double[2];

    public Lm13700()
    {
        var pins = new Terminal[17];
        Terminal Pin(int n, string name, TerminalType type) => pins[n] = new Terminal($"p{n}", name, type, Digital.DipPackage.PinOffset(n, 16));
        var bias1 = Pin(1, "AMP BIAS1", TerminalType.Input);
        var diode1 = Pin(2, "DIODE BIAS1", TerminalType.Input);
        var plus1 = Pin(3, "IN1+", TerminalType.Input);
        var minus1 = Pin(4, "IN1-", TerminalType.Input);
        var out1 = Pin(5, "OUT1", TerminalType.Output);
        NegativeSupply = Pin(6, "V-", TerminalType.Power);
        var bufIn1 = Pin(7, "BUF IN1", TerminalType.Input);
        var bufOut1 = Pin(8, "BUF OUT1", TerminalType.Output);
        var bufOut2 = Pin(9, "BUF OUT2", TerminalType.Output);
        var bufIn2 = Pin(10, "BUF IN2", TerminalType.Input);
        PositiveSupply = Pin(11, "V+", TerminalType.Power);
        var out2 = Pin(12, "OUT2", TerminalType.Output);
        var minus2 = Pin(13, "IN2-", TerminalType.Input);
        var plus2 = Pin(14, "IN2+", TerminalType.Input);
        var diode2 = Pin(15, "DIODE BIAS2", TerminalType.Input);
        var bias2 = Pin(16, "AMP BIAS2", TerminalType.Input);
        _halves = [(bias1, diode1, plus1, minus1, out1, bufIn1, bufOut1), (bias2, diode2, plus2, minus2, out2, bufIn2, bufOut2)];
        Terminals = [.. pins.Skip(1)];
    }

    public Terminal PositiveSupply { get; }

    public Terminal NegativeSupply { get; }

    /// <summary>One half's pins, indexed from 0.</summary>
    public (Terminal AmpBias, Terminal DiodeBias, Terminal NonInverting, Terminal Inverting, Terminal Output, Terminal BufferIn, Terminal BufferOut) Half(int index) =>
        _halves[index];

    /// <summary>The bias current each half is taking, in amps, at the last accepted point.</summary>
    public double AmplifierBias(int index) => _iabc[index];

    /// <summary>A half's small-signal transconductance, Iabc / 2VT, in siemens.</summary>
    public double Transconductance(int index) => _iabc[index] / (2 * Vt);

    public override string ComponentType => "LM13700";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => $"LM13700 gm {Cirq.Core.Units.SiPrefix.Format(Transconductance(0), "S")}";

    public override bool IsNonlinear => true;

    // Per half: the AMP BIAS pin held two diode drops above V− (its current is Iabc), and the buffer.
    public override int VoltageSourceCount => 4;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var vneg = system.Node(NegativeSupply);
        var vpos = system.Node(PositiveSupply);
        system.StampCurrentSource(vpos, vneg, 1.3e-3);
        for (var i = 0; i < 2; i++)
        {
            var h = _halves[i];
            var (bias, plus, minus, output) = (system.Node(h.Bias), system.Node(h.Plus), system.Node(h.Minus), system.Node(h.Out));
            var biasBranch = system.Branch(this, i * 2);

            // AMP BIAS at V− + 1.2 V through 200 Ω, so the current into it is the branch current (into the + node).
            system.StampTheveninSource(biasBranch, bias, vneg, 1.2, 200);
            var iabc = Math.Max(system.IterationValue(biasBranch), 0);
            var on = system.IterationValue(biasBranch) > 0 ? 1.0 : 0.0;

            // Iout = Iabc·tanh(vd / 2VT), out of the OUT pin; linearised about the last iteration in both vd and Iabc.
            var vd = system.IterationVoltageAcross(plus, minus);
            var t = Math.Tanh(vd / (2 * Vt));
            var dt = (1 - (t * t)) / (2 * Vt);
            var gm = iabc * dt;
            var i0 = iabc * t;
            // KCL at OUT: current leaving = -(i), so add the injected current with a minus on the row.
            system.Add(output, plus, -gm);
            system.Add(output, minus, gm);
            system.Add(output, biasBranch, -t * on);
            system.AddRhs(output, i0 - (gm * vd) - (t * on * iabc));
            system.StampConductance(output, vneg, 1e-9);

            // The inputs: 1 µA of bias each, as 400 kΩ to the diode-bias node's level, and the linearising diodes.
            system.StampResistor(plus, vneg, 50e6);
            system.StampResistor(minus, vneg, 50e6);
            var diode = system.Node(h.Diode);
            _diodes[i * 2].Stamp(system, diode, plus, 1e-14);
            _diodes[(i * 2) + 1].Stamp(system, diode, minus, 1e-14);
            system.StampResistor(diode, vneg, 100e6);

            // The Darlington buffer: BUF OUT = BUF IN − 1.4 V through 50 Ω, BUF IN taking almost nothing.
            var buffer = system.Branch(this, (i * 2) + 1);
            var (bufIn, bufOut) = (system.Node(h.BufIn), system.Node(h.BufOut));
            system.Add(bufOut, buffer, 1.0);
            system.Add(buffer, bufOut, 1.0);
            system.Add(buffer, bufIn, -1.0);
            system.Add(buffer, buffer, -50);
            system.AddRhs(buffer, -1.4);
            system.StampResistor(bufIn, vneg, 100e6);
        }
    }

    public override bool HasConverged(MnaSystem system, SimulationState state) => !_diodes.Any(d => d.Limited);

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        for (var i = 0; i < 2; i++) _iabc[i] = Math.Max(system.BranchCurrentAt(system.Branch(this, i * 2)), 0);
        NotifyValueChanged();
    }

    public override void ResetState()
    {
        foreach (var d in _diodes) d.Reset();
        Array.Clear(_iabc);
    }
}
