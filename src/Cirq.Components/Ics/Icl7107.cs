using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Ics;

/// <summary>
/// The ICL7107, the 3½-digit dual-slope voltmeter chip that drives LED displays directly: everything a panel meter
/// needs in a DIP-40 but the displays, its reference and a few passives.
/// <para>
/// It reads 1000 · (IN HI − IN LO) / (REF HI − REF LO), −1999 to 1999, so a 1.000 V reference reads 1.999 V full
/// scale and 100.0 mV makes a 199.9 mV meter. Past full scale it shows a 1 with the other digits blank. The segment pins
/// sink 8 mA each into common-anode displays tied to V+ (the hundreds-of-thousands digit is the 1 and the minus sign
/// alone); it takes a new reading <see cref="ReadingsPerSecond"/> times a second (three, from its 48 kHz oscillator).
/// COMMON sits 2.8 V below V+, the usual place to hang the reference divider. The integrator, auto-zero and reference
/// capacitors are taken as fitted; the oscillator pins are taken as running.
/// </para>
/// </summary>
public sealed partial class Icl7107 : CircuitComponent, IBreakpointSource
{
    // Segment pins by digit (units, tens, hundreds), a to g.
    private static readonly int[][] DigitPins =
    [
        [5, 4, 3, 2, 8, 6, 7],
        [12, 11, 10, 9, 14, 13, 25],
        [23, 16, 24, 15, 18, 17, 22],
    ];

    private static readonly int[] Glyphs = [0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F];

    private readonly Terminal[] _pins = new Terminal[41];
    private double _next;
    private int _reading;
    private bool _over;
    private bool[] _lit = new bool[41];

    public Icl7107()
    {
        string Name(int n) => n switch
        {
            1 => "V+", 2 => "D1", 3 => "C1", 4 => "B1", 5 => "A1", 6 => "F1", 7 => "G1", 8 => "E1",
            9 => "D2", 10 => "C2", 11 => "B2", 12 => "A2", 13 => "F2", 14 => "E2", 15 => "D3", 16 => "B3",
            17 => "F3", 18 => "E3", 19 => "AB4", 20 => "POL", 21 => "GND", 22 => "G3", 23 => "A3", 24 => "C3",
            25 => "G2", 26 => "V-", 27 => "INT", 28 => "BUFF", 29 => "A-Z", 30 => "IN LO", 31 => "IN HI",
            32 => "COMMON", 33 => "C REF-", 34 => "C REF+", 35 => "REF LO", 36 => "REF HI", 37 => "TEST",
            38 => "OSC3", 39 => "OSC2", _ => "OSC1",
        };

        for (var n = 1; n <= 40; n++)
        {
            var type = n is 1 or 26 ? TerminalType.Power : n == 21 ? TerminalType.Ground : n is (>= 2 and <= 20) or 22 or 23 or 24 or 25 ? TerminalType.Output : TerminalType.Passive;
            _pins[n] = new Terminal($"p{n}", Name(n), type, Digital.DipPackage.PinOffset(n, 40));
        }

        Terminals = [.. _pins.Skip(1)];
    }

    public Terminal Pin(int n) => _pins[n];

    public Terminal Pin(string name) => _pins.Skip(1).First(p => p.Name == name);

    [ObservableProperty]
    public partial double ReadingsPerSecond { get; set; } = 3;

    /// <summary>The last reading, −1999 to 1999, and whether it was over the range.</summary>
    public int Reading => _reading;

    public bool OverRange => _over;

    public override string ComponentType => "ICL7107";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => _over ? "ICL7107 1---" : $"ICL7107 {_reading}";

    public override bool IsNonlinear => true;

    public override int VoltageSourceCount => 1;

    public double? NextBreakpointAfter(double time) => _next > time + 1e-12 ? _next : null;

    private void Display()
    {
        _lit = new bool[41];
        var value = Math.Abs(_reading);
        if (_over)
        {
            _lit[19] = true;
        }
        else
        {
            var digits = new[] { value % 10, (value / 10) % 10, (value / 100) % 10 };
            for (var d = 0; d < 3; d++)
            {
                // Leading zeros above the tens stay lit, as on the chip: it shows 0.00 style readings in full.
                var glyph = Glyphs[digits[d]];
                for (var s = 0; s < 7; s++) _lit[DigitPins[d][s]] = (glyph & (1 << s)) != 0;
            }

            _lit[19] = value >= 1000;
        }

        _lit[20] = _reading < 0;
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var (vpos, gnd, vneg) = (system.Node(_pins[1]), system.Node(_pins[21]), system.Node(_pins[26]));
        system.StampTheveninSource(system.Branch(this, 0), system.Node(_pins[32]), vpos, -2.8, 15);
        system.StampCurrentSource(vpos, vneg, 1.8e-3);
        foreach (var n in new[] { 30, 31, 35, 36 }) system.StampResistor(system.Node(_pins[n]), system.Node(_pins[32]), 1e9);

        // Each segment output a current sink to GND: 8 mA, softly limited near GND so a pin with nothing on it is calm.
        for (var n = 2; n <= 25; n++)
        {
            if (n == 21) continue;
            var p = system.Node(_pins[n]);
            if (!_lit[n])
            {
                system.StampConductance(p, gnd, 1e-9);
                continue;
            }

            var v = system.IterationVoltageAcross(p, gnd);
            var t = Math.Tanh(v / 0.15);
            var i = 8e-3 * t;
            var g = Math.Max(8e-3 * (1 - (t * t)) / 0.15, 1e-9);
            system.StampNorton(p, gnd, g, i - (g * v));
        }
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        if (state.Time + 1e-12 < _next) return;
        var vin = system.NodeVoltage(_pins[31]) - system.NodeVoltage(_pins[30]);
        var vref = system.NodeVoltage(_pins[36]) - system.NodeVoltage(_pins[35]);
        var counts = vref > 1e-6 ? 1000 * vin / vref : 0;
        _over = Math.Abs(counts) > 1999.5;
        _reading = (int)Math.Clamp(Math.Round(counts), -1999, 1999);
        Display();
        _next = state.Time + (1 / Math.Max(ReadingsPerSecond, 0.1));
        NotifyValueChanged();
    }

    public override void ResetState()
    {
        (_next, _reading, _over) = (0, 0, false);
        _lit = new bool[41];
    }
}
