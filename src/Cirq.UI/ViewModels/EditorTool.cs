namespace Cirq.UI.ViewModels;

/// <summary>The active canvas interaction mode.</summary>
public enum EditorTool
{
    /// <summary>Select, drag and inspect components.</summary>
    Select,
    /// <summary>Draw orthogonal wires between terminals.</summary>
    Wire,
    /// <summary>Attach an oscilloscope probe to a terminal or net.</summary>
    Probe,
    /// <summary>Delete whatever is clicked.</summary>
    Delete,
}
