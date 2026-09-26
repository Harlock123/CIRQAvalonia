using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Annotations;

/// <summary>
/// The thick line a bus is drawn as.
/// <para>
/// It is <b>drawing, not wiring</b>, and that is worth being plain about. The signals in a bus are
/// joined by their names — a <c>D3</c> tap here and a <c>D3</c> tap there are one net whether or not
/// anything is drawn between them — and this is the line that shows a reader where they travel. It
/// carries no current and appears in no netlist, exactly like the box you draw round a section.
/// </para>
/// <para>
/// Which is how every schematic tool works underneath, and the source of the commonest confusion
/// about buses: people expect the line to make the connection, and it never does. Drawing it as an
/// annotation is the honest arrangement rather than a shortcut — there is nothing here to pretend
/// otherwise about.
/// </para>
/// </summary>
public sealed partial class BusLine : CircuitComponent, IAnnotation
{
    public BusLine()
    {
        Terminals = [];
    }

    public BusLine(string label, double length = 240.0) : this()
    {
        Label = label;
        Length = length;
    }

    /// <summary>What the bus is called, written along it — <c>D[0..7]</c>.</summary>
    [ObservableProperty]
    public partial string Label { get; set; } = "D[0..7]";

    /// <summary>How long it is, in canvas units. Rotate the part to stand it on end.</summary>
    [ObservableProperty]
    public partial double Length { get; set; } = 240.0;

    /// <summary>
    /// How thick, in canvas units. A bus is drawn heavier than a wire because that is the whole of
    /// what tells a reader it is one.
    /// </summary>
    [ObservableProperty]
    public partial double Thickness { get; set; } = 4.0;

    public override string ComponentType => "Bus";

    public override string DesignatorPrefix => "BUS";

    public override string ValueLabel => Label;

    /// <summary>Half its length, which is where each end sits either side of its position.</summary>
    public double HalfLength => Math.Max(Length, 20.0) / 2.0;

    public double HalfWidth => HalfLength;

    /// <summary>
    /// Enough to take the line and the label above it. A bus is grabbed and dragged like anything
    /// else, and a hit box the thickness of the line would be a thing nobody can pick up.
    /// </summary>
    public double HalfHeight => Math.Max(Thickness, 2.0) + 12.0;

    public override void StampMatrix(MnaSystem system, SimulationState state) { }

    partial void OnLabelChanged(string value) => NotifyValueChanged();

    partial void OnLengthChanged(double value) => NotifyValueChanged();
}
