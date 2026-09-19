namespace Cirq.Core.Topology;

/// <summary>Electrical role of a component terminal. Drives UI affordances and netlist checks.</summary>
public enum TerminalType
{
    /// <summary>Non-directional analog pin (resistor lead, capacitor plate, ...).</summary>
    Passive,
    /// <summary>Analog or digital input pin.</summary>
    Input,
    /// <summary>Driven output pin.</summary>
    Output,
    /// <summary>Pin that may source or sink (bus, open-collector, ...).</summary>
    Bidirectional,
    /// <summary>Supply rail pin (Vcc / Vdd / V+).</summary>
    Power,
    /// <summary>Ground reference pin.</summary>
    Ground,
}
