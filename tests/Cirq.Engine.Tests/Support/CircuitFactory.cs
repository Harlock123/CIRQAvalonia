using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;

namespace Cirq.Engine.Tests.Support;

/// <summary>Builders for the small reference circuits the engine tests are measured against.</summary>
public static class CircuitFactory
{
    /// <summary>
    /// Series RC driven by a DC source, with the capacitor referenced to ground:
    /// <c>V+ -- R -- (out) -- C -- GND</c>.
    /// </summary>
    public static (Circuit Circuit, Terminal Output, DcVoltageSource Source, Resistor R, Capacitor C)
        RcLowPass(double resistance, double capacitance, double voltage)
    {
        var circuit = new Circuit { Title = "RC low-pass" };
        var source = circuit.Add(new DcVoltageSource(voltage));
        var r = circuit.Add(new Resistor(resistance));
        var c = circuit.Add(new Capacitor(capacitance));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(source.Positive, r.A);
        circuit.Connect(r.B, c.A);
        circuit.Connect(c.B, gnd.Pin);
        circuit.Connect(source.Negative, gnd.Pin);

        return (circuit, c.A, source, r, c);
    }

    /// <summary>Series RL driven by a DC source, output taken across the resistor.</summary>
    public static (Circuit Circuit, Terminal Output, Inductor L, Resistor R)
        RlSeries(double resistance, double inductance, double voltage)
    {
        var circuit = new Circuit { Title = "RL series" };
        var source = circuit.Add(new DcVoltageSource(voltage));
        var l = circuit.Add(new Inductor(inductance));
        var r = circuit.Add(new Resistor(resistance));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(source.Positive, l.A);
        circuit.Connect(l.B, r.A);
        circuit.Connect(r.B, gnd.Pin);
        circuit.Connect(source.Negative, gnd.Pin);

        return (circuit, l.B, l, r);
    }

    /// <summary>Resistive divider between a DC source and ground.</summary>
    public static (Circuit Circuit, Terminal Midpoint) VoltageDivider(double r1, double r2, double voltage)
    {
        var circuit = new Circuit { Title = "Divider" };
        var source = circuit.Add(new DcVoltageSource(voltage));
        var top = circuit.Add(new Resistor(r1));
        var bottom = circuit.Add(new Resistor(r2));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(source.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, gnd.Pin);
        circuit.Connect(source.Negative, gnd.Pin);

        return (circuit, top.B);
    }
}
