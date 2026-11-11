using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Ics;

/// <summary>
/// The MSGEQ7 seven-band graphic equaliser chip, the heart of every bouncing-bar spectrum display: audio into IN (through
/// 10 nF and 22 kΩ, about half the supply inside), seven band-pass filters at 63 Hz, 160 Hz, 400 Hz, 1 kHz, 2.5 kHz,
/// 6.25 kHz and 16 kHz, each followed by a peak detector, and one OUT multiplexed between them. RESET high starts the
/// multiplexer again; each STROBE falling edge after that moves OUT to the next band, the 63 Hz one first, the level
/// valid within about 36 µs and while STROBE stays low. OUT is a DC level from about 0.5 V for silence up to the supply
/// for a loud band. The filters run on the circuit's own time points, so the step has to be short enough for 16 kHz: a
/// few microseconds. The clock (CKIN, 165 kHz from 200 kΩ and 33 pF) is taken as running.
/// </summary>
public sealed partial class Msgeq7 : CircuitComponent
{
    private static readonly double[] Bands = [63, 160, 400, 1e3, 2.5e3, 6.25e3, 16e3];
    private readonly (double S1, double S2)[] _filters = new (double, double)[7];
    private readonly double[] _peak = new double[7];
    private int _band = -1;
    private bool _lastStrobe, _lastReset;
    private double _lastTime;

    public Msgeq7()
    {
        var pins = new Terminal[9];
        Terminal Pin(int n, string name, TerminalType type) => pins[n] = new Terminal($"p{n}", name, type, Digital.DipPackage.PinOffset(n, 8));
        Vdd = Pin(1, "VDD", TerminalType.Power);
        Vss = Pin(2, "VSS", TerminalType.Ground);
        Output = Pin(3, "OUT", TerminalType.Output);
        Strobe = Pin(4, "STROBE", TerminalType.Input);
        Input = Pin(5, "IN", TerminalType.Input);
        Pin(6, "GND", TerminalType.Passive);
        Reset = Pin(7, "RESET", TerminalType.Input);
        Pin(8, "CKIN", TerminalType.Passive);
        Terminals = [.. pins.Skip(1)];
    }

    public Terminal Vdd { get; }
    public Terminal Vss { get; }
    public Terminal Output { get; }
    public Terminal Strobe { get; }
    public Terminal Input { get; }
    public Terminal Reset { get; }

    /// <summary>Volts out a volt of input at a band's centre.</summary>
    [ObservableProperty]
    public partial double Gain { get; set; } = 6.0;

    /// <summary>Which band OUT is showing, 0 (63 Hz) to 6 (16 kHz), or −1 before the first strobe.</summary>
    public int Band => _band;

    /// <summary>A band's peak-detected level, in volts of input.</summary>
    public double Peak(int band) => _peak[band];

    public static IReadOnlyList<double> BandCentres => Bands;

    public override string ComponentType => "MSGEQ7";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => _band < 0 ? "MSGEQ7" : $"MSGEQ7 {Bands[_band]:0} Hz";

    public override int VoltageSourceCount => 1;

    private double OutputLevel(double supply) =>
        _band < 0 ? 0.5 : Math.Clamp(0.5 + (Gain * _peak[_band]), 0, Math.Max(supply - 0.2, 0.5));

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var (vdd, vss) = (system.Node(Vdd), system.Node(Vss));
        system.StampResistor(vdd, system.Node(Input), 100e3);
        system.StampResistor(system.Node(Input), vss, 100e3);
        system.StampResistor(system.Node(Strobe), vss, 1e6);
        system.StampResistor(system.Node(Reset), vss, 1e6);
        system.StampCurrentSource(vdd, vss, 0.8e-3);
        var supply = system.IterationVoltageAcross(vdd, vss);
        system.StampTheveninSource(system.Branch(this, 0), system.Node(Output), vss, OutputLevel(supply), 200);
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var vss = system.NodeVoltage(Vss);
        var supply = system.NodeVoltage(Vdd) - vss;
        var x = system.NodeVoltage(Input) - vss - (supply / 2);
        var dt = Math.Max(state.Time - _lastTime, 0);
        _lastTime = state.Time;
        if (dt > 0)
        {
            // Each band a trapezoidal state-variable band-pass (stable at any step), Q of 1.6, then a peak detector
            // that charges at once and decays over 30 ms.
            var decay = Math.Exp(-dt / 30e-3);
            for (var b = 0; b < 7; b++)
            {
                var g = Math.Tan(Math.PI * Math.Min(Bands[b] * dt, 0.45));
                const double k = 1 / 1.6;
                var (s1, s2) = _filters[b];
                var hp = (x - ((k + g) * s1) - s2) / (1 + (g * (g + k)));
                var bp = (g * hp) + s1;
                var lp = (g * bp) + s2;
                _filters[b] = ((2 * bp) - s1, (2 * lp) - s2);
                var level = Math.Abs(bp * k);
                _peak[b] = Math.Max(level, _peak[b] * decay);
            }
        }

        var strobe = system.NodeVoltage(Strobe) - vss > supply / 2;
        var reset = system.NodeVoltage(Reset) - vss > supply / 2;
        if (reset) _band = -1;
        else if (!strobe && _lastStrobe) _band = (_band + 1) % 7;
        (_lastStrobe, _lastReset) = (strobe, reset);
    }

    public override void ResetState()
    {
        Array.Clear(_filters);
        Array.Clear(_peak);
        (_band, _lastStrobe, _lastReset, _lastTime) = (-1, false, false, 0);
    }
}
