using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Ics;

/// <summary>
/// The NE602 (SA612) double-balanced mixer with its oscillator, the front end of a thousand homebrew receivers: the
/// signal differentially on IN A and IN B (about 1.5 kΩ each, biased inside), the local oscillator on OSC B, and the
/// products — the sum and difference frequencies — differentially on OUT A and OUT B (about 1.5 kΩ each, sitting near
/// VCC − 1.5 V), with a conversion gain of about 14 dB. The oscillator transistor is taken as driven from outside: inject
/// the LO into OSC B through a capacitor (200-300 mV peak to peak, as the datasheet allows), and the cell switches on
/// it about OSC B's own bias, half the supply from the chip's divider.
/// </summary>
public sealed partial class Ne602 : CircuitComponent
{
    public Ne602()
    {
        var pins = new Terminal[9];
        Terminal Pin(int n, string name, TerminalType type) => pins[n] = new Terminal($"p{n}", name, type, Digital.DipPackage.PinOffset(n, 8));
        InputA = Pin(1, "IN A", TerminalType.Input);
        InputB = Pin(2, "IN B", TerminalType.Input);
        Gnd = Pin(3, "GND", TerminalType.Ground);
        OutputA = Pin(4, "OUT A", TerminalType.Output);
        OutputB = Pin(5, "OUT B", TerminalType.Output);
        OscillatorBase = Pin(6, "OSC B", TerminalType.Input);
        OscillatorEmitter = Pin(7, "OSC E", TerminalType.Passive);
        Vcc = Pin(8, "VCC", TerminalType.Power);
        Terminals = [.. pins.Skip(1)];
    }

    public Terminal InputA { get; }
    public Terminal InputB { get; }
    public Terminal Gnd { get; }
    public Terminal OutputA { get; }
    public Terminal OutputB { get; }
    public Terminal OscillatorBase { get; }
    public Terminal OscillatorEmitter { get; }
    public Terminal Vcc { get; }

    /// <summary>Differential volts out a differential volt in, with the oscillator switching fully.</summary>
    [ObservableProperty]
    public partial double Gain { get; set; } = 8.0;

    public override string ComponentType => "NE602";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => "NE602 mixer";

    public override bool IsNonlinear => true;

    public override int VoltageSourceCount => 4;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var (gnd, vcc, ina, inb, osc) = (system.Node(Gnd), system.Node(Vcc), system.Node(InputA), system.Node(InputB), system.Node(OscillatorBase));
        var supply = system.IterationVoltageAcross(vcc, gnd);
        system.StampCurrentSource(vcc, gnd, 2.4e-3);

        // The inputs, each 1.5 kΩ to 1.4 V; the oscillator base and emitter biased from the supply.
        system.StampTheveninSource(system.Branch(this, 0), ina, gnd, 1.4, 1.5e3);
        system.StampTheveninSource(system.Branch(this, 1), inb, gnd, 1.4, 1.5e3);
        system.StampResistor(osc, vcc, 20e3);
        system.StampResistor(osc, gnd, 20e3);
        system.StampResistor(system.Node(OscillatorEmitter), gnd, 20e3);

        // Each output: v(out) − v(gnd) − R·i = (VCC − GND) − 1.5 + sign·vin·tanh(vlo / 60 mV), linearised in vin and vlo.
        var vin0 = system.IterationVoltageAcross(ina, inb);
        var vosc0 = system.IterationVoltage(osc) - system.IterationVoltage(gnd);
        var vlo = vosc0 - (supply / 2);     // the cell switches about OSC B's own bias, half the supply
        var s0 = Math.Tanh(vlo / 0.06);
        var ds0 = (1 - (s0 * s0)) / 0.06;
        foreach (var (output, sign, local) in new[] { (system.Node(OutputA), 0.5 * Gain, 2), (system.Node(OutputB), -0.5 * Gain, 3) })
        {
            var branch = system.Branch(this, local);
            system.Add(output, branch, 1.0);
            system.Add(gnd, branch, -1.0);
            system.Add(branch, output, 1.0);
            system.Add(branch, gnd, -1.0);
            system.Add(branch, branch, -1.5e3);
            system.Add(branch, vcc, -1.0);
            system.Add(branch, gnd, 1.0);
            system.Add(branch, ina, -sign * s0);
            system.Add(branch, inb, sign * s0);
            system.Add(branch, osc, -sign * vin0 * ds0);
            system.Add(branch, gnd, sign * vin0 * ds0);
            system.AddRhs(branch, -1.5 - (sign * vin0 * ds0 * vosc0));
        }
    }

}

/// <summary>
/// The MC1496 balanced modulator: a Gilbert cell, the signal pair's current split by the carrier pair, so the
/// difference in the two output currents is (2·V_SIG / R_E) · tanh(V_CARRIER / 2VT) — the signal times the sign of the
/// carrier for a carrier of more than about 100 mV, or the true product for a small one. That's a balanced modulator:
/// double-sideband with the carrier suppressed, or with the signal inputs offset, AM. The signal inputs are pins 1 and
/// 4, with R_E between the gain-adjust pins 2 and 3; the carrier inputs 8 and 10; the outputs 6 and 12 sink their
/// currents and want load resistors to V+; pin 5 sets the bias, each half of the cell taking the current pushed into
/// it (which sits about 0.7 V above V−, pin 14). The base-emitter drops are 0.7 V; the inputs want biasing.
/// </summary>
public sealed partial class Mc1496 : CircuitComponent
{
    private const double Vt = 0.025852;

    public Mc1496()
    {
        var pins = new Terminal[15];
        Terminal Pin(int n, string name, TerminalType type) => pins[n] = new Terminal($"p{n}", name, type, Digital.DipPackage.PinOffset(n, 14));
        SignalPlus = Pin(1, "SIG+", TerminalType.Input);
        GainA = Pin(2, "GAIN", TerminalType.Passive);
        GainB = Pin(3, "GAIN", TerminalType.Passive);
        SignalMinus = Pin(4, "SIG-", TerminalType.Input);
        Bias = Pin(5, "BIAS", TerminalType.Passive);
        OutputPlus = Pin(6, "OUT+", TerminalType.Output);
        Pin(7, "NC", TerminalType.Passive);
        CarrierPlus = Pin(8, "CAR+", TerminalType.Input);
        Pin(9, "NC", TerminalType.Passive);
        CarrierMinus = Pin(10, "CAR-", TerminalType.Input);
        Pin(11, "NC", TerminalType.Passive);
        OutputMinus = Pin(12, "OUT-", TerminalType.Output);
        Pin(13, "NC", TerminalType.Passive);
        NegativeSupply = Pin(14, "V-", TerminalType.Power);
        Terminals = [.. pins.Skip(1)];
    }

    public Terminal SignalPlus { get; }
    public Terminal SignalMinus { get; }
    public Terminal GainA { get; }
    public Terminal GainB { get; }
    public Terminal Bias { get; }
    public Terminal OutputPlus { get; }
    public Terminal OutputMinus { get; }
    public Terminal CarrierPlus { get; }
    public Terminal CarrierMinus { get; }
    public Terminal NegativeSupply { get; }

    public override string ComponentType => "MC1496";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => "MC1496 modulator";

    public override bool IsNonlinear => true;

    // The bias pin, and the two signal-transistor emitters as followers.
    public override int VoltageSourceCount => 3;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var vneg = system.Node(NegativeSupply);
        var (s1, s4, g2, g3, b5) = (system.Node(SignalPlus), system.Node(SignalMinus), system.Node(GainA), system.Node(GainB), system.Node(Bias));
        var (c8, c10, o6, o12) = (system.Node(CarrierPlus), system.Node(CarrierMinus), system.Node(OutputPlus), system.Node(OutputMinus));
        foreach (var input in new[] { s1, s4, c8, c10 }) system.StampResistor(input, vneg, 2e6);

        // Pin 5 held at V− + 0.7 V through 500 Ω: its branch current is the bias, I5.
        var bias = system.Branch(this, 0);
        system.StampTheveninSource(bias, b5, vneg, 0.7, 500);

        // Each emitter follows its base down 0.7 V; a tail current of I5 pulls on each emitter.
        var (e2, e3) = (system.Branch(this, 1), system.Branch(this, 2));
        foreach (var (emitter, input, branch) in new[] { (g2, s1, e2), (g3, s4, e3) })
        {
            // −i into the emitter node is what the transistor supplies: v(emitter) − v(base) = −0.7, through 5 Ω.
            system.Add(emitter, branch, -1.0);
            system.Add(branch, emitter, 1.0);
            system.Add(branch, input, -1.0);
            system.Add(branch, branch, 5.0);
            system.AddRhs(branch, -0.7);
            system.Add(emitter, bias, 1.0);       // the tail source: I5 out of the emitter node
            system.Add(vneg, bias, -1.0);
        }

        // The collector currents (the branch currents) steered by the carrier: p = (1 + tanh(vc / 2VT)) / 2.
        var vc = system.IterationVoltageAcross(c8, c10);
        var t = Math.Tanh(vc / (2 * Vt));
        var p = (1 + t) / 2;
        var dp = (1 - (t * t)) / (4 * Vt);
        var (i2, i3) = (system.IterationValue(e2), system.IterationValue(e3));
        var delta = i2 - i3;
        foreach (var (output, share, slope) in new[] { (o6, p, dp), (o12, 1 - p, -dp) })
        {
            // Current into the output pin, on to V−: share·i2 + (1 − share)·i3, linearised in vc.
            system.Add(output, e2, share);
            system.Add(output, e3, 1 - share);
            system.Add(output, c8, slope * delta);
            system.Add(output, c10, -slope * delta);
            system.AddRhs(output, slope * delta * vc);
            system.Add(vneg, e2, -share);
            system.Add(vneg, e3, -(1 - share));
            system.Add(vneg, c8, -slope * delta);
            system.Add(vneg, c10, slope * delta);
            system.AddRhs(vneg, -slope * delta * vc);
        }
    }
}
