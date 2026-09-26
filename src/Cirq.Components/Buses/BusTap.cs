using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Buses;

/// <summary>
/// One signal taken off a bus — <c>D3</c> of <c>D[0..7]</c>.
/// <para>
/// A bus is a way of not drawing eight wires. The eight signals in it are ordinary nets with
/// ordinary names, and a tap is what joins a pin to one of them: put a <c>D3</c> tap on the
/// counter's output and another on the decoder's input, and they are one net, exactly as two net
/// labels reading <c>D3</c> would be.
/// </para>
/// <para>
/// So this is a net label with the arithmetic done for you, and that is the whole of it. What it
/// buys is not new semantics but bulk: eight of these arrive from one dialog, numbered, instead of
/// eight labels typed by hand — and typing <c>D3</c> as <c>D4</c> on one of sixteen is a mistake
/// that costs an afternoon, because the schematic looks right.
/// </para>
/// </summary>
public sealed partial class BusTap : CircuitComponent, INetNaming
{
    public BusTap()
    {
        Pin = new Terminal("bus", string.Empty, TerminalType.Passive, new Point(-30, 0));
        Terminals = [Pin];
    }

    public BusTap(string bus, int bit) : this()
    {
        Bus = bus;
        Bit = bit;
    }

    /// <summary>The pin, which goes to whatever this signal is.</summary>
    public Terminal Pin { get; }

    public Terminal NamedTerminal => Pin;

    /// <summary>What the bus is called — <c>D</c>, <c>ADDR</c>, <c>Q</c>.</summary>
    [ObservableProperty]
    public partial string Bus { get; set; } = "D";

    /// <summary>Which signal of it this is.</summary>
    [ObservableProperty]
    public partial int Bit { get; set; }

    /// <summary>
    /// The net this tap names, which is the bus and the bit run together: <c>D</c> and 3 make
    /// <c>D3</c>. No separator, because that is what every datasheet and every net list calls it.
    /// </summary>
    public string NetName => $"{Bus.Trim()}{Bit}";

    public override string ComponentType => "Bus Tap";

    public override string DesignatorPrefix => "B";

    public override string ValueLabel => NetName;

    /// <summary>
    /// A tap contributes nothing to the matrix. Its whole effect happened when the netlist was
    /// built, which is why changing its number is a change of topology rather than of a value.
    /// </summary>
    public override void StampMatrix(MnaSystem system, SimulationState state) { }

    partial void OnBusChanged(string value) => NotifyValueChanged();

    partial void OnBitChanged(int value) => NotifyValueChanged();
}
