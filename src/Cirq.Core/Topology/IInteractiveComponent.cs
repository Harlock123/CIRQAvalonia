namespace Cirq.Core.Topology;

/// <summary>
/// A component the user can operate directly on the canvas — a switch, a button, a logic toggle.
/// The editor double-clicks these rather than making the user go to the inspector for something
/// that is physically a flick of a finger.
/// </summary>
public interface IInteractiveComponent
{
    /// <summary>Operates the device: flips a switch, presses a button.</summary>
    void Interact();

    /// <summary>Short description of what operating it will do, shown in the status bar.</summary>
    string InteractionHint { get; }
}
