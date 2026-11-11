using Cirq.Components.Nonlinear;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Ics;

/// <summary>
/// An EL84 (6BQ5) output pentode, on Norman Koren's pentode model: plate current
/// Ip = (E1^Ex / Kg1)·(1 + sgn E1)·atan(Vpk / Kvb), E1 = (Vg2k / Kp)·ln(1 + e^(Kp·(1/μ + Vg1k/Vg2k))), almost flat with
/// the plate voltage once it's above a few tens of volts; screen current (Vg1k + Vg2k/μ)^1.5 / Kg2. The constants are
/// fitted to the datasheet's class A point (250 V plate and screen, −7.3 V grid: 48 mA and 5.5 mA). G3 is tied to the
/// cathode inside; the heater's taken as warm. Single-ended through an output transformer it makes about 4 W.
/// </summary>
public sealed partial class Pentode : CircuitComponent
{
    public Pentode()
    {
        Plate = new Terminal("p", "P", TerminalType.Passive, new Point(0, -50));
        Screen = new Terminal("g2", "G2", TerminalType.Passive, new Point(40, -20));
        Grid = new Terminal("g1", "G1", TerminalType.Input, new Point(-40, 0));
        Cathode = new Terminal("k", "K", TerminalType.Passive, new Point(0, 50));
        Terminals = [Plate, Screen, Grid, Cathode];
    }

    public Terminal Plate { get; }
    public Terminal Screen { get; }
    public Terminal Grid { get; }
    public Terminal Cathode { get; }

    [ObservableProperty]
    public partial double Mu { get; set; } = 19;

    [ObservableProperty]
    public partial double Ex { get; set; } = 1.35;

    [ObservableProperty]
    public partial double Kg1 { get; set; } = 670;

    [ObservableProperty]
    public partial double Kg2 { get; set; } = 2580;

    [ObservableProperty]
    public partial double Kp { get; set; } = 200;

    [ObservableProperty]
    public partial double Kvb { get; set; } = 24;

    public override string ComponentType => "Pentode";

    public override string DesignatorPrefix => "V";

    public override string ValueLabel => "EL84";

    public override bool IsNonlinear => true;

    public double PlateCurrent { get; private set; }

    public double ScreenCurrent { get; private set; }

    public double PlateCurrentAt(double vpk, double vg1k, double vg2k)
    {
        if (vg2k <= 1) return 0;
        var x = Kp * ((1 / Mu) + (vg1k / vg2k));
        var e1 = vg2k / Kp * (x > 30 ? x : Math.Log(1 + Math.Exp(x)));
        return e1 <= 0 ? 0 : 2 * Math.Pow(e1, Ex) / Kg1 * Math.Atan(Math.Max(vpk, 0) / Kvb);
    }

    public double ScreenCurrentAt(double vg1k, double vg2k)
    {
        var x = vg1k + (vg2k / Mu);
        return x <= 0 ? 0 : Math.Pow(x, 1.5) / Kg2;
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var (p, g2, g1, k) = (system.Node(Plate), system.Node(Screen), system.Node(Grid), system.Node(Cathode));
        var vk = system.IterationVoltage(k);
        var vpk = Math.Clamp(system.IterationVoltage(p) - vk, -50, 1500);
        var vg1 = Math.Clamp(system.IterationVoltage(g1) - vk, -200, 50);
        var vg2 = Math.Clamp(system.IterationVoltage(g2) - vk, -50, 1000);
        const double h = 1e-3;

        // Plate: linearised in all three of its voltages.
        var ip = PlateCurrentAt(vpk, vg1, vg2);
        var gp = Math.Max((PlateCurrentAt(vpk + h, vg1, vg2) - PlateCurrentAt(vpk - h, vg1, vg2)) / (2 * h), 1e-9);
        var gm1 = (PlateCurrentAt(vpk, vg1 + h, vg2) - PlateCurrentAt(vpk, vg1 - h, vg2)) / (2 * h);
        var gm2 = (PlateCurrentAt(vpk, vg1, vg2 + h) - PlateCurrentAt(vpk, vg1, vg2 - h)) / (2 * h);
        system.StampConductance(p, k, gp);
        system.StampVccs(p, k, g1, k, gm1);
        system.StampVccs(p, k, g2, k, gm2);
        system.StampCurrentSource(p, k, ip - (gp * vpk) - (gm1 * vg1) - (gm2 * vg2));

        // Screen.
        var is2 = ScreenCurrentAt(vg1, vg2);
        var gs = Math.Max((ScreenCurrentAt(vg1, vg2 + h) - ScreenCurrentAt(vg1, vg2 - h)) / (2 * h), 1e-9);
        var gs1 = (ScreenCurrentAt(vg1 + h, vg2) - ScreenCurrentAt(vg1 - h, vg2)) / (2 * h);
        system.StampConductance(g2, k, gs);
        system.StampVccs(g2, k, g1, k, gs1);
        system.StampCurrentSource(g2, k, is2 - (gs * vg2) - (gs1 * vg1));

        // Grid current above the cathode, as the triode's.
        var x = Math.Clamp(vg1 / 0.1, -60, 60);
        var ig = 0.1 * (x > 30 ? x : Math.Log(1 + Math.Exp(x))) / 2e3;
        var gg = Math.Max(1 / (1 + Math.Exp(-x)) / 2e3, 1e-12);
        system.StampNorton(g1, k, gg, ig - (gg * vg1));
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var vk = system.NodeVoltage(Cathode);
        var (vpk, vg1, vg2) = (system.NodeVoltage(Plate) - vk, system.NodeVoltage(Grid) - vk, system.NodeVoltage(Screen) - vk);
        PlateCurrent = PlateCurrentAt(vpk, vg1, vg2);
        ScreenCurrent = ScreenCurrentAt(vg1, vg2);
    }
}

/// <summary>
/// A 5Y3 full-wave rectifier valve: two plates and the filament between them as the cathode, each plate conducting by
/// the three-halves power law, I = K·V^1.5, so it drops about 60 V at 125 mA — the soft, sagging supply of a valve amp,
/// which is part of how one sounds. Nothing flows backwards. The heater's taken as warm (on a real one, 5 V at 2 A).
/// </summary>
public sealed partial class ValveRectifier : CircuitComponent
{
    public ValveRectifier()
    {
        Plate1 = new Terminal("p1", "P1", TerminalType.Passive, new Point(-40, -20));
        Plate2 = new Terminal("p2", "P2", TerminalType.Passive, new Point(-40, 20));
        Cathode = new Terminal("k", "K", TerminalType.Passive, new Point(40, 0));
        Terminals = [Plate1, Plate2, Cathode];
    }

    public Terminal Plate1 { get; }
    public Terminal Plate2 { get; }
    public Terminal Cathode { get; }

    /// <summary>The perveance, K in I = K·V^1.5.</summary>
    [ObservableProperty]
    public partial double Perveance { get; set; } = 2.7e-4;

    public override string ComponentType => "Rectifier Valve";

    public override string DesignatorPrefix => "V";

    public override string ValueLabel => "5Y3";

    public override bool IsNonlinear => true;

    public double CurrentAt(double v) => v <= 0 ? 0 : Perveance * Math.Pow(v, 1.5);

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var k = system.Node(Cathode);
        foreach (var plate in new[] { Plate1, Plate2 })
        {
            var p = system.Node(plate);
            var v = Math.Clamp(system.IterationVoltageAcross(p, k), -2000, 500);
            var i = CurrentAt(v);
            var g = Math.Max(v <= 0 ? 0 : 1.5 * Perveance * Math.Sqrt(v), 1e-9);
            system.StampNorton(p, k, g, i - (g * v));
        }
    }
}
