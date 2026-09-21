using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Annotations;

/// <summary>
/// A labelled rectangle drawn behind the circuit, for grouping a section of it.
/// <para>
/// Drawing a box round the input stage and writing "input stage" on it does more for a reader than
/// any amount of tidy routing. It is drawn behind everything else, so it groups without getting in
/// the way of the parts inside it.
/// </para>
/// </summary>
public sealed partial class SchematicBox : CircuitComponent, IAnnotation
{
    public SchematicBox()
    {
        Terminals = [];
    }

    public SchematicBox(string caption) : this()
    {
        Caption = caption;
    }

    /// <summary>What the section is called. Drawn at the top left, inside the border.</summary>
    [ObservableProperty]
    public partial string Caption { get; set; } = "Section";

    [ObservableProperty]
    public partial double Width { get; set; } = 320.0;

    [ObservableProperty]
    public partial double Height { get; set; } = 220.0;

    /// <summary>
    /// True to fill it faintly as well as outlining it. Useful for one box among several; a page
    /// where every box is filled is a page of boxes rather than a schematic.
    /// </summary>
    [ObservableProperty]
    public partial bool IsShaded { get; set; }

    public override string ComponentType => "Area";

    public override string DesignatorPrefix => "AREA";

    public override string ValueLabel => Caption;

    public double HalfWidth => Math.Max(Width, 40.0) / 2.0;

    public double HalfHeight => Math.Max(Height, 40.0) / 2.0;

    /// <summary>A box is not in the circuit, so it contributes nothing to the matrix.</summary>
    public override void StampMatrix(MnaSystem system, SimulationState state) { }

    partial void OnCaptionChanged(string value) => NotifyValueChanged();
}
