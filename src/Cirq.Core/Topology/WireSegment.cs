using System.Collections.ObjectModel;
using Cirq.Core.Primitives;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Core.Topology;

/// <summary>A drawn connection between two terminals, with optional orthogonal routing waypoints.</summary>
public partial class WireSegment : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();

    [ObservableProperty]
    public partial Terminal SourceTerminal { get; set; } = null!;

    [ObservableProperty]
    public partial Terminal TargetTerminal { get; set; } = null!;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public ObservableCollection<Point> Waypoints { get; } = [];

    /// <summary>Net this wire resolved to on the last netlist build.</summary>
    public Guid NetId { get; set; }

    /// <summary>Full ordered path from source terminal, through waypoints, to target terminal.</summary>
    public IEnumerable<Point> RoutePoints()
    {
        yield return SourceTerminal.AbsolutePosition;
        foreach (var p in Waypoints) yield return p;
        yield return TargetTerminal.AbsolutePosition;
    }
}
