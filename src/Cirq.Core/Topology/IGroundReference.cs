namespace Cirq.Core.Topology;

/// <summary>Marks a component whose terminals define the 0 V reference node.</summary>
public interface IGroundReference
{
    Terminal GroundTerminal { get; }
}
